using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;
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
        // Add this public method to your DepotDumper.cs file
        public static void AddUpdateBranchDate(string branch, DateTime date)
        {
            branchLastModified.AddOrUpdate(branch, date, (key, existingDate) =>
            {
                // Keep existing date from tracking system
                return existingDate;
            });
        }
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

            // Check for packages that contain either the depot directly or the app if checking app ownership
            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    // If checking app access directly and the app is in this package
                    if (appId == depotId && package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == appId))
                    {
                        Logger.Debug($"Account has access to app {appId} through package {license}");
                        return true;
                    }

                    // Check for depot access in the standard way
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId) ||
                        package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                    {
                        Logger.Debug($"Account has access to depot {depotId} through package {license}");
                        return true;
                    }
                }
            }

            Logger.Debug($"Account does NOT have access to {(appId == depotId ? $"app {appId}" : $"depot {depotId} (app context {appId})")}");
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
        public static async Task<uint> GetParentAppIdAsync(uint appId)
        {
            try
            {
                // First check if we already know this is a DLC via our helper method
                var (isDlc, parentId) = await DlcDetection.DetectDlcAndParentAsync(steam3, appId);

                if (isDlc && parentId != appId)
                {
                    Logger.Info($"App {appId} is a DLC for app {parentId}");
                    return parentId;
                }

                // Look in the app's common section for DLCForAppID
                await steam3.RequestAppInfo(appId);
                var commonSection = GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (commonSection != null && commonSection != KeyValue.Invalid)
                {
                    var typeNode = commonSection["type"];
                    if (typeNode != KeyValue.Invalid && typeNode.Value != null &&
                        typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase))
                    {
                        var dlcForAppIdNode = commonSection["DLCForAppID"];
                        if (dlcForAppIdNode != KeyValue.Invalid && dlcForAppIdNode.Value != null)
                        {
                            if (uint.TryParse(dlcForAppIdNode.Value, out uint dlcParentId) && dlcParentId != 0)
                            {
                                Logger.Info($"App {appId} is a DLC for app {dlcParentId} (from DLCForAppID)");
                                return dlcParentId;
                            }
                        }
                    }
                }

                // Check if this app is marked in our DLC info
                if (IsDlc(appId, out uint storedParentId) && storedParentId != appId)
                {
                    Logger.Info($"App {appId} is a DLC for app {storedParentId} (from stored info)");
                    return storedParentId;
                }

                // No parent found, return the original app ID
                return appId;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error determining parent app ID for {appId}: {ex.Message}");
                return appId; // Default to itself if we can't determine parent
            }
        }

        static async Task<(bool ShouldProcess, DateTime? LastUpdated)> ShouldProcessAppExtendedAsync(uint appId)
        {
            try
            {
                if (steam3 == null || !steam3.IsLoggedOn)
                {
                    Logger.Warning($"Cannot determine if app {appId} should be processed: Steam session not available");
                    return (true, null);
                }

                await steam3.RequestAppInfo(appId);

                // Get app type from common section
                var commonSection = GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (commonSection != null && commonSection != KeyValue.Invalid)
                {
                    var typeNode = commonSection["type"];
                    if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                    {
                        string appType = typeNode.Value.ToLowerInvariant();
                        Logger.Debug($"App {appId} type from SteamKit: {appType}");

                        // Skip certain app types but NOT DLCs
                        if (appType == "demo" || /* removed dlc */ appType == "music" ||
                            appType == "video" || appType == "hardware" || appType == "mod")
                        {
                            Logger.Info($"Skipping app {appId} because its type is '{appType}'.");
                            return (false, null);
                        }

                        // Special handling for DLCs - check if it has depots and is owned
                        if (appType == "dlc")
                        {
                            // First check if the DLC has any depots
                            var dlcDepotsSection = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                            bool hasDepots = false;

                            if (dlcDepotsSection != null && dlcDepotsSection != KeyValue.Invalid)
                            {
                                // Look for actual depots (ignoring the "branches" node)
                                foreach (var child in dlcDepotsSection.Children)
                                {
                                    if (child.Name != "branches" && uint.TryParse(child.Name, out _))
                                    {
                                        hasDepots = true;
                                        Logger.Debug($"DLC {appId} has its own depot: {child.Name}");
                                        break;
                                    }
                                }
                            }

                            // If it doesn't have depots, skip it
                            if (!hasDepots)
                            {
                                Logger.Info($"Skipping DLC {appId} because it has no depots");
                                return (false, null);
                            }

                            // Otherwise check if the account owns this DLC
                            if (!await AccountHasAccessAsync(appId, appId))
                            {
                                Logger.Info($"Skipping DLC {appId} because the account doesn't own it");
                                return (false, null);
                            }

                            Logger.Info($"Processing DLC {appId} - it has depots and is owned by the account");
                        }
                    }

                    // Check for free to download flag
                    var freeToDownloadNode = commonSection["freetodownload"];
                    if (freeToDownloadNode != KeyValue.Invalid && freeToDownloadNode.Value == "1")
                    {
                        Logger.Info($"Skipping app {appId} because it appears to be free to download (freetodownload=1).");
                        return (false, null);
                    }
                }

                // Get last updated time - check public branch first
                DateTime? lastUpdated = null;
                var depotsSection = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                if (depotsSection != null && depotsSection != KeyValue.Invalid)
                {
                    var branchesNode = depotsSection["branches"];
                    if (branchesNode != KeyValue.Invalid)
                    {
                        var publicNode = branchesNode["public"];
                        if (publicNode != KeyValue.Invalid)
                        {
                            var timeUpdatedNode = publicNode["timeupdated"];
                            if (timeUpdatedNode != KeyValue.Invalid && timeUpdatedNode.Value != null &&
                                long.TryParse(timeUpdatedNode.Value, out long unixTime))
                            {
                                lastUpdated = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                                Logger.Debug($"Found last updated time for app {appId}: {lastUpdated}");
                            }
                        }
                    }

                    // If we couldn't find it in branches, try looking at last depot update
                    if (lastUpdated == null)
                    {
                        var latestTime = new DateTime(2000, 1, 1); // Start with old date

                        foreach (var depotNode in depotsSection.Children)
                        {
                            if (depotNode.Name == "branches" || !uint.TryParse(depotNode.Name, out _))
                                continue;

                            var lastUpdateNode = depotNode["lastupdate"];
                            if (lastUpdateNode != KeyValue.Invalid && lastUpdateNode.Value != null &&
                                long.TryParse(lastUpdateNode.Value, out long depotUpdateTime))
                            {
                                var depotDate = DateTimeOffset.FromUnixTimeSeconds(depotUpdateTime).DateTime;
                                if (depotDate > latestTime)
                                {
                                    latestTime = depotDate;
                                }
                            }
                        }

                        if (latestTime.Year > 2000)
                        {
                            lastUpdated = latestTime;
                            Logger.Debug($"Determined last updated time for app {appId} from depot updates: {lastUpdated}");
                        }
                    }
                }

                Logger.Debug($"App {appId}: ShouldProcessApp returning: shouldProcess=true, lastUpdated={lastUpdated}");
                return (true, lastUpdated);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in ShouldProcessAppExtendedAsync for {appId}: {ex.Message}");
                return (true, null);
            }
        }
        static async Task<ulong> GetSteam3DepotManifestAsync(uint depotId, uint appId, string branch)
        {
            Console.WriteLine($"Attempting to get manifest for depot {depotId}, app {appId}, branch '{branch}'");
            Logger.Info($"GetSteam3DepotManifestAsync called for depot {depotId}, app {appId}, branch '{branch}'");

            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null || depots == KeyValue.Invalid)
            {
                Console.WriteLine($"No depots section found for app {appId}");
                return INVALID_MANIFEST_ID;
            }

            var depotChild = depots[depotId.ToString()];
            if (depotChild == KeyValue.Invalid)
            {
                Console.WriteLine($"Depot {depotId} not found in app {appId}, checking alternative approaches");

                // Try looking up using depot ID as app ID for DLCs
                if (depotId > 2000000)
                {
                    Console.WriteLine($"Trying to find depot {depotId} manifests using depot ID as app context");
                    await steam3.RequestAppInfo(depotId);
                    var dlcDepots = GetSteam3AppSection(depotId, EAppInfoSection.Depots);
                    if (dlcDepots != null && dlcDepots != KeyValue.Invalid)
                    {
                        var dlcDepotChild = dlcDepots[depotId.ToString()];
                        if (dlcDepotChild != KeyValue.Invalid)
                        {
                            Console.WriteLine($"Found depot {depotId} information using depot ID as app context");
                            depotChild = dlcDepotChild;
                        }
                        else
                        {
                            // Check branches directly for DLCs
                            var branchesNode = dlcDepots["branches"];
                            if (branchesNode != KeyValue.Invalid)
                            {
                                var branchNode = branchesNode[branch];
                                if (branchNode != KeyValue.Invalid)
                                {
                                    var buildidNode = branchNode["buildid"];
                                    if (buildidNode != KeyValue.Invalid && buildidNode.Value != null)
                                    {
                                        if (ulong.TryParse(buildidNode.Value, out ulong buildId) && buildId != 0)
                                        {
                                            Console.WriteLine($"Found buildid {buildId} for branch '{branch}' in DLC {depotId}, using as manifest ID");
                                            return buildId;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // If still invalid, try depotfromapp
                if (depotChild == KeyValue.Invalid)
                {
                    var depotFromAppNode = depots["depotfromapp"];
                    if (depotFromAppNode != KeyValue.Invalid)
                    {
                        uint otherAppId = depotFromAppNode.AsUnsignedInteger();
                        if (otherAppId == appId)
                        {
                            Logger.Warning($"App {appId}, Depot {depotId} has recursive depotfromapp!");
                            return INVALID_MANIFEST_ID;
                        }
                        Console.WriteLine($"Depot {depotId} has depotfromapp reference to {otherAppId}, following reference");
                        await steam3.RequestAppInfo(otherAppId);
                        return await GetSteam3DepotManifestAsync(depotId, otherAppId, branch);
                    }
                }

                if (depotChild == KeyValue.Invalid)
                {
                    Console.WriteLine($"Could not find depot {depotId} info in app {appId}");
                    return INVALID_MANIFEST_ID;
                }
            }

            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    Logger.Warning($"App {appId}, Depot {depotId} has recursive depotfromapp!");
                    return INVALID_MANIFEST_ID;
                }
                Console.WriteLine($"Depot {depotId} has depotfromapp reference to {otherAppId}, following reference");
                await steam3.RequestAppInfo(otherAppId);
                return await GetSteam3DepotManifestAsync(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests == KeyValue.Invalid && manifests_encrypted == KeyValue.Invalid)
            {
                Console.WriteLine($"No manifests or encrypted manifests found for depot {depotId}");

                // Try checking branches for buildid as fallback
                var branches = depots["branches"];
                if (branches != KeyValue.Invalid)
                {
                    var branchNode = branches[branch];
                    if (branchNode != KeyValue.Invalid)
                    {
                        var buildidNode = branchNode["buildid"];
                        if (buildidNode != KeyValue.Invalid && buildidNode.Value != null)
                        {
                            if (ulong.TryParse(buildidNode.Value, out ulong buildId) && buildId != 0)
                            {
                                Console.WriteLine($"Using buildid {buildId} as manifest ID for branch '{branch}'");
                                return buildId;
                            }
                        }
                    }
                }

                return INVALID_MANIFEST_ID;
            }

            KeyValue node = KeyValue.Invalid;
            if (manifests != KeyValue.Invalid)
            {
                node = manifests[branch];
            }

            if (node != KeyValue.Invalid && node["gid"] != KeyValue.Invalid)
            {
                ulong manifestId = node["gid"].AsUnsignedLong();
                Console.WriteLine($"Found manifest ID {manifestId} for depot {depotId}, branch '{branch}'");
                return manifestId;
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
                        if (!steam3.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword))
                        {
                            Logger.Error($"Password was invalid for branch '{branch}'");
                            return INVALID_MANIFEST_ID;
                        }
                        var input = Util.DecodeHexString(encryptedNode["gid"].Value);
                        byte[] manifest_bytes = Util.SymmetricDecryptECB(input, appBetaPassword);
                        return BitConverter.ToUInt64(manifest_bytes, 0);
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Failed to decrypt manifest for branch '{branch}': {e.Message}");
                        return INVALID_MANIFEST_ID;
                    }
                }
            }

            Console.WriteLine($"No manifest GID found for depot {depotId}, branch '{branch}'");
            return INVALID_MANIFEST_ID;
        }
        static async Task<List<(ulong manifestId, string branch)>> GetManifestsToDumpAsync(uint depotId, uint appId)
        {
            var results = new List<(ulong manifestId, string branch)>();

            // Get the depots information from the app
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null || depots == KeyValue.Invalid)
                return results;

            var depotChild = depots[depotId.ToString()];
            if (depotChild == KeyValue.Invalid)
                return results;

            // Handle shared depots
            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                uint otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    Console.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return results;
                }

                await steam3.RequestAppInfo(otherAppId);
                return await GetManifestsToDumpAsync(depotId, otherAppId);
            }

            var manifests = depotChild["manifests"];
            var manifests_encrypted = depotChild["encryptedmanifests"];

            if (manifests == KeyValue.Invalid && manifests_encrypted == KeyValue.Invalid)
                return results;

            // Process unencrypted manifests
            if (manifests != KeyValue.Invalid)
            {
                foreach (var branchNode in manifests.Children)
                {
                    var branch = branchNode.Name;
                    if (branchNode["gid"] != KeyValue.Invalid)
                    {
                        ulong manifestId = branchNode["gid"].AsUnsignedLong();
                        if (manifestId != INVALID_MANIFEST_ID)
                        {
                            results.Add((manifestId, branch));
                            Logger.Debug($"Found manifest {manifestId} for depot {depotId} in branch '{branch}'");
                        }
                    }
                }
            }

            // Process encrypted manifests if needed
            if (manifests_encrypted != KeyValue.Invalid)
            {
                foreach (var encryptedBranch in manifests_encrypted.Children)
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
                            if (encryptedManifestId != INVALID_MANIFEST_ID)
                            {
                                results.Add((encryptedManifestId, branch));
                                Logger.Debug($"Added encrypted manifest {encryptedManifestId} for branch '{branch}'");
                            }
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
                sourceDirectory = sourceDirectory.TrimEnd();
                zipFilePath = zipFilePath.TrimEnd();

                string zipDirectory = Path.GetDirectoryName(zipFilePath);
                if (!string.IsNullOrEmpty(zipDirectory) && !Directory.Exists(zipDirectory))
                {
                    Directory.CreateDirectory(zipDirectory);
                }

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
                sourceDirName = sourceDirName.TrimEnd();
                destDirName = destDirName.TrimEnd();

                DirectoryInfo dir = new DirectoryInfo(sourceDirName);
                if (!dir.Exists) throw new DirectoryNotFoundException($"Source directory not found: {sourceDirName}");

                Directory.CreateDirectory(destDirName);

                foreach (FileInfo file in dir.GetFiles())
                {
                    try
                    {
                        string tempPath = Path.Combine(destDirName, file.Name);
                        file.CopyTo(tempPath, true);
                    }
                    catch (IOException ioEx)
                    {
                        Logger.Warning($"Error copying file {file.Name}: {ioEx.Message}");
                    }
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
            catch (Exception ex)
            {
                Logger.Error($"Error during directory copy from '{sourceDirName}' to '{destDirName}': {ex.ToString()}");
            }
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

                branchName = branchName.Trim();
                newFolderName = newFolderName.Trim();
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
        private static async Task<Dictionary<uint, string>> GetDlcInfoViaSteamKitOnlyAsync(uint appId)
        {
            var dlcAppIds = new Dictionary<uint, string>();
            Logger.Debug($"Starting SteamKit-only DLC detection for appId: {appId}");

            try
            {
                // Make sure we have the Steam3 session
                if (steam3 == null || !steam3.IsLoggedOn)
                {
                    Logger.Warning("Cannot get DLC info: Steam3 session is not valid or not logged on");
                    return dlcAppIds;
                }

                // Request app info for the base app
                await steam3.RequestAppInfo(appId);

                // Get the extended section which contains the DLC list
                var extendedSection = GetSteam3AppSection(appId, EAppInfoSection.Extended);
                if (extendedSection == null || extendedSection == KeyValue.Invalid)
                {
                    Logger.Warning($"App {appId} does not have an extended section in app info");
                    return dlcAppIds;
                }

                // Look for the 'listofdlc' key that contains comma-separated DLC IDs
                var listOfDlcNode = extendedSection["listofdlc"];
                if (listOfDlcNode == KeyValue.Invalid || string.IsNullOrEmpty(listOfDlcNode.Value))
                {
                    Logger.Debug($"App {appId} does not have a listofdlc in extended section");
                    return dlcAppIds;
                }

                // Parse the DLC IDs
                var dlcIds = listOfDlcNode.Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => uint.TryParse(s, out _))
                    .Select(s => uint.Parse(s))
                    .ToList();

                Logger.Info($"Found {dlcIds.Count} DLCs listed for app {appId}");

                // Now check each DLC to determine if it has depots
                foreach (var dlcId in dlcIds)
                {
                    await steam3.RequestAppInfo(dlcId);
                    bool isDlc = false;
                    bool hasDepots = false;
                    string dlcName = $"DLC {dlcId}";

                    // Check if it's a DLC by looking at the type in common section
                    var commonSection = GetSteam3AppSection(dlcId, EAppInfoSection.Common);
                    if (commonSection != null && commonSection != KeyValue.Invalid)
                    {
                        var typeNode = commonSection["type"];
                        if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                        {
                            isDlc = typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase);
                        }

                        var nameNode = commonSection["name"];
                        if (nameNode != KeyValue.Invalid && nameNode.Value != null)
                        {
                            dlcName = nameNode.Value;
                        }
                    }

                    // Skip if not a DLC
                    if (!isDlc)
                    {
                        Logger.Debug($"App {dlcId} is not a DLC (type not 'dlc'), skipping");
                        continue;
                    }

                    // Check for depots in various ways

                    // Method 1: Check if the DLC has its own depots section
                    var depotsSection = GetSteam3AppSection(dlcId, EAppInfoSection.Depots);
                    if (depotsSection != null && depotsSection != KeyValue.Invalid)
                    {
                        // Look for actual depots (ignoring the "branches" node which isn't a depot)
                        foreach (var child in depotsSection.Children)
                        {
                            if (child.Name != "branches" && uint.TryParse(child.Name, out _))
                            {
                                hasDepots = true;
                                Logger.Debug($"DLC {dlcId} has its own depot: {child.Name}");
                                break;
                            }
                        }
                    }

                    // Method 2: Check for DLC depots in the base app's depots section
                    if (!hasDepots)
                    {
                        var baseDepotsSection = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                        if (baseDepotsSection != null && baseDepotsSection != KeyValue.Invalid)
                        {
                            foreach (var child in baseDepotsSection.Children)
                            {
                                if (child.Name == "branches" || !uint.TryParse(child.Name, out _))
                                    continue;

                                var dlcAppIdNode = child["dlcappid"];
                                if (dlcAppIdNode != KeyValue.Invalid &&
                                    uint.TryParse(dlcAppIdNode.Value, out uint depotDlcId) &&
                                    depotDlcId == dlcId)
                                {
                                    hasDepots = true;
                                    Logger.Debug($"Found depot for DLC {dlcId} in parent app's depots (dlcappid reference)");
                                    break;
                                }
                            }
                        }
                    }

                    // Method 3: Check for hasdepotsindlc in config section 
                    if (!hasDepots)
                    {
                        var configSection = GetSteam3AppSection(dlcId, EAppInfoSection.Config);
                        if (configSection != null && configSection != KeyValue.Invalid)
                        {
                            var hasDepotsNode = configSection["hasdepotsindlc"];
                            if (hasDepotsNode != KeyValue.Invalid && hasDepotsNode.Value != null)
                            {
                                if (hasDepotsNode.Value == "1" ||
                                    hasDepotsNode.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                    hasDepotsNode.Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasDepots = true;
                                    Logger.Debug($"DLC {dlcId} has hasdepotsindlc flag set in config");
                                }
                            }
                        }
                    }

                    // Add to result if it's a DLC without depots
                    if (!hasDepots)
                    {
                        Logger.Info($"Adding DLC {dlcId} ('{dlcName}') to list - it has NO depots");
                        dlcAppIds[dlcId] = dlcName;
                    }
                    else
                    {
                        Logger.Debug($"Skipping DLC {dlcId} ('{dlcName}') - it HAS depots");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in GetDlcInfoViaSteamKitOnlyAsync for app {appId}: {ex.ToString()}");
            }

            Logger.Info($"SteamKit-only DLC detection found {dlcAppIds.Count} DLCs without depots for app {appId}");
            return dlcAppIds;
        }
        private static async Task<Dictionary<uint, string>> GetAppDlcInfoAsync(uint appId)
        {
            var dlcAppIds = new Dictionary<uint, string>();
            Logger.Debug($"Starting GetAppDlcInfoAsync for appId: {appId} (Using SteamKit only)");

            try
            {
                // Make sure we have the Steam3 session
                if (steam3 == null || !steam3.IsLoggedOn)
                {
                    Logger.Warning("Cannot get DLC info: Steam3 session is not valid or not logged on");
                    return dlcAppIds;
                }

                // Request app info for the base app
                await steam3.RequestAppInfo(appId);

                // Get the extended section which contains the DLC list
                var extendedSection = GetSteam3AppSection(appId, EAppInfoSection.Extended);
                if (extendedSection == null || extendedSection == KeyValue.Invalid)
                {
                    Logger.Warning($"App {appId} does not have an extended section in app info");
                    return dlcAppIds;
                }

                // Look for the 'listofdlc' key that contains comma-separated DLC IDs
                var listOfDlcNode = extendedSection["listofdlc"];
                if (listOfDlcNode == KeyValue.Invalid || string.IsNullOrEmpty(listOfDlcNode.Value))
                {
                    Logger.Debug($"App {appId} does not have a listofdlc in extended section");
                    return dlcAppIds;
                }

                // Parse the DLC IDs
                var dlcIds = listOfDlcNode.Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => uint.TryParse(s, out _))
                    .Select(s => uint.Parse(s))
                    .ToList();

                Logger.Info($"Found {dlcIds.Count} DLCs listed for app {appId}");

                // Now check each DLC to determine if it has depots
                foreach (var dlcId in dlcIds)
                {
                    await steam3.RequestAppInfo(dlcId);
                    bool isDlc = false;
                    bool hasDepots = false;
                    string dlcName = $"DLC {dlcId}";

                    // Check if it's a DLC by looking at the type in common section
                    var commonSection = GetSteam3AppSection(dlcId, EAppInfoSection.Common);
                    if (commonSection != null && commonSection != KeyValue.Invalid)
                    {
                        var typeNode = commonSection["type"];
                        if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                        {
                            isDlc = typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase);
                        }

                        var nameNode = commonSection["name"];
                        if (nameNode != KeyValue.Invalid && nameNode.Value != null)
                        {
                            dlcName = nameNode.Value;
                        }
                    }

                    // Skip if not a DLC
                    if (!isDlc)
                    {
                        Logger.Debug($"App {dlcId} is not a DLC (type not 'dlc'), skipping");
                        continue;
                    }

                    // Check for depots in various ways

                    // Method 1: Check if the DLC has its own depots section
                    var depotsSection = GetSteam3AppSection(dlcId, EAppInfoSection.Depots);
                    if (depotsSection != null && depotsSection != KeyValue.Invalid)
                    {
                        // Look for actual depots (ignoring the "branches" node which isn't a depot)
                        foreach (var child in depotsSection.Children)
                        {
                            if (child.Name != "branches" && uint.TryParse(child.Name, out _))
                            {
                                hasDepots = true;
                                Logger.Debug($"DLC {dlcId} has its own depot: {child.Name}");
                                break;
                            }
                        }
                    }

                    // Method 2: Check for DLC depots in the base app's depots section
                    if (!hasDepots)
                    {
                        var baseDepotsSection = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                        if (baseDepotsSection != null && baseDepotsSection != KeyValue.Invalid)
                        {
                            foreach (var child in baseDepotsSection.Children)
                            {
                                if (child.Name == "branches" || !uint.TryParse(child.Name, out _))
                                    continue;

                                var dlcAppIdNode = child["dlcappid"];
                                if (dlcAppIdNode != KeyValue.Invalid &&
                                    uint.TryParse(dlcAppIdNode.Value, out uint depotDlcId) &&
                                    depotDlcId == dlcId)
                                {
                                    hasDepots = true;
                                    Logger.Debug($"Found depot for DLC {dlcId} in parent app's depots (dlcappid reference)");
                                    break;
                                }
                            }
                        }
                    }

                    // Method 3: Check for hasdepotsindlc in config section 
                    if (!hasDepots)
                    {
                        var configSection = GetSteam3AppSection(dlcId, EAppInfoSection.Config);
                        if (configSection != null && configSection != KeyValue.Invalid)
                        {
                            var hasDepotsNode = configSection["hasdepotsindlc"];
                            if (hasDepotsNode != KeyValue.Invalid && hasDepotsNode.Value != null)
                            {
                                if (hasDepotsNode.Value == "1" ||
                                    hasDepotsNode.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                    hasDepotsNode.Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasDepots = true;
                                    Logger.Debug($"DLC {dlcId} has hasdepotsindlc flag set in config");
                                }
                            }
                        }
                    }

                    // Method 4: In some cases, a DLC's config section may have a 'depotsetup' key
                    if (!hasDepots)
                    {
                        var configSection = GetSteam3AppSection(dlcId, EAppInfoSection.Config);
                        if (configSection != null && configSection != KeyValue.Invalid)
                        {
                            var depotSetupNode = configSection["depotsetup"];
                            if (depotSetupNode != KeyValue.Invalid && depotSetupNode.Children.Count > 0)
                            {
                                hasDepots = true;
                                Logger.Debug($"DLC {dlcId} has depotsetup in config section");
                            }
                        }
                    }

                    // Method 5: Check if this DLC is mentioned in any of the parent app's depot dlcappid fields
                    if (!hasDepots)
                    {
                        var baseDepotsSection = GetSteam3AppSection(appId, EAppInfoSection.Depots);
                        if (baseDepotsSection != null && baseDepotsSection != KeyValue.Invalid)
                        {
                            foreach (var depotNode in baseDepotsSection.Children)
                            {
                                if (depotNode.Name == "branches" || !uint.TryParse(depotNode.Name, out _))
                                    continue;

                                var dlcAppIdNode = depotNode["dlcappid"];
                                if (dlcAppIdNode != KeyValue.Invalid &&
                                    uint.TryParse(dlcAppIdNode.Value, out uint linkedDlcId) &&
                                    linkedDlcId == dlcId)
                                {
                                    hasDepots = true;
                                    Logger.Debug($"DLC {dlcId} is referenced by a depot in the parent app");
                                    break;
                                }
                            }
                        }
                    }

                    // Add to result if it's a DLC without depots
                    if (!hasDepots)
                    {
                        Logger.Info($"Adding DLC {dlcId} ('{dlcName}') to list - it has NO depots");
                        dlcAppIds[dlcId] = dlcName;
                    }
                    else
                    {
                        Logger.Debug($"Skipping DLC {dlcId} ('{dlcName}') - it HAS depots");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in GetAppDlcInfoAsync for app {appId}: {ex.ToString()}");
            }

            Logger.Debug($"Finished GetAppDlcInfoAsync for appId: {appId}. Found {dlcAppIds.Count} DLCs (without depots).");
            return dlcAppIds;
        }
        private static async Task UpdateLuaFileWithDlcAsync(string filePath, uint appId, uint depotId, string depotKeyHex, ulong manifestId, Dictionary<uint, string> dlcAppIds)
        {
            try
            {
                // Create directory if it doesn't exist
                string directoryPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }

                // Keep track of which apps we've already added
                HashSet<uint> processedAppIds = new HashSet<uint>();
                Dictionary<uint, string> processedDepotKeys = new Dictionary<uint, string>();
                Dictionary<uint, string> processedManifestIds = new Dictionary<uint, string>();

                // Add the entries we know about from parameters
                processedAppIds.Add(appId);
                if (!string.IsNullOrEmpty(depotKeyHex))
                {
                    processedDepotKeys[depotId] = depotKeyHex;
                }
                processedManifestIds[depotId] = manifestId.ToString();

                // Process existing content if file exists
                if (File.Exists(filePath))
                {
                    foreach (string line in File.ReadAllLines(filePath))
                    {
                        string trimmedLine = line.Trim();

                        // Skip empty lines and pure comment lines
                        if (string.IsNullOrWhiteSpace(trimmedLine) || (trimmedLine.StartsWith("--") && !trimmedLine.Contains("addappid(")))
                        {
                            continue;
                        }

                        // Process app ID lines
                        if (trimmedLine.StartsWith("addappid("))
                        {
                            int startPos = "addappid(".Length;
                            int endPos;

                            // Check if it has a depot key (has a comma)
                            if (trimmedLine.Contains(","))
                            {
                                // This is a depot line with key
                                endPos = trimmedLine.IndexOf(',');
                                if (startPos < endPos && uint.TryParse(trimmedLine.Substring(startPos, endPos - startPos), out uint depotId2))
                                {
                                    // Extract the key if present
                                    int keyStartPos = trimmedLine.IndexOf('"');
                                    int keyEndPos = trimmedLine.LastIndexOf('"');
                                    if (keyStartPos >= 0 && keyEndPos > keyStartPos)
                                    {
                                        string key = trimmedLine.Substring(keyStartPos + 1, keyEndPos - keyStartPos - 1);
                                        processedDepotKeys[depotId2] = key;
                                    }
                                }
                            }
                            else
                            {
                                // This is a regular app ID line
                                endPos = trimmedLine.IndexOf(')');
                                if (startPos < endPos && uint.TryParse(trimmedLine.Substring(startPos, endPos - startPos), out uint appId2))
                                {
                                    processedAppIds.Add(appId2);
                                }
                            }
                        }
                        // Process manifest ID lines
                        else if (trimmedLine.StartsWith("setManifestid("))
                        {
                            int startPos = "setManifestid(".Length;
                            int endPos = trimmedLine.IndexOf(',');
                            if (startPos < endPos && uint.TryParse(trimmedLine.Substring(startPos, endPos - startPos), out uint depotId2))
                            {
                                // Extract the manifest ID
                                int manifestStartPos = trimmedLine.IndexOf('"', endPos);
                                int manifestEndPos = trimmedLine.IndexOf('"', manifestStartPos + 1);
                                if (manifestStartPos >= 0 && manifestEndPos > manifestStartPos)
                                {
                                    string manifestIdStr = trimmedLine.Substring(manifestStartPos + 1, manifestEndPos - manifestStartPos - 1);
                                    processedManifestIds[depotId2] = manifestIdStr;
                                }
                            }
                        }
                    }
                }

                // Add DLC IDs to processed app IDs list if provided
                if (dlcAppIds != null)
                {
                    foreach (var dlcId in dlcAppIds.Keys)
                    {
                        processedAppIds.Add(dlcId);
                    }
                }

                // Build the updated content in the exact format needed
                List<string> outputLines = new List<string>();

                // First add the main app ID
                outputLines.Add($"addappid({appId})");

                // Then add each depot with its key and manifest ID in pairs
                foreach (var depotEntry in processedDepotKeys.OrderBy(kvp => kvp.Key))
                {
                    uint currentDepotId = depotEntry.Key;
                    string currentDepotKey = depotEntry.Value;

                    // Add the depot line with key
                    outputLines.Add($"addappid({currentDepotId}, 1, \"{currentDepotKey}\")");

                    // Add the matching manifest line if we have one
                    if (processedManifestIds.TryGetValue(currentDepotId, out string manifestIdStr))
                    {
                        outputLines.Add($"setManifestid({currentDepotId}, \"{manifestIdStr}\", 0)");
                    }
                }

                // Add any DLC app IDs without depot keys (simple app IDs)
                List<uint> simpleDlcIds = new List<uint>();
                foreach (var appId2 in processedAppIds)
                {
                    // Skip the main app ID (already added) and any depots (already processed)
                    if (appId2 != appId && !processedDepotKeys.ContainsKey(appId2))
                    {
                        simpleDlcIds.Add(appId2);
                    }
                }

                // Add any DLC lines with comments if available
                foreach (var dlcId in simpleDlcIds.OrderBy(id => id))
                {
                    string comment = dlcAppIds != null && dlcAppIds.TryGetValue(dlcId, out string name) && !string.IsNullOrWhiteSpace(name)
                        ? $" -- {name}"
                        : "";

                    outputLines.Add($"addappid({dlcId}){comment}");
                }

                // Write the updated content
                File.WriteAllLines(filePath, outputLines);
                Logger.Info($"Successfully updated Lua file: {filePath}");
            }
            catch (IOException ioEx)
            {
                Logger.Error($"IO Error updating Lua file {filePath}: {ioEx.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error updating Lua file {filePath}: {ex.ToString()}");
            }

            await Task.CompletedTask;
        }
        private static async Task ProcessManifestIndividuallyAsync(uint depotId, uint appId, ulong manifestId, string branch, string path, string depotKeyHex, DateTime manifestDate, Dictionary<uint, string> appDlcInfo, CDNClientPool cdnPoolInstance, uint directoryAppId = 0)
        {
            // Default directoryAppId to appId if not provided
            if (directoryAppId == 0)
            {
                directoryAppId = appId;
            }

            bool manifestDownloaded = false;
            bool manifestSkipped = false;
            List<string> manifestErrors = new List<string>();
            string fullManifestPath = null;
            DepotManifest manifest = null;

            // Initialize with the API-provided date
            DateTime definitiveDate = manifestDate;
            Logger.Debug($"Manifest {manifestId} (Depot {depotId}, App {appId}, Branch '{branch}'): Received manifestDate = {manifestDate}");

            if (cdnPoolInstance == null)
            {
                Logger.Error($"ProcessManifestIndividuallyAsync called with null cdnPoolInstance for manifest {manifestId}, depot {depotId}. Cannot proceed.");
                manifestErrors.Add("Internal error: CDN Pool instance was null.");
                StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, false, false, null, manifestErrors, null);
                return;
            }

            try
            {
                var cleanBranchName = branch.Replace('/', '_').Replace('\\', '_');
                // Use the directory app ID (parent) for path construction
                var appPath = Path.Combine(path, directoryAppId.ToString());
                var branchPath = Path.Combine(appPath, cleanBranchName);
                Directory.CreateDirectory(branchPath);
                var manifestFilename = $"{depotId}_{manifestId}.manifest";
                fullManifestPath = Path.Combine(branchPath, manifestFilename);

                // If manifest already exists, simply mark it as skipped and update stats WITHOUT MODIFYING ANYTHING
                if (File.Exists(fullManifestPath))
                {
                    manifestSkipped = true;
                    Logger.Info($"Manifest {manifestId} exists for depot {depotId} branch '{branch}', skipping completely.");

                    // Check if this manifest is in our date tracker
                    var existingEntry = ManifestDateTracker.GetEntry(depotId, manifestId, branch);
                    if (existingEntry == null)
                    {
                        // If not in tracker but exists on disk, try to get date from folder name
                        var dateFromFolder = Util.GetDateFromExistingFolder(appPath, cleanBranchName, directoryAppId);
                        if (dateFromFolder.HasValue)
                        {
                            // Store in tracker but don't modify anything on disk
                            ManifestDateTracker.SetEntry(depotId, manifestId, branch, dateFromFolder.Value);
                        }
                    }

                    // Add the branch to processed branches but DO NOT update any dates or folders
                    if (!processedBranches.Contains(cleanBranchName))
                    {
                        processedBranches.Add(cleanBranchName);
                    }

                    // Track stats for existing manifest but don't modify anything
                    StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, false, true, fullManifestPath, null, null);
                    return; // Exit early - don't process anything else for existing manifests
                }

                // Only clean up old manifests if we're downloading a new one
                try
                {
                    // Clean up old manifests with same depot ID but different manifest ID
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
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to clean up old manifests for depot {depotId} in branch '{cleanBranchName}': {ex.Message}");
                }

                // At this point we know we need to download the manifest
                try
                {
                    Logger.Info($"DownloadManifestAsync called for manifest {manifestId}, depot {depotId}, app {appId}, branch '{branch}'");
                    manifest = await DownloadManifestAsync(depotId, appId, manifestId, branch, cdnPoolInstance);
                    if (manifest != null)
                        Logger.Debug($"Manifest {manifestId}: Download successful. Manifest.CreationTime = {manifest.CreationTime}");
                    else
                        Logger.Warning($"Manifest {manifestId}: Download failed (manifest is null).");
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Error downloading manifest {manifestId}: {ex.Message}";
                    manifestErrors.Add(errorMsg);
                    Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}");
                }

                if (manifest != null)
                {
                    try
                    {
                        manifest.SaveToFile(fullManifestPath);
                        manifestDownloaded = true;

                        // For newly downloaded manifests, get the date from the manifest itself
                        if (manifest.CreationTime.Year >= 2000)
                        {
                            definitiveDate = manifest.CreationTime;
                            Logger.Debug($"Using manifest creation time for manifest {manifestId}: {definitiveDate}");
                        }

                        // Store in the date cache for future reference
                        ManifestDateTracker.SetEntry(depotId, manifestId, branch, definitiveDate);

                        Logger.Info($"Successfully saved manifest {manifestId} from branch '{branch}' to {fullManifestPath}");

                        // Use directory app ID (parent) in LUA file path
                        string branchLuaFile = Path.Combine(branchPath, $"{directoryAppId}.lua");
                        await UpdateLuaFileWithDlcAsync(branchLuaFile, directoryAppId, depotId, depotKeyHex?.ToLowerInvariant(), manifestId, appDlcInfo ?? new Dictionary<uint, string>());

                        // Flag that we have new manifests - this is used to trigger folder cleanup later
                        anyNewManifests = true;

                        // Add the branch to processed branches
                        if (!processedBranches.Contains(cleanBranchName))
                        {
                            processedBranches.Add(cleanBranchName);
                        }

                        // Update branch last modified date only for new downloads
                        branchLastModified.AddOrUpdate(cleanBranchName, definitiveDate, (key, existingDate) =>
                        {
                            // Always prefer the older date
                            var dateToUse = existingDate;
                            if (definitiveDate < existingDate)
                            {
                                dateToUse = definitiveDate;
                            }
                            Logger.Debug($"Manifest {manifestId}: Updating branch '{key}'. ExistingDate={existingDate}, NewDate={definitiveDate}. Using {dateToUse}");
                            return dateToUse;
                        });

                        StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, true, false, fullManifestPath, null, manifest.CreationTime);
                        Logger.Debug($"[ProcessManifestIndividuallyAsync] Manifest {manifestId} downloaded successfully for depot {depotId} branch '{branch}'");
                    }
                    catch (Exception ex)
                    {
                        string errorMsg = $"Failed to save manifest {manifestId} to file {fullManifestPath}: {ex.Message}";
                        manifestErrors.Add(errorMsg);
                        Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}");
                        StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, false, false, fullManifestPath, manifestErrors, null);
                    }
                }
                else
                {
                    manifestErrors.Add($"Download failed (manifest null) for {manifestId}");
                    Logger.Error($"DownloadManifestAsync returned null for manifest {manifestId}, depot {depotId}, branch '{branch}'.");
                    StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, false, false, null, manifestErrors, null);
                    Logger.Warning($"[ProcessManifestIndividuallyAsync] Manifest {manifestId} download failed for depot {depotId} branch '{branch}'");
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Outer error processing manifest {manifestId}: {ex.Message}";
                manifestErrors.Add(errorMsg);
                Console.WriteLine($"Error processing manifest: {errorMsg}");
                Logger.Error($"{errorMsg} - StackTrace: {ex.StackTrace}");
                if (!manifestDownloaded && !manifestSkipped)
                {
                    StatisticsTracker.TrackManifestProcessing(depotId, manifestId, branch, false, false, null, manifestErrors, null);
                }
            }
        }
        private static async Task<DepotManifest> DownloadManifestAsync(uint depotId, uint appId, ulong manifestId, string branch, CDNClientPool cdnPoolInstance)
        {
            Logger.Info($"DownloadManifestAsync called for manifest {manifestId}, depot {depotId}, app {appId}, branch '{branch}'");

            if (cdnPoolInstance == null)
            {
                Logger.Error($"DownloadManifestAsync called with null cdnPoolInstance for manifest {manifestId}, depot {depotId}. Cannot download.");
                return null;
            }

            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            Server connection = null;

            try
            {
                connection = cdnPoolInstance.GetConnection(cts.Token);
                if (connection == null)
                {
                    Console.WriteLine($"Failed to get connection for manifest {manifestId}");
                    Logger.Warning($"Failed to get CDN connection for manifest {manifestId}");
                    return null;
                }

                ulong manifestRequestCode = 0;
                var manifestRequestCodeExpiration = DateTime.MinValue;
                int retryCount = 0;
                const int maxRetries = 5;
                TimeSpan[] backoffDelays = {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3)
        };

                while (retryCount < maxRetries)
                {
                    try
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        string cdnToken = null;
                        if (steam3 != null && steam3.CDNAuthTokens.TryGetValue((depotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        var now = DateTime.Now;
                        if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                        {
                            if (steam3 == null)
                            {
                                Logger.Error($"GetDepotDecryptionKey for depot {depotId} (app context {appId}): steam3 session is null. Cannot get manifest request code.");
                                throw new InvalidOperationException("Steam3 session is not initialized.");
                            }

                            manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(depotId, appId, manifestId, branch);
                            manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                            if (manifestRequestCode == 0)
                            {
                                Logger.Warning($"Failed to get manifest request code for {manifestId}");
                                break;
                            }
                        }

                        Console.WriteLine("Downloading manifest {0} from {1} with {2}", manifestId, connection, cdnPoolInstance.ProxyServer != null ? cdnPoolInstance.ProxyServer : "no proxy");

                        if (steam3 == null || !steam3.DepotKeys.TryGetValue(depotId, out var depotKey) || depotKey == null)
                        {
                            Logger.Warning($"No depot key available for depot {depotId} in DownloadManifestAsync");
                            return null;
                        }

                        if (cdnPoolInstance.CDNClient == null)
                        {
                            Logger.Error("DownloadManifestAsync: cdnPoolInstance.CDNClient is null. Cannot download manifest.");
                            throw new InvalidOperationException("CDN Client is not initialized within the provided CDN Pool.");
                        }

                        return await cdnPoolInstance.CDNClient.DownloadManifestAsync(depotId, manifestId, manifestRequestCode, connection, depotKey, cdnPoolInstance.ProxyServer, cdnToken);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        if (e.StatusCode == HttpStatusCode.Forbidden && steam3 != null && !steam3.CDNAuthTokens.ContainsKey((depotId, connection.Host)))
                        {
                            await steam3.RequestCDNAuthToken(appId, depotId, connection);
                            cdnPoolInstance.ReturnConnection(connection);
                            connection = cdnPoolInstance.GetConnection(cts.Token);

                            if (connection == null)
                            {
                                Logger.Warning("Could not re-establish CDN connection after auth token request.");
                                break;
                            }

                            continue;
                        }

                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            Logger.Error($"Error downloading manifest {manifestId} after {maxRetries} retries: {e.Message}");
                            break;
                        }

                        Logger.Warning($"Download attempt {retryCount}/{maxRetries} failed: {e.Message}. Retrying after delay...");
                        await Task.Delay(backoffDelays[retryCount - 1]);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Warning($"Operation canceled downloading manifest {manifestId}.");
                        break;
                    }
                    catch (Exception e)
                    {
                        retryCount++;
                        if (retryCount >= maxRetries)
                        {
                            Logger.Error($"Error downloading manifest {manifestId} after {maxRetries} retries: {e.ToString()}");
                            break;
                        }

                        Logger.Warning($"Retrying download {retryCount}/{maxRetries} due to error: {e.Message}. Retrying after delay...");
                        await Task.Delay(backoffDelays[retryCount - 1]);
                    }
                }

                return null;
            }
            finally
            {
                if (connection != null)
                    cdnPoolInstance.ReturnConnection(connection);

                cts.Dispose();
            }
        }
        public static bool IsDlc(uint appId, out uint parentId)
        {
            parentId = appId; // Default to self
            try
            {
                string appPath = Path.Combine(Config.DumpDirectory ?? DEFAULT_DUMP_DIR, appId.ToString());
                string dlcInfoPath = Path.Combine(appPath, $"{appId}.dlcinfo");
                if (File.Exists(dlcInfoPath))
                {
                    string content = File.ReadAllText(dlcInfoPath);
                    string[] parts = content.Split(';');
                    if (parts.Length >= 3 && parts[2].StartsWith("DLC_For_"))
                    {
                        string parentIdStr = parts[2].Substring("DLC_For_".Length);
                        if (uint.TryParse(parentIdStr, out uint result))
                        {
                            parentId = result;
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error reading DLC info file for {appId}: {ex.Message}");
            }
            return false;
        }

        static async Task DumpDepotAsync(uint depotId, uint appId, string path, Dictionary<(uint depotId, string branch), DateTime> manifestDates, DateTime? appLastUpdated = null, Dictionary<uint, string> appDlcInfo = null)
        {
            appDlcInfo ??= new Dictionary<uint, string>();
            CDNClientPool currentCdnPool = null;

            try
            {
                // First determine if this app is a DLC and get its parent ID
                uint parentAppId = await GetParentAppIdAsync(appId);

                // Use the provided app ID for API operations, but parent app ID for directory structure
                uint effectiveAppId = appId;
                uint directoryAppId = parentAppId; // Use parent app ID for directory structure

                Logger.Info($"Dumping depot {depotId} for app {effectiveAppId}" +
                            (parentAppId != appId ? $" (child of {parentAppId})" : ""));

                currentCdnPool = new CDNClientPool(steam3, appId);

                // Request depot key using consistent app context
                Logger.Info($"Requesting depot key for depot {depotId} using app context {appId}");
                await steam3.RequestDepotKey(depotId, appId);

                if (!steam3.DepotKeys.TryGetValue(depotId, out var depotKey))
                {
                    Console.WriteLine("No valid depot key for {0} (App Context: {1}).", depotId, appId);
                    Logger.Warning($"No valid depot key for Depot {depotId} (App Context: {appId}).");
                    StatisticsTracker.TrackDepotSkipped(depotId, effectiveAppId, "Missing depot key");
                    return;
                }

                var manifestsToDump = await GetManifestsToDumpAsync(depotId, appId);

                Logger.Info($"Starting to process depot {depotId} for app context {appId}");
                Console.WriteLine($"Starting to process depot {depotId} for app context {appId}");

                string depotKeyHex = string.Concat(depotKey.Select(b => b.ToString("X2")).ToArray());

                // Use parent app ID for directory paths but maintain the original app ID in filenames
                string appDumpPath = Path.Combine(path, directoryAppId.ToString());
                Directory.CreateDirectory(appDumpPath);

                // Save a mapping file to track the DLC relationship if this is a DLC
                if (parentAppId != appId)
                {
                    string dlcInfoPath = Path.Combine(appDumpPath, $"{appId}.dlcinfo");
                    string parentAppName = GetAppName(parentAppId);
                    string appName = GetAppName(appId);
                    File.WriteAllText(dlcInfoPath, $"{appId};{appName};DLC_For_{parentAppId}");
                    Logger.Info($"Created DLC info file for app {appId} -> parent {parentAppId}");

                    // Also create parent info file if it doesn't exist
                    string parentInfoPath = Path.Combine(appDumpPath, $"{parentAppId}.info");
                    if (!File.Exists(parentInfoPath))
                    {
                        File.WriteAllText(parentInfoPath, $"{parentAppId};{parentAppName}");
                        Logger.Info($"Created parent app info file for app {parentAppId}");
                    }
                }

                var keyFilePath = Path.Combine(appDumpPath, $"{parentAppId.ToString()}.key");
                bool keyExists = false;

                if (File.Exists(keyFilePath))
                {
                    string linePrefix = $"{depotId};";
                    try
                    {
                        keyExists = File.ReadLines(keyFilePath).Any(line => line.TrimStart().StartsWith(linePrefix));
                    }
                    catch (IOException ex)
                    {
                        Logger.Warning($"Could not read key file {keyFilePath}: {ex.Message}");
                    }
                }

                if (!keyExists)
                {
                    try
                    {
                        File.AppendAllText(keyFilePath, $"{depotId};{depotKeyHex}\n");
                        Console.WriteLine("Depot {0} key: {1}", depotId, depotKeyHex);
                        Logger.Info($"Appended key for Depot {depotId} to {keyFilePath}");
                    }
                    catch (IOException ex)
                    {
                        Logger.Error($"Could not write to key file {keyFilePath}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("Using existing depot {0} key", depotId);
                    Logger.Debug($"Key for Depot {depotId} already exists in {keyFilePath}");
                }

                if (manifestsToDump.Count == 0)
                {
                    Console.WriteLine("No accessible manifests found for depot {0}.", depotId);
                    Logger.Warning($"No accessible manifests found for depot {depotId} (App Context: {appId}).");
                    StatisticsTracker.TrackDepotCompletion(depotId, true);
                    return;
                }

                // Continue with manifest processing - using same context throughout
                DateTime defaultDate = appLastUpdated ?? DateTime.Now;
                Logger.Debug($"Depot {depotId} (App {appId}): appLastUpdated = {appLastUpdated}, defaultDate set to: {defaultDate}");

                int maxConcurrent = Config.MaxDownloads > 0 ? Config.MaxDownloads : 4;
                Console.WriteLine($"Processing {manifestsToDump.Count} manifests for depot {depotId} with up to {maxConcurrent} at a time");
                Logger.Info($"Processing {manifestsToDump.Count} manifests for depot {depotId} with up to {maxConcurrent} at a time");

                var tasks = new List<Task>();
                using var concurrencySemaphore = new SemaphoreSlim(maxConcurrent);

                StatisticsTracker.TrackDepotStart(depotId, effectiveAppId, manifestsToDump.Count);
                Logger.Debug($"[DumpDepotAsync] Starting dump of depot {depotId} for app {effectiveAppId}. Manifest count: {manifestsToDump.Count}");

                foreach (var (manifestId, branch) in manifestsToDump)
                {
                    DateTime manifestDate = defaultDate;

                    if (manifestDates != null && manifestDates.TryGetValue((depotId, branch), out DateTime specificDate))
                    {
                        manifestDate = specificDate;
                        Logger.Debug($"Depot {depotId}, Branch '{branch}': Using specific date from manifestDates dictionary: {manifestDate}");
                    }

                    await concurrencySemaphore.WaitAsync();
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            // Use the parent app ID for file structure but maintain the original app context
                            await ProcessManifestIndividuallyAsync(depotId, effectiveAppId, manifestId, branch, path, depotKeyHex, manifestDate, appDlcInfo, currentCdnPool, directoryAppId);
                        }
                        finally
                        {
                            concurrencySemaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                Console.WriteLine($"Completed processing all manifests for depot {depotId}");
                Logger.Info($"Completed processing all manifests for depot {depotId}");

                StatisticsTracker.TrackDepotCompletion(depotId, true);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error dumping depot {depotId}: {e.Message}");
                Logger.Error($"Error dumping depot {depotId}: {e.ToString()}");
                StatisticsTracker.TrackDepotCompletion(depotId, false, new List<string> { e.Message });
            }
            finally
            {
                currentCdnPool?.Shutdown();
            }
        }
        public static async Task DumpAppAsync(bool select, uint specificAppId = INVALID_APP_ID)
        {
            var dumpPath = string.IsNullOrWhiteSpace(Config.DumpDirectory) ? DEFAULT_DUMP_DIR : Config.DumpDirectory;
            Directory.CreateDirectory(Path.Combine(dumpPath, CONFIG_DIR));

            // Add explicit check for app exclusion
            if (specificAppId != INVALID_APP_ID && Config.ExcludedAppIds != null && Config.ExcludedAppIds.Contains(specificAppId))
            {
                Console.WriteLine($"Skipping excluded app {specificAppId}");
                Logger.Info($"Skipping excluded app {specificAppId}");
                StatisticsTracker.TrackAppStart(specificAppId, $"App {specificAppId} (Excluded)");
                StatisticsTracker.TrackAppCompletion(specificAppId, true, new List<string> { "App was in exclusion list" });
                return;
            }

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

                try
                {
                    await steam3?.RequestAppInfo(specificAppId);
                    var appName = GetAppName(specificAppId);
                    if (string.IsNullOrEmpty(appName)) appName = "Unknown_App";

                    // Determine if this is a DLC and find its parent app ID
                    uint parentAppId = await GetParentAppIdAsync(specificAppId);
                    bool isDlc = parentAppId != specificAppId;

                    // Use parent app ID for directory structure if this is a DLC
                    uint directoryAppId = isDlc ? parentAppId : specificAppId;

                    ManifestDateTracker.PreloadBranchDatesFromFolders(dumpPath, directoryAppId);

                    // If this is a DLC, make sure we have the parent app's info too
                    if (isDlc)
                    {
                        await steam3?.RequestAppInfo(parentAppId);
                        var parentAppName = GetAppName(parentAppId);

                        Console.WriteLine($"App {specificAppId} ({appName}) is a DLC for app {parentAppId} ({parentAppName})");
                        Logger.Info($"App {specificAppId} ({appName}) is a DLC for app {parentAppId} ({parentAppName})");

                        // Create parent directory
                        Directory.CreateDirectory(Path.Combine(dumpPath, parentAppId.ToString()));

                        // Create DLC info file
                        string dlcInfoPath = Path.Combine(dumpPath, parentAppId.ToString(), $"{specificAppId}.dlcinfo");
                        File.WriteAllText(dlcInfoPath, $"{specificAppId};{appName};DLC_For_{parentAppId}");

                        // Create parent info file if it doesn't exist
                        string parentInfoPath = Path.Combine(dumpPath, parentAppId.ToString(), $"{parentAppId}.info");
                        if (!File.Exists(parentInfoPath))
                        {
                            File.WriteAllText(parentInfoPath, $"{parentAppId};{parentAppName}");
                        }
                    }

                    (bool shouldProcess, var appLastUpdated) = await ShouldProcessAppExtendedAsync(specificAppId);

                    // Simplified DLC handling - no need for complex parent/child relationship detection
                    StatisticsTracker.TrackAppStart(specificAppId, appName, appLastUpdated);
                    Logger.Debug($"Fetching DLC info for app {specificAppId} ('{appName}') once.");
                    appDlcInfo = await GetAppDlcInfoAsync(specificAppId);
                    Logger.Debug($"Fetched {appDlcInfo.Count} DLCs (without depots) for app {specificAppId}.");

                    // Create directory using the appropriate app ID (parent for DLCs, self for normal apps)
                    Directory.CreateDirectory(Path.Combine(dumpPath, directoryAppId.ToString()));

                    var depots = GetSteam3AppSection(specificAppId, EAppInfoSection.Depots);
                    if (depots == null || depots == KeyValue.Invalid)
                    {
                        Console.WriteLine("No depots section found for app {0}", specificAppId);
                        Logger.Warning($"No depots section found for app {specificAppId}");
                        StatisticsTracker.TrackAppCompletion(specificAppId, true, new List<string> { "No depots found" });
                        return;
                    }

                    if (!shouldProcess)
                    {
                        Console.WriteLine("Skipping app {0}: {1} (detected as non-processed type)", specificAppId, appName);
                        Logger.Info($"Skipping app {specificAppId}: {appName} (detected as non-processed type)");
                        StatisticsTracker.TrackAppCompletion(specificAppId, true, new List<string> { "Skipped (non-processed type)" });
                        return;
                    }

                    var depotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();

                    if (appLastUpdated == null)
                        Console.WriteLine($"Dumping app {specificAppId}: {appName}");
                    else
                        Console.WriteLine($"Dumping app {specificAppId}: {appName} (Last updated: {appLastUpdated.Value:yyyy-MM-dd HH:mm:ss})");

                    // Write app info to file - use directory app ID (parent for DLCs)
                    string infoFilePath = Path.Combine(dumpPath, directoryAppId.ToString(),
                        isDlc ? $"{specificAppId}.dlcinfo" : $"{specificAppId.ToString()}.info");

                    string infoContent = isDlc ?
                        $"{specificAppId};{appName};DLC_For_{parentAppId}" :
                        $"{specificAppId};{appName}";

                    File.WriteAllText(infoFilePath, infoContent);

                    // Process each depot with the app ID as context
                    foreach (var depotSection in depots.Children)
                    {
                        if (!uint.TryParse(depotSection.Name, out uint id) || id == uint.MaxValue || depotSection.Name == "branches" || depotSection.Children.Count == 0)
                            continue;

                        try
                        {
                            if (!await AccountHasAccessAsync(specificAppId, id))
                            {
                                Console.WriteLine($"No access to depot {id}, skipping");
                                StatisticsTracker.TrackDepotSkipped(id, specificAppId, "No account access");
                                continue;
                            }
                        }
                        catch (Exception accessEx)
                        {
                            Console.WriteLine($"Error checking access for depot {id}: {accessEx.Message}");
                            StatisticsTracker.TrackDepotSkipped(id, specificAppId, $"Access check error: {accessEx.Message}");
                            continue;
                        }

                        if (select)
                        {
                            Console.WriteLine($"Dump depot {depotSection.Name}? (Press N to skip/any other key to continue)");
                            if (Console.ReadKey().Key.ToString().Equals("N", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine("\nSkipped.");
                                StatisticsTracker.TrackDepotSkipped(id, specificAppId, "User selected skip");
                                continue;
                            }
                            Console.WriteLine("\n");
                        }

                        // Process the depot using the specific app ID context
                        var currentDepotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();
                        await DumpDepotAsync(id, specificAppId, dumpPath, currentDepotManifestDates, appLastUpdated, appDlcInfo);
                    }

                    // Process DLC entries in Lua
                    if (appDlcInfo != null && appDlcInfo.Count > 0 && processedBranches.Count > 0)
                    {
                        Logger.Info($"Appending {appDlcInfo.Count} DLC entries to Lua files for parent app {directoryAppId} across {processedBranches.Count} processed branches.");
                        var dlcLinesToAdd = new List<string>();
                        dlcLinesToAdd.Add("");
                        foreach (var dlcEntry in appDlcInfo.OrderBy(kvp => kvp.Key))
                        {
                            dlcLinesToAdd.Add($"addappid({dlcEntry.Key}) -- {dlcEntry.Value}");
                        }

                        foreach (var branchName in processedBranches)
                        {
                            string luaFilePath = Path.Combine(dumpPath, directoryAppId.ToString(), branchName, $"{directoryAppId}.lua");
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
                                catch (IOException ioEx)
                                {
                                    Logger.Error($"IO Error finalizing DLC entries in Lua file {luaFilePath}: {ioEx.Message}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Error finalizing DLC entries in Lua file {luaFilePath}: {ex.ToString()}");
                                }
                            }
                            else
                            {
                                Logger.Warning($"Lua file not found for final DLC append: {luaFilePath}. Skipping append for this branch.");
                            }
                        }
                    }
                    else
                    {
                        if (appDlcInfo == null || appDlcInfo.Count == 0)
                            Logger.Info($"No DLCs found or fetched for app {specificAppId}, skipping final Lua append step.");
                        if (processedBranches.Count == 0)
                            Logger.Info($"No branches were processed for app {specificAppId}, skipping final Lua append step.");
                    }

                    // Use the directory app ID (parent for DLCs) for zip creation
                    await CreateZipsForApp(directoryAppId, dumpPath, isDlc ? GetAppName(parentAppId) : appName);
                    CleanupEmptyDirectories(dumpPath);
                    StatisticsTracker.TrackAppCompletion(specificAppId, true);

                    // Clear all state information after finishing an app
                    branchLastModified.Clear();
                    processedBranches.Clear();
                    anyNewManifests = false;
                    Logger.Info($"Completely reset all branch state after app {specificAppId} processing completed");
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error processing specific app {0}: {1}", specificAppId, e.Message);
                    Logger.Error($"Error processing specific app {specificAppId}: {e.ToString()}");
                    StatisticsTracker.TrackAppCompletion(specificAppId, false, new List<string> { e.Message });

                    // Still clear state even on error
                    branchLastModified.Clear();
                    processedBranches.Clear();
                    anyNewManifests = false;
                    Logger.Info($"Reset branch state after error in app {specificAppId} processing");
                }

                return;
            }
            else // This is the streamlined "process all apps" case
            {
                // Get app IDs efficiently
                var allAppIds = new HashSet<uint>();

                try
                {
                    if (steam3.Licenses != null)
                    {
                        // Group licenses into batches to request package info more efficiently
                        const int batchSize = 500; // Increased from 100 to 500
                        var licenseBatches = new List<List<uint>>();
                        var currentBatch = new List<uint>();

                        // Create batches of license IDs (processing ALL licenses, no Take() limit)
                        foreach (var license in steam3.Licenses)
                        {
                            currentBatch.Add(license.PackageID);

                            if (currentBatch.Count >= batchSize)
                            {
                                licenseBatches.Add(currentBatch);
                                currentBatch = new List<uint>();
                            }
                        }

                        // Add the last batch if it has any items
                        if (currentBatch.Count > 0)
                        {
                            licenseBatches.Add(currentBatch);
                        }

                        // Process each batch
                        foreach (var batch in licenseBatches)
                        {
                            await steam3.RequestPackageInfo(batch);

                            foreach (var packageId in batch)
                            {
                                if (steam3.PackageInfo.TryGetValue(packageId, out var package) && package != null)
                                {
                                    var appIdsNode = package.KeyValues["appids"];
                                    if (appIdsNode != null && appIdsNode != KeyValue.Invalid)
                                    {
                                        foreach (var appIdNode in appIdsNode.Children)
                                        {
                                            uint appId = appIdNode.AsUnsignedInteger();
                                            if (appId != 0)
                                            {
                                                allAppIds.Add(appId);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    Logger.Info($"Found {allAppIds.Count} apps in licenses to process");
                    Console.WriteLine($"Found {allAppIds.Count} apps in licenses to process");

                    // Filter out excluded apps before processing
                    if (Config.ExcludedAppIds != null && Config.ExcludedAppIds.Count > 0)
                    {
                        var excludedCount = allAppIds.Count(id => Config.ExcludedAppIds.Contains(id));
                        if (excludedCount > 0)
                        {
                            Logger.Info($"Filtering out {excludedCount} excluded apps");
                            allAppIds = new HashSet<uint>(allAppIds.Where(id => !Config.ExcludedAppIds.Contains(id)));
                            Console.WriteLine($"After filtering exclusions: {allAppIds.Count} apps to process");
                        }
                    }

                    // Process ALL found apps, no Take() limit
                    foreach (var appId in allAppIds)
                    {
                        try
                        {
                            // Skip invalid apps
                            if (appId == 0 || appId == INVALID_APP_ID)
                                continue;

                            // Double-check exclusion (in case it was missed earlier)
                            if (Config.ExcludedAppIds != null && Config.ExcludedAppIds.Contains(appId))
                            {
                                Logger.Info($"Skipping excluded app {appId}");
                                continue;
                            }

                            // Request app info and check if worth processing
                            await steam3.RequestAppInfo(appId);
                            var appName = GetAppName(appId);

                            if (string.IsNullOrEmpty(appName))
                            {
                                continue;
                            }

                            (bool shouldProcess, var lastUpdated) = await ShouldProcessAppExtendedAsync(appId);
                            if (!shouldProcess)
                            {
                                continue;
                            }

                            Console.WriteLine($"Processing app {appId}: {appName}");
                            Logger.Info($"Processing app {appId}: {appName}");

                            // Process this app
                            await DumpAppAsync(select, appId);

                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Error processing app {appId}: {ex.Message}");
                            Console.WriteLine($"Error processing app {appId}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error during all apps processing: {ex.ToString()}");
                    Console.WriteLine($"Error during all apps processing: {ex.Message}");
                }
            }
        }
        private static async Task ProcessSingleAppWithinGroup(uint currentAppId, uint parentAppId, bool select, string dumpPath, Dictionary<uint, string> appDlcInfo, DateTime? appLastUpdated)
        {
            string appName = $"App {currentAppId}";
            List<string> currentAppErrors = new List<string>();
            DateTime startTime = DateTime.Now;
            try
            {
                Console.WriteLine($"--- Starting individual processing for AppID: {currentAppId} (Parent: {parentAppId}) ---");
                Logger.Info($"Starting individual processing for AppID: {currentAppId} (Parent: {parentAppId})");
                await steam3?.RequestAppInfo(currentAppId);
                appName = GetAppName(currentAppId);
                if (string.IsNullOrEmpty(appName)) appName = $"Unknown App {currentAppId}";
                var (appIsDlc, _) = await DlcDetection.DetectDlcAndParentAsync(steam3, currentAppId);
                var (shouldProcessCurrent, _) = await ShouldProcessAppExtendedAsync(currentAppId);

                if (!shouldProcessCurrent)
                {
                    Console.WriteLine("Skipping app {0}: {1} (detected as non-processed type or doesn't have depots)", currentAppId, appName);
                    Logger.Info($"Skipping app {currentAppId}: {appName} (detected as non-processed type or doesn't have depots)");
                    return;
                }

                var depots = GetSteam3AppSection(currentAppId, EAppInfoSection.Depots);
                if (depots == null || depots == KeyValue.Invalid)
                {
                    Logger.Warning($"No depots section found for app {currentAppId} within group {parentAppId}, skipping its depots.");
                    return;
                }

                Console.WriteLine($"Dumping app {currentAppId}: {appName} (Parent Context: {parentAppId})");
                string infoFilePath = Path.Combine(dumpPath, parentAppId.ToString(), $"{currentAppId.ToString()}.{(appIsDlc ? "dlcinfo" : "info")}");
                File.WriteAllText(infoFilePath, $"{currentAppId};{appName}{(appIsDlc ? $";DLC_For_{parentAppId}" : "")}");

                foreach (var depotSection in depots.Children)
                {
                    if (!uint.TryParse(depotSection.Name, out uint id) || id == uint.MaxValue || depotSection.Name == "branches" || depotSection.Children.Count == 0) continue;
                    try
                    {
                        if (!await AccountHasAccessAsync(currentAppId, id))
                        {
                            StatisticsTracker.TrackDepotSkipped(id, parentAppId, "No account access");
                            continue;
                        }
                    }
                    catch (Exception accessEx)
                    {
                        Logger.Warning($"Error checking access for depot {id}: {accessEx.Message}");
                        StatisticsTracker.TrackDepotSkipped(id, parentAppId, $"Access check error: {accessEx.Message}");
                        continue;
                    }
                    if (select)
                    {
                        Console.WriteLine($"Dump depot {depotSection.Name} for app {currentAppId}? (Press N to skip/any other key to continue)");
                        if (Console.ReadKey().Key.ToString().Equals("N", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine("\nSkipped.");
                            StatisticsTracker.TrackDepotSkipped(id, parentAppId, "User selected skip");
                            continue;
                        }
                        Console.WriteLine("\n");
                    }
                    var currentDepotManifestDates = new Dictionary<(uint depotId, string branch), DateTime>();
                    await DumpDepotAsync(id, parentAppId, dumpPath, currentDepotManifestDates, appLastUpdated, appDlcInfo);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing AppID {currentAppId} within group {parentAppId}: {ex.ToString()}");
                Console.WriteLine($"Error processing AppID {currentAppId}: {ex.Message}");
            }
            finally
            {
                TimeSpan duration = DateTime.Now - startTime;
                Console.WriteLine($"--- Finished individual processing for AppID: {currentAppId}. Duration: {duration.TotalSeconds:F2}s ---");
                Logger.Info($"Finished individual processing for AppID: {currentAppId}. Duration: {duration.TotalSeconds:F2}s");
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
        static async Task CreateZipsForApp(uint appId, string path, string appName)
        {
            if (processedBranches.Count == 0)
            {
                Console.WriteLine($"No branches processed for app {appId} ({appName}), skipping zip creation.");
                Logger.Info($"No branches processed for app {appId} ('{appName}'), skipping zip creation.");
                return;
            }

            Console.WriteLine($"Creating zip archives for {processedBranches.Count} branches of app {appId} ('{appName}')");
            Logger.Info($"Creating zip archives for {processedBranches.Count} branches of app {appId} ('{appName}')");

            // Debug dump of branchLastModified dictionary
            Logger.Debug($"Contents of branchLastModified dictionary:");
            foreach (var kvp in branchLastModified)
            {
                Logger.Debug($"  Branch '{kvp.Key}', Date: {kvp.Value}");
            }

            string safeAppName = appName;
            // Use appId (which is the effective/parent appId passed in) for pathing
            string appPath = Path.Combine(path, appId.ToString());
            string infoFilePath = Path.Combine(appPath, $"{appId}.info");
            // Also check for potential DLC info file using the same appId path context
            string dlcInfoFilePath = Path.Combine(appPath, $"{appId}.dlcinfo");

            // First, let's collect all DLCs with no depots for this app
            Dictionary<uint, string> appDlcInfo = null;
            try
            {
                // Get DLCs with no depots
                appDlcInfo = await GetDlcInfoViaSteamKitOnlyAsync(appId);
                Logger.Debug($"Found {appDlcInfo.Count} DLCs without depots for app {appId}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error getting DLC info for app {appId}: {ex.Message}");
                appDlcInfo = new Dictionary<uint, string>();
            }

            try
            {
                // Prioritize info file if it exists at the effective appId path
                if (File.Exists(infoFilePath))
                {
                    string infoContent = await File.ReadAllTextAsync(infoFilePath);
                    string[] parts = infoContent.Split(';');
                    // Expecting AppID;AppName format
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        safeAppName = parts[1];
                        Logger.Info($"Found app name from info file ({infoFilePath}): {safeAppName}");
                    }
                }
                // Check DLC info file if base info wasn't conclusive or didn't exist
                else if (File.Exists(dlcInfoFilePath))
                {
                    string infoContent = await File.ReadAllTextAsync(dlcInfoFilePath);
                    string[] parts = infoContent.Split(';');
                    // Expecting AppID;AppName;DLC_For_ParentAppID format
                    if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        safeAppName = parts[1]; // Initial DLC name
                        if (parts.Length >= 3 && parts[2].StartsWith("DLC_For_"))
                        {
                            string parentIdStr = parts[2].Substring("DLC_For_".Length);
                            if (uint.TryParse(parentIdStr, out uint parentId))
                            {
                                // Look for parent app's info file in its own directory
                                string parentInfoPath = Path.Combine(path, parentId.ToString(), $"{parentId}.info");
                                if (File.Exists(parentInfoPath))
                                {
                                    string parentInfo = await File.ReadAllTextAsync(parentInfoPath);
                                    string[] parentParts = parentInfo.Split(';');
                                    if (parentParts.Length >= 2 && !string.IsNullOrWhiteSpace(parentParts[1]))
                                    {
                                        safeAppName = $"{parentParts[1]} - {safeAppName}"; // Prepend parent name
                                        Logger.Info($"Using parent name for DLC from parent info file: {safeAppName}");
                                    }
                                }
                            }
                        }
                        Logger.Info($"Found app name from DLC info file ({dlcInfoFilePath}): {safeAppName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error reading info/dlcinfo file for app {appId}: {ex.Message}. Using potentially fallback name '{safeAppName}'.");
            }

            // Fallback to SteamKit if name is still default, empty, or potentially just the ID
            if (string.IsNullOrWhiteSpace(safeAppName) || safeAppName == $"Unknown App {appId}" || safeAppName == $"App {appId}" || safeAppName == appName)
            {
                try
                {
                    // Use SteamKit directly instead of API
                    if (steam3 != null && steam3.IsLoggedOn)
                    {
                        await steam3.RequestAppInfo(appId);
                        var commonSection = GetSteam3AppSection(appId, EAppInfoSection.Common);
                        if (commonSection != null && commonSection != KeyValue.Invalid)
                        {
                            var nameNode = commonSection["name"];
                            if (nameNode != KeyValue.Invalid && nameNode.Value != null)
                            {
                                safeAppName = nameNode.Value;
                                Logger.Info($"Using app name from SteamKit: {safeAppName}");

                                // If it's a DLC, try to include the parent name too
                                var typeNode = commonSection["type"];
                                if (typeNode != KeyValue.Invalid && typeNode.Value != null &&
                                    typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase))
                                {
                                    var dlcForAppIdNode = commonSection["DLCForAppID"];
                                    if (dlcForAppIdNode != KeyValue.Invalid && dlcForAppIdNode.Value != null &&
                                        uint.TryParse(dlcForAppIdNode.Value, out uint parentId))
                                    {
                                        // Try to get parent name
                                        await steam3.RequestAppInfo(parentId);
                                        var parentCommonSection = GetSteam3AppSection(parentId, EAppInfoSection.Common);
                                        if (parentCommonSection != null && parentCommonSection != KeyValue.Invalid)
                                        {
                                            var parentNameNode = parentCommonSection["name"];
                                            if (parentNameNode != KeyValue.Invalid && parentNameNode.Value != null)
                                            {
                                                safeAppName = $"{parentNameNode.Value} - {safeAppName}";
                                                Logger.Info($"Using parent name for DLC from SteamKit: {safeAppName}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to get app name from SteamKit for app {appId}: {ex.Message}. Using original name '{safeAppName}'.");
                }
            }

            safeAppName = string.Join("_", safeAppName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (safeAppName.Length > 50) safeAppName = safeAppName.Substring(0, 50);
            safeAppName = safeAppName.Replace("__", "_").Trim('_', ' ');
            if (string.IsNullOrEmpty(safeAppName)) safeAppName = $"Unknown_App_{appId}"; // Ensure some valid name

            // Make sure safe app name doesn't contain periods since they break folder name parsing
            safeAppName = safeAppName.Replace('.', '_');

            // Process DLC entries in Lua - do this before creating zips
            if (appDlcInfo != null && appDlcInfo.Count > 0 && processedBranches.Count > 0)
            {
                Logger.Info($"Appending {appDlcInfo.Count} DLC entries to Lua files for app {appId} across {processedBranches.Count} processed branches.");
                var dlcLinesToAdd = new List<string>();
                dlcLinesToAdd.Add("");
                foreach (var dlcEntry in appDlcInfo.OrderBy(kvp => kvp.Key))
                {
                    dlcLinesToAdd.Add($"addappid({dlcEntry.Key}) -- {dlcEntry.Value}");
                }

                foreach (var branchName in processedBranches)
                {
                    string luaFilePath = Path.Combine(appPath, branchName, $"{appId}.lua");
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
                        catch (IOException ioEx)
                        {
                            Logger.Error($"IO Error finalizing DLC entries in Lua file {luaFilePath}: {ioEx.Message}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error finalizing DLC entries in Lua file {luaFilePath}: {ex.ToString()}");
                        }
                    }
                    else
                    {
                        Logger.Warning($"Lua file not found for final DLC append: {luaFilePath}. Skipping append for this branch.");
                    }
                }
            }
            else
            {
                if (appDlcInfo == null || appDlcInfo.Count == 0)
                    Logger.Info($"No DLCs found or fetched for app {appId}, skipping DLC append step.");
                if (processedBranches.Count == 0)
                    Logger.Info($"No branches were processed for app {appId}, skipping DLC append step.");
            }

            // Use a different variable name in the loop to avoid conflict
            foreach (var branchName in processedBranches)
            {
                // Debug check if branch is in the dictionary
                Logger.Debug($"Branch '{branchName}' in processedBranches: {processedBranches.Contains(branchName)}");
                Logger.Debug($"Branch '{branchName}' in branchLastModified: {branchLastModified.ContainsKey(branchName)}");

                try
                {
                    // Use a different name for the path variable inside the loop
                    // Use appId (effective/parent ID) for pathing
                    string branchSourcePath = Path.Combine(appPath, branchName.Trim());
                    if (!Directory.Exists(branchSourcePath))
                    {
                        Logger.Warning($"Branch source directory not found for zipping: {branchSourcePath}");
                        continue;
                    }

                    DateTime branchDate = DateTime.Now; // Default fallback
                    if (branchLastModified.TryGetValue(branchName, out var date))
                    {
                        branchDate = date;
                        Logger.Debug($"Zip Creation (App {appId}, Branch '{branchName}'): Found date in branchLastModified: {branchDate}");
                    }
                    else
                    {
                        Logger.Warning($"Zip Creation (App {appId}, Branch '{branchName}'): Date NOT found in branchLastModified. Falling back to DateTime.Now ({branchDate}).");
                    }

                    string dateTimeStr = branchDate.ToString("yyyy-MM-dd_HH-mm-ss");
                    Logger.Debug($"Zip Creation (App {appId}, Branch '{branchName}'): Using dateTimeStr: {dateTimeStr}");

                    // Use appId (effective/parent ID) in the folder name structure
                    string folderName = $"{appId}.{branchName.Trim()}.{dateTimeStr}.{safeAppName}".Trim();
                    string dateBranchFolder = Path.Combine(appPath, folderName); // Base folder using appId
                    string zipFilePath = Path.Combine(dateBranchFolder, $"{appId}.zip"); // Zip name also uses appId

                    // Use appId (effective/parent ID) for cleanup path context
                    if (anyNewManifests) { CleanupOldZipsAndFolders(path, appId, branchName, folderName); }

                    if (!Directory.Exists(dateBranchFolder)) { Directory.CreateDirectory(dateBranchFolder); }

                    if (File.Exists(zipFilePath) && !anyNewManifests)
                    {
                        Logger.Info($"Zip archive already exists and no new manifests downloaded for branch '{branchName}', skipping: {zipFilePath}");
                        continue;
                    }

                    if (File.Exists(zipFilePath))
                    {
                        Logger.Info($"Updating zip archive for branch '{branchName}' due to new manifests.");
                        File.Delete(zipFilePath);
                    }

                    string tempDir = Path.Combine(path, $"temp_zip_{appId}_{Guid.NewGuid().ToString().Substring(0, 8)}");
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); }
                        catch (Exception ex) { Logger.Warning($"Error cleaning up temp zip directory {tempDir}: {ex.Message}"); }
                    }

                    Directory.CreateDirectory(tempDir);
                    int manifestsIncluded = 0;
                    int luaFilesIncluded = 0;

                    // Define blacklisted file extensions
                    var blacklistedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dlcinfo",
                ".key",
                ".info"
            };

                    // Files to include are typically within the branchSourcePath
                    foreach (var file in Directory.EnumerateFiles(branchSourcePath))
                    {
                        string fileName = Path.GetFileName(file);
                        string extension = Path.GetExtension(fileName);

                        // Skip blacklisted file extensions
                        if (blacklistedExtensions.Contains(extension))
                        {
                            Logger.Debug($"Skipping blacklisted file: {fileName}");
                            continue;
                        }

                        // Include only manifest and lua files
                        if (fileName.EndsWith(".manifest") || fileName.EndsWith(".lua"))
                        {
                            try
                            {
                                File.Copy(file, Path.Combine(tempDir, fileName), true);
                                if (fileName.EndsWith(".manifest"))
                                    manifestsIncluded++;
                                else if (fileName.EndsWith(".lua"))
                                    luaFilesIncluded++;
                            }
                            catch (IOException ex)
                            {
                                Logger.Warning($"Failed to copy file {fileName} to temp zip dir: {ex.Message}");
                                continue;
                            }
                        }
                    }

                    // Skip copying files from app directory since they'd all be blacklisted

                    if (Directory.EnumerateFileSystemEntries(tempDir).Any())
                    {
                        Logger.Info($"Creating zip for branch '{branchName}' with {manifestsIncluded} manifests, " +
                                    $"{luaFilesIncluded} lua files at {zipFilePath}");
                        CreateZipArchive(tempDir, zipFilePath);
                    }
                    else
                    {
                        Logger.Info($"No files found to include in zip for branch '{branchName}' of app {appId}");
                    }

                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); }
                        catch { Logger.Warning($"Failed to delete temp zip directory: {tempDir}"); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error creating zip for branch '{branchName}' of app {appId}: {ex.ToString()}");
                }
            }
        }
    }
}