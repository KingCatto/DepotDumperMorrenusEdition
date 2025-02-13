// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDumper
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintVersion();
                PrintUsage();

                if (OperatingSystem.IsWindowsVersionAtLeast(5, 0))
                {
                    PlatformUtilities.VerifyConsoleLaunch();
                }

                return 0;
            }

            Ansi.Init();

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            // Not using HasParameter because it is case insensitive
            if (args.Length == 1 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                return 0;
            }

            if (HasParameter(args, "-debug"))
            {
                PrintVersion(true);

                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();
            }

            var username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
            var password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");

            DepotDumper.Config.RememberPassword = HasParameter(args, "-remember-password");
            DepotDumper.Config.UseQrCode = HasParameter(args, "-qr");

            if (username == null)
            {
                if (DepotDumper.Config.RememberPassword)
                {
                    Console.WriteLine("Error: -remember-password can not be used without -username.");
                    return 1;
                }

                if (DepotDumper.Config.UseQrCode)
                {
                    Console.WriteLine("Error: -qr can not be used without -username.");
                    return 1;
                }
            }

            var cellId = GetParameter(args, "-cellid", -1);
            if (cellId == -1)
            {
                cellId = 0;
            }

            DepotDumper.Config.CellID = cellId;

            DepotDumper.Config.MaxServers = GetParameter(args, "-max-servers", 20);

            DepotDumper.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            if (InitializeSteam(username, password))
            {
                try
                {
                    bool select = HasParameter(args, "-select");
                    await DepotDumper.DumpAppAsync(select).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                   ex is DepotDumperException
                   || ex is OperationCanceledException)
                {
                    Console.WriteLine(ex.Message);
                    return 1;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Download failed to due to an unhandled exception: {0}", e.Message);
                    throw;
                }
                finally
                {
                    DepotDumper.ShutdownSteam3();
                }
            }
            else
            {
                Console.WriteLine("Error: InitializeSteam failed");
                return 1;
            }
            
            return 0;
        }

        static bool InitializeSteam(string username, string password)
        {
            if (!DepotDumper.Config.UseQrCode)
            {
                if (username != null && password == null && (!DepotDumper.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username)))
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
                    } while (string.Empty == password);
                }
                else if (username == null)
                {
                    Console.WriteLine("No username given. Using anonymous account with dedicated server subscription.");
                }
            }

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

        static T GetParameter<T>(string[] args, string param, T defaultValue = default)
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

            return default;
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
            Console.WriteLine("\t-select \t\t- select depot to dump key.");
            Console.WriteLine("\t-qr \t\t Use QR code to login.");
        }

        static void PrintVersion(bool printExtra = false)
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Console.WriteLine($"DepotDumper v{version}");

            if (!printExtra)
            {
                return;
            }

            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        }
    }
}
