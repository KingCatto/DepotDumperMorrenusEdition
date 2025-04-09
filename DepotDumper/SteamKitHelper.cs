using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDumper
{
    // Match the accessibility level of the SteamKitHelper class to the Steam3Session class
    internal static class SteamKitHelper
    {
        // 1. Get App Details (replacing SteamStoreAPI.GetAppDetailsAsync)
        public static async Task<AppDetails> GetAppDetailsAsync(uint appId)
        {
            var result = new AppDetails
            {
                AppId = appId,
                Name = $"App {appId}",
                Type = "unknown"
            };
            
            try
            {
                // Make sure we have a valid Steam session
                if (DepotDumper.steam3 == null || !DepotDumper.steam3.IsLoggedOn)
                {
                    Logger.Warning($"Cannot get app details: Steam session not available");
                    return result;
                }
                
                // Request app info through SteamKit
                await DepotDumper.steam3.RequestAppInfo(appId);
                
                // Get the common section which has basic app info
                var commonSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (commonSection != null && commonSection != KeyValue.Invalid)
                {
                    // Get app name
                    var nameNode = commonSection["name"];
                    if (nameNode != KeyValue.Invalid && nameNode.Value != null)
                    {
                        result.Name = nameNode.Value;
                    }
                    
                    // Get app type
                    var typeNode = commonSection["type"];
                    if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                    {
                        result.Type = typeNode.Value.ToLowerInvariant();
                        result.IsDlc = typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase);
                    }
                }
                
                // If it's a DLC, try to find the parent app
                if (result.IsDlc)
                {
                    var dlcNode = commonSection["DLCForAppID"];
                    if (dlcNode != KeyValue.Invalid && dlcNode.Value != null && uint.TryParse(dlcNode.Value, out uint parentId))
                    {
                        result.ParentAppId = parentId;
                    }
                    else
                    {
                        // Try to find parent in extended section
                        var extendedSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Extended);
                        if (extendedSection != null && extendedSection != KeyValue.Invalid)
                        {
                            var parentNode = extendedSection["parent"];
                            if (parentNode != KeyValue.Invalid && parentNode.Value != null && uint.TryParse(parentNode.Value, out uint extParentId))
                            {
                                result.ParentAppId = extParentId;
                            }
                        }
                    }
                }
                
                Logger.Info($"Retrieved app details for {appId} via SteamKit: {result.Name}, Type: {result.Type}, IsDLC: {result.IsDlc}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting app details via SteamKit for {appId}: {ex.Message}");
            }
            
            return result;
        }
        
        // 2. Get app last updated time (replacing API date checks)
        public static async Task<DateTime?> GetAppLastUpdatedAsync(uint appId)
        {
            DateTime? lastUpdated = null;
            
            try
            {
                // Make sure we have a valid Steam session
                if (DepotDumper.steam3 == null || !DepotDumper.steam3.IsLoggedOn)
                {
                    Logger.Warning($"Cannot get app last updated time: Steam session not available");
                    return null;
                }
                
                await DepotDumper.steam3.RequestAppInfo(appId);
                
                // Check the public branch in depots section for time updated
                var depotsSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Depots);
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
            catch (Exception ex)
            {
                Logger.Error($"Error getting last updated time via SteamKit for {appId}: {ex.Message}");
            }
            
            return lastUpdated;
        }
        
        // 3. Check if app should be processed (replacing ShouldProcessAppExtendedAsync)
        public static async Task<(bool ShouldProcess, DateTime? LastUpdated)> ShouldProcessAppAsync(uint appId)
        {
            bool shouldProcess = true;
            DateTime? lastUpdated = null;
            
            try
            {
                // Make sure we have a valid Steam session
                if (DepotDumper.steam3 == null || !DepotDumper.steam3.IsLoggedOn)
                {
                    Logger.Warning($"Cannot determine if app should be processed: Steam session not available");
                    return (true, null);
                }
                
                await DepotDumper.steam3.RequestAppInfo(appId);
                
                // Get app type from common section
                var commonSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (commonSection != null && commonSection != KeyValue.Invalid)
                {
                    var typeNode = commonSection["type"];
                    if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                    {
                        string appType = typeNode.Value.ToLowerInvariant();
                        Logger.Debug($"App {appId} type from SteamKit: {appType}");
                        
                        // Skip certain app types
                        if (appType == "demo" || appType == "dlc" || appType == "music" || 
                            appType == "video" || appType == "hardware" || appType == "mod")
                        {
                            shouldProcess = false;
                            Logger.Info($"Skipping app {appId} because its type is '{appType}'.");
                        }
                    }
                    
                    // Check for free to download flag
                    var freeToDownloadNode = commonSection["freetodownload"];
                    if (shouldProcess && freeToDownloadNode != KeyValue.Invalid && 
                        freeToDownloadNode.Value == "1")
                    {
                        shouldProcess = false;
                        Logger.Info($"Skipping app {appId} because it appears to be free to download (freetodownload=1).");
                    }
                }
                
                // Get last updated time
                lastUpdated = await GetAppLastUpdatedAsync(appId);
                
                Logger.Debug($"App {appId}: ShouldProcessApp returning: shouldProcess={shouldProcess}, lastUpdated={lastUpdated}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in ShouldProcessApp for {appId}: {ex.Message}");
            }
            
            return (shouldProcess, lastUpdated);
        }
        
        // 4. Detect if an app is a DLC and find its parent (replacing DlcDetection.DetectDlcAndParentAsync)
        public static async Task<(bool isDlc, uint parentAppId)> DetectDlcAndParentAsync(uint appId)
        {
            bool isDlc = false;
            uint parentAppId = appId;
            
            try
            {
                // Make sure we have a valid Steam session
                if (DepotDumper.steam3 == null || !DepotDumper.steam3.IsLoggedOn)
                {
                    Logger.Warning($"Cannot detect DLC status: Steam session not available");
                    return (false, appId);
                }
                
                await DepotDumper.steam3.RequestAppInfo(appId);
                
                // Check common section for DLC flag and parent app ID
                var commonSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (commonSection != null && commonSection != KeyValue.Invalid)
                {
                    // Check type for "dlc"
                    var typeNode = commonSection["type"];
                    if (typeNode != KeyValue.Invalid && typeNode.Value != null)
                    {
                        isDlc = typeNode.Value.Equals("dlc", StringComparison.OrdinalIgnoreCase);
                    }
                    
                    // If it's a DLC, try to find the parent app from DLCForAppID
                    if (isDlc)
                    {
                        var dlcForAppIdNode = commonSection["DLCForAppID"];
                        if (dlcForAppIdNode != KeyValue.Invalid && dlcForAppIdNode.Value != null)
                        {
                            if (uint.TryParse(dlcForAppIdNode.Value, out uint dlcForAppId) && dlcForAppId != 0)
                            {
                                parentAppId = dlcForAppId;
                                Logger.Info($"App {appId} is a DLC for app {parentAppId} (detected via DLCForAppID)");
                                return (true, parentAppId);
                            }
                        }
                    }
                }
                
                // Check extended section
                if (isDlc)
                {
                    var extendedSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Extended);
                    if (extendedSection != null && extendedSection != KeyValue.Invalid)
                    {
                        var parentNode = extendedSection["parent"];
                        if (parentNode != KeyValue.Invalid && parentNode.Value != null)
                        {
                            if (uint.TryParse(parentNode.Value, out uint parent) && parent != 0)
                            {
                                parentAppId = parent;
                                Logger.Info($"App {appId} is a DLC for app {parentAppId} (detected via extended.parent)");
                                return (true, parentAppId);
                            }
                        }
                    }
                }
                
                // If we still couldn't find the parent but we know it's a DLC,
                // try to find any app that lists this as a DLC in its listofdlc
                if (isDlc && parentAppId == appId)
                {
                    // This requires having a list of potential parent apps
                    // For now, we can check apps from user's licenses
                    var potentialParents = await FindPotentialParentAppsAsync(appId);
                    if (potentialParents.Count > 0)
                    {
                        Logger.Info($"Found potential parent app {potentialParents[0]} for DLC {appId} via license scanning");
                        parentAppId = potentialParents[0];
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in DetectDlcAndParent for {appId}: {ex.Message}");
            }
            
            return (isDlc, parentAppId);
        }
        
        // Helper method to find potential parent apps
        private static async Task<List<uint>> FindPotentialParentAppsAsync(uint dlcAppId)
        {
            var results = new List<uint>();
            
            try
            {
                // Make sure we have a valid Steam session with licenses
                if (DepotDumper.steam3 == null || !DepotDumper.steam3.IsLoggedOn || DepotDumper.steam3.Licenses == null)
                {
                    Logger.Warning("Cannot find potential parent apps: Steam session not available or no licenses");
                    return results;
                }
                    
                // Get potential app IDs from packages
                var packageIds = DepotDumper.steam3.Licenses.Select(x => x.PackageID).Distinct();
                await DepotDumper.steam3.RequestPackageInfo(packageIds);
                
                var potentialAppIds = new HashSet<uint>();
                foreach (var packageId in packageIds)
                {
                    if (DepotDumper.steam3.PackageInfo.TryGetValue(packageId, out var package) && package != null)
                    {
                        foreach (var appidKv in package.KeyValues["appids"].Children)
                        {
                            potentialAppIds.Add(appidKv.AsUnsignedInteger());
                        }
                    }
                }
                
                // Check each potential parent
                foreach (var potentialParentId in potentialAppIds)
                {
                    // Skip self
                    if (potentialParentId == dlcAppId)
                        continue;
                        
                    await DepotDumper.steam3.RequestAppInfo(potentialParentId);
                    
                    // Check extended section for listofdlc
                    var extendedSection = DepotDumper.GetSteam3AppSection(potentialParentId, EAppInfoSection.Extended);
                    if (extendedSection != null && extendedSection != KeyValue.Invalid)
                    {
                        var listOfDlcNode = extendedSection["listofdlc"];
                        if (listOfDlcNode != KeyValue.Invalid && listOfDlcNode.Value != null)
                        {
                            var dlcList = listOfDlcNode.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var dlcEntry in dlcList)
                            {
                                if (uint.TryParse(dlcEntry.Trim(), out uint listedDlcId) && listedDlcId == dlcAppId)
                                {
                                    results.Add(potentialParentId);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error finding potential parent apps for DLC {dlcAppId}: {ex.Message}");
            }
            
            return results;
        }
    }
}