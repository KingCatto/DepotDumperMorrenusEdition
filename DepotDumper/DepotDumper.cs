using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
using static SteamKit2.Internal.CContentBuilder_CommitAppBuild_Request;

namespace DepotDumper
{
    class DepotDumperException : Exception
    {
        public DepotDumperException(string value) : base(value) { }
    }

    public class ConcurrentHashSet<T> : IEnumerable<T>
    {
        private readonly ConcurrentDictionary<T, byte> _dictionary = new ConcurrentDictionary<T, byte>();
        public bool Add(T item) => _dictionary.TryAdd(item, 0);
        public bool Contains(T item) => _dictionary.ContainsKey(item);
        public bool Remove(T item) => _dictionary.TryRemove(item, out _);
        public int Count => _dictionary.Count;
        public void Clear() => _dictionary.Clear();
        public IEnumerator<T> GetEnumerator() => _dictionary.Keys.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static class DepotDumper
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";
        public static DumpConfig Config = new();
        public static Steam3Session steam3;
        public const string DEFAULT_DUMP_DIR = "dumps";
        public const string CONFIG_DIR = ".DepotDumper";
        private static readonly ConcurrentDictionary<string, DateTime> branchLastModified = new ConcurrentDictionary<string, DateTime>();
        private static readonly ConcurrentHashSet<string> processedBranches = new ConcurrentHashSet<string>();
        private static int anyNewManifestsFlag = 0;

        private static bool anyNewManifests
        {
            get => Interlocked.CompareExchange(ref anyNewManifestsFlag, 0, 0) == 1;
            set => Interlocked.Exchange(ref anyNewManifestsFlag, value ? 1 : 0);
        }

        private sealed class DepotDumpInfo
        {
            public uint DepotId { get; }
            public uint AppId { get; }
            public ulong ManifestId { get; }
            public string Branch { get; }
            public string DumpDir { get; }
            public byte[] DepotKey { get; }

            public DepotDumpInfo(uint depotid, uint appId, ulong manifestId, string branch, string DumpDir, byte[] depotKey)
            {
                DepotId = depotid;
                AppId = appId;
                ManifestId = manifestId;
                Branch = branch;
                this.DumpDir = DumpDir;
                DepotKey = depotKey;
            }
        }

        static async Task<bool> AccountHasAccessAsync(uint appId, uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = [17906];
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            await steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId)) return true;
                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId)) return true;
                }
            }
            return false;
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null || !steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
                return null;

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            return appinfo.Children.FirstOrDefault(c => c.Name == section_key) ?? KeyValue.Invalid;
        }

        static async Task<(bool ShouldProcess, DateTime? LastUpdated)> ShouldProcessAppExtendedAsync(uint appId)
        {
            try
            {
                using var httpClient = HttpClientFactory.CreateHttpClient();
                var response = await httpClient.GetAsync($"https://api.steamcmd.net/v1/info/{appId}");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning($"API request failed for ShouldProcessAppExtendedAsync app {appId} with status {response.StatusCode}");
                    return (true, null);
                }

                string jsonContent = await response.Content.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(jsonContent);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("status", out var statusElement) || statusElement.GetString() != "success" ||
                    !root.TryGetProperty("data", out var dataElement) || !dataElement.TryGetProperty(appId.ToString(), out var appElement))
                {
                    Logger.Warning($"Could not parse API response structure for ShouldProcessAppExtendedAsync app {appId}");
                    return (true, null);
                }

                bool shouldProcess = true;
                if (appElement.TryGetProperty("common", out var commonElement))
                {
                    if (commonElement.TryGetProperty("type", out var typeElement))
                    {
                        string appType = typeElement.GetString()?.ToLowerInvariant();
                        Logger.Debug($"App {appId} type from API: {appType}");
                        if (appType == "demo" || appType == "dlc" || appType == "music" || appType == "video" || appType == "hardware" || appType == "mod")
                        {
                            shouldProcess = false;
                            Logger.Info($"Skipping app {appId} because its type is '{appType}'.");
                        }
                    }
                    if (shouldProcess && commonElement.TryGetProperty("freetodownload", out var freeToDownload) && freeToDownload.ValueKind == JsonValueKind.String && freeToDownload.GetString() == "1")
                    {
                        shouldProcess = false;
                        Logger.Info($"Skipping app {appId} because it appears to be free to download (freetodownload=1).");
                    }
                }

                DateTime? lastUpdated = null;
                if (appElement.TryGetProperty("depots", out var depotsElement) &&
                    depotsElement.TryGetProperty("branches", out var branchesElement) &&
                    branchesElement.TryGetProperty("public", out var publicElement) &&
                    publicElement.TryGetProperty("timeupdated", out var timeElement) &&
                    timeElement.ValueKind == JsonValueKind.String &&
                    long.TryParse(timeElement.GetString(), out long unixTime))
                {
                    lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                    Logger.Debug($"App {appId} public branch last updated: {lastUpdated}");
                }
                return (shouldProcess, lastUpdated);
            }
            catch (JsonException jsonEx) { Logger.Error($"Error parsing JSON in ShouldProcessAppExtendedAsync for {appId}: {jsonEx.Message}"); return (true, null); }
            catch (Exception ex) { Logger.Error($"Error in ShouldProcessAppExtendedAsync for {appId}: {ex.Message}"); return (true, null); }
        }

        static async Task<ulong> GetSteam3DepotManifestAsync(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null || depots == KeyValue.Invalid) return INVALID_MANIFEST_ID;

            var depotChild = depots[depotId.ToString()];
            if (depotChild == KeyValue.Invalid) return INVALID_MANIFEST_ID;

            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId) { Logger.Warning($"App {appId}, Depot {depotId} has recursive depotfromapp!"); return INVALID_MANIFEST_ID; }
                await steam3.RequestAppInfo(otherAppId);
                return await GetSteam3DepotManifestAsync(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests == KeyValue.Invalid && manifests_encrypted == KeyValue.Invalid) return INVALID_MANIFEST_ID;

            KeyValue node = KeyValue.Invalid;
            if (manifests != KeyValue.Invalid) node = manifests[branch];

            if (node != KeyValue.Invalid && node["gid"] != KeyValue.Invalid)
            {
                return node["gid"].AsUnsignedLong();
            }

            if (manifests_encrypted != KeyValue.Invalid)
            {
                var encryptedNode = manifests_encrypted[branch];
                if (encryptedNode != KeyValue.Invalid && encryptedNode["gid"] != KeyValue.Invalid)
                {
                    string password = "";
                    while (string.IsNullOrEmpty(password))
                    {
                        Console.Write($"Password required for branch '{branch}' of depot {depotId}: ");
                        password = Console.ReadLine();
                    }
                    try
                    {
                        await steam3.CheckAppBetaPassword(appId, password);
                        if (!steam3.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword)) { Logger.Error($"Password was invalid for branch '{branch}'"); return INVALID_MANIFEST_ID; }
                        var input = Util.DecodeHexString(encryptedNode["gid"].Value);
                        byte[] manifest_bytes = Util.SymmetricDecryptECB(input, appBetaPassword);
                        return BitConverter.ToUInt64(manifest_bytes, 0);
                    }
                    catch (Exception e) { Logger.Error($"Failed to decrypt manifest for branch '{branch}': {e.Message}"); return INVALID_MANIFEST_ID; }
                }
            }
            Logger.Debug($"No manifest GID found for depot {depotId}, branch '{branch}'");
            return INVALID_MANIFEST_ID;
        }

        static async Task<List<(ulong manifestId, string branch)>> GetManifestsToDumpAsync(uint depotId, uint appId)
        {
            var results = new List<(ulong manifestId, string branch)>();
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null || depots == KeyValue.Invalid) return results;

            var depotChild = depots[depotId.ToString()];
            if (depotChild == KeyValue.Invalid) return results;

            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId) { Logger.Warning($"Recursive depotfromapp for {depotId}"); return results; }
                await steam3.RequestAppInfo(otherAppId);
                return await GetManifestsToDumpAsync(depotId, otherAppId);
            }

            var manifests = depotChild["manifests"];
            var manifestsEncrypted = depotChild["encryptedmanifests"];

            if (manifests != KeyValue.Invalid)
            {
                foreach (var branchNode in manifests.Children)
                {
                    var branch = branchNode.Name;
                    if (branchNode["gid"] != KeyValue.Invalid)
                    {
                        ulong manifestId = branchNode["gid"].AsUnsignedLong();
                        if (manifestId != INVALID_MANIFEST_ID) { results.Add((manifestId, branch)); Logger.Debug($"Found manifest {manifestId} for depot {depotId} in branch '{branch}'"); }
                    }
                }
            }

            if (manifestsEncrypted != KeyValue.Invalid)
            {
                 foreach (var encryptedBranch in manifestsEncrypted.Children)
                 {
                    var branch = encryptedBranch.Name;
                    if (encryptedBranch["gid"] != KeyValue.Invalid)
                    {
                         Logger.Info($"Found encrypted manifest for depot {depotId} in branch '{branch}'");
                         Console.Write($"Attempt to get encrypted manifest from branch '{branch}'? (y/n): ");
                         var response = Console.ReadLine()?.Trim().ToLowerInvariant();
                         if (response == "y" || response == "yes")
                         {
                            ulong encryptedManifestId = await GetSteam3DepotManifestAsync(depotId, appId, branch);
                            if (encryptedManifestId != INVALID_MANIFEST_ID) { results.Add((encryptedManifestId, branch)); Logger.Debug($"Added encrypted manifest {encryptedManifestId} for branch '{branch}'"); }
                         }
                    }
                }
            }
            return results;
        }

        static void CreateZipArchive(string sourceDirectory, string zipFilePath)
        {
            try
            {
                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);

                if (Directory.Exists(sourceDirectory) && Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
                {
                    ZipFile.CreateFromDirectory(sourceDirectory, zipFilePath, CompressionLevel.Optimal, false);
                    Console.WriteLine($"Created zip archive: {Path.GetFileName(zipFilePath)}");
                    Logger.Info($"Created zip archive: {zipFilePath}");
                }
                else
                {
                    Console.WriteLine($"Skipping zip creation for {Path.GetFileName(zipFilePath)} - no files found in source.");
                    Logger.Info($"Skipping zip creation for {zipFilePath} - source directory missing or empty.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating zip archive {Path.GetFileName(zipFilePath)}: {ex.Message}");
                Logger.Error($"Error creating zip archive {zipFilePath}: {ex.ToString()}");
            }
        }

        static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            try
            {
                DirectoryInfo dir = new DirectoryInfo(sourceDirName);
                if (!dir.Exists) throw new DirectoryNotFoundException($"Source directory not found: {sourceDirName}");

                Directory.CreateDirectory(destDirName);

                foreach (FileInfo file in dir.GetFiles())
                {
                    try { string tempPath = Path.Combine(destDirName, file.Name); file.CopyTo(tempPath, true); }
                    catch (IOException ioEx) { Logger.Warning($"Error copying file {file.Name}: {ioEx.Message}"); }
                }

                if (copySubDirs)
                {
                    foreach (DirectoryInfo subdir in dir.GetDirectories())
                    {
                        string tempPath = Path.Combine(destDirName, subdir.Name);
                        DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Error during directory copy from '{sourceDirName}' to '{destDirName}': {ex.ToString()}"); }
        }

        public static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            return info?["name"]?.Value ?? string.Empty;
        }

        static void CleanupOldZipsAndFolders(string path, uint appId, string branchName, string newFolderName)
        {
            try
            {
                string appFolder = Path.Combine(path, appId.ToString());
                if (!Directory.Exists(appFolder)) return;

                string searchPattern = $"{appId}.{branchName}.*";
                var oldFolders = Directory.EnumerateDirectories(appFolder, searchPattern)
                    .Where(folder => !Path.GetFileName(folder).Equals(newFolderName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (oldFolders.Count > 0)
                {
                    Logger.Info($"Found {oldFolders.Count} older version folders for app {appId}, branch '{branchName}' to clean up.");
                    foreach (var oldFolder in oldFolders)
                    {
                        try
                        {
                            Logger.Info($"Removing old folder: {Path.GetFileName(oldFolder)}");
                            Directory.Delete(oldFolder, true);
                        }
                        catch (Exception ex) { Logger.Error($"Error removing old folder {Path.GetFileName(oldFolder)}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Error during cleanup of old folders/zips for {appId}/{branchName}: {ex.ToString()}"); }
        }

        static void CleanupEmptyDirectories(string basePath)
        {
            try
            {
                Logger.Info("Cleaning up empty directories...");
                foreach (string dir in Directory.EnumerateDirectories(basePath))
                {
                    if (Path.GetFileName(dir).Equals(CONFIG_DIR, StringComparison.OrdinalIgnoreCase)) continue;
                    CleanupEmptySubdirectories(dir);
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); Logger.Info($"Deleted empty directory: {dir}"); }
                        catch (Exception ex) { Logger.Warning($"Could not delete directory {dir}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Error during directory cleanup: {ex.ToString()}"); }
        }

        static void CleanupEmptySubdirectories(string directory)
        {
            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(directory))
                {
                    CleanupEmptySubdirectories(subDir);
                    if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        try { Directory.Delete(subDir); Logger.Debug($"Deleted empty subdirectory: {subDir}"); }
                        catch (Exception ex) { Logger.Warning($"Could not delete subdirectory {subDir}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Error cleaning subdirectories of {directory}: {ex.ToString()}"); }
        }

        static bool IsDirectoryEmpty(string path) { try { return !Directory.EnumerateFileSystemEntries(path).Any(); } catch { return false; } }

        private static async Task<Dictionary<uint, string>> GetAppDlcInfoAsync(uint appId)
        {
            var dlcAppIds = new Dictionary<uint, string>();
            Logger.Debug($"Starting GetAppDlcInfoAsync for appId: {appId} (Using SteamCMD API ONLY)");
            List<uint> discoveredDlcList = new List<uint>();

            try
            {
                using var httpClient = HttpClientFactory.CreateHttpClient();
                string baseCmdApiUrl = $"https://api.steamcmd.net/v1/info/{appId}";
                Logger.Debug($"Calling SteamCMD API for base game info: {baseCmdApiUrl}");
                var baseResponse = await httpClient.GetAsync(baseCmdApiUrl);
                Logger.Debug($"Base game SteamCMD API response status: {baseResponse.StatusCode}");

                if (!baseResponse.IsSuccessStatusCode)
                {
                    Logger.Warning($"Base game SteamCMD API request failed for app {appId} with status {baseResponse.StatusCode}");
                    return dlcAppIds;
                }

                string baseJsonContent = await baseResponse.Content.ReadAsStringAsync();
                Logger.Debug($"Base game SteamCMD API response content length: {baseJsonContent.Length}");
                using JsonDocument baseDocument = JsonDocument.Parse(baseJsonContent);

                if (!baseDocument.RootElement.TryGetProperty("status", out var baseStatusElement) || baseStatusElement.GetString() != "success")
                {
                    Logger.Warning($"Base game ({appId}) SteamCMD response status was not 'success'.");
                    return dlcAppIds;
                }

                if (!baseDocument.RootElement.TryGetProperty("data", out var baseDataElement))
                {
                    Logger.Warning($"Base game ({appId}) SteamCMD JSON response missing 'data' property.");
                    return dlcAppIds;
                }

                if (!baseDataElement.TryGetProperty(appId.ToString(), out var baseAppElement))
                {
                    Logger.Warning($"Base game ({appId}) SteamCMD JSON response missing 'data.{appId}' property.");
                    return dlcAppIds;
                }

                if (!baseAppElement.TryGetProperty("extended", out var extendedElement))
                {
                    Logger.Warning($"Base game ({appId}) SteamCMD JSON response missing 'extended' property.");
                    return dlcAppIds;
                }

                if (!extendedElement.TryGetProperty("listofdlc", out var listOfDlcElement))
                {
                    Logger.Debug($"Base game ({appId}) SteamCMD JSON response missing 'listofdlc' property in 'extended'. Game might not have DLC listed via this API.");
                }
                else if (listOfDlcElement.ValueKind != JsonValueKind.String)
                {
                    Logger.Warning($"Base game ({appId}) 'listofdlc' property is not a string: {listOfDlcElement.ValueKind}");
                }
                else
                {
                    string dlcString = listOfDlcElement.GetString();
                    Logger.Debug($"Found 'listofdlc' string: \"{dlcString}\"");
                    if (!string.IsNullOrEmpty(dlcString))
                    {
                        string[] dlcIdStrings = dlcString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (string idStr in dlcIdStrings)
                        {
                            if (uint.TryParse(idStr, out uint discoveredId))
                            {
                                discoveredDlcList.Add(discoveredId);
                                Logger.Debug($"  Parsed DLC ID {discoveredId} from listofdlc.");
                            }
                            else
                            {
                                Logger.Warning($"  Could not parse DLC ID '{idStr}' from listofdlc string.");
                            }
                        }
                    }
                    else
                    {
                        Logger.Debug("The 'listofdlc' string is empty. No DLCs listed.");
                    }
                }

                Logger.Info($"Found {discoveredDlcList.Count} potential DLCs for app {appId} via SteamCMD API's listofdlc.");

                foreach (var dlcId in discoveredDlcList)
                {
                    Logger.Debug($"Processing discovered DLC ID: {dlcId}");
                    try
                    {
                        string dlcCmdApiUrl = $"https://api.steamcmd.net/v1/info/{dlcId}";
                        Logger.Debug($"  Calling SteamCMD API for DLC details: {dlcCmdApiUrl}");
                        var dlcResponse = await httpClient.GetAsync(dlcCmdApiUrl);
                        Logger.Debug($"  DLC details SteamCMD API response status: {dlcResponse.StatusCode}");

                        if (!dlcResponse.IsSuccessStatusCode)
                        {
                            Logger.Warning($"  SteamCMD API request failed for DLC details {dlcId} with status {dlcResponse.StatusCode}");
                            continue;
                        }

                        string dlcJsonContent = await dlcResponse.Content.ReadAsStringAsync();
                        Logger.Debug($"  DLC details SteamCMD API response content length: {dlcJsonContent.Length}");
                        using JsonDocument dlcDocument = JsonDocument.Parse(dlcJsonContent);

                        if (!dlcDocument.RootElement.TryGetProperty("status", out var dlcStatusElement) || dlcStatusElement.GetString() != "success" ||
                            !dlcDocument.RootElement.TryGetProperty("data", out var dlcDataElement) || !dlcDataElement.TryGetProperty(dlcId.ToString(), out var dlcAppElement))
                        {
                            Logger.Warning($"  Failed to get valid DLC data structure for {dlcId} from SteamCMD API");
                            continue;
                        }
                        Logger.Debug($"  Successfully parsed SteamCMD API response for DLC {dlcId} details");

                        bool hasDepots = false;
                        if (dlcAppElement.TryGetProperty("depots", out var dlcDepotsElement))
                        {
                            if (dlcDepotsElement.TryGetProperty("hasdepotsindlc", out var hasDepotsInDlcElement))
                            {
                                hasDepots = hasDepotsInDlcElement.ValueKind == JsonValueKind.String && hasDepotsInDlcElement.GetString() != "0";
                                Logger.Debug($"  DLC {dlcId} 'hasdepotsindlc' property value: {hasDepotsInDlcElement.ToString()}. hasDepots = {hasDepots}");
                            }
                            else if (dlcDepotsElement.TryGetProperty("depots", out var depotsListElement) && depotsListElement.ValueKind == JsonValueKind.Object && depotsListElement.EnumerateObject().Any())
                            {
                                hasDepots = true;
                                Logger.Debug($"  DLC {dlcId} 'hasdepotsindlc' not found, but found depots list. Assuming hasDepots = true");
                            }
                            else
                            {
                                Logger.Debug($"  DLC {dlcId} 'hasdepotsindlc' not found and no depots list found. Assuming hasDepots = false");
                            }
                        }
                        else
                        {
                            Logger.Debug($"  DLC {dlcId} response has no 'depots' section. Assuming hasDepots = false");
                        }

                        string dlcName = "Unknown DLC";
                        if (dlcAppElement.TryGetProperty("common", out var dlcCommonElement) && dlcCommonElement.TryGetProperty("name", out var dlcNameElement))
                        {
                            dlcName = dlcNameElement.GetString();
                        }
                        Logger.Debug($"  DLC {dlcId} Name: '{dlcName}'");

                        if (!hasDepots)
                        {
                            Logger.Debug($"  Adding DLC {dlcId} ('{dlcName}') to list because it has NO depots.");
                            dlcAppIds.Add(dlcId, dlcName);
                        }
                        else
                        {
                            Logger.Debug($"  Skipping DLC {dlcId} ('{dlcName}') because it HAS depots.");
                        }
                    }
                    catch (JsonException jsonEx) { Logger.Error($"  Error parsing JSON for DLC {dlcId} details: {jsonEx.Message}"); }
                    catch (Exception ex) { Logger.Error($"  Unexpected error checking DLC {dlcId} details: {ex.Message}"); }
                }
            }
            catch (JsonException jsonEx) { Logger.Error($"Error parsing base game ({appId}) JSON from SteamCMD API: {jsonEx.Message}"); }
            catch (Exception ex) { Logger.Error($"Unexpected error in GetAppDlcInfoAsync (SteamCMD Only) for app {appId}: {ex.ToString()}"); }

            Logger.Debug($"Finished GetAppDlcInfoAsync (SteamCMD Only) for appId: {appId}. Returning {dlcAppIds.Count} DLCs (without depots).");
            return dlcAppIds;
        }

        private static async Task UpdateLuaFileWithDlcAsync(string filePath, uint appId, uint depotId, string depotKeyHex, ulong manifestId, Dictionary<uint, string> dlcAppIds)
        {
            try
            {
                string directoryPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                bool hasAppId = false;
                bool hasDepotKey = false;
                bool hasManifestId = false;
                string manifestLine = $"setManifestid({depotId}, \"{manifestId}\", 0)";

                List<string> updatedLines = new List<string>();
                HashSet<string> uniqueLinesTracker = new HashSet<string>(StringComparer.Ordinal);

                if (File.Exists(filePath))
                {
                    string[] existingLines = File.ReadAllLines(filePath);
                    foreach (string line in existingLines)
                    {
                        string trimmedLine = line.Trim();
                        bool isLikelyOldDlc = trimmedLine.StartsWith("addappid(") && !trimmedLine.Contains(",");

                        if (string.IsNullOrWhiteSpace(line) || (trimmedLine.StartsWith("--") && !isLikelyOldDlc))
                        {
                            updatedLines.Add(line);
                            continue;
                        }

                        if (isLikelyOldDlc)
                        {
                            Logger.Debug($"Skipping existing potential DLC line: {line}");
                            continue;
                        }

                        if (trimmedLine.Equals($"addappid({appId})"))
                        {
                            if (uniqueLinesTracker.Add(trimmedLine)) { updatedLines.Add(line); hasAppId = true; }
                            else { Logger.Debug($"Duplicate base app ID line skipped: {line}"); }
                        }
                        else if (!string.IsNullOrEmpty(depotKeyHex) && trimmedLine.StartsWith($"addappid({depotId}, 1,"))
                        {
                            string updatedDepotLine = $"addappid({depotId}, 1, \"{depotKeyHex}\")";
                            if (uniqueLinesTracker.Add(updatedDepotLine)) { updatedLines.Add(updatedDepotLine); hasDepotKey = true; }
                            else { Logger.Debug($"Duplicate depot key line skipped: {updatedDepotLine}"); }
                            hasDepotKey = true;
                        }
                        else if (trimmedLine.StartsWith($"setManifestid({depotId},"))
                        {
                            if (uniqueLinesTracker.Add(manifestLine)) { updatedLines.Add(manifestLine); hasManifestId = true; }
                            else { Logger.Debug($"Duplicate manifest line skipped: {manifestLine}"); }
                            hasManifestId = true;
                        }
                        else if (uniqueLinesTracker.Add(trimmedLine))
                        {
                            updatedLines.Add(line);
                        }
                        else
                        {
                            Logger.Debug($"Duplicate other line skipped: {line}");
                        }
                    }
                }

                if (!hasAppId)
                {
                    string lineToAdd = $"addappid({appId})";
                    if (uniqueLinesTracker.Add(lineToAdd.Trim())) { updatedLines.Insert(0, lineToAdd); Logger.Debug($"Added missing base AppID line: {lineToAdd}"); }
                }

                if (!hasDepotKey && !string.IsNullOrEmpty(depotKeyHex))
                {
                    string lineToAdd = $"addappid({depotId}, 1, \"{depotKeyHex}\")";
                    if (uniqueLinesTracker.Add(lineToAdd.Trim())) { updatedLines.Add(lineToAdd); Logger.Debug($"Added missing depot key line: {lineToAdd}"); }
                }

                if (!hasManifestId)
                {
                    string lineToAdd = manifestLine;
                    if (uniqueLinesTracker.Add(lineToAdd.Trim())) { updatedLines.Add(lineToAdd); Logger.Debug($"Added missing manifest line: {lineToAdd}"); }
                }

                File.WriteAllLines(filePath, updatedLines);
            }
            catch (IOException ioEx) { Logger.Error($"IO Error updating Lua file {filePath}: {ioEx.Message}"); }
            catch (Exception ex) { Logger.Error($"Error updating Lua file {filePath}: {ex.ToString()}"); }
            await Task.CompletedTask;
        }

        private static async Task ProcessManifestIndividuallyAsync(uint depotId, uint appId, ulong manifestId, string branch, string path, string depotKeyHex, DateTime manifestDate, Dictionary<uint, string> appDlcInfo, CDNClientPool cdnPoolInstance)
        {
            bool manifestDownloaded = false;
            bool manifestSkipped = false;
            List<string> manifestErrors = new List<string>();
            string fullManifestPath = null;

            if (cdnPoolInstance == null) { Logger.Error($"ProcessManifestIndividuallyAsync called with null cdnPoolInstance for manifest {manifestId}, depot {depotId}. Cannot proceed."); manifestErrors.Add("Internal error: CDN Pool instance was null."); return; }

            try
            {
                var cleanBranchName = branch.Replace('/', '_').Replace('\\', '_');
                var appPath = Path.Combine(path, appId.ToString());
                var branchPath = Path.Combine(appPath, cleanBranchName);
                Directory.CreateDirectory(branchPath);

                branchLastModified.AddOrUpdate(cleanBranchName, manifestDate, (key, existingDate) => manifestDate > existingDate ? manifestDate : existingDate);
                processedBranches.Add(cleanBranchName);

                var manifestFilename = $"{depotId}_{manifestId}.manifest";
                fullManifestPath = Path.Combine(branchPath, manifestFilename);

                try
                {
                    var oldManifestFiles = Directory.GetFiles(branchPath, $"{depotId}_*.manifest");
                    foreach (var oldManifest in oldManifestFiles)
                    {
                        if (!Path.GetFileName(oldManifest).Equals(manifestFilename, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Removing old manifest: {Path.GetFileName(oldManifest)} in branch '{cleanBranchName}'");
                            File.Delete(oldManifest);
                            Logger.Info($"Removing old manifest: {Path.GetFileName(oldManifest)} in branch '{cleanBranchName}'");
                        }
                    }
                }
                catch (Exception ex) { Logger.Warning($"Failed to clean up old manifests for depot {depotId} in branch '{cleanBranchName}': {ex.Message}"); }

                bool shouldDownload = !File.Exists(fullManifestPath);

                if (!shouldDownload)
                {
                    manifestSkipped = true;
                    Logger.Info($"Manifest {manifestId} exists for depot {depotId} branch '{branch}', skipping download.");
                    var branchLuaFile = Path.Combine(branchPath, $"{appId}.lua");
                    Console.WriteLine($"Updating Lua file for existing manifest {manifestId} in branch '{branch}'");
                    await UpdateLuaFileWithDlcAsync(branchLuaFile, appId, depotId, depotKeyHex?.ToLowerInvariant(), manifestId, appDlcInfo ?? new Dictionary<uint, string>());
                }
                else
                {
                    DepotManifest manifest = null;
                    try
                    {
                        Logger.Debug($"Attempting download for manifest {manifestId}, depot {depotId}, branch '{branch}'");
                        manifest = await DownloadManifestAsync(depotId, appId, manifestId, branch, cdnPoolInstance);
                    }
                    catch (Exception ex) { string errorMsg = $"Error downloading manifest {manifestId}: {ex.Message}"; manifestErrors.Add(errorMsg); Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}"); }

                    if (manifest != null)
                    {
                        try
                        {
                            manifest.SaveToFile(fullManifestPath);
                            manifestDownloaded = true;
                            Logger.Info($"Successfully saved manifest {manifestId} from branch '{branch}' to {fullManifestPath}");
                            var branchLuaFile = Path.Combine(branchPath, $"{appId}.lua");
                            await UpdateLuaFileWithDlcAsync(branchLuaFile, appId, depotId, depotKeyHex?.ToLowerInvariant(), manifestId, appDlcInfo ?? new Dictionary<uint, string>());
                            anyNewManifests = true;
                        }
                        catch (Exception ex) { string errorMsg = $"Failed to save manifest {manifestId} to file {fullManifestPath}: {ex.Message}"; manifestErrors.Add(errorMsg); Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}"); }
                    }
                    else
                    {
                        manifestErrors.Add($"Download failed (manifest null) for {manifestId}");
                        Logger.Error($"DownloadManifestAsync returned null for manifest {manifestId}, depot {depotId}, branch '{branch}'.");
                    }
                }
            }
            catch (Exception ex) { string errorMsg = $"Outer error processing manifest {manifestId}: {ex.Message}"; manifestErrors.Add(errorMsg); Console.WriteLine($"Error processing manifest: {errorMsg}"); Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}"); }
            finally { }
        }

        private static async Task<DepotManifest> DownloadManifestAsync(uint depotId, uint appId, ulong manifestId, string branch, CDNClientPool cdnPoolInstance)
        {
            if (cdnPoolInstance == null) { Logger.Error($"DownloadManifestAsync called with null cdnPoolInstance for manifest {manifestId}, depot {depotId}. Cannot download."); return null; }
            var cts = new CancellationTokenSource(); cts.CancelAfter(TimeSpan.FromMinutes(5)); Server connection = null;
            try
            {
                connection = cdnPoolInstance.GetConnection(cts.Token); if (connection == null) { Console.WriteLine($"Failed to get connection for manifest {manifestId}"); Logger.Warning($"Failed to get CDN connection for manifest {manifestId}"); return null; }
                ulong manifestRequestCode = 0; var manifestRequestCodeExpiration = DateTime.MinValue; int retryCount = 0; const int maxRetries = 3; TimeSpan[] backoffDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };
                while (retryCount < maxRetries)
                {
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested(); string cdnToken = null; if (steam3 != null && steam3.CDNAuthTokens.TryGetValue((depotId, connection.Host), out var authTokenCallbackPromise)) { var result = await authTokenCallbackPromise.Task; cdnToken = result.Token; }
                        var now = DateTime.Now; if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration) { if (steam3 == null) { Logger.Error("DownloadManifestAsync: steam3 session is null. Cannot get manifest request code."); throw new InvalidOperationException("Steam3 session is not initialized."); } manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(depotId, appId, manifestId, branch); manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5)); if (manifestRequestCode == 0) { Logger.Warning($"Failed to get manifest request code for {manifestId}"); break; } }
                        Console.WriteLine("Downloading manifest {0} from {1} with {2}", manifestId, connection, cdnPoolInstance.ProxyServer != null ? cdnPoolInstance.ProxyServer : "no proxy"); if (steam3 == null || !steam3.DepotKeys.TryGetValue(depotId, out var depotKey) || depotKey == null) { Logger.Warning($"No depot key available for depot {depotId} in DownloadManifestAsync"); return null; }
                        if (cdnPoolInstance.CDNClient == null) { Logger.Error("DownloadManifestAsync: cdnPoolInstance.CDNClient is null. Cannot download manifest."); throw new InvalidOperationException("CDN Client is not initialized within the provided CDN Pool."); }
                        return await cdnPoolInstance.CDNClient.DownloadManifestAsync(depotId, manifestId, manifestRequestCode, connection, depotKey, cdnPoolInstance.ProxyServer, cdnToken);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        if (e.StatusCode == HttpStatusCode.Forbidden && steam3 != null && !steam3.CDNAuthTokens.ContainsKey((depotId, connection.Host))) { await steam3.RequestCDNAuthToken(appId, depotId, connection); cdnPoolInstance.ReturnConnection(connection); connection = cdnPoolInstance.GetConnection(cts.Token); if (connection == null) { Logger.Warning("Could not re-establish CDN connection after auth token request."); break; } continue; }
                        retryCount++; if (retryCount >= maxRetries) { Logger.Error($"Error downloading manifest {manifestId} after {maxRetries} retries: {e.Message}"); break; } Logger.Warning($"Download attempt {retryCount}/{maxRetries} failed: {e.Message}. Retrying after delay..."); await Task.Delay(backoffDelays[retryCount - 1]);
                    }
                    catch (OperationCanceledException) { Logger.Warning($"Operation canceled downloading manifest {manifestId}."); break; }
                    catch (Exception e) { retryCount++; if (retryCount >= maxRetries) { Logger.Error($"Error downloading manifest {manifestId} after {maxRetries} retries: {e.ToString()}"); break; } Logger.Warning($"Retrying download {retryCount}/{maxRetries} due to error: {e.Message}. Retrying after delay..."); await Task.Delay(backoffDelays[retryCount - 1]); }
                }
                return null;
            }
            finally { if (connection != null) cdnPoolInstance.ReturnConnection(connection); cts.Dispose(); }
        }

        static async Task DumpDepotAsync(uint depotId, uint appId, string path, Dictionary<(uint depotId, string branch), DateTime> manifestDates, DateTime? appLastUpdated = null, Dictionary<uint, string> appDlcInfo = null)
        {
            appDlcInfo ??= new Dictionary<uint, string>(); CDNClientPool currentCdnPool = null;
            try
            {
                currentCdnPool = new CDNClientPool(steam3, appId);
                await steam3.RequestDepotKey(depotId, appId);
                if (!steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
                {
                    Console.WriteLine("No valid depot key for {0} (App Context: {1}).", depotId, appId);
                    Logger.Warning($"No valid depot key for Depot {depotId} (App Context: {appId}).");
                    return;
                }
                var manifestsToDump = await GetManifestsToDumpAsync(depotId, appId);
                Logger.Info($"Starting to process depot {depotId} for app context {appId}");
                Console.WriteLine($"Starting to process depot {depotId} for app context {appId}");
                string depotKeyHex = string.Concat(depotKey.Select(b => b.ToString("X2")).ToArray());
                string appDumpPath = Path.Combine(path, appId.ToString());
                Directory.CreateDirectory(appDumpPath);
                var keyFilePath = Path.Combine(appDumpPath, $"{appId.ToString()}.key");
                bool keyExists = false;
                if (File.Exists(keyFilePath)) { string linePrefix = $"{depotId};"; try { keyExists = File.ReadLines(keyFilePath).Any(line => line.TrimStart().StartsWith(linePrefix)); } catch (IOException ex) { Logger.Warning($"Could not read key file {keyFilePath}: {ex.Message}"); } }
                if (!keyExists) { try { File.AppendAllText(keyFilePath, $"{depotId};{depotKeyHex}\n"); Console.WriteLine("Depot {0} key: {1}", depotId, depotKeyHex); Logger.Info($"Appended key for Depot {depotId} to {keyFilePath}"); } catch (IOException ex) { Logger.Error($"Could not write to key file {keyFilePath}: {ex.Message}"); } }
                else { Console.WriteLine("Using existing depot {0} key", depotId); Logger.Debug($"Key for Depot {depotId} already exists in {keyFilePath}"); }
                if (manifestsToDump.Count == 0)
                {
                    Console.WriteLine("No accessible manifests found for depot {0}.", depotId);
                    Logger.Warning($"No accessible manifests found for depot {depotId} (App Context: {appId}).");
                    return;
                }
                DateTime defaultDate = appLastUpdated ?? DateTime.Now;
                int maxConcurrent = Config.MaxDownloads > 0 ? Config.MaxDownloads : 4;
                Console.WriteLine($"Processing {manifestsToDump.Count} manifests for depot {depotId} with up to {maxConcurrent} at a time");
                Logger.Info($"Processing {manifestsToDump.Count} manifests for depot {depotId} with up to {maxConcurrent} at a time");
                var tasks = new List<Task>();
                using var concurrencySemaphore = new SemaphoreSlim(maxConcurrent);
                foreach (var (manifestId, branch) in manifestsToDump)
                {
                    DateTime manifestDate = defaultDate;
                    if (manifestDates != null && manifestDates.TryGetValue((depotId, branch), out DateTime specificDate)) { manifestDate = specificDate; }
                    await concurrencySemaphore.WaitAsync();
                    tasks.Add(Task.Run(async () => { try { await ProcessManifestIndividuallyAsync(depotId, appId, manifestId, branch, path, depotKeyHex, manifestDate, appDlcInfo, currentCdnPool); } finally { concurrencySemaphore.Release(); } }));
                }
                await Task.WhenAll(tasks);
                Console.WriteLine($"Completed processing all manifests for depot {depotId}");
                Logger.Info($"Completed processing all manifests for depot {depotId}");
            }
            catch (Exception e) { Console.WriteLine($"Error dumping depot {depotId}: {e.Message}"); Logger.Error($"Error dumping depot {depotId}: {e.ToString()}"); }
            finally { currentCdnPool?.Shutdown(); }
        }

        public static async Task DumpAppAsync(bool select, uint specificAppId = INVALID_APP_ID)
        {
            var dumpPath = string.IsNullOrWhiteSpace(Config.DumpDirectory) ? DEFAULT_DUMP_DIR : Config.DumpDirectory;
            Directory.CreateDirectory(Path.Combine(dumpPath, CONFIG_DIR));
            Console.WriteLine("Getting licenses...");
            if (specificAppId == INVALID_APP_ID && steam3.Licenses == null)
            {
                Console.WriteLine("Licenses not loaded, waiting...");
                if (steam3.Licenses == null)
                {
                    Console.WriteLine("Licenses could not be loaded. Cannot process all apps.");
                    Logger.Error("Licenses could not be loaded. Cannot process all apps.");
                    return;
                }
            }

            if (specificAppId != INVALID_APP_ID)
            {
                branchLastModified.Clear();
                processedBranches.Clear();
                anyNewManifests = false;
                Dictionary<uint, string> appDlcInfo = null;
                string parentAppName = "Unknown_Parent_App";
                uint parentAppId = specificAppId;
                try
                {
                    await steam3?.RequestAppInfo(specificAppId);
                    var appName = GetAppName(specificAppId);
                    if (string.IsNullOrEmpty(appName)) appName = "Unknown_App";
                    var (isDlc, resolvedParentAppId) = await DlcDetection.DetectDlcAndParentAsync(steam3, specificAppId);
                    parentAppId = resolvedParentAppId;
                    parentAppName = appName;
                    if (isDlc)
                    {
                        Console.WriteLine($"App {specificAppId} is a DLC for app {parentAppId}, using parent app ID {parentAppId} for storage.");
                        await steam3?.RequestAppInfo(parentAppId);
                        parentAppName = GetAppName(parentAppId);
                        if (string.IsNullOrEmpty(parentAppName)) parentAppName = "Unknown_Parent_App";
                    }
                    else { parentAppName = appName; }
                    Logger.Debug($"Fetching DLC info for parent app {parentAppId} ('{parentAppName}') once.");
                    appDlcInfo = await GetAppDlcInfoAsync(parentAppId);
                    Logger.Debug($"Fetched {appDlcInfo.Count} DLCs (without depots) for parent app {parentAppId}.");
                    Directory.CreateDirectory(Path.Combine(dumpPath, parentAppId.ToString()));
                    var depots = GetSteam3AppSection(specificAppId, EAppInfoSection.Depots);
                    if (depots == null || depots == KeyValue.Invalid) { Console.WriteLine("No depots section found for app {0}", specificAppId); Logger.Warning($"No depots section found for app {specificAppId}"); return; }
                    var (shouldProcess, _) = await ShouldProcessAppExtendedAsync(specificAppId);
                    if (!shouldProcess) { Console.WriteLine("Skipping app {0}: {1} (detected as non-processed type)", specificAppId, appName); Logger.Info($"Skipping app {specificAppId}: {appName} (detected as non-processed type)"); return; }
                    DateTime? appLastUpdated = null;
                    var depotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();
                    if (appLastUpdated == null) Console.WriteLine($"Dumping app {specificAppId}: {appName}"); else Console.WriteLine($"Dumping app {specificAppId}: {appName} (Last updated: {appLastUpdated.Value:yyyy-MM-dd HH:mm:ss})");
                    string infoFilePath = Path.Combine(dumpPath, parentAppId.ToString(), $"{specificAppId.ToString()}.{(isDlc ? "dlcinfo" : "info")}");
                    File.WriteAllText(infoFilePath, $"{specificAppId};{appName}{(isDlc ? $";DLC_For_{parentAppId}" : "")}");
                    foreach (var depotSection in depots.Children)
                    {
                        if (!uint.TryParse(depotSection.Name, out uint id) || id == uint.MaxValue || depotSection.Name == "branches" || depotSection.Children.Count == 0) continue;
                        try { if (!await AccountHasAccessAsync(specificAppId, id)) { Console.WriteLine($"No access to depot {id}, skipping"); continue; } } catch (Exception accessEx) { Console.WriteLine($"Error checking access for depot {id}: {accessEx.Message}"); continue; }
                        if (select) { Console.WriteLine($"Dump depot {depotSection.Name}? (Press N to skip/any other key to continue)"); if (Console.ReadKey().Key.ToString().Equals("N", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("\nSkipped."); continue; } Console.WriteLine("\n"); }
                        var currentDepotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();
                        await DumpDepotAsync(id, parentAppId, dumpPath, currentDepotManifestDates, appLastUpdated, appDlcInfo);
                    }
                    if (appDlcInfo != null && appDlcInfo.Count > 0 && processedBranches.Count > 0)
                    {
                        Logger.Info($"Appending {appDlcInfo.Count} DLC entries to Lua files for parent app {parentAppId} across {processedBranches.Count} processed branches.");
                        var dlcLinesToAdd = new List<string>();
                        dlcLinesToAdd.Add("");
                        dlcLinesToAdd.Add("-- Discovered DLCs (without depots)");
                        foreach (var dlcEntry in appDlcInfo.OrderBy(kvp => kvp.Key)) { dlcLinesToAdd.Add($"addappid({dlcEntry.Key}) -- {dlcEntry.Value}"); }
                        foreach (var branchName in processedBranches)
                        {
                            string luaFilePath = Path.Combine(dumpPath, parentAppId.ToString(), branchName, $"{parentAppId}.lua");
                            Logger.Debug($"Checking Lua file for final DLC append: {luaFilePath}");
                            if (File.Exists(luaFilePath))
                            {
                                try
                                {
                                    List<string> existingLines = File.ReadAllLines(luaFilePath).ToList();
                                    var dlcLinePrefixes = new HashSet<string>(appDlcInfo.Keys.Select(k => $"addappid({k})"));
                                    List<string> linesToWrite = existingLines.Where(line => !dlcLinePrefixes.Any(prefix => line.TrimStart().StartsWith(prefix))).ToList();
                                    linesToWrite.AddRange(dlcLinesToAdd);
                                    File.WriteAllLines(luaFilePath, linesToWrite);
                                    Logger.Info($"Successfully appended {appDlcInfo.Count} DLC entries to {Path.GetFileName(luaFilePath)} in branch '{branchName}'.");
                                }
                                catch (IOException ioEx) { Logger.Error($"IO Error finalizing DLC entries in Lua file {luaFilePath}: {ioEx.Message}"); }
                                catch (Exception ex) { Logger.Error($"Error finalizing DLC entries in Lua file {luaFilePath}: {ex.ToString()}"); }
                            }
                            else { Logger.Warning($"Lua file not found for final DLC append: {luaFilePath}. Skipping append for this branch."); }
                        }
                    }
                    else
                    {
                        if (appDlcInfo == null || appDlcInfo.Count == 0) Logger.Info($"No DLCs found or fetched for parent app {parentAppId}, skipping final Lua append step.");
                        if (processedBranches.Count == 0) Logger.Info($"No branches were processed for parent app {parentAppId}, skipping final Lua append step.");
                    }
                    CreateZipsForApp(parentAppId, dumpPath, parentAppName);
                    CleanupEmptyDirectories(dumpPath);
                }
                catch (Exception e) { Console.WriteLine("Error processing specific app {0}: {1}", specificAppId, e.Message); Logger.Error($"Error processing specific app {specificAppId}: {e.ToString()}"); }
                return;
            }

            if (steam3.Licenses == null) { return; }
            var licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            await steam3.RequestPackageInfo(licenseQuery);
            var processedParentApps = new HashSet<uint>();
            var allOwnedAppIds = new HashSet<uint>();
            foreach (var license in licenseQuery.Where(l => l != 0)) { if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null) { foreach (var appIdKV in package.KeyValues["appids"].Children) { allOwnedAppIds.Add(appIdKV.AsUnsignedInteger()); } } }
            Logger.Info($"Found {allOwnedAppIds.Count} unique owned AppIDs across all licenses.");

            foreach (uint currentAppId in allOwnedAppIds)
            {
                string parentAppName = "Unknown_Parent_App";
                uint parentAppId = currentAppId;
                Dictionary<uint, string> appDlcInfo = null;
                bool processedThisParentGroup = false;
                try
                {
                    var (isDlc, resolvedParentAppId) = await DlcDetection.DetectDlcAndParentAsync(steam3, currentAppId);
                    parentAppId = resolvedParentAppId;
                    if (processedParentApps.Contains(parentAppId)) { Logger.Debug($"Parent app {parentAppId} already fully processed, skipping check for related app {currentAppId}."); continue; }
                    branchLastModified.Clear();
                    processedBranches.Clear();
                    anyNewManifests = false;
                    if (parentAppId != currentAppId) await steam3?.RequestAppInfo(parentAppId);
                    parentAppName = GetAppName(parentAppId);
                    if (string.IsNullOrEmpty(parentAppName)) parentAppName = "Unknown_Parent_App";
                    Logger.Info($"--- Processing Parent Group Start: {parentAppId} ('{parentAppName}') ---");
                    processedParentApps.Add(parentAppId);
                    Directory.CreateDirectory(Path.Combine(dumpPath, parentAppId.ToString()));
                    Logger.Debug($"Fetching DLC info for parent app {parentAppId} ('{parentAppName}') once.");
                    appDlcInfo = await GetAppDlcInfoAsync(parentAppId);
                    Logger.Debug($"Fetched {appDlcInfo.Count} DLCs (without depots) for parent app {parentAppId}.");
                    processedThisParentGroup = true;
                    foreach (uint appToCheck in allOwnedAppIds)
                    {
                        var (appIsDlc, appParentId) = await DlcDetection.DetectDlcAndParentAsync(steam3, appToCheck);
                        if (appParentId == parentAppId)
                        {
                            await ProcessSingleAppWithinGroup(appToCheck, parentAppId, select, dumpPath, appDlcInfo);
                        }
                    }
                    if (processedThisParentGroup)
                    {
                        if (appDlcInfo != null && appDlcInfo.Count > 0 && processedBranches.Count > 0)
                        {
                            Logger.Info($"Appending {appDlcInfo.Count} DLC entries to Lua files for parent app {parentAppId} across {processedBranches.Count} processed branches.");
                            var dlcLinesToAdd = new List<string>();
                            dlcLinesToAdd.Add("");
                            dlcLinesToAdd.Add("-- Discovered DLCs (without depots)");
                            foreach (var dlcEntry in appDlcInfo.OrderBy(kvp => kvp.Key)) { dlcLinesToAdd.Add($"addappid({dlcEntry.Key}) -- {dlcEntry.Value}"); }
                            foreach (var branchName in processedBranches)
                            {
                                string luaFilePath = Path.Combine(dumpPath, parentAppId.ToString(), branchName, $"{parentAppId}.lua");
                                Logger.Debug($"Checking Lua file for final DLC append: {luaFilePath}");
                                if (File.Exists(luaFilePath))
                                {
                                    try
                                    {
                                        List<string> existingLines = File.ReadAllLines(luaFilePath).ToList();
                                        var dlcLinePrefixes = new HashSet<string>(appDlcInfo.Keys.Select(k => $"addappid({k})"));
                                        List<string> linesToWrite = existingLines.Where(line => !dlcLinePrefixes.Any(prefix => line.TrimStart().StartsWith(prefix))).ToList();
                                        linesToWrite.AddRange(dlcLinesToAdd);
                                        File.WriteAllLines(luaFilePath, linesToWrite);
                                        Logger.Info($"Successfully appended {appDlcInfo.Count} DLC entries to {Path.GetFileName(luaFilePath)} in branch '{branchName}'.");
                                    }
                                    catch (IOException ioEx) { Logger.Error($"IO Error finalizing DLC entries in Lua file {luaFilePath}: {ioEx.Message}"); }
                                    catch (Exception ex) { Logger.Error($"Error finalizing DLC entries in Lua file {luaFilePath}: {ex.ToString()}"); }
                                }
                                else { Logger.Warning($"Lua file not found for final DLC append: {luaFilePath}. Skipping append for this branch."); }
                            }
                        }
                        else
                        {
                            if (appDlcInfo == null || appDlcInfo.Count == 0) Logger.Info($"No DLCs found or fetched for parent app {parentAppId}, skipping final Lua append step.");
                            if (processedBranches.Count == 0) Logger.Info($"No branches were processed for parent app {parentAppId}, skipping final Lua append step.");
                        }
                        CreateZipsForApp(parentAppId, dumpPath, parentAppName);
                        Logger.Info($"--- Processing Parent Group End: {parentAppId} ---");
                    }
                }
                catch (Exception e) { Console.WriteLine("Error processing app {0} (Parent Context {1}): {2}", currentAppId, parentAppId, e.Message); Logger.Error($"Error processing app {currentAppId} (Parent Context {parentAppId}): {e.ToString()}"); }
            }
            CleanupEmptyDirectories(dumpPath);
        }

        private static async Task ProcessSingleAppWithinGroup(uint currentAppId, uint parentAppId, bool select, string dumpPath, Dictionary<uint, string> appDlcInfo)
        {
            await steam3?.RequestAppInfo(currentAppId);
            var appName = GetAppName(currentAppId);
            if (string.IsNullOrEmpty(appName)) appName = "Unknown_App";
            var (isDlc, _) = await DlcDetection.DetectDlcAndParentAsync(steam3, currentAppId);
            var (shouldProcessCurrent, _) = await ShouldProcessAppExtendedAsync(currentAppId);
            if (!shouldProcessCurrent)
            {
                Console.WriteLine("Skipping app {0}: {1} (detected as non-processed type)", currentAppId, appName);
                Logger.Info($"Skipping app {currentAppId}: {appName} (detected as non-processed type)");
                return;
            }
            var depots = GetSteam3AppSection(currentAppId, EAppInfoSection.Depots);
            if (depots == null || depots == KeyValue.Invalid) { Logger.Warning($"No depots section found for app {currentAppId} within group {parentAppId}, skipping its depots."); return; }
            DateTime? appLastUpdated = null;
            Console.WriteLine($"Dumping app {currentAppId}: {appName} (Parent Context: {parentAppId})");
            string infoFilePath = Path.Combine(dumpPath, parentAppId.ToString(), $"{currentAppId.ToString()}.{(isDlc ? "dlcinfo" : "info")}");
            File.WriteAllText(infoFilePath, $"{currentAppId};{appName}{(isDlc ? $";DLC_For_{parentAppId}" : "")}");
            foreach (var depotSection in depots.Children)
            {
                if (!uint.TryParse(depotSection.Name, out uint id) || id == uint.MaxValue || depotSection.Name == "branches" || depotSection.Children.Count == 0) continue;
                try { if (!await AccountHasAccessAsync(currentAppId, id)) { continue; } } catch (Exception) { continue; }
                if (select) { Console.WriteLine($"Dump depot {depotSection.Name} for app {currentAppId}? (Press N to skip/any other key to continue)"); if (Console.ReadKey().Key.ToString().Equals("N", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("\nSkipped."); continue; } Console.WriteLine("\n"); }
                var currentDepotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();
                await DumpDepotAsync(id, parentAppId, dumpPath, currentDepotManifestDates, appLastUpdated, appDlcInfo);
            }
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;
            if (username != null && Config.RememberPassword)
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);

            steam3 = new Steam3Session(new SteamUser.LogOnDetails { Username = username, Password = loginToken == null ? password : null, ShouldRememberPassword = Config.RememberPassword, AccessToken = loginToken, LoginID = Config.LoginID ?? 0x534B32, });

            if (!steam3.WaitForCredentials())
            {
                Console.WriteLine("Unable to get steam3 credentials.");
                Logger.Error("Unable to get steam3 credentials.");
                return false;
            }

            Logger.Info("Steam3 credentials obtained successfully.");
            Task.Run(steam3.TickCallbacks);
            return true;
        }

        public static void ShutdownSteam3()
        {
            if (steam3 == null) return;
            Logger.Info("Disconnecting from Steam3...");
            steam3.Disconnect();
            steam3 = null;
            Logger.Info("Steam3 disconnected.");
        }

        static void CreateZipsForApp(uint appId, string path, string appName)
        {
            if (processedBranches.Count == 0)
            {
                Console.WriteLine($"No branches processed for app {appId} ({appName}), skipping zip creation.");
                Logger.Info($"No branches processed for app {appId} ('{appName}'), skipping zip creation.");
                return;
            }
            Console.WriteLine($"Creating zip archives for {processedBranches.Count} branches of app {appId} ('{appName}')");
            Logger.Info($"Creating zip archives for {processedBranches.Count} branches of app {appId} ('{appName}')");

            string safeAppName = string.Join("_", appName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safeAppName.Length > 50) safeAppName = safeAppName.Substring(0, 50);
            safeAppName = safeAppName.Replace("__", "_").Trim('_');
            if (string.IsNullOrEmpty(safeAppName)) safeAppName = "Unknown_App";


            foreach (var branchName in processedBranches)
            {
                try
                {
                    string appPath = Path.Combine(path, appId.ToString());
                    string branchSourcePath = Path.Combine(appPath, branchName);
                    if (!Directory.Exists(branchSourcePath)) { Logger.Warning($"Branch source directory not found for zipping: {branchSourcePath}"); continue; }

                    DateTime branchDate = DateTime.Now;
                    if (branchLastModified.TryGetValue(branchName, out var date)) branchDate = date;
                    string dateTimeStr = branchDate.ToString("yyyy-MM-dd_HH-mm-ss");

                    string folderName = $"{appId}.{branchName}.{dateTimeStr}.{safeAppName}";
                    string dateBranchFolder = Path.Combine(appPath, folderName);
                    string zipFilePath = Path.Combine(dateBranchFolder, $"{appId}.zip");

                    if (anyNewManifests) { CleanupOldZipsAndFolders(path, appId, branchName, folderName); }
                    if (!Directory.Exists(dateBranchFolder)) { Directory.CreateDirectory(dateBranchFolder); }

                    if (File.Exists(zipFilePath) && !anyNewManifests) { Logger.Info($"Zip archive already exists and no new manifests downloaded for branch '{branchName}', skipping: {zipFilePath}"); continue; }
                    if (File.Exists(zipFilePath)) { Logger.Info($"Updating zip archive for branch '{branchName}' due to new manifests."); File.Delete(zipFilePath); }

                    string tempDir = Path.Combine(path, $"temp_zip_{appId}_{Guid.NewGuid().ToString().Substring(0, 8)}");
                    if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch (Exception ex) { Logger.Warning($"Error cleaning up temp zip directory {tempDir}: {ex.Message}"); } }
                    Directory.CreateDirectory(tempDir);

                    int manifestsIncluded = 0;
                    int luaFilesIncluded = 0;
                    int keyFilesIncluded = 0;
                    int infoFilesIncluded = 0;

                    foreach (var file in Directory.EnumerateFiles(branchSourcePath))
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".manifest") || fileName.EndsWith(".lua") || fileName.EndsWith(".key") || fileName.EndsWith(".info") || fileName.EndsWith(".dlcinfo"))
                        {
                            try
                            {
                                File.Copy(file, Path.Combine(tempDir, fileName), true);
                                if (fileName.EndsWith(".manifest")) manifestsIncluded++;
                                else if (fileName.EndsWith(".lua")) luaFilesIncluded++;
                                else if (fileName.EndsWith(".key")) keyFilesIncluded++;
                                else if (fileName.EndsWith(".info") || fileName.EndsWith(".dlcinfo")) infoFilesIncluded++;
                            } catch (IOException ex) { Logger.Warning($"Failed to copy file {fileName} to temp zip dir: {ex.Message}"); continue; }
                        }
                    }

                    if (Directory.EnumerateFileSystemEntries(tempDir).Any())
                    {
                        Logger.Info($"Creating zip for branch '{branchName}' with {manifestsIncluded} manifests, {luaFilesIncluded} lua, {keyFilesIncluded} key, {infoFilesIncluded} info files at {zipFilePath}");
                        CreateZipArchive(tempDir, zipFilePath);
                    }
                    else
                    {
                        Logger.Info($"No files found to include in zip for branch '{branchName}' of app {appId}");
                    }

                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { Logger.Warning($"Failed to delete temp zip directory: {tempDir}"); }
                    }

                }
                catch (Exception ex) { Logger.Error($"Error creating zip for branch '{branchName}' of app {appId}: {ex.ToString()}"); }
            }
        }

    }
}
//comment goes here