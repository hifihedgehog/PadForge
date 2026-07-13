using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Cache;
using SteamKit2;
using SteamKit2.Internal;

// EPublishedFileQueryType is declared in both SteamKit2 and SteamKit2.Internal. Bind the
// name to the public enum (clean member names) to resolve the ambiguity.
using EPublishedFileQueryType = SteamKit2.EPublishedFileQueryType;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// The one Steam-protocol client in the feature: an anonymous CM session over
    /// SteamKit2 that runs <c>PublishedFile.QueryFiles</c> to list controller configs for a
    /// game. Uses the WebSocket transport (port 443, firewall-friendly), pumps callbacks on
    /// a background task, enforces 15 s connect/logon timeouts, single-flights identical
    /// in-flight queries, spaces outbound requests by at least 100 ms, and caches results
    /// (24 h). The constructor throws if the opt-in gate is off.
    /// </summary>
    public sealed class SteamWorkshopClient : IAsyncDisposable
    {
        /// <summary>Steam's "Controller Configs" Workshop bucket app id. Config items
        /// are consumed by this app; the game they belong to rides an "app" kv-tag.</summary>
        public const uint ControllerConfigsAppId = 241100;

        /// <summary>k_PFI_MatchingFileType_ControllerBindings from the Steamworks
        /// QueryFiles matching-file-type enum. Not the same enum as the per-item
        /// EWorkshopFileType (where ControllerBinding is 12).</summary>
        private const uint MatchingFileTypeControllerBindings = 15;

        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan LogonTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

        private readonly SteamWorkshopCache _cache;
        private readonly SteamClient _client;
        private readonly CallbackManager _callbacks;
        private readonly SteamUser _user;
        private readonly SemaphoreSlim _logonGate = new SemaphoreSlim(1, 1);
        private readonly object _spacingLock = new object();
        private readonly ConcurrentDictionary<string, Task<CPublishedFile_QueryFiles_Response>> _inflight =
            new ConcurrentDictionary<string, Task<CPublishedFile_QueryFiles_Response>>();

        private SteamUnifiedMessages _unified;
        private PublishedFile _publishedFile;
        private CancellationTokenSource _pumpCts;
        private Task _pumpTask;
        private TaskCompletionSource<bool> _connectTcs;
        private TaskCompletionSource<EResult> _logonTcs;
        private TaskCompletionSource<bool> _flushTcs;
        private volatile bool _loggedOn;
        private volatile bool _disposed;
        private DateTime _nextRequestUtc = DateTime.MinValue;

        /// <summary>Sentinel posted through the SteamKit callback queue to prove every
        /// earlier callback has been delivered (the queue is FIFO and delivery happens
        /// only on the pump thread via <c>RunWaitCallbacks</c>).</summary>
        private sealed class QueueFlushedCallback : CallbackMsg
        {
        }

        public SteamWorkshopClient(ISteamWorkshopGate gate, SteamWorkshopCache cache = null)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
            _cache = cache;

            var config = SteamConfiguration.Create(b => b
                .WithProtocolTypes(ProtocolTypes.WebSocket)
                .WithDirectoryFetch(true));

            _client = new SteamClient(config);
            _callbacks = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>();

            _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbacks.Subscribe<QueueFlushedCallback>(OnQueueFlushed);
        }

        /// <summary>
        /// Connects and logs on anonymously if not already. Idempotent and serialized: a
        /// single connect/logon runs even under concurrent callers.
        /// </summary>
        public async Task EnsureLoggedOnAsync(CancellationToken ct = default)
        {
            if (_disposed)
                throw new SteamWorkshopException("The Steam client is disposed.");
            if (_loggedOn && _client.IsConnected) return;

            // The browse dialog disposes this client on close, racing any
            // in-flight logon. The gate's ObjectDisposedException must surface
            // as the typed error every caller already handles, not escape as
            // an unobserved ODE on the single-flight task.
            try
            {
                await _logonGate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new SteamWorkshopException("The Steam client is disposed.");
            }
            try
            {
                if (_loggedOn && _client.IsConnected) return;

                StartPump();

                // A previous attempt (timeout teardown, failed logon, server drop)
                // can leave its Connected/Disconnected callbacks undelivered in the
                // FIFO queue; delivered later, they would complete THIS attempt's
                // fresh TCSes and fail it spuriously. Tear down any half-open
                // connection first (CMClient posts the Disconnected callback before
                // Disconnect() returns), then flush the queue with a sentinel so
                // every stale callback lands before the new attempt wires up. The
                // teardown hops to the pool because CMClient.Disconnect blocks on
                // the connection-setup task (which can include the network-bound
                // server-directory fetch), and this method can be entered inline on
                // the caller's thread.
                await Task.Run(TryDisconnect).ConfigureAwait(false);
                _flushTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _client.PostCallback(new QueueFlushedCallback());
                try
                {
                    await _flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The flush is best-effort hygiene; a stalled pump surfaces via
                    // the connect timeout below anyway.
                }

                _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _logonTcs = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);

                _client.Connect();
                try
                {
                    await _connectTcs.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Tear the half-open attempt down so a retry starts from a
                    // clean state instead of racing this attempt's late
                    // Connected/Disconnected callbacks (SteamKit asserts on
                    // exactly that overlap).
                    TryDisconnect();
                    throw new SteamWorkshopException("Timed out connecting to Steam.");
                }

                if (!_client.IsConnected)
                    throw new SteamWorkshopException("Disconnected from Steam during connect.");

                _user.LogOnAnonymous();

                EResult result;
                try
                {
                    result = await _logonTcs.Task.WaitAsync(LogonTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Same clean-state teardown as the connect timeout above.
                    TryDisconnect();
                    throw new SteamWorkshopException("Timed out during anonymous Steam logon.");
                }

                if (result != EResult.OK)
                    throw new SteamWorkshopException($"Anonymous Steam logon failed: {result}.");

                _unified = _client.GetHandler<SteamUnifiedMessages>();
                _publishedFile = _unified.CreateService<PublishedFile>();
                _loggedOn = true;
            }
            finally
            {
                try
                {
                    _logonGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    throw new SteamWorkshopException("The Steam client is disposed.");
                }
            }
        }

        /// <summary>
        /// Queries controller configs for a game. Results are cache-first (24 h), then
        /// single-flighted and throttled on a miss. <paramref name="requiredTags"/> filters
        /// by Workshop tag (for example <c>controller_ps5</c> or <c>controller_neptune</c>).
        /// </summary>
        public async Task<CPublishedFile_QueryFiles_Response> SearchAsync(
            int appId,
            EPublishedFileQueryType queryType = EPublishedFileQueryType.RankedByTotalUniqueSubscriptions,
            int page = 1,
            int perPage = 30,
            IReadOnlyCollection<string> requiredTags = null,
            CancellationToken ct = default)
        {
            var cacheKey = BuildSearchKey(appId, queryType, page, perPage, requiredTags);

            if (_cache != null &&
                _cache.TryGetBytes(CacheCategory.Search, cacheKey, CacheTtls.Search, out var cachedBytes))
            {
                var cached = TryDeserialize(cachedBytes);
                if (cached != null) return cached;
            }

            var task = _inflight.GetOrAdd(cacheKey,
                key => FetchAndCacheAsync(appId, queryType, page, perPage, requiredTags, key));
            try
            {
                return await task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _inflight.TryRemove(new KeyValuePair<string, Task<CPublishedFile_QueryFiles_Response>>(cacheKey, task));
            }
        }

        /// <summary>Diagnostic-only single-item details over the CM session with tags and
        /// kv-tags included. Used by the smoke harness to ground query-shape facts.</summary>
        public async Task<PublishedFileDetails> GetCmDetailsAsync(ulong fileId, CancellationToken ct = default)
        {
            await EnsureLoggedOnAsync(ct).ConfigureAwait(false);
            var request = new CPublishedFile_GetDetails_Request
            {
                includetags = true,
                includekvtags = true,
                includemetadata = true,
            };
            request.publishedfileids.Add(fileId);
            var response = await _publishedFile.GetDetails(request).ToTask().ConfigureAwait(false);
            if (response.Result != EResult.OK)
                throw new SteamWorkshopException($"Steam GetDetails failed: {response.Result}.");
            return response.Body.publishedfiledetails.Count > 0 ? response.Body.publishedfiledetails[0] : null;
        }

        private async Task<CPublishedFile_QueryFiles_Response> FetchAndCacheAsync(
            int appId, EPublishedFileQueryType queryType, int page, int perPage,
            IReadOnlyCollection<string> requiredTags, string cacheKey)
        {
            await EnsureLoggedOnAsync(CancellationToken.None).ConfigureAwait(false);
            await ThrottleAsync(CancellationToken.None).ConfigureAwait(false);

            // Query shape grounded live (2026-07-13, smoke harness): controller
            // configs are consumed by the 241100 bucket (appid filters on the
            // CONSUMER app), typed ControllerBindings in the matching-file-type
            // enum (15, k_PFI_MatchingFileType_ControllerBindings; distinct from
            // EWorkshopFileType.ControllerBinding = 12 on the items themselves),
            // and scoped to a game by the "app" kv-tag every config carries.
            // Without filetype the query returns nothing; without the kv-tag it
            // returns nothing for the bucket (Skyrim SE: 155,694 items under
            // this shape, zero under appid=game).
            var request = new CPublishedFile_QueryFiles_Request
            {
                query_type = (uint)queryType,
                page = (uint)page,
                numperpage = (uint)perPage,
                appid = ControllerConfigsAppId,
                filetype = MatchingFileTypeControllerBindings,
                return_vote_data = true,
                return_tags = true,
                return_kv_tags = true,
                return_short_description = true,
                return_metadata = true,
                return_details = true,
                return_playtime_stats = 30,
            };

            request.required_kv_tags.Add(new CPublishedFile_QueryFiles_Request.KVTag
            {
                key = "app",
                value = appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

            if (requiredTags != null)
            {
                foreach (var tag in requiredTags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        request.requiredtags.Add(tag);
                }
            }

            CPublishedFile_QueryFiles_Response body;
            try
            {
                var response = await _publishedFile.QueryFiles(request).ToTask().ConfigureAwait(false);
                if (response.Result != EResult.OK)
                    throw new SteamWorkshopException($"Steam QueryFiles failed: {response.Result}.");
                body = response.Body;
            }
            catch (TaskCanceledException)
            {
                // The AsyncJob's default timeout cancels the job task. No
                // caller token reaches this single-flight task, so a
                // cancellation here always means Steam never answered. Wrap
                // it so callers see the same exception type the connect and
                // logon timeouts throw.
                throw new SteamWorkshopException("Timed out querying the Steam Workshop.");
            }
            catch (AsyncJobFailedException)
            {
                throw new SteamWorkshopException("Steam reported the Workshop query failed.");
            }
            if (_cache != null && body != null)
            {
                var bytes = TrySerialize(body);
                if (bytes != null)
                    _cache.PutBytes(CacheCategory.Search, cacheKey, bytes);
            }

            return body;
        }

        private Task ThrottleAsync(CancellationToken ct)
        {
            TimeSpan wait;
            lock (_spacingLock)
            {
                var now = DateTime.UtcNow;
                var earliest = _nextRequestUtc;
                if (earliest <= now)
                {
                    _nextRequestUtc = now + MinRequestSpacing;
                    wait = TimeSpan.Zero;
                }
                else
                {
                    wait = earliest - now;
                    _nextRequestUtc = earliest + MinRequestSpacing;
                }
            }
            return wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;
        }

        private static string BuildSearchKey(int appId, EPublishedFileQueryType queryType, int page, int perPage,
            IReadOnlyCollection<string> requiredTags)
        {
            var tagKey = string.Empty;
            if (requiredTags != null && requiredTags.Count > 0)
            {
                var tags = new List<string>(requiredTags);
                tags.Sort(StringComparer.Ordinal);
                tagKey = string.Join("+", tags);
            }
            return $"{appId}_{(uint)queryType}_{page}_{perPage}_{tagKey}";
        }

        private static byte[] TrySerialize(CPublishedFile_QueryFiles_Response response)
        {
            try
            {
                using var ms = new MemoryStream();
                ProtoBuf.Serializer.Serialize(ms, response);
                return ms.ToArray();
            }
            catch (Exception)
            {
                // Caching is an optimization; never let a serialization hiccup fail the query.
                return null;
            }
        }

        private static CPublishedFile_QueryFiles_Response TryDeserialize(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                return ProtoBuf.Serializer.Deserialize<CPublishedFile_QueryFiles_Response>(ms);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void StartPump()
        {
            if (_pumpTask != null) return;

            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pumpTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        _callbacks.RunWaitCallbacks(PumpInterval);
                    }
                    catch (Exception)
                    {
                        // The pump must never crash the session; surface real failures via the
                        // connect/logon TCS timeouts instead.
                    }
                }
            }, token);
        }

        /// <summary>Best-effort disconnect for the timeout paths. Disconnect
        /// is safe to call on a client that never finished connecting.</summary>
        private void TryDisconnect()
        {
            try
            {
                _client.Disconnect();
            }
            catch (Exception)
            {
                // Teardown is best-effort. The thrown SteamWorkshopException
                // already carries the user-facing failure.
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback) => _connectTcs?.TrySetResult(true);

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _loggedOn = false;
            _connectTcs?.TrySetResult(false);
            _logonTcs?.TrySetResult(EResult.NoConnection);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback) => _logonTcs?.TrySetResult(callback.Result);

        private void OnQueueFlushed(QueueFlushedCallback callback) => _flushTcs?.TrySetResult(true);

        public async ValueTask DisposeAsync()
        {
            _disposed = true;

            // CMClient.Disconnect blocks until the connection-setup task completes,
            // and that task can include the network-bound server-directory fetch. An
            // async method runs synchronously to its first await, and the browse
            // dialog disposes from the UI thread on close, so the teardown hops to
            // the pool before touching the client.
            await Task.Run(() =>
            {
                try
                {
                    if (_client.IsConnected)
                        _user?.LogOff();
                }
                catch (Exception)
                {
                    // Best-effort logoff.
                }

                try
                {
                    _client?.Disconnect();
                }
                catch (Exception)
                {
                    // Best-effort disconnect.
                }
            }).ConfigureAwait(false);

            if (_pumpCts != null)
            {
                _pumpCts.Cancel();
                if (_pumpTask != null)
                {
                    try
                    {
                        await _pumpTask.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Pump task cancellation is expected.
                    }
                }
                _pumpCts.Dispose();
            }

            _logonGate.Dispose();
        }
    }
}
