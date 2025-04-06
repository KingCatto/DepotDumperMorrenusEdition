using System;
using System.Linq;
namespace DepotDumper
{
    public static class ConfigUtility
    {
        public static void CreateOrUpdateConfig(string configPath = null)
{
    var config = ConfigFile.Load();
    Console.WriteLine("\n=== DepotDumper Configuration Utility ===\n");
    Console.WriteLine("=== Authentication Settings ===");
    config.Username = PromptForInput("Steam Username", config.Username);
    Console.Write($"Steam Password [{(string.IsNullOrEmpty(config.Password) ? "not set" : "****")}]: ");
    var passwordInput = Console.ReadLine();
    if (!string.IsNullOrEmpty(passwordInput))
    {
        config.Password = passwordInput;
    }
    config.RememberPassword = PromptForBool("Remember Password", config.RememberPassword);
    config.UseQrCode = PromptForBool("Use QR Code for Login", config.UseQrCode);
    
    Console.WriteLine("\n=== Performance Settings ===");
    config.MaxDownloads = PromptForInt("Max Concurrent Downloads per App", config.MaxDownloads);
    config.MaxConcurrentApps = PromptForInt("Max Concurrent Apps to Process", config.MaxConcurrentApps);
    config.MaxServers = PromptForInt("Max Servers", config.MaxServers);
    
    Console.WriteLine("\n=== Output Settings ===");
    config.DumpDirectory = PromptForInput("Dump Directory", config.DumpDirectory);
    config.UseNewNamingFormat = PromptForBool("Use New Naming Format", config.UseNewNamingFormat);
    
    // NEW SECTION: Add log level configuration
    Console.WriteLine("\n=== Logging Settings ===");
    string[] validLevels = { "Debug", "Info", "Warning", "Error", "Critical" };
    bool validLevel = false;
    string logLevelInput = config.LogLevel;
    
    do {
        Console.Write($"Log Level [{config.LogLevel}] (Debug, Info, Warning, Error, Critical): ");
        string input = Console.ReadLine();
        
        if (string.IsNullOrWhiteSpace(input))
        {
            validLevel = true; // Keep default
        }
        else
        {
            if (validLevels.Contains(input, StringComparer.OrdinalIgnoreCase))
            {
                logLevelInput = input;
                validLevel = true;
            }
            else
            {
                Console.WriteLine($"Invalid log level. Please enter one of: {string.Join(", ", validLevels)}");
            }
        }
    } while (!validLevel);
    
    config.LogLevel = logLevelInput;
    
    Console.WriteLine("\n=== App ID Settings ===");
    bool editAppIds = PromptForBool("Edit App IDs", false);
    if (editAppIds)
    {
        EditAppIds(config);
    }
    
    config.Save();
    Console.WriteLine("Configuration saved successfully!");
}
        private static void EditAppIds(ConfigFile config)
        {
            bool continueEditing = true;
            while (continueEditing)
            {
                Console.WriteLine("\nCurrent App IDs:");
                if (config.AppIdsToProcess.Count == 0)
                {
                    Console.WriteLine("  No App IDs configured.");
                }
                else
                {
                    var sortedAppIds = config.AppIdsToProcess.OrderBy(id => id).ToList();
                    for (int i = 0; i < sortedAppIds.Count; i++)
                    {
                        var appId = sortedAppIds[i];
                        string status = config.ExcludedAppIds.Contains(appId) ? "Excluded" : "Included";
                        Console.WriteLine($"  {i + 1}. App ID {appId} ({status})");
                    }
                }
                Console.WriteLine("\nOptions:");
                Console.WriteLine("  1. Add App ID");
                Console.WriteLine("  2. Remove App ID");
                Console.WriteLine("  3. Toggle Exclude/Include App ID");
                Console.WriteLine("  4. Finish Editing");
                Console.Write("\nEnter option (1-4): ");
                string option = Console.ReadLine();
                switch (option)
                {
                    case "1":
                        Console.Write("Enter App ID to add: ");
                        if (uint.TryParse(Console.ReadLine(), out uint newAppId))
                        {
                            if (config.AppIdsToProcess.Contains(newAppId))
                            {
                                Console.WriteLine($"App ID {newAppId} already exists in configuration.");
                            }
                            else
                            {
                                config.AppIdsToProcess.Add(newAppId);
                                Console.WriteLine($"Added App ID {newAppId}");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Invalid App ID. Please enter a valid number.");
                        }
                        break;
                    case "2":
                        Console.Write("Enter App ID to remove (or index number): ");
                        string removeInput = Console.ReadLine();
                        if (uint.TryParse(removeInput, out uint removeAppId))
                        {
                            if (removeAppId <= config.AppIdsToProcess.Count && removeAppId > 0)
                            {
                                var sortedIds = config.AppIdsToProcess.OrderBy(id => id).ToList();
                                uint actualId = sortedIds[(int)removeAppId - 1];
                                config.AppIdsToProcess.Remove(actualId);
                                config.ExcludedAppIds.Remove(actualId);
                                Console.WriteLine($"Removed App ID {actualId}");
                            }
                            else if (config.AppIdsToProcess.Contains(removeAppId))
                            {
                                config.AppIdsToProcess.Remove(removeAppId);
                                config.ExcludedAppIds.Remove(removeAppId);
                                Console.WriteLine($"Removed App ID {removeAppId}");
                            }
                            else
                            {
                                Console.WriteLine($"App ID {removeAppId} not found in the configuration.");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Invalid input. Please enter a valid number.");
                        }
                        break;
                    case "3":
                        Console.Write("Enter App ID to toggle (or index number): ");
                        string toggleInput = Console.ReadLine();
                        if (uint.TryParse(toggleInput, out uint toggleAppId))
                        {
                            uint actualId;
                            if (toggleAppId <= config.AppIdsToProcess.Count && toggleAppId > 0)
                            {
                                var sortedIds = config.AppIdsToProcess.OrderBy(id => id).ToList();
                                actualId = sortedIds[(int)toggleAppId - 1];
                            }
                            else if (config.AppIdsToProcess.Contains(toggleAppId))
                            {
                                actualId = toggleAppId;
                            }
                            else
                            {
                                Console.WriteLine($"App ID {toggleAppId} not found in the configuration.");
                                break;
                            }
                            if (config.ExcludedAppIds.Contains(actualId))
                            {
                                config.ExcludedAppIds.Remove(actualId);
                                Console.WriteLine($"App ID {actualId} is now Included (will be processed)");
                            }
                            else
                            {
                                config.ExcludedAppIds.Add(actualId);
                                Console.WriteLine($"App ID {actualId} is now Excluded (will be skipped)");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Invalid input. Please enter a valid number.");
                        }
                        break;
                    case "4":
                        continueEditing = false;
                        break;
                    default:
                        Console.WriteLine("Invalid option. Please enter a number between 1 and 4.");
                        break;
                }
            }
        }
        
        private static string PromptForInput(string prompt, string defaultValue)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            string input = Console.ReadLine();
            return string.IsNullOrWhiteSpace(input) ? defaultValue : input;
        }
        
        private static int PromptForInt(string prompt, int defaultValue)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }
            if (int.TryParse(input, out int result))
            {
                return result;
            }
            Console.WriteLine("Invalid input. Using default value.");
            return defaultValue;
        }
        
        private static bool PromptForBool(string prompt, bool defaultValue)
        {
            Console.Write($"{prompt} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            string input = Console.ReadLine()?.ToLower();
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }
            return input == "y" || input == "yes" || input == "true";
        }
    }
}