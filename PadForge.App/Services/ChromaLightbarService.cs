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

        /// <summary>Generation stamp of the most recent Start across every
        /// instance. Stop waits three seconds and then clears its state
        /// whether or not the worker left, so a worker parked in a REST call
        /// or in the owner's StateChanged marshal can outlive its service.
        /// The owner disposes and recreates the service on re-enable, and
        /// the orphaned worker of the OLD instance must not report into the
        /// Dashboard status the live instance now owns. Each worker captures
        /// the generation it started under and compares against this on
        /// every report. The Lightsync mirror's shape, leg for leg.</summary>
        private static int s_generation;

        /// <summary>The color a Set Chroma Color macro action asserts (#468),
        /// 0x00RRGGBB, and the tick of its latest assertion. The action writes
        /// both on every frame it is current, from the poll thread, and the
        /// worker reads them. A color counts while its latest assertion is
        /// younger than <see cref="MacroAssertWindowMs"/>, so the lighting
        /// follows the action ending within that window. Several macros
        /// asserting in one frame resolve to the last write, which is the
        /// last one evaluated.</summary>
        private static int s_macroRgb;
        private static long s_macroAssertTicks;

        /// <summary>Set by the first macro assertion of the process, the
        /// owner's cue to run the service for macros with the mirror off.</summary>
        private static volatile bool s_macroRequested;

        /// <summary>Wakes the worker when a macro color starts or changes, so
        /// a press repaints without waiting out a poll.</summary>
        private static readonly SemaphoreSlim s_wake = new(0, 1);

        /// <summary>How long a macro color outlives its latest assertion. The
        /// action asserts every poll, 1 ms apart, and an idle engine still
        /// polls several times a second, so this only ever measures the gap
        /// after the action ends.</summary>
        internal const int MacroAssertWindowMs = 120;

        private volatile bool _mirrorEnabled = true;

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

        /// <summary>Whether the lightbar mirror (#373) is on. While it is off
        /// the service paints only macro colors and holds a Synapse session
        /// only while one is live.</summary>
        public bool MirrorEnabled
        {
            get => _mirrorEnabled;
            set
            {
                _mirrorEnabled = value;
                Wake();
            }
        }

        /// <summary>True once any Set Chroma Color action has run.</summary>
        public static bool MacroColorRequested => s_macroRequested;

        /// <summary>Asserts a macro color for this frame (#468). Called by
        /// the Set Chroma Color action on every frame it is current.</summary>
        public static void AssertMacroColor(byte r, byte g, byte b)
        {
            int rgb = (r << 16) | (g << 8) | b;
            long now = Environment.TickCount64;
            bool fresh = Volatile.Read(ref s_macroRgb) != rgb
                || now - Volatile.Read(ref s_macroAssertTicks) > MacroAssertWindowMs;
            Volatile.Write(ref s_macroRgb, rgb);
            Volatile.Write(ref s_macroAssertTicks, now);
            s_macroRequested = true;
            if (fresh) Wake();
        }

        /// <summary>The live macro color, if one was asserted within the
        /// window.</summary>
        internal static bool TryGetMacroColor(long now, out int rgb)
        {
            rgb = Volatile.Read(ref s_macroRgb);
            long at = Volatile.Read(ref s_macroAssertTicks);
            return at != 0 && now - at <= MacroAssertWindowMs;
        }

        /// <summary>Test seam: forgets every macro assertion.</summary>
        internal static void ResetMacroColorForTest()
        {
            Volatile.Write(ref s_macroAssertTicks, 0);
            Volatile.Write(ref s_macroRgb, 0);
            s_macroRequested = false;
        }

        private static void Wake()
        {
            try { if (s_wake.CurrentCount == 0) s_wake.Release(); }
            catch (SemaphoreFullException) { }
        }

        /// <summary>Whether a Synapse session is wanted right now: the mirror
        /// is on, or a macro color is live.</summary>
        private bool Wanted(long now) => _mirrorEnabled || TryGetMacroColor(now, out _);

        /// <summary>Waits a poll interval or until a macro color wakes the
        /// worker, whichever comes first.</summary>
        private Task WaitPollAsync(CancellationToken ct) => s_wake.WaitAsync(_pollMs, ct);

        /// <summary>0x00RRGGBB to the Chroma BGR integer,
        /// R + (G &lt;&lt; 8) + (B &lt;&lt; 16).</summary>
        internal static int ToBgr(int rgb)
            => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

        public void Start()
        {
            if (_cts != null) return; // Already started.
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // Every Start supersedes whatever worker came before it, an
            // orphan of a timed-out Stop included.
            int generation = Interlocked.Increment(ref s_generation);
            _loop = Task.Run(() => LoopAsync(token, generation), token);
        }

        public void Stop()
        {
            if (_cts == null) return;
            try { _cts.Cancel(); } catch { }
            // The worker may be parked in a REST call or in the owner's
            // StateChanged marshal. Give it the wait, then let it finish on
            // its own as an orphan: the generation check strips its reports
            // once a newer Start exists, and the session it ends is its own.
            try { _loop?.Wait(3000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        /// <summary>True once a newer Start exists than the one that made
        /// <paramref name="generation"/>: the worker holding it is an orphan
        /// whose reports belong to nobody.</summary>
        private static bool Superseded(int generation)
            => generation != Volatile.Read(ref s_generation);

        private void Report(ChromaServiceState state, int generation)
        {
            // A superseded worker's StateChanged closure targets the
            // Dashboard the live instance now owns: drop the report.
            if (Superseded(generation))
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"CHROMA superseded worker dropped state={state}");
                return;
            }
            PadForge.Engine.SdlDiagLog.WriteLine($"CHROMA state={state}");
            try { StateChanged?.Invoke(state); } catch { }
        }

        private async Task LoopAsync(CancellationToken ct, int generation)
        {
            while (!ct.IsCancellationRequested)
            {
                // Nothing to paint: hold no session, so Synapse keeps the
                // lighting (#468). The mirror alone keeps the old contract of
                // a session for as long as it is on.
                if (!Wanted(Environment.TickCount64))
                {
                    try { await WaitPollAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

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
                    Report(ChromaServiceState.WaitingForSynapse, generation);
                    try { await Task.Delay(_retryMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                Report(ChromaServiceState.Connected, generation);
                int lastSent = -1;
                bool paintedByMacro = false;
                bool handBack = false;
                long lastHeartbeat = Environment.TickCount64;
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        long now = Environment.TickCount64;
                        bool macroLive = TryGetMacroColor(now, out int macroRgb);
                        int published = Volatile.Read(ref s_publishedRgb);
                        bool mirror = _mirrorEnabled;

                        // Hand the lighting back to Synapse when nothing wants
                        // painting, or when a macro color ended with no mirror
                        // color to replace it. Only ending the session restores
                        // Synapse's own effect: CHROMA_NONE would blank
                        // PadForge's layer instead. A mirror that is still on
                        // opens a fresh session on the next pass.
                        if (!mirror && !macroLive) { handBack = true; break; }
                        if (paintedByMacro && !macroLive && !(mirror && published >= 0)) { handBack = true; break; }

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

                        // A macro color takes priority over the mirror's.
                        int rgb = macroLive ? macroRgb : (mirror ? published : -1);
                        if (rgb >= 0 && rgb != lastSent)
                        {
                            // lastSent advances only when every category
                            // accepted the effect. A rejected PUT (HTTP 200
                            // with a nonzero result) leaves it unchanged so
                            // the next poll retries the same color instead
                            // of holding the previous one until the game
                            // writes a new color.
                            if (await SendStaticAsync(session, ToBgr(rgb), ct).ConfigureAwait(false))
                            {
                                lastSent = rgb;
                                paintedByMacro = macroLive;
                            }
                        }
                        else if (rgb >= 0)
                        {
                            // Same color from the other source: whoever
                            // asserts it now owns what the keys show.
                            paintedByMacro = macroLive;
                        }

                        await WaitPollAsync(ct).ConfigureAwait(false);
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
                // A deliberate hand-back is not a failure: go straight back to
                // waiting for something to paint, with no retry delay.
                if (handBack)
                {
                    Report(ChromaServiceState.Stopped, generation);
                    continue;
                }
                Report(ChromaServiceState.WaitingForSynapse, generation);
                try { await Task.Delay(_retryMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            Report(ChromaServiceState.Stopped, generation);
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
