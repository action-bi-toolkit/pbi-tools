﻿using System.IO;
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