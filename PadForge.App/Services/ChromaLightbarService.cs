using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services
{
    /// <summary>Connection state the service reports to its owner, who maps
    /// it to localized status text on the UI thread.</summary>
    public enum ChromaServiceState
    {
        Stopped,
        WaitingForSynapse,
        Connected,
    }

    /// <summary>
    /// Razer Chroma lightbar mirror (#373, asked in discussion #368): registers
    /// PadForge as a Chroma app with the REST server Razer Synapse runs on
    /// localhost, and forwards the color a game paints on a virtual Sony pad's
    /// lightbar to every Chroma device category. Razer's Chroma gamepads
    /// (Wolverine V3 Pro, Raiju V3 Pro) cannot read Sony lightbar colors on
    /// their own, and the user scopes or disables the mirror in Synapse's
    /// Connect tab like any other Chroma app.
    ///
    /// <para>Protocol, triangulated from the official Chroma REST docs and
    /// chroma-sdk/Colore (RestApi.cs), which agree on every field: init is
    /// POST {endpoint}/razer/chromasdk with the app-info JSON and returns
    /// {"sessionid", "uri"}; the session dies after 15 seconds without a
    /// command, so a PUT {uri}/heartbeat rides every second (Colore's own
    /// interval); PUT {uri}/{category} with {"effect": "CHROMA_STATIC",
    /// "param": {"color": N}} applies immediately (PUT applies, POST would
    /// create an effect id for later application); DELETE {uri} ends the
    /// session. The color integer is BGR: R + (G &lt;&lt; 8) + (B &lt;&lt; 16),
    /// stated in the official docs and implemented identically by Colore's
    /// Color constructor.</para>
    ///
    /// <para>URIs are joined by string concatenation, never Uri(base,
    /// relative): the session URI has no trailing slash, and relative Uri
    /// resolution would replace its last segment instead of appending.</para>
    ///
    /// <para>The endpoint is constructor-injectable for the same reason the
    /// external-control pipe's name is: the production port is machine-global,
    /// and a test that talked to it would reach a real Synapse.</para>
    /// </summary>
    public sealed class ChromaLightbarService : IDisposable
    {
        /// <summary>The Chroma REST server Synapse serves. Official docs and
        /// Colore's DefaultEndpoint agree on the port.</summary>
        public const string DefaultEndpoint = "http://localhost:54235";

        /// <summary>The six Chroma device categories, every one addressed on
        /// each color push so whatever Synapse maps a device under lights up.
        /// The user narrows the scope in Synapse, which is the workflow the
        /// requester described.</summary>
        private static readonly string[] Categories =
            { "keyboard", "mouse", "headset", "mousepad", "keypad", "chromalink" };

        /// <summary>The app-info JSON Synapse displays in its Connect tab.
        /// Field names verbatim from the official init page.</summary>
        internal const string InitBody =
            "{\"title\":\"PadForge\","
            + "\"description\":\"Mirrors the Sony lightbar of PadForge virtual controllers to Razer Chroma devices.\","
            + "\"author\":{\"name\":\"hifihedgehog\",\"contact\":\"https://padforge.org\"},"
            + "\"device_supported\":[\"keyboard\",\"mouse\",\"headset\",\"mousepad\",\"keypad\",\"chromalink\"],"
            + "\"category\":\"application\"}";

        /// <summary>The published lightbar color as 0x00RRGGBB, or -1 before
        /// any game write. Written lock-free from the HM output callback via
        /// <see cref="Publish"/>, read by the worker loop. Static so the
        /// callback needs no service reference and publishing while the
        /// mirror is off costs one volatile write.</summary>
        private static int s_publishedRgb = -1;

        private readonly string _endpoint;
        private readonly int _heartbeatMs;
        private readonly int _retryMs;
        private readonly int _pollMs;
        private readonly HttpClient _http;
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _disposed;

        /// <summary>Raised from the worker thread on connection-state
        /// changes. The owner marshals to the UI thread.</summary>
        public event Action<ChromaServiceState> StateChanged;

        /// <summary>How long one REST call may take before HttpClient
        /// abandons it. A cold Synapse answers the init POST late, and the
        /// loop treats that as a retry, never as a stop.</summary>
        internal const int DefaultHttpTimeoutMs = 5000;

        public ChromaLightbarService(
            string endpoint = null, int heartbeatMs = 1000, int retryMs = 30000, int pollMs = 100)
            : this(endpoint, heartbeatMs, retryMs, pollMs, DefaultHttpTimeoutMs) { }

        /// <summary>Test seam for the HTTP timeout: the bench provokes a
        /// slow init answer in hundreds of milliseconds rather than the
        /// five seconds production waits for a cold Synapse.</summary>
        internal ChromaLightbarService(
            string endpoint, int heartbeatMs, int retryMs, int pollMs, int httpTimeoutMs)
        {
            _endpoint = string.IsNullOrEmpty(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
            _heartbeatMs = heartbeatMs;
            _retryMs = retryMs;
            _pollMs = pollMs;
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs) };
        }

        /// <summary>Publishes the game-set lightbar color. Called from the
        /// HM output callback for every valid lightbar write to a Sony
        /// virtual; multiple active Sony slots resolve last-writer-wins.</summary>
        public static void Publish(byte r, byte g, byte b)
            => Volatile.Write(ref s_publishedRgb, (r << 16) | (g << 8) | b);

        /// <summary>Test seam: returns the store to its no-color-yet state.</summary>
        internal static void ResetPublishedForTest()
            => Volatile.Write(ref s_publishedRgb, -1);

        /// <summary>0x00RRGGBB to the Chroma BGR integer,
        /// R + (G &lt;&lt; 8) + (B &lt;&lt; 16).</summary>
        internal static int ToBgr(int rgb)
            => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

        public void Start()
        {
            if (_cts != null) return; // Already started.
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }

        public void Stop()
        {
            if (_cts == null) return;
            try { _cts.Cancel(); } catch { }
            try { _loop?.Wait(3000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        private void Report(ChromaServiceState state)
        {
            PadForge.Engine.SdlDiagLog.WriteLine($"CHROMA state={state}");
            try { StateChanged?.Invoke(state); } catch { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string session = null;
                // HttpClient signals its own timeout as TaskCanceledException,
                // an OperationCanceledException subclass, so the filter is
                // what keeps a slow init (a cold Synapse answering the POST
                // late) on the retry path below instead of leaving the loop
                // and reporting Stopped while the toggle stays on. Only a
                // Stop, seen on the token, ends the loop here.
                try { session = await InitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { /* Synapse absent, refused, or slow: retry below */ }

                if (session == null)
                {
                    Report(ChromaServiceState.WaitingForSynapse);
                    try { await Task.Delay(_retryMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                Report(ChromaServiceState.Connected);
                int lastSent = -1;
                long lastHeartbeat = Environment.TickCount64;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        long now = Environment.TickCount64;
                        if (now - lastHeartbeat >= _heartbeatMs)
                        {
                            lastHeartbeat = now;
                            // No body, matching Colore's no-data PUT overload
                            // (RestClient.cs:107 sends null content when the
                            // heartbeat has nothing to say).
                            using var hb = await _http.PutAsync(
                                session + "/heartbeat", null, ct).ConfigureAwait(false);
                            if (!hb.IsSuccessStatusCode)
                                break; // Session died on the server: re-init.
                        }

                        int rgb = Volatile.Read(ref s_publishedRgb);
                        if (rgb >= 0 && rgb != lastSent)
                        {
                            // lastSent advances only when every category
                            // accepted the effect. A rejected PUT (HTTP 200
                            // with a nonzero result) leaves it unchanged so
                            // the next poll retries the same color instead
                            // of holding the previous one until the game
                            // writes a new color.
                            if (await SendStaticAsync(session, ToBgr(rgb), ct).ConfigureAwait(false))
                                lastSent = rgb;
                        }

                        await Task.Delay(_pollMs, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { /* stopping, or a call timed out: the token check below tells them apart */ }
                catch { /* transport broke: fall through to reconnect */ }

                // End the session either way; a fresh init replaces it on
                // reconnect, and Synapse reaps abandoned ones at 15 s anyway.
                try
                {
                    using var bounded = new CancellationTokenSource(1000);
                    await _http.DeleteAsync(session, bounded.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }

                if (ct.IsCancellationRequested) break;
                Report(ChromaServiceState.WaitingForSynapse);
                try { await Task.Delay(_retryMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            Report(ChromaServiceState.Stopped);
        }

        private async Task<string> InitAsync(CancellationToken ct)
        {
            using var resp = await _http.PostAsync(
                _endpoint + "/razer/chromasdk", JsonContent(InitBody), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("uri", out var uriProp)) return null;
            string uri = uriProp.GetString();
            return string.IsNullOrWhiteSpace(uri) ? null : uri.TrimEnd('/');
        }

        /// <summary>The result code for a category with no device behind
        /// it: Colore Data/Result.cs DeviceNotConnected = 1167, the one code
        /// Colore's native path (NativeApi.cs QueryDeviceAsync) reads as
        /// "no device" rather than as an error. A mirror addressing all six
        /// categories counts it as accepted.</summary>
        internal const int ResultDeviceNotConnected = 1167;

        /// <summary>The last rejection logged ("category result=code"), so a
        /// category the server keeps rejecting costs one diag line per
        /// distinct failure rather than one per poll. Cleared when a push
        /// is accepted in full.</summary>
        private string _lastRejectLogged;

        /// <summary>PUTs CHROMA_STATIC to every category and returns whether
        /// all of them accepted it. The REST server answers HTTP 200 with a
        /// nonzero "result" on a rejected effect, and Colore checks that
        /// field after every effect call (Rest/RestApi.cs SetEffectAsync and
        /// CreateEffectAsync), so acceptance is read from the body, never
        /// from the status code alone. Zero and DeviceNotConnected accept.
        /// A rejection is logged once per distinct category and code, and
        /// the caller leaves lastSent alone so the next poll retries.</summary>
        private async Task<bool> SendStaticAsync(string session, int bgr, CancellationToken ct)
        {
            string body = "{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":"
                + bgr.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
            string firstReject = null;
            foreach (string category in Categories)
            {
                using var resp = await _http.PutAsync(
                    session + "/" + category, JsonContent(body), ct).ConfigureAwait(false);
                // Keep pushing the remaining categories after a rejection so
                // one bad category never starves the others. A non-success
                // status means the session broke, which the next heartbeat
                // detects and answers with a re-init.
                string reject = null;
                if (!resp.IsSuccessStatusCode)
                {
                    reject = category + " http=" + (int)resp.StatusCode;
                }
                else
                {
                    string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!TryReadResult(content, out int result))
                        reject = category + " result=unparsable";
                    else if (result != 0 && result != ResultDeviceNotConnected)
                        reject = category + " result=" + result.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                firstReject ??= reject;
            }

            if (firstReject == null)
            {
                _lastRejectLogged = null;
                return true;
            }
            if (firstReject != _lastRejectLogged)
            {
                _lastRejectLogged = firstReject;
                PadForge.Engine.SdlDiagLog.WriteLine($"CHROMA effect rejected: {firstReject}, retrying on the next poll");
            }
            return false;
        }

        /// <summary>Reads the "result" integer every effect response carries
        /// (Colore Rest/Data/SdkResponse.cs). False when the body is not
        /// that shape, which the caller treats as a rejection, the way
        /// Colore treats a null response.</summary>
        private static bool TryReadResult(string content, out int result)
        {
            result = 0;
            try
            {
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("result", out var prop)
                    && prop.ValueKind == JsonValueKind.Number
                    && prop.TryGetInt32(out result);
            }
            catch (JsonException) { return false; }
        }

        private static StringContent JsonContent(string body)
            => new StringContent(body, Encoding.UTF8, "application/json");

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _http.Dispose();
        }
    }
}
