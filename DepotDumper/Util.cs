using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDumper
{
    static class Util
    {
        
        private static readonly ArrayPool<byte> sharedBufferPool = ArrayPool<byte>.Shared;
        
        
        private static readonly ConcurrentDictionary<string, byte[]> fileChecksumCache = 
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            
        
        private static readonly ConcurrentDictionary<(uint depotId, ulong manifestId), (DateTime timestamp, byte[] hash)> manifestCache = 
            new ConcurrentDictionary<(uint, ulong), (DateTime, byte[])>();
            
        
        private static readonly ConcurrentDictionary<string, BufferedStream> fileWriteBuffers = 
            new ConcurrentDictionary<string, BufferedStream>();
        
        
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
            
            
            byte[] buffer = sharedBufferPool.Rent(128 * 1024);
            
            try
            {
                foreach (var data in chunkdata)
                {
                    fs.Seek((long)data.Offset, SeekOrigin.Begin);
                    var adler = AdlerHash(fs, (int)data.UncompressedLength, buffer);
                    if (!adler.SequenceEqual(BitConverter.GetBytes(data.Checksum)))
                    {
                        neededChunks.Add(data);
                    }
                }
            }
            finally
            {
                sharedBufferPool.Return(buffer);
            }
            
            return neededChunks;
        }
        
        
        public static byte[] AdlerHash(Stream stream, int length, byte[] buffer = null)
        {
            uint a = 0, b = 0;
            
            
            if (buffer != null && length > 1024)
            {
                int bytesRead;
                int bytesRemaining = length;
                
                while (bytesRemaining > 0)
                {
                    int bytesToRead = Math.Min(bytesRemaining, buffer.Length);
                    bytesRead = stream.Read(buffer, 0, bytesToRead);
                    
                    if (bytesRead == 0) break;
                    
                    for (int i = 0; i < bytesRead; i++)
                    {
                        a = (a + buffer[i]) % 65521;
                        b = (b + a) % 65521;
                    }
                    
                    bytesRemaining -= bytesRead;
                }
            }
            else
            {
                
                for (var i = 0; i < length; i++)
                {
                    var c = (uint)stream.ReadByte();
                    a = (a + c) % 65521;
                    b = (b + a) % 65521;
                }
            }
            
            return BitConverter.GetBytes(a | (b << 16));
        }
        
        
        public static byte[] FileSHAHash(string filename)
        {
            
            if (fileChecksumCache.TryGetValue(filename, out var cachedHash))
            {
                return cachedHash;
            }
            
            byte[] hash;
            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024))
            using (var sha = SHA1.Create())
            {
                hash = sha.ComputeHash(fs);
            }
            
            
            fileChecksumCache[filename] = hash;
            return hash;
        }
        
        
        public static DepotManifest LoadManifestFromFile(string directory, uint depotId, ulong manifestId, bool badHashWarning)
        {
            
            var cacheKey = (depotId, manifestId);
            if (manifestCache.TryGetValue(cacheKey, out var cacheEntry))
            {
                Logger.Debug($"Using cached manifest info for {depotId}_{manifestId}");
            }
            
            var filename = Path.Combine(directory, $"{depotId}_{manifestId}.manifest");
            if (File.Exists(filename))
            {
                byte[] expectedChecksum = null;
                string checksumFile = filename + ".sha";
                
                try
                {
                    if (File.Exists(checksumFile))
                    {
                        expectedChecksum = File.ReadAllBytes(checksumFile);
                    }
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }
                
                var currentChecksum = FileSHAHash(filename);
                
                
                manifestCache[cacheKey] = (File.GetLastWriteTime(filename), currentChecksum);
                
                if (expectedChecksum != null && expectedChecksum.SequenceEqual(currentChecksum))
                {
                    try
                    {
                        return DepotManifest.LoadFromFile(filename);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Error loading manifest file {filename}: {ex.Message}");
                    }
                }
                else if (badHashWarning)
                {
                    Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
                }
            }
            
            
            filename = Path.Combine(directory, $"{depotId}_{manifestId}.bin");
            if (File.Exists(filename))
            {
                byte[] expectedChecksum = null;
                string checksumFile = filename + ".sha";
                
                try
                {
                    if (File.Exists(checksumFile))
                    {
                        expectedChecksum = File.ReadAllBytes(checksumFile);
                    }
                }
                catch (IOException)
                {
                    expectedChecksum = null;
                }
                
                byte[] currentChecksum;
                var oldManifest = ProtoManifest.LoadFromFile(filename, out currentChecksum);
                
                if (oldManifest != null)
                {
                    if (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum))
                    {
                        oldManifest = null;
                        if (badHashWarning)
                        {
                            Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
                        }
                    }
                    else
                    {
                        
                        manifestCache[cacheKey] = (File.GetLastWriteTime(filename), currentChecksum);
                    }
                }
                
                if (oldManifest != null)
                {
                    try
                    {
                        return oldManifest.ConvertToSteamManifest(depotId);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Error converting legacy manifest {filename}: {ex.Message}");
                    }
                }
            }
            
            return null;
        }
        
        
        public static bool SaveManifestToFile(string directory, DepotManifest manifest)
        {
            try
            {
                var filename = Path.Combine(directory, $"{manifest.DepotID}_{manifest.ManifestGID}.manifest");
                Directory.CreateDirectory(directory);
                
                
                using (var memStream = new MemoryStream())
                {
                    manifest.SaveToStream(memStream);
                    memStream.Position = 0;
                    
                    
                    var hash = SHA1.HashData(memStream.ToArray());
                    
                    
                    WriteBufferedData(filename, memStream.ToArray());
                    WriteBufferedData(filename + ".sha", hash);
                    
                    
                    manifestCache[(manifest.DepotID, manifest.ManifestGID)] = (DateTime.Now, hash);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving manifest: {ex.Message}");
                return false;
            }
        }
        
        
        public static void WriteBufferedData(string path, byte[] data)
        {
            var stream = fileWriteBuffers.GetOrAdd(path, p => 
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                return new BufferedStream(
                    new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None), 
                    DepotDumper.Config.FileBufferSizeKb * 1024);
            });
            
            lock (stream)
            {
                stream.Write(data, 0, data.Length);
            }
        }
        
        
        public static void FlushAllFileBuffers()
        {
            int closedStreams = 0;
            
            foreach (var buffer in fileWriteBuffers)
            {
                try
                {
                    buffer.Value.Flush();
                    buffer.Value.Dispose();
                    closedStreams++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error closing buffer for {buffer.Key}: {ex.Message}");
                }
            }
            
            fileWriteBuffers.Clear();
            Logger.Info($"Closed {closedStreams} file write buffers");
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
            
            
            using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
            var runningTasks = new List<Task>(maxDegreeOfParallelism);
            
            foreach (var factory in queue)
            {
                
                runningTasks.Add(Task.Run(async () => 
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await factory();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            
            await Task.WhenAll(runningTasks);
        }
    }
}