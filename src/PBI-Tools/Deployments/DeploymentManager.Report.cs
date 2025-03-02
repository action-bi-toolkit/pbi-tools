/*
 * This file is part of the pbi-tools project <https://github.com/pbi-tools/pbi-tools>.
 * Copyright (C) 2018 Mathias Thierbach
 *
 * pbi-tools is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * pbi-tools is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * A copy of the GNU Affero General Public License is available in the LICENSE file,
 * and at <https://goto.pbi.tools/license>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.PowerBI.Api;
using Microsoft.PowerBI.Api.Models;
using Microsoft.Rest;
using Newtonsoft.Json.Linq;

namespace PbiTools.Deployments
{
    using Model;
    using PowerBI;
    using ProjectSystem;
    using Utils;

    public partial class DeploymentManager
    {

        internal async Task DeployReportAsync(PbiDeploymentManifest manifest, string label, string environment)
        { 
            if (!manifest.Environments.ContainsKey(environment))
                throw new DeploymentException($"The manifest does not contain the specified environment: {environment}.");

            var deploymentEnv = manifest.Environments[environment];

            if (deploymentEnv.Disabled)
            {
                Log.Warning("Deployment environment '{Environment}' disabled. Aborting.", environment);
                return;
            }

            Log.Information("Starting deployment '{DeploymentLabel}' into environment: {Environment} ...", label, environment);

            // Source: Folder | File
            var basePath = (BasePath == null)
                ? new FileInfo(Project.OriginalPath).DirectoryName
                : new DirectoryInfo(BasePath).FullName;

            Log.Write(WhatIfLogLevel, "Determining reports from {SourceType} source: \"{SourcePath}\"", manifest.Source.Type, manifest.Source.Path);
            Log.Write(WhatIfLogLevel, "Base folder: {BasePath}", basePath);

            var reports = manifest.Source.Type switch
            {
                PbiDeploymentSourceType.Folder => GenerateReportsFromFolderSource(manifest, deploymentEnv, basePath),
                PbiDeploymentSourceType.File   => GetReportsFromFileSource(manifest, deploymentEnv, basePath),
                _ => throw new DeploymentException($"Unsupported source type: '{manifest.Source.Type}'")
            };

            // TODO Possibly perform further transforms here

            // Upload *****************************************************

            // Get auth token
            if (manifest.Authentication.Type != PbiDeploymentAuthenticationType.ServicePrincipal)
                throw new DeploymentException("Only ServicePrincipal authentication is supported.");

            Log.Write(WhatIfLogLevel, "Acquiring access token...");
            var tokenProvider = PowerBITokenProviderFactory(manifest.Authentication.ExpandAndValidate());

            AuthenticationResult authResult;
            
            try {
                authResult = await tokenProvider.AcquireTokenAsync();
                Log.Write(WhatIfLogLevel, "Token Endpoint: {TokenEndpoint}", authResult?.AuthenticationResultMetadata.TokenEndpoint);
            }
            catch (MsalServiceException ex) {
                throw DeploymentException.From(ex);
            }

            var tokenCredentials = new TokenCredentials(authResult!.AccessToken, authResult.TokenType);
            Log.Information("Access token received. Expires On: {ExpiresOn}", authResult.ExpiresOn);
            if (WhatIf) Log.Information("---");

            using var powerBI = PowerBIClientFactory(manifest?.Options?.PbiBaseUri ?? DefaultPowerBIApiBaseUri, tokenCredentials);

            foreach (var report in reports)
            {
                var workspace = deploymentEnv.Workspace.ExpandParameters(report.Parameters);

                if (WhatIf) {
                    Log.Information("Report: {Path}", report.SourcePath);
                    Log.Information("  DisplayName: {DisplayName}", report.DisplayName);
                    Log.Information("  Parameters:");
                    foreach (var parameter in report.Parameters)
                        Log.Information("  * {ParamKey} = \"{ParamValue}\"", parameter.Key, parameter.Value);
                    Log.Information("  Workspace: {Workspace}", workspace);
                    Log.Information("---");
                    continue;
                }

                try { 
                    await ImportReportAsync(report, powerBI, workspace);
                }
                catch (HttpOperationException ex) {
                    if (ex.Response.Content.TryParseJson<JObject>(out var json)) {
                        throw new DeploymentException($"HTTP Error: {ex.Response.StatusCode}\n{json.ToString(Newtonsoft.Json.Formatting.Indented)}", ex);
                    }
                    throw;
                }
            }

        }

        internal static string MakeRelativePath(string baseDir, string fullPath) =>
#if NETFRAMEWORK
            new Uri(baseDir + "/").MakeRelativeUri(new Uri(fullPath)).OriginalString.Replace("%20", " ");
#elif NET
            Path.GetRelativePath(baseDir, fullPath);
#endif

        internal static DeploymentSourceInfo[] ResolveSourceFolders(string path, string baseFolder, IDictionary<string, string> systemParameters)
        {
            var sourceFolderRegex = ConvertSearchPatternToRegex(path ?? throw new ArgumentNullException(nameof(path)));
            var currentDir = baseFolder ?? throw new ArgumentNullException(nameof(baseFolder));

            var sourceFolders = Directory.EnumerateDirectories(
                currentDir,
                "*",
                SearchOption.AllDirectories
            )
            .Select(d =>
            {
                var dir = new DirectoryInfo(d);
                var relPath = MakeRelativePath(currentDir, dir.FullName);
                var match = sourceFolderRegex.Match(relPath.Replace('\\', '/'));
                return new
                {
                    Directory = dir,
                    Match = match
                };
            })
            .Where(x => x.Match.Success && PbixProject.IsPbixProjFolder(x.Directory.FullName))
            .Select(x => new DeploymentSourceInfo {
                FullPath = x.Directory.FullName,
                Parameters = x.Match.Groups.OfType<System.Text.RegularExpressions.Group>()
                    .Aggregate(
                        new Dictionary<string, string>(systemParameters)
                            .With(DeploymentParameters.Names.PBIXPROJ_NAME, x.Directory.Name)
                            .With(DeploymentParameters.Names.FILE_NAME_WITHOUT_EXT, x.Directory.Name),
                        (dict, group) => {
                            dict[group.Name == "0" ? DeploymentParameters.Names.PBIXPROJ_FOLDER : group.Name] = group.Value;
                            return dict;
                        }
                    )
            })
            .ToArray();

            if (sourceFolders.Length == 0)
            {
                Log.Warning("Found no matching source folders for path: '{SourcePath}'. Exiting...", path);
            }
            else
            {
                Log.Information("Found {Count} source folders to deploy.", sourceFolders.Length);
            }

            return sourceFolders;
        }

        internal static DeploymentSourceInfo[] ResolveSourceFiles(string path, string baseFolder, IDictionary<string, string> systemParameters)
        {
            var sourceFileRegex = ConvertSearchPatternToRegex(path);
            var currentDir = baseFolder ?? throw new ArgumentNullException(nameof(baseFolder));

            var sourceFiles = Directory.EnumerateFiles(
                    currentDir,
                    "*.*",
                    SearchOption.AllDirectories
                )
                .Select(filePath =>
                {
                    var file = new FileInfo(filePath);
                    var relPath = MakeRelativePath(currentDir, file.FullName);
                    var match = sourceFileRegex.Match(relPath.Replace('\\', '/'));
                    return new
                    {
                        File = file,
                        Match = match
                    };
                })
                .Where(x => {
                    if (!x.Match.Success)
                        return false;
                    if (x.File.Extension.ToLowerInvariant() != ".pbix")
                        Log.Warning("This file matched by the source expression does not have a .pbix extension and will likely fail to deploy: {FullPath}", x.File.FullName);
                    return true;
                })
                .Select(x => new DeploymentSourceInfo {
                    FullPath = x.File.FullName,
                    Parameters = x.Match.Groups.OfType<System.Text.RegularExpressions.Group>()
                        .Aggregate(
                            new Dictionary<string, string>(systemParameters)
                                .With(DeploymentParameters.Names.FILE_NAME, x.File.Name)
                                .With(DeploymentParameters.Names.FILE_NAME_WITHOUT_EXT, Path.GetFileNameWithoutExtension(x.File.Name)),
                            (dict, group) => {
                                dict[group.Name == "0" ? DeploymentParameters.Names.FILE_PATH : group.Name] = group.Value;
                                return dict;
                            }
                        )

                })
                .ToArray();

            if (sourceFiles.Length == 0)
            {
                Log.Warning("Found no matching source files for path: '{SourcePath}'. Exiting...", path);
            }
            else
            {
                Log.Information("Found {Count} source files to deploy.", sourceFiles.Length);
            }

            return sourceFiles;
        }

        internal ReportDeploymentInfo[] GenerateReportsFromFolderSource(PbiDeploymentManifest manifest, PbiDeploymentEnvironment environment, string baseFolder = null)
        {
            var sourceFolders = ResolveSourceFolders(manifest.Source.Path, baseFolder ?? Environment.CurrentDirectory, DeploymentParameters.GetSystemParameters(environment.Name));

            var tempDir = manifest.ResolveTempDir();
            Log.Debug("Using TEMP dir: {TempDir}", tempDir);

            // Compile PBIX files *****************************************

            return sourceFolders.Select(folder => CompileReportForDeployment(  // TODO Perform transforms here (connection swap, for instance)
                manifest.Options,
                environment.DisplayName,
                folder.FullPath,
                tempDir,
                DeploymentParameters.CalculateForEnvironment(
                    manifest,
                    environment,
                    folder.Parameters
                )
            )).ToArray();
        }

        internal ReportDeploymentInfo[] GetReportsFromFileSource(PbiDeploymentManifest manifest, PbiDeploymentEnvironment environment, string baseFolder = null)
        {
            var sourceFiles = ResolveSourceFiles(
                manifest.Source.Path,
                baseFolder ?? Environment.CurrentDirectory,
                DeploymentParameters.GetSystemParameters(environment.Name));

            return sourceFiles.Select(file =>
            {
                var parameters = DeploymentParameters.CalculateForEnvironment(
                    manifest,
                    environment,
                    file.Parameters);

                return new ReportDeploymentInfo
                {
                    Options = manifest.Options,
                    Parameters = new DeploymentParameters(parameters),
                    SourcePath = file.FullPath,
                    PbixPath = file.FullPath,
                    DisplayName = environment.DisplayName.ExpandParameters(parameters) ?? Path.GetFileName(file.FullPath),
                };
            }).ToArray();
        }

        internal ReportDeploymentInfo CompileReportForDeployment(PbiDeploymentOptions options, 
            string displayName, 
            string pbixProjFolder, 
            string tempDir, 
            IDictionary<string, DeploymentParameter> parameters, 
            JObject connectionsOverwrite = default)
        {
            var reportPath = Path.Combine(tempDir, $"{Guid.NewGuid()}", $"{new DirectoryInfo(pbixProjFolder).Name}.pbix");

            var reportInfo = new ReportDeploymentInfo
            {
                Options = options,
                Parameters = new DeploymentParameters(parameters),
                SourcePath = pbixProjFolder,
                PbixPath = reportPath,
                DisplayName = displayName.ExpandParameters(parameters) ?? Path.GetFileName(reportPath),
            };

            if (!WhatIf) { 
                Log.Information("Creating PBIX from report source at: '{Path}'...", reportPath);

                var model = PbixModel.FromFolder(pbixProjFolder);
                model.ToFile(reportPath, PbiFileFormat.PBIX, connectionsOverwrite);

                reportInfo.Model = model;
            }

            return reportInfo;
        }

        internal static async Task ImportReportAsync(ReportDeploymentInfo args, IPowerBIClient powerBI, string workspaceRef)
        {
            // Determine Workspace
            var workspace = await workspaceRef.ResolveWorkspaceAsync(powerBI, args.WorkspaceCache);

            Import import;
            using (var file = File.OpenRead(args.PbixPath))
            {
                import = await powerBI.Imports.PostImportWithFileAsyncInGroup(workspace.Id, file,
                    datasetDisplayName: args.DisplayName, // will FAIL without the parameter (although the API marks it as optional)
                    nameConflict: args.Options.Import.NameConflict,
                    skipReport: args.Options.Import.SkipReport,
                    overrideReportLabel: args.Options.Import.OverrideReportLabel,
                    overrideModelLabel: args.Options.Import.OverrideModelLabel
                );
            }

            while (import.ImportState != "Succeeded") // Must use magic string here :(
            {
                if (import.ImportState != null)
                    Log.Information("Import: {Name}, State: {ImportState} (Id: {Id})", import.Name, import.ImportState, import.Id);

                if (import.ImportState == "Failed")
                    throw new DeploymentException($"Deployment '{import.Name}' ({import.Id}) failed.");

                await Task.Delay(500);

                import = await powerBI.Imports.GetImportInGroupAsync(workspace.Id, import.Id);
            }

            Log.Information("Import succeeded: {Id} ({Name})", import.Id, import.Name);
            if (import.Reports.TryGetFirst(out var report))
                Log.Information("\tReport: {ReportId} \"{ReportName}\"\n\tUrl: {WebUrl}"
                    , report.Id
                    , report.Name
                    , report.WebUrl);
            if (import.Datasets.TryGetFirst(out var dataset))
                Log.Information("\tDataset: {DatasetId} \"{DatasetName}\"\n\tUrl: {WebUrl}"
                    , dataset.Id
                    , dataset.Name
                    , dataset.WebUrl);
            Log.Information("Report Created: {Created}", import.CreatedDateTime);
            Log.Information("Report Updated: {Updated}", import.UpdatedDateTime);
        }

        public class DeploymentSourceInfo
        {
            public string FullPath { get; set; }
            public IDictionary<string, string> Parameters { get; set; }
        }

        public class ReportDeploymentInfo
        {
            public PbiDeploymentOptions Options { get; init; }
            public DeploymentParameters Parameters { get; init; }
            public string SourcePath { get; init; }
            public string DisplayName { get; init; }
            public string PbixPath { get; init; }
            public IPbixModel Model { get; internal set; }
            public Dictionary<string, (Group, Capacity)> WorkspaceCache { get; } = new();
        }

    }
}
