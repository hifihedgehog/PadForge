using System;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services
{
    /// <summary>States the Lightsync mirror reports to the Dashboard.</summary>
    public enum LightsyncServiceState
    {
        Stopped,
        WaitingForGHub,
        Connected,
    }

    /// <summary>
    /// Logitech LIGHTSYNC lightbar mirror (#382, asked in discussion #379):
    /// forwards the lightbar color a game paints on a virtual PlayStation
    /// pad to Logitech LIGHTSYNC devices, the Razer Chroma mirror's (#373)
    /// sibling, leg for leg.
    ///
    /// <para>TRANSPORT. The official SDK ships games a shim,
    /// LogitechLedEnginesWrapper.dll, whose entire mechanism (proven by PE
    /// import and string-table inspection of the committed binaries in the
    /// cloned references, two wrapper generations) is: read the default
    /// value of SOFTWARE\Classes\CLSID\{A6519E67-7632-4375-AFDF-
    /// CAA889744403}\ServerBinary, LoadLibraryW the LED engine G HUB or
    /// LGS registered there, and GetProcAddress the undecorated cdecl
    /// LogiLed functions. PadForge replicates the shim instead of
    /// redistributing it: nothing of Logitech's ships in the repo, and the
    /// lighting functions are the family the shim resolves by identical
    /// name with no translation. Verified against RGB.NET
    /// (Native/_LogitechGSDK.cs, the same dynamic-load design), Aurora
    /// (LgsInstallationUtils.cs:34-64, the FileDescription validation
    /// adopted below), Artemis.Plugins, sidewinder94/Logitech-LED,
    /// logitech-led-sdk-rs (the official header's bindings), and
    /// LogiLed2Corsair.</para>
    ///
    /// <para>BEHAVIOR facts the references paid for, all adopted: colors
    /// are PERCENTAGES 0-100 (four independent confirmations), Aurora
    /// sleeps 100 ms between init and the first set ("logitech says to
    /// wait a bit of time between Init() and SetLighting()",
    /// LogitechDevice.cs:39-41), Aurora waits 5 s after the G HUB agent
    /// process reappears before re-initializing
    /// (LogitechRgbNetDevice.cs:33-38), the target-device mask is sticky
    /// global state re-asserted before sends (RGB.NET's per-queue
    /// discipline), save-restore brackets the session
    /// (LogitechDeviceProvider.cs:203, 276-293), a set color persists with
    /// no keep-alive anywhere in the corpus, and a dead G HUB announces
    /// itself only through failing calls, answered with teardown and
    /// re-init (RgbNetDevice.cs:134-138). The periodic re-send below is
    /// the liveness probe that surfaces those failures even when the game
    /// holds one color.</para>
    /// </summary>
    public sealed class LightsyncLightbarService : IDisposable
    {
        /// <summary>The native seam. Production is
        /// <see cref="LogiLedEngineNative"/>; the test bench scripts a
        /// fake, the Chroma fake-server pattern applied to a DLL
        /// boundary. All calls are made from the service worker only (the
        /// references serialize every SDK call, and the Rust binding wraps
        /// the whole API in a process mutex on the recorded assumption the
        /// SDK is not thread-safe).</summary>
        internal interface ILogiLedNative
        {
            /// <summary>Cheap presence gate: is any Logitech LED host
            /// process running (lghub_agent, lgs, LCore, the names every
            /// reference checks)?</summary>
            bool SoftwarePresent();

            /// <summary>Registry probe + engine load + export resolution.
            /// False when the key, file, validation, or a required export
            /// is missing. <paramref name="detail"/> names the failure for
            /// the diag line.</summary>
            bool TryLoad(out string detail);

            /// <summary>LogiLedInitWithName("PadForge"), falling back to
            /// LogiLedInit when the export is absent (old engines carry
            /// 13 exports). Nonzero return means a session began.</summary>
            bool Init();

            /// <summary>LogiLedSetTargetDevice(LOGI_DEVICETYPE_ALL = 7).
            /// Optional export: absent counts as success, matching the old
            /// SDK generation that had no target concept.</summary>
            bool SetTargetAll();

            /// <summary>LogiLedSaveCurrentLighting, so Restore can put the
            /// user's lighting back when the mirror stops.</summary>
            bool SaveCurrent();

            /// <summary>LogiLedSetLighting(r, g, b) in PERCENT 0-100.</summary>
            bool SetLighting(int rPct, int gPct, int bPct);

            /// <summary>LogiLedRestoreLighting then LogiLedShutdown, each
            /// swallowed independently (the RGB.NET dispose shape: the
            /// author does not trust Shutdown to restore, and teardown
            /// calls are known to be able to fail).</summary>
            void RestoreAndShutdown();

            /// <summary>FreeLibrary, delegates nulled first so a stray
            /// call lands on a null check instead of a freed code page
            /// (the RGB.NET UnloadLogitechGSDK discipline). Idempotent.</summary>
            void Unload();
        }

        // Last game-painted lightbar color, packed 0x00RRGGBB, -1 = no
        // game write yet. Static so the HM output callback publishes
        // without a service reference, one volatile write per decode,
        // last-writer-wins across slots. The Chroma shape verbatim.
        private static int s_publishedRgb = -1;

        private readonly ILogiLedNative _native;
        private readonly int _retryMs;
        private readonly int _pollMs;
        private readonly int _settleMs;
        private readonly int _presenceSettleMs;
        private readonly int _livenessMs;
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _disposed;

        /// <summary>Raised from the worker thread. The owner marshals.</summary>
        public event Action<LightsyncServiceState> StateChanged;

        /// <summary>Production constructor: the real engine loader and
        /// the shipped cadences.</summary>
        public LightsyncLightbarService() : this(null) { }

        internal LightsyncLightbarService(
            ILogiLedNative native = null,
            int retryMs = 30000,
            int pollMs = 100,
            int settleMs = 100,
            int presenceSettleMs = 5000,
            int livenessMs = 5000)
        {
            _native = native ?? new LogiLedEngineNative();
            _retryMs = retryMs;
            _pollMs = pollMs;
            _settleMs = settleMs;
            _presenceSettleMs = presenceSettleMs;
            _livenessMs = livenessMs;
        }

        /// <summary>Publishes the game-set lightbar color. Called from the
        /// HM output-decode callback beside the Chroma publish.</summary>
        public static void Publish(byte r, byte g, byte b)
            => Volatile.Write(ref s_publishedRgb, (r << 16) | (g << 8) | b);

        internal static void ResetPublishedForTest()
            => Volatile.Write(ref s_publishedRgb, -1);

        /// <summary>0-255 channel to the SDK's 0-100 percent, rounded
        /// (RGB.NET rounds, Aurora truncates and loses up to a percent
        /// per channel, rounding wins).</summary>
        internal static int ToPercent(int channel)
            => (int)Math.Round(Math.Clamp(channel, 0, 255) * 100.0 / 255.0);

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }

        public void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            try { _loop?.Wait(3000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        private void Report(LightsyncServiceState state)
        {
            PadForge.Engine.SdlDiagLog.WriteLine($"LIGHTSYNC state={state}");
            try { StateChanged?.Invoke(state); } catch { }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            bool wasAbsent = true;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Presence gate before any native load: the wasted
                    // engine load on a G HUB-less machine costs more than
                    // a process-name scan, and every reference gates the
                    // same way.
                    bool present = false;
                    try { present = _native.SoftwarePresent(); } catch { }
                    if (!present)
                    {
                        wasAbsent = true;
                        Report(LightsyncServiceState.WaitingForGHub);
                        await Task.Delay(_retryMs, ct);
                        continue;
                    }
                    if (wasAbsent)
                    {
                        // The agent just (re)appeared. Init immediately
                        // after G HUB starts succeeds but does nothing
                        // (Artemis waits 14 s on a cold start it launched
                        // itself, Aurora waits 5 s on reappearance).
                        wasAbsent = false;
                        await Task.Delay(_presenceSettleMs, ct);
                    }

                    if (!_native.TryLoad(out string detail))
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine($"LIGHTSYNC load failed: {detail}");
                        Report(LightsyncServiceState.WaitingForGHub);
                        await Task.Delay(_retryMs, ct);
                        continue;
                    }
                    if (!_native.Init())
                    {
                        _native.Unload();
                        Report(LightsyncServiceState.WaitingForGHub);
                        await Task.Delay(_retryMs, ct);
                        continue;
                    }

                    // Logitech's own guidance via Aurora: settle between
                    // init and the first lighting call.
                    await Task.Delay(_settleMs, ct);
                    _native.SetTargetAll();
                    _native.SaveCurrent();
                    Report(LightsyncServiceState.Connected);

                    int lastSent = -1;
                    long lastSendMs = 0;
                    int failStreak = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        int rgb = Volatile.Read(ref s_publishedRgb);
                        long now = Environment.TickCount64;
                        // Change-only, plus a periodic re-send of the held
                        // color: the SDK needs no keep-alive, but a failing
                        // re-send is the only signal a restarted G HUB
                        // gives, and it drops us back to the retry loop.
                        if (rgb >= 0 && (rgb != lastSent || now - lastSendMs >= _livenessMs))
                        {
                            _native.SetTargetAll(); // sticky global, re-asserted like RGB.NET
                            bool ok = _native.SetLighting(
                                ToPercent((rgb >> 16) & 0xFF),
                                ToPercent((rgb >> 8) & 0xFF),
                                ToPercent(rgb & 0xFF));
                            if (ok)
                            {
                                lastSent = rgb;
                                lastSendMs = now;
                                failStreak = 0;
                            }
                            else if (++failStreak >= 3)
                            {
                                PadForge.Engine.SdlDiagLog.WriteLine("LIGHTSYNC send failing, reinitializing");
                                break;
                            }
                        }
                        await Task.Delay(_pollMs, ct);
                    }

                    // Session over (engine died, or we are stopping): put
                    // the user's lighting back and release the engine.
                    try { _native.RestoreAndShutdown(); } catch { }
                    try { _native.Unload(); } catch { }
                    if (!ct.IsCancellationRequested)
                    {
                        wasAbsent = true;
                        Report(LightsyncServiceState.WaitingForGHub);
                        await Task.Delay(_retryMs, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                try { _native.RestoreAndShutdown(); } catch { }
                try { _native.Unload(); } catch { }
                Report(LightsyncServiceState.Stopped);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
