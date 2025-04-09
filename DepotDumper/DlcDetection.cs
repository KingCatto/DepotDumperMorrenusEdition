using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDumper
{
    public static class DlcDetection
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly Dictionary<uint, (bool isDlc, uint parentId)> dlcCache = new Dictionary<uint, (bool isDlc, uint parentId)>();
        private static readonly Dictionary<uint, ParentAppData> parentAppCache = new Dictionary<uint, ParentAppData>();
        private static readonly SemaphoreSlim cacheLock = new SemaphoreSlim(1, 1);
        private static readonly string cacheFilePath;
        private static bool cacheLoaded = false;
        
        static DlcDetection()
        {
            // Set up the cache file path
            string baseDir = DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR;
            string configDir = Path.Combine(baseDir, DepotDumper.CONFIG_DIR);
            cacheFilePath = Path.Combine(configDir, "dlc_relationships.json");
            
            // Make sure the directory exists
            Directory.CreateDirectory(configDir);
        }
        
        /// <summary>
        /// Detects if an app is a DLC and gets its parent app ID
        /// </summary>
        /// <param name="steamSession">The Steam3Session instance (kept for compatibility)</param>
        /// <param name="appId">The app ID to check</param>
        /// <returns>A tuple with (isDlc, parentAppId)</returns>
        public static async Task<(bool isDlc, uint parentAppId)> DetectDlcAndParentAsync(Steam3Session steamSession, uint appId)
        {
            await EnsureCacheLoadedAsync();
            
            // Check if we already have this in our memory cache
            if (dlcCache.TryGetValue(appId, out var cachedResult))
            {
                Logger.Debug($"Using cached DLC info for {appId}: isDlc={cachedResult.isDlc}, parentId={cachedResult.parentId}");
                return cachedResult;
            }
            
            try
            {
                // First try using the Steam Store API
                var result = await GetDlcInfoFromStoreApiAsync(appId);
                
                // If successful, save the result
                if (result.isDlc && result.parentAppId != 0)
                {
                    await UpdateCacheAsync(appId, result.isDlc, result.parentAppId, steamSession);
                    return result;
                }
                else if (!result.isDlc)
                {
                    // If it's definitely not a DLC, cache that too
                    await UpdateCacheAsync(appId, false, appId, steamSession);
                    return result;
                }
                
                // If we couldn't determine from the API, fall back to SteamKit
                Logger.Debug($"Steam Store API didn't provide conclusive DLC info for {appId}, falling back to SteamKit");
                var steamKitResult = await SteamKitHelper.DetectDlcAndParentAsync(appId);
                
                // Cache the SteamKit result too
                await UpdateCacheAsync(appId, steamKitResult.isDlc, steamKitResult.parentAppId, steamSession);
                
                return steamKitResult;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error in DetectDlcAndParent using API for {appId}: {ex.Message}. Falling back to SteamKit.");
                
                try
                {
                    var fallbackResult = await SteamKitHelper.DetectDlcAndParentAsync(appId);
                    await UpdateCacheAsync(appId, fallbackResult.isDlc, fallbackResult.parentAppId, steamSession);
                    return fallbackResult;
                }
                catch (Exception innerEx)
                {
                    Logger.Error($"SteamKit fallback also failed for {appId}: {innerEx.Message}");
                    // Return default non-DLC result
                    return (false, appId);
                }
            }
        }
        
        /// <summary>
        /// Gets DLC info from the Steam Store API
        /// </summary>
        private static async Task<(bool isDlc, uint parentAppId)> GetDlcInfoFromStoreApiAsync(uint appId)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                Logger.Debug($"Requesting Store API data for {appId} from {url}");
                
                string jsonResponse = await httpClient.GetStringAsync(url);
                
                // Parse the response
                using (JsonDocument doc = JsonDocument.Parse(jsonResponse))
                {
                    // Check if the request was successful
                    if (!doc.RootElement.TryGetProperty(appId.ToString(), out var appElement) || 
                        !appElement.TryGetProperty("success", out var successElement) ||
                        !successElement.GetBoolean())
                    {
                        Logger.Warning($"Steam Store API returned unsuccessful response for {appId}");
                        return (false, appId);
                    }
                    
                    // Check if the app has data
                    if (!appElement.TryGetProperty("data", out var dataElement))
                    {
                        Logger.Warning($"Steam Store API response for {appId} doesn't contain data");
                        return (false, appId);
                    }
                    
                    // Get app name
                    string appName = "Unknown";
                    if (dataElement.TryGetProperty("name", out var nameElement))
                    {
                        appName = nameElement.GetString();
                    }
                    
                    // Check if it's a DLC
                    if (!dataElement.TryGetProperty("type", out var typeElement) || 
                        !typeElement.GetString().Equals("dlc", StringComparison.OrdinalIgnoreCase))
                    {
                        // Not a DLC - store the app name in the cache
                        await cacheLock.WaitAsync();
                        try
                        {
                            if (!parentAppCache.ContainsKey(appId))
                            {
                                parentAppCache[appId] = new ParentAppData
                                {
                                    AppName = appName,
                                    LastUpdated = DateTime.Now,
                                    DLCs = new Dictionary<uint, DlcInfo>()
                                };
                            }
                            else
                            {
                                parentAppCache[appId].AppName = appName;
                                parentAppCache[appId].LastUpdated = DateTime.Now;
                            }
                        }
                        finally
                        {
                            cacheLock.Release();
                        }
                        
                        Logger.Debug($"App {appId} is not a DLC according to Steam Store API");
                        return (false, appId);
                    }
                    
                    // It's a DLC, try to get the parent app ID
                    if (dataElement.TryGetProperty("fullgame", out var fullGameElement) && 
                        fullGameElement.TryGetProperty("appid", out var parentAppIdElement))
                    {
                        uint parentAppId;
                        if (parentAppIdElement.ValueKind == JsonValueKind.Number)
                        {
                            parentAppId = parentAppIdElement.GetUInt32();
                        }
                        else if (parentAppIdElement.ValueKind == JsonValueKind.String)
                        {
                            if (uint.TryParse(parentAppIdElement.GetString(), out parentAppId))
                            {
                                 //successfully parsed
                            }
                            else
                            {
                                Logger.Warning($"App {appId} is a DLC, but parent app ID is not a valid number: {parentAppIdElement.GetString()}");
                                return (true, appId); // Consider it a DLC with unknown parent.
                            }
                        }
                        else
                        {
                            Logger.Warning($"App {appId} is a DLC, but parent app ID is not a number or string.");
                            return (true, appId); // Consider it a DLC with unknown parent.
                        }
                        
                        // Get parent name if available
                        string parentName = "Unknown";
                        if (fullGameElement.TryGetProperty("name", out var parentNameElement))
                        {
                            parentName = parentNameElement.GetString();
                        }
                        
                        Logger.Info($"App {appId} '{appName}' is a DLC for app {parentAppId} '{parentName}' (from Steam Store API)");
                        
                        // Update the parent cache with the parent name
                        await cacheLock.WaitAsync();
                        try
                        {
                            if (!parentAppCache.ContainsKey(parentAppId))
                            {
                                parentAppCache[parentAppId] = new ParentAppData
                                {
                                    AppName = parentName,
                                    LastUpdated = DateTime.Now,
                                    DLCs = new Dictionary<uint, DlcInfo>()
                                };
                            }
                            
                            // Also update the DLC info in the parent cache
                            if (!parentAppCache[parentAppId].DLCs.ContainsKey(appId))
                            {
                                parentAppCache[parentAppId].DLCs[appId] = new DlcInfo
                                {
                                    AppName = appName,
                                    LastUpdated = DateTime.Now
                                };
                            }
                            else
                            {
                                parentAppCache[parentAppId].DLCs[appId].AppName = appName;
                                parentAppCache[parentAppId].DLCs[appId].LastUpdated = DateTime.Now;
                            }
                        }
                        finally
                        {
                            cacheLock.Release();
                        }
                        
                        return (true, parentAppId);
                    }
                    
                    // It's a DLC but we couldn't find the parent
                    Logger.Warning($"App {appId} '{appName}' is a DLC but parent app ID not found in Steam Store API");
                    return (true, appId);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting DLC info from Steam Store API for {appId}: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Ensures that the cache is loaded from disk
        /// </summary>
        private static async Task EnsureCacheLoadedAsync()
        {
            if (cacheLoaded)
                return;
                
            await cacheLock.WaitAsync();
            try
            {
                if (cacheLoaded) // Double-check after acquiring lock
                    return;
                    
                if (File.Exists(cacheFilePath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(cacheFilePath);
                        var cacheData = JsonSerializer.Deserialize<Dictionary<string, ParentAppData>>(json);
                        
                        if (cacheData != null)
                        {
                            foreach (var parentEntry in cacheData)
                            {
                                if (uint.TryParse(parentEntry.Key, out uint parentAppId))
                                {
                                    // Add to parent cache
                                    parentAppCache[parentAppId] = parentEntry.Value;
                                    
                                    // Add parent app to DLC cache (as non-DLC)
                                    dlcCache[parentAppId] = (false, parentAppId);
                                    
                                    // Add all DLCs to the DLC cache
                                    foreach (var dlcEntry in parentEntry.Value.DLCs)
                                    {
                                        dlcCache[dlcEntry.Key] = (true, parentAppId);
                                    }
                                }
                            }
                        }
                        
                        Logger.Info($"Loaded DLC relationship cache from {cacheFilePath} with {parentAppCache.Count} parent apps and {dlcCache.Count} total entries");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error loading DLC cache from {cacheFilePath}: {ex.Message}");
                        // Continue with empty cache
                    }
                }
                else
                {
                    Logger.Info($"DLC cache file not found at {cacheFilePath}, starting with empty cache");
                }
                
                cacheLoaded = true;
            }
            finally
            {
                cacheLock.Release();
            }
        }
        
        /// <summary>
        /// Updates the memory cache and saves to disk
        /// </summary>
        private static async Task UpdateCacheAsync(uint appId, bool isDlc, uint parentAppId, Steam3Session steamSession)
        {
            await cacheLock.WaitAsync();
            try
            {
                // Update memory cache
                dlcCache[appId] = (isDlc, parentAppId);
                
                if (isDlc)
                {
                    // Get app names if possible
                    string dlcName = await GetAppNameAsync(appId, steamSession);
                    string parentName = await GetAppNameAsync(parentAppId, steamSession);
                    
                    // Create parent entry if it doesn't exist
                    if (!parentAppCache.ContainsKey(parentAppId))
                    {
                        parentAppCache[parentAppId] = new ParentAppData
                        {
                            AppName = parentName,
                            LastUpdated = DateTime.Now,
                            DLCs = new Dictionary<uint, DlcInfo>()
                        };
                    }
                    
                    // Add DLC to parent
                    parentAppCache[parentAppId].DLCs[appId] = new DlcInfo
                    {
                        AppName = dlcName,
                        LastUpdated = DateTime.Now
                    };
                }
                else if (appId == parentAppId) // This is a confirmed non-DLC parent app
                {
                    string appName = await GetAppNameAsync(appId, steamSession);
                    
                    // Create or update parent entry
                    if (!parentAppCache.ContainsKey(appId))
                    {
                        parentAppCache[appId] = new ParentAppData
                        {
                            AppName = appName,
                            LastUpdated = DateTime.Now,
                            DLCs = new Dictionary<uint, DlcInfo>()
                        };
                    }
                    else
                    {
                        parentAppCache[appId].AppName = appName;
                        parentAppCache[appId].LastUpdated = DateTime.Now;
                    }
                }
                
                // Save to disk
                await SaveCacheToDiskAsync();
            }
            finally
            {
                cacheLock.Release();
            }
        }
        
        /// <summary>
        /// Saves the current cache to disk
        /// </summary>
        private static async Task SaveCacheToDiskAsync()
        {
            try
            {
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                
                // Create a serializable dictionary with string keys (JSON requirement)
                var serializableCache = new Dictionary<string, ParentAppData>();
                foreach (var entry in parentAppCache)
                {
                    serializableCache[entry.Key.ToString()] = entry.Value;
                }
                
                string json = JsonSerializer.Serialize(serializableCache, options);
                await File.WriteAllTextAsync(cacheFilePath, json);
                Logger.Debug($"Updated DLC relationship cache with {parentAppCache.Count} parent apps and {dlcCache.Count} total entries");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to save DLC cache to {cacheFilePath}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets an app name from SteamKit if possible
        /// </summary>
        private static async Task<string> GetAppNameAsync(uint appId, Steam3Session steamSession)
        {
            try
            {
                if (steamSession?.IsLoggedOn == true)
                {
                    await steamSession.RequestAppInfo(appId);
                    string name = DepotDumper.GetAppName(appId);
                    return !string.IsNullOrEmpty(name) ? name : $"App {appId}";
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error getting app name for {appId}: {ex.Message}");
            }
            
            return $"App {appId}";
        }
        
        /// <summary>
        /// Parent app data class for the cache
        /// </summary>
        private class ParentAppData
        {
            public string AppName { get; set; }
            public DateTime LastUpdated { get; set; }
            public Dictionary<uint, DlcInfo> DLCs { get; set; } = new Dictionary<uint, DlcInfo>();
        }
        
        /// <summary>
        /// DLC info class for the cache
        /// </summary>
        private class DlcInfo
        {
            public string AppName { get; set; }
            public DateTime LastUpdated { get; set; }
        }
    }
}

