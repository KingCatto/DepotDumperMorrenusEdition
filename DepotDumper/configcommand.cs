using System;
using System.Linq;
namespace DepotDumper
{
    public static class ConfigCommand
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }
            string subCommand = args[1].ToLower();
            switch (subCommand)
            {
                case "create":
                    ConfigUtility.CreateOrUpdateConfig();
                    return 0;
                case "add-app":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing App ID");
                        PrintUsage();
                        return 1;
                    }
                    if (!uint.TryParse(args[2], out uint appId))
                    {
                        Console.WriteLine("Error: Invalid App ID");
                        return 1;
                    }
                    AddAppId(appId);
                    return 0;
                case "remove-app":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing App ID");
                        PrintUsage();
                        return 1;
                    }
                    if (!uint.TryParse(args[2], out uint removeAppId))
                    {
                        Console.WriteLine("Error: Invalid App ID");
                        return 1;
                    }
                    RemoveAppId(removeAppId);
                    return 0;
                case "exclude-app":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing App ID");
                        PrintUsage();
                        return 1;
                    }
                    if (!uint.TryParse(args[2], out uint excludeAppId))
                    {
                        Console.WriteLine("Error: Invalid App ID");
                        return 1;
                    }
                    ExcludeAppId(excludeAppId);
                    return 0;
                case "include-app":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing App ID");
                        PrintUsage();
                        return 1;
                    }
                    if (!uint.TryParse(args[2], out uint includeAppId))
                    {
                        Console.WriteLine("Error: Invalid App ID");
                        return 1;
                    }
                    IncludeAppId(includeAppId);
                    return 0;
                case "list-apps":
                    ListAppIds();
                    return 0;
                default:
                    Console.WriteLine($"Unknown subcommand: {subCommand}");
                    PrintUsage();
                    return 1;
            }
        }
        private static void PrintUsage()
        {
            Console.WriteLine("Usage: DepotDumper config <subcommand> [options]");
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  create                      Create or update configuration file");
            Console.WriteLine("  add-app <appid>            Add an App ID to the configuration");
            Console.WriteLine("  remove-app <appid>         Remove an App ID from the configuration");
            Console.WriteLine("  exclude-app <appid>        Add an App ID to the exclusion list");
            Console.WriteLine("  include-app <appid>        Remove an App ID from the exclusion list");
            Console.WriteLine("  list-apps                  List all App IDs in the configuration");
        }
        private static void AddAppId(uint appId)
        {
            var config = ConfigFile.Load();
            if (config.AppIdsToProcess.Contains(appId))
            {
                Console.WriteLine($"App ID {appId} already in configuration.");
                return;
            }
            config.AppIdsToProcess.Add(appId);
            Console.WriteLine($"Added App ID {appId} to configuration.");
            config.Save();
        }
        private static void RemoveAppId(uint appId)
        {
            var config = ConfigFile.Load();
            if (config.AppIdsToProcess.Contains(appId))
            {
                config.AppIdsToProcess.Remove(appId);
                Console.WriteLine($"Removed App ID {appId} from configuration.");
                config.Save();
            }
            else
            {
                Console.WriteLine($"App ID {appId} not found in configuration.");
            }
        }
        private static void ExcludeAppId(uint appId)
        {
            var config = ConfigFile.Load();
            if (config.ExcludedAppIds.Contains(appId))
            {
                Console.WriteLine($"App ID {appId} is already excluded.");
                return;
            }
            if (!config.AppIdsToProcess.Contains(appId))
            {
                config.AppIdsToProcess.Add(appId);
                Console.WriteLine($"Added App ID {appId} to configuration as it wasn't present.");
            }
            config.ExcludedAppIds.Add(appId);
            Console.WriteLine($"Added App ID {appId} to exclusion list. It will be skipped during processing.");
            config.Save();
        }
        private static void IncludeAppId(uint appId)
        {
            var config = ConfigFile.Load();
            if (config.ExcludedAppIds.Contains(appId))
            {
                config.ExcludedAppIds.Remove(appId);
                Console.WriteLine($"Removed App ID {appId} from exclusion list. It will be processed normally.");
                config.Save();
            }
            else
            {
                Console.WriteLine($"App ID {appId} was not in the exclusion list.");
            }
        }
        private static void ListAppIds()
        {
            var config = ConfigFile.Load();
            if (config.AppIdsToProcess.Count == 0)
            {
                Console.WriteLine("No App IDs configured.");
                return;
            }
            Console.WriteLine("Configured App IDs:");
            foreach (var appId in config.AppIdsToProcess.OrderBy(id => id))
            {
                string status = config.ExcludedAppIds.Contains(appId) ? "Excluded" : "Included";
                Console.WriteLine($"  {appId}: {status}");
            }
        }
    }
}