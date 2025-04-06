using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDumper
{
    /// <summary>
    /// Handles optimized file operations for better performance
    /// </summary>
    public static class FileOperations
    {
        // Cache of file write buffers for better performance
        private static readonly ConcurrentDictionary<string, BufferedStream> fileWriteBuffers = 
            new ConcurrentDictionary<string, BufferedStream>();
            
        // Cache of file read buffers for better performance
        private static readonly ConcurrentDictionary<string, BufferedStream> fileReadBuffers = 
            new ConcurrentDictionary<string, BufferedStream>();
            
        // Semaphore to limit concurrent file operations
        private static readonly SemaphoreSlim fileOperationSemaphore = new SemaphoreSlim(32, 32);
        
        /// <summary>
        /// Initialize file operations system
        /// </summary>
        public static void Initialize()
        {
            // Configure buffer size based on system memory
            int bufferSizeKb = DepotDumper.Config?.FileBufferSizeKb ?? 64;
            
            Logger.Info($"File operations initialized with {bufferSizeKb}KB buffer size");
        }
        
        /// <summary>
        /// Write data to a file using efficient buffering
        /// </summary>
        public static void WriteFile(string path, byte[] data)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var bufferSizeKb = DepotDumper.Config?.FileBufferSizeKb ?? 64;
            
            var stream = fileWriteBuffers.GetOrAdd(path, p => 
                new BufferedStream(
                    new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None), 
                    bufferSizeKb * 1024));
            
            lock (stream)
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }
        
        /// <summary>
        /// Write data to a file asynchronously using efficient buffering
        /// </summary>
        public static async Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            await fileOperationSemaphore.WaitAsync(cancellationToken);
            
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 
                                           bufferSize: (DepotDumper.Config?.FileBufferSizeKb ?? 64) * 1024, 
                                           useAsync: true))
                {
                    await fs.WriteAsync(data, 0, data.Length, cancellationToken);
                    await fs.FlushAsync(cancellationToken);
                }
            }
            finally
            {
                fileOperationSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Read all bytes from a file with efficient buffering
        /// </summary>
        public static byte[] ReadFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }
            
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 
                                       bufferSize: (DepotDumper.Config?.FileBufferSizeKb ?? 64) * 1024))
            {
                byte[] buffer = new byte[fs.Length];
                fs.Read(buffer, 0, buffer.Length);
                return buffer;
            }
        }
        
        /// <summary>
        /// Read all bytes from a file asynchronously with efficient buffering
        /// </summary>
        public static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
        {
            await fileOperationSemaphore.WaitAsync(cancellationToken);
            
            try
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"File not found: {path}");
                }
                
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 
                                           bufferSize: (DepotDumper.Config?.FileBufferSizeKb ?? 64) * 1024, 
                                           useAsync: true))
                {
                    byte[] buffer = new byte[fs.Length];
                    await fs.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    return buffer;
                }
            }
            finally
            {
                fileOperationSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Create a ZIP archive from a directory with optimal compression
        /// </summary>
        public static void CreateZipArchive(string sourceDirectory, string zipFilePath, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            if (File.Exists(zipFilePath))
            {
                File.Delete(zipFilePath);
            }
            
            if (!Directory.Exists(sourceDirectory) || !Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Logger.Warning($"Source directory empty or missing: {sourceDirectory}, skipping zip creation");
                return;
            }
            
            try
            {
                ZipFile.CreateFromDirectory(sourceDirectory, zipFilePath, compressionLevel, false);
                Logger.Info($"Created zip archive: {zipFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating zip archive {zipFilePath}: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Create a ZIP archive asynchronously from a directory
        /// </summary>
        public static async Task CreateZipArchiveAsync(string sourceDirectory, string zipFilePath, 
                                                  CompressionLevel compressionLevel = CompressionLevel.Optimal,
                                                  CancellationToken cancellationToken = default)
        {
            await fileOperationSemaphore.WaitAsync(cancellationToken);
            
            try
            {
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                }
                
                if (!Directory.Exists(sourceDirectory) || !Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
                {
                    Logger.Warning($"Source directory empty or missing: {sourceDirectory}, skipping zip creation");
                    return;
                }
                
                await Task.Run(() => 
                {
                    ZipFile.CreateFromDirectory(sourceDirectory, zipFilePath, compressionLevel, false);
                }, cancellationToken);
                
                Logger.Info($"Created zip archive: {zipFilePath}");
            }
            catch (OperationCanceledException)
            {
                Logger.Warning($"Zip archive creation cancelled for {zipFilePath}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating zip archive {zipFilePath}: {ex.Message}");
                throw;
            }
            finally
            {
                fileOperationSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Save an object to a JSON file
        /// </summary>
        public static void SaveObjectToJsonFile<T>(string path, T obj, bool indented = true)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented
            };
            
            string json = JsonSerializer.Serialize(obj, options);
            File.WriteAllText(path, json);
        }
        
        /// <summary>
        /// Load an object from a JSON file
        /// </summary>
        public static T LoadObjectFromJsonFile<T>(string path) where T : class
        {
            if (!File.Exists(path))
            {
                return null;
            }
            
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        
        /// <summary>
        /// Clean up any resources and close file handles
        /// </summary>
        public static void Shutdown()
        {
            // Close all write buffers
            foreach (var buffer in fileWriteBuffers)
            {
                try
                {
                    buffer.Value.Flush();
                    buffer.Value.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error closing file buffer for {buffer.Key}: {ex.Message}");
                }
            }
            
            fileWriteBuffers.Clear();
            
            // Close all read buffers
            foreach (var buffer in fileReadBuffers)
            {
                try
                {
                    buffer.Value.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error closing file buffer for {buffer.Key}: {ex.Message}");
                }
            }
            
            fileReadBuffers.Clear();
            
            // Dispose semaphore
            fileOperationSemaphore.Dispose();
            
            Logger.Info("File operations shutdown complete");
        }
    }
}