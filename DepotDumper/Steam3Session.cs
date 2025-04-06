using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace DepotDumper
{
    class Steam3Session
    {
        private readonly TaskCompletionSource<bool> _licenseListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task LicenseListReady => _licenseListTcs.Task;
        public string LoggedInUsername => IsLoggedOn ? logonDetails?.Username : null;

        public bool IsLoggedOn { get; private set; }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, ulong> AppTokens { get; } = [];
        public Dictionary<uint, ulong> PackageTokens { get; } = [];
        public Dictionary<uint, byte[]> DepotKeys { get; } = [];
        public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
        public Dictionary<string, byte[]> AppBetaPasswords { get; } = [];

        // Performance tracking
        private readonly ConcurrentDictionary<string, Stopwatch> perfMetrics = new ConcurrentDictionary<string, Stopwatch>();

        // Request throttling
        private readonly SemaphoreSlim appInfoSemaphore = new SemaphoreSlim(5, 5); // Max 5 concurrent app info requests
        private readonly SemaphoreSlim packageInfoSemaphore = new SemaphoreSlim(5, 5); // Max 5 concurrent package info requests
        private readonly SemaphoreSlim depotKeysSemaphore = new SemaphoreSlim(10, 10); // Max 10 concurrent depot key requests
        private readonly SemaphoreSlim cdnAuthSemaphore = new SemaphoreSlim(15, 15); // Max 15 concurrent CDN auth requests

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        readonly SteamApps steamApps;
        readonly SteamCloud steamCloud;
        readonly PublishedFile steamPublishedFile;

        readonly CallbackManager callbacks;

        readonly bool authenticatedUser;
        bool bConnecting;
        bool bAborted;
        bool bExpectingDisconnectRemote;
        bool bDidDisconnect;
        bool bIsConnectionRecovery = false;
        }

        void Connect()
        {
            bAborted = false;
            bConnecting = true;
            connectionBackoff = 0;
            authSession = null;

            ResetConnectionFlags();
            this.steamClient.Connect();
            Logger.Info("Initiating connection to Steam...");
        }

        private void Abort(bool sendLogOff = true)
        {
            if (bAborted) return;
            Logger.Warning($"Aborting Steam session. SendLogOff={sendLogOff}");
            Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            if (bAborted && !bConnecting)
            {
                Logger.Debug("Disconnect called, but already aborted/disconnected.");
                return;
            }

            if (sendLogOff && IsLoggedOn)
            {
                Logger.Info("Logging off Steam user.");
                steamUser.LogOff();
            }

            bAborted = true;
            bConnecting = false;
            bIsConnectionRecovery = false;

            if (!abortedToken.IsCancellationRequested)
            {
                abortedToken.Cancel();
                Logger.Debug("Cancellation token signaled.");
            }

            steamClient.Disconnect();
            Logger.Info("SteamClient disconnected.");

            Ansi.Progress(Ansi.ProgressState.Hidden);

            var disconnectTimeout = TimeSpan.FromSeconds(5);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (!bDidDisconnect && stopwatch.Elapsed < disconnectTimeout)
            {
                callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            }
            stopwatch.Stop();

            if (!bDidDisconnect)
            {
                Logger.Warning("Did not receive DisconnectedCallback within timeout during Disconnect.");
            }
            else
            {
                Logger.Debug("DisconnectedCallback received during Disconnect process.");
            }
        }

        private void Reconnect()
        {
            if (bAborted) return;
            Logger.Info("Attempting to reconnect to Steam...");
            bIsConnectionRecovery = true;
            steamClient.Disconnect();
        }

        private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            Console.WriteLine(" Done!");
            Logger.Info("Successfully connected to Steam.");
            bConnecting = false;

            connectionBackoff = 0;

            if (!authenticatedUser)
            {
                Console.Write("Logging anonymously into Steam3...");
                Logger.Info("Attempting anonymous login...");
                steamUser.LogOnAnonymous();
            }
            else
            {
                if (logonDetails.Username != null)
                {
                    Console.WriteLine("Logging '{0}' into Steam3...", logonDetails.Username);
                }

                if (authSession is null)
                {
                    if (logonDetails.Username != null && logonDetails.Password != null && logonDetails.AccessToken is null)
                    {
                        try
                        {
                            _ = AccountSettingsStore.Instance.GuardData.TryGetValue(logonDetails.Username, out var guarddata);
                            authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new SteamKit2.Authentication.AuthSessionDetails
                            {
                                Username = logonDetails.Username,
                                Password = logonDetails.Password,
                                IsPersistentSession = DepotDumper.Config.RememberPassword,
                                GuardData = guarddata,
                                Authenticator = new UserConsoleAuthenticator(),
                            });
                        }
                        catch (TaskCanceledException) { return; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                    else if (logonDetails.AccessToken is null && DepotDumper.Config.UseQrCode)
                    {
                        Console.WriteLine("Logging in with QR code...");
                        try
                        {
                            var session = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                            {
                                IsPersistentSession = DepotDumper.Config.RememberPassword,
                                Authenticator = new UserConsoleAuthenticator(),
                            });
                            authSession = session;
                            session.ChallengeURLChanged = () =>
                            {
                                Console.WriteLine("\nThe QR code has changed:");
                                DisplayQrCode(session.ChallengeURL);
                            };
                            DisplayQrCode(session.ChallengeURL);
                        }
                        catch (TaskCanceledException) { return; }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                }

                if (authSession != null)
                {
                    try
                    {
                        var result = await authSession.PollingWaitForResultAsync();
                        logonDetails.Username = result.AccountName;
                        logonDetails.Password = null;
                        logonDetails.AccessToken = result.RefreshToken;

                        if (result.NewGuardData != null) { AccountSettingsStore.Instance.GuardData[result.AccountName] = result.NewGuardData; }
                        else { AccountSettingsStore.Instance.GuardData.Remove(result.AccountName); }
                        AccountSettingsStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
                        AccountSettingsStore.Save();
                    }
                    catch (TaskCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                        Abort(false);
                        return;
                    }
                    authSession = null;
                }

                if (logonDetails.Username != null || logonDetails.AccessToken != null)
                {
                    steamUser.LogOn(logonDetails);
                    Logger.Info($"Attempting login for user: {logonDetails.Username ?? "using token"}...");
                }
                else
                {
                    Logger.Error("Authenticated user flow reached without username or token in logonDetails.");
                    Abort(false);
                }
            }
        }


        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            bDidDisconnect = true;

            DebugLog.WriteLine(nameof(Steam3Session), $"Disconnected: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");
            Logger.Info($"Steam disconnected. UserInitiated={disconnected.UserInitiated}, IsConnectionRecovery={bIsConnectionRecovery}, ExpectingRemoteDisconnect={bExpectingDisconnectRemote}");

            if (!_licenseListTcs.Task.IsCompleted)
            {
                _licenseListTcs.TrySetException(new Exception("Disconnected from Steam before license list could be retrieved."));
                Logger.Warning("Signaled license TCS with exception due to disconnect.");
            }

            if (!bAborted && (bIsConnectionRecovery || (!disconnected.UserInitiated && !bExpectingDisconnectRemote)))
            {
                connectionBackoff += 1;
                if (connectionBackoff >= 10)
                {
                    Console.WriteLine("Could not connect to Steam after 10 tries");
                    Logger.Error("Could not connect to Steam after 10 tries.");
                    Abort(false);
                }
                else
                {
                    int delaySeconds = Math.Min(60, (int)Math.Pow(2, connectionBackoff));
                    Console.WriteLine($"Lost connection to Steam. Reconnecting in {delaySeconds} seconds (Attempt #{connectionBackoff})...");
                    Logger.Warning($"Lost connection to Steam. Reconnecting in {delaySeconds}s (Attempt #{connectionBackoff}).");

                    Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));

                    ResetConnectionFlags();
                    bConnecting = true;
                    steamClient.Connect();
                }
            }
            else if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
            {
                Console.WriteLine("Disconnected from Steam");
                Logger.Info("Disconnected from Steam as expected or user-initiated.");

                if (!bAborted)
                {
                    Abort(false);
                }
            }
            else if (bAborted)
            {
                Logger.Debug("Disconnected callback handled, session was already aborted.");
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            Logger.Info($"LogOnCallback received. Result: {loggedOn.Result}");

            var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            var isAccessToken = DepotDumper.Config.RememberPassword && logonDetails.AccessToken != null &&
                loggedOn.Result is EResult.InvalidPassword
                or EResult.InvalidSignature
                or EResult.AccessDenied
                or EResult.Expired
                or EResult.Revoked;

            if (isSteamGuard || is2FA || isAccessToken)
            {
                Logger.Warning($"Logon failed, requires user intervention. Result: {loggedOn.Result}");
                bExpectingDisconnectRemote = true;
                Abort(false);

                if (!isAccessToken) { Console.WriteLine("This account is protected by Steam Guard."); }

                if (is2FA)
                {
                    do
                    {
                        Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                        logonDetails.TwoFactorCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.TwoFactorCode);
                }
                else if (isAccessToken)
                {
                    AccountSettingsStore.Instance.LoginTokens.Remove(logonDetails.Username);
                    AccountSettingsStore.Save();
                    Console.WriteLine($"Access token was rejected ({loggedOn.Result}). Please login with password/QR again.");
                    Logger.Warning($"Access token was rejected ({loggedOn.Result}). Removing token and retrying requires password/QR.");
                    // Abort(false) was already called, Connect will happen after DisconnectedCallback
                    return; // Important: Return here, don't fall through
                }
                else
                {
                    do
                    {
                        Console.Write("Please enter the authentication code sent to your email address: ");
                        logonDetails.AuthCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.AuthCode);
                }

                Console.Write("Retrying Steam3 connection...");
                Connect();
                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                Logger.Info("Logon result is TryAnotherCM. Attempting reconnection...");
                Console.Write("Retrying Steam3 connection (TryAnotherCM)...");
                Reconnect();
                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Logger.Error($"Logon failed due to ServiceUnavailable ({loggedOn.Result}). Aborting.");
                Abort(false);
                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Logger.Error($"Logon failed with unexpected result: {loggedOn.Result}. Aborting.");
                Abort();
                return;
            }

            Console.WriteLine(" Done!");
            Logger.Info($"Successfully logged on to Steam. Account: {steamUser.SteamID?.Render()}, CellID: {loggedOn.CellID}");

            this.seq++;
            IsLoggedOn = true;

            if (DepotDumper.Config.CellID == 0)
            {
                Console.WriteLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
                Logger.Info($"Using suggested CellID: {loggedOn.CellID}");
                DepotDumper.Config.CellID = (int)loggedOn.CellID;
            }

            Logger.Debug("Logon successful. Expecting LicenseListCallback.");
        }


        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            Logger.Info($"LicenseListCallback received. Result: {licenseList.Result}");

            try
            {
                if (licenseList.Result != EResult.OK)
                {
                    Console.WriteLine("Unable to get license list: {0} ", licenseList.Result);
                    Logger.Error($"Failed to get license list: {licenseList.Result}");
                    _licenseListTcs.TrySetException(new Exception($"Failed to get license list: {licenseList.Result}"));
                    Abort();
                    return;
                }

                Console.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
                Logger.Info($"Received {licenseList.LicenseList.Count} licenses.");
                Licenses = licenseList.LicenseList;

                PackageTokens.Clear();
                foreach (var license in licenseList.LicenseList)
                {
                    if (license.AccessToken > 0)
                    {
                        PackageTokens[license.PackageID] = license.AccessToken;
                    }
                }
                Logger.Info($"Processed {PackageTokens.Count} package tokens from license list.");

                _licenseListTcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception in LicenseListCallback: {ex}");
                _licenseListTcs.TrySetException(ex);
                Abort();
            }
        }


        private static void DisplayQrCode(string challengeUrl)
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);

            char darkBlock = '█';
            char lightBlock = ' ';
            int moduleSize = 1;
            bool drawQuietZones = true;

            var sb = new System.Text.StringBuilder();
            int size = qrCodeData.ModuleMatrix.Count;
            int quietZoneSize = drawQuietZones ? 2 : 0;

            for (int qy = 0; qy < quietZoneSize; qy++)
            {
                for (int qx = 0; qx < (size + 2 * quietZoneSize) * moduleSize; qx++) sb.Append(lightBlock);
                sb.AppendLine();
            }

            for (int y = 0; y < size; y++)
            {
                for (int qx = 0; qx < quietZoneSize * moduleSize; qx++) sb.Append(lightBlock);
                for (int x = 0; x < size; x++)
                {
                    char blockChar = qrCodeData.ModuleMatrix[y][x] ? darkBlock : lightBlock;
                    for (int m = 0; m < moduleSize; m++) sb.Append(blockChar);
                }
                for (int qx = 0; qx < quietZoneSize * moduleSize; qx++) sb.Append(lightBlock);
                sb.AppendLine();
            }

            for (int qy = 0; qy < quietZoneSize; qy++)
            {
                for (int qx = 0; qx < (size + 2 * quietZoneSize) * moduleSize; qx++) sb.Append(lightBlock);
                sb.AppendLine();
            }

            Console.WriteLine();
            Console.WriteLine("Use the Steam Mobile App to sign in with this QR code:");
            Console.WriteLine();
            Console.Write(sb.ToString());
            Console.WriteLine();
            Console.WriteLine("If the QR code doesn't display properly, try resizing your terminal window.");
            Console.WriteLine();
            Logger.Info("QR Code displayed for authentication.");
        }
    };
int connectionBackoff;
int seq;
AuthSession authSession;
readonly CancellationTokenSource abortedToken = new();

readonly SteamUser.LogOnDetails logonDetails;

private enum ErrorCategory
{
    Network,
    Authentication,
    RateLimit,
    ServerError,
    ClientError,
    Unknown
}

public Steam3Session(SteamUser.LogOnDetails details)
{
    this.logonDetails = details;
    this.authenticatedUser = details.Username != null || DepotDumper.Config.UseQrCode;

    var clientConfiguration = SteamConfiguration.Create(config =>
        config
            .WithHttpClientFactory(HttpClientFactory.CreateHttpClient)
    );

    this.steamClient = new SteamClient(clientConfiguration);

    this.steamUser = this.steamClient.GetHandler<SteamUser>();
    this.steamApps = this.steamClient.GetHandler<SteamApps>();
    this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
    var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
    this.steamPublishedFile = steamUnifiedMessages.CreateService<PublishedFile>();
    this.steamContent = this.steamClient.GetHandler<SteamContent>();

    this.callbacks = new CallbackManager(this.steamClient);

    this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
    this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
    this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
    this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);

    Console.Write("Connecting to Steam3...");
    Connect();
}

public delegate bool WaitCondition();

private readonly Lock steamLock = new();

public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
{
    while (!bAborted && !waiter())
    {
        lock (steamLock)
        {
            submitter();
        }

        var seq = this.seq;
        do
        {
            lock (steamLock)
            {
                callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        } while (!bAborted && this.seq == seq && !waiter());
    }

    return bAborted;
}

public bool WaitForCredentials()
{
    if (IsLoggedOn || bAborted)
        return IsLoggedOn;

    WaitUntilCallback(() => { }, () => IsLoggedOn);

    return IsLoggedOn;
}

public async Task TickCallbacks()
{
    var token = abortedToken.Token;

    try
    {
        while (!token.IsCancellationRequested)
        {
            await callbacks.RunWaitCallbackAsync(token);
        }
    }
    catch (OperationCanceledException)
    {
    }
}

// Performance tracking methods
private void StartMetric(string name)
{
    var sw = new Stopwatch();
    sw.Start();
    perfMetrics[name] = sw;
}

private TimeSpan StopMetric(string name)
{
    if (perfMetrics.TryRemove(name, out var sw))
    {
        sw.Stop();
        return sw.Elapsed;
    }
    return TimeSpan.Zero;
}

private void LogMetric(string name)
{
    if (perfMetrics.TryGetValue(name, out var sw))
    {
        Logger.Debug($"Performance: {name} - {sw.Elapsed.TotalSeconds:F2}s");
    }
}

// Error categorization for better retry decisions
private static ErrorCategory CategorizeError(Exception ex, EResult? result = null)
{
    if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException || ex is TimeoutException)
        return ErrorCategory.Network;

    if (result.HasValue)
    {
        switch (result.Value)
        {
            case EResult.AccessDenied:
            case EResult.InvalidPassword:
            case EResult.AccountLogonDenied:
                return ErrorCategory.Authentication;

            case EResult.RateLimitExceeded:
            case EResult.ServiceUnavailable:
            case EResult.TryAnotherCM:
                return ErrorCategory.RateLimit;

            case EResult.Busy:
            case EResult.ServiceUnavailable:
                return ErrorCategory.ServerError;

            default:
                return ErrorCategory.Unknown;
        }
    }

    return ErrorCategory.Unknown;
}

// Retry with exponential backoff and jitter
private async Task<T> RetryWithBackoffAsync<T>(Func<Task<T>> operation, int maxRetries = 5, int initialDelayMs = 200)
{
    Random jitter = new Random();

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (attempt < maxRetries)
        {
            var category = CategorizeError(ex);

            // Skip retries for authentication errors
            if (category == ErrorCategory.Authentication)
                throw;

            // Calculate backoff with jitter
            int delayMs = initialDelayMs * (int)Math.Pow(2, attempt);
            delayMs = delayMs + jitter.Next(-delayMs / 4, delayMs / 4);

            Logger.Warning($"Attempt {attempt + 1}/{maxRetries} failed: {ex.Message}. Retrying after {delayMs}ms");
            await Task.Delay(delayMs);
        }
    }

    throw new Exception($"Operation failed after {maxRetries} attempts");
}

// Improved AppInfo request with batching
public async Task RequestMultipleAppInfoAsync(IEnumerable<uint> appIds, bool bForce = false)
{
    var appsToRequest = appIds.Where(id => !AppInfo.ContainsKey(id) || bForce).ToList();
    if (appsToRequest.Count == 0 || bAborted)
        return;

    Logger.Info($"Requesting info for {appsToRequest.Count} apps in bulk");

    // Request tokens in batches of 50 (Steam API limit)
    for (int i = 0; i < appsToRequest.Count; i += 50)
    {
        var batchIds = appsToRequest.Skip(i).Take(50).ToList();

        await appInfoSemaphore.WaitAsync();
        try
        {
            string metricName = $"app_tokens_batch_{i / 50}";
            StartMetric(metricName);

            var jobResult = await steamApps.PICSGetAccessTokens(batchIds, Enumerable.Empty<uint>());
            var appTokens = jobResult;

            StopMetric(metricName);

            foreach (var token_dict in appTokens.AppTokens)
            {
                this.AppTokens[token_dict.Key] = token_dict.Value;
                Logger.Debug($"Stored access token for AppID {token_dict.Key}");
            }

            // Prepare requests with tokens
            var requests = new List<SteamApps.PICSRequest>();
            foreach (var appId in batchIds)
            {
                var request = new SteamApps.PICSRequest(appId);
                if (AppTokens.TryGetValue(appId, out var token))
                    request.AccessToken = token;
                requests.Add(request);
            }

            // Get product info
            metricName = $"app_info_batch_{i / 50}";
            StartMetric(metricName);

            var appInfoMultiple = await steamApps.PICSGetProductInfo(
                requests, Enumerable.Empty<SteamApps.PICSRequest>());

            var elapsed = StopMetric(metricName);
            Logger.Debug($"Got info for {batchIds.Count} apps in {elapsed.TotalSeconds:F2}s");

            if (appInfoMultiple?.Results == null)
            {
                Logger.Error($"PICSGetProductInfo returned null result for batch of {batchIds.Count} apps");
                continue;
            }

            // Process results
            foreach (var appInfoResult in appInfoMultiple.Results)
            {
                if (appInfoResult.Apps == null) continue;

                foreach (var app_value in appInfoResult.Apps)
                {
                    var app = app_value.Value;
                    if (app == null) continue;

                    Logger.Debug($"Got AppInfo for {app.ID}");
                    AppInfo[app.ID] = app;
                }

                if (appInfoResult.UnknownApps == null) continue;

                foreach (var app in appInfoResult.UnknownApps)
                {
                    Logger.Warning($"AppInfo request resulted in UnknownApp for AppID {app}");
                    AppInfo[app] = null;
                }
            }
        }
        finally
        {
            appInfoSemaphore.Release();
        }

        // Add a small delay between batches to avoid rate limiting
        if (i + 50 < appsToRequest.Count)
        {
            await Task.Delay(300);
        }
    }
}

public async Task RequestAppInfo(uint appId, bool bForce = false)
{
    await RequestMultipleAppInfoAsync(new[] { appId }, bForce);
}

public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
{
    var packagesToRequest = packageIds.Except(PackageInfo.Keys).Where(id => id != 0).Distinct().ToList();

    if (packagesToRequest.Count == 0 || bAborted)
        return;

    Logger.Info($"Requesting PackageInfo for {packagesToRequest.Count} packages");

    // Process packages in batches of 50
    for (int i = 0; i < packagesToRequest.Count; i += 50)
    {
        var batchIds = packagesToRequest.Skip(i).Take(50).ToList();

        await packageInfoSemaphore.WaitAsync();
        try
        {
            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in batchIds)
            {
                var request = new SteamApps.PICSRequest(package);

                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                packageRequests.Add(request);
            }

            string metricName = $"package_info_batch_{i / 50}";
            StartMetric(metricName);

            var packageInfoMultiple = await steamApps.PICSGetProductInfo(
                Enumerable.Empty<SteamApps.PICSRequest>(), packageRequests);

            var elapsed = StopMetric(metricName);
            Logger.Debug($"Got info for {batchIds.Count} packages in {elapsed.TotalSeconds:F2}s");

            if (packageInfoMultiple?.Results == null)
            {
                Logger.Error($"PICSGetProductInfo returned null result for package request batch.");
                foreach (var pkgId in batchIds)
                {
                    PackageInfo[pkgId] = null;
                }
                continue;
            }

            foreach (var packageInfoResult in packageInfoMultiple.Results)
            {
                if (packageInfoResult.Packages == null) continue;

                foreach (var package_value in packageInfoResult.Packages)
                {
                    var package = package_value.Value;
                    if (package == null) continue;

                    Logger.Debug($"Received PackageInfo for {package.ID}");
                    PackageInfo[package.ID] = package;
                }

                if (packageInfoResult.UnknownPackages == null) continue;

                foreach (var package in packageInfoResult.UnknownPackages)
                {
                    Logger.Warning($"PackageInfo request resulted in UnknownPackage for PackageID {package}");
                    PackageInfo[package] = null;
                }
            }
        }
        finally
        {
            packageInfoSemaphore.Release();
        }

        // Add a small delay between batches to avoid rate limiting
        if (i + 50 < packagesToRequest.Count)
        {
            await Task.Delay(300);
        }
    }
}


public async Task<bool> RequestFreeAppLicense(uint appId)
{
    if (bAborted) return false;

    try
    {
        return await RetryWithBackoffAsync(async () =>
        {
            var resultInfo = await steamApps.RequestFreeLicense(new List<uint> { appId });

            if (resultInfo == null)
            {
                Logger.Warning($"RequestFreeLicense for app {appId} returned null.");
                return false;
            }

            bool granted = resultInfo.GrantedApps.Contains(appId);
            Logger.Info($"RequestFreeLicense for app {appId}: Result={resultInfo.Result}, Granted={granted}");
            return granted;
        }, 3, 500);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to request FreeOnDemand license for app {appId}: {ex.Message}");
        Logger.Error($"Exception during RequestFreeLicense for app {appId}: {ex}");
        return false;
    }
}

// Batch depot key request
public async Task RequestMultipleDepotKeysAsync(IEnumerable<(uint depotId, uint appId)> depotAppPairs)
{
    var pairsToRequest = depotAppPairs.Where(p => !DepotKeys.ContainsKey(p.depotId)).ToList();
    if (pairsToRequest.Count == 0 || bAborted)
        return;

    Logger.Info($"Requesting {pairsToRequest.Count} depot keys in parallel");

    // Perform requests in parallel with a limit
    var tasks = new List<Task>();

    foreach (var (depotId, appId) in pairsToRequest)
    {
        await depotKeysSemaphore.WaitAsync();
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                await RequestDepotKey(depotId, appId);
            }
            finally
            {
                depotKeysSemaphore.Release();
            }
        }));
    }

    await Task.WhenAll(tasks);
}

public async Task RequestDepotKey(uint depotId, uint appid = 0)
{
    if (DepotKeys.ContainsKey(depotId) || bAborted)
        return;

    string metricName = $"depot_key_{depotId}";
    StartMetric(metricName);

    try
    {
        var depotKeyResult = await RetryWithBackoffAsync(async () =>
            await steamApps.GetDepotDecryptionKey(depotId, appid), 3, 500);

        var elapsed = StopMetric(metricName);

        if (depotKeyResult == null)
        {
            Logger.Error($"GetDepotDecryptionKey for depot {depotId} (app context {appid}) returned null after {elapsed.TotalSeconds:F2}s");
            return;
        }

        Logger.Info($"Depot key result for {depotKeyResult.DepotID} (app {appid}): {depotKeyResult.Result} in {elapsed.TotalSeconds:F2}s");

        if (depotKeyResult.Result != EResult.OK)
        {
            return;
        }

        if (depotKeyResult.DepotKey == null || depotKeyResult.DepotKey.Length == 0)
        {
            Logger.Warning($"GetDepotDecryptionKey for depot {depotId} returned OK but key was null or empty.");
            return;
        }

        DepotKeys[depotKeyResult.DepotID] = depotKeyResult.DepotKey;
        Logger.Debug($"Stored depot key for {depotKeyResult.DepotID}");
    }
    catch (Exception ex)
    {
        StopMetric(metricName);
        Logger.Error($"Error getting depot key for {depotId} (app {appid}): {ex.Message}");
    }
}

public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
{
    if (bAborted)
        return 0;

    string metricName = $"manifest_code_{depotId}_{manifestId}";
    StartMetric(metricName);

    try
    {
        var requestCode = await RetryWithBackoffAsync(async () =>
            await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch), 3, 500);

        var elapsed = StopMetric(metricName);

        if (requestCode == 0)
        {
            Logger.Info($"Manifest request code is 0 for depot {depotId}, app {appId}, manifest {manifestId} (branch '{branch}'). Assuming public or failure. Time: {elapsed.TotalSeconds:F2}s");
        }
        else
        {
            Logger.Info($"Got manifest request code {requestCode} for depot {depotId}, app {appId}, manifest {manifestId} (branch '{branch}'). Time: {elapsed.TotalSeconds:F2}s");
        }

        return requestCode;
    }
    catch (Exception ex)
    {
        var elapsed = StopMetric(metricName);
        Logger.Error($"Error getting manifest request code for depot {depotId}, app {appId}, manifest {manifestId}: {ex.Message} in {elapsed.TotalSeconds:F2}s");
        return 0;
    }
}


public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
{
    if (server?.Host == null)
    {
        Logger.Warning("RequestCDNAuthToken called with null server or server host.");
        return;
    }

    var cdnKey = (depotid, server.Host);
    var completion = new TaskCompletionSource<SteamContent.CDNAuthToken>(TaskCreationOptions.RunContinuationsAsynchronously);

    if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
    {
        if (CDNAuthTokens.TryGetValue(cdnKey, out var existingTcs))
        {
            Logger.Debug($"CDN auth token request already in progress for {server.Host}, awaiting existing task.");
            await existingTcs.Task;
        }
        else if (!bAborted)
        {
            Logger.Warning($"Failed to add CDN auth token TCS for {server.Host}, but no existing task found.");
        }
        return;
    }

    await cdnAuthSemaphore.WaitAsync();
    try
    {
        DebugLog.WriteLine(nameof(Steam3Session), $"Requesting CDN auth token for {server.Host}");
        Logger.Debug($"Requesting CDN auth token for {server.Host} (App: {appid}, Depot: {depotid})");

        string metricName = $"cdn_auth_{cdnKey.depotid}_{server.Host.GetHashCode()}";
        StartMetric(metricName);

        try
        {
            var cdnAuth = await RetryWithBackoffAsync(async () =>
                await steamContent.GetCDNAuthToken(appid, depotid, server.Host), 3, 500);

            var elapsed = StopMetric(metricName);

            if (cdnAuth == null)
            {
                Logger.Error($"GetCDNAuthToken for {server.Host} returned null after {elapsed.TotalSeconds:F2}s");
                completion.TrySetException(new Exception($"GetCDNAuthToken returned null for host {server.Host}"));
                CDNAuthTokens.TryRemove(cdnKey, out _);
                return;
            }

            Logger.Info($"CDN auth token for {server.Host}: Result={cdnAuth.Result}, Expires={cdnAuth.Expiration} in {elapsed.TotalSeconds:F2}s");

            if (cdnAuth.Result != EResult.OK)
            {
                completion.TrySetException(new Exception($"Failed to get CDN auth token for {server.Host}. Result: {cdnAuth.Result}"));
            }
            else
            {
                completion.TrySetResult(cdnAuth);
            }
        }
        catch (Exception ex)
        {
            StopMetric(metricName);
            Logger.Error($"Exception during GetCDNAuthToken for {server.Host}: {ex}");
            completion.TrySetException(ex);
        }
    }
    finally
    {
        cdnAuthSemaphore.Release();
        if (!completion.Task.IsCompleted)
        {
            completion.TrySetCanceled();
        }
        if (completion.Task.IsFaulted || completion.Task.IsCanceled)
        {
            CDNAuthTokens.TryRemove(cdnKey, out _);
        }
    }
}

public async Task CheckAppBetaPassword(uint appid, string password)
{
    if (bAborted) return;

    string metricName = $"beta_password_{appid}";
    StartMetric(metricName);

    try
    {
        var appPasswordResult = await RetryWithBackoffAsync(async () =>
            await steamApps.CheckAppBetaPassword(appid, password), 3, 500);

        var elapsed = StopMetric(metricName);

        if (appPasswordResult == null)
        {
            Logger.Error($"CheckAppBetaPassword for app {appid} returned null after {elapsed.TotalSeconds:F2}s");
            return;
        }

        Logger.Info($"CheckAppBetaPassword result for app {appid}: {appPasswordResult.Result}, Keys retrieved: {appPasswordResult.BetaPasswords.Count} in {elapsed.TotalSeconds:F2}s");

        if (appPasswordResult.Result == EResult.OK && appPasswordResult.BetaPasswords != null)
        {
            foreach (var entry in appPasswordResult.BetaPasswords)
            {
                AppBetaPasswords[entry.Key] = entry.Value;
                Logger.Debug($"Stored beta password key '{entry.Key}' for app {appid}");
            }
        }
        else if (appPasswordResult.Result != EResult.OK)
        {
            Logger.Warning($"Failed to check beta password for app {appid}. Result: {appPasswordResult.Result}");
        }
    }
    catch (Exception ex)
    {
        StopMetric(metricName);
        Logger.Error($"Error checking beta password for app {appid}: {ex.Message}");
    }
}

public async Task<PublishedFileDetails> GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
{
    if (bAborted) return null;

    var pubFileRequest = new CPublishedFile_GetDetails_Request { appid = appId };
    pubFileRequest.publishedfileids.Add(pubFile);

    string metricName = $"published_file_{pubFile}";
    StartMetric(metricName);

    try
    {
        var detailsResponse = await RetryWithBackoffAsync(async () =>
            await steamPublishedFile.GetDetails(pubFileRequest), 3, 500);

        var elapsed = StopMetric(metricName);

        if (detailsResponse == null || detailsResponse.Body == null)
        {
            Logger.Error($"GetPublishedFileDetails response or body was null for pubfile {pubFile} (App: {appId}) after {elapsed.TotalSeconds:F2}s");
            throw new Exception($"GetDetails returned null response/body for pubfile {pubFile}.");
        }

        var detailsBody = detailsResponse.Body;

        Logger.Info($"GetPublishedFileDetails result for pubfile {pubFile} (App: {appId}): {detailsResponse.Result} in {elapsed.TotalSeconds:F2}s");

        if (detailsResponse.Result == EResult.OK)
        {
            return detailsBody.publishedfiledetails?.FirstOrDefault();
        }

        Logger.Warning($"GetPublishedFileDetails failed for pubfile {pubFile}. EResult: {detailsResponse.Result}");
        throw new Exception($"EResult {(int)detailsResponse.Result} ({detailsResponse.Result}) while retrieving file details for pubfile {pubFile}.");
    }
    catch (Exception ex)
    {
        StopMetric(metricName);
        Logger.Error($"Error getting published file details for {pubFile} (app {appId}): {ex.Message}");
        throw;
    }
}

public async Task<SteamCloud.UGCDetailsCallback> GetUGCDetails(UGCHandle ugcHandle)
{
    if (bAborted) return null;

    string metricName = $"ugc_details_{ugcHandle}";
    StartMetric(metricName);

    try
    {
        var callback = await RetryWithBackoffAsync(async () =>
            await steamCloud.RequestUGCDetails(ugcHandle), 3, 500);

        var elapsed = StopMetric(metricName);

        if (callback == null)
        {
            Logger.Error($"RequestUGCDetails returned null callback for UGC handle {ugcHandle} after {elapsed.TotalSeconds:F2}s");
            throw new Exception($"RequestUGCDetails returned null for {ugcHandle}.");
        }

        Logger.Info($"GetUGCDetails result for handle {ugcHandle}: {callback.Result} in {elapsed.TotalSeconds:F2}s");

        if (callback.Result == EResult.OK)
        {
            return callback;
        }
        else if (callback.Result == EResult.FileNotFound)
        {
            Logger.Warning($"UGC details not found for handle {ugcHandle}.");
            return null;
        }

        Logger.Error($"GetUGCDetails failed for handle {ugcHandle}. EResult: {callback.Result}");
        throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
    }
    catch (Exception ex)
    {
        StopMetric(metricName);
        Logger.Error($"Error getting UGC details for {ugcHandle}: {ex.Message}");
        throw;
    }
}


private void ResetConnectionFlags()
{
    bExpectingDisconnectRemote = false;
    bDidDisconnect = false;
    bIsConnectionRecovery = false;

    if (!abortedToken.IsCancellationRequested)
    {
        abortedToken.Cancel();
        Logger.Debug("Cancellation token signaled.");
    }

    steamClient.Disconnect();
    Logger.Info("SteamClient disconnected.");

    Ansi.Progress(Ansi.ProgressState.Hidden);

    var disconnectTimeout = TimeSpan.FromSeconds(5);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    while (!bDidDisconnect && stopwatch.Elapsed < disconnectTimeout)
    {
        callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
    }
    stopwatch.Stop();

    if (!bDidDisconnect)
    {
        Logger.Warning("Did not receive DisconnectedCallback within timeout during Disconnect.");
    }
    else
    {
        Logger.Debug("DisconnectedCallback received during Disconnect process.");
    }
}

private void Reconnect()
{
    if (bAborted) return;
    Logger.Info("Attempting to reconnect to Steam...");
    bIsConnectionRecovery = true;
    steamClient.Disconnect();
}