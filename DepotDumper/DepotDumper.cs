using System;
using SteamKit2;

namespace DepotDumper
{
    public class DepotDumperException : Exception
    {
        public DepotDumperException(String value) : base(value) { }
    }

    static class DepotDumper
    {


        public static DumpConfig Config = new DumpConfig();

        private static Steam3Session steam3;
        private static Steam3Session.Credentials steam3Credentials;




        public static bool InitializeSteam3(string username, string password)
        {
            string loginKey = null;

            if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginKeys.TryGetValue(username, out loginKey);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginKey == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    LoginKey = loginKey,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            steam3Credentials = steam3.WaitForCredentials();

            if (!steam3Credentials.IsValid)
            {
                Console.WriteLine("Unable to get steam3 credentials.");
                return false;
            }

            return true;
        }
    }
}
