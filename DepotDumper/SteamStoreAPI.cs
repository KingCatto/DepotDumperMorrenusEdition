using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DepotDumper
{
    public static class SteamStoreAPI
    {
        private static readonly HttpClient httpClient = HttpClientFactory.CreateHttpClient();
        private static readonly Dictionary<uint, AppDetails> appDetailsCache = new Dictionary<uint, AppDetails>();
        
        public static async Task<AppDetails> GetAppDetailsAsync(uint appId)
        {
            if (appDetailsCache.TryGetValue(appId, out var cachedDetails))
            {
                Logger.Debug($"Using cached app details for {appId}");
                return cachedDetails;
            }

            try
            {
                var storeDetails = await GetAppDetailsFromStoreAsync(appId);
                if (storeDetails != null)
                {
                    appDetailsCache[appId] = storeDetails;
                    return storeDetails;
                }

                var cmdDetails = await GetAppDetailsFromSteamCmdAsync(appId);
                if (cmdDetails != null)
                {
                    appDetailsCache[appId] = cmdDetails;
                    return cmdDetails;
                }

                return new AppDetails { AppId = appId, Name = $"Unknown App {appId}", Type = "unknown" };
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get app details for {appId}: {ex.Message}");
                return new AppDetails { AppId = appId, Name = $"Unknown App {appId}", Type = "unknown" };
            }
        }

        private static async Task<AppDetails> GetAppDetailsFromStoreAsync(uint appId)
        {
            try
            {
                var response = await httpClient.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug($"Store API returned {response.StatusCode} for app {appId}");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(content);
                
                if (!document.RootElement.TryGetProperty(appId.ToString(), out var appElement) || 
                    !appElement.TryGetProperty("success", out var successElement) ||
                    !successElement.GetBoolean())
                {
                    Logger.Debug($"Store API returned unsuccessful response for app {appId}");
                    return null;
                }

                var result = new AppDetails { AppId = appId };
                
                if (appElement.TryGetProperty("data", out var dataElement))
                {
                    if (dataElement.TryGetProperty("name", out var nameElement))
                    {
                        result.Name = nameElement.GetString();
                    }

                    if (dataElement.TryGetProperty("type", out var typeElement))
                    {
                        result.Type = typeElement.GetString()?.ToLowerInvariant();
                    }

                    if (dataElement.TryGetProperty("type", out var appTypeElement) && 
                        appTypeElement.GetString()?.ToLowerInvariant() == "dlc")
                    {
                        result.IsDlc = true;
                        
                        if (dataElement.TryGetProperty("fullgame", out var fullGameElement) && 
                            fullGameElement.TryGetProperty("appid", out var parentAppIdElement))
                        {
                            result.ParentAppId = parentAppIdElement.GetUInt32();
                        }
                    }
                }

                Logger.Info($"Got app details from Store API for {appId}: {result.Name} (Type: {result.Type})");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error retrieving app details from Store API for {appId}: {ex.Message}");
                return null;
            }
        }

        private static async Task<AppDetails> GetAppDetailsFromSteamCmdAsync(uint appId)
        {
            try
            {
                var response = await httpClient.GetAsync($"https://api.steamcmd.net/v1/info/{appId}");
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Debug($"SteamCMD API returned {response.StatusCode} for app {appId}");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(content);
                
                if (!document.RootElement.TryGetProperty("status", out var statusElement) || 
                    statusElement.GetString() != "success" ||
                    !document.RootElement.TryGetProperty("data", out var dataElement) || 
                    !dataElement.TryGetProperty(appId.ToString(), out var appElement))
                {
                    Logger.Debug($"SteamCMD API returned unsuccessful response for app {appId}");
                    return null;
                }

                var result = new AppDetails { AppId = appId };
                
                if (appElement.TryGetProperty("common", out var commonElement))
                {
                    if (commonElement.TryGetProperty("name", out var nameElement))
                    {
                        result.Name = nameElement.GetString();
                    }

                    if (commonElement.TryGetProperty("type", out var typeElement))
                    {
                        result.Type = typeElement.GetString()?.ToLowerInvariant();
                        if (result.Type == "dlc")
                        {
                            result.IsDlc = true;
                        }
                    }
                }

                if (result.IsDlc && appElement.TryGetProperty("extended", out var extendedElement))
                {
                    if (extendedElement.TryGetProperty("parent", out var parentElement) &&
                        uint.TryParse(parentElement.GetString(), out uint parentAppId) && parentAppId != 0)
                    {
                        result.ParentAppId = parentAppId;
                    }
                }

                Logger.Info($"Got app details from SteamCMD API for {appId}: {result.Name} (Type: {result.Type})");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error retrieving app details from SteamCMD API for {appId}: {ex.Message}");
                return null;
            }
        }
    }

    public class AppDetails
    {
        public uint AppId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } = "unknown";
        public bool IsDlc { get; set; }
        public uint? ParentAppId { get; set; }
    }
}