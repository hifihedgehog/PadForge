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

        // ─────────────────────────────────────────────
        //  App wiring
        // ─────────────────────────────────────────────

        /// <summary>Per-slot per-device passthrough config, wired by
        /// InputService against the live PlayStationSlotConfig dictionaries:
        /// returns (deviceGuid, passthroughEnabled, mirrorSourceEndpointId)
        /// for a slot. An empty source means the system default device.</summary>
        public static Func<int, IEnumerable<(Guid Device, bool PassthroughOn, string MirrorSource)>> PassthroughConfigProvider { get; set; }

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

            /// <summary>Endpoint ID this sink's mirror captures; "" = the
            /// system default device (re-resolved every worker pass).</summary>
            public string MirrorSourceId = "";

            /// <summary>The resolved capture feeding this sink's mirror, or
            /// null (mirror off / source unavailable / own endpoint).</summary>
            public CaptureEntry Capture;

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

            // ── DualSense BT lane (Opus over report 0x35) — BT thread only.
            /// <summary>Per-sink Opus encoder for the DS5 BT speaker stream
            /// (created and used only on the BT thread).</summary>
            public IOpusEncoder Ds5OpusEncoder;

            /// <summary>BT idle-gate state (BT thread only).</summary>
            public bool BtStreaming;

            // ── DualShock 4 BT lane (SBC over report 0x17) — BT thread only.
            /// <summary>Clean-room SBC encoder (32 kHz JS/SNR/bitpool 48).</summary>
            public Ds4SbcEncoder Ds4Sbc;
            /// <summary>Resampled 32 kHz s16 interleaved samples awaiting a
            /// full 256-sample encode block.</summary>
            public short[] Ds4Pending;
            public int Ds4PendingCount;
            /// <summary>48→32 kHz linear-resampler phase carry, in input
            /// samples, persisted across ticks so the 3:2 ratio stays exact.</summary>
            public double Ds4ResamplePhase;
            /// <summary>Last input sample pair of the previous tick — the
            /// left interpolation endpoint for output positions that land in
            /// the tick-boundary gap, so the resampler is continuous across
            /// ticks (libsamplerate's stream API gives the reference
            /// implementation this for free).</summary>
            public float Ds4CarryL, Ds4CarryR;
            /// <summary>Encoded 109-byte SBC frames awaiting a 4-frame 0x17
            /// report.</summary>
            public System.Collections.Generic.Queue<byte[]> Ds4Frames;
            /// <summary>16-bit frame counter at report bytes [3..4]; advances
            /// by frames-per-report (DS4AudioStreamer HidAudioRouterWorker).</summary>
            public ushort Ds4FrameCounter;

            // USB
            public IWavePlayer Player;

            // BT
            public IntPtr BtHandle = new IntPtr(-1);
            /// <summary>Fire-and-forget overlapped write pool (created with
            /// the transport on the worker; used on the BT thread).</summary>
            public BtWritePool Tx;
        }

        /// <summary>Non-blocking overlapped HID writes. Each send queues into
        /// the kernel's HID IRP queue and returns immediately — the field
        /// telemetry showed single blocking writes taking a full 16 ms under
        /// link backpressure, stalling the stream loop and starving the ring.
        /// The kernel queue is the jitter buffer; we never wait on the radio.
        /// Eight slots ≈ 80 ms in flight; when all are busy the link is
        /// saturated and the frame is skipped (the stall clamp re-primes).</summary>
        internal sealed class BtWritePool : IDisposable
        {
            private const int Slots = 8;
            private const int OverlappedSize = 32; // x64 OVERLAPPED
            private readonly byte[][] _buf = new byte[Slots][];
            private readonly GCHandle[] _pin = new GCHandle[Slots];
            private readonly IntPtr[] _ev = new IntPtr[Slots];
            private readonly IntPtr[] _ol = new IntPtr[Slots];
            private int _next;

            // TrySend runs on the BT thread, Dispose on the worker: a send
            // already past the sink's Tx null-check could otherwise touch
            // freed events / native OVERLAPPED memory. Uncontended in normal
            // operation, so the cost at ~94 Hz is nanoseconds.
            private readonly object _gate = new();
            private bool _disposed;

            public BtWritePool(int reportSize)
            {
                for (int i = 0; i < Slots; i++)
                {
                    _buf[i] = new byte[reportSize];
                    _pin[i] = GCHandle.Alloc(_buf[i], GCHandleType.Pinned);
                    _ev[i] = NativeMethods.CreateEventW(IntPtr.Zero, true, true, null); // signaled = free
                    _ol[i] = Marshal.AllocHGlobal(OverlappedSize);
                }
            }

            /// <summary>Queues one report. Returns false with
            /// <paramref name="hardFail"/> false when the pool is saturated
            /// (skip the frame), true on an I/O error (tear down).</summary>
            public bool TrySend(IntPtr handle, byte[] report, out bool hardFail)
            {
                hardFail = false;
                lock (_gate)
                {
                    if (_disposed) { hardFail = true; return false; }
                    int s = _next;
                    if (NativeMethods.WaitForSingleObject(_ev[s], 0) != 0)
                        return false; // oldest write still in flight — saturated

                    // Write exactly the report's length — the DS4 lane sends
                    // both 462-byte 0x17 and 270-byte 0x14 reports through a
                    // pool sized for the larger one.
                    int len = Math.Min(report.Length, _buf[s].Length);
                    Buffer.BlockCopy(report, 0, _buf[s], 0, len);
                    NativeMethods.ResetEvent(_ev[s]);
                    for (int o = 0; o < OverlappedSize - 8; o += 8)
                        Marshal.WriteInt64(_ol[s], o, 0);
                    Marshal.WriteIntPtr(_ol[s], 24, _ev[s]);

                    if (!NativeMethods.WriteFileRaw(handle, _pin[s].AddrOfPinnedObject(),
                            (uint)len, IntPtr.Zero, _ol[s]))
                    {
                        if (Marshal.GetLastWin32Error() != 997 /*ERROR_IO_PENDING*/)
                        {
                            NativeMethods.SetEvent(_ev[s]); // slot stays free
                            hardFail = true;
                            return false;
                        }
                    }
                    _next = (s + 1) % Slots;
                    return true;
                }
            }

            /// <summary>Caller must CancelIo the handle first.</summary>
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    for (int i = 0; i < Slots; i++)
                    {
                        if (_ev[i] != IntPtr.Zero)
                        {
                            NativeMethods.WaitForSingleObject(_ev[i], 100);
                            NativeMethods.CloseHandle(_ev[i]);
                            _ev[i] = IntPtr.Zero;
                        }
                        if (_ol[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_ol[i]); _ol[i] = IntPtr.Zero; }
                        if (_pin[i].IsAllocated) _pin[i].Free();
                    }
                }
            }
        }

        private static readonly Dictionary<Guid, Sink> _sinks = new();
        private static Thread _btThread;
        private static Thread _workerThread;
        private static readonly AutoResetEvent _workSignal = new(false);
        private static volatile bool _running;

        // Speaker-path bookkeeping for the DS5 dispatcher.
        private static readonly HashSet<Guid> _speakerPathCleared = new();

        // ─────────────────────────────────────────────
        //  Loopback captures (one per mirror source, per-sink cursors)
        // ─────────────────────────────────────────────

        private const int RingFrames = Rate / 2; // 0.5 s of 48k stereo

        /// <summary>One running loopback capture and its ring. A sink's
        /// mirror source resolves to one of these; several pads can share
        /// one. The ring array doubles as its own lock object.</summary>
        internal sealed class CaptureEntry
        {
            public string EndpointId;
            public Guid Container;
            public WasapiLoopbackCapture Cap;
            public volatile bool Dead;   // RecordingStopped fired; worker recreates
            public readonly float[] Ring = new float[RingFrames * 2];
            public long Write;           // total frames written (monotonic), under lock(Ring)
        }

        private static readonly Dictionary<string, CaptureEntry> _captures = new(StringComparer.Ordinal);

        /// <summary>Worker-only. Maintains one capture per distinct mirror
        /// source among passthrough-enabled sinks. A sink's MirrorSourceId of
        /// "" means "the system default device", re-resolved every pass so a
        /// default change follows automatically (DSY-v2's validate cadence).
        /// Brief locks around state; all COM and capture start/stop happens
        /// unlocked so no other thread ever waits on device I/O.</summary>
        private static void ReconcileCapturesOnWorker()
        {
            List<Sink> mirrors;
            lock (_lock)
                mirrors = _running ? _sinks.Values.Where(s => s.PassthroughOn).ToList() : new List<Sink>();

            string defaultId = "";
            try
            {
                using var en0 = new MMDeviceEnumerator();
                using var dd = en0.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = dd.ID;
            }
            catch { }

            var wantedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in mirrors)
            {
                string id = string.IsNullOrEmpty(s.MirrorSourceId) ? defaultId : s.MirrorSourceId;
                if (!string.IsNullOrEmpty(id)) wantedIds.Add(id);
            }

            // Detach unwanted / dead captures under the lock, dispose outside.
            var drop = new List<CaptureEntry>();
            lock (_lock)
            {
                foreach (var kv in _captures.ToList())
                {
                    if (wantedIds.Contains(kv.Key) && !kv.Value.Dead) continue;
                    drop.Add(kv.Value);
                    _captures.Remove(kv.Key);
                }
            }
            foreach (var e in drop) StopCaptureEntry(e);

            // Start missing captures (COM + StartRecording, unlocked).
            foreach (var id in wantedIds)
            {
                bool have;
                lock (_lock) have = _captures.ContainsKey(id);
                if (have) continue;
                var entry = StartCaptureEntry(id);
                if (entry == null) continue;
                bool committed = false;
                lock (_lock)
                {
                    if (_running && !_captures.ContainsKey(id)) { _captures[id] = entry; committed = true; }
                }
                if (!committed) StopCaptureEntry(entry);
            }

            // Point each mirror sink at its entry — except a pad's own
            // endpoint, which must never feed back into itself.
            lock (_lock)
            {
                foreach (var s in _sinks.Values)
                {
                    if (!s.PassthroughOn) { s.Capture = null; continue; }
                    string id = string.IsNullOrEmpty(s.MirrorSourceId) ? defaultId : s.MirrorSourceId;
                    _captures.TryGetValue(id, out var entry);
                    if (entry != null && entry.Container != Guid.Empty && entry.Container == s.Container)
                    {
                        entry = null;
                    }
                    s.Capture = entry;
                }
            }
        }

        private static CaptureEntry StartCaptureEntry(string endpointId)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDevice(endpointId);
                if (dev.State != DeviceState.Active)
                {
                    return null;
                }
                var entry = new CaptureEntry
                {
                    EndpointId = endpointId,
                    Container = GetEndpointContainerId(dev),
                };
                var cap = new WasapiLoopbackCapture(dev);
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
                    lock (entry.Ring)
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
                            long idx = (entry.Write % RingFrames) * 2;
                            entry.Ring[idx] = l;
                            entry.Ring[idx + 1] = r;
                            entry.Write++;
                        }
                        pos -= srcFrames;
                    }
                };
                cap.RecordingStopped += (s, e) => entry.Dead = true; // worker recreates
                cap.StartRecording();
                entry.Cap = cap;
                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static void StopCaptureEntry(CaptureEntry e)
        {
            try { e.Cap?.StopRecording(); } catch { }
            try { e.Cap?.Dispose(); } catch { }
            e.Cap = null;
        }

        /// <summary>Per-sink source: the sink's own macro mixer plus (when
        /// the passthrough toggle is on) this sink's cursor over the shared
        /// loopback ring. 48 kHz stereo float.</summary>
        private sealed class SinkSource : ISampleProvider
        {
            private readonly Sink _sink;
            private long _cursor = -1;
            private CaptureEntry _lastCap; // cursor resets when the source swaps

            /// <summary>Frames the cursor trails the live edge after the last
            /// read, or -1 when the mirror is off. The BT thread reads this to
            /// rate-match its send cadence to the capture clock.</summary>
            public int LoopbackLagFrames = -1;

            public SinkSource(Sink sink) { _sink = sink; }

            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

            public int Read(float[] buffer, int offset, int count)
            {
                // Macro mixer (ReadFully → zeros when idle).
                int read = _sink.MacroMixer.Read(buffer, offset, count);
                for (int i = read; i < count; i++) buffer[offset + i] = 0f;

                var cap = _sink.Capture;
                if (_sink.PassthroughOn && cap != null)
                {
                    // Full scale: master volume lives in the firmware speaker
                    // volume byte (UserEffectsDispatcher), not the samples.
                    // The cursor's distance behind the live edge is steered to
                    // a small steady cushion by the BT thread's adaptive
                    // pacing, NOT by jumping here. A hard jump drops audio, so
                    // this only resyncs on a genuine stall (cursor invalid, or
                    // ran past the live edge, or fell a quarter-second behind).
                    const int CatastropheLag = 12000; // 250 ms @ 48 kHz
                    const int ResyncCushion = 960;     // 20 ms (== BtTargetLag)
                    lock (cap.Ring)
                    {
                        long avail = cap.Write;
                        if (!ReferenceEquals(cap, _lastCap)) { _cursor = -1; _lastCap = cap; }
                        if (_cursor < 0 || _cursor > avail || avail - _cursor > CatastropheLag)
                            _cursor = Math.Max(0, avail - ResyncCushion);
                        int frames = count / 2;
                        int canRead = (int)Math.Min(frames, avail - _cursor);
                        for (int f = 0; f < canRead; f++)
                        {
                            long idx = ((_cursor + f) % RingFrames) * 2;
                            buffer[offset + f * 2] += cap.Ring[idx];
                            buffer[offset + f * 2 + 1] += cap.Ring[idx + 1];
                        }
                        _cursor += canRead;
                        LoopbackLagFrames = (int)(avail - _cursor);
                    }
                }
                else LoopbackLagFrames = -1;

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
                // Wired DS4 has no audio interface (BT-only audio); the USB
                // wireless adaptor (0x0BA0) does. See ReconcileOnWorker.
                if (isDs4 && !isBt && (ushort)ud.ProdId != 0x0BA0) continue;
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
            List<CaptureEntry> caps;
            lock (_lock)
            {
                _running = false;
                drop = _sinks.Values.ToList();
                _sinks.Clear();
                foreach (var s in drop)
                    if (SinkAlive(s)) _speakerPathCleared.Add(s.DeviceGuid);
                caps = _captures.Values.ToList();
                _captures.Clear();
            }
            _workSignal.Set();
            foreach (var s in drop) DisposeTransport(s);
            foreach (var c in caps) StopCaptureEntry(c);
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
            var desired = new List<(int Slot, Guid Guid, string Path, bool IsBt, bool IsDs4, bool PtOn, string MirrorSrc)>();
            for (int slot = 0; slot < MaxPads; slot++)
            {
                bool demand;
                lock (_lock) demand = _macroDemand.Contains(slot);
                foreach (var (guid, ud) in EnumerateAssignedSonyPads(slot))
                {
                    bool isBt = (ud.DevicePath ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDs4 = Ds4Pids.Contains((ushort)ud.ProdId);
                    // DS4 audio is BLUETOOTH-ONLY: the wired DS4 exposes no
                    // USB audio interface at all (ds4mac docs §3.1 — HID
                    // endpoints only, no UAC descriptors). BT DS4 streams SBC
                    // over report 0x17. Exception: Sony's USB wireless
                    // adaptor (PID 0x0BA0) tunnels the radio link and exposes
                    // real UAC endpoints, so it keeps the USB container path.
                    if (isDs4 && !isBt && (ushort)ud.ProdId != 0x0BA0) continue;
                    var (ptOn, mirrorSrc) = ReadPassthroughConfig(slot, guid);
                    // A sink exists while the device's mirror toggle is on or
                    // the slot's macros have asked for controller routing.
                    // Pads using neither get no transport and no firmware
                    // speaker-path assertion.
                    if (!ptOn && !demand) continue;
                    desired.Add((slot, guid, ud.DevicePath, isBt, isDs4, ptOn, mirrorSrc));
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
                    sink.MirrorSourceId = d.MirrorSrc ?? "";
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

            // Phase 4 — loopback captures (own brief locks).
            ReconcileCapturesOnWorker();

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
                Tx = s.Tx,
            };
            s.Player = null;
            s.BtHandle = new IntPtr(-1);
            s.Tx = null;
            s.Ds5OpusEncoder = null;   // rebuilt sinks start with a fresh encoder
            s.BtStreaming = false;  // and a fresh stream clock
            s.Ds4Sbc = null;
            s.Ds4Frames = null;
            s.Ds4PendingCount = 0;
            s.Ds4ResamplePhase = 0;
            return carrier;
        }

        private static void DisposeTransport(Sink s)
        {
            try { s.Player?.Stop(); } catch { }
            try { s.Player?.Dispose(); } catch { }
            s.Player = null;
            if (s.BtHandle != new IntPtr(-1))
            {
                // In-flight overlapped writes must be cancelled and their
                // pool drained before the handle and buffers go away.
                NativeMethods.CancelIo(s.BtHandle);
                s.Tx?.Dispose();
                s.Tx = null;
                NativeMethods.CloseHandle(s.BtHandle);
                s.BtHandle = new IntPtr(-1);
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
                // Persistent raw HID handle for the ~100 Hz audio frame stream.
                IntPtr h = NativeMethods.OpenHid(s.HidPath);
                if (h == new IntPtr(-1))
                {
                    return;
                }
                var tx = new BtWritePool(s.IsDs4 ? Ds4BtReportSize : Ds5BtReportSize);

                // DS4: enable the firmware audio path before streaming — one
                // report 0x11 with ONLY the volume-enable bits set (0x10
                // headphone L, 0x20 headphone R, 0x80 speaker — rumble /
                // lightbar bits stay clear so effect state isn't clobbered).
                // Layout per DS4AudioStreamer SendControllerDataReport and
                // DS4Windows DS4Device.cs; CRC32 0xA2-seeded like all Sony
                // BT output reports.
                if (s.IsDs4)
                {
                    var vol = new byte[Ds4ControlReportSize];
                    vol[0] = 0x11;
                    vol[1] = 0xC0;      // EnableCRC | EnableHID
                    vol[2] = 0xA2;      // audio flags (EnableAudio set)
                    vol[3] = 0xB0;      // volume enables only
                    vol[21] = 0x4F;     // headphone L (max 0x4F)
                    vol[22] = 0x4F;     // headphone R
                    vol[24] = 0x4F;     // speaker
                    uint vcrc = Crc32(vol, Ds4ControlReportSize - 4);
                    vol[Ds4ControlReportSize - 4] = (byte)vcrc;
                    vol[Ds4ControlReportSize - 3] = (byte)(vcrc >> 8);
                    vol[Ds4ControlReportSize - 2] = (byte)(vcrc >> 16);
                    vol[Ds4ControlReportSize - 1] = (byte)(vcrc >> 24);
                    WriteOneShot(h, vol);
                }

                lock (_lock)
                {
                    if (_running && _sinks.TryGetValue(s.DeviceGuid, out var cur)
                        && ReferenceEquals(cur, s) && !SinkAlive(s))
                    {
                        s.BtHandle = h;
                        s.Tx = tx;
                        if (s.IsDs4)
                        {
                            s.Ds4Sbc ??= new Ds4SbcEncoder();
                            s.Ds4Pending ??= new short[Ds4SbcEncoder.PcmSamplesPerFrame * 4];
                            s.Ds4Frames ??= new System.Collections.Generic.Queue<byte[]>();
                        }
                        else
                        {
                            // Pre-create the encoder so the stream's first frame
                            // doesn't pay ~15 ms of Concentus construction.
                            s.Ds5OpusEncoder ??= CreateDs5OpusEncoder();
                        }
                        return;
                    }
                }
                NativeMethods.CloseHandle(h);
                tx.Dispose();
                return;
            }

            // USB: find the UAC endpoint with the same Container ID as the HID.
            try
            {
                Guid container = NativeMethods.GetContainerIdForDevicePath(s.HidPath);
                if (container == Guid.Empty)
                {
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
                            }
                        }
                        catch { }
                        match = FindActiveEndpointByContainer(en, container);
                    }
                }
                if (match == null)
                {
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
                    // 30 ms event-driven buffer — on USB this buffer sits in
                    // BOTH the macro and mirror paths, so halving it from 60
                    // tightens everything the pad plays.
                    var player = new WasapiOut(match, AudioClientShareMode.Shared, true, 30);
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
                }
            }
            catch
            {
                // Best-effort: a failed USB sink build retries on the
                // worker's next 5 s pass.
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

        private static (bool On, string MirrorSource) ReadPassthroughConfig(int slot, Guid device)
        {
            try
            {
                var provider = PassthroughConfigProvider;
                if (provider == null) return (false, "");
                foreach (var (dev, on, src) in provider(slot) ?? Enumerable.Empty<(Guid, bool, string)>())
                    if (dev == device) return (on, src ?? "");
            }
            catch { }
            return (false, "");
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
        //  BT audio frame streams — one shared 10.667 ms tick drives two
        //  per-device lanes: DualSense (Opus over report 0x35) and
        //  DualShock 4 (SBC over reports 0x17/0x14). Constants are
        //  prefixed by lane; plain Bt* names are shared infrastructure.
        // ─────────────────────────────────────────────

        // Shared tick: input frames consumed per ~10.667 ms (512 × 93.75 = 48 kHz).
        private const int BtPullFrames = 512;

        // DualSense: one Opus frame per tick in a report 0x35, hard CBR so
        // every frame fills the 0x13 speaker-lane slot exactly.
        private const int Ds5OpusFrameSamples = 480;   // Opus frame samples per channel
        private const int Ds5OpusBytes = 200;          // hard-CBR frame size (160 kbps)
        private const int Ds5BtReportSize = 334;       // report 0x35 wire size
        private static byte _ds5PktCounter;            // 0x11 header rolling counter
        private static int _ds5Seq;                    // report seq tag (byte 1 high nibble)

        // DualShock 4 BT audio: SBC frames over output report 0x17
        // (462 bytes, 4 frames). Layout per DS4AudioStreamer
        // HidAudioRouterWorker._worker and ds4mac's audio documentation:
        // [0]=0x17 [1]=0x40 [2]=0xA2 [3..4]=u16 LE frame counter
        // [5]=audio target (0x02 speaker / 0x24 headset) [6..]=SBC frames
        // [458..461]=CRC32 (0xA2-seeded, same as every Sony BT report).
        private const int Ds4BtReportSize = 462;       // report 0x17, 4 SBC frames
        private const int Ds4SmallReportSize = 270;    // report 0x14, 2 SBC frames
        private const int Ds4ControlReportSize = 78;   // report 0x11
        private const int Ds4FramesPerReport = 4;
        private const int Ds4MinBufferedFrames = 4;    // wake threshold (ds4mac §12.6)
        private const int Ds4FrameQueueCap = 12;       // 48 ms of audio
        private const double Ds4ResampleStep = 1.5;    // 48 kHz → 32 kHz

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
            IntPtr hrTimer = NativeMethods.CreateHighResTimer();
            try
            {
                // The DUALSENSE firmware contract, established by A/B/C music
                // experiment on hardware (issue #83): exactly ONE report per
                // ~10.667 ms tick, never bursts — faster delivery or
                // back-to-back catch-up frames overflow the pad's shallow
                // receive buffer and it drops audio (the cutouts/garble).
                // Each frame is a 480-sample Opus frame, but real time
                // advances 512 samples per tick, so every tick consumes 512
                // input frames and time-compresses them 512→480 (16:15): the
                // source is consumed at exactly 48 kHz and the pitch is
                // exact. (Plain 480-per-tick — the awalol baseline —
                // underfeeds 6 % and plays audibly flat; strict 10.000 ms
                // delivery cuts out.)
                //
                // The DUALSHOCK 4 lane shares only the tick: its cadence is
                // availability-driven with bursts allowed (see Ds4BtTick) —
                // the DS5 one-report-per-tick rule is NOT a DS4 fact.
                var pull = new float[(BtPullFrames + 8) * 2];
                var frame = new float[Ds5OpusFrameSamples * 2];
                var opus = new byte[Ds5OpusBytes + 16];
                var report = new byte[Ds5BtReportSize];
                const double CadenceMs = 10.0 + 2.0 / 3.0;
                // 20 ms cushion: just enough to absorb WASAPI loopback's
                // ~10 ms bursty delivery, bringing the mirror within ~15 ms
                // of the macro path (owner request 2026-06-12). The original
                // 45 ms was chosen mid-dropout-war, before the async write
                // pool / high-res timer / skip-not-burst fixes removed the
                // sender-side jitter it was also covering for.
                const int BtTargetLag = 960;          // 20 ms ring cushion @ 48 kHz
                const int LagDeadband = 240;          // ±5 ms before trimming
                long cadTicks = (long)(CadenceMs * TimeSpan.TicksPerMillisecond);
                long next = DateTime.UtcNow.Ticks + cadTicks;
                var me = Thread.CurrentThread;

                while (_running && ReferenceEquals(_btThread, me))
                {
                    List<Sink> btSinks;
                    lock (_lock)
                        btSinks = _sinks.Values.Where(s => s.IsBt && !s.TransportFailed && s.BtHandle != new IntPtr(-1)).ToList();

                    foreach (var s in btSinks)
                    {
                        // Last-resort guard (same role as the DS5 passthrough
                        // dispatcher's DispatchLoopAsync catch): the sink list
                        // is a lock-free snapshot, so the worker can detach a
                        // sink (nulling Tx / Ds4Frames / encoders under _lock)
                        // mid-tick. Without this catch, that race throws on
                        // the BT thread and kills ALL BT audio until restart.
                        // One sink's bad tick now just flags it for rebuild.
                        try
                        {
                            // Mirror drift trim: the ring is steered to its target
                            // cushion by consuming a few samples more or fewer per
                            // tick (±0.8 % momentary rate trim through the same
                            // 16:15 compressor — inaudible), never by extra or
                            // skipped reports.
                            int inFrames = BtPullFrames;
                            int lag = s.Source.LoopbackLagFrames;
                            if (lag >= 0)
                            {
                                if (lag > BtTargetLag + LagDeadband) inFrames += 4;
                                else if (lag < BtTargetLag - LagDeadband) inFrames -= 4;
                            }

                            s.Source.Read(pull, 0, inFrames * 2);

                            // Idle gate: after 2 s of silence stop sending so the
                            // pad's radio and our CPU rest; the read above keeps
                            // the ring cursor live and the activity stamp fresh.
                            bool audible = Environment.TickCount64 - s.LastAudibleTicks <= 2000;
                            s.BtStreaming = audible;
                            if (!audible) continue;

                            if (s.IsDs4)
                            {
                                Ds4BtTick(s, pull, inFrames);
                                continue;
                            }

                            // 512→480 linear time-compression (pitch-exact).
                            double step = inFrames / (double)Ds5OpusFrameSamples;
                            for (int o = 0; o < Ds5OpusFrameSamples; o++)
                            {
                                double pos = o * step;
                                int i0 = (int)pos;
                                double t = pos - i0;
                                int i1 = Math.Min(i0 + 1, inFrames - 1);
                                frame[o * 2] = (float)(pull[i0 * 2] * (1 - t) + pull[i1 * 2] * t);
                                frame[o * 2 + 1] = (float)(pull[i0 * 2 + 1] * (1 - t) + pull[i1 * 2 + 1] * t);
                            }
                            SendDs5BtFrame(s, frame, opus, report);
                        }
                        catch
                        {
                            s.TransportFailed = true;
                        }
                    }

                    // One tick per cadence on an absolute schedule. Lateness
                    // is never repaid: a missed slot is skipped (schedule
                    // re-snaps), because catch-up frames are back-to-back
                    // deliveries — the exact burst the firmware drops — and
                    // each one also drains 512 ring frames with no real time
                    // passing. The pad conceals a missing frame gracefully.
                    long nowTicks = DateTime.UtcNow.Ticks;
                    double waitMs = (next - nowTicks) / (double)TimeSpan.TicksPerMillisecond;
                    if (waitMs > 0)
                    {
                        if (hrTimer != IntPtr.Zero) NativeMethods.HighResWait(hrTimer, waitMs);
                        else Thread.Sleep((int)Math.Max(1, waitMs));
                        next += cadTicks;
                    }
                    else
                    {
                        next = nowTicks + cadTicks;
                    }
                }
            }
            finally
            {
                if (hrTimer != IntPtr.Zero) NativeMethods.CloseHandle(hrTimer);
                NativeMethods.timeEndPeriod(1);
            }
        }

        /// <summary>Encode one 10 ms frame from <paramref name="pull"/> and
        /// send it as a 0x35 report on the sink's 0x13 speaker lane.</summary>
        private static void SendDs5BtFrame(Sink s, float[] pull, byte[] opus, byte[] report)
        {
            s.Ds5OpusEncoder ??= CreateDs5OpusEncoder();
            int n;
            try { n = s.Ds5OpusEncoder.Encode(pull.AsSpan(), Ds5OpusFrameSamples, opus.AsSpan(), Ds5OpusBytes); }
            catch { s.Ds5OpusEncoder = null; return; }

            Array.Clear(report, 0, report.Length);
            report[0] = 0x35;
            report[1] = (byte)((_ds5Seq & 0x0F) << 4);
            _ds5Seq = (_ds5Seq + 1) & 0x0F;
            // packet 0x11: session header (SAxense default — no handshake)
            report[2] = 0x11 | 0x80;
            report[3] = 7;
            report[4] = 0xFE;
            report[9] = 0xFF;
            report[10] = _ds5PktCounter++;
            // packet 0x13: speaker audio lane (0x16 = headset jack), one
            // Opus frame filling the slot
            report[11] = 0x13 | 0x80;
            report[12] = (byte)Ds5OpusBytes;
            Array.Copy(opus, 0, report, 13, Math.Min(n, Ds5OpusBytes));
            uint crc = Crc32(report, Ds5BtReportSize - 4);
            report[Ds5BtReportSize - 4] = (byte)(crc & 0xFF);
            report[Ds5BtReportSize - 3] = (byte)((crc >> 8) & 0xFF);
            report[Ds5BtReportSize - 2] = (byte)((crc >> 16) & 0xFF);
            report[Ds5BtReportSize - 1] = (byte)(crc >> 24);

            bool hardFail = true; // no pool == hard failure
            bool sent = s.Tx != null && s.Tx.TrySend(s.BtHandle, report, out hardFail);
            if (!sent)
            {
                if (hardFail)
                {
                    // I/O error — mark and let the worker detach/rebuild on
                    // its 5 s cadence.
                    s.TransportFailed = true;
                }
                // else: pool saturated (link backpressure) — drop this frame;
                // the pad conceals one missing frame far better than it
                // handles a catch-up burst.
                return;
            }
        }

        /// <summary>One DS4 tick: resample the tick's 48 kHz pull to 32 kHz
        /// s16 (persistent-phase linear, exact 3:2 so pitch is exact; the
        /// drift trim arrives through <paramref name="inFrames"/> like the
        /// DS5 lane), encode full 256-sample blocks to 109-byte SBC frames,
        /// and ship at most ONE 4-frame report 0x17 per tick — the DS5
        /// hardware experiments showed Sony firmware drops bursts, so the
        /// same skip-not-burst discipline applies here as the conservative
        /// default until DS4 hardware says otherwise. Steady state: 2.67
        /// frames produced per 10.667 ms tick, one report per ~16 ms.</summary>
        private static void Ds4BtTick(Sink s, float[] pull, int inFrames)
        {
            if (s.Ds4Sbc == null || s.Ds4Pending == null || s.Ds4Frames == null) return;

            // 48 → 32 kHz, continuous across ticks: the virtual input is
            // [carry] + pull[0..inFrames-1] with position 0 at the carry
            // sample, so every output position has a real interpolation
            // pair and the phase stays in [0, 1.5) — no boundary jitter.
            double pos = s.Ds4ResamplePhase;
            int cap = s.Ds4Pending.Length;
            while (pos < inFrames && s.Ds4PendingCount <= cap - 2)
            {
                int i0 = (int)pos;
                double t = pos - i0;
                float l0 = i0 == 0 ? s.Ds4CarryL : pull[(i0 - 1) * 2];
                float r0 = i0 == 0 ? s.Ds4CarryR : pull[(i0 - 1) * 2 + 1];
                float l1 = pull[i0 * 2];
                float r1 = pull[i0 * 2 + 1];
                float l = (float)(l0 * (1 - t) + l1 * t);
                float r = (float)(r0 * (1 - t) + r1 * t);
                s.Ds4Pending[s.Ds4PendingCount++] = (short)Math.Clamp((int)(l * 32767f), short.MinValue, short.MaxValue);
                s.Ds4Pending[s.Ds4PendingCount++] = (short)Math.Clamp((int)(r * 32767f), short.MinValue, short.MaxValue);
                pos += Ds4ResampleStep;
            }
            s.Ds4ResamplePhase = Math.Max(0, pos - inFrames);
            s.Ds4CarryL = pull[(inFrames - 1) * 2];
            s.Ds4CarryR = pull[(inFrames - 1) * 2 + 1];

            // Encode every complete 256-sample block (128 per channel = one
            // 4 ms SBC frame).
            int consumed = 0;
            while (s.Ds4PendingCount - consumed >= Ds4SbcEncoder.PcmSamplesPerFrame)
            {
                var frame = new byte[Ds4SbcEncoder.FrameBytes];
                s.Ds4Sbc.Encode(
                    s.Ds4Pending.AsSpan(consumed, Ds4SbcEncoder.PcmSamplesPerFrame), frame);
                consumed += Ds4SbcEncoder.PcmSamplesPerFrame;
                if (s.Ds4Frames.Count >= Ds4FrameQueueCap)
                    s.Ds4Frames.Dequeue();   // bound latency; drop oldest
                s.Ds4Frames.Enqueue(frame);
            }
            if (consumed > 0)
            {
                Array.Copy(s.Ds4Pending, consumed, s.Ds4Pending, 0, s.Ds4PendingCount - consumed);
                s.Ds4PendingCount -= consumed;
            }

            // Reference cadence (DS4AudioStreamer HidAudioRouterWorker._worker,
            // corroborated by ds4mac §12.6-12.7): wake at ≥ 4 buffered frames,
            // then drain while ≥ 2 remain — 4-frame 0x17 preferred, 2-frame
            // 0x14 fallback. Post-stall bursts are the reference's proven
            // recovery; the DS4 codec resynchronizes via the frame counter.
            // (The DS5's one-report-per-tick contract is a DS5 finding and
            // deliberately NOT projected here.)
            if (s.Ds4Frames.Count < Ds4MinBufferedFrames) return;
            while (s.Ds4Frames.Count >= 2)
            {
                int frames = s.Ds4Frames.Count >= Ds4FramesPerReport ? Ds4FramesPerReport : 2;
                int size = frames == Ds4FramesPerReport ? Ds4BtReportSize : Ds4SmallReportSize;
                var report = new byte[size];
                report[0] = frames == Ds4FramesPerReport ? (byte)0x17 : (byte)0x14;
                report[1] = 0x40;
                report[2] = 0xA2;
                report[3] = (byte)(s.Ds4FrameCounter & 0xFF);
                report[4] = (byte)(s.Ds4FrameCounter >> 8);
                report[5] = 0x02;   // internal speaker (0x24 = headset jack)
                int o2 = 6;
                for (int i = 0; i < frames; i++)
                {
                    var f = s.Ds4Frames.Dequeue();
                    Array.Copy(f, 0, report, o2, f.Length);
                    o2 += f.Length;
                }
                s.Ds4FrameCounter += (ushort)frames;
                uint crc = Crc32(report, size - 4);
                report[size - 4] = (byte)crc;
                report[size - 3] = (byte)(crc >> 8);
                report[size - 2] = (byte)(crc >> 16);
                report[size - 1] = (byte)(crc >> 24);

                bool hardFail = true;   // no pool == hard failure
                bool sent = s.Tx != null && s.Tx.TrySend(s.BtHandle, report, out hardFail);
                if (!sent)
                {
                    if (hardFail) s.TransportFailed = true;
                    break;   // pool saturated: leave frames queued for next tick
                }
            }
        }

        /// <summary>Single synchronous overlapped write for one-shot control
        /// reports (the streaming pool's buffers are sized for audio reports
        /// and must not pad a short control report).</summary>
        private static bool WriteOneShot(IntPtr h, byte[] report)
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(report, System.Runtime.InteropServices.GCHandleType.Pinned);
            IntPtr ev = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
            IntPtr ol = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i += 8) System.Runtime.InteropServices.Marshal.WriteInt64(ol, i, 0);
                System.Runtime.InteropServices.Marshal.WriteIntPtr(ol, 24, ev); // OVERLAPPED.hEvent (x64)
                bool ok = NativeMethods.WriteFileRaw(h, pin.AddrOfPinnedObject(), (uint)report.Length, IntPtr.Zero, ol);
                if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 997 /* ERROR_IO_PENDING */)
                    ok = NativeMethods.WaitForSingleObject(ev, 1000) == 0;
                if (!ok) NativeMethods.CancelIo(h);
                return ok;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ol);
                NativeMethods.CloseHandle(ev);
                pin.Free();
            }
        }

        /// <summary>48 kHz stereo, 10 ms frames, hard CBR at 160 kbps so
        /// every frame is exactly <see cref="Ds5OpusBytes"/> bytes — the
        /// firmware expects the frame to fill the 0x13 packet slot.</summary>
        private static IOpusEncoder CreateDs5OpusEncoder()
        {
            var enc = OpusCodecFactory.CreateEncoder(Rate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
            enc.Bitrate = Ds5OpusBytes * 8 * 100;
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
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ResetEvent(IntPtr h);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetEvent(IntPtr h);
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIo(IntPtr h);
            [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteFile")]
            public static extern bool WriteFileRaw(IntPtr h, IntPtr buf, uint n, IntPtr written, IntPtr overlapped);

            // High-resolution waitable timer — true sub-ms waits regardless
            // of the scheduler quantum (timeBeginPeriod is ignored under
            // Win11 power throttling for background processes). Same primitive
            // the reference haptic timer threads use.
            private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
            // TIMER_MODIFY_STATE | SYNCHRONIZE — TIMER_ALL_ACCESS gets
            // rejected together with the high-resolution flag on some builds
            // (the field telemetry showed the fallback Sleep quantum, meaning
            // creation was failing).
            private const uint TIMER_ACCESS = 0x0002 | 0x00100000;
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name, uint flags, uint access);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetWaitableTimer(IntPtr h, ref long dueTime, int period, IntPtr cb, IntPtr arg, bool resume);

            /// <summary>A high-resolution timer, or IntPtr.Zero if unavailable
            /// (caller falls back to Thread.Sleep).</summary>
            public static IntPtr CreateHighResTimer()
            {
                IntPtr t = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ACCESS);
                if (t == IntPtr.Zero) // pre-1803 or flag rejected: plain timer beats the Sleep quantum
                    t = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ACCESS);
                return t;
            }

            /// <summary>Wait <paramref name="ms"/> with sub-ms accuracy.</summary>
            public static void HighResWait(IntPtr timer, double ms)
            {
                long due = -(long)(ms * 10000.0); // negative = relative, 100 ns units
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitForSingleObject(timer, 100);
            }
            [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initial, string name);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

            [DllImport("kernel32.dll")]
            public static extern uint WaitForSingleObject(IntPtr h, uint ms);

            public static IntPtr OpenHid(string path)
            {
                return CreateFileW(path,
                    0x40000000u | 0x80000000u,        // GENERIC_WRITE | GENERIC_READ
                    0x1u | 0x2u,                      // share read/write
                    IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0x40000000u /*FILE_FLAG_OVERLAPPED*/, IntPtr.Zero);
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
