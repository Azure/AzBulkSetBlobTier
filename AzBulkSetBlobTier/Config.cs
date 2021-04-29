using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzBulkSetBlobTier
{
    public class Config
    {
        public string Run { get; set; }
        public string Prefix { get; set; }
        public string StorageConnectionString { get; set; }
        public string Container { get; set; }
        public string TargetAccessTier { get; set; }
        public string SourceAccessTier { get; set; }
        public string Delimiter { get; set; }
        public int ThreadCount { get; set; }
        public bool WhatIf { get; set; }
    }
}
