using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SteamKit2;

namespace DepotDumper
{
    class Program
    {
        public static StreamWriter sw;
        public static StreamWriter sw2;
        private static Steam3Session steam3;
        static int Main(string[] args)
        {
            var username = "";
            var password = "";
            if (args.Length == 0)
            {
                PrintUsage();
                Console.Write("Steam Username: ");
                username = Console.ReadLine();
                Console.Write("Steam Password: ");
                password = Console.ReadLine();
            }
            else
            {
                username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
                password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");
            }
            if (username == "" || password == "")
            {
                Console.WriteLine("Username/Password Empty.");
                return 1;
            }

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            if (HasParameter(args, "-debug"))
            {
                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();

                DebugLog.WriteLine("DepotDumper", "Version: {0}", Assembly.GetExecutingAssembly().GetName().Version);
                DebugLog.WriteLine("DepotDumper", "Runtime: {0}", RuntimeInformation.FrameworkDescription);
            }

            DepotDumper.Config.RememberPassword = HasParameter(args, "-remember-password");
            DepotDumper.Config.DumpDirectory = GetParameter<string>(args, "-dir");
            DepotDumper.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            InitializeSteam3(username, password);
            sw = new StreamWriter($"steam.keys");
            sw.AutoFlush = true;
            sw2 = new StreamWriter($"steam.appids");
            sw2.AutoFlush = true;

            if (InitializeSteam(username, password))
            {
                Console.WriteLine("Getting licenses...");
                steam3.WaitUntilCallback(() => { }, () => { return steam3.Licenses != null; });

                IEnumerable<uint> licenseQuery;
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
                steam3.RequestPackageInfo(licenseQuery);

                foreach (var license in licenseQuery)
                {
                    if (license == 0)
                    {
                        continue;
                    }
                    SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                    if (steam3.PackageInfo.TryGetValue(license, out package) && package != null)
                    {
                        foreach (uint appId in package.KeyValues["appids"].Children.Select(x => x.AsUnsignedInteger()))
                        {
                            steam3.RequestAppInfo(appId);

                            SteamApps.PICSProductInfoCallback.PICSProductInfo app;
                            if (!steam3.AppInfo.TryGetValue(appId, out app) || app == null)
                            {
                                continue;
                            }

                            KeyValue appinfo = app.KeyValues;
                            KeyValue depots = appinfo.Children.Where(c => c.Name == "depots").FirstOrDefault();
                            KeyValue common = appinfo.Children.Where(c => c.Name == "common").FirstOrDefault();
                            KeyValue config = appinfo.Children.Where(c => c.Name == "config").FirstOrDefault();


                            if (depots == null)
                            {
                                continue;
                            }

                            string appName = "** UNKNOWN **";
                            if (common != null)
                            {
                                KeyValue nameKV = common.Children.Where(c => c.Name == "name").FirstOrDefault();
                                if (nameKV != null)
                                {
                                    appName = nameKV.AsString();
                                }
                            }

                            Console.WriteLine("Got AppInfo for {0}: {1}", appId, appName);

                            sw2.WriteLine($"{appId};{appName}");

                            foreach (var depotSection in depots.Children)
                            {
                                uint id = uint.MaxValue;

                                if (!uint.TryParse(depotSection.Name, out id) || id == uint.MaxValue)
                                    continue;

                                if (depotSection.Children.Count == 0)
                                    continue;

                                if (config == KeyValue.Invalid)
                                    continue;

                                if (!AccountHasAccess(id))
                                    continue;

                                int attempt = 1;
                                while (!steam3.DepotKeys.ContainsKey(id) && attempt <= 3)
                                {
                                    if (attempt > 1)
                                    {
                                        Console.WriteLine($"Retrying... ({attempt})");
                                    }
                                    steam3.RequestDepotKey(id, appId);
                                    attempt++;
                                }

                            }
                        }
                    }
                }
                sw.Close();
                sw2.Close();
                Console.WriteLine("\nDone!!");
                return 0;
            }
            else
            {
                return 1;
            }
        }

        static bool InitializeSteam(string username, string password)
        {
            if (username != null && password == null && (!DepotDumper.Config.RememberPassword || !AccountSettingsStore.Instance.LoginKeys.ContainsKey(username)))
            {
                do
                {
                    Console.Write("Enter account password for \"{0}\": ", username);
                    if (Console.IsInputRedirected)
                    {
                        password = Console.ReadLine();
                    }
                    else
                    {
                        // Avoid console echoing of password
                        password = Util.ReadPassword();
                    }

                    Console.WriteLine();
                } while (String.Empty == password);
            }
            else if (username == null)
            {
                Console.WriteLine("No username given. Can't dump keys.");
                return false;
            }

            // capture the supplied password in case we need to re-use it after checking the login key
            DepotDumper.Config.SuppliedPassword = password;

            return DepotDumper.InitializeSteam3(username, password);
        }

        static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                    return x;
            }

            return -1;
        }

        static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        static T GetParameter<T>(string[] args, string param, T defaultValue = default(T))
        {
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                return (T)converter.ConvertFromString(strParam);
            }

            return default(T);
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return list;

            index++;

            while (index < args.Length)
            {
                var strParam = args[index];

                if (strParam[0] == '-') break;

                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    list.Add((T)converter.ConvertFromString(strParam));
                }

                index++;
            }

            return list;
        }

        static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("Usage - dumping all depots key in steam account:");
            Console.WriteLine("\tDepotDumper -username <username> -password <password>");
            Console.WriteLine("\t-username <user>\t\t- the username of the account to dump keys.");
            Console.WriteLine("\t-password <pass>\t\t- the password of the account to dump keys.");
            Console.WriteLine("\t-remember-password\t\t- if set, remember the password for subsequent logins of this user. (Use -username <username> -remember-password as login credentials)");
            Console.WriteLine("\t-loginid <#>\t\t- a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDumper concurrently.");
        }
        private static Steam3Session.Credentials steam3Credentials;
        public static bool InitializeSteam3(string username, string password)
        {
            string loginKey = null;

            if (username != null && DepotDumper.Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginKeys.TryGetValue(username, out loginKey);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginKey == null ? password : null,
                    ShouldRememberPassword = DepotDumper.Config.RememberPassword,
                    LoginKey = loginKey,
                    LoginID = DepotDumper.Config.LoginID ?? 0x534B32, // "SK2"
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
        public static bool AccountHasAccess(uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = new List<uint> { 17906 };
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                SteamApps.PICSProductInfoCallback.PICSProductInfo package;
                if (steam3.PackageInfo.TryGetValue(license, out package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            return false;
        }
    }

}
