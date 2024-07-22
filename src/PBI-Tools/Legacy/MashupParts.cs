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

using System.IO;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace PbiTools.Model
{
    public class MashupParts
    {
        public MemoryStream Package { get; set; }
        public JObject Permissions { get; set; }
        public XDocument Metadata { get; set; }
        public JArray QueryGroups { get; set; }
        public MemoryStream Content { get; set; }
    }
}
