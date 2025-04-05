using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDumper
{
    class Program
    {
        public static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                    return x;
            }
            return -1;
        }

        public static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        public static T GetParameter<T>(string[] args, string param, T defaultValue = default)
        {
            var index = IndexOfParam(args, param);
            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                try
                {
                    return (T)converter.ConvertFromString(strParam);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not parse parameter '{param}' value '{strParam}' as {typeof(T).Name}. Using default. Error: {ex.Message}");
                    Logger.Warning($"Could not parse parameter '{param}' value '{strParam}' as {typeof(T).Name}. Using default. Error: {ex.Message}");
                    return defaultValue;
                }
            }
            return default;
        }

        static async Task<int> Main(string[] args)
        {
            string configPathArg = null;
            ConfigFile config = null;

            // Initialize statistics tracking early
            StatisticsTracker.Initialize();

            bool isDoubleClick = args.Length == 0;
            if (isDoubleClick)
            {
                Console.WriteLine("DepotDumper started via double-click...");
                string configPath = "config.json";
                config = ConfigFile.Load(configPath);
                config.ApplyToDepotDumperConfig();
                args = new[] { "-config", configPath, "-generate-reports" };
                Console.WriteLine($"Loading configuration from: {configPath}");
                Console.WriteLine("Output will be saved to reports directory.");
                Console.WriteLine();
            }

            if (args.Length > 0 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                if (isDoubleClick) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
                return 0;
            }

            if (args.Length >= 1 && args[0].ToLower() == "config")
            {
                int result = ConfigCommand.Run(args);
                if (isDoubleClick) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
                return result;
            }

            if (HasParameter(args, "-config"))
            {
                configPathArg = GetParameter<string>(args, "-config");
            }
            config = configPathArg != null ? ConfigFile.Load(configPathArg) : ConfigFile.Load();
            config.MergeCommandLineParameters(args);

            Ansi.Init();
            DebugLog.Enabled = false;
            AccountSettingsStore.LoadFromFile("account.config");

            bool generateReports = HasParameter(args, "-generate-reports") || isDoubleClick;
            string dumpPath = string.IsNullOrWhiteSpace(DepotDumper.Config.DumpDirectory) ? DepotDumper.DEFAULT_DUMP_DIR : DepotDumper.Config.DumpDirectory;
            string reportsDirectory = GetParameter<string>(args, "-reports-dir") ?? Path.Combine(dumpPath, "reports");
            string logsDirectory = GetParameter<string>(args, "-logs-dir") ?? Path.Combine(dumpPath, "logs");
            LogLevel logLevel = LogLevel.Info;

            if (HasParameter(args, "-log-level"))
            {
                string levelStr = GetParameter<string>(args, "-log-level", "info").ToLower();
                switch (levelStr)
                {
                    case "debug": logLevel = LogLevel.Debug; break;
                    case "info": logLevel = LogLevel.Info; break;
                    case "warning": logLevel = LogLevel.Warning; break;
                    case "error": logLevel = LogLevel.Error; break;
                    case "critical": logLevel = LogLevel.Critical; break;
                    default: logLevel = LogLevel.Info; break;
                }
            }

            Directory.CreateDirectory(logsDirectory);
            string logFilePath = Path.Combine(logsDirectory, $"depotdumper_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            Logger.Initialize(logFilePath, logLevel);

            // Re-initialize statistics tracker after logging is set up
            StatisticsTracker.Initialize();

            if (HasParameter(args, "-debug") || logLevel == LogLevel.Debug)
            {
                PrintVersion(true);
                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) => { Console.WriteLine("[{0}] {1}", category, message); });
                Logger.Info("Debug logging enabled.");
            }

            bool saveConfig = HasParameter(args, "-save-config");
            var username = config.Username;
            var password = config.Password;
            uint specificAppId = GetParameter(args, "-appid", DepotDumper.INVALID_APP_ID);
            string appIdsFilePath = GetParameter<string>(args, "-appids-file");
            List<uint> appIdsList = new List<uint>();

            if (!string.IsNullOrEmpty(appIdsFilePath))
            {
                if (!File.Exists(appIdsFilePath))
                {
                    Logger.Error($"Error: App IDs file not found: {appIdsFilePath}");
                    Console.WriteLine($"Error: App IDs file not found: {appIdsFilePath}");
                    if (isDoubleClick) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
                    return 1;
                }
                try
                {
                    string[] lines = File.ReadAllLines(appIdsFilePath);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                        if (uint.TryParse(trimmed, out uint appId)) appIdsList.Add(appId);
                        else Logger.Warning($"Warning: Invalid app ID format in file: '{trimmed}'");
                    }
                    if (appIdsList.Count == 0) { Logger.Error("Error: No valid app IDs found in the file."); Console.WriteLine("Error: No valid app IDs found in the file."); if (isDoubleClick) { /* Wait */ } return 1; }
                    Logger.Info($"Loaded {appIdsList.Count} app IDs from file: {appIdsFilePath}");
                    Console.WriteLine($"Loaded {appIdsList.Count} app IDs from file: {appIdsFilePath}");
                }
                catch (Exception ex) { Logger.Error($"Error reading app IDs file: {ex.Message}"); Console.WriteLine($"Error reading app IDs file: {ex.Message}"); if (isDoubleClick) { /* Wait */ } return 1; }
            }
            else if (specificAppId == DepotDumper.INVALID_APP_ID && config.AppIdsToProcess.Count > 0)
            {
                appIdsList.AddRange(config.AppIdsToProcess.Where(id => !config.ExcludedAppIds.Contains(id)));
                if (appIdsList.Count > 0)
                {
                    Logger.Info($"Loaded {appIdsList.Count} app IDs from configuration file.");
                    Console.WriteLine($"Loaded {appIdsList.Count} app IDs from configuration file.");
                    int excludedCount = config.AppIdsToProcess.Count - appIdsList.Count;
                    if (excludedCount > 0)
                    {
                        Logger.Info($"Skipping {excludedCount} excluded app IDs from configuration.");
                        Console.WriteLine($"Skipping {excludedCount} excluded app IDs from configuration.");
                    }
                }
            }

            if (specificAppId != DepotDumper.INVALID_APP_ID)
            {
                if (!config.AppIdsToProcess.Contains(specificAppId))
                {
                    config.AppIdsToProcess.Add(specificAppId);
                }
            }

            int exitCode = 0;
            bool success = false;
            if (InitializeSteam(username, password, isDoubleClick))
            {
                try
                {
                    bool select = HasParameter(args, "-select");

                    if (appIdsList.Count > 0)
                    {
                        Logger.Info($"Processing {appIdsList.Count} AppIDs from list.");
                        await ProcessMultipleAppsAsync(appIdsList, select, dumpPath, config.MaxConcurrentApps);
                        success = true;
                    }
                    else if (specificAppId != DepotDumper.INVALID_APP_ID)
                    {
                        Logger.Info($"Processing specific AppID: {specificAppId}");

                        // Track the app start before processing
                        string appName = "Unknown App";
                        List<string> appErrors = new List<string>();
                        try
                        {
                            await DepotDumper.steam3?.RequestAppInfo(specificAppId);
                            appName = DepotDumper.GetAppName(specificAppId);
                            if (string.IsNullOrEmpty(appName)) appName = $"Unknown App {specificAppId}";
                        }
                        catch (Exception ex)
                        {
                            appErrors.Add($"Failed to get app name: {ex.Message}");
                            Logger.Warning($"Failed to get app name for {specificAppId}: {ex.Message}");
                        }

                        // Start tracking this app
                        StatisticsTracker.TrackAppStart(specificAppId, appName);

                        // Process the app
                        await DepotDumper.DumpAppAsync(select, specificAppId).ConfigureAwait(false);

                        // Mark app as successfully completed
                        StatisticsTracker.TrackAppCompletion(specificAppId, true, appErrors.Count > 0 ? appErrors : null);

                        success = true;
                    }
                    else
                    {
                        Logger.Info("Processing all apps from licenses.");
                        await DepotDumper.DumpAppAsync(select).ConfigureAwait(false);
                        success = true;
                    }

                    if (saveConfig)
                    {
                        Logger.Info("Saving configuration as requested by -save-config flag.");
                        config.Save();
                    }
                }
                catch (DepotDumperException ddEx)
                {
                    // Track failure if processing a specific app
                    if (specificAppId != DepotDumper.INVALID_APP_ID)
                    {
                        StatisticsTracker.TrackAppCompletion(specificAppId, false, new List<string> { ddEx.Message });
                    }
                    StatisticsTracker.TrackError(ddEx.Message);

                    Logger.Error(ddEx.Message);
                    Console.WriteLine(ddEx.Message);
                    exitCode = 1;
                    success = false;
                }
                catch (OperationCanceledException ocEx)
                {
                    // Track failure if processing a specific app
                    if (specificAppId != DepotDumper.INVALID_APP_ID)
                    {
                        StatisticsTracker.TrackAppCompletion(specificAppId, false, new List<string> { ocEx.Message });
                    }
                    StatisticsTracker.TrackError($"Operation Canceled: {ocEx.Message}");

                    Logger.Error($"Operation Canceled: {ocEx.Message}");
                    Console.WriteLine($"Operation Canceled: {ocEx.Message}");
                    exitCode = 1;
                    success = false;
                }
                catch (Exception e)
                {
                    // Track failure if processing a specific app
                    if (specificAppId != DepotDumper.INVALID_APP_ID)
                    {
                        StatisticsTracker.TrackAppCompletion(specificAppId, false, new List<string> { e.ToString() });
                    }
                    StatisticsTracker.TrackError($"Unhandled exception: {e.Message}");

                    Logger.Critical($"Unhandled exception during processing: {e.ToString()}");
                    Console.WriteLine($"Download failed due to an unhandled exception: {e.Message}");
                    exitCode = 1;
                    success = false;
                }
                finally
                {
                    if (generateReports)
                    {
                        try
                        {
                            // Print statistics summary to console
                            StatisticsTracker.PrintSummary();

                            // Get a summary for the reports
                            var summary = StatisticsTracker.GetSummary();
                            ReportGenerator.SaveAllReports(summary, reportsDirectory);

                            Logger.Info($"Reports generated successfully in {reportsDirectory}");
                            Console.WriteLine($"Reports generated successfully in {reportsDirectory}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Failed to generate reports: {ex.Message}");
                            Console.WriteLine($"Failed to generate reports: {ex.Message}");
                        }
                    }
                    DepotDumper.ShutdownSteam3();
                    if (isDoubleClick) { PlayCompletionSound(success); }
                }
            }
            else
            {
                // Track Steam initialization failure
                StatisticsTracker.TrackError("Steam initialization failed");

                Logger.Critical("Error: Steam initialization failed.");
                Console.WriteLine("Error: InitializeSteam failed");
                exitCode = 1;
            }

            if (isDoubleClick)
            {
                Console.WriteLine("\nProcessing finished. Press any key to exit...");
                Console.ReadKey();
            }

            return exitCode;
        }

        private static void PlayCompletionSound(bool success)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using (var player = new SoundPlayer())
                    {
                        string successSound = @"C:\Windows\Media\tada.wav";
                        string failureSound = @"C:\Windows\Media\Windows Critical Stop.wav";
                        string notifySound = @"C:\Windows\Media\Windows Notify.wav";
                        string exclamationSound = @"C:\Windows\Media\Windows Exclamation.wav";

                        string soundFile = success ? successSound : failureSound;

                        if (!File.Exists(soundFile))
                        {
                            soundFile = success ? notifySound : exclamationSound;
                        }

                        if (File.Exists(soundFile))
                        {
                            player.SoundLocation = soundFile;
                            player.Play();
                            Logger.Debug($"Played sound: {soundFile}");
                        }
                        else
                        {
                            Logger.Warning($"Could not find sound file to play: {soundFile}");
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Warning($"Failed to play completion sound: {ex.Message}"); }
        }

        static bool InitializeSteam(string username, string password, bool isDoubleClick = false)
        {
            if (!DepotDumper.Config.UseQrCode)
            {
                bool needPassword = username != null && password == null && (!DepotDumper.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username));
                if (needPassword)
                {
                    do
                    {
                        Console.Write("Enter account password for \"{0}\": ", username);
                        password = Console.IsInputRedirected ? Console.ReadLine() : Util.ReadPassword();
                        Console.WriteLine();
                    } while (string.IsNullOrEmpty(password));
                }
                else if (username == null)
                {
                    Logger.Info("No username given. Using anonymous account.");
                    Console.WriteLine("No username given. Using anonymous account.");
                }
            }

            bool result = DepotDumper.InitializeSteam3(username, password);

            if (!result && isDoubleClick)
            {
                Logger.Error("Failed to initialize Steam. Please check your configuration or credentials.");
                Console.WriteLine("Failed to initialize Steam. Please check your configuration or credentials.");
            }
            else if (!result)
            {
                Logger.Error("Failed to initialize Steam.");
            }

            return result;
        }

        static async Task ProcessMultipleAppsAsync(List<uint> appIds, bool select, string dumpPath, int maxConcurrent)
        {
            if (maxConcurrent <= 0) maxConcurrent = 1;
            Console.WriteLine($"Processing {appIds.Count} app IDs with concurrency level: {maxConcurrent}");
            Logger.Info($"Processing {appIds.Count} app IDs with concurrency level: {maxConcurrent}");

            using var semaphore = new SemaphoreSlim(maxConcurrent);
            var tasks = new List<Task>();
            var results = new ConcurrentDictionary<uint, bool>();

            foreach (uint appId in appIds)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    bool appSuccess = false;
                    List<string> appErrors = new List<string>();

                    try
                    {
                        Console.WriteLine($"-------------------------------------------");
                        Console.WriteLine($"Starting processing for app ID: {appId}");
                        Logger.Info($"Starting processing for app ID: {appId}");

                        // Get app name for tracking
                        string appName = "Unknown App";
                        try
                        {
                            await DepotDumper.steam3?.RequestAppInfo(appId);
                            appName = DepotDumper.GetAppName(appId);
                            if (string.IsNullOrEmpty(appName)) appName = $"Unknown App {appId}";
                        }
                        catch (Exception ex)
                        {
                            appErrors.Add($"Failed to get app name: {ex.Message}");
                            Logger.Warning($"Failed to get app name for {appId}: {ex.Message}");
                        }

                        // Start tracking this app
                        StatisticsTracker.TrackAppStart(appId, appName);

                        await DepotDumper.DumpAppAsync(select, appId).ConfigureAwait(false);
                        appSuccess = true;
                        Console.WriteLine($"Finished processing for app ID: {appId}");
                        Logger.Info($"Finished processing for app ID: {appId}");
                    }
                    catch (DepotDumperException ddEx)
                    {
                        appErrors.Add(ddEx.Message);
                        Logger.Error($"Error processing app ID {appId}: {ddEx.Message}");
                        Console.WriteLine($"Error processing app ID {appId}: {ddEx.Message}");
                        appSuccess = false;
                    }
                    catch (OperationCanceledException ocEx)
                    {
                        appErrors.Add(ocEx.Message);
                        Logger.Error($"Operation Canceled processing app ID {appId}: {ocEx.Message}");
                        Console.WriteLine($"Operation Canceled processing app ID {appId}: {ocEx.Message}");
                        appSuccess = false;
                    }
                    catch (Exception ex)
                    {
                        appErrors.Add(ex.ToString());
                        Logger.Error($"Unexpected error processing app ID {appId}: {ex.ToString()}");
                        Console.WriteLine($"Unexpected error processing app ID {appId}: {ex.Message}");
                        appSuccess = false;
                    }
                    finally
                    {
                        // Track app completion with success status and any errors that occurred
                        StatisticsTracker.TrackAppCompletion(appId, appSuccess, appErrors.Count > 0 ? appErrors : null);

                        results.TryAdd(appId, appSuccess);
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);

            int successCount = results.Count(r => r.Value);
            int failedCount = appIds.Count - successCount;
            Console.WriteLine($"-------------------------------------------");
            Console.WriteLine($"Batch processing complete. Processed {successCount} app(s) successfully, {failedCount} failed.");
            Logger.Info($"Batch processing complete. Processed {successCount} app(s) successfully, {failedCount} failed.");
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();
            var index = IndexOfParam(args, param);
            if (index == -1 || index == (args.Length - 1)) return list;

            index++;
            while (index < args.Length)
            {
                var strParam = args[index];
                if (strParam.StartsWith("-")) break;

                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    try { list.Add((T)converter.ConvertFromString(strParam)); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Could not parse value '{strParam}' for multi-value parameter '{param}' as {typeof(T).Name}. Skipping value. Error: {ex.Message}");
                        Logger.Warning($"Could not parse value '{strParam}' for multi-value parameter '{param}' as {typeof(T).Name}. Skipping value. Error: {ex.Message}");
                    }
                }
                index++;
            }
            return list;
        }

        static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("Usage - dumping all depots key in steam account:");
            Console.WriteLine("\tDepotDumper -username <username> -password <password> [options]");
            Console.WriteLine();
            Console.WriteLine("Required Account Options:");
            Console.WriteLine("\t-username <user>      \t- Your Steam username.");
            Console.WriteLine("\t-password <pass>      \t- Your Steam password. Not needed if using QR or remember-password.");
            Console.WriteLine();
            Console.WriteLine("Optional Login Options:");
            Console.WriteLine("\t-qr                   \t- Use QR code for login via Steam Mobile App.");
            Console.WriteLine("\t-remember-password    \t- Remember password/token for subsequent logins (stored locally).");
            Console.WriteLine("\t-loginid <#>          \t- Unique 32-bit ID if running multiple instances concurrently.");
            Console.WriteLine();
            Console.WriteLine("Dumping Options:");
            Console.WriteLine("\t-appid <#>            \t- Dump ONLY depots for this specific App ID.");
            Console.WriteLine("\t-appids-file <path>   \t- Dump ONLY depots for App IDs listed in the specified file (one per line).");
            Console.WriteLine("\t-select               \t- Interactively select depots to dump for each app.");
            Console.WriteLine();
            Console.WriteLine("Configuration & Output Options:");
            Console.WriteLine("\t-config <path>        \t- Use configuration from the specified JSON file.");
            Console.WriteLine("\t-save-config          \t- Save current command-line/config settings back to the config file.");
            Console.WriteLine("\t-dir <path>           \t- Directory to dump depots into (Default: 'dumps'). Also -dump-directory.");
            Console.WriteLine("\t-log-level <level>    \t- Set logging detail (debug, info, warning, error, critical. Default: info).");
            Console.WriteLine("\t-logs-dir <path>      \t- Directory to store log files (Default: dumps/logs).");
            Console.WriteLine("\t-generate-reports     \t- Generate HTML, JSON, CSV, TXT reports after completion.");
            Console.WriteLine("\t-reports-dir <path>   \t- Directory to store reports (Default: dumps/reports).");
            Console.WriteLine();
            Console.WriteLine("Performance Options:");
            Console.WriteLine("\t-max-downloads <#>    \t- Max concurrent manifest downloads per depot (Default: 4).");
            Console.WriteLine("\t-max-servers <#>      \t- Max CDN servers to use (Default: 20).");
            Console.WriteLine("\t-max-concurrent-apps <#>\t- Max apps to process concurrently if using -appids-file or config (Default: 1).");
            Console.WriteLine("\t-cellid <#>           \t- Specify Cell ID for connection (Default: Auto).");
            Console.WriteLine();
            Console.WriteLine("Other Options:");
            Console.WriteLine("\t-debug                \t- Enable verbose debug messages.");
            Console.WriteLine("\t-V | --version        \t- Show version information.");
            Console.WriteLine("\tconfig                \t- Enter configuration utility mode (e.g., DepotDumper config).");
            Console.WriteLine();
            Console.WriteLine("Note: When started with double-click (no arguments), DepotDumper will use settings from 'config.json'");
        }

        static void PrintVersion(bool printExtra = false)
        {
            var assembly = typeof(Program).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString();
            Console.WriteLine($"DepotDumper v{version}");
            if (!printExtra) return;
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
        }
    }
}