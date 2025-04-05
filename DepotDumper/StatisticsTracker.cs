using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace DepotDumper
{
    public static class StatisticsTracker
    {
        private static readonly object Lock = new object();
        private static OperationSummary currentSummary;
        private static ConcurrentDictionary<uint, AppProcessingSummary> appSummaries = new ConcurrentDictionary<uint, AppProcessingSummary>();
        private static ConcurrentDictionary<uint, DepotProcessingSummary> depotSummaries = new ConcurrentDictionary<uint, DepotProcessingSummary>();
        private static ConcurrentBag<string> errors = new ConcurrentBag<string>();
        private static int totalApps = 0;
        private static int successfulApps = 0;
        private static int failedApps = 0;
        private static int totalDepots = 0;
        private static int successfulDepots = 0;
        private static int failedDepots = 0;
        private static int totalManifests = 0;
        private static int newManifests = 0;
        private static int skippedManifests = 0;
        private static int failedManifests = 0;

        public static void Initialize()
        {
            lock (Lock)
            {
                currentSummary = new OperationSummary
                {
                    StartTime = DateTime.Now
                };
                appSummaries = new ConcurrentDictionary<uint, AppProcessingSummary>();
                depotSummaries = new ConcurrentDictionary<uint, DepotProcessingSummary>();
                errors = new ConcurrentBag<string>();
                totalApps = 0;
                successfulApps = 0;
                failedApps = 0;
                totalDepots = 0;
                successfulDepots = 0;
                failedDepots = 0;
                totalManifests = 0;
                newManifests = 0;
                skippedManifests = 0;
                failedManifests = 0;
                Logger.Info("Statistics tracking initialized");
            }
        }

        public static void TrackAppStart(uint appId, string appName, DateTime? lastUpdated = null)
        {
            lock (Lock)
            {
                Interlocked.Increment(ref totalApps);
                var summary = new AppProcessingSummary
                {
                    AppId = appId,
                    AppName = appName ?? $"App {appId}",
                    LastUpdated = lastUpdated
                };
                appSummaries[appId] = summary;
                Logger.Debug($"Started tracking app {appId} ({appName})");
            }
        }

        public static void TrackAppCompletion(uint appId, bool success, List<string> appErrors = null)
        {
            lock (Lock)
            {
                if (appSummaries.TryGetValue(appId, out var summary))
                {
                    summary.Success = success; // Explicitly set success flag
                    
                    if (appErrors != null)
                    {
                        summary.AppErrors.AddRange(appErrors);
                        foreach (var error in appErrors)
                        {
                            errors.Add($"App {appId}: {error}");
                        }
                    }
                    
                    if (success)
                    {
                        Interlocked.Increment(ref successfulApps);
                        Logger.Info($"App {appId} ({summary.AppName}) processed successfully");
                    }
                    else
                    {
                        Interlocked.Increment(ref failedApps);
                        Logger.Warning($"App {appId} ({summary.AppName}) processing failed");
                    }
                }
            }
        }

        public static void TrackDepotStart(uint depotId, uint appId, int manifestCount = 0)
        {
            lock (Lock)
            {
                Interlocked.Increment(ref totalDepots);
                var summary = new DepotProcessingSummary
                {
                    DepotId = depotId,
                    AppId = appId,
                    ManifestsFound = manifestCount
                };
                depotSummaries[depotId] = summary;
                if (appSummaries.TryGetValue(appId, out var appSummary))
                {
                    appSummary.TotalDepots++;
                    appSummary.DepotSummaries.Add(summary);
                }
                Logger.Debug($"Started tracking depot {depotId} for app {appId} with {manifestCount} manifests");
            }
        }

        public static void TrackDepotCompletion(uint depotId, bool success, List<string> depotErrors = null)
        {
            lock (Lock)
            {
                if (depotSummaries.TryGetValue(depotId, out var summary))
                {
                    summary.Success = success; // Explicitly set success flag
                    
                    if (depotErrors != null)
                    {
                        summary.DepotErrors.AddRange(depotErrors);
                        foreach (var error in depotErrors)
                        {
                            errors.Add($"Depot {depotId}: {error}");
                        }
                    }
                    
                    if (success)
                    {
                        Interlocked.Increment(ref successfulDepots);
                        Logger.Info($"Depot {depotId} processed successfully");
                    }
                    else
                    {
                        Interlocked.Increment(ref failedDepots);
                        Logger.Warning($"Depot {depotId} processing failed");
                    }
                    
                    if (appSummaries.TryGetValue(summary.AppId, out var appSummary))
                    {
                        appSummary.ProcessedDepots++;
                    }
                }
            }
        }

        public static void TrackDepotSkipped(uint depotId, uint appId, string reason)
        {
            lock (Lock)
            {
                if (appSummaries.TryGetValue(appId, out var appSummary))
                {
                    appSummary.SkippedDepots++;
                    Logger.Debug($"Skipped depot {depotId} for app {appId}: {reason}");
                }
            }
        }

        public static void TrackManifestProcessing(uint depotId, ulong manifestId, string branch,
            bool downloaded, bool skipped, string filePath = null, DateTime? lastUpdated = null, List<string> manifestErrors = null)
        {
            lock (Lock)
            {
                Interlocked.Increment(ref totalManifests);
                if (downloaded)
                    Interlocked.Increment(ref newManifests);
                if (skipped)
                    Interlocked.Increment(ref skippedManifests);
                if (manifestErrors != null && manifestErrors.Count > 0)
                    Interlocked.Increment(ref failedManifests);
                
                var manifestSummary = new ManifestSummary
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    Branch = branch,
                    WasDownloaded = downloaded,
                    WasSkipped = skipped,
                    FilePath = filePath,
                    LastUpdated = lastUpdated,
                    ManifestErrors = manifestErrors ?? new List<string>()
                };
                
                if (depotSummaries.TryGetValue(depotId, out var depotSummary))
                {
                    depotSummary.Manifests.Add(manifestSummary);
                    if (downloaded)
                        depotSummary.ManifestsDownloaded++;
                    if (skipped)
                        depotSummary.ManifestsSkipped++;
                    
                    if (appSummaries.TryGetValue(depotSummary.AppId, out var appSummary))
                    {
                        appSummary.TotalManifests++;
                        if (downloaded)
                            appSummary.NewManifests++;
                        if (skipped)
                            appSummary.SkippedManifests++;
                    }
                }
                
                if (manifestErrors != null)
                {
                    foreach (var error in manifestErrors)
                    {
                        errors.Add($"Manifest {depotId}_{manifestId} ({branch}): {error}");
                    }
                }
                
                if (downloaded)
                    Logger.Info($"Downloaded manifest {manifestId} for depot {depotId} (branch: {branch})");
                else if (skipped)
                    Logger.Debug($"Skipped manifest {manifestId} for depot {depotId} (branch: {branch})");
                else if (manifestErrors != null && manifestErrors.Count > 0)
                    Logger.Error($"Failed to process manifest {manifestId} for depot {depotId} (branch: {branch})");
            }
        }

        public static void TrackError(string error)
        {
            lock (Lock)
            {
                errors.Add(error);
                Logger.Error(error);
            }
        }

        public static OperationSummary GetSummary()
        {
            lock (Lock)
            {
                currentSummary.EndTime = DateTime.Now;
                currentSummary.TotalAppsProcessed = totalApps;
                currentSummary.SuccessfulApps = successfulApps;
                currentSummary.FailedApps = failedApps;
                currentSummary.TotalDepotsProcessed = totalDepots;
                currentSummary.SuccessfulDepots = successfulDepots;
                currentSummary.FailedDepots = failedDepots;
                currentSummary.TotalManifestsProcessed = totalManifests;
                currentSummary.NewManifestsDownloaded = newManifests;
                currentSummary.ManifestsSkipped = skippedManifests;
                currentSummary.FailedManifests = failedManifests;
                currentSummary.AppSummaries = new List<AppProcessingSummary>(appSummaries.Values);
                currentSummary.ProcessedAppIds = new List<string>();
                
                foreach (var app in appSummaries.Values)
                {
                    currentSummary.ProcessedAppIds.Add(app.AppId.ToString());
                }
                
                currentSummary.Errors = new List<string>(errors);
                return currentSummary;
            }
        }

        public static void PrintSummary()
        {
            var summary = GetSummary();
            Console.WriteLine();
            Console.WriteLine("=== Operation Summary ===");
            Console.WriteLine($"Duration: {FormatTimeSpan(summary.Duration)}");
            Console.WriteLine($"Apps: {summary.SuccessfulApps} successful, {summary.FailedApps} failed");
            Console.WriteLine($"Depots: {summary.SuccessfulDepots} successful, {summary.FailedDepots} failed");
            Console.WriteLine($"Manifests: {summary.NewManifestsDownloaded} downloaded, {summary.ManifestsSkipped} skipped, {summary.FailedManifests} failed");
            if (summary.Errors.Count > 0)
            {
                Console.WriteLine($"Errors: {summary.Errors.Count} errors encountered");
            }
            Console.WriteLine("========================");
        }

        private static string FormatTimeSpan(TimeSpan span)
        {
            if (span.TotalDays >= 1)
                return $"{span.Days}d {span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalHours >= 1)
                return $"{span.Hours}h {span.Minutes}m {span.Seconds}s";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds}s";
            return $"{span.Seconds}.{span.Milliseconds}s";
        }
    }
}