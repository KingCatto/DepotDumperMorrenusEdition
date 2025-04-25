using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SteamKit2;
namespace DepotDumper
{
    /// <summary>
    /// Provides persistent storage and retrieval of manifest creation dates
    /// to ensure consistent dates across multiple runs
    /// </summary>
    public static class ManifestDate
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, DateTime> _knownDates = new Dictionary<string, DateTime>();
        private static bool _isLoaded = false;
        private static bool _isDirty = false;

        private static string DatabasePath => Path.Combine(
            DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR,
            DepotDumper.CONFIG_DIR,
            "manifest_dates.json");

        /// <summary>
        /// Generates a unique key for identifying a specific manifest
        /// </summary>
        private static string GetKey(uint depotId, ulong manifestId, string branch)
        {
            return $"{depotId}_{manifestId}_{branch.Replace('/', '_')}";
        }

        /// <summary>
        /// Loads the manifest date database from disk if not already loaded
        /// </summary>
        public static void Initialize()
        {
            if (_isLoaded) return;

            lock (_lock)
            {
                if (_isLoaded) return;

                try
                {
                    if (File.Exists(DatabasePath))
                    {
                        string json = File.ReadAllText(DatabasePath);
                        var loadedDates = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                        if (loadedDates != null)
                        {
                            foreach (var kvp in loadedDates)
                            {
                                _knownDates[kvp.Key] = kvp.Value;
                            }

                            Logger.Info($"Loaded {_knownDates.Count} manifest dates from database");
                        }
                    }
                    else
                    {
                        Logger.Info("No manifest date database found, will create one");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading manifest dates database: {ex.Message}");
                }

                _isLoaded = true;
            }
        }

        /// <summary>
        /// Saves the manifest date database to disk if changes have been made
        /// </summary>
        public static void Save()
        {
            if (!_isDirty) return;

            lock (_lock)
            {
                if (!_isDirty) return;

                try
                {
                    string dirPath = Path.GetDirectoryName(DatabasePath);
                    if (!string.IsNullOrEmpty(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                    string json = JsonSerializer.Serialize(_knownDates, options);
                    File.WriteAllText(DatabasePath, json);

                    Logger.Info($"Saved {_knownDates.Count} manifest dates to database");
                    _isDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error saving manifest dates database: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets the known date for a manifest, or null if not available
        /// </summary>
        public static DateTime? GetDate(uint depotId, ulong manifestId, string branch)
        {
            Initialize();

            string key = GetKey(depotId, manifestId, branch);
            lock (_lock)
            {
                return _knownDates.TryGetValue(key, out var date) ? date : null;
            }
        }

        /// <summary>
        /// Sets or updates the date for a manifest
        /// </summary>
        public static void SetDate(uint depotId, ulong manifestId, string branch, DateTime date)
        {
            Initialize();

            string key = GetKey(depotId, manifestId, branch);
            lock (_lock)
            {
                if (!_knownDates.TryGetValue(key, out var existingDate) || existingDate != date)
                {
                    _knownDates[key] = date;
                    _isDirty = true;
                    Logger.Debug($"Stored date {date} for manifest {manifestId} (Depot {depotId}, Branch '{branch}')");
                }
            }
        }

        /// <summary>
        /// Determines the best date for a manifest and stores it for future use
        /// </summary>
        public static DateTime DetermineAndStoreDate(
            uint depotId,
            ulong manifestId,
            string branch,
            DepotManifest manifest,
            DateTime fallbackDate)
        {
            // First check if we already have a stored date
            DateTime? storedDate = GetDate(depotId, manifestId, branch);
            if (storedDate.HasValue)
            {
                Logger.Debug($"Using stored date for manifest {manifestId}: {storedDate.Value}");
                return storedDate.Value;
            }

            // Try to get the creation time from the manifest
            DateTime determinedDate;

            if (manifest != null && manifest.CreationTime.Year >= 2000)
            {
                determinedDate = manifest.CreationTime;
                Logger.Debug($"Using manifest creation time for manifest {manifestId}: {determinedDate}");
            }
            else
            {
                determinedDate = fallbackDate;
                Logger.Debug($"Using fallback date for manifest {manifestId}: {determinedDate}");
            }

            // Store the determined date
            SetDate(depotId, manifestId, branch, determinedDate);

            return determinedDate;
        }
    }
}