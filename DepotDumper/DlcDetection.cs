using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDumper
{
    /// <summary>
    /// Extension class to add DLC detection functionality to DepotDumper
    /// </summary>
    static class DlcDetection
    {
        /// <summary>
        /// Detects if an app is a DLC and gets its parent app ID
        /// </summary>
        /// <param name="steamSession">The Steam3Session instance</param>
        /// <param name="appId">The app ID to check</param>
        /// <returns>A tuple with (isDlc, parentAppId)</returns>
        public static async Task<(bool isDlc, uint parentAppId)> DetectDlcAndParentAsync(Steam3Session steamSession, uint appId)
        {
            // Simply forward to our SteamKitHelper implementation
            return await SteamKitHelper.DetectDlcAndParentAsync(appId);
        }
    }
}