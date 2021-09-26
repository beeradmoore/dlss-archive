using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DLSSArchiveBuilder
{
    internal class DLSSRecords
    {
        [JsonPropertyName("stable")]
        public List<DLSSRecord> Stable { get; set; } = new List<DLSSRecord>();

        [JsonPropertyName("experimental")]
        public List<DLSSRecord> Experimental { get; set; } = new List<DLSSRecord>();
    }
}
