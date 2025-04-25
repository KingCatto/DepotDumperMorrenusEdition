using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DepotDumper
{
    public static class PathHelper
    {
        public static string SanitizeName(string name, int maxLength = 50)
        {
            if (string.IsNullOrEmpty(name))
                return "Unknown";
                
            string sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            
            sanitized = sanitized.Replace('.', '_');
            
            while (sanitized.Contains("__"))
                sanitized = sanitized.Replace("__", "_");
                
            sanitized = sanitized.Trim('_', ' ');
            
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);
                
            sanitized = sanitized.Trim('_', ' ');
            
            return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
        }
        
        public static string CreateSafeFolderPath(string basePath, params string[] folderParts)
        {
            basePath = basePath.TrimEnd();
            
            var pathBuilder = new StringBuilder(basePath);
            foreach (var part in folderParts)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    string sanitized = SanitizeName(part);
                    pathBuilder.Append(Path.DirectorySeparatorChar);
                    pathBuilder.Append(sanitized);
                }
            }
            
            return pathBuilder.ToString();
        }
        
        public static string CreateSafeFilePath(string directory, string fileName, string extension)
        {
            directory = directory.TrimEnd();
            string safeFileName = SanitizeName(fileName);
            
            if (extension.StartsWith("."))
                extension = extension.Substring(1);
                
            return Path.Combine(directory, $"{safeFileName}.{extension}");
        }
        
        public static bool EnsureDirectoryExists(string directoryPath)
        {
            try
            {
                directoryPath = directoryPath.TrimEnd();
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Logger.Debug($"Created directory: {directoryPath}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create directory {directoryPath}: {ex.Message}");
                return false;
            }
        }
    }
}