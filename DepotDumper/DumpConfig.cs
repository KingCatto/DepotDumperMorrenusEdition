namespace DepotDumper
{
    class DumpConfig
    {
        public int CellID { get; set; }
        public string DumpDirectory { get; set; }
        public int MaxServers { get; set; } = 50;  // Increased from 20
        public int MaxDownloads { get; set; } = 16; // Increased from 4
        public bool RememberPassword { get; set; }
        public uint? LoginID { get; set; }
        public bool UseQrCode { get; set; }
        public bool UseNewNamingFormat { get; set; } = true;
        
        // Additional performance settings
        public int ConnectionPoolSize { get; set; } = 20;
        public int RequestTimeout { get; set; } = 60; // seconds
        public bool EnableCheckpointing { get; set; } = true;
        public bool SkipExistingManifests { get; set; } = true;
        public int NetworkRetryCount { get; set; } = 5;
        public int NetworkRetryDelayMs { get; set; } = 500;
        public bool CompressManifests { get; set; } = true;
        public bool UseSharedCdnPools { get; set; } = true;
        public int FileBufferSizeKb { get; set; } = 64;
    }
}