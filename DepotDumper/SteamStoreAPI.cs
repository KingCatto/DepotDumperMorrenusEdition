using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DepotDumper
{
    public static class SteamStoreAPI
    {
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
                // Use SteamKit implementation instead of API call
                var details = await SteamKitHelper.GetAppDetailsAsync(appId);
                
                if (details != null)
                {
                    appDetailsCache[appId] = details;
                    return details;
                }

                return new AppDetails { AppId = appId, Name = $"Unknown App {appId}", Type = "unknown" };
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get app details for {appId}: {ex.Message}");
                return new AppDetails { AppId = appId, Name = $"Unknown App {appId}", Type = "unknown" };
            }
        }
    }

    // Keep the AppDetails class unchanged
    public class AppDetails
    {
        public uint AppId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } = "unknown";
        public bool IsDlc { get; set; }
        public uint? ParentAppId { get; set; }
    }
}