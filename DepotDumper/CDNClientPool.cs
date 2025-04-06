using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using SteamKit2.CDN;
namespace DepotDumper
{
    class CDNClientPool
    {
        // Increased constants for better performance
        private const int ServerEndpointMinimumSize = 20; // Increased from 8
        private const int ConnectionAcquisitionTimeout = 30000; // 30 seconds timeout
        private const int MaxRetries = 5;
        private const int RetryDelayBaseMs = 500;

        // Tracking for pool performance
        private long connectionRequestsCount = 0;
        private long connectionFailuresCount = 0;
        private long connectionHitsCount = 0;
        private readonly Stopwatch poolLifetimeStopwatch = new Stopwatch();

        private readonly Steam3Session steamSession;
        private readonly uint appId;
        public Client CDNClient { get; }
        public Server ProxyServer { get; private set; }

        // Improved connection tracking
        private readonly ConcurrentStack<Server> activeConnectionPool = [];
        private readonly BlockingCollection<Server> availableServerEndpoints = [];
        private readonly ConcurrentDictionary<string, int> serverPenalties = new ConcurrentDictionary<string, int>();
        private readonly AutoResetEvent populatePoolEvent = new(true);
        private readonly Task monitorTask;
        private readonly CancellationTokenSource shutdownToken = new();
        private readonly SemaphoreSlim connectionSemaphore;

        // Track server health
        private readonly ConcurrentDictionary<string, DateTime> serverLastFailure = new ConcurrentDictionary<string, DateTime>();

        public CancellationTokenSource ExhaustedToken { get; set; }

        public CDNClientPool(Steam3Session steamSession, uint appId)
        {
            this.steamSession = steamSession;
            this.appId = appId;

            // Configure connection semaphore based on system specs
            int maxConnections = Math.Max(DepotDumper.Config.MaxServers, 20);
            connectionSemaphore = new SemaphoreSlim(maxConnections, maxConnections);

            CDNClient = new Client(steamSession.steamClient);

            // Start monitoring connection pool
            monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
            poolLifetimeStopwatch.Start();

            Logger.Info($"Created CDN pool for app {appId} with max {maxConnections} connections");
        }

        public void Shutdown()
        {
            poolLifetimeStopwatch.Stop();

            // Log pool statistics
            double successRate = connectionRequestsCount > 0
                ? 100.0 * (connectionRequestsCount - connectionFailuresCount) / connectionRequestsCount
                : 0;
            double hitRate = connectionRequestsCount > 0
                ? 100.0 * connectionHitsCount / connectionRequestsCount
                : 0;

            Logger.Info($"CDN pool for app {appId} shutdown after {poolLifetimeStopwatch.Elapsed.TotalSeconds:F1}s - " +
                      $"Requests: {connectionRequestsCount}, Failures: {connectionFailuresCount}, " +
                      $"Success rate: {successRate:F1}%, Hit rate: {hitRate:F1}%");

            // Cleanup and shutdown
            shutdownToken.Cancel();

            try
            {
                monitorTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error waiting for monitor task during shutdown: {ex.Message}");
            }

            // Clean up resources
            availableServerEndpoints.Dispose();
            populatePoolEvent.Dispose();
            connectionSemaphore.Dispose();
            shutdownToken.Dispose();
        }

        private async Task<IReadOnlyCollection<Server>> FetchBootstrapServerListAsync()
        {
            string metricName = $"fetch_cdn_servers_app_{appId}";
            var sw = new Stopwatch();
            sw.Start();

            try
            {
                var cdnServers = await this.steamSession.steamContent.GetServersForSteamPipe();
                sw.Stop();

                if (cdnServers != null)
                {
                    Logger.Info($"Retrieved {cdnServers.Count} CDN servers for app {appId} in {sw.Elapsed.TotalSeconds:F2}s");
                    return cdnServers;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Error($"Failed to retrieve content server list for app {appId} in {sw.Elapsed.TotalSeconds:F2}s: {ex.Message}");
            }

            return null;
        }

        private async Task ConnectionPoolMonitorAsync()
        {
            var random = new Random();
            var didPopulate = false;

            while (!shutdownToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for signal or timeout
                    if (!populatePoolEvent.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        // Periodic check even without explicit request
                        if (availableServerEndpoints.Count < ServerEndpointMinimumSize / 2 &&
                            steamSession.steamClient.IsConnected)
                        {
                            Logger.Debug($"Pool running low ({availableServerEndpoints.Count} endpoints), refreshing server list");
                            populatePoolEvent.Set();
                        }
                        continue;
                    }

                    if (availableServerEndpoints.Count < ServerEndpointMinimumSize &&
                        steamSession.steamClient.IsConnected)
                    {
                        var sw = Stopwatch.StartNew();
                        var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);
                        sw.Stop();

                        if (servers == null || servers.Count == 0)
                        {
                            Logger.Warning($"Failed to get servers or empty server list for app {appId} after {sw.Elapsed.TotalSeconds:F2}s");

                            // Retry after backoff delay
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * Math.Pow(2, didPopulate ? 1 : 0))));

                            if (availableServerEndpoints.Count == 0)
                            {
                                Logger.Error($"No available server endpoints for app {appId}, cancelling token");
                                ExhaustedToken?.Cancel();
                                return;
                            }

                            continue;
                        }

                        ProxyServer = servers.Where(x => x.UseAsProxy).FirstOrDefault();

                        // Filter servers by app eligibility, type, and load with penalty consideration
                        var weightedCdnServers = servers
                            .Where(server =>
                            {
                                var isEligibleForApp = server.AllowedAppIds.Length == 0 ||
                                                     server.AllowedAppIds.Contains(appId);
                                return isEligibleForApp &&
                                      (server.Type == "SteamCache" || server.Type == "CDN");
                            })
                            .Select(server =>
                            {
                                // Check if server has recent failure
                                if (serverLastFailure.TryGetValue(server.Host, out var lastFailure))
                                {
                                    var timeSinceFailure = DateTime.Now - lastFailure;
                                    if (timeSinceFailure.TotalMinutes < 5)
                                    {
                                        // Apply temporary penalty that decreases over time
                                        double penaltyFactor = 1.0 - (timeSinceFailure.TotalSeconds / 300.0);
                                        return (server, server.WeightedLoad + 5000 * penaltyFactor);
                                    }
                                }

                                // Get stored penalty
                                if (!serverPenalties.TryGetValue(server.Host, out var penalty))
                                {
                                    AccountSettingsStore.Instance.ContentServerPenalty.TryGetValue(
                                        server.Host, out penalty);
                                    serverPenalties[server.Host] = penalty;
                                }

                                // Calculate final weight (lower is better)
                                return (server, server.WeightedLoad + penalty);
                            })
                            .OrderBy(pair => pair.Item2); // Order by calculated weight

                        // Clear the current collection if it's nearly empty
                        if (availableServerEndpoints.Count < ServerEndpointMinimumSize / 4)
                        {
                            while (availableServerEndpoints.TryTake(out _)) { }
                        }

                        int serversAdded = 0;

                        // Add servers with weighting
                        foreach (var (server, weight) in weightedCdnServers)
                        {
                            // Add better servers more often (with multiplier based on weight)
                            int entriesToAdd = Math.Max(1, server.NumEntries);

                            // If server had failures, reduce entries
                            if (serverLastFailure.ContainsKey(server.Host))
                            {
                                entriesToAdd = Math.Max(1, entriesToAdd / 2);
                            }

                            for (var i = 0; i < entriesToAdd; i++)
                            {
                                try
                                {
                                    // Try to add with small timeout to avoid blocking
                                    if (availableServerEndpoints.TryAdd(server, 100))
                                    {
                                        serversAdded++;
                                    }
                                }
                                catch (Exception) { }
                            }
                        }

                        Logger.Debug($"Added {serversAdded} server endpoints to pool for app {appId}, " +
                                   $"total available: {availableServerEndpoints.Count}");

                        didPopulate = true;
                    }
                    else if (availableServerEndpoints.Count == 0 &&
                            !steamSession.steamClient.IsConnected &&
                            didPopulate)
                    {
                        Logger.Warning($"No available server endpoints and Steam disconnected for app {appId}");
                        ExhaustedToken?.Cancel();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error in CDN connection pool monitor for app {appId}: {ex.Message}");

                    // Add delay to avoid fast error loops
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        private Server BuildConnection(CancellationToken token)
        {
            if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
            {
                populatePoolEvent.Set();
            }

            Server server = null;

            try
            {
                Interlocked.Increment(ref connectionRequestsCount);
                server = availableServerEndpoints.Take(token);
                return server;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref connectionFailuresCount);
                Logger.Warning($"Operation canceled while trying to get connection for app {appId}");
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref connectionFailuresCount);
                Logger.Error($"Error building connection for app {appId}: {ex.Message}");
                throw;
            }
        }

        public async Task<Server> GetConnectionAsync(CancellationToken token)
        {
            // First check the active pool for a reusable connection
            if (activeConnectionPool.TryPop(out var connection))
            {
                Interlocked.Increment(ref connectionHitsCount);
                return connection;
            }

            // Try to acquire semaphore with timeout
            if (!await connectionSemaphore.WaitAsync(ConnectionAcquisitionTimeout, token))
            {
                Logger.Warning($"Timed out waiting for connection semaphore for app {appId}");
                throw new TimeoutException("Timed out waiting for available connection slot");
            }

            try
            {
                // Retry logic for building connection
                int retryCount = 0;
                var random = new Random();

                while (retryCount < MaxRetries)
                {
                    try
                    {
                        return BuildConnection(token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Don't retry cancellations
                    }
                    catch (Exception ex)
                    {
                        if (++retryCount >= MaxRetries)
                        {
                            Logger.Error($"Failed to get connection after {MaxRetries} retries for app {appId}");
                            throw;
                        }

                        // Exponential backoff with jitter
                        int delay = RetryDelayBaseMs * (int)Math.Pow(2, retryCount);
                        delay = delay + random.Next(-delay / 4, delay / 4); // Add jitter

                        Logger.Warning($"Retry {retryCount}/{MaxRetries} getting connection for app {appId}: {ex.Message}. Retrying in {delay}ms");
                        await Task.Delay(delay, token);
                    }
                }

                // Should never reach here due to retryCount exception, but just in case
                throw new Exception($"Failed to get connection after {MaxRetries} retries");
            }
            finally
            {
                connectionSemaphore.Release();
            }
        }

        public Server GetConnection(CancellationToken token)
        {
            // Synchronous wrapper around the async method for backward compatibility
            try
            {
                return GetConnectionAsync(token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                throw;
            }
        }

        public void ReturnBrokenConnection(Server server)
        {
            if (server == null) return;

            // Mark server as problematic
            serverLastFailure[server.Host] = DateTime.Now;

            // Increase server penalty
            serverPenalties.AddOrUpdate(
                server.Host,
                _ => 1000,
                (_, oldPenalty) => oldPenalty + 1000
            );

            // Update persistent penalty
            int currentPenalty = serverPenalties[server.Host];
            AccountSettingsStore.Instance.ContentServerPenalty[server.Host] = currentPenalty;

            Logger.Warning($"Marked server {server.Host} as broken for app {appId}, penalty: {currentPenalty}");

            // Trigger repopulation of server pool
            populatePoolEvent.Set();
        }