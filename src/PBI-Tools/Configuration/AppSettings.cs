// Copyright (c) Mathias Thierbach
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Serilog.Core;
using Serilog.Events;

namespace PbiTools.Configuration
{
    using ProjectSystem;

    public static class Env
    {
        public static string GetEnvironmentSetting(string name) => Environment.GetEnvironmentVariable(name) switch
        {
            var value when value is not null => Environment.ExpandEnvironmentVariables(value),
            _ => null
        };

        public static bool TryGetEnvironmentSetting(string name, out string value)
        {
            value = GetEnvironmentSetting(name);
            return value != null;
        }

        private const string EnvPrefix = "PBITOOLS_";

        public static readonly string LogLevel = $"{EnvPrefix}{nameof(LogLevel)}";
        public static readonly string PbiInstallDir = $"{EnvPrefix}{nameof(PbiInstallDir)}";
        public static readonly string AppDataDir = $"{EnvPrefix}{nameof(AppDataDir)}";
        public static readonly string Debug = $"{EnvPrefix}{nameof(Debug)}";
        public static readonly string UICulture = $"{EnvPrefix}{nameof(UICulture)}";
        public static readonly string EffectiveDate = $"{EnvPrefix}{nameof(EffectiveDate)}";
        public static readonly string DefaultModelSerialization = $"{EnvPrefix}{nameof(DefaultModelSerialization)}";
#if NETFRAMEWORK
        public static readonly string ExternalAmoPath = $"{EnvPrefix}{nameof(ExternalAmoPath)}";
#endif
    }

    public class AppSettings
    {

        public const string Edition
#if NETFRAMEWORK
            = "Desktop";
#elif NET
            = "Core";
#endif

        public static string GetEnvironmentSetting(string name) => Env.GetEnvironmentSetting(name);

        public static bool GetBooleanSetting(string name) => Env.GetEnvironmentSetting(name)
            switch
            {
                var value when bool.TryParse(value, out var result) => result,
                "1" => true,
                "0" => false,
                _ => false
            };

        public static ModelSerializationMode? DefaultModelSerializationMode => GetEnvironmentSetting(Env.DefaultModelSerialization) switch
        {
            var s when Enum.TryParse<ModelSerializationMode>(s, out var result) => result,
            _ => null
        };

        public AppSettings()
        {
            // The Console log level can optionally be configured vai an environment variable:
            var envLogLevel = GetEnvironmentSetting(Env.LogLevel);
            var initialLogLevel = envLogLevel != null && Enum.TryParse<LogEventLevel>(envLogLevel, out var logLevel)
                ? logLevel
                : LogEventLevel.Information; // Default log level
            this.LevelSwitch = new LoggingLevelSwitch(
#if DEBUG
                // A DEBUG compilation will always log at the Verbose level
                LogEventLevel.Verbose
#else
                initialLogLevel
#endif
            );
        }

        public LoggingLevelSwitch LevelSwitch { get; }

        internal bool ShouldSuppressConsoleLogs { get; set; } = false;

        public bool TryApplyCustomCulture(out Exception error) 
        {
            error = default;
            var envSetting = GetEnvironmentSetting(Env.UICulture);
            if (String.IsNullOrWhiteSpace(envSetting))
                return true;

            try {
                var culture = CultureInfo.CreateSpecificCulture(envSetting);

                Thread.CurrentThread.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;

                return true;
            }
            catch (CultureNotFoundException ex) {
                error = ex;
                return false;
            }
        }

        public static JObject AsJson() =>
            new JObject(
                typeof(Env).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                .Select(f =>
                {
                    var envName = (string)f.GetValue(null);
                    return new JProperty(envName, GetEnvironmentSetting(envName));
                })
            );

        /// <summary>
        /// Completely disables the console logger inside an <c>IDisposable</c> scope.
        /// </summary>
        public IDisposable SuppressConsoleLogs()
        {
            var prevValue = this.ShouldSuppressConsoleLogs;
            this.ShouldSuppressConsoleLogs = true;
            return new Disposable(()=>
            {
                this.ShouldSuppressConsoleLogs = prevValue;
            });
        }

        /// <summary>
        /// Resets the console log level to the specified level inside an <c>IDisposable</c> scope.
        /// </summary>
        public IDisposable SetScopedLogLevel(LogEventLevel logLevel, bool overwriteVerbose = false)
        {
            if (this.LevelSwitch.MinimumLevel == LogEventLevel.Verbose && !overwriteVerbose)
                return new Disposable(() => {});

            if (this.LevelSwitch.MinimumLevel == logLevel)
                return new Disposable(() => {});

            var prevLogLevel = this.LevelSwitch.MinimumLevel;
            this.LevelSwitch.MinimumLevel = logLevel;
            return new Disposable(() => 
            {
                this.LevelSwitch.MinimumLevel = prevLogLevel;
            });
        }

        private class Disposable : IDisposable
        {
            private readonly Action _disposeAction;

            public Disposable(Action disposeAction)
            {
                this._disposeAction = disposeAction ?? throw new ArgumentNullException(nameof(disposeAction));
            }
            void IDisposable.Dispose()
            {
                _disposeAction();
            }
        }
    }

}
