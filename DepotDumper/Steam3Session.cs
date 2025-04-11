using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public class Steam3Session
    {
        public readonly TaskCompletionSource<bool> _licenseListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        bool bIsConnectionRecovery;
        int connectionBackoff;
        int seq;
        AuthSession authSession;
        readonly CancellationTokenSource abortedToken = new();

        readonly SteamUser.LogOnDetails logonDetails;

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

        public readonly Lock steamLock = new();

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

        public async Task RequestAppInfo(uint appId, bool bForce = false)
        {
            if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
                return;

            var jobResult = await steamApps.PICSGetAccessTokens(new List<uint> { appId }, Enumerable.Empty<uint>());
            var appTokens = jobResult;

            if (appTokens.AppTokensDenied.Contains(appId))
            {
                Console.WriteLine("Insufficient privileges to get access token for app {0}", appId);
                Logger.Warning($"Insufficient privileges for AppID {appId} access token.");
            }

            foreach (var token_dict in appTokens.AppTokens)
            {
                this.AppTokens[token_dict.Key] = token_dict.Value;
                Logger.Debug($"Stored access token for AppID {token_dict.Key}");
            }

            var request = new SteamApps.PICSRequest(appId);

            if (AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
                Logger.Debug($"Using access token for AppID {appId} request.");
            }
            else
            {
                Logger.Debug($"No access token found for AppID {appId} request.");
            }


            var picsInfo = await steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, Enumerable.Empty<SteamApps.PICSRequest>());
            var appInfoMultiple = picsInfo;

            if (appInfoMultiple?.Results == null)
            {
                Logger.Error($"PICSGetProductInfo returned null result for AppID {appId}");
                AppInfo[appId] = null;
                return;
            }


            foreach (var appInfoResult in appInfoMultiple.Results)
            {
                if (appInfoResult.Apps == null) continue;

                foreach (var app_value in appInfoResult.Apps)
                {
                    var app = app_value.Value;
                    if (app == null) continue;

                    Console.WriteLine("Got AppInfo for {0}", app.ID);
                    Logger.Info($"Received AppInfo for {app.ID}");
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


        public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            var packagesToRequest = packageIds.Except(PackageInfo.Keys).Where(id => id != 0).Distinct().ToList();

            if (packagesToRequest.Count == 0 || bAborted)
                return;

            Logger.Debug($"Requesting PackageInfo for {packagesToRequest.Count} packages: {string.Join(", ", packagesToRequest)}");


            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in packagesToRequest)
            {
                var request = new SteamApps.PICSRequest(package);

                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                packageRequests.Add(request);
            }

            var picsInfo = await steamApps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), packageRequests);
            var packageInfoMultiple = picsInfo;

            if (packageInfoMultiple?.Results == null)
            {
                Logger.Error($"PICSGetProductInfo returned null result for package request.");
                foreach (var pkgId in packagesToRequest)
                {
                    PackageInfo[pkgId] = null;
                }
                return;
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


        public async Task<bool> RequestFreeAppLicense(uint appId)
        {
            if (bAborted) return false;

            try
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to request FreeOnDemand license for app {appId}: {ex.Message}");
                Logger.Error($"Exception during RequestFreeLicense for app {appId}: {ex}");
                return false;
            }
        }


        public async Task RequestDepotKey(uint depotId, uint appid = 0)
{
    if (DepotKeys.ContainsKey(depotId) || bAborted)
        return;

    Logger.Info($"Requesting depot key for {depotId} using app context {appid}");
    var depotKeyResult = await steamApps.GetDepotDecryptionKey(depotId, appid);
    
    if (depotKeyResult == null)
    {
        Logger.Error($"GetDepotDecryptionKey for depot {depotId} (app context {appid}) returned null.");
        return;
    }
    
    Console.WriteLine("Got depot key for {0} result: {1}", depotKeyResult.DepotID, depotKeyResult.Result);
    Logger.Info($"Depot key result for {depotKeyResult.DepotID} (app context {appid}): {depotKeyResult.Result}");

    if (depotKeyResult.Result != EResult.OK)
    {
        return;
    }

    DepotKeys[depotKeyResult.DepotID] = depotKeyResult.DepotKey;
    Logger.Debug($"Stored depot key for {depotKeyResult.DepotID}");
}
        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (bAborted)
                return 0;

            var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

            if (requestCode == 0)
            {
                Console.WriteLine($"Manifest request code received as 0 for depot {depotId}, app {appId}, manifest {manifestId} (branch '{branch}')");
                Logger.Info($"Manifest request code is 0 for depot {depotId}, app {appId}, manifest {manifestId} (branch '{branch}'). Assuming public or failure.");
            }
            else
            {
                Console.WriteLine($"Got manifest request code for depot {depotId} from app {appId}, manifest {manifestId}, result: {requestCode}");
                Logger.Info($"Got manifest request code {requestCode} for depot {depotId}, app {appId}, manifest {manifestId} (branch '{branch}').");
            }

            return requestCode;
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

            DebugLog.WriteLine(nameof(Steam3Session), $"Requesting CDN auth token for {server.Host}");
            Logger.Debug($"Requesting CDN auth token for {server.Host} (App: {appid}, Depot: {depotid})");

            try
            {
                var cdnAuth = await steamContent.GetCDNAuthToken(appid, depotid, server.Host);

                if (cdnAuth == null)
                {
                    Logger.Error($"GetCDNAuthToken for {server.Host} returned null.");
                    completion.TrySetException(new Exception($"GetCDNAuthToken returned null for host {server.Host}"));
                    CDNAuthTokens.TryRemove(cdnKey, out _);
                    return;
                }

                Console.WriteLine($"Got CDN auth token for {server.Host} result: {cdnAuth.Result} (expires {cdnAuth.Expiration})");
                Logger.Info($"CDN auth token for {server.Host}: Result={cdnAuth.Result}, Expires={cdnAuth.Expiration}");

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
                Logger.Error($"Exception during GetCDNAuthToken for {server.Host}: {ex}");
                completion.TrySetException(ex);
            }
            finally
            {
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

            var appPasswordResult = await steamApps.CheckAppBetaPassword(appid, password);

            if (appPasswordResult == null)
            {
                Logger.Error($"CheckAppBetaPassword for app {appid} returned null.");
                return;
            }


            Console.WriteLine("Retrieved {0} beta keys with result: {1}", appPasswordResult.BetaPasswords.Count, appPasswordResult.Result);
            Logger.Info($"CheckAppBetaPassword result for app {appid}: {appPasswordResult.Result}, Keys retrieved: {appPasswordResult.BetaPasswords.Count}");

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

        public async Task<PublishedFileDetails> GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            if (bAborted) return null;

            var pubFileRequest = new CPublishedFile_GetDetails_Request { appid = appId };
            pubFileRequest.publishedfileids.Add(pubFile);

            var detailsResponse = await steamPublishedFile.GetDetails(pubFileRequest);

            if (detailsResponse == null || detailsResponse.Body == null)
            {
                Logger.Error($"GetPublishedFileDetails response or body was null for pubfile {pubFile} (App: {appId}).");
                throw new Exception($"GetDetails returned null response/body for pubfile {pubFile}.");
            }

            var detailsBody = detailsResponse.Body;

            Logger.Info($"GetPublishedFileDetails result for pubfile {pubFile} (App: {appId}): {detailsResponse.Result}");

            if (detailsResponse.Result == EResult.OK)
            {
                return detailsBody.publishedfiledetails?.FirstOrDefault();
            }

            Logger.Warning($"GetPublishedFileDetails failed for pubfile {pubFile}. EResult: {detailsResponse.Result}");
            throw new Exception($"EResult {(int)detailsResponse.Result} ({detailsResponse.Result}) while retrieving file details for pubfile {pubFile}.");
        }



        public async Task<SteamCloud.UGCDetailsCallback> GetUGCDetails(UGCHandle ugcHandle)
        {
            if (bAborted) return null;

            var callback = await steamCloud.RequestUGCDetails(ugcHandle);

            if (callback == null)
            {
                Logger.Error($"RequestUGCDetails returned null callback for UGC handle {ugcHandle}.");
                throw new Exception($"RequestUGCDetails returned null for {ugcHandle}.");
            }

            Logger.Info($"GetUGCDetails result for handle {ugcHandle}: {callback.Result}");

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


        public void ResetConnectionFlags()
        {
            bExpectingDisconnectRemote = false;
            bDidDisconnect = false;
            bIsConnectionRecovery = false;
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

        public void Abort(bool sendLogOff = true)
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

        public void Reconnect()
        {
            if (bAborted) return;
            Logger.Info("Attempting to reconnect to Steam...");
            bIsConnectionRecovery = true;
            steamClient.Disconnect();
        }

        public async void ConnectedCallback(SteamClient.ConnectedCallback connected)
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


        public void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
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

        public void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
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


        public void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
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


        public static void DisplayQrCode(string challengeUrl)
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
    }
}