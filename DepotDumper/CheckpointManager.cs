using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DepotDumper
{
    /// <summary>
    /// Manages checkpointing to resume processing between runs
    /// </summary>
    public static class CheckpointManager
    {
        private static readonly object checkpointLock = new object();
        private static readonly string checkpointFileName = "checkpoint.json";
        private static readonly string checkpointBackupFileName = "checkpoint.backup.json";
        private static DumpCheckpoint currentCheckpoint;
        private static bool initialized = false;
        private static Timer checkpointTimer;
        private static TimeSpan autoSaveInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Current checkpoint state
        /// </summary>
        public static DumpCheckpoint Current => currentCheckpoint;

        /// <summary>
        /// Initialize checkpoint system
        /// </summary>
        public static void Initialize(string basePath = null)
        {
            if (initialized) return;

            lock (checkpointLock)
            {
                if (initialized) return;

                basePath ??= DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR;

                try
                {
                    string checkpointDirectory = Path.Combine(basePath, DepotDumper.CONFIG_DIR);
                    Directory.CreateDirectory(checkpointDirectory);

                    string checkpointPath = Path.Combine(checkpointDirectory, checkpointFileName);
                    string backupPath = Path.Combine(checkpointDirectory, checkpointBackupFileName);

                    if (File.Exists(checkpointPath))
                    {
                        try
                        {
                            string json = File.ReadAllText(checkpointPath);
                            currentCheckpoint = JsonSerializer.Deserialize<DumpCheckpoint>(json);

                            if (currentCheckpoint == null)
                            {
                                throw new JsonException("Deserialized checkpoint is null");
                            }

                            Logger.Info($"Loaded checkpoint from {checkpointPath}, " +
                                      $"last updated: {currentCheckpoint.LastUpdated}");

                            // Create backup of valid checkpoint
                            File.Copy(checkpointPath, backupPath, true);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Error loading checkpoint: {ex.Message}");

                            if (File.Exists(backupPath))
                            {
                                Logger.Info("Attempting to load backup checkpoint");

                                try
                                {
                                    string backupJson = File.ReadAllText(backupPath);
                                    currentCheckpoint = JsonSerializer.Deserialize<DumpCheckpoint>(backupJson);

                                    if (currentCheckpoint != null)
                                    {
                                        Logger.Info($"Successfully loaded backup checkpoint, " +
                                                  $"last updated: {currentCheckpoint.LastUpdated}");
                                    }
                                }
                                catch (Exception backupEx)
                                {
                                    Logger.Warning($"Error loading backup checkpoint: {backupEx.Message}");
                                }
                            }
                        }
                    }

                    // If we still don't have a checkpoint, create a new one
                    if (currentCheckpoint == null)
                    {
                        currentCheckpoint = new DumpCheckpoint();
                        Logger.Info("Created new checkpoint (no previous checkpoint found)");
                    }

                    // Initialize auto-save timer if enabled
                    if (DepotDumper.Config?.EnableCheckpointing == true)
                    {
                        checkpointTimer = new Timer(
                            AutoSaveCheckpoint,
                            checkpointDirectory,
                            autoSaveInterval,
                            autoSaveInterval);

                        Logger.Info($"Enabled auto-save checkpointing with {autoSaveInterval.TotalMinutes} minute interval");
                    }

                    initialized = true;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to initialize checkpoint system: {ex.Message}");
                    currentCheckpoint = new DumpCheckpoint();
                }
            }
        }

        /// <summary>
        /// Auto-save callback for timer
        /// </summary>
        private static void AutoSaveCheckpoint(object state)
        {
            if (!initialized || currentCheckpoint == null) return;

            string checkpointDirectory = (string)state;

            try
            {
                SaveCheckpoint(checkpointDirectory);
                Logger.Debug("Auto-saved checkpoint");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error during auto-save checkpoint: {ex.Message}");
            }
        }

        /// <summary>
        /// Save current checkpoint state
        /// </summary>
        public static void SaveCheckpoint(string basePath = null)
        {
            if (!initialized || currentCheckpoint == null) return;

            lock (checkpointLock)
            {
                try
                {
                    basePath ??= DepotDumper.Config?.DumpDirectory ?? DepotDumper.DEFAULT_DUMP_DIR;
                    string checkpointDirectory = Path.Combine(basePath, DepotDumper.CONFIG_DIR);
                    Directory.CreateDirectory(checkpointDirectory);

                    string checkpointPath = Path.Combine(checkpointDirectory, checkpointFileName);
                    string tempPath = checkpointPath + ".tmp";

                    currentCheckpoint.LastUpdated = DateTime.Now;

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                    // Write to temp file first, then move to ensure atomic update
                    string json = JsonSerializer.Serialize(currentCheckpoint, options);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, checkpointPath, true);

                    Logger.Debug($"Saved checkpoint to {checkpointPath}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error saving checkpoint: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Check if an app has been processed in the current session
        /// </summary>
        public static bool IsAppProcessed(uint appId, out bool wasSuccessful)
        {
            wasSuccessful = false;

            if (!initialized || currentCheckpoint == null)
                return false;

            lock (checkpointLock)
            {
                if (currentCheckpoint.ProcessedApps.TryGetValue(appId, out var result))
                {
                    wasSuccessful = result;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Mark an app as processed
        /// </summary>
        public static void MarkAppProcessed(uint appId, bool success)
        {
            if (!initialized || currentCheckpoint == null)
                return;

            lock (checkpointLock)
            {
                currentCheckpoint.ProcessedApps[appId] = success;

                // Auto-save if enabled and timer isn't active
                if (DepotDumper.Config?.EnableCheckpointing == true && checkpointTimer == null)
                {
                    SaveCheckpoint();
                }
            }
        }
    }
}