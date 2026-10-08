using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Razer Chroma worker (#494). Chroma addresses device categories,
    /// never one unit, so each category shows the color its path resolves to
    /// (<see cref="PeripheralOutputs.TryResolveColor(OutputPath, out int)"/>):
    /// a Razer mouse or keyboard assigned to a virtual controller claims its
    /// own category, the Razer Chroma row claims the ones no device claims,
    /// and the smallest displayed player number rules a category two
    /// controllers claim.
    ///
    /// <para>Set Chroma Color (#468) paints the categories of the Razer
    /// devices assigned to the controller its macro runs on, whether or not
    /// their Lighting tabs take control, and lets go within
    /// <see cref="PeripheralOutputs.MacroAssertWindowMs"/> of the action
    /// ending. Two controllers' macros on one category: the smaller displayed
    /// player number rules.</para>
    ///
    /// <para>Protocol, triangulated from the official Chroma REST docs and
    /// chroma-sdk/Colore (RestApi.cs), which agree on every field. Init is
    /// POST {endpoint}/razer/chromasdk with the app-info JSON, and it
    /// returns {"sessionid", "uri"}. The session dies after 15 seconds
    /// without a command, so a PUT {uri}/heartbeat rides every second
    /// (Colore's own interval). PUT {uri}/{category} with {"effect":
    /// "CHROMA_STATIC", "param": {"color": N}} applies immediately. DELETE
    /// {uri} ends the session, which hands the lighting back to Synapse. The color integer
    /// is BGR: R + (G &lt;&lt; 8) + (B &lt;&lt; 16). Each category's PUT
    /// stands on its own inside one session.</para>
    ///
    /// <para>The session lists only the categories something claims, and it
    /// is opened again when that set changes, so a category nothing claims
    /// stays Synapse's: the docs do not say whether Synapse leaves alone a
    /// category a session lists and never paints.</para>
    ///
    /// <para>URIs are joined by string concatenation, never Uri(base,
    /// relative): the session URI has no trailing slash, and relative Uri
    /// resolution would replace its last segment instead of appending. The
    /// endpoint is injectable because the production port is machine-global,
    /// and a test that talked to it would reach a real Synapse.</para>
    /// </summary>
    internal sealed class ChromaBackend : IDisposable
    {
        /// <summary>The Chroma REST server Synapse serves. Official docs and
        /// Colore's DefaultEndpoint agree on the port.</summary>
        public const string DefaultEndpoint = "http://localhost:54235";

        /// <summary>How long one REST call may take before HttpClient
        /// abandons it. A cold Synapse answers the init POST late, and the
        /// loop treats that as a retry, never as a stop.</summary>
        internal const int DefaultHttpTimeoutMs = 5000;

        /// <summary>The result for a category with no device behind it:
        /// Colore Data/Result.cs DeviceNotConnected = 1167, the one code
        /// Colore's native path reads as "no device" rather than as an
        /// error. Counted as accepted.</summary>
        internal const int ResultDeviceNotConnected = 1167;

        private readonly string _endpoint;
        private readonly int _heartbeatMs;
        private readonly int _retryMs;
        private readonly int _pollMs;
        private readonly Func<int, ISet<Guid>> _devicesOnSlot;
        private readonly Func<int, int> _slotNumber;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private CancellationTokenSource _cts;
        private Task _loop;
        private string _lastRejectLogged;
        private int _disposed;

        public ChromaBackend()
            : this(null, 1000, 30000, 100, DefaultHttpTimeoutMs, null, null) { }

        /// <summary>Test seam: the endpoint, the cadences, the HTTP timeout,
        /// and the assignment lookups a Set Chroma Color reads.</summary>
        internal ChromaBackend(string endpoint, int heartbeatMs, int retryMs, int pollMs, int httpTimeoutMs,
            Func<int, ISet<Guid>> devicesOnSlot, Func<int, int> slotNumber)
        {
            _endpoint = string.IsNullOrEmpty(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
            _heartbeatMs = heartbeatMs;
            _retryMs = retryMs;
            _pollMs = pollMs;
            _devicesOnSlot = devicesOnSlot ?? PeripheralOutputs.DevicesOnSlot;
            _slotNumber = slotNumber ?? PeripheralOutputs.DisplayedSlotNumber;
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs) };
        }

        internal bool WorkerAlive => _loop != null && !_loop.IsCompleted;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.ChromaMacroChanged += Wake;
            _loop = Task.Run(() => LoopAsync(token), token);
        }

        public void Stop()
        {
            if (_cts == null) return;
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.ChromaMacroChanged -= Wake;
            try { _cts.Cancel(); } catch { }
            // The worker may be parked in a REST call. Give it the wait,
            // then let it finish on its own: the session it ends is its own.
            try { _loop?.Wait(3000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        private void Wake()
        {
            try { if (_wake.CurrentCount == 0) _wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>0x00RRGGBB to the Chroma BGR integer,
        /// R + (G &lt;&lt; 8) + (B &lt;&lt; 16).</summary>
        internal static int ToBgr(int rgb)
            => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

        /// <summary>The app-info JSON Synapse shows in its Connect tab, with
        /// the categories this session lights. Field names verbatim from the
        /// official init page.</summary>
        internal static string InitBody(IReadOnlyList<string> categories)
        {
            var sb = new StringBuilder();
            sb.Append("{\"title\":\"PadForge\",");
            sb.Append("\"description\":\"Lights Razer devices from the PadForge virtual controllers they are assigned to.\",");
            sb.Append("\"author\":{\"name\":\"hifihedgehog\",\"contact\":\"https://padforge.org\"},");
            sb.Append("\"device_supported\":[");
            for (int i = 0; i < categories.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(categories[i]).Append('"');
            }
            sb.Append("],\"category\":\"application\"}");
            return sb.ToString();
        }

        /// <summary>Each category's color right now, in the canonical
        /// category order: a live Set Chroma Color on a slot that has a Razer
        /// device of the category, the smaller displayed number first, else
        /// the category's ruling claim.</summary>
        internal static List<KeyValuePair<string, int>> Wanted(LinkTable links, long now,
            Func<int, ISet<Guid>> devicesOnSlot, Func<int, int> slotNumber)
        {
            var macros = new Dictionary<string, (int Number, int Rgb)>();
            if (PeripheralOutputs.AnyChromaMacro(now))
            {
                for (int slot = 0; slot < InputManager.MaxPads; slot++)
                {
                    if (!PeripheralOutputs.TryGetChromaMacro(slot, now, out int macroRgb)) continue;
                    var devices = devicesOnSlot(slot);
                    if (devices == null || devices.Count == 0) continue;
                    int number = slotNumber(slot);
                    foreach (var category in PeripheralLinker.ChromaCategories)
                    {
                        var path = new OutputPath(OutputFamily.ChromaCategory, category);
                        if (!links.ByPath.TryGetValue(path, out var linked)) continue;
                        bool assigned = false;
                        foreach (var device in linked)
                            if (devices.Contains(device)) { assigned = true; break; }
                        if (!assigned) continue;
                        if (macros.TryGetValue(category, out var held) && held.Number <= number) continue;
                        macros[category] = (number, macroRgb);
                    }
                }
            }

            var wanted = new List<KeyValuePair<string, int>>();
            foreach (var category in PeripheralLinker.ChromaCategories)
            {
                if (macros.TryGetValue(category, out var macro))
                    wanted.Add(new KeyValuePair<string, int>(category, macro.Rgb));
                else if (PeripheralOutputs.TryResolveColor(new OutputPath(OutputFamily.ChromaCategory, category), out int rgb))
                    wanted.Add(new KeyValuePair<string, int>(category, rgb));
            }
            return wanted;
        }

        private static bool SameCategories(List<KeyValuePair<string, int>> wanted, List<string> session)
        {
            if (session == null || session.Count != wanted.Count) return false;
            for (int i = 0; i < wanted.Count; i++)
                if (wanted[i].Key != session[i]) return false;
            return true;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            string session = null;
            List<string> sessionCategories = null;
            var lastSent = new Dictionary<string, int>();
            long lastHeartbeat = 0;
            long retryAt = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    long now = Environment.TickCount64;
                    var wanted = Wanted(PeripheralOutputs.Links, now, _devicesOnSlot, _slotNumber);

                    if (wanted.Count == 0)
                    {
                        // Nothing claims a category: hold no session, so
                        // Synapse keeps the lighting.
                        if (session != null)
                        {
                            await EndSessionAsync(session).ConfigureAwait(false);
                            session = null;
                            sessionCategories = null;
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma handed back");
                        }
                        retryAt = 0;
                        PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Idle);
                        await WaitAsync(_pollMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (session == null || !SameCategories(wanted, sessionCategories))
                    {
                        if (now < retryAt)
                        {
                            await WaitAsync(_pollMs, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (session != null) await EndSessionAsync(session).ConfigureAwait(false);
                        session = null;
                        var categories = new List<string>(wanted.Count);
                        foreach (var pair in wanted) categories.Add(pair.Key);
                        // HttpClient reports its own timeout as a
                        // TaskCanceledException, so the filter keeps a slow
                        // init on the retry path. Only a stop ends the loop.
                        try { session = await InitAsync(categories, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch { /* Synapse absent, refused, or slow: retry below */ }
                        if (session == null)
                        {
                            sessionCategories = null;
                            retryAt = Environment.TickCount64 + _retryMs;
                            PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Waiting);
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma waiting for Synapse");
                            continue;
                        }
                        sessionCategories = categories;
                        lastSent.Clear();
                        lastHeartbeat = Environment.TickCount64;
                        PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Connected);
                        PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma session for " + string.Join(",", categories));
                    }

                    bool broken = false;
                    try
                    {
                        now = Environment.TickCount64;
                        if (now - lastHeartbeat >= _heartbeatMs)
                        {
                            lastHeartbeat = now;
                            // No body, Colore's no-data PUT overload
                            // (RestClient.cs:107).
                            using var hb = await _http.PutAsync(session + "/heartbeat", null, ct).ConfigureAwait(false);
                            if (!hb.IsSuccessStatusCode) broken = true;
                        }
                        if (!broken)
                        {
                            foreach (var pair in wanted)
                            {
                                if (lastSent.TryGetValue(pair.Key, out int sent) && sent == pair.Value) continue;
                                // A rejected PUT leaves the last color in place
                                // so the next poll retries it.
                                if (await SendStaticAsync(session, pair.Key, ToBgr(pair.Value), ct).ConfigureAwait(false))
                                    lastSent[pair.Key] = pair.Value;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch { broken = true; }

                    if (broken)
                    {
                        // The session died on the server or the transport
                        // broke: open a new one after the retry delay.
                        await EndSessionAsync(session).ConfigureAwait(false);
                        session = null;
                        sessionCategories = null;
                        retryAt = Environment.TickCount64 + _retryMs;
                        PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Waiting);
                        PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma session lost");
                        continue;
                    }
                    await WaitAsync(_pollMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma worker fault: " + ex.GetType().Name);
            }
            finally
            {
                if (session != null) await EndSessionAsync(session).ConfigureAwait(false);
                PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Idle);
            }
        }

        /// <summary>Waits a poll interval, or less when a claim changes.</summary>
        private async Task WaitAsync(int ms, CancellationToken ct)
        {
            try { await _wake.WaitAsync(ms, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Ends a session, bounded at a second. Synapse reaps an
        /// abandoned one at 15 seconds anyway.</summary>
        private async Task EndSessionAsync(string session)
        {
            try
            {
                using var bounded = new CancellationTokenSource(1000);
                using var response = await _http.DeleteAsync(session, bounded.Token).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }

        private async Task<string> InitAsync(IReadOnlyList<string> categories, CancellationToken ct)
        {
            using var resp = await _http.PostAsync(
                _endpoint + "/razer/chromasdk", JsonContent(InitBody(categories)), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("uri", out var uriProp)) return null;
            string uri = uriProp.GetString();
            return string.IsNullOrWhiteSpace(uri) ? null : uri.TrimEnd('/');
        }

        /// <summary>PUTs CHROMA_STATIC to one category and returns whether it
        /// was accepted. The REST server answers HTTP 200 with a nonzero
        /// "result" on a rejected effect, and Colore checks that field after
        /// every effect call (Rest/RestApi.cs SetEffectAsync), so acceptance
        /// is read from the body. A rejection is logged once per distinct
        /// category and code.</summary>
        private async Task<bool> SendStaticAsync(string session, string category, int bgr, CancellationToken ct)
        {
            string body = "{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":"
                + bgr.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
            using var resp = await _http.PutAsync(session + "/" + category, JsonContent(body), ct).ConfigureAwait(false);
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
            if (reject == null) return true;
            if (reject != _lastRejectLogged)
            {
                _lastRejectLogged = reject;
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL Chroma effect rejected: {reject}, retrying on the next poll");
            }
            return false;
        }

        /// <summary>Reads the "result" integer every effect response carries
        /// (Colore Rest/Data/SdkResponse.cs).</summary>
        internal static bool TryReadResult(string content, out int result)
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
            _wake.Dispose();
        }
    }
}
