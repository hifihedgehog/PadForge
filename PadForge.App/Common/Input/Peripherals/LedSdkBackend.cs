using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The native seam of the LED SDK worker. Production is
    /// <see cref="PadForge.Services.LogiLedEngineNative"/>; the test bench
    /// scripts a fake. Every call is made from the worker alone: the
    /// references serialize every SDK call, and the Rust binding wraps the
    /// whole API in a process mutex on the recorded assumption that the SDK
    /// is not thread-safe.
    /// </summary>
    internal interface ILogiLedNative
    {
        /// <summary>Cheap presence gate: is a Logitech LED host process
        /// running (lghub_agent, lgs, LCore, the names every reference
        /// checks)?</summary>
        bool SoftwarePresent();

        /// <summary>Registry probe, engine load and export resolution.</summary>
        bool TryLoad(out string detail);

        /// <summary>LogiLedInitWithName("PadForge"), falling back to
        /// LogiLedInit.</summary>
        bool Init();

        /// <summary>LogiLedSetTargetDevice with a LOGI_DEVICETYPE mask. A
        /// missing export counts as success.</summary>
        bool SetTarget(int deviceTypeMask);

        /// <summary>Whether the loaded engine exports
        /// LogiLedSetTargetDevice. Without it a LogiLedSetLighting reaches
        /// every connected device (LogitechGamingLEDSDK.pdf p.23), so nothing
        /// that should reach one class only is sent.</summary>
        bool TargetCallsAvailable { get; }

        bool SaveCurrent();

        /// <summary>LogiLedRestoreLighting: the lighting saved at the start
        /// of the session comes back.</summary>
        bool Restore();

        /// <summary>LogiLedSetLighting in PERCENT 0-100, on the target mask.</summary>
        bool SetLighting(int rPct, int gPct, int bPct);

        /// <summary>LogiLedSetLightingForTargetZone in PERCENT 0-100. False
        /// when the engine lacks the export or the call answers false.</summary>
        bool SetLightingForTargetZone(int deviceType, int zone, int rPct, int gPct, int bPct);

        /// <summary>Whether the loaded engine exports
        /// LogiLedSetLightingForTargetZone. Without it only the whole-device
        /// path and per-key keyboards can be painted, both through
        /// LogiLedSetLighting on their own target.</summary>
        bool ZoneCallsAvailable { get; }

        /// <summary>Restore, then shutdown, each swallowed.</summary>
        void RestoreAndShutdown();

        /// <summary>FreeLibrary, delegates nulled first. Idempotent.</summary>
        void Unload();
    }

    /// <summary>
    /// The Logitech LED SDK worker (#494). While G HUB runs it owns the
    /// devices it lights, so a Logitech device lit through HID++ takes its
    /// LED SDK device type instead of its own unit, and the LIGHTSYNC row
    /// takes the types no device claims. The SDK addresses a device type,
    /// never one unit: every assigned mouse shares the mouse type, and the
    /// smallest displayed player number rules it.
    ///
    /// <para>Each claimed type gets its color on every zone the references
    /// write for it: the mouse's three, the mousemat's one, the headset's and
    /// speaker's four (Aurora LogitechDevice.cs:104-123), the keyboard's five
    /// (RGB.NET's G213 at LogitechDeviceProvider.cs:99), and for a per-key
    /// keyboard the whole board through LogiLedSetTargetDevice(PERKEY_RGB) and
    /// LogiLedSetLighting.</para>
    ///
    /// <para>The LIGHTSYNC row's whole-device path lights the devices with no
    /// zones through LogiLedSetTargetDevice(RGB | MONOCHROME) and
    /// LogiLedSetLighting, the manual's own example, which per-key boards
    /// ignore (LogitechGamingLEDSDK.pdf p.23). RGB.NET lights them the same
    /// way on the RGB target alone (LogitechPerDeviceUpdateQueue.cs:34-37),
    /// and the lightbar mirror this replaces lit them on every target. The
    /// call reaches zonal devices too, so as in Aurora, which sends it before
    /// the zone calls on every update (LogitechDevice.cs:94-124), every
    /// claimed type is painted again after it.</para>
    ///
    /// <para>The manual gives two reasons a call answers false,
    /// a session that was never initialized and a lost connection to
    /// Logitech's software (LogitechGamingLEDSDK.pdf,
    /// LogiLedSetLightingForTargetZone). It says nothing about a type with no
    /// device behind it, so a type that fails alone is never read as a dead
    /// session.</para>
    ///
    /// <para>The session brackets the user's lighting with save and restore,
    /// and runs only while a type is claimed. A type that stops being claimed
    /// while others still are gets the saved lighting back through a restore,
    /// and the remaining types are painted again over it.</para>
    ///
    /// <para>Behavior the references paid for, carried from the LIGHTSYNC
    /// mirror (#382) this replaces. Colors are PERCENTAGES. Aurora sleeps
    /// 100 ms between init and the first set and waits 5 s after the G HUB
    /// agent reappears. A set color needs no keep-alive, but a dead G HUB
    /// shows only as failing calls, so every claimed type is sent again every
    /// <see cref="LivenessMs"/>, and three of those rounds in a row in which
    /// no call took reinitialize.
    /// A Stop that times out inside a native call leaves the worker as an
    /// orphan the next Start waits for, so two workers never overlap inside
    /// the SDK. An orphan that finishes before a newer worker loads the
    /// engine still restores and shuts down its own session, and one that
    /// finishes after leaves the SDK to the newer session.</para>
    ///
    /// <para>An engine without the zone call paints only the whole-device
    /// path and per-key keyboards, and one without the target call paints
    /// zones only. A session for types the engine cannot paint would paint
    /// nothing and fail every liveness round, so the engine goes back
    /// unopened until the claimed types change, and the types it can paint
    /// are published for the Lighting tab
    /// (<see cref="PeripheralOutputs.LedSdkPaintable"/>).</para>
    /// </summary>
    internal sealed class LedSdkBackend : IDisposable
    {
        internal const int LogiDeviceTypeAll = 7;     // MONOCHROME 1 | RGB 2 | PERKEY_RGB 4
        internal const int LogiDeviceTypePerKey = 4;
        internal const int LogiDeviceTypeWholeDevices = 3;   // MONOCHROME 1 | RGB 2
        internal const int LivenessMs = 5000;

        /// <summary>How long a new worker waits for an orphan before it loads
        /// the engine anyway. Above the 14 s an Artemis-launched G HUB cold
        /// start takes.</summary>
        internal const int DefaultOrphanWaitMs = 15000;

        /// <summary>LogiLed::DeviceType per path key
        /// (logitech-led-sdk-rs bindings-x86_64.rs:136-142) and the zones
        /// written for it.</summary>
        internal static (int Code, int Zones) TypeOf(string key) => key switch
        {
            "keyboard" => (0, 5),
            "mouse" => (3, 3),
            "mousemat" => (4, 1),
            "headset" => (8, 4),
            "speaker" => (14, 4),
            _ => (-1, 0),
        };

        private static int s_generation;
        private static Task s_orphan;

        /// <summary>The newest generation that started loading the engine,
        /// written under <see cref="s_sdkGate"/>, which an orphan's teardown
        /// holds too, so the two never run at once.</summary>
        private static int s_loaderGeneration;
        private static readonly object s_sdkGate = new();

        /// <summary>The longest a worker waits for an orphan's teardown before
        /// it loads the engine anyway.</summary>
        internal const int GateWaitMs = 3000;

        private readonly ILogiLedNative _native;
        private readonly int _retryMs;
        private readonly int _pollMs;
        private readonly int _settleMs;
        private readonly int _presenceSettleMs;
        private readonly int _livenessMs;
        private readonly int _stopWaitMs;
        private readonly int _orphanWaitMs;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _disposed;

        public LedSdkBackend() : this(null) { }

        internal LedSdkBackend(
            ILogiLedNative native = null,
            int retryMs = 30000,
            int pollMs = 100,
            int settleMs = 100,
            int presenceSettleMs = 5000,
            int livenessMs = LivenessMs,
            int stopWaitMs = 3000,
            int orphanWaitMs = DefaultOrphanWaitMs)
        {
            _native = native ?? new PadForge.Services.LogiLedEngineNative();
            _retryMs = retryMs;
            _pollMs = pollMs;
            _settleMs = settleMs;
            _presenceSettleMs = presenceSettleMs;
            _livenessMs = livenessMs;
            _stopWaitMs = stopWaitMs;
            _orphanWaitMs = orphanWaitMs;
        }

        /// <summary>0-255 channel to the SDK's 0-100 percent, rounded.</summary>
        internal static int ToPercent(int channel)
            => (int)Math.Round(Math.Clamp(channel, 0, 255) * 100.0 / 255.0);

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            int generation = Interlocked.Increment(ref s_generation);
            PeripheralOutputs.LightingChanged += Wake;
            _loop = Task.Run(() => LoopAsync(ct, generation));
        }

        public void Stop()
        {
            if (_cts == null) return;
            PeripheralOutputs.LightingChanged -= Wake;
            _cts.Cancel();
            bool finished = true;
            try { finished = _loop == null || _loop.Wait(_stopWaitMs); } catch { }
            if (!finished)
            {
                Volatile.Write(ref s_orphan, _loop);
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL LED SDK stop timed out after {_stopWaitMs} ms, worker orphaned inside the SDK");
            }
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

        private static bool Superseded(int generation) => generation != Volatile.Read(ref s_generation);

        private void Report(BackendState state, int generation)
        {
            if (Superseded(generation)) return;
            PeripheralOutputs.SetBackendState(OutputFamily.LedSdkType, state);
        }

        /// <summary>Each claimed type's color right now, in path order.</summary>
        internal static List<KeyValuePair<string, int>> Wanted()
        {
            var wanted = new List<KeyValuePair<string, int>>();
            foreach (var type in PeripheralLinker.LedSdkTypes)
                if (PeripheralOutputs.TryResolveColor(new OutputPath(OutputFamily.LedSdkType, type), out int rgb))
                    wanted.Add(new KeyValuePair<string, int>(type, rgb));
            return wanted;
        }

        private async Task WaitAsync(int ms, CancellationToken ct)
        {
            try { await _wake.WaitAsync(ms, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException)
            {
                // A disposed semaphore throws before it reads the token, which
                // would turn every wait of an orphaned worker into a spin.
                ct.ThrowIfCancellationRequested();
                await Task.Delay(ms, ct).ConfigureAwait(false);
            }
        }

        /// <summary>The claimed types, in path order, as one key.</summary>
        private static string TypesOf(List<KeyValuePair<string, int>> wanted)
        {
            var key = new System.Text.StringBuilder();
            foreach (var pair in wanted) key.Append(pair.Key).Append(',');
            return key.ToString();
        }

        /// <summary>Whether the loaded engine can paint a path: the whole
        /// devices and a per-key keyboard need the target call, every zone the
        /// zone call.</summary>
        private bool CanPaint(string type) => type switch
        {
            PeripheralLinker.LedSdkWholeDevices => _native.TargetCallsAvailable,
            "keyboard" => _native.TargetCallsAvailable || _native.ZoneCallsAvailable,
            _ => _native.ZoneCallsAvailable,
        };

        /// <summary>Whether this engine can paint any claimed type.</summary>
        private bool AnyPaintable(List<KeyValuePair<string, int>> wanted)
        {
            foreach (var pair in wanted)
                if (CanPaint(pair.Key)) return true;
            return false;
        }

        private void PublishPaintable(int generation)
        {
            if (Superseded(generation)) return;
            var paintable = new List<string>();
            foreach (var type in PeripheralLinker.LedSdkTypes)
                if (CanPaint(type)) paintable.Add(type);
            PeripheralOutputs.LedSdkPaintable = paintable.ToArray();
        }

        private static void ForgetPaintable(int generation)
        {
            if (Superseded(generation)) return;
            PeripheralOutputs.LedSdkPaintable = null;
        }

        /// <summary>Waits until the claimed types differ from
        /// <paramref name="types"/>.</summary>
        private async Task WaitForTypesChangeAsync(string types, CancellationToken ct)
        {
            while (TypesOf(Wanted()) == types)
            {
                ct.ThrowIfCancellationRequested();
                await WaitAsync(_pollMs, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Records this worker as the newest to load the engine,
        /// after any orphan's teardown in progress, bounded.</summary>
        private static void MarkLoader(int generation)
        {
            bool entered = false;
            try
            {
                Monitor.TryEnter(s_sdkGate, GateWaitMs, ref entered);
                if (generation > Volatile.Read(ref s_loaderGeneration))
                    Volatile.Write(ref s_loaderGeneration, generation);
            }
            finally { if (entered) Monitor.Exit(s_sdkGate); }
        }

        /// <summary>Ends this worker's session: the user's lighting back and
        /// the engine shut down, unless a newer worker has already loaded the
        /// engine and holds the SDK, then the engine released.</summary>
        private void Teardown(int generation)
        {
            bool entered = false;
            try
            {
                Monitor.TryEnter(s_sdkGate, GateWaitMs, ref entered);
                if (Volatile.Read(ref s_loaderGeneration) <= generation)
                    try { _native.RestoreAndShutdown(); } catch { }
                try { _native.Unload(); } catch { }
            }
            finally { if (entered) Monitor.Exit(s_sdkGate); }
        }

        /// <summary>Waits <paramref name="ms"/> unless the claims all go,
        /// polled at the poll interval. True when something is still
        /// claimed at the end.</summary>
        private async Task<bool> WaitWhileWantedAsync(int ms, CancellationToken ct)
        {
            long until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                ct.ThrowIfCancellationRequested();
                if (Wanted().Count == 0) return false;
                await WaitAsync(Math.Min(_pollMs, (int)Math.Max(1, until - Environment.TickCount64)), ct).ConfigureAwait(false);
            }
            return Wanted().Count > 0;
        }

        /// <summary>One type in one color. True when any call took.</summary>
        private bool Paint(string type, int rgb)
        {
            int r = ToPercent((rgb >> 16) & 0xFF), g = ToPercent((rgb >> 8) & 0xFF), b = ToPercent(rgb & 0xFF);
            if (type == PeripheralLinker.LedSdkWholeDevices)
            {
                // One color for every device without zones, on the RGB and
                // monochrome targets. The mask is sticky global state, so it
                // goes back to every type after the call.
                _native.SetTarget(LogiDeviceTypeWholeDevices);
                bool took = _native.SetLighting(r, g, b);
                _native.SetTarget(LogiDeviceTypeAll);
                return took;
            }
            var (code, zones) = TypeOf(type);
            if (code < 0) return false;
            bool any = false;
            if (type == "keyboard" && _native.TargetCallsAvailable)
            {
                // A per-key board answers only the whole-board call on its
                // own target mask, which the call needs: without it the SDK
                // paints every device.
                _native.SetTarget(LogiDeviceTypePerKey);
                any |= _native.SetLighting(r, g, b);
            }
            // The mask is sticky global state, so it goes back to every type
            // before the zone calls, as RGB.NET sets it before every zone
            // batch (LogitechZoneUpdateQueue.cs).
            _native.SetTarget(LogiDeviceTypeAll);
            if (_native.ZoneCallsAvailable)
                for (int zone = 0; zone < zones; zone++)
                    any |= _native.SetLightingForTargetZone(code, zone, r, g, b);
            return any;
        }

        private async Task LoopAsync(CancellationToken ct, int generation)
        {
            bool loaded = false;
            bool wasAbsent = true;
            try
            {
                Task orphan = Volatile.Read(ref s_orphan);
                if (orphan != null && !orphan.IsCompleted)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        "PERIPHERAL LED SDK waiting for the orphaned worker of the previous session to leave the SDK");
                    long deadline = Environment.TickCount64 + _orphanWaitMs;
                    while (!orphan.IsCompleted && Environment.TickCount64 < deadline)
                        await Task.Delay(50, ct).ConfigureAwait(false);
                }
                if (orphan == null || orphan.IsCompleted)
                    _ = Interlocked.CompareExchange(ref s_orphan, null, orphan);

                while (!ct.IsCancellationRequested)
                {
                    if (Wanted().Count == 0)
                    {
                        ForgetPaintable(generation);
                        Report(BackendState.Idle, generation);
                        await WaitAsync(_pollMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Presence before any native load.
                    bool present = false;
                    try { present = _native.SoftwarePresent(); } catch { }
                    if (!present)
                    {
                        wasAbsent = true;
                        Report(BackendState.Waiting, generation);
                        await WaitWhileWantedAsync(_retryMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (wasAbsent)
                    {
                        // Init right after G HUB starts succeeds and does
                        // nothing (Aurora waits 5 s on reappearance).
                        wasAbsent = false;
                        if (!await WaitWhileWantedAsync(_presenceSettleMs, ct).ConfigureAwait(false)) continue;
                    }

                    // A worker a newer Start replaced loads nothing more.
                    if (Superseded(generation)) break;
                    MarkLoader(generation);
                    if (!_native.TryLoad(out string detail))
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LED SDK load failed: {detail}");
                        Report(BackendState.Waiting, generation);
                        await WaitWhileWantedAsync(_retryMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    loaded = true;
                    PublishPaintable(generation);
                    if (!AnyPaintable(Wanted()))
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            "PERIPHERAL LED SDK engine cannot paint any claimed type");
                        _native.Unload();
                        loaded = false;
                        Report(BackendState.Waiting, generation);
                        await WaitForTypesChangeAsync(TypesOf(Wanted()), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!_native.Init())
                    {
                        _native.Unload();
                        loaded = false;
                        Report(BackendState.Waiting, generation);
                        await WaitWhileWantedAsync(_retryMs, ct).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(_settleMs, ct).ConfigureAwait(false);
                    _native.SetTarget(LogiDeviceTypeAll);
                    _native.SaveCurrent();
                    Report(BackendState.Connected, generation);
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL LED SDK session open");

                    var sent = new Dictionary<string, int>();
                    long lastRound = Environment.TickCount64 - _livenessMs;
                    int failStreak = 0;
                    bool reinit = false;
                    while (!ct.IsCancellationRequested)
                    {
                        var wanted = Wanted();
                        if (wanted.Count == 0 || !AnyPaintable(wanted)) break;
                        long now = Environment.TickCount64;

                        // A type nobody claims any more gets the saved
                        // lighting back, and the others go on top again.
                        bool dropped = false;
                        foreach (var type in sent.Keys)
                        {
                            bool still = false;
                            foreach (var pair in wanted)
                                if (pair.Key == type) { still = true; break; }
                            if (!still) { dropped = true; break; }
                        }
                        if (dropped)
                        {
                            _native.Restore();
                            sent.Clear();
                        }

                        // The liveness round sends every claimed type. Between
                        // rounds only a changed color goes out.
                        bool round = now - lastRound >= _livenessMs;
                        if (round) lastRound = now;
                        bool took = false;
                        bool repaintAll = false;
                        foreach (var pair in wanted)
                        {
                            // A type this engine cannot paint counts for
                            // nothing.
                            if (!CanPaint(pair.Key)) continue;
                            if (!round && !repaintAll && sent.TryGetValue(pair.Key, out int last) && last == pair.Value)
                                continue;
                            took |= Paint(pair.Key, pair.Value);
                            // Recorded whether or not it took, so a type that
                            // fails waits for a new color or the next round
                            // instead of going out on every poll.
                            sent[pair.Key] = pair.Value;
                            // The whole-device call reaches zonal devices
                            // too, so every type after it goes out again.
                            if (pair.Key == PeripheralLinker.LedSdkWholeDevices) repaintAll = true;
                        }
                        if (took) failStreak = 0;
                        else if (round && ++failStreak >= 3)
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL LED SDK calls failing, reinitializing");
                            reinit = true;
                            break;
                        }
                        await WaitAsync(_pollMs, ct).ConfigureAwait(false);
                    }

                    // The session ends: the user's lighting goes back and the
                    // engine is released, unless a newer session already holds
                    // the SDK.
                    Teardown(generation);
                    loaded = false;
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL LED SDK handed back");
                    if (reinit && !ct.IsCancellationRequested)
                    {
                        wasAbsent = true;
                        Report(BackendState.Waiting, generation);
                        await WaitWhileWantedAsync(_retryMs, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL LED SDK worker fault: " + ex.GetType().Name);
            }
            finally
            {
                if (loaded) Teardown(generation);
                ForgetPaintable(generation);
                Report(BackendState.Idle, generation);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
