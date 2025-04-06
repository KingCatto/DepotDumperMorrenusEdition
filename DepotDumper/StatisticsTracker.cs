using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
                if (!appSummaries.ContainsKey(appId))
                {
                    Interlocked.Increment(ref totalApps);
                    var summary = new AppProcessingSummary
                    {
                        AppId = appId,
                        AppName = appName ?? $"App {appId}",
                        LastUpdated = lastUpdated,
                        TotalDepots = 0,
                        ProcessedDepots = 0,
                        SkippedDepots = 0,
                        TotalManifests = 0,
                        NewManifests = 0,
                        SkippedManifests = 0,
                    };
                    appSummaries[appId] = summary;
                    Logger.Debug($"[TrackAppStart] App {appId} ({appName}) started tracking. LastUpdated: {lastUpdated}. Total Apps Now: {totalApps}");
                }
                else
                {
                    if (appSummaries.TryGetValue(appId, out var existingSummary))
                    {
                        if (existingSummary.AppName != appName && !string.IsNullOrEmpty(appName)) existingSummary.AppName = appName;
                        if (existingSummary.LastUpdated != lastUpdated && lastUpdated.HasValue) existingSummary.LastUpdated = lastUpdated;
                         Logger.Debug($"[TrackAppStart] App {appId} ({appName}) already tracked, potentially updated info.");
                    }
                }
            }
        }

        public static void TrackAppCompletion(uint appId, bool success, List<string> appErrors = null)
        {
            lock (Lock)
            {
                if (appSummaries.TryGetValue(appId, out var summary))
                {
                     if (summary.Success == null)
                     {
                        summary.Success = success;

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
                            Logger.Info($"[TrackAppCompletion] App {appId} ({summary.AppName}) processed successfully");
                        }
                        else
                        {
                            Interlocked.Increment(ref failedApps);
                            Logger.Warning($"[TrackAppCompletion] App {appId} ({summary.AppName}) processing failed");
                        }
                        Logger.Debug($"[TrackAppCompletion] App {appId} Summary: TotalDepots={summary.TotalDepots}, ProcessedDepots={summary.ProcessedDepots}, SkippedDepots={summary.SkippedDepots}, TotalManifests={summary.TotalManifests}, NewManifests={summary.NewManifests}, SkippedManifests={summary.SkippedManifests}");
                    }
                    else
                    {
                         Logger.Debug($"[TrackAppCompletion] App {appId} completion already recorded. Success: {summary.Success}");
                    }
                }
                else
                {
                    Logger.Warning($"[TrackAppCompletion] AppSummary not found for app {appId} during completion tracking.");
                }
            }
        }

        public static bool IsAppTracked(uint appId)
        {
            lock (Lock)
            {
                return appSummaries.ContainsKey(appId);
            }
        }


        public static void TrackDepotStart(uint depotId, uint appId, int manifestCount = 0)
        {
            lock (Lock)
            {
                if (!depotSummaries.ContainsKey(depotId))
                {
                    Interlocked.Increment(ref totalDepots);
                    var summary = new DepotProcessingSummary
                    {
                        DepotId = depotId,
                        AppId = appId,
                        ManifestsFound = manifestCount,
                        Success = null
                    };
                    depotSummaries[depotId] = summary;

                    if (appSummaries.TryGetValue(appId, out var appSummary))
                    {
                        appSummary.TotalDepots++;
                        appSummary.DepotSummaries.Add(summary);
                        Logger.Debug($"[TrackDepotStart] Added DepotSummary for depot {depotId} to AppSummary for app {appId}. DepotSummaries.Count: {appSummary.DepotSummaries.Count}");
                    }
                    else
                    {
                        Logger.Warning($"[TrackDepotStart] AppSummary not found for app {appId} when tracking depot {depotId}");
                    }
                    Logger.Info($"[TrackDepotStart] Started tracking depot {depotId} for app {appId} with {manifestCount} manifests");
                }
                else
                {
                     Logger.Debug($"[TrackDepotStart] Depot {depotId} already tracked.");
                }
            }
        }

        public static void TrackDepotCompletion(uint depotId, bool success, List<string> depotErrors = null)
        {
            lock (Lock)
            {
                if (depotSummaries.TryGetValue(depotId, out var summary))
                {
                    if (summary.Success == null)
                    {
                        summary.Success = success;

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
                            Logger.Info($"[TrackDepotCompletion] Depot {depotId} processed successfully");
                        }
                        else
                        {
                            Interlocked.Increment(ref failedDepots);
                            Logger.Warning($"[TrackDepotCompletion] Depot {depotId} processing failed");
                        }

                        if (appSummaries.TryGetValue(summary.AppId, out var appSummary))
                        {
                            appSummary.ProcessedDepots++;
                            Logger.Debug($"[TrackDepotCompletion] Incremented ProcessedDepots count for app {summary.AppId}. ProcessedDepots: {appSummary.ProcessedDepots}, TotalDepots: {appSummary.TotalDepots}");
                        }
                        else
                        {
                            Logger.Warning($"[TrackDepotCompletion] AppSummary not found for app {summary.AppId} when completing depot {depotId}");
                        }
                    }
                     else
                     {
                          Logger.Debug($"[TrackDepotCompletion] Depot {depotId} completion already recorded. Success: {summary.Success}");
                     }
                }
                else
                {
                    Logger.Warning($"[TrackDepotCompletion] DepotSummary not found for depot {depotId} on completion");
                }
            }
        }

        public static void TrackDepotSkipped(uint depotId, uint appId, string reason)
        {
            lock (Lock)
            {
                if (!depotSummaries.ContainsKey(depotId))
                {
                    Interlocked.Increment(ref totalDepots);
                    var summary = new DepotProcessingSummary
                    {
                        DepotId = depotId,
                        AppId = appId,
                        ManifestsFound = 0,
                        Success = true
                    };
                    depotSummaries[depotId] = summary;
                    if (appSummaries.TryGetValue(appId, out var appSummaryForDepotAdd))
                    {
                         if (!appSummaryForDepotAdd.DepotSummaries.Any(ds => ds.DepotId == depotId))
                         {
                              appSummaryForDepotAdd.TotalDepots++;
                              appSummaryForDepotAdd.DepotSummaries.Add(summary);
                         }
                    }
                    else
                    {
                         Logger.Warning($"[TrackDepotSkipped] AppSummary {appId} not found when initially tracking skipped depot {depotId}.");
                    }
                }

                if (appSummaries.TryGetValue(appId, out var appSummary))
                {
                    if (depotSummaries.TryGetValue(depotId, out var depotSummary) && depotSummary.Success == null)
                    {
                        appSummary.SkippedDepots++;
                        depotSummary.Success = true;
                        if (!depotSummary.DepotErrors.Contains($"Skipped: {reason}"))
                        {
                           depotSummary.DepotErrors.Add($"Skipped: {reason}");
                        }
                        Interlocked.Increment(ref successfulDepots);
                        Logger.Info($"[TrackDepotSkipped] Skipped depot {depotId} for app {appId}: {reason}");
                    }
                    else if(depotSummaries.TryGetValue(depotId, out var depotSummaryCheck) && depotSummaryCheck.Success != null)
                    {
                         Logger.Debug($"[TrackDepotSkipped] Depot {depotId} completion already recorded, skip count not incremented again.");
                    }
                    else
                    {
                         Logger.Warning($"[TrackDepotSkipped] DepotSummary {depotId} missing when trying to increment skip count for App {appId}.");
                    }
                }
                else
                {
                    Logger.Warning($"[TrackDepotSkipped] AppSummary {appId} not found when attempting to increment skip count for depot {depotId}.");
                }
            }
        }

        // Ensure this is the correct signature the compiler uses
        public static void TrackManifestProcessing(uint depotId, ulong manifestId, string branch,
            bool downloaded, bool skipped, string filePath = null, List<string> manifestErrors = null, DateTime? manifestCreationTime = null)
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
                    ManifestErrors = manifestErrors ?? new List<string>(),
                    Success = manifestErrors == null || manifestErrors.Count == 0
                };

                if (depotSummaries.TryGetValue(depotId, out var depotSummary))
                {
                    if (!depotSummary.Manifests.Any(m => m.ManifestId == manifestId && m.Branch == branch))
                    {
                         depotSummary.Manifests.Add(manifestSummary);
                    }

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

                        if (manifestCreationTime.HasValue && appSummary.LastUpdated == null)
                        {
                            appSummary.LastUpdated = manifestCreationTime.Value;
                            Logger.Debug($"[TrackManifestProcessing] Updated App {appSummary.AppId} LastUpdated using fallback manifest time: {manifestCreationTime.Value}");
                        }
                    }
                }
                 else
                 {
                     Logger.Warning($"[TrackManifestProcessing] DepotSummary not found for depot {depotId} when processing manifest {manifestId}.");
                 }

                if (manifestErrors != null)
                {
                    foreach (var error in manifestErrors)
                    {
                        string errorMsg = $"Manifest {depotId}_{manifestId} ({branch}): {error}";
                        if(!errors.Contains(errorMsg))
                           errors.Add(errorMsg);
                    }
                }

                if (downloaded)
                    Logger.Info($"[TrackManifestProcessing] Downloaded manifest {manifestId} for depot {depotId} (branch: {branch})");
                else if (skipped)
                    Logger.Debug($"[TrackManifestProcessing] Skipped manifest {manifestId} for depot {depotId} (branch: {branch})");
                else if (manifestErrors != null && manifestErrors.Count > 0)
                    Logger.Error($"[TrackManifestProcessing] Failed to process manifest {manifestId} for depot {depotId} (branch: {branch})");
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

                Logger.Debug($"[GetSummary] AppSummaries.Count: {currentSummary.AppSummaries.Count}, TotalAppsProcessed: {currentSummary.TotalAppsProcessed}, SuccessfulApps: {currentSummary.SuccessfulApps}, FailedApps: {currentSummary.FailedApps}, TotalDepotsProcessed: {currentSummary.TotalDepotsProcessed}, SuccessfulDepots: {currentSummary.SuccessfulDepots}, FailedDepots: {currentSummary.FailedDepots}, TotalManifestsProcessed: {currentSummary.TotalManifestsProcessed}, NewManifestsDownloaded: {currentSummary.NewManifestsDownloaded}, ManifestsSkipped: {currentSummary.ManifestsSkipped}, FailedManifests: {currentSummary.FailedManifests}");

                return currentSummary;
            }
        }

        public static void PrintSummary()
        {
            var summary = GetSummary();
            Console.WriteLine();
            Console.WriteLine("=== Operation Summary ===");
            Console.WriteLine($"Duration: {FormatTimeSpan(summary.Duration)}");
            Console.WriteLine($"Apps: {summary.SuccessfulApps} successful, {summary.FailedApps} failed (Total Parent Groups Tracked: {summary.TotalAppsProcessed})");
            Console.WriteLine($"Depots: {summary.SuccessfulDepots} successful, {summary.FailedDepots} failed (Total Depots Tracked: {summary.TotalDepotsProcessed})");
            Console.WriteLine($"Manifests: {summary.NewManifestsDownloaded} downloaded, {summary.ManifestsSkipped} skipped, {summary.FailedManifests} failed (Total Manifests Tracked: {summary.TotalManifestsProcessed})");
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
            return $"{span.Seconds}.{span.Milliseconds / 10}s";
        }
    }
}