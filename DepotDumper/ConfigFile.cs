using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using System.Linq;
namespace DepotDumper
{
    public class ConfigFile
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public bool RememberPassword { get; set; } = false;
        public bool UseQrCode { get; set; } = false;
        public int CellID { get; set; } = 0;
        public int MaxDownloads { get; set; } = 16;  // Increased from 4
        public int MaxServers { get; set; } = 50;    // Increased from 20
        public uint? LoginID { get; set; } = null;
        public string DumpDirectory { get; set; } = "dumps";
        public bool UseNewNamingFormat { get; set; } = true;
        public int MaxConcurrentApps { get; set; } = Environment.ProcessorCount / 2;  // Dynamic based on system

        // Performance tuning settings
        public int ConnectionPoolSize { get; set; } = 20;
        public int RequestTimeout { get; set; } = 60; // seconds
        public bool EnableCheckpointing { get; set; } = true;
        public bool SkipExistingManifests { get; set; } = true;
        public int NetworkRetryCount { get; set; } = 5;
        public int NetworkRetryDelayMs { get; set; } = 500;
        public bool CompressManifests { get; set; } = true;
        public bool UseSharedCdnPools { get; set; } = true;
        public int FileBufferSizeKb { get; set; } = 64;

        public HashSet<uint> AppIdsToProcess { get; set; } = new HashSet<uint>();
        public HashSet<uint> ExcludedAppIds { get; set; } = new HashSet<uint>();

        [JsonIgnore]
        public Dictionary<uint, bool> AppIDs
        {
            get
            {
                var result = new Dictionary<uint, bool>();
                foreach (var appId in AppIdsToProcess)
                {
                    result[appId] = !ExcludedAppIds.Contains(appId);
                }
                return result;
            }
        }

        private static readonly string DefaultConfigPath = Path.Combine(
            AppContext.BaseDirectory,
            "config.json");

        // Cache loaded config
        private static ConfigFile cachedConfig = null;
        private static string cachedConfigPath = null;

        public static ConfigFile Load()
        {
            return Load(DefaultConfigPath);
        }

        public static ConfigFile Load(string path)
        {
            // Return cached config if already loaded from this path
            if (cachedConfig != null && path == cachedConfigPath)
            {
                return cachedConfig;
            }

            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Logger.Warning("Configuration load path was null or empty, using default in-memory config.");
                    cachedConfig = new ConfigFile();
                    cachedConfigPath = null;
                    return cachedConfig;
                }

                if (!File.Exists(path))
                {
                    Logger.Info($"Configuration file not found at '{path}'. Using default settings.");
                    cachedConfig = new ConfigFile();
                    cachedConfigPath = path;
                    return cachedConfig;
                }

                Logger.Info($"Loading configuration from '{path}'.");
                string json = File.ReadAllText(path);

                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                if (string.IsNullOrWhiteSpace(json))
                {
                    Logger.Warning($"Configuration file at '{path}' is empty. Using default settings.");
                    cachedConfig = new ConfigFile();
                    cachedConfigPath = path;
                    return cachedConfig;
                }

                var loadedConfig = JsonSerializer.Deserialize<ConfigFile>(json, options) ?? new ConfigFile();

                // Apply runtime adjustments
                ApplyRuntimeAdjustments(loadedConfig);

                cachedConfig = loadedConfig;
                cachedConfigPath = path;
                return loadedConfig;
            }
            catch (JsonException jsonEx)
            {
                Logger.Error($"Error parsing configuration file '{path}': {jsonEx.Message}. Using default settings.");
                cachedConfig = new ConfigFile();
                cachedConfigPath = null;
                return cachedConfig;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading configuration file '{path}': {ex.Message}. Using default settings.");
                cachedConfig = new ConfigFile();
                cachedConfigPath = null;
                return cachedConfig;
            }
        }

        // Apply runtime adjustments to config
        private static void ApplyRuntimeAdjustments(ConfigFile config)
        {
            // Set MaxConcurrentApps based on system if not explicitly configured
            if (config.MaxConcurrentApps <= 0)
            {
                config.MaxConcurrentApps = Math.Max(1, Environment.ProcessorCount / 2);
                Logger.Info($"Auto-adjusted MaxConcurrentApps to {config.MaxConcurrentApps} based on system cores");
            }

            // Cap MaxServers at a reasonable value
            if (config.MaxServers > 100)
            {
                Logger.Warning($"MaxServers value of {config.MaxServers} is too high, capping at 100");
                config.MaxServers = 100;
            }

            // Check values are reasonable
            if (config.MaxDownloads <= 0)
            {
                config.MaxDownloads = 16;
                Logger.Warning($"Invalid MaxDownloads value, using default: {config.MaxDownloads}");
            }

            // Set buffer size based on memory
            try
            {
                long memoryMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
                if (memoryMB > 16000) // 16GB+
                {
                    config.FileBufferSizeKb = 256;
                }
                else if (memoryMB > 8000) // 8GB+
                {
                    config.FileBufferSizeKb = 128;
                }
                else if (memoryMB > 4000) // 4GB+
                {
                    config.FileBufferSizeKb = 64;
                }
                else // Low memory
                {
                    config.FileBufferSizeKb = 32;
                }
                Logger.Debug($"Auto-adjusted FileBufferSizeKb to {config.FileBufferSizeKb} based on system memory");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to auto-adjust buffer size: {ex.Message}");
            }
        }

        public void Save()
        {
            Save(DefaultConfigPath);
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                string errorMessage = "Error saving configuration: The provided save path was null or empty.";
                Console.WriteLine(errorMessage);
                Logger.Error(errorMessage);
                if (!string.IsNullOrEmpty(DefaultConfigPath))
                {
                    path = DefaultConfigPath;
                    Logger.Info($"Using default path instead: {DefaultConfigPath}");
                }
                else
                {
                    return;
                }
            }

            try
            {
                string directoryPath = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
                {
                    Logger.Info($"Creating directory for config file: {directoryPath}");
                    Directory.CreateDirectory(directoryPath);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(path, json);

                Console.WriteLine($"Configuration saved to {path}");
                Logger.Info($"Configuration saved to {path}");

                // Update cache
                cachedConfig = this;
                cachedConfigPath = path;
            }
            catch (Exception ex)
            {
                string errorMessage = $"Error saving configuration file to '{path}': {ex.Message}";
                Console.WriteLine(errorMessage);
                Logger.Error($"{errorMessage} - StackTrace: {ex.StackTrace}");
            }
        }

        public void ApplyToDepotDumperConfig()
        {
            DepotDumper.Config ??= new DumpConfig();
            DepotDumper.Config.RememberPassword = this.RememberPassword;
            DepotDumper.Config.UseQrCode = this.UseQrCode;
            DepotDumper.Config.CellID = this.CellID;
            DepotDumper.Config.MaxDownloads = this.MaxDownloads;
            DepotDumper.Config.MaxServers = this.MaxServers;
            DepotDumper.Config.LoginID = this.LoginID;
            DepotDumper.Config.DumpDirectory = this.DumpDirectory;
            DepotDumper.Config.UseNewNamingFormat = this.UseNewNamingFormat;

            // Also apply performance settings if they exist in DepotDumper
            var dumpConfigType = typeof(DumpConfig);

            // Apply dynamic properties using reflection for future compatibility
            foreach (var property in GetType().GetProperties())
            {
                if (property.Name == nameof(AppIdsToProcess) ||
                    property.Name == nameof(ExcludedAppIds) ||
                    property.Name == nameof(AppIDs))
                {
                    continue; // Skip collection properties
                }

                var targetProperty = dumpConfigType.GetProperty(property.Name);
                if (targetProperty != null && targetProperty.CanWrite)
                {
                    try
                    {
                        var value = property.GetValue(this);
                        targetProperty.SetValue(DepotDumper.Config, value);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not apply config property {property.Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}