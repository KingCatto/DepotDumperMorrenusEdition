using System;
using System.Collections.Generic;
using System.IO;
namespace DepotDumper
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }
    public static class Logger
    {
        private static readonly object LogLock = new object();
        private static string logFilePath = "DepotDumper.log";
        private static LogLevel minLogLevel = LogLevel.Info;
        private static List<string> errorLog = new List<string>();
        private static bool consoleOutput = true;
        private static bool fileOutput = true;
        public static void Initialize(string logPath = null, LogLevel level = LogLevel.Info, bool toConsole = true, bool toFile = true)
        {
            lock (LogLock)
            {
                if (logPath != null)
                {
                    logFilePath = logPath;
                }
                string logDirectory = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }
                minLogLevel = level;
                consoleOutput = toConsole;
                fileOutput = toFile;
                if (fileOutput && File.Exists(logFilePath))
                {
                    File.WriteAllText(logFilePath, string.Empty);
                }
                string initialMessage = $"Log initialized at {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")} with level {level}";
                if (fileOutput)
                {
                    File.AppendAllText(logFilePath, initialMessage + Environment.NewLine);
                }
                if (consoleOutput)
                {
                    Console.WriteLine(initialMessage);
                }
            }
        }
        public static void Log(LogLevel level, string message)
        {
            if (level < minLogLevel)
                return;
            lock (LogLock)
            {
                string formattedMessage = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}] [{level.ToString().ToUpper()}] {message}";
                if (level >= LogLevel.Error)
                {
                    errorLog.Add(formattedMessage);
                }
                if (fileOutput)
                {
                    try
                    {
                        File.AppendAllText(logFilePath, formattedMessage + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to write to log file: {ex.Message}");
                    }
                }
                if (consoleOutput)
                {
                    ConsoleColor originalColor = Console.ForegroundColor;
                    switch (level)
                    {
                        case LogLevel.Debug:
                            Console.ForegroundColor = ConsoleColor.Gray;
                            break;
                        case LogLevel.Info:
                            Console.ForegroundColor = ConsoleColor.White;
                            break;
                        case LogLevel.Warning:
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            break;
                        case LogLevel.Error:
                            Console.ForegroundColor = ConsoleColor.Red;
                            break;
                        case LogLevel.Critical:
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            break;
                    }
                    Console.WriteLine(formattedMessage);
                    Console.ForegroundColor = originalColor;
                }
            }
        }
        public static void Debug(string message) => Log(LogLevel.Debug, message);
        public static void Info(string message) => Log(LogLevel.Info, message);
        public static void Warning(string message) => Log(LogLevel.Warning, message);
        public static void Error(string message) => Log(LogLevel.Error, message);
        public static void Critical(string message) => Log(LogLevel.Critical, message);
        public static List<string> GetErrors()
        {
            lock (LogLock)
            {
                return new List<string>(errorLog);
            }
        }
        public static int GetErrorCount()
        {
            lock (LogLock)
            {
                return errorLog.Count;
            }
        }
        public static void ClearErrors()
        {
            lock (LogLock)
            {
                errorLog.Clear();
            }
        }
    }
}