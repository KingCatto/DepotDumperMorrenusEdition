using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
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
        // Performance metrics tracking
        private static readonly ConcurrentDictionary<string, Stopwatch> performanceMetrics =
            new ConcurrentDictionary<string, Stopwatch>();
        private static int appProcessCounter = 0;

        // Global pool of CDN clients for reuse across app processing
        private static readonly ConcurrentDictionary<uint, CDNClientPool> globalCdnPools =
            new ConcurrentDictionary<uint, CDNClientPool>();

        // Improved parameter handling methods
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

        // Get multiple parameter values
        public static List<T> GetParameterList<T>(string[] args, string param)
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
                if (converter != null && converter.CanConvertFrom(typeof(string)))
                {
                    try { list.Add((T)converter.ConvertFromString(strParam)); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Could not parse value '{strParam}' for multi-value parameter '{param}' as {typeof(T).Name}. Skipping value. Error: {ex.Message}");
                        Logger.Warning($"Could not parse value '{strParam}' for multi-value parameter '{param}' as {typeof(T).Name}. Skipping value. Error: {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warning($"No converter found to parse value '{strParam}' for multi-value parameter '{param}' as {typeof(T).Name}. Skipping value.");
                }
                index++;
            }
            return list;
        }

        // Performance tracking methods
        private static void StartMetric(string name)
        {
            var sw = new Stopwatch();
            sw.Start();
            performanceMetrics[name] = sw;
        }

        private static TimeSpan StopMetric(string name)
        {
            if (performanceMetrics.TryRemove(name, out var sw))
            {
                sw.Stop();
                return sw.Elapsed;
            }
            return TimeSpan.Zero;
        }

        private static void LogMetric(string name)
        {
            if (performanceMetrics.TryGetValue(name, out var sw))
            {
                Logger.Debug($"Performance: {name} - {sw.Elapsed.TotalSeconds:F2}s");
            }
        }

        static async Task<int> Main(string[] args)
        {
            string configPathArg = null;
            ConfigFile config = null;

            // Initialize statistics tracking
            StatisticsTracker.Initialize();

            bool isDoubleClick = args.Length == 0;
            if (isDoubleClick)
            {
                Console.WriteLine("DepotDumper started via double-click...");
                string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
                config = ConfigFile.Load(configPath);
                config.ApplyToDepotDumperConfig();

                List<string> effectiveArgs = new List<string>();
                if (!string.IsNullOrWhiteSpace(configPath))
                {
                    effectiveArgs.AddRange(new[] { "-config", configPath });
                }

                string[] originalArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                if (!HasParameter(originalArgs, "-generate-reports") && !HasParameter(args, "-generate-reports"))
                {
                    effectiveArgs.Add("-generate-reports");
                }

                args = effectiveArgs.ToArray();
                Console.WriteLine($"Loading configuration from: {configPath}");

                if (HasParameter(args, "-generate-reports"))
                {
                    Console.WriteLine("Output will be saved to reports directory.");
                }

                Console.WriteLine();
            }

            if (args.Length > 0 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                return 0;
            }

            if (args.Length >= 1 && args[0].ToLower() == "config")
            {
                int result = ConfigCommand.Run(args);
                if (isDoubleClick) { Console.WriteLine("\nPress any key to exit..."); Console.ReadKey(); }
                return result;
            }

            // Set up our performance metrics
            var totalRunStopwatch = new Stopwatch();
            totalRunStopwatch.Start();

            // Process configuration
            configPathArg = GetParameter<string>(args, "-config");
            string defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            string pathToLoad = configPathArg ?? defaultConfigPath;
            bool loadConfigFromFile = !string.IsNullOrEmpty(configPathArg) || File.Exists(defaultConfigPath);

            config = loadConfigFromFile
                 ? ConfigFile.Load(pathToLoad)
                 : new ConfigFile();

            config.MergeCommandLineParameters(args);
            config.ApplyToDepotDumperConfig();

            // Initialize systems
            Ansi.Init();
            DebugLog.Enabled = false;
            AccountSettingsStore.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "account.config"));

            // Set up output directories
            bool generateReports = HasParameter(args, "-generate-reports");
            string dumpPath = string.IsNullOrWhiteSpace(DepotDumper.Config.DumpDirectory) ? DepotDumper.DEFAULT_DUMP_DIR : DepotDumper.Config.DumpDirectory;
            try { Directory.CreateDirectory(dumpPath); } catch (Exception ex) { Console.WriteLine($"Warning: Could not create dump directory '{dumpPath}': {ex.Message}"); }

            string reportsDirectory = GetParameter<string>(args, "-reports-dir") ?? Path.Combine(dumpPath, "reports");
            string logsDirectory = GetParameter<string>(args, "-logs-dir") ?? Path.Combine(dumpPath, "logs");

            // Configure logging
            LogLevel logLevel = LogLevel.Info;
            if (HasParameter(args, "-log-level"))
            {
                string levelStr = GetParameter<string>(args, "-log-level", "info").ToLower();
                if (!Enum.TryParse<LogLevel>(levelStr, true, out logLevel))
                {
                    logLevel = LogLevel.Info;
                    Console.WriteLine($"Warning: Invalid log level '{levelStr}'. Defaulting to Info.");
                }
            }

            try { Directory.CreateDirectory(logsDirectory); } catch (Exception ex) { Console.WriteLine($"Warning: Could not create logs directory '{logsDirectory}': {ex.Message}"); }
            string logFilePath = Path.Combine(logsDirectory, $"depotdumper_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            // Initialize logger
            Logger.Initialize(logFilePath, logLevel, toConsole: !Console.IsOutputRedirected, toFile: true);

            // Initialize checkpoint system if enabled
            if (DepotDumper.Config.EnableCheckpointing)
            {
                CheckpointManager.Initialize(dumpPath);
                Logger.Info("Checkpoint system initialized");
            }

            // Initialize file operations
            FileOperations.Initialize();

            if (HasParameter(args, "-debug") || logLevel == LogLevel.Debug)
            {
                PrintVersion(true);
                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) => { Logger.Debug($"[{category}] {message}"); });
                Logger.Info("Debug logging enabled.");
            }

            bool saveConfig = HasParameter(args, "-save-config");
            var username = config.Username;
            var password = config.Password;
            uint specificAppId = GetParameter(args, "-appid", DepotDumper.INVALID_APP_ID);
            string appIdsFilePath = GetParameter<string>(args, "-appids-file");

            List<uint> appIdsList = new List<uint>();
            bool processAll = false;

            if (!string.IsNullOrEmpty(appIdsFilePath))
            {
                if (!File.Exists(appIdsFilePath))
                {
                    Logger.Error($"Error: App IDs file not found: {appIdsFilePath}");
                    return 1;
                }

                try
                {
                    string[] appIdLines = File.ReadAllLines(appIdsFilePath);
                    foreach (string line in appIdLines)
                    {
                        string trimmedLine = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#"))
                            continue;

                        if (uint.TryParse(trimmedLine, out uint parsedAppId))
                        {
                            appIdsList.Add(parsedAppId);
                        }
                        else
                        {
                            Logger.Warning($"Invalid App ID in file: {trimmedLine}");
                        }
                    }

                    Logger.Info($"Loaded {appIdsList.Count} App IDs from file: {appIdsFilePath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error reading app IDs file: {ex.Message}");
                    return 1;
                }
            }
            else if (specificAppId != DepotDumper.INVALID_APP_ID)
            {
                Logger.Info($"Processing specific AppID: {specificAppId} from command line.");
                appIdsList.Add(specificAppId);

                if (!config.AppIdsToProcess.Contains(specificAppId))
                {
                    config.AppIdsToProcess.Add(specificAppId);
                }
            }
            else if (config.AppIdsToProcess != null && config.AppIdsToProcess.Any())
            {
                appIdsList.AddRange(config.AppIdsToProcess.Where(id => !config.ExcludedAppIds.Contains(id)));

                if (appIdsList.Count > 0)
                {
                    Logger.Info($"Processing {appIdsList.Count} App IDs from configuration file.");
                }
                else if (config.AppIdsToProcess.Any())
                {
                    Logger.Warning("All App IDs specified in the configuration are excluded.");
                    return 1;
                }
                else
                {
                    Logger.Info("AppIdsToProcess is empty in config. Processing all apps from licenses.");
                    processAll = true;
                }
            }
            else
            {
                Logger.Info("No specific App IDs provided. Processing all apps from licenses.");
                processAll = true;
            }

            int exitCode = 0;
            bool success = false;

            StartMetric("total_execution");

            try
            {
                if (InitializeSteam(username, ref password, isDoubleClick))
                {
                    StartMetric("processing_time");

                    if (processAll)
                    {
                        Console.WriteLine("Waiting for license list...");
                        Logger.Info("Waiting for license list callback...");

                        try
                        {
                            if (DepotDumper.steam3 == null)
                                throw new InvalidOperationException("Steam3 session is not initialized.");

                            await DepotDumper.steam3.LicenseListReady.WaitAsync(TimeSpan.FromSeconds(45));

                            if (DepotDumper.steam3.Licenses == null)
                            {
                                throw new InvalidOperationException("License list task completed but license list is still null.");
                            }

                            Console.WriteLine("License list received.");
                            Logger.Info($"License list ready. Found {DepotDumper.steam3.Licenses.Count} licenses.");
                        }
                        catch (TimeoutException)
                        {
                            Console.WriteLine("Error getting license list: Timed out waiting for license list from Steam.");
                            Logger.Error("Failed to get license list: Timed out.");
                            StatisticsTracker.TrackError("Failed to get license list: Timeout");
                            exitCode = 1;
                            success = false;
                            processAll = false;
                            appIdsList.Clear();
                        }
                        catch (Exception licenseEx)
                        {
                            Console.WriteLine($"Error getting license list: {licenseEx.Message}");
                            Logger.Error($"Failed to get license list: {licenseEx.ToString()}");
                            StatisticsTracker.TrackError($"Failed to get license list: {licenseEx.Message}");
                            exitCode = 1;
                            success = false;
                            processAll = false;
                            appIdsList.Clear();
                        }
                    }

                    if (exitCode == 0)
                    {
                        try
                        {
                            bool select = HasParameter(args, "-select");

                            if (appIdsList.Count > 0)
                            {
                                await ProcessMultipleAppsAsync(appIdsList, select, dumpPath, config.MaxConcurrentApps);
                                success = true;
                            }
                            else if (processAll)
                            {
                                if (DepotDumper.steam3 != null && DepotDumper.steam3.IsLoggedOn)
                                {
                                    await DepotDumper.DumpAppAsync(select).ConfigureAwait(false);
                                    success = true;
                                }
                                else
                                {
                                    Logger.Error("Cannot process all apps: Steam3 session is not valid or not logged on.");
                                    throw new InvalidOperationException("Steam session lost before processing all apps.");
                                }
                            }
                            else
                            {
                                Logger.Warning("No apps identified for processing.");
                                Console.WriteLine("No apps to process.");
                                success = true;
                            }

                            if (saveConfig)
                            {
                                Logger.Info("Saving configuration as requested by -save-config flag.");
                                config.Password = password;
                                config.Username = DepotDumper.steam3?.LoggedInUsername ?? config.Username;
                                config.RememberPassword = DepotDumper.Config.RememberPassword;
                                config.Save();
                            }
                        }
                        catch (DepotDumperException ddEx)
                        {
                            StatisticsTracker.TrackError(ddEx.Message);
                            Logger.Error(ddEx.Message);
                            Console.WriteLine(ddEx.Message);
                            exitCode = 1;
                            success = false;
                        }
                        catch (OperationCanceledException ocEx)
                        {
                            StatisticsTracker.TrackError($"Operation Canceled: {ocEx.Message}");
                            Logger.Error($"Operation Canceled: {ocEx.Message}");
                            Console.WriteLine($"Operation Canceled: {ocEx.Message}");
                            exitCode = 1;
                            success = false;
                        }
                        catch (Exception e)
                        {
                            StatisticsTracker.TrackError($"Unhandled exception: {e.ToString()}");
                            Logger.Critical($"Unhandled exception during processing: {e.ToString()}");
                            Console.WriteLine($"Processing failed due to an unhandled exception: {e.Message}");
#if DEBUG
                            Console.WriteLine(e.ToString());
#endif
                            exitCode = 1;
                            success = false;
                        }
                    }

                    var processingTime = StopMetric("processing_time");
                    Logger.Info($"Processing completed in {processingTime.TotalSeconds:F1}s");

                    try
                    {
                        if (generateReports)
                        {
                            StartMetric("report_generation");

                            try
                            {
                                StatisticsTracker.PrintSummary();
                                var summary = StatisticsTracker.GetSummary();
                                ReportGenerator.SaveAllReports(summary, reportsDirectory);

                                var reportTime = StopMetric("report_generation");
                                Logger.Info($"Reports generated successfully in {reportsDirectory} ({reportTime.TotalSeconds:F1}s)");
                                Console.WriteLine($"Reports generated successfully in {reportsDirectory}");
                            }
                            catch (Exception reportEx)
                            {
                                StopMetric("report_generation");
                                Logger.Error($"Failed to generate reports: {reportEx.ToString()}");
                                Console.WriteLine($"Failed to generate reports: {reportEx.Message}");
                            }
                        }
                    }
                    finally
                    {
                        // Clean up resources
                        CleanupResources();

                        if (isDoubleClick)
                        {
                            PlayCompletionSound(success);
                        }
                    }
                }
                else
                {
                    StatisticsTracker.TrackError("Steam initialization failed");
                    Logger.Critical("Error: Steam initialization failed.");
                    Console.WriteLine("Error: InitializeSteam failed");
                    exitCode = 1;
                }
            }
            finally
            {
                totalRunStopwatch.Stop();
                Logger.Info($"Total execution time: {totalRunStopwatch.Elapsed.TotalSeconds:F1}s");
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
                        string notifySound = @"C:\Windows\Media\Windows Notify System Generic.wav";
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
                            Logger.Debug($"Played sound: {Path.GetFileName(soundFile)}");
                        }
                        else
                        {
                            Logger.Warning($"Could not find completion sound file: {soundFile}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to play completion sound: {ex.Message}");
            }
        }

        // Improved Steam initialization
        static bool InitializeSteam(string username, ref string password, bool isDoubleClick = false)
        {
            string metricName = "steam_initialization";
            StartMetric(metricName);

            if (!DepotDumper.Config.UseQrCode)
            {
                bool hasLoginToken = username != null && AccountSettingsStore.Instance.LoginTokens.ContainsKey(username);
                bool needPassword = username != null && string.IsNullOrEmpty(password) && (!DepotDumper.Config.RememberPassword || !hasLoginToken);

                if (needPassword)
                {
                    do
                    {
                        Console.Write("Enter account password for \"{0}\": ", username);
                        password = Console.IsInputRedirected ? Console.ReadLine() : Util.ReadPassword();
                        Console.WriteLine();
                    } while (string.IsNullOrEmpty(password));
                    Logger.Info("Password obtained from user prompt.");
                }
                else if (username == null && !hasLoginToken)
                {
                    Logger.Info("No username given or usable token found. Using anonymous account.");
                    Console.WriteLine("No username given. Using anonymous account.");
                    username = null;
                    password = null;
                }
                else if (hasLoginToken && DepotDumper.Config.RememberPassword)
                {
                    Logger.Info($"Using remembered login token for user '{username}'.");
                    Console.WriteLine($"Using remembered login token for user '{username}'.");
                    password = null;
                }
                else if (!string.IsNullOrEmpty(password))
                {
                    Logger.Debug("Password provided, will attempt password login.");
                }
            }
            else
            {
                Logger.Info("QR Code login selected.");
                Console.WriteLine("Attempting login using QR Code...");
                username = null;
                password = null;
            }

            // Clear any existing Steam3 session
            DepotDumper.ShutdownSteam3();

            SteamUser.LogOnDetails details = new SteamUser.LogOnDetails
            {
                Username = username,
                Password = password,
                ShouldRememberPassword = DepotDumper.Config.RememberPassword,
                AccessToken = (username != null && AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out var token)) ? token : null,
                LoginID = DepotDumper.Config.LoginID ?? 0x534B32,
            };

            try
            {
                // Initialize Steam with timeout
                using var initTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var initTask = Task.Run(() =>
                {
                    DepotDumper.steam3 = new Steam3Session(details);
                    return DepotDumper.steam3.WaitForCredentials();
                }, initTimeoutCts.Token);

                try
                {
                    bool success = initTask.GetAwaiter().GetResult();

                    if (!success)
                    {
                        Console.WriteLine("Unable to get steam3 credentials.");
                        Logger.Error("Unable to get steam3 credentials after session initialization.");
                        return false;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Timed out waiting for Steam credentials (2 minutes).");
                    Logger.Error("Timed out waiting for Steam credentials (2 minutes).");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during Steam3Session initialization or credential wait: {ex.Message}");
                Logger.Critical($"Exception during Steam3Session setup: {ex.ToString()}");
                return false;
            }

            var elapsed = StopMetric(metricName);
            Logger.Info($"Steam3 initialized and credentials obtained successfully in {elapsed.TotalSeconds:F1}s");

            _ = Task.Run(DepotDumper.steam3.TickCallbacks);
            return true;
        }

        // Improved process multiple apps logic with batching and smarter parallelism
        static async Task ProcessMultipleAppsAsync(List<uint> appIds, bool select, string dumpPath, int maxConcurrent)
        {
            // Auto-adjust concurrency based on system resources if not specified
            if (maxConcurrent <= 0)
            {
                // Use 75% of available processors, min 1, max 8
                maxConcurrent = Math.Min(8, Math.Max(1, Environment.ProcessorCount * 3 / 4));
                Logger.Info($"Auto-adjusted concurrency to {maxConcurrent} based on CPU cores");
            }

            Console.WriteLine($"Processing {appIds.Count} app IDs with concurrency level: {maxConcurrent}");
            Logger.Info($"Processing {appIds.Count} app IDs with concurrency level: {maxConcurrent}");

            // First pass - group apps by their parent IDs to avoid duplicating work
            var appGroups = new Dictionary<uint, List<uint>>();
            var parentLookupTasks = new List<Task>();
            var parentAppLock = new object();

            // Use a semaphore to limit concurrent parent lookup operations
            using var parentLookupSemaphore = new SemaphoreSlim(maxConcurrent * 2);

            foreach (uint appId in appIds)
            {
                await parentLookupSemaphore.WaitAsync();
                parentLookupTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Skip if already processed in a previous run via checkpoint
                        if (DepotDumper.Config.EnableCheckpointing &&
                            CheckpointManager.IsAppProcessed(appId, out bool wasSuccessful))
                        {
                            Logger.Info($"Skipping app {appId} as it was already processed (Success: {wasSuccessful})");
                            return;
                        }

                        // Detect if this is a DLC and get its parent app
                        var (isDlc, parentId) = await DlcDetection.DetectDlcAndParentAsync(DepotDumper.steam3, appId);

                        lock (parentAppLock)
                        {
                            if (!appGroups.ContainsKey(parentId))
                                appGroups[parentId] = new List<uint>();

                            appGroups[parentId].Add(appId);
                        }

                        Logger.Debug($"App {appId} assigned to parent group {parentId}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Error during parent detection for app {appId}: {ex.Message}");

                        // If we fail to detect parent, treat the app as its own parent
                        lock (parentAppLock)
                        {
                            if (!appGroups.ContainsKey(appId))
                                appGroups[appId] = new List<uint>();

                            appGroups[appId].Add(appId);
                        }
                    }
                    finally
                    {
                        parentLookupSemaphore.Release();
                    }
                }));
            }

            // Wait for all parent lookups to complete
            await Task.WhenAll(parentLookupTasks);

            // Log grouping results
            Logger.Info($"Grouped {appIds.Count} apps into {appGroups.Count} parent groups");
            foreach (var group in appGroups)
            {
                Logger.Debug($"Parent group {group.Key}: {group.Value.Count} apps");
            }

            // Process parent app groups in parallel with smart scheduling
            using var processSemaphore = new SemaphoreSlim(maxConcurrent);
            var tasks = new List<Task>();
            var results = new ConcurrentDictionary<uint, bool>();

            // Process larger groups first for better load balancing
            foreach (var group in appGroups.OrderByDescending(g => g.Value.Count))
            {
                uint parentId = group.Key;
                var groupApps = group.Value;

                await processSemaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    string metricName = $"app_group_{parentId}";
                    StartMetric(metricName);
                    int localAppCounter = Interlocked.Increment(ref appProcessCounter);

                    try
                    {
                        Console.WriteLine($"-------------------------------------------");
                        Console.WriteLine($"[Group {localAppCounter}] Starting processing for parent app {parentId} with {groupApps.Count} related apps");
                        Logger.Info($"Starting processing for parent app {parentId} with {groupApps.Count} related apps");

                        // Make sure we have app info for the parent
                        try
                        {
                            await DepotDumper.steam3.RequestAppInfo(parentId);
                            string parentAppName = DepotDumper.GetAppName(parentId);
                            if (string.IsNullOrEmpty(parentAppName))
                                parentAppName = $"Unknown App {parentId}";

                            StatisticsTracker.TrackAppStart(parentId, parentAppName);

                            // Create parent directory
                            string parentDir = Path.Combine(dumpPath, parentId.ToString());
                            Directory.CreateDirectory(parentDir);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to get parent app info for {parentId}: {ex.Message}");
                        }

                        // Process all apps in this group
                        var groupResults = await ProcessAppGroupAsync(parentId, groupApps, select, dumpPath);

                        foreach (var result in groupResults)
                        {
                            results.TryAdd(result.Key, result.Value);
                        }

                        TimeSpan elapsed = StopMetric(metricName);

                        Console.WriteLine($"[Group {localAppCounter}] Finished processing parent app {parentId} with {groupApps.Count} related apps in {elapsed.TotalSeconds:F1}s");
                        Logger.Info($"Finished processing parent app {parentId} with {groupApps.Count} related apps in {elapsed.TotalSeconds:F1}s");
                    }
                    catch (Exception ex)
                    {
                        StopMetric(metricName);
                        Logger.Error($"Error processing parent app {parentId}: {ex.Message}");

                        foreach (var appId in groupApps)
                        {
                            results.TryAdd(appId, false);
                            StatisticsTracker.TrackAppCompletion(appId, false, new List<string> { $"Parent group processing error: {ex.Message}" });
                        }
                    }
                    finally
                    {
                        processSemaphore.Release();
                    }
                }));
            }

            // Wait for all groups to complete
            await Task.WhenAll(tasks);

            // Summarize results
            int successCount = results.Count(r => r.Value);
            int failedCount = appIds.Count - successCount;

            Console.WriteLine($"-------------------------------------------");
            Console.WriteLine($"Batch processing complete. Processed {successCount} app(s) successfully, {failedCount} failed.");
            Logger.Info($"Batch processing complete. Processed {successCount} app(s) successfully, {failedCount} failed.");
        }

        // Process a group of apps that share the same parent
        private static async Task<Dictionary<uint, bool>> ProcessAppGroupAsync(
            uint parentId, List<uint> groupApps, bool select, string dumpPath)
        {
            Dictionary<uint, bool> results = new Dictionary<uint, bool>();
            Dictionary<uint, string> appDlcInfo = null;

            string metricName = $"app_dlc_info_{parentId}";
            StartMetric(metricName);

            try
            {
                // Fetch DLC info for parent app once to reuse across all group apps
                Logger.Debug($"Fetching DLC info for parent app {parentId} once.");
                appDlcInfo = await DepotDumper.GetAppDlcInfoAsync(parentId);
                Logger.Debug($"Fetched {appDlcInfo.Count} DLCs (without depots) for parent app {parentId}.");

                var elapsed = StopMetric(metricName);
                Logger.Debug($"DLC info fetched for parent app {parentId} in {elapsed.TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                StopMetric(metricName);
                Logger.Warning($"Error fetching DLC info for parent app {parentId}: {ex.Message}");
                appDlcInfo = new Dictionary<uint, string>();
            }

            // Get or create CDN client pool for this parent app (reused across depots)
            CDNClientPool sharedCdnPool = globalCdnPools.GetOrAdd(parentId, id =>
            {
                Logger.Info($"Creating shared CDN pool for parent app {id}");
                return new CDNClientPool(DepotDumper.steam3, id);
            });

            // Process each app in the group
            foreach (var appId in groupApps)
            {
                bool appSuccess = false;
                List<string> appErrors = new List<string>();

                metricName = $"app_processing_{appId}";
                StartMetric(metricName);

                try
                {
                    // Skip if already processed in a previous run via checkpoint
                    if (DepotDumper.Config.EnableCheckpointing &&
                        CheckpointManager.IsAppProcessed(appId, out bool wasSuccessful))
                    {
                        Logger.Info($"Skipping app {appId} as it was already processed (Success: {wasSuccessful})");
                        results[appId] = wasSuccessful;
                        continue;
                    }

                    Console.WriteLine($"Processing app {appId} (Parent: {parentId})");
                    Logger.Info($"Processing app {appId} (Parent: {parentId})");

                    string appName = "Unknown App";
                    try
                    {
                        // Get app name if not already loaded
                        await DepotDumper.steam3.RequestAppInfo(appId);
                        appName = DepotDumper.GetAppName(appId);
                        if (string.IsNullOrEmpty(appName))
                            appName = $"Unknown App {appId}";
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to get app name for {appId}: {ex.Message}");
                        appErrors.Add($"Failed to get app name: {ex.Message}");
                    }

                    // Start tracking this app
                    StatisticsTracker.TrackAppStart(appId, appName);

                    // Use shared CDN pool for this app
                    DepotDumper.SetSharedCdnPool(sharedCdnPool);

                    // Process the app
                    await DepotDumper.DumpAppAsync(select, appId, appDlcInfo);

                    appSuccess = true;
                    results[appId] = true;

                    // Update checkpoint if enabled
                    if (DepotDumper.Config.EnableCheckpointing)
                    {
                        CheckpointManager.MarkAppProcessed(appId, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error processing app {appId}: {ex.Message}");
                    appErrors.Add($"Processing error: {ex.Message}");
                    results[appId] = false;
                    appSuccess = false;

                    // Update checkpoint if enabled
                    if (DepotDumper.Config.EnableCheckpointing)
                    {
                        CheckpointManager.MarkAppProcessed(appId, false);
                    }
                }
                finally
                {
                    var elapsed = StopMetric(metricName);

                    // Track completion 
                    StatisticsTracker.TrackAppCompletion(appId, appSuccess, appErrors.Count > 0 ? appErrors : null);

                    Logger.Info($"Finished processing app {appId} in {elapsed.TotalSeconds:F2}s. Success: {appSuccess}");
                }
            }

            return results;
        }

        // Cleanup all resources
        static void CleanupResources()
        {
            // Shutdown all CDN pools
            foreach (var poolEntry in globalCdnPools)
            {
                try
                {
                    Logger.Debug($"Shutting down CDN pool for app {poolEntry.Key}");
                    poolEntry.Value.Shutdown();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error shutting down CDN pool for app {poolEntry.Key}: {ex.Message}");
                }
            }

            globalCdnPools.Clear();

            // Flush file buffers
            try
            {
                Util.FlushAllFileBuffers();
                Logger.Debug("Flushed all file buffers");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error flushing file buffers: {ex.Message}");
            }

            // Save checkpoint data if enabled
            if (DepotDumper.Config.EnableCheckpointing)
            {
                try
                {
                    CheckpointManager.Shutdown();
                    Logger.Debug("Checkpoint system shutdown completed");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error during checkpoint shutdown: {ex.Message}");
                }
            }

            // Close file operations
            try
            {
                FileOperations.Shutdown();
                Logger.Debug("File operations shutdown completed");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error during file operations shutdown: {ex.Message}");
            }

            // Shutdown Steam3
            DepotDumper.ShutdownSteam3();
            Logger.Info("All resources cleaned up successfully");
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
            Console.WriteLine("\t-config <path>        \t- Use configuration from the specified JSON file (Default: config.json).");
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
            Console.WriteLine("      located in the same directory as the executable.");
        }
        static void PrintVersion(bool printExtra = false)
        {
            var assembly = typeof(Program).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";
            Console.WriteLine($"DepotDumper v{version}");
            if (!printExtra) return;
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}");
            Console.WriteLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        }
    }
}