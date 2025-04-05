using System;
using System.Collections.Generic;
namespace DepotDumper
{
    public class OperationSummary
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public int TotalAppsProcessed { get; set; }
        public int SuccessfulApps { get; set; }
        public int FailedApps { get; set; }
        public int TotalDepotsProcessed { get; set; }
        public int SuccessfulDepots { get; set; }
        public int FailedDepots { get; set; }
        public int TotalManifestsProcessed { get; set; }
        public int NewManifestsDownloaded { get; set; }
        public int ManifestsSkipped { get; set; }
        public int FailedManifests { get; set; }
        public List<string> ProcessedAppIds { get; set; } = new List<string>();
        public List<AppProcessingSummary> AppSummaries { get; set; } = new List<AppProcessingSummary>();
        public List<string> Errors { get; set; } = new List<string>();
    }
    public class AppProcessingSummary
    {
        public uint AppId { get; set; }
        public string AppName { get; set; }
        public DateTime? LastUpdated { get; set; }
        public int TotalDepots { get; set; }
        public int ProcessedDepots { get; set; }
        public int SkippedDepots { get; set; }
        public int TotalManifests { get; set; }
        public int NewManifests { get; set; }
        public int SkippedManifests { get; set; }
        public List<string> AppErrors { get; set; } = new List<string>();
        public List<DepotProcessingSummary> DepotSummaries { get; set; } = new List<DepotProcessingSummary>();
        public bool Success => AppErrors.Count == 0 && ProcessedDepots == TotalDepots - SkippedDepots;
    }
    public class DepotProcessingSummary
    {
        public uint DepotId { get; set; }
        public uint AppId { get; set; }
        public int ManifestsFound { get; set; }
        public int ManifestsDownloaded { get; set; }
        public int ManifestsSkipped { get; set; }
        public List<string> DepotErrors { get; set; } = new List<string>();
        public List<ManifestSummary> Manifests { get; set; } = new List<ManifestSummary>();
        public bool Success => DepotErrors.Count == 0;
    }
    public class ManifestSummary
    {
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public string Branch { get; set; }
        public bool WasDownloaded { get; set; }
        public bool WasSkipped { get; set; }
        public string FilePath { get; set; }
        public DateTime? LastUpdated { get; set; }
        public List<string> ManifestErrors { get; set; } = new List<string>();
        public bool Success => ManifestErrors.Count == 0 && (WasDownloaded || WasSkipped);
    }
}