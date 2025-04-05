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
        public int MaxDownloads { get; set; } = 4;
        public int MaxServers { get; set; } = 20;
        public uint? LoginID { get; set; } = null;
        public string DumpDirectory { get; set; } = "dumps";
        public bool UseNewNamingFormat { get; set; } = true;
        public int MaxConcurrentApps { get; set; } = 1;
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
        public static ConfigFile Load()
        {
            return Load(DefaultConfigPath);
        }
        public static ConfigFile Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    Logger.Warning("Configuration load path was null or empty, using default in-memory config.");
                    return new ConfigFile();
                }
                if (!File.Exists(path))
                {
                    Logger.Info($"Configuration file not found at '{path}'. Using default settings.");
                    return new ConfigFile();
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
                    return new ConfigFile();
                }
                return JsonSerializer.Deserialize<ConfigFile>(json, options) ?? new ConfigFile();
            }
            catch (JsonException jsonEx)
            {
                Logger.Error($"Error parsing configuration file '{path}': {jsonEx.Message}. Using default settings.");
                return new ConfigFile();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading configuration file '{path}': {ex.Message}. Using default settings.");
                return new ConfigFile();
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
        }
        public void MergeCommandLineParameters(string[] args)
        {
            if (Program.HasParameter(args, "-username") || Program.HasParameter(args, "-user"))
            {
                Username = Program.GetParameter<string>(args, "-username") ?? Program.GetParameter<string>(args, "-user");
            }
            if (Program.HasParameter(args, "-password") || Program.HasParameter(args, "-pass"))
            {
                Password = Program.GetParameter<string>(args, "-password") ?? Program.GetParameter<string>(args, "-pass");
            }
            if (Program.HasParameter(args, "-remember-password"))
            {
                RememberPassword = true;
            }
            if (Program.HasParameter(args, "-qr"))
            {
                UseQrCode = true;
            }
            if (Program.HasParameter(args, "-cellid"))
            {
                CellID = Program.GetParameter(args, "-cellid", 0);
            }
            if (Program.HasParameter(args, "-max-downloads"))
            {
                MaxDownloads = Program.GetParameter(args, "-max-downloads", 4);
            }
            if (Program.HasParameter(args, "-max-servers"))
            {
                MaxServers = Program.GetParameter(args, "-max-servers", 20);
            }
            if (Program.HasParameter(args, "-loginid"))
            {
                LoginID = Program.GetParameter<uint?>(args, "-loginid", null);
            }
            if (Program.HasParameter(args, "-dump-directory") || Program.HasParameter(args, "-dir"))
            {
                DumpDirectory = Program.GetParameter<string>(args, "-dump-directory") ??
                               Program.GetParameter<string>(args, "-dir");
            }
            if (Program.HasParameter(args, "-max-concurrent-apps"))
            {
                MaxConcurrentApps = Program.GetParameter(args, "-max-concurrent-apps", 1);
            }
            ApplyToDepotDumperConfig();
        }
    }
}