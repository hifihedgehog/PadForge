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
    /// ending: while one is live the worker's wait ends at its lapse
    /// (<see cref="PeripheralOutputs.NextChromaMacroLapse"/>). Two
    /// controllers' macros on one category: the smaller displayed player
    /// number rules.</para>
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
    /// category a session lists and never paints. A category whose device
    /// is lit on its own through the Razer direct worker is left out the
    /// same way (<see cref="Shadowed"/>), so one device is never painted
    /// from two sides.</para>
    ///
    /// <para>URIs are joined by string concatenation, never Uri(base,
    /// relative): the session URI has no trailing slash, and relative Uri
    /// resolution would replace its last segment instead of appending. The
    /// endpoint is injectable because the production port is machine-global,
    /// and a test that talked to it would reach a real Synapse.</para>
    ///
    /// <para>A claim that carries light colors (#557) paints its category
    /// light by light through the category's custom effect
    /// (<see cref="ChromaLayouts.EffectBody"/>), and a claim with none, or a
    /// Set Chroma Color, sends CHROMA_STATIC as before. Each category's
    /// lights are published for the Key Colors card while the worker runs.
    /// An effect goes out again only when its body changes, so a picture a
    /// category already shows is not sent twice.</para>
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
        private readonly Func<long> _clock;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _disposed;
        private int _passes;
        private int _waitMs;

        public ChromaBackend()
            : this(null, 1000, 30000, 100, DefaultHttpTimeoutMs, null, null) { }

        /// <summary>Test seam: the endpoint, the cadences, the HTTP timeout,
        /// the assignment lookups a Set Chroma Color reads, and the clock the
        /// worker reads, which a test moves itself so a macro's window is
        /// its own to open and close.</summary>
        internal ChromaBackend(string endpoint, int heartbeatMs, int retryMs, int pollMs, int httpTimeoutMs,
            Func<int, ISet<Guid>> devicesOnSlot, Func<int, int> slotNumber, Func<long> clock = null)
        {
            _endpoint = string.IsNullOrEmpty(endpoint) ? DefaultEndpoint : endpoint.TrimEnd('/');
            _heartbeatMs = heartbeatMs;
            _retryMs = retryMs;
            _pollMs = pollMs;
            _devicesOnSlot = devicesOnSlot ?? PeripheralOutputs.DevicesOnSlot;
            _slotNumber = slotNumber ?? PeripheralOutputs.DisplayedSlotNumber;
            _clock = clock ?? (() => Environment.TickCount64);
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs) };
        }

        /// <summary>Whether an endpoint is the one Razer's own server
        /// listens on.</summary>
        internal static bool IsRazersServer(string endpoint)
            => string.Equals(endpoint?.TrimEnd('/'), DefaultEndpoint, StringComparison.OrdinalIgnoreCase);

        /// <summary>What one category should show: a color, and the light
        /// colors on top of it, null for none.</summary>
        internal readonly record struct Want(string Category, int Rgb, KeyFrame Keys);

        internal bool WorkerAlive => _loop != null && !_loop.IsCompleted;

        /// <summary>Passes the worker has started, for the test of its
        /// wait.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker last asked for, so the test of a
        /// macro's lapse reads the wait it chose and times nothing.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            PeripheralOutputs.LightingChanged += Wake;
            PeripheralOutputs.ChromaMacroChanged += Wake;
            foreach (var pair in ChromaLayouts.Maps)
                PeripheralLightMaps.Publish(new OutputPath(OutputFamily.ChromaCategory, pair.Key), pair.Value);
            _loop = Task.Run(() => LoopAsync(token), token);
        }

        public void Stop()
        {
            if (_cts == null) return;
            PeripheralOutputs.LightingChanged -= Wake;
            PeripheralOutputs.ChromaMacroChanged -= Wake;
            foreach (var pair in ChromaLayouts.Maps)
                PeripheralLightMaps.Remove(new OutputPath(OutputFamily.ChromaCategory, pair.Key));
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

        /// <summary>
        /// The categories a Razer device lit on its own stands in for right
        /// now, or null for none (#494). With Synapse closed the direct
        /// worker paints such a device through its own reports, and the
        /// Chroma SDK's services can still be answering here: a session
        /// that listed the device's category would paint the same device
        /// through the SDK, from another ruling. The linker names the
        /// category on the device's row (<see cref="DeviceLinks.Shadows"/>),
        /// and it counts while the direct worker paints the device: a
        /// claim resolves on the row's own path, or a Set Chroma Color is
        /// live on a virtual controller that holds a row of it, which
        /// paints it with the switch off
        /// (<see cref="RazerDirectBackend.MacroOn"/>).
        /// </summary>
        internal static HashSet<string> Shadowed(LinkTable links, long now,
            Func<int, ISet<Guid>> devicesOnSlot, Func<int, int> slotNumber)
        {
            HashSet<string> shadowed = null;
            foreach (var row in links.ByDevice.Values)
            {
                if (row.Shadows.Length == 0) continue;
                bool painted = false;
                foreach (var path in row.Lighting)
                {
                    if (path.Shared) continue;
                    painted = PeripheralOutputs.TryResolveColor(path, out _)
                        || (links.ByPath.TryGetValue(path, out var rows)
                            && RazerDirectBackend.MacroOn(RazerDirectBackend.LitThrough(links, path, rows), now,
                                devicesOnSlot, slotNumber, out _));
                    if (painted) break;
                }
                if (!painted) continue;
                foreach (var shadow in row.Shadows)
                    if (shadow.Family == OutputFamily.ChromaCategory) (shadowed ??= new HashSet<string>()).Add(shadow.Key);
            }
            return shadowed;
        }

        /// <summary>Each category's color right now, in the canonical
        /// category order: a live Set Chroma Color on a slot that has a Razer
        /// device of the category, the smaller displayed number first, else
        /// the category's ruling claim with its light colors. A category a
        /// directly lit device stands in for is left out
        /// (<see cref="Shadowed"/>), so the session never lists it, and
        /// every other category is wanted as before.</summary>
        internal static List<Want> Wanted(LinkTable links, long now,
            Func<int, ISet<Guid>> devicesOnSlot, Func<int, int> slotNumber)
        {
            var shadowed = Shadowed(links, now, devicesOnSlot, slotNumber);
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

            var wanted = new List<Want>();
            foreach (var category in PeripheralLinker.ChromaCategories)
            {
                if (shadowed != null && shadowed.Contains(category)) continue;
                if (macros.TryGetValue(category, out var macro))
                    wanted.Add(new Want(category, macro.Rgb, null));
                else if (PeripheralOutputs.TryResolveColor(new OutputPath(OutputFamily.ChromaCategory, category),
                             out int rgb, out var ruler))
                    wanted.Add(new Want(category, rgb, ruler.Keys));
            }
            return wanted;
        }

        private static bool SameCategories(List<Want> wanted, List<string> session)
        {
            if (session == null || session.Count != wanted.Count) return false;
            for (int i = 0; i < wanted.Count; i++)
                if (wanted[i].Category != session[i]) return false;
            return true;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            string session = null;
            List<string> sessionCategories = null;
            // The effect each category last took, by its body.
            var lastSent = new Dictionary<string, string>();
            // The rejection last logged per category, so a category refused
            // on every poll logs once. Cleared for a category when a color is
            // accepted there and for all when a session opens, so a refusal
            // that comes back logs again (SendStaticAsync).
            var rejectLogged = new Dictionary<string, string>();
            // Set once "waiting for Synapse" is written, so a Synapse that
            // keeps refusing logs it once rather than at every retry. Cleared
            // when a session opens or nothing is claimed.
            bool waitingLogged = false;
            long lastHeartbeat = 0;
            long retryAt = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _passes);
                    long now = _clock();
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
                        waitingLogged = false;
                        PeripheralOutputs.SetProgramRefusal(OutputFamily.ChromaCategory, null);
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
                        foreach (var want in wanted) categories.Add(want.Category);
                        // HttpClient reports its own timeout as a
                        // TaskCanceledException, so the filter keeps a slow
                        // init on the retry path. Only a stop ends the loop.
                        try { session = await InitAsync(categories, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch { /* Synapse absent, refused, or slow: retry below */ }
                        if (session == null)
                        {
                            sessionCategories = null;
                            retryAt = _clock() + _retryMs;
                            ReportRefusal();
                            PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Waiting);
                            if (!waitingLogged)
                            {
                                waitingLogged = true;
                                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma waiting for Synapse");
                            }
                            continue;
                        }
                        sessionCategories = categories;
                        lastSent.Clear();
                        rejectLogged.Clear();
                        waitingLogged = false;
                        lastHeartbeat = _clock();
                        PeripheralOutputs.SetProgramRefusal(OutputFamily.ChromaCategory, null);
                        PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Connected);
                        PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma session for " + string.Join(",", categories));
                    }

                    bool broken = false;
                    try
                    {
                        long beat = _clock();
                        if (beat - lastHeartbeat >= _heartbeatMs)
                        {
                            lastHeartbeat = beat;
                            // No body, Colore's no-data PUT overload
                            // (RestClient.cs:107).
                            using var hb = await _http.PutAsync(session + "/heartbeat", null, ct).ConfigureAwait(false);
                            if (!hb.IsSuccessStatusCode) broken = true;
                        }
                        if (!broken)
                        {
                            foreach (var want in wanted)
                            {
                                string body = ChromaLayouts.EffectBody(want.Category, want.Rgb, want.Keys);
                                if (lastSent.TryGetValue(want.Category, out string sent) && sent == body) continue;
                                // A rejected PUT leaves the last effect in
                                // place, so the next poll retries it.
                                if (await SendEffectAsync(session, want.Category, body, rejectLogged, ct).ConfigureAwait(false))
                                    lastSent[want.Category] = body;
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
                        retryAt = _clock() + _retryMs;
                        ReportRefusal();
                        PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Waiting);
                        PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Chroma session lost");
                        continue;
                    }
                    await WaitAsync(PollWaitMs(now), ct).ConfigureAwait(false);
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
                PeripheralOutputs.SetProgramRefusal(OutputFamily.ChromaCategory, null);
                PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Idle);
            }
        }

        /// <summary>No session stands. With Synapse running that is its answer,
        /// which the tab names in place of a wait for it to start.</summary>
        private static void ReportRefusal()
            => PeripheralOutputs.SetProgramRefusal(OutputFamily.ChromaCategory,
                PeripheralOutputs.Presence.RazerSoftwareRunning ? ProgramRefusal.Everything : null);

        /// <summary>Waits a poll interval, or less when a claim changes.</summary>
        private async Task WaitAsync(int ms, CancellationToken ct)
        {
            Volatile.Write(ref _waitMs, ms);
            try { await _wake.WaitAsync(ms, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>The poll interval, or less while a Set Chroma Color is
        /// live, so the pass that lets the macro go runs at its lapse: nothing
        /// wakes the worker when an action stops being asserted. The lapse is
        /// that of a macro live at <paramref name="evaluated"/>, the tick the
        /// pass read its colors at, and the wait is what is left of it now.
        /// Taken from the tick the pass ended at, the lapse of a macro that
        /// ran out while its color was sent was already behind, and the
        /// worker held that color for a whole poll. The shape of
        /// LedSdkBackend.WaitWhileWantedAsync's deadline wait.</summary>
        private int PollWaitMs(long evaluated)
        {
            long lapse = PeripheralOutputs.NextChromaMacroLapse(evaluated);
            return lapse == long.MaxValue ? _pollMs : (int)Math.Min(_pollMs, Math.Max(1, lapse - _clock()));
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
            // Razer's own server is not called from the test host. A test
            // stands a server of its own in for it.
            if (PadForge.Engine.Common.VendorProgramGate.Refuses && IsRazersServer(_endpoint)) return null;
            using var resp = await _http.PostAsync(
                _endpoint + "/razer/chromasdk", JsonContent(InitBody(categories)), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("uri", out var uriProp)) return null;
            string uri = uriProp.GetString();
            return string.IsNullOrWhiteSpace(uri) ? null : uri.TrimEnd('/');
        }

        /// <summary>PUTs an effect to one category and returns whether it
        /// was accepted. The REST server answers HTTP 200 with a nonzero
        /// "result" on a rejected effect, and Colore checks that field after
        /// every effect call (Rest/RestApi.cs SetEffectAsync), so acceptance
        /// is read from the body. A rejection is logged once per category and
        /// code until that category is accepted or a new session opens, which
        /// <paramref name="rejectLogged"/>, the worker's own map, keeps.</summary>
        private async Task<bool> SendEffectAsync(string session, string category, string body,
            Dictionary<string, string> rejectLogged, CancellationToken ct)
        {
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
            if (reject == null)
            {
                rejectLogged.Remove(category);
                return true;
            }
            if (!rejectLogged.TryGetValue(category, out string logged) || logged != reject)
            {
                rejectLogged[category] = reject;
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
