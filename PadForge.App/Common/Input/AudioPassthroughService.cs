using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Issue #83 — per-assigned-device controller audio. Owns one audio sink
    /// per speaker-capable Sony pad (DualSense / Edge / DualShock 4) assigned
    /// to a slot, and feeds each sink from two sources: the slot's macro
    /// sounds (always) and a WASAPI loopback mirror of the system default
    /// output (per-device toggle, <c>PlayStationSlotConfig.AudioPassthroughEnabled</c>).
    ///
    /// <para><b>Reference implementation:</b> DualSenseY-v2's
    /// <c>audioPassthrough.cpp</c> (cloned at
    /// <c>..\DualSenseY-v2</c>), ported to PadForge idioms:</para>
    /// <list type="bullet">
    /// <item><b>USB</b> — the pad's USB Audio Class endpoint is found by
    /// Container-ID match (the HID interface and the UAC interface of the
    /// same physical pad share a container). Playback at 48 kHz float,
    /// frames per the reference: channel 0 zeroed, channel 1 carries the
    /// mono program mix (the firmware's speaker tap), remaining channels
    /// (DualSense haptic actuators) zeroed.
    /// (<c>PlaybackDualsenseDataCallback</c> / <c>PlaybackDualshock4DataCallback</c>,
    /// <c>FindDeviceByContainerIdWindows</c>.)</item>
    /// <item><b>Bluetooth</b> — no Windows endpoint exists; audio is sent to
    /// the firmware's speaker stream in Sony BT HID frames: report 0x35
    /// (334 bytes) carrying a 0x11 session header packet and a 0x13 packet
    /// (0x16 would be the headset jack) whose payload is one Opus frame —
    /// 48 kHz stereo, 10 ms, hard CBR 160 kbps so every frame is exactly
    /// 200 bytes — CRC32 tail, one report every ~10.67 ms. Research credit:
    /// egormanga's SAxense (transport/packet grammar,
    /// https://apps.sdore.me/SAxense), u/Idkiamaguy645 (combined notes +
    /// testing), and awalol's dualsense-bt-haptics HeadsetPlayMusic tool
    /// (the speaker recipe).</item>
    /// </list>
    ///
    /// <para>The DS5 dispatcher asserts the firmware speaker output path +
    /// volume in the effect report while a device's sink is active —
    /// <see cref="WantsSpeakerPath"/> / <see cref="TryConsumeSpeakerPathCleared"/>.</para>
    /// </summary>
    internal static class AudioPassthroughService
    {
        private const int MaxPads = 16;
        private const int Rate = 48000;

        // Sony speaker-capable pads (mirrors UserEffectsDispatcher).
        private const ushort SonyVid = 0x054C;
        private static readonly ushort[] Ds5Pids = { 0x0CE6, 0x0DF2 };
        private static readonly ushort[] Ds4Pids = { 0x05C4, 0x09CC, 0x0BA0 };

        private static readonly object _lock = new();

        // TEMP diagnostics for first-hardware bring-up; remove once the
        // user confirms speaker output on USB and BT.
        private static void Diag(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge_audio_diag.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // ─────────────────────────────────────────────
        //  App wiring
        // ─────────────────────────────────────────────

        /// <summary>Per-slot per-device passthrough flags, wired by
        /// InputService against the live PlayStationSlotConfig dictionaries:
        /// returns (deviceGuid, passthroughEnabled) for a slot.</summary>
        public static Func<int, IEnumerable<(Guid Device, bool PassthroughOn)>> PassthroughConfigProvider { get; set; }

        // ─────────────────────────────────────────────
        //  Sink model
        // ─────────────────────────────────────────────

        private sealed class Sink
        {
            public Guid DeviceGuid;
            public int Slot;
            public bool IsBt;
            public bool IsDs4;
            public string HidPath;
            public bool PassthroughOn;

            /// <summary>The pad's PnP container (set when the USB transport
            /// is built) — used to keep the loopback mirror from capturing
            /// the pad's own endpoint back into itself.</summary>
            public Guid Container;

            /// <summary>Macro sounds targeted at this sink (48 kHz stereo float).</summary>
            public MixingSampleProvider MacroMixer;
            /// <summary>Macro + optional loopback mirror.</summary>
            public SinkSource Source;

            /// <summary>Last tick the source produced a non-silent sample
            /// (written by <see cref="SinkSource.Read"/>); the BT stream
            /// pauses after 2 s of silence so an idle pad's radio rests.</summary>
            public long LastAudibleTicks;

            /// <summary>Set by the BT stream thread on a failed write; the
            /// worker detaches and rebuilds/tears down. Streaming threads
            /// never do transport I/O themselves.</summary>
            public volatile bool TransportFailed;

            /// <summary>Per-sink Opus encoder for the BT speaker stream
            /// (created and used only on the BT thread).</summary>
            public IOpusEncoder OpusEncoder;

            // USB
            public IWavePlayer Player;

            // BT
            public IntPtr BtHandle = new IntPtr(-1);
            public IntPtr BtEvent = IntPtr.Zero;
        }

        private static readonly Dictionary<Guid, Sink> _sinks = new();
        private static Thread _btThread;
        private static Thread _workerThread;
        private static readonly AutoResetEvent _workSignal = new(false);
        private static volatile bool _running;

        // Speaker-path bookkeeping for the DS5 dispatcher.
        private static readonly HashSet<Guid> _speakerPathCleared = new();

        // ─────────────────────────────────────────────
        //  Loopback capture (shared ring, per-sink cursors)
        // ─────────────────────────────────────────────

        private const int RingFrames = Rate / 2; // 0.5 s of 48k stereo
        private static readonly float[] _ring = new float[RingFrames * 2];
        private static long _ringWrite; // total frames written (monotonic)
        private static WasapiLoopbackCapture _capture;
        private static string _captureDeviceId = "";

        private static bool AnyPassthroughOn_NoLock()
            => _sinks.Values.Any(s => s.PassthroughOn);

        /// <summary>Worker-only. Brief locks around state; all COM and
        /// capture start/stop happens unlocked so no other thread ever
        /// waits on device I/O.</summary>
        private static void ReconcileCaptureOnWorker()
        {
            bool want;
            string haveId;
            bool haveCapture;
            lock (_lock)
            {
                want = _running && AnyPassthroughOn_NoLock();
                haveId = _captureDeviceId;
                haveCapture = _capture != null;
            }

            if (!want)
            {
                StopCaptureOnWorker();
                return;
            }

            // Restart when the default render device changed (DSY-v2
            // re-validates every 5 s rather than registering callbacks).
            string defaultId = "";
            Guid defaultContainer = Guid.Empty;
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = dev.ID;
                defaultContainer = GetEndpointContainerId(dev);
            }
            catch { }

            // Never mirror a pad's own endpoint back into itself.
            bool feedback;
            lock (_lock)
                feedback = defaultContainer != Guid.Empty
                    && _sinks.Values.Any(x => x.Container == defaultContainer);
            if (feedback)
            {
                Diag("[CAPTURE-SKIP] default output is a pad's own endpoint");
                StopCaptureOnWorker();
                return;
            }

            if (haveCapture && string.Equals(defaultId, haveId, StringComparison.Ordinal))
                return;

            StopCaptureOnWorker();
            try
            {
                var cap = new WasapiLoopbackCapture(); // default render endpoint
                int srcRate = cap.WaveFormat.SampleRate;
                int srcCh = cap.WaveFormat.Channels;
                int bytesPerSample = cap.WaveFormat.BitsPerSample / 8;
                bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
                double step = (double)srcRate / Rate;

                double pos = 0;
                cap.DataAvailable += (s, e) =>
                {
                    // Convert to 48 kHz stereo float and append to the ring.
                    int srcFrames = e.BytesRecorded / (bytesPerSample * srcCh);
                    if (srcFrames <= 0) return;
                    lock (_ring)
                    {
                        for (; pos < srcFrames; pos += step)
                        {
                            int f = (int)pos;
                            float l, r;
                            if (isFloat)
                            {
                                l = BitConverter.ToSingle(e.Buffer, (f * srcCh + 0) * 4);
                                r = srcCh > 1 ? BitConverter.ToSingle(e.Buffer, (f * srcCh + 1) * 4) : l;
                            }
                            else // 16-bit PCM
                            {
                                l = BitConverter.ToInt16(e.Buffer, (f * srcCh + 0) * 2) / 32768f;
                                r = srcCh > 1 ? BitConverter.ToInt16(e.Buffer, (f * srcCh + 1) * 2) / 32768f : l;
                            }
                            long idx = (_ringWrite % RingFrames) * 2;
                            _ring[idx] = l;
                            _ring[idx + 1] = r;
                            _ringWrite++;
                        }
                        pos -= srcFrames;
                    }
                };
                cap.RecordingStopped += (s, e) => { /* worker restarts on its next pass */ };
                cap.StartRecording();
                bool committed = false;
                lock (_lock)
                {
                    if (_running)
                    {
                        _capture = cap;
                        _captureDeviceId = defaultId;
                        committed = true;
                    }
                }
                if (!committed)
                {
                    try { cap.StopRecording(); } catch { }
                    try { cap.Dispose(); } catch { }
                    return;
                }
                Diag($"[CAPTURE] started on default endpoint (rate={srcRate} ch={srcCh} float={isFloat})");
            }
            catch (Exception ex)
            {
                Diag($"[CAPTURE-FAIL] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void StopCaptureOnWorker()
        {
            WasapiLoopbackCapture old;
            lock (_lock)
            {
                old = _capture;
                _capture = null;
                _captureDeviceId = "";
            }
            if (old == null) return;
            try { old.StopRecording(); } catch { }
            try { old.Dispose(); } catch { }
        }

        /// <summary>Per-sink source: the sink's own macro mixer plus (when
        /// the passthrough toggle is on) this sink's cursor over the shared
        /// loopback ring. 48 kHz stereo float.</summary>
        private sealed class SinkSource : ISampleProvider
        {
            private readonly Sink _sink;
            private long _cursor = -1;

            public SinkSource(Sink sink) { _sink = sink; }

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

            public int Read(float[] buffer, int offset, int count)
            {
                // Macro mixer (ReadFully → zeros when idle).
                int read = _sink.MacroMixer.Read(buffer, offset, count);
                for (int i = read; i < count; i++) buffer[offset + i] = 0f;

                if (_sink.PassthroughOn)
                {
                    // Full scale: master volume lives in the firmware speaker
                    // volume byte (UserEffectsDispatcher), not the samples.
                    // Latency clamp: if this sink's cursor falls more than
                    // ~50 ms behind the live edge (pacing drift, stalls),
                    // jump to a ~20 ms cushion behind live rather than ever
                    // playing stale audio.
                    const int MaxLagFrames = 2400;  // 50 ms @ 48 kHz
                    const int CushionFrames = 960;  // 20 ms
                    lock (_ring)
                    {
                        long avail = _ringWrite;
                        if (_cursor < 0 || _cursor > avail || avail - _cursor > MaxLagFrames)
                            _cursor = Math.Max(0, avail - CushionFrames);
                        int frames = count / 2;
                        int canRead = (int)Math.Min(frames, avail - _cursor);
                        for (int f = 0; f < canRead; f++)
                        {
                            long idx = ((_cursor + f) % RingFrames) * 2;
                            buffer[offset + f * 2] += _ring[idx];
                            buffer[offset + f * 2 + 1] += _ring[idx + 1];
                        }
                        _cursor += canRead;
                    }
                }

                // Activity stamp for the BT idle gate: any non-silent sample
                // keeps the stream sending; 2 s of silence pauses it.
                for (int i = 0; i < count; i++)
                {
                    if (buffer[offset + i] > 1e-4f || buffer[offset + i] < -1e-4f)
                    {
                        _sink.LastAudibleTicks = Environment.TickCount64;
                        break;
                    }
                }
                return count;
            }
        }

        /// <summary>USB frame shaper per the DSY-v2 playback callbacks:
        /// channel 0 = 0, channel 1 = mono program mix (the firmware's
        /// speaker tap), channels 2+ (DualSense haptic actuators) = 0.
        /// IWaveProvider carrying the endpoint's own mix format: shared-mode
        /// WASAPI rejects a non-extensible format beyond stereo
        /// (E_INVALIDARG — the DS5 endpoint is extensible float 48k 4ch),
        /// and NAudio's ISampleProvider Init path refuses extensible-float,
        /// so the float→byte hop happens here.</summary>
        private sealed class UsbFrameProvider : IWaveProvider
        {
            private readonly ISampleProvider _src;
            private readonly int _outChannels;
            private float[] _pull = new float[4096];
            private float[] _frames = new float[8192];

            public UsbFrameProvider(ISampleProvider src, WaveFormat endpointFormat)
            {
                _src = src;
                WaveFormat = endpointFormat;
                _outChannels = endpointFormat.Channels;
            }

            public WaveFormat WaveFormat { get; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int frames = count / (4 * _outChannels);
                int need = frames * 2;
                if (_pull.Length < need) _pull = new float[need];
                if (_frames.Length < frames * _outChannels) _frames = new float[frames * _outChannels];
                _src.Read(_pull, 0, need);
                for (int f = 0; f < frames; f++)
                {
                    float mono = Math.Clamp((_pull[f * 2] + _pull[f * 2 + 1]) * 0.5f, -1f, 1f);
                    int o = f * _outChannels;
                    _frames[o] = 0f;
                    _frames[o + 1] = mono;
                    for (int c = 2; c < _outChannels; c++) _frames[o + c] = 0f;
                }
                Buffer.BlockCopy(_frames, 0, buffer, offset, frames * _outChannels * 4);
                return frames * _outChannels * 4;
            }
        }

        // ─────────────────────────────────────────────
        //  Public surface
        // ─────────────────────────────────────────────

        /// <summary>The macro mixers a slot's sounds should play into — one
        /// per active controller sink on that slot. Pure state read: all
        /// transport I/O happens on the worker thread, never on the caller
        /// (engine / UI). <paramref name="pendingActivation"/> is true when
        /// the slot has eligible speaker pads whose sinks aren't live yet —
        /// the caller should drop the sound (not leak it to the PC speakers);
        /// the worker is signalled and the next trigger lands on the pad.</summary>
        public static List<MixingSampleProvider> GetSlotSinkMixers(int slot, out bool pendingActivation)
        {
            bool anyEligible = false;
            foreach (var (_, ud) in EnumerateAssignedSonyPads(slot))
            {
                bool isBt = (ud.DevicePath ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isDs4 = Ds4Pids.Contains((ushort)ud.ProdId);
                if (isBt && isDs4) continue; // no BT transport for DS4
                anyEligible = true;
                break;
            }

            List<MixingSampleProvider> live;
            lock (_lock)
            {
                EnsureThreads_NoLock();
                // Sticky: this slot's macros want controller routing; the
                // worker builds (and keeps) its sinks from now on.
                _macroDemand.Add(slot);
                live = _sinks.Values
                    .Where(s => s.Slot == slot && SinkAlive(s))
                    .Select(s => s.MacroMixer)
                    .ToList();
            }
            pendingActivation = anyEligible && live.Count == 0;
            if (pendingActivation) _workSignal.Set();
            return live;
        }

        /// <summary>True while the device has an active sink — the DS5
        /// dispatcher asserts the firmware speaker output path + volume.</summary>
        public static bool WantsSpeakerPath(Guid deviceGuid)
        {
            lock (_lock)
                return _sinks.TryGetValue(deviceGuid, out var s) && SinkAlive(s);
        }

        /// <summary>One-shot per device after its sink is torn down, so the
        /// dispatcher restores the firmware headphone path once.</summary>
        public static bool TryConsumeSpeakerPathCleared(Guid deviceGuid)
        {
            lock (_lock)
                return _speakerPathCleared.Remove(deviceGuid);
        }

        /// <summary>Requests a sink reconcile and returns immediately. Call
        /// on device assignment changes and passthrough toggle changes —
        /// safe from the UI thread: the worker does all device I/O. The
        /// worker also self-wakes every 5 s (DSY-v2's Validate cadence) to
        /// ride out hot-plugs and default-device changes.</summary>
        public static void Reconcile()
        {
            lock (_lock) EnsureThreads_NoLock();
            _workSignal.Set();
        }

        /// <summary>Engine shutdown. Detaches all state under the lock, then
        /// disposes the transports outside it; the streaming threads observe
        /// <see cref="_running"/> and exit. Restartable: the next
        /// Reconcile / GetSlotSinkMixers call brings the threads back.</summary>
        public static void Shutdown()
        {
            List<Sink> drop;
            WasapiLoopbackCapture cap;
            lock (_lock)
            {
                _running = false;
                drop = _sinks.Values.ToList();
                _sinks.Clear();
                foreach (var s in drop)
                    if (SinkAlive(s)) _speakerPathCleared.Add(s.DeviceGuid);
                cap = _capture;
                _capture = null;
                _captureDeviceId = "";
            }
            _workSignal.Set();
            foreach (var s in drop) DisposeTransport(s);
            if (cap != null)
            {
                try { cap.StopRecording(); } catch { }
                try { cap.Dispose(); } catch { }
            }
        }

        // ─────────────────────────────────────────────
        //  Sink lifecycle
        // ─────────────────────────────────────────────

        private static bool SinkAlive(Sink s) => s.Player != null || s.BtHandle != new IntPtr(-1);

        // Slots whose macro sounds have requested controller routing; sticky
        // so sinks persist across reconnects like the mirror toggle does.
        private static readonly HashSet<int> _macroDemand = new();

        /// <summary>Worker-only reconcile pass. The phases keep all device
        /// I/O outside <see cref="_lock"/> — a BT CreateFile on a sleeping
        /// pad can block for seconds, and with the old in-lock layout that
        /// froze the UI (Reconcile ran on the UI thread), the engine (macro
        /// placement) and the effects dispatcher (per-report
        /// WantsSpeakerPath) all at once.</summary>
        private static void ReconcileOnWorker()
        {
            // Phase 1 — desired state, no locks (SettingsManager and the
            // config provider take their own locks; never nest them under
            // ours).
            var desired = new List<(int Slot, Guid Guid, string Path, bool IsBt, bool IsDs4, bool PtOn)>();
            for (int slot = 0; slot < MaxPads; slot++)
            {
                bool demand;
                lock (_lock) demand = _macroDemand.Contains(slot);
                foreach (var (guid, ud) in EnumerateAssignedSonyPads(slot))
                {
                    bool isBt = (ud.DevicePath ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDs4 = Ds4Pids.Contains((ushort)ud.ProdId);
                    // The 0x32 raw audio stream is DualSense-only (SAxense);
                    // DS4 BT audio is SBC-coded over different reports and
                    // unimplemented.
                    if (isBt && isDs4) continue;
                    bool ptOn = ReadPassthroughFlag(slot, guid);
                    // A sink exists while the device's mirror toggle is on or
                    // the slot's macros have asked for controller routing.
                    // Pads using neither get no transport and no firmware
                    // speaker-path assertion.
                    if (!ptOn && !demand) continue;
                    desired.Add((slot, guid, ud.DevicePath, isBt, isDs4, ptOn));
                }
            }

            // Phase 2 — state sync under the lock; no I/O.
            var toDispose = new List<Sink>();
            var toBuild = new List<Sink>();
            lock (_lock)
            {
                if (!_running) return;
                var wanted = new HashSet<Guid>();
                foreach (var d in desired)
                {
                    wanted.Add(d.Guid);
                    if (!_sinks.TryGetValue(d.Guid, out var sink))
                    {
                        sink = new Sink
                        {
                            DeviceGuid = d.Guid,
                            IsBt = d.IsBt,
                            IsDs4 = d.IsDs4,
                            MacroMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { ReadFully = true },
                        };
                        sink.Source = new SinkSource(sink);
                        _sinks[d.Guid] = sink;
                    }
                    sink.Slot = d.Slot;
                    sink.HidPath = d.Path;
                    sink.PassthroughOn = d.PtOn;
                    if (sink.TransportFailed)
                    {
                        toDispose.Add(DetachTransport_NoLock(sink));
                        sink.TransportFailed = false;
                    }
                    if (!SinkAlive(sink)) toBuild.Add(sink);
                }
                foreach (var kv in _sinks.ToList())
                {
                    if (wanted.Contains(kv.Key)) continue;
                    toDispose.Add(DetachTransport_NoLock(kv.Value));
                    _sinks.Remove(kv.Key);
                }
            }

            // Phase 3 — device I/O, unlocked.
            foreach (var s in toDispose) DisposeTransport(s);
            foreach (var s in toBuild) BuildTransportOnWorker(s);

            // Phase 4 — loopback capture (own brief locks).
            ReconcileCaptureOnWorker();

            // Phase 5 — macro-routing notify, outside _lock: it takes
            // SoundMacroService's lock and can tear down a WasapiOut.
            var routed = new bool[MaxPads];
            lock (_lock)
            {
                foreach (var s in _sinks.Values)
                    if (SinkAlive(s) && (uint)s.Slot < MaxPads) routed[s.Slot] = true;
            }
            for (int slot = 0; slot < MaxPads; slot++)
                SoundMacroService.SetSlotControllerRouted(slot, routed[slot]);
        }

        /// <summary>Move a sink's transport onto a carrier so it can be
        /// disposed outside the lock; flags the headphone-path restore.</summary>
        private static Sink DetachTransport_NoLock(Sink s)
        {
            if (SinkAlive(s)) _speakerPathCleared.Add(s.DeviceGuid);
            var carrier = new Sink
            {
                DeviceGuid = s.DeviceGuid,
                Player = s.Player,
                BtHandle = s.BtHandle,
                BtEvent = s.BtEvent,
            };
            s.Player = null;
            s.BtHandle = new IntPtr(-1);
            s.BtEvent = IntPtr.Zero;
            s.OpusEncoder = null; // rebuilt sinks start with a fresh encoder
            return carrier;
        }

        private static void DisposeTransport(Sink s)
        {
            try { s.Player?.Stop(); } catch { }
            try { s.Player?.Dispose(); } catch { }
            s.Player = null;
            if (s.BtHandle != new IntPtr(-1))
            {
                NativeMethods.CloseHandle(s.BtHandle);
                s.BtHandle = new IntPtr(-1);
            }
            if (s.BtEvent != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(s.BtEvent);
                s.BtEvent = IntPtr.Zero;
            }
        }

        /// <summary>Worker-only: open the transport with no locks held, then
        /// commit under the lock only if the sink is still the wanted one.
        /// A losing build (device unassigned mid-open) is disposed as an
        /// orphan.</summary>
        private static void BuildTransportOnWorker(Sink s)
        {
            if (s.IsBt)
            {
                // Persistent raw HID handle for the 94 Hz audio frame stream.
                IntPtr h = NativeMethods.OpenHid(s.HidPath);
                if (h == new IntPtr(-1))
                {
                    Diag($"[SINK-FAIL] BT open failed slot={s.Slot} path={s.HidPath}");
                    return;
                }
                IntPtr ev = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
                lock (_lock)
                {
                    if (_running && _sinks.TryGetValue(s.DeviceGuid, out var cur)
                        && ReferenceEquals(cur, s) && !SinkAlive(s))
                    {
                        s.BtHandle = h;
                        s.BtEvent = ev;
                        Diag($"[SINK] BT stream open slot={s.Slot} dev={s.DeviceGuid.ToString().Substring(0, 8)}");
                        return;
                    }
                }
                NativeMethods.CloseHandle(h);
                if (ev != IntPtr.Zero) NativeMethods.CloseHandle(ev);
                return;
            }

            // USB: find the UAC endpoint with the same Container ID as the HID.
            try
            {
                Guid container = NativeMethods.GetContainerIdForDevicePath(s.HidPath);
                if (container == Guid.Empty)
                {
                    Diag($"[SINK-FAIL] no container id for {s.HidPath}");
                    return;
                }
                using var en = new MMDeviceEnumerator();
                MMDevice match = FindActiveEndpointByContainer(en, container);
                if (match == null)
                {
                    // The pad's endpoint often exists but sits disabled in
                    // Windows sound settings (a common DualSense setup, done
                    // to keep games off the pad's audio device). Audio was
                    // explicitly routed at this pad, so enable the endpoint
                    // (IPolicyConfig::SetEndpointVisibility — one-way, never
                    // auto-disabled back) and retry.
                    string disabledId = null;
                    foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Disabled))
                    {
                        using (dev)
                        {
                            if (GetEndpointContainerId(dev) == container) { disabledId = dev.ID; break; }
                        }
                    }
                    string prevDefault = null;
                    try { using var dd = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); prevDefault = dd.ID; } catch { }
                    if (disabledId != null && NativeMethods.TryEnableEndpoint(disabledId))
                    {
                        Diag($"[SINK] enabled disabled endpoint {disabledId} (slot={s.Slot})");
                        Thread.Sleep(250); // endpoint activation isn't instant
                        // Windows can promote a newly enabled endpoint to the
                        // default output; the user's default shouldn't move
                        // because PadForge needed the pad's endpoint.
                        try
                        {
                            using var dd = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                            if (prevDefault != null && prevDefault != disabledId && dd.ID == disabledId)
                            {
                                NativeMethods.TrySetDefaultEndpoint(prevDefault);
                                Diag($"[SINK] restored previous default output");
                            }
                        }
                        catch { }
                        match = FindActiveEndpointByContainer(en, container);
                    }
                }
                if (match == null)
                {
                    Diag($"[SINK-FAIL] no UAC endpoint matches container {container} (slot={s.Slot})");
                    return;
                }
                using (match)
                {
                    s.Container = container;
                    // Endpoint-native format when it's already 48 kHz float
                    // (the DS5 is extensible float 48k 4ch — WASAPI needs the
                    // extensible form with its channel mask beyond stereo);
                    // otherwise plain stereo float and the engine converts.
                    WaveFormat mix = null;
                    try { mix = match.AudioClient.MixFormat; } catch { }
                    bool nativeOk = mix != null && mix.Channels >= 2
                        && mix.SampleRate == Rate && mix.BitsPerSample == 32
                        && (mix.Encoding == WaveFormatEncoding.IeeeFloat
                            || (mix is WaveFormatExtensible wfe
                                && wfe.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
                    var feedFormat = nativeOk ? mix : WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
                    int channels = feedFormat.Channels;
                    var feed = new UsbFrameProvider(s.Source, feedFormat);
                    var player = new WasapiOut(match, AudioClientShareMode.Shared, true, 60);
                    player.Init(feed);
                    player.Play();
                    bool committed = false;
                    lock (_lock)
                    {
                        if (_running && _sinks.TryGetValue(s.DeviceGuid, out var cur)
                            && ReferenceEquals(cur, s) && !SinkAlive(s))
                        {
                            s.Player = player;
                            committed = true;
                        }
                    }
                    if (!committed)
                    {
                        try { player.Stop(); } catch { }
                        try { player.Dispose(); } catch { }
                        return;
                    }
                    Diag($"[SINK] USB open slot={s.Slot} endpoint='{match.FriendlyName}' ch={channels}");
                }
            }
            catch (Exception ex)
            {
                Diag($"[SINK-FAIL] USB {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────
        //  Device / config lookups
        // ─────────────────────────────────────────────

        private static IEnumerable<(Guid Guid, Engine.Data.UserDevice Device)> EnumerateAssignedSonyPads(int slot)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) yield break;
            List<Guid> guids = new();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.MapTo == slot) guids.Add(us.InstanceGuid);
            }
            foreach (var g in guids)
            {
                var ud = FindOnlineSonyDevice(g);
                if (ud != null) yield return (g, ud);
            }
        }

        private static Engine.Data.UserDevice FindOnlineSonyDevice(Guid guid)
        {
            var ud = SettingsManager.FindDeviceByInstanceGuid(guid);
            if (ud == null || !ud.IsOnline || string.IsNullOrEmpty(ud.DevicePath)) return null;
            if (ud.VendorId != SonyVid) return null;
            ushort pid = (ushort)ud.ProdId;
            if (!Ds5Pids.Contains(pid) && !Ds4Pids.Contains(pid)) return null;
            return ud;
        }

        private static bool IsAssignedToSlot(Guid guid, int slot)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && us.InstanceGuid == guid && us.MapTo == slot) return true;
            }
            return false;
        }

        private static bool ReadPassthroughFlag(int slot, Guid device)
        {
            try
            {
                var provider = PassthroughConfigProvider;
                if (provider == null) return false;
                foreach (var (dev, on) in provider(slot) ?? Enumerable.Empty<(Guid, bool)>())
                    if (dev == device) return on;
            }
            catch { }
            return false;
        }

        private static MMDevice FindActiveEndpointByContainer(MMDeviceEnumerator en, Guid container)
        {
            MMDevice match = null;
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    if (GetEndpointContainerId(dev) == container) { match = dev; break; }
                }
                finally { if (!ReferenceEquals(match, dev)) dev.Dispose(); }
            }
            return match;
        }

        private static Guid GetEndpointContainerId(MMDevice dev)
        {
            // PKEY_Device_ContainerId = {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}, 2
            try
            {
                var key = new NAudio.CoreAudioApi.PropertyKey(
                    new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
                var props = dev.Properties;
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    if (p.Key.formatId == key.formatId && p.Key.propertyId == key.propertyId)
                    {
                        var v = p.Value;
                        if (v is Guid g) return g;
                        if (v is byte[] b && b.Length == 16) return new Guid(b);
                    }
                }
            }
            catch { }
            return Guid.Empty;
        }

        // ─────────────────────────────────────────────
        //  BT audio frame stream (DSY-v2 HapticTimerThread port)
        // ─────────────────────────────────────────────

        private const int BtFrameSamples = 480;    // 10 ms @ 48 kHz, per channel
        private const int BtOpusBytes = 200;       // hard-CBR frame size (160 kbps)
        private const int BtReportSize = 334;      // report 0x35 wire size
        private static byte _btPktCounter;         // 0x11 header rolling counter
        private static int _btSeq;                 // report seq tag (byte 1 high nibble)

        private static void EnsureThreads_NoLock()
        {
            if (_running) return;
            _running = true;
            _btThread = new Thread(BtThreadMain) { IsBackground = true, Name = "PadForge.BtAudio", Priority = ThreadPriority.Highest };
            _btThread.Start();
            _workerThread = new Thread(WorkerThreadMain) { IsBackground = true, Name = "PadForge.AudioWorker" };
            _workerThread.Start();
        }

        /// <summary>The single owner of all sink / capture device I/O. Wakes
        /// on <see cref="_workSignal"/> (Reconcile requests) and every 5 s
        /// regardless (DSY-v2's Validate cadence) to ride out hot-plugs and
        /// default-device changes. The thread-identity check lets a
        /// Shutdown → restart cycle replace the thread without two workers
        /// ever running at once.</summary>
        private static void WorkerThreadMain()
        {
            var me = Thread.CurrentThread;
            while (_running && ReferenceEquals(_workerThread, me))
            {
                try { ReconcileOnWorker(); } catch { }
                _workSignal.WaitOne(5000);
            }
        }

        private static void BtThreadMain()
        {
            NativeMethods.timeBeginPeriod(1);
            try
            {
                // One report per 10 ms Opus frame: pull 480 stereo float
                // frames from the sink's 48 kHz source, encode at hard CBR
                // (every frame exactly 200 bytes), wrap in report 0x35 per
                // the awalol HeadsetPlayMusic recipe.
                var pull = new float[BtFrameSamples * 2];
                var opus = new byte[BtOpusBytes + 16];
                var report = new byte[BtReportSize];
                // Exactly one 10 ms frame per 10 ms of wall time: each frame
                // carries 480 samples of 48 kHz audio, so any other cadence
                // drifts against the capture clock. (The old 10.667 ms
                // haptics cadence under-consumed 6%, backlogging the mirror
                // up to the ring's half-second capacity — the source of the
                // 0.5-1 s PC-to-pad delay — and forced the pad's decoder
                // into gap concealment.)
                long periodTicks = 10 * TimeSpan.TicksPerMillisecond;
                long next = DateTime.UtcNow.Ticks + periodTicks;
                var me = Thread.CurrentThread;

                while (_running && ReferenceEquals(_btThread, me))
                {
                    List<Sink> btSinks;
                    lock (_lock)
                        btSinks = _sinks.Values.Where(s => s.IsBt && !s.TransportFailed && s.BtHandle != new IntPtr(-1)).ToList();

                    foreach (var s in btSinks)
                    {
                        // Always pull (keeps the ring cursor at the live edge
                        // and the activity stamp fresh) but pause encode+send
                        // after 2 s of silence so an idle pad's radio and our
                        // CPU rest; the next audible sample resumes within a
                        // frame (one cold-start frame from the encoder).
                        s.Source.Read(pull, 0, pull.Length);
                        if (Environment.TickCount64 - s.LastAudibleTicks > 2000)
                            continue;

                        s.OpusEncoder ??= CreateBtOpusEncoder();
                        int n;
                        try { n = s.OpusEncoder.Encode(pull.AsSpan(), BtFrameSamples, opus.AsSpan(), BtOpusBytes); }
                        catch { s.OpusEncoder = null; continue; }

                        Array.Clear(report, 0, report.Length);
                        report[0] = 0x35;
                        report[1] = (byte)((_btSeq & 0x0F) << 4);
                        _btSeq = (_btSeq + 1) & 0x0F;
                        // packet 0x11: session header (SAxense default — no
                        // handshake needed)
                        report[2] = 0x11 | 0x80;
                        report[3] = 7;
                        report[4] = 0xFE;
                        report[9] = 0xFF;
                        report[10] = _btPktCounter++;
                        // packet 0x13: speaker audio lane (0x16 = headset
                        // jack), one Opus frame filling the slot
                        report[11] = 0x13 | 0x80;
                        report[12] = (byte)BtOpusBytes;
                        Array.Copy(opus, 0, report, 13, Math.Min(n, BtOpusBytes));
                        uint crc = Crc32(report, BtReportSize - 4);
                        report[BtReportSize - 4] = (byte)(crc & 0xFF);
                        report[BtReportSize - 3] = (byte)((crc >> 8) & 0xFF);
                        report[BtReportSize - 2] = (byte)((crc >> 16) & 0xFF);
                        report[BtReportSize - 1] = (byte)(crc >> 24);

                        if (!NativeMethods.WriteHid(s.BtHandle, s.BtEvent, report))
                        {
                            // No transport I/O here — mark and let the worker
                            // detach/rebuild on its 5 s cadence. (Signalling
                            // immediately caused a 94 Hz fail->reopen loop
                            // while a pad was mid-disconnect.)
                            Diag($"[BT-WRITE-FAIL] slot={s.Slot}; deferring to worker");
                            s.TransportFailed = true;
                        }
                    }

                    long now = DateTime.UtcNow.Ticks;
                    int sleepMs = (int)Math.Max(0, (next - now) / TimeSpan.TicksPerMillisecond);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                    next += periodTicks;
                    if (now > next + 10 * periodTicks) next = now + periodTicks; // resync after stall
                }
            }
            finally
            {
                NativeMethods.timeEndPeriod(1);
            }
        }

        /// <summary>48 kHz stereo, 10 ms frames, hard CBR at 160 kbps so
        /// every frame is exactly <see cref="BtOpusBytes"/> bytes — the
        /// firmware expects the frame to fill the 0x13 packet slot.</summary>
        private static IOpusEncoder CreateBtOpusEncoder()
        {
            var enc = OpusCodecFactory.CreateEncoder(Rate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
            enc.Bitrate = BtOpusBytes * 8 * 100;
            enc.UseVBR = false;
            return enc;
        }

        /// <summary>Reflected CRC32 over the first <paramref name="length"/> bytes,
        /// pre-seeded with the 0xA2 BT output-report prefix: the firmware checks
        /// CRC32({0xA2} + report bytes), like every Sony BT output report. The init
        /// constant is the CRC state after hashing 0xA2 — the reference's
        /// `crc = ~0xEADA2D49; // 0xA2 seed` (audioPassthrough.cpp).</summary>
        private static uint Crc32(byte[] data, int length)
        {
            uint crc = 0x1525D2B6; // == ~0xEADA2D49
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(crc & 1)));
            }
            return ~crc;
        }

        // ─────────────────────────────────────────────
        //  Native interop
        // ─────────────────────────────────────────────

        private static class NativeMethods
        {
            // ── IPolicyConfig (undocumented mmsys policy store; the same
            //    interface SoundSwitch / AudioEndPointLibrary use). Only
            //    SetEndpointVisibility is called; the earlier methods are
            //    slot placeholders — vtable ORDER is the contract
            //    (AudioEndPointLibrary PolicyConfig.h, IPolicyConfig
            //    {f8679f50-850a-41cf-9c72-430f290290c8}). ──
            [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IPolicyConfig
            {
                int GetMixFormat(IntPtr a, IntPtr b);
                int GetDeviceFormat(IntPtr a, int b, IntPtr c);
                int ResetDeviceFormat(IntPtr a);
                int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
                int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
                int SetProcessingPeriod(IntPtr a, IntPtr b);
                int GetShareMode(IntPtr a, IntPtr b);
                int SetShareMode(IntPtr a, IntPtr b);
                int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
                int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
                int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
                int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
            }

            [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
            private class PolicyConfigClient { }

            /// <summary>Re-enables an endpoint disabled in Windows sound
            /// settings. Returns true when the policy store accepted it.</summary>
            public static bool TryEnableEndpoint(string endpointId)
            {
                try
                {
                    var pc = (IPolicyConfig)new PolicyConfigClient();
                    try { return pc.SetEndpointVisibility(endpointId, 1) >= 0; }
                    finally { Marshal.ReleaseComObject(pc); }
                }
                catch { return false; }
            }

            /// <summary>Restores the default render endpoint for all three
            /// roles (Console / Multimedia / Communications).</summary>
            public static bool TrySetDefaultEndpoint(string endpointId)
            {
                try
                {
                    var pc = (IPolicyConfig)new PolicyConfigClient();
                    try
                    {
                        bool ok = true;
                        for (int role = 0; role <= 2; role++)
                            ok &= pc.SetDefaultEndpoint(endpointId, role) >= 0;
                        return ok;
                    }
                    finally { Marshal.ReleaseComObject(pc); }
                }
                catch { return false; }
            }

            [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
            [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initial, string name);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

            [StructLayout(LayoutKind.Sequential)]
            private struct OVERLAPPED
            {
                public IntPtr Internal, InternalHigh;
                public uint OffsetLow, OffsetHigh;
                public IntPtr hEvent;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool WriteFile(IntPtr h, byte[] buf, uint len, out uint written, ref OVERLAPPED o);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetOverlappedResult(IntPtr h, ref OVERLAPPED o, out uint written, bool wait);
            [DllImport("kernel32.dll")]
            private static extern uint WaitForSingleObject(IntPtr h, uint ms);

            public static IntPtr OpenHid(string path)
            {
                return CreateFileW(path,
                    0x40000000u | 0x80000000u,        // GENERIC_WRITE | GENERIC_READ
                    0x1u | 0x2u,                      // share read/write
                    IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0x40000000u /*FILE_FLAG_OVERLAPPED*/, IntPtr.Zero);
            }

            public static bool WriteHid(IntPtr h, IntPtr ev, byte[] report)
            {
                var o = new OVERLAPPED { hEvent = ev };
                if (!WriteFile(h, report, (uint)report.Length, out _, ref o))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 997 /*ERROR_IO_PENDING*/) return false;
                    if (WaitForSingleObject(ev, 100) != 0) return false;
                    if (!GetOverlappedResult(h, ref o, out _, false)) return false;
                }
                return true;
            }

            // ── Container ID from a HID device interface path (duaLib port) ──

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_Device_Interface_PropertyW(
                string pszDeviceInterface, ref DEVPROPKEY propertyKey,
                out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            private static extern int CM_Get_DevNode_PropertyW(
                uint devInst, ref DEVPROPKEY propertyKey,
                out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);

            [StructLayout(LayoutKind.Sequential)]
            private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

            public static Guid GetContainerIdForDevicePath(string interfacePath)
            {
                try
                {
                    // DEVPKEY_Device_InstanceId = {78C34FC8-104A-4ACA-9EA4-524D52996E57}, 256
                    var keyInstanceId = new DEVPROPKEY
                    { fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256 };
                    var buf = new byte[1024];
                    uint size = (uint)buf.Length;
                    if (CM_Get_Device_Interface_PropertyW(interfacePath, ref keyInstanceId,
                            out _, buf, ref size, 0) != 0)
                        return Guid.Empty;
                    string instanceId = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
                    if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
                        return Guid.Empty;

                    // Walk up to the device container root if needed — the
                    // ContainerId property exists on every node of the
                    // container, so the direct node suffices.
                    // DEVPKEY_Device_ContainerId = {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}, 2
                    var keyContainer = new DEVPROPKEY
                    { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };
                    var guidBuf = new byte[16];
                    uint gSize = 16;
                    if (CM_Get_DevNode_PropertyW(devInst, ref keyContainer,
                            out _, guidBuf, ref gSize, 0) != 0 || gSize != 16)
                        return Guid.Empty;
                    return new Guid(guidBuf);
                }
                catch
                {
                    return Guid.Empty;
                }
            }
        }
    }
}
