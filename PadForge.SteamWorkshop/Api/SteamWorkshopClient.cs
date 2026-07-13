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
        /// <summary>Steam's "Controller Configs" Workshop bucket app id.</summary>
        public const uint ControllerConfigsAppId = 241100;

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
        private volatile bool _loggedOn;
        private DateTime _nextRequestUtc = DateTime.MinValue;

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
        }

        /// <summary>
        /// Connects and logs on anonymously if not already. Idempotent and serialized: a
        /// single connect/logon runs even under concurrent callers.
        /// </summary>
        public async Task EnsureLoggedOnAsync(CancellationToken ct = default)
        {
            if (_loggedOn && _client.IsConnected) return;

            await _logonGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_loggedOn && _client.IsConnected) return;

                StartPump();

                _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _logonTcs = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);

                _client.Connect();
                try
                {
                    await _connectTcs.Task.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
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
                _logonGate.Release();
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

        private async Task<CPublishedFile_QueryFiles_Response> FetchAndCacheAsync(
            int appId, EPublishedFileQueryType queryType, int page, int perPage,
            IReadOnlyCollection<string> requiredTags, string cacheKey)
        {
            await EnsureLoggedOnAsync(CancellationToken.None).ConfigureAwait(false);
            await ThrottleAsync(CancellationToken.None).ConfigureAwait(false);

            var request = new CPublishedFile_QueryFiles_Request
            {
                query_type = (uint)queryType,
                page = (uint)page,
                numperpage = (uint)perPage,
                appid = (uint)appId,
                creator_appid = ControllerConfigsAppId,
                return_vote_data = true,
                return_tags = true,
                return_kv_tags = true,
                return_short_description = true,
                return_metadata = true,
                return_details = true,
                return_playtime_stats = 30,
            };

            if (requiredTags != null)
            {
                foreach (var tag in requiredTags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        request.requiredtags.Add(tag);
                }
            }

            var response = await _publishedFile.QueryFiles(request).ToTask().ConfigureAwait(false);
            if (response.Result != EResult.OK)
                throw new SteamWorkshopException($"Steam QueryFiles failed: {response.Result}.");

            var body = response.Body;
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

        private void OnConnected(SteamClient.ConnectedCallback callback) => _connectTcs?.TrySetResult(true);

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _loggedOn = false;
            _connectTcs?.TrySetResult(false);
            _logonTcs?.TrySetResult(EResult.NoConnection);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback) => _logonTcs?.TrySetResult(callback.Result);

        public async ValueTask DisposeAsync()
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
