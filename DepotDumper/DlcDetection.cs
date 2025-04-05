using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SteamKit2;
namespace DepotDumper
{
    /// <summary>
    /// Extension class to add DLC detection functionality to DepotDumper
    /// </summary>
    static class DlcDetection
    {
        private const string API_URL_BASE = "https://api.steamcmd.net/v1/info/";
        /// <summary>
        /// Detects if an app is a DLC and gets its parent app ID
        /// </summary>
        /// <param name="steamSession">The Steam3Session instance</param>
        /// <param name="appId">The app ID to check</param>
        /// <returns>A tuple with (isDlc, parentAppId)</returns>
        public static async Task<(bool isDlc, uint parentAppId)> DetectDlcAndParentAsync(Steam3Session steamSession, uint appId)
        {
            // First check using the traditional method (DLCForAppID in KeyValues)
            var commonSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (commonSection != null)
            {
                var dlcNode = commonSection["DLCForAppID"];
                if (dlcNode != KeyValue.Invalid && dlcNode.Value != null)
                {
                    uint dlcForAppId = dlcNode.AsUnsignedInteger();
                    if (dlcForAppId != 0 && dlcForAppId != appId)
                    {
                        Console.WriteLine($"App {appId} is a DLC for app {dlcForAppId} (detected via DLCForAppID)");
                        return (true, dlcForAppId);
                    }
                }
            }
            // If not detected using KeyValues, try using the Steam API
            try
            {
                bool isDlc = false;
                uint parentAppId = appId;
                // Check if the app is a DLC in the extended section (isSubscription, isDLC, etc.)
                var extendedSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Extended);
                if (extendedSection != null)
                {
                    var isDlcNode = extendedSection["isDLC"];
                    if (isDlcNode != KeyValue.Invalid && isDlcNode.AsBoolean())
                    {
                        isDlc = true;
                        Console.WriteLine($"App {appId} is marked as a DLC in extended info");
                    }
                }
                // If we haven't identified it as a DLC yet, check the Steam API
                if (isDlc)
                {
                    using var httpClient = HttpClientFactory.CreateHttpClient();
                    var response = await httpClient.GetAsync($"{API_URL_BASE}{appId}");
                    if (response.IsSuccessStatusCode)
                    {
                        string jsonContent = await response.Content.ReadAsStringAsync();
                        using JsonDocument document = JsonDocument.Parse(jsonContent);
                        JsonElement root = document.RootElement;
                        if (root.TryGetProperty("status", out var statusElement) &&
                            statusElement.GetString() == "success" &&
                            root.TryGetProperty("data", out var dataElement) &&
                            dataElement.TryGetProperty(appId.ToString(), out var appElement))
                        {
                            // Check if it's marked as a DLC in the type
                            if (appElement.TryGetProperty("common", out var commonElement) &&
                                commonElement.TryGetProperty("type", out var typeElement) &&
                                typeElement.GetString() == "DLC")
                            {
                                isDlc = true;
                                Console.WriteLine($"App {appId} is marked as DLC in Steam API");
                                // Try to find parent app ID in the extended section
                                if (appElement.TryGetProperty("extended", out var extendedElement) &&
                                    extendedElement.TryGetProperty("parent", out var parentElement))
                                {
                                    if (uint.TryParse(parentElement.GetString(), out uint parent) && parent != 0)
                                    {
                                        Console.WriteLine($"Found parent app {parent} for DLC {appId} in Steam API");
                                        parentAppId = parent;
                                    }
                                }
                            }
                        }
                    }
                }
                // If we detected it's a DLC but couldn't find its parent, try a reverse lookup
                if (isDlc && parentAppId == appId)
                {
                    // Get all apps from user's licenses and check if any lists this app as DLC
                    var potentialParents = await FindPotentialParentAppsAsync(steamSession, appId);
                    if (potentialParents.Count > 0)
                    {
                        Console.WriteLine($"Found {potentialParents.Count} potential parent app(s) for DLC {appId}");
                        parentAppId = potentialParents.First(); // Use the first one found
                    }
                }
                return (isDlc, parentAppId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during DLC detection for app {appId}: {ex.Message}");
                return (false, appId);
            }
        }
        /// <summary>
        /// Finds potential parent apps for a DLC by checking which apps list it as DLC
        /// </summary>
        private static async Task<List<uint>> FindPotentialParentAppsAsync(Steam3Session steamSession, uint dlcAppId)
        {
            var results = new List<uint>();
            // Get all apps from the user's licenses
            if (steamSession.Licenses == null) return results;
            var licenseQuery = steamSession.Licenses.Select(x => x.PackageID).Distinct();
            await steamSession.RequestPackageInfo(licenseQuery);
            var potentialAppIds = new HashSet<uint>();
            foreach (var license in licenseQuery)
            {
                if (steamSession.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    foreach (var appId in package.KeyValues["appids"].Children.Select(x => x.AsUnsignedInteger()))
                    {
                        potentialAppIds.Add(appId);
                    }
                }
            }
            // Check each potential parent app
            foreach (var appId in potentialAppIds)
            {
                try
                {
                    await steamSession.RequestAppInfo(appId);
                    // Check if the extended section has a listofdlc field
                    var extendedSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Extended);
                    if (extendedSection != null)
                    {
                        var listOfDlcNode = extendedSection["listofdlc"];
                        if (listOfDlcNode != KeyValue.Invalid && listOfDlcNode.Value != null)
                        {
                            var dlcList = listOfDlcNode.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var dlcEntry in dlcList)
                            {
                                if (uint.TryParse(dlcEntry.Trim(), out uint listedDlcId) && listedDlcId == dlcAppId)
                                {
                                    Console.WriteLine($"App {appId} lists app {dlcAppId} as its DLC");
                                    results.Add(appId);
                                    break;
                                }
                            }
                        }
                    }
                    // Also check the depots section for a dlcappid field
                    var depotSection = DepotDumper.GetSteam3AppSection(appId, EAppInfoSection.Depots);
                    if (depotSection != null)
                    {
                        foreach (var depotChild in depotSection.Children)
                        {
                            var dlcAppIdNode = depotChild["dlcappid"];
                            if (dlcAppIdNode != KeyValue.Invalid && dlcAppIdNode.Value != null)
                            {
                                if (uint.TryParse(dlcAppIdNode.Value, out uint depotDlcId) && depotDlcId == dlcAppId)
                                {
                                    Console.WriteLine($"App {appId} has a depot with dlcappid {dlcAppId}");
                                    results.Add(appId);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error checking app {appId} as potential parent: {ex.Message}");
                }
            }
            return results;
        }
    }
}