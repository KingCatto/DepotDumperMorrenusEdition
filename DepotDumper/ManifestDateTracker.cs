using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace DepotDumper
{
    public class ManifestDateEntry
    {
        public uint DepotId { get; set; }
        public ulong ManifestId { get; set; }
        public string Branch { get; set; }
        public DateTime CreationDate { get; set; }
        public string FolderPath { get; set; }
    }
    public static class ManifestDateTracker
    {
        private static readonly string JsonFilePath;
        private static readonly Dictionary<string, ManifestDateEntry> dateEntries = new Dictionary<string, ManifestDateEntry>();
        private static readonly object fileLock = new object();
        private static bool isDirty = false;
        static ManifestDateTracker()
        {
            string baseDir = DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR;
            string configDir = Path.Combine(baseDir, DepotDumper.CONFIG_DIR);
            JsonFilePath = Path.Combine(configDir, "manifest_folder_dates.json");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            LoadFromFile();
        }
        // Generate a unique key for each manifest
        private static string GetKey(uint depotId, ulong manifestId, string branch)
        {
            return $"{depotId}_{manifestId}_{branch.Replace('/', '_')}";
        }
        // Load existing date information from JSON file
        public static void LoadFromFile()
        {
            lock (fileLock)
            {
                try
                {
                    if (File.Exists(JsonFilePath))
                    {
                        string json = File.ReadAllText(JsonFilePath);
                        var entries = JsonSerializer.Deserialize<List<ManifestDateEntry>>(json);
                        if (entries != null)
                        {
                            dateEntries.Clear();
                            foreach (var entry in entries)
                            {
                                string key = GetKey(entry.DepotId, entry.ManifestId, entry.Branch);
                                dateEntries[key] = entry;
                            }
                            Logger.Info($"Loaded {dateEntries.Count} manifest date entries from {JsonFilePath}");
                        }
                    }
                    else
                    {
                        Logger.Info($"Manifest date JSON file not found at {JsonFilePath}, starting with empty data");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading manifest dates from JSON: {ex.Message}");
                }
            }
        }
        // Save all current date information to the JSON file
        public static void SaveToFile()
        {
            lock (fileLock)
            {
                if (!isDirty)
                {
                    Logger.Debug("No changes to manifest date tracker, skipping save");
                    return;
                }
                try
                {
                    var entries = dateEntries.Values.ToList();
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    string json = JsonSerializer.Serialize(entries, options);
                    File.WriteAllText(JsonFilePath, json);
                    Logger.Info($"Saved {entries.Count} manifest date entries to {JsonFilePath}");
                    isDirty = false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error saving manifest dates to JSON: {ex.Message}");
                }
            }
        }
        // Add or update an entry
        public static void SetEntry(uint depotId, ulong manifestId, string branch, DateTime date, string folderPath = null)
        {
            lock (fileLock)
            {
                string key = GetKey(depotId, manifestId, branch);
                // If entry exists and the folder path is not being updated, preserve the existing path
                if (dateEntries.TryGetValue(key, out var existingEntry) && folderPath == null)
                {
                    folderPath = existingEntry.FolderPath;
                    // If we're not changing anything, don't mark as dirty
                    if (existingEntry.CreationDate == date)
                        return;
                }
                dateEntries[key] = new ManifestDateEntry
                {
                    DepotId = depotId,
                    ManifestId = manifestId,
                    Branch = branch,
                    CreationDate = date,
                    FolderPath = folderPath
                };
                isDirty = true;
            }
        }
        // Get a date entry
        public static ManifestDateEntry GetEntry(uint depotId, ulong manifestId, string branch)
        {
            lock (fileLock)
            {
                string key = GetKey(depotId, manifestId, branch);
                if (dateEntries.TryGetValue(key, out var entry))
                {
                    return entry;
                }
                return null;
            }
        }
        // Get a date directly
        public static DateTime? GetDate(uint depotId, ulong manifestId, string branch)
        {
            var entry = GetEntry(depotId, manifestId, branch);
            return entry?.CreationDate;
        }
        // Update folder path for an entry
        public static void UpdateFolderPath(uint depotId, ulong manifestId, string branch, string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;
            lock (fileLock)
            {
                string key = GetKey(depotId, manifestId, branch);
                if (dateEntries.TryGetValue(key, out var entry))
                {
                    if (entry.FolderPath != folderPath)
                    {
                        entry.FolderPath = folderPath;
                        isDirty = true;
                    }
                }
            }
        }
        // Update folder paths by scanning directories
        public static void UpdateFolderPaths(string baseDirectory)
        {
            lock (fileLock)
            {
                try
                {
                    Logger.Info("Updating folder paths for manifest date entries...");
                    // Group entries by app ID to make searching more efficient
                    var entriesByApp = new Dictionary<uint, List<ManifestDateEntry>>();
                    foreach (var entry in dateEntries.Values)
                    {
                        uint appId = 0;
                        // App ID is not stored directly in the entry, so we need to extract it
                        // from the folder path if it exists
                        if (!string.IsNullOrEmpty(entry.FolderPath))
                        {
                            var pathParts = entry.FolderPath.Split(Path.DirectorySeparatorChar);
                            foreach (var part in pathParts)
                            {
                                if (uint.TryParse(part, out uint potentialAppId))
                                {
                                    appId = potentialAppId;
                                    break;
                                }
                            }
                        }
                        if (appId == 0)
                        {
                            // Skip entries where we can't determine the app ID
                            continue;
                        }
                        if (!entriesByApp.TryGetValue(appId, out var appEntries))
                        {
                            appEntries = new List<ManifestDateEntry>();
                            entriesByApp[appId] = appEntries;
                        }
                        appEntries.Add(entry);
                    }
                    // For each app, scan its directories
                    foreach (var appId in entriesByApp.Keys)
                    {
                        var folderDates = Util.GetDatesFromFolders(baseDirectory, appId);
                        foreach (var entry in entriesByApp[appId])
                        {
                            // Get the branch from the entry
                            string cleanBranchName = entry.Branch.Replace('/', '_').Replace('\\', '_');
                            // Check if we found a folder for this branch
                            if (folderDates.TryGetValue(cleanBranchName, out var folderDate))
                            {
                                // Found a folder for this branch, check if the date matches
                                if (Math.Abs((folderDate - entry.CreationDate).TotalHours) < 24)
                                {
                                    // Dates are close enough, update the folder path
                                    string appFolder = Path.Combine(baseDirectory, appId.ToString());
                                    // Find the exact folder that matches
                                    var folders = Directory.GetDirectories(appFolder, $"{appId}.{cleanBranchName}.*");
                                    foreach (var folder in folders)
                                    {
                                        DateTime? folderDateTime = Util.GetDateFromFolderName(folder);
                                        if (folderDateTime.HasValue && Math.Abs((folderDateTime.Value - entry.CreationDate).TotalHours) < 24)
                                        {
                                            entry.FolderPath = folder;
                                            Logger.Debug($"Updated folder path for Depot {entry.DepotId}, Manifest {entry.ManifestId}, Branch '{entry.Branch}': {folder}");
                                            isDirty = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // Save the updated entries if changed
                    if (isDirty)
                        SaveToFile();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error updating folder paths: {ex.Message}");
                }
            }
        }
        public static void PreloadBranchDatesFromFolders(string baseDirectory, uint appId)
        {
            var folderDates = Util.GetDatesFromFolders(baseDirectory, appId);
            if (folderDates.Count > 0)
            {
                Logger.Info($"Preloaded {folderDates.Count} branch dates from existing folders for app {appId}");
                // Use a public method instead of direct access
                foreach (var kvp in folderDates)
                {
                    DepotDumper.AddUpdateBranchDate(kvp.Key, kvp.Value);
                }
            }
        }
    }
}