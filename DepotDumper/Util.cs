using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;
namespace DepotDumper
{
    static class Util
    {
        public static string GetSteamOS()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macos";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                return "linux";
            }
            return "unknown";
        }
        public static string GetSteamArch()
        {
            return Environment.Is64BitOperatingSystem ? "64" : "32";
        }
        public static string ReadPassword()
        {
            ConsoleKeyInfo keyInfo;
            var password = new StringBuilder();
            do
            {
                keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Remove(password.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }
                var c = keyInfo.KeyChar;
                if (c >= ' ' && c <= '~')
                {
                    password.Append(c);
                    Console.Write('*');
                }
            } while (keyInfo.Key != ConsoleKey.Enter);
            return password.ToString();
        }
        public static List<DepotManifest.ChunkData> ValidateSteam3FileChecksums(FileStream fs, DepotManifest.ChunkData[] chunkdata)
        {
            var neededChunks = new List<DepotManifest.ChunkData>();
            foreach (var data in chunkdata)
            {
                fs.Seek((long)data.Offset, SeekOrigin.Begin);
                var adler = AdlerHash(fs, (int)data.UncompressedLength);
                if (!adler.SequenceEqual(BitConverter.GetBytes(data.Checksum)))
                {
                    neededChunks.Add(data);
                }
            }
            return neededChunks;
        }
        public static byte[] AdlerHash(Stream stream, int length)
        {
            uint a = 0, b = 0;
            for (var i = 0; i < length; i++)
            {
                var c = (uint)stream.ReadByte();
                a = (a + c) % 65521;
                b = (b + a) % 65521;
            }
            return BitConverter.GetBytes(a | (b << 16));
        }
        public static byte[] FileSHAHash(string filename)
        {
            using (var fs = File.Open(filename, FileMode.Open))
            using (var sha = SHA1.Create())
            {
                var output = sha.ComputeHash(fs);
                return output;
            }
        }
        public static DepotManifest LoadManifestFromFile(string directory, uint depotId, ulong manifestId, bool badHashWarning)
        {
            var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", depotId, manifestId));
            if (File.Exists(filename))
            {
                byte[] expectedChecksum;
                try
                {
                    expectedChecksum = File.ReadAllBytes(filename + ".sha");
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }
                var currentChecksum = FileSHAHash(filename);
                if (expectedChecksum != null && expectedChecksum.SequenceEqual(currentChecksum))
                {
                    return DepotManifest.LoadFromFile(filename);
                }
                else if (badHashWarning)
                {
                    Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
                }
            }
            filename = Path.Combine(directory, string.Format("{0}_{1}.bin", depotId, manifestId));
            if (File.Exists(filename))
            {
                byte[] expectedChecksum;
                try
                {
                    expectedChecksum = File.ReadAllBytes(filename + ".sha");
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }
                byte[] currentChecksum;
                var oldManifest = ProtoManifest.LoadFromFile(filename, out currentChecksum);
                if (oldManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
                {
                    oldManifest = null;
                    if (badHashWarning)
                    {
                        Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
                    }
                }
                if (oldManifest != null)
                {
                    return oldManifest.ConvertToSteamManifest(depotId);
                }
            }
            return null;
        }
        public static bool SaveManifestToFile(string directory, DepotManifest manifest)
        {
            try
            {
                var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", manifest.DepotID, manifest.ManifestGID));
                manifest.SaveToFile(filename);
                File.WriteAllBytes(filename + ".sha", FileSHAHash(filename));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        public static byte[] DecodeHexString(string hex)
        {
            if (hex == null)
                return null;
            var chars = hex.Length;
            var bytes = new byte[chars / 2];
            for (var i = 0; i < chars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
        public static byte[] SymmetricDecryptECB(byte[] input, byte[] key)
        {
            using var aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var aesTransform = aes.CreateDecryptor(key, null);
            var output = aesTransform.TransformFinalBlock(input, 0, input.Length);
            return output;
        }
        public static async Task InvokeAsync(IEnumerable<Func<Task>> taskFactories, int maxDegreeOfParallelism)
        {
            ArgumentNullException.ThrowIfNull(taskFactories);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDegreeOfParallelism, 0);
            var queue = taskFactories.ToArray();
            if (queue.Length == 0)
            {
                return;
            }
            var tasksInFlight = new List<Task>(maxDegreeOfParallelism);
            var index = 0;
            do
            {
                while (tasksInFlight.Count < maxDegreeOfParallelism && index < queue.Length)
                {
                    var taskFactory = queue[index++];
                    tasksInFlight.Add(taskFactory());
                }
                var completedTask = await Task.WhenAny(tasksInFlight).ConfigureAwait(false);
                await completedTask.ConfigureAwait(false);
                tasksInFlight.Remove(completedTask);
            } while (index < queue.Length || tasksInFlight.Count != 0);
        }
    public static DateTime? GetDateFromFolderName(string folderPath)
        {
            try
            {
                // Get just the folder name from the path
                string folderName = Path.GetFileName(folderPath);
                
                // Example format: "284830.public.2014-04-23_12-01-22.Clockwork Tales_ Of Glass and Ink"
                
                // Split the folder name by dots
                string[] parts = folderName.Split('.');
                
                // Check if we have enough parts (we need at least 3: appId, branch, dateTime)
                if (parts.Length < 3)
                {
                    Logger.Warning($"Folder name format doesn't match expected pattern: {folderName}");
                    return null;
                }
                
                // The date part should be the third element (index 2)
                string datePart = parts[2];
                
                // Split the date part which might look like "2014-04-23_12-01-22"
                string[] dateTimeParts = datePart.Split('_');
                
                // Check if we have date and time
                if (dateTimeParts.Length != 2)
                {
                    // Try alternate formats or just return null
                    if (DateTime.TryParse(datePart, out DateTime dateResult))
                    {
                        return dateResult;
                    }
                    Logger.Warning($"Date time format doesn't match expected pattern: {datePart}");
                    return null;
                }
                
                // Parse the date and time parts
                string datePortion = dateTimeParts[0]; // 2014-04-23
                string timePortion = dateTimeParts[1].Replace('-', ':'); // 12-01-22 -> 12:01:22
                
                // Try to parse the combined date and time
                string dateTimeString = $"{datePortion} {timePortion}";
                if (DateTime.TryParse(dateTimeString, out DateTime result))
                {
                    return result;
                }
                
                Logger.Warning($"Could not parse date and time from: {dateTimeString}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing date from folder name: {ex.Message}");
                return null;
            }
        }

        public static Dictionary<string, DateTime> GetDatesFromFolders(string baseDirectory, uint appId)
        {
            var result = new Dictionary<string, DateTime>();
            
            try
            {
                // Path to the app's directory
                string appDirectory = Path.Combine(baseDirectory, appId.ToString());
                
                // Check if the directory exists
                if (!Directory.Exists(appDirectory))
                {
                    Logger.Warning($"App directory does not exist: {appDirectory}");
                    return result;
                }
                
                // Get all subdirectories that match the pattern for this app
                string searchPattern = $"{appId}.*";
                var folders = Directory.GetDirectories(appDirectory, searchPattern);
                
                foreach (var folder in folders)
                {
                    // Extract the branch name from the folder name
                    // Format: "284830.public.2014-04-23_12-01-22.Clockwork Tales_ Of Glass and Ink"
                    string folderName = Path.GetFileName(folder);
                    string[] parts = folderName.Split('.');
                    
                    if (parts.Length < 3)
                    {
                        Logger.Warning($"Skipping folder with unexpected format: {folderName}");
                        continue;
                    }
                    
                    // The branch is the second part
                    string branch = parts[1];
                    
                    // Get the date from the folder name
                    DateTime? folderDate = GetDateFromFolderName(folder);
                    
                    if (folderDate.HasValue)
                    {
                        // Store the branch and date
                        string cleanBranchName = branch.Replace('/', '_').Replace('\\', '_');
                        result[cleanBranchName] = folderDate.Value;
                        Logger.Debug($"Found date {folderDate.Value} for branch '{cleanBranchName}' in folder {folderName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting dates from folders: {ex.Message}");
            }
            
            return result;
        }

        public static DateTime? GetDateFromExistingFolder(string appPath, string branchName, uint appId)
        {
            try
            {
                // Look for folders matching the pattern "appId.branchName.*" 
                var folders = Directory.GetDirectories(appPath, $"{appId}.{branchName}.*");
                
                foreach (var folder in folders)
                {
                    DateTime? folderDate = GetDateFromFolderName(folder);
                    if (folderDate.HasValue)
                    {
                        Logger.Debug($"Found date {folderDate.Value} from existing folder: {Path.GetFileName(folder)}");
                        return folderDate.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error getting date from existing folders: {ex.Message}");
            }
            
            return null;
        }
    }
}


