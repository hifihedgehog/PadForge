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
    /// output (per-device toggle, <c>DeviceSlotConfig.AudioPassthroughEnabled</c>).
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
        /// InputService against the live DeviceSlotConfig dictionaries:
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

            /// <summary>Remote output relay (#138): this sink renders speaker PCM
            /// arriving over the link from a consumer, not the local loopback+macro
            /// mix. The transport (BT Opus/SBC or USB) is the real local device; only
            /// the PCM source is the network ring.</summary>
            public bool RemoteFed;
            public RemoteAudioRing Remote;

            /// <summary>Consumer side (#138): this pad lives on a peer (HidPath is
            /// "peer://..."). It has NO local device transport — the stream thread's
            /// peer lane pulls <see cref="Source"/>.Read (the same test-tone + macro +
            /// passthrough mix a local pad gets) and ships it to the owner, who
            /// re-renders to the real pad speaker. HidPath is the RemoteLinkOutputRouter
            /// ship key. ShipBuf/ShipCount are the float->s16 carry, stream thread only.</summary>
            public bool IsPeer;
            public byte[] ShipBuf;
            public int ShipCount;

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

            /// <summary>Per-sink DS5 BT audio sequence tag (report byte 1 high
            /// nibble) and 0x11-header rolling packet counter. These were global,
            /// so a second DualSense made each firmware see a sequence jumping by
            /// two and drop/garble the audio. One counter pair per pad keeps every
            /// stream monotonic (BT thread only).</summary>
            public int Ds5Seq;
            public byte Ds5PktCounter;

            /// <summary>Persona haptic stream (report 0x32) counters, SEPARATE
            /// from the 0x35 speaker pair above by the same rule that created
            /// that pair: the firmware tracks sequence per stream, and a shared
            /// counter makes each stream see jumps of two and garble. The
            /// references agree (dualsense-bt-haptics runs an independent
            /// reportSeqCounter for its 0x32s beside outputSeq for 0x31).</summary>
            public int Ds5HapticSeq;
            public byte Ds5HapticPktCounter;
            /// <summary>Last tick the decimated haptic block held signal; the
            /// 0x32 stream idles after 2 s of silence like the speaker lane,
            /// instead of interleaving zero-payload reports forever.</summary>
            public long Ds5HapticAudibleTicks;
            /// <summary>BT mic session state for the persona feed: 0 closed,
            /// 1 open-requested. The open/close toggle report rides this
            /// sink's 0x32 stream (BT thread only).</summary>
            public int Ds5MicOpen;
            public long Ds5MicOpenSentTicks;
            public int Ds5MicOpenTries;
            public bool Ds5MicCloseScrubbed;

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
                        // Free a slot only when its drain wait signaled: the kernel
                        // keeps referencing the pinned buffer and native OVERLAPPED
                        // until the cancelled completion fires, so a slot whose
                        // write is still in flight after 100 ms is deliberately
                        // leaked (bounded, pathological-path-only) instead of
                        // handing the BT stack freed memory to complete into.
                        bool drained = true;
                        if (_ev[i] != IntPtr.Zero)
                            drained = NativeMethods.WaitForSingleObject(_ev[i], 100) == 0;
                        if (!drained) { _ev[i] = IntPtr.Zero; _ol[i] = IntPtr.Zero; _pin[i] = default; continue; }
                        if (_ev[i] != IntPtr.Zero)
                        {
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
                // Remote output relay (#138): a remote-fed sink renders the speaker PCM
                // the consumer shipped over the link, in place of local loopback+macros.
                if (_sink.RemoteFed && _sink.Remote != null)
                {
                    _sink.Remote.ReadFloat(buffer, offset, count);
                    LoopbackLagFrames = -1;
                    for (int i = 0; i < count; i++)
                        if (buffer[offset + i] > 1e-4f || buffer[offset + i] < -1e-4f)
                        { _sink.LastAudibleTicks = Environment.TickCount64;
                          // count is interleaved-stereo element count (frames*2);
                          // the counter is documented as frames, so halve it.
                          System.Threading.Interlocked.Add(ref _remoteAudioRenderedFrames, count / 2); break; }
                    return count;
                }

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

                // Composite persona speaker mix (HM#39): the PCM the game
                // rendered into the virtual pad's endpoint, additive like
                // the branches above. Gain was applied at ring write from
                // the endpoint's UAC volume/mute, so this is a plain sum.
                if (_personaSpeakerRings.TryGetValue(_sink.DeviceGuid, out var persona))
                    persona.ReadFloatAdd(buffer, offset, count);

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
        /// <summary>Device's configured AudioOutputPath as an int
        /// (DeviceSlotConfig enum: 0 Automatic .. 4 SpeakerOnly).
        /// Wired by InputService; null / throw resolves 0.</summary>
        internal static Func<Guid, int> DeviceAudioOutputPathProvider;

        // Last observed headphone-jack state per pad, written by the BT
        // raw reader from the input status byte (duaLib /*53.0*/
        // PluggedHeadphones). Absent = never observed (USB pads, or no
        // persona lane running). Retained after the reader stops: a
        // stale reading beats flapping the route to Default mid-song.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> s_padJackState = new();

        internal static void NoteHeadphoneJack(Guid pad, bool plugged)
        {
            if (pad != Guid.Empty) s_padJackState[pad] = plugged;
        }

        internal static bool? TryGetHeadphoneJack(Guid pad)
            => s_padJackState.TryGetValue(pad, out var v) ? v : (bool?)null;

        /// <summary>Resolves a configured AudioOutputPath to the
        /// EFFECTIVE path this frame. FollowHeadphoneJack (5) becomes
        /// StereoHeadset while the jack reads plugged, SpeakerOnly while
        /// it reads unplugged, and Default when no reading exists, so an
        /// unobservable jack degrades to stock behaviour instead of
        /// guessing. Every consumer of the path (the dispatcher's
        /// register write, the BT lane pid, the USB channel shaper) MUST
        /// route through this so a plug/unplug switches all three
        /// coherently; the dispatcher's change gating then turns the
        /// transition into the one-shot route re-arm, exactly
        /// DS5_Bridge's bt_rearm_speaker_output_route.</summary>
        internal static int ResolveOutputPath(int configured, Guid device)
        {
            if (configured != 5) return configured;
            bool? jack = TryGetHeadphoneJack(device);
            if (jack == true) return 1;    // StereoHeadset
            if (jack == false) return 4;   // SpeakerOnly
            return 0;                       // unknown: Default
        }

        /// <summary>Maps one stereo source frame onto the pad's UAC
        /// channels 0/1 for the configured output path.
        ///
        /// <para>The pad's UAC channel roles depend on OutputPathSelect:
        /// under path 0 (stereo headset) ch0/ch1 are headphone L/R;
        /// under path 1/2 ch0 feeds both ears (mono headset) and ch1
        /// feeds the speaker (path 2) or nothing (path 1); under path 3
        /// the speaker plays ch1. The original hardcoded (0, mono)
        /// shape was correct ONLY for the speaker paths, so selecting
        /// Headphones (Stereo) played silence in the left ear and a
        /// mono downmix in the right. Owner-reported 2026-08-01.</para></summary>
        internal static void MapMirrorChannels(int path, float l, float r,
            out float ch0, out float ch1)
        {
            switch (path)
            {
                case 1:   // StereoHeadset (firmware 0, L_R_X)
                    ch0 = l;
                    ch1 = r;
                    break;
                case 2:   // MonoHeadset (firmware 1, L_L_X): ch0 to both ears
                    ch0 = Math.Clamp((l + r) * 0.5f, -1f, 1f);
                    ch1 = 0f;
                    break;
                case 3:   // HeadsetAndSpeaker (firmware 2, L_L_R)
                    ch0 = Math.Clamp((l + r) * 0.5f, -1f, 1f);
                    ch1 = ch0;
                    break;
                default:  // Automatic / SpeakerOnly: the speaker plays ch1
                    ch0 = 0f;
                    ch1 = Math.Clamp((l + r) * 0.5f, -1f, 1f);
                    break;
            }
        }

        private sealed class UsbFrameProvider : IWaveProvider
        {
            private readonly ISampleProvider _src;
            private readonly int _outChannels;
            private float[] _pull = new float[4096];
            private float[] _frames = new float[8192];

            public UsbFrameProvider(ISampleProvider src, WaveFormat endpointFormat, Guid deviceGuid = default)
            {
                _src = src;
                WaveFormat = endpointFormat;
                _outChannels = endpointFormat.Channels;
                _deviceGuid = deviceGuid;
            }

            public WaveFormat WaveFormat { get; }
            private readonly Guid _deviceGuid;
            private short[] _haptic = Array.Empty<short>();

            public int Read(byte[] buffer, int offset, int count)
            {
                int frames = count / (4 * _outChannels);
                int need = frames * 2;
                if (_pull.Length < need) _pull = new float[need];
                if (_frames.Length < frames * _outChannels) _frames = new float[frames * _outChannels];
                _src.Read(_pull, 0, need);

                // Authored haptics (HM#39): the real pad's UAC channels
                // 2/3 are its voice-coil actuators, zeroed until a
                // composite persona feeds them. Same 48 kHz s16 stereo on
                // both sides, so this is a pass-through, no resample.
                bool haveHaptics = _outChannels >= 4
                    && _deviceGuid != default
                    && _personaHapticRings.TryGetValue(_deviceGuid, out var hring);
                if (haveHaptics)
                {
                    if (_haptic.Length < frames * 2) _haptic = new short[frames * 2];
                    _personaHapticRings[_deviceGuid].ReadFrames(_haptic, frames);
                }

                // Resolve the device's output path once per Read (the
                // provider walks a small config map; ~100 calls/s).
                int outPath = 0;
                try { outPath = DeviceAudioOutputPathProvider?.Invoke(_deviceGuid) ?? 0; }
                catch { }

                for (int f = 0; f < frames; f++)
                {
                    int o = f * _outChannels;
                    MapMirrorChannels(outPath, _pull[f * 2], _pull[f * 2 + 1],
                        out _frames[o], out _frames[o + 1]);
                    if (haveHaptics)
                    {
                        _frames[o + 2] = _haptic[f * 2] / 32768f;
                        _frames[o + 3] = _haptic[f * 2 + 1] / 32768f;
                        for (int c = 4; c < _outChannels; c++) _frames[o + c] = 0f;
                    }
                    else
                    {
                        for (int c = 2; c < _outChannels; c++) _frames[o + c] = 0f;
                    }
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
        public static List<MixingSampleProvider> GetSlotSinkMixers(int slot, out bool pendingActivation, Guid? deviceFilter = null)
        {
            bool anyEligible = false;
            foreach (var (_, ud) in EnumerateAssignedSonyPads(slot))
            {
                // A peer:// pad's transport realities (BT vs USB) are the owner's
                // problem; the consumer just ships the mix. Always eligible so a
                // remote DS4 (whose peer path lacks the BT container guid) isn't
                // wrongly excluded by the wired-DS4 filter below.
                if ((ud.DevicePath ?? "").StartsWith("peer://", StringComparison.Ordinal)) { anyEligible = true; break; }
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
                live = _sinks.Values
                    .Where(s => s.Slot == slot && SinkAlive(s)
                                && (deviceFilter == null || s.DeviceGuid == deviceFilter.Value))
                    .Select(s => s.MacroMixer)
                    .ToList();
            }
            pendingActivation = anyEligible && live.Count == 0;
            if (pendingActivation) _workSignal.Set();
            return live;
        }

        // Physical pads with a forwarded Sony vendor audio test (firmware
        // waveout) in flight, with an auto-expiry tick. While a test runs
        // the TESTER owns the pad's audio plane: WantsSpeakerPath reports
        // false — which suspends both the effects dispatcher's speaker-path
        // asserts and the passthrough dispatcher's audio-control masking —
        // and the BT mirror stream pauses so the Opus frames don't fight
        // the firmware tone. Without this, a headphone waveout test bled
        // through the speaker because the mirror kept the speaker path and
        // volume asserted and masked the tester's own volume writes.
        private static readonly Dictionary<Guid, long> _vendorAudioTests = new();

        /// <summary>Marks a forwarded vendor audio test active/inactive on
        /// a physical pad. Expires after 60 s on its own in case the
        /// test's off command never arrives (closed browser tab).
        ///
        /// Suspending the asserts is NOT enough: the routing byte is
        /// sticky in firmware. Our mirror holds output_path_select = 3
        /// (X_X_R — headphone muted, speaker taps the shared bus's R
        /// channel, per InputPlumber's SetState struct docs), and the
        /// firmware waveout injects into that same bus, so a headphone
        /// test audibly bled from the speaker until the routing was
        /// actively restored. Queue the existing speaker-path-clear
        /// one-shot (audioControlFlags = 0 → output_path L_R_X) so the
        /// effects dispatcher rewrites the routing before the tone
        /// starts; the test-end re-assert brings the mirror back.</summary>
        public static void SetVendorAudioTest(Guid deviceGuid, bool active)
        {
            lock (_lock)
            {
                if (active)
                {
                    _vendorAudioTests[deviceGuid] = Environment.TickCount64 + 60_000;
                    _speakerPathCleared.Add(deviceGuid);
                }
                else
                {
                    _vendorAudioTests.Remove(deviceGuid);
                }
            }
        }

        private static bool VendorAudioTestActive_NoLock(Guid guid)
        {
            if (!_vendorAudioTests.TryGetValue(guid, out long exp)) return false;
            if (Environment.TickCount64 >= exp) { _vendorAudioTests.Remove(guid); return false; }
            return true;
        }

        /// <summary>True while the device has an active sink — the DS5
        /// dispatcher asserts the firmware speaker output path + volume.
        /// False while a forwarded vendor audio test owns the pad.</summary>
        public static bool WantsSpeakerPath(Guid deviceGuid)
        {
            lock (_lock)
                return _sinks.TryGetValue(deviceGuid, out var s) && SinkAlive(s)
                    && !VendorAudioTestActive_NoLock(deviceGuid);
        }

        /// <summary>Non-consuming read of the one-shot below. The dispatcher
        /// builds its payload under one lock and performs the blocking HID
        /// write later under another, and that write can be dropped as stale or
        /// fail outright. Consuming at build time therefore armed the flag,
        /// dropped the write, and left the firmware speaker path asserted with
        /// nothing left to restore it. Peek while building, consume only once
        /// the write has actually landed.</summary>
        public static bool PeekSpeakerPathCleared(Guid deviceGuid)
        {
            lock (_lock)
                return _speakerPathCleared.Contains(deviceGuid);
        }

        /// <summary>One-shot per device after its sink is torn down, so the
        /// dispatcher restores the firmware headphone path once.</summary>
        public static bool TryConsumeSpeakerPathCleared(Guid deviceGuid)
        {
            lock (_lock)
                return _speakerPathCleared.Remove(deviceGuid);
        }

        // ── Remote speaker audio (issue #138) ───────────────────────────────
        // A consumer ships the speaker PCM it produced for a shared pad; the owner
        // renders it to the real pad speaker through the existing Sink transport.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RemoteAudioRing> _remoteRings = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _remoteAudioDemand = new();

        // Owner-side diagnostics (#138 audio): blocks the ring received vs. audible
        // frames the device transport actually pulled. Surfaced in the SNAP line so an
        // owner log alone shows whether received audio is reaching the pad speaker.
        private static long _remoteAudioRxBlocks;
        private static long _remoteAudioRenderedFrames;
        public static long RemoteAudioRxBlocks => System.Threading.Interlocked.Read(ref _remoteAudioRxBlocks);
        public static long RemoteAudioRenderedFrames => System.Threading.Interlocked.Read(ref _remoteAudioRenderedFrames);

        /// <summary>Owner-side: a paired peer sent a speaker PCM block (s16 48 kHz
        /// stereo, interleaved LE) for a shared pad. Feed it into the device's render
        /// ring and mark demand so the worker builds the real BT/USB transport and the
        /// sink renders the network audio in place of local loopback.</summary>
        public static void FeedRemoteAudio(Guid physicalDeviceGuid, byte[] s16StereoPcm)
        {
            if (s16StereoPcm == null || s16StereoPcm.Length < 4) return;
            System.Threading.Interlocked.Increment(ref _remoteAudioRxBlocks);
            var ring = _remoteRings.GetOrAdd(physicalDeviceGuid, _ => new RemoteAudioRing());
            ring.WriteS16(s16StereoPcm);
            bool isNew = !_remoteAudioDemand.ContainsKey(physicalDeviceGuid);
            _remoteAudioDemand[physicalDeviceGuid] = Environment.TickCount64;
            if (isNew)
            {
                lock (_lock) EnsureThreads_NoLock();
                _workSignal.Set(); // build the transport for this newly-demanded pad
            }
        }

        // Consumer ship-block size (#138). 256 frames * 2 ch * 2 B = 1024 B per block;
        // sealed (+30 B header+tag) it stays under the 1500 B MTU so the audio datagram
        // is never IP-fragmented. The mix shipped is the per-pad Sink's full output
        // (test tone + slot macros + system passthrough), pulled by the stream thread's
        // peer lane in ShipPeerAudioTick — NOT a separate system-endpoint loopback.
        private const int RemoteAudioBlockBytes = 1024;

        /// <summary>Float ring fed by the network thread (WriteS16) and drained by the
        /// sink's render pull (ReadFloat). Holds ~0.5 s; on underrun it returns silence,
        /// and the owner's adaptive BT pacing rides out the jitter.</summary>
        internal sealed class RemoteAudioRing
        {
            private readonly float[] _ring = new float[Rate]; // 0.5 s of 48k stereo (Rate samples = Rate/2 frames *2)
            private long _write;
            private long _read = -1;
            private readonly object _g = new();

            public void WriteS16(byte[] pcm)
            {
                // Whole interleaved-stereo frames only (4 B = L+R s16): an odd
                // sample count would advance _write by an odd amount and swap
                // L/R permanently for everything after it (ReadFloat consumes
                // pairs), so a short tail byte or lone sample is dropped.
                int samples = (pcm.Length / 4) * 2;
                lock (_g)
                {
                    for (int i = 0; i < samples; i++)
                    {
                        short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                        _ring[_write % _ring.Length] = s / 32768f;
                        _write++;
                    }
                }
            }

            /// <summary>Persona-feed writer: extract one stereo pair per
            /// <paramref name="strideBytes"/>-sized frame of a wider
            /// interleave (the composite's 4-channel window), scale, and
            /// append. Same whole-pairs discipline as WriteS16.</summary>
            public void WriteS16PairsScaled(ReadOnlySpan<byte> src, int strideBytes, int offL, int offR, float gain)
            {
                lock (_g)
                {
                    for (int i = 0; i + strideBytes <= src.Length; i += strideBytes)
                    {
                        short l = (short)(src[i + offL] | (src[i + offL + 1] << 8));
                        short r = (short)(src[i + offR] | (src[i + offR + 1] << 8));
                        _ring[_write % _ring.Length] = l / 32768f * gain;
                        _ring[(_write + 1) % _ring.Length] = r / 32768f * gain;
                        _write += 2;
                    }
                }
            }

            /// <summary>Additive variant of <see cref="ReadFloat"/> for the
            /// persona speaker mix: sums into the caller's buffer instead
            /// of overwriting, and never zero-fills, so it composes with
            /// the macro and passthrough branches around it.</summary>
            public void ReadFloatAdd(float[] buffer, int offset, int count)
            {
                lock (_g)
                {
                    const int Cushion = 1920;
                    const int Catastrophe = 24000;
                    long avail = _write;
                    if (_read < 0 || _read > avail || avail - _read > Catastrophe)
                        _read = Math.Max(0, avail - Cushion);
                    int canRead = (int)Math.Min(count, avail - _read);
                    for (int i = 0; i < canRead; i++)
                        buffer[offset + i] += _ring[(_read + i) % _ring.Length];
                    _read += canRead;
                }
            }

            /// <summary>Fill `count` interleaved-stereo floats from the ring. Keeps a
            /// ~20 ms cushion behind the write edge; underruns emit silence.</summary>
            public void ReadFloat(float[] buffer, int offset, int count)
            {
                lock (_g)
                {
                    const int Cushion = 1920;    // 20 ms stereo samples (960 frames)
                    const int Catastrophe = 24000; // 250 ms
                    long avail = _write;
                    if (_read < 0 || _read > avail || avail - _read > Catastrophe)
                        _read = Math.Max(0, avail - Cushion);
                    int canRead = (int)Math.Min(count, avail - _read);
                    for (int i = 0; i < canRead; i++)
                        buffer[offset + i] = _ring[(_read + i) % _ring.Length];
                    for (int i = canRead; i < count; i++)
                        buffer[offset + i] = 0f;
                    _read += canRead;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Composite-persona feed (HM v1.4.0, HM#39 / #255)
        // ─────────────────────────────────────────────
        //
        // A slot whose VC is a composite USB persona has a REAL Windows
        // audio endpoint the game renders into. HM delivers that PCM as
        // 4-channel windows (ChannelRoles: speakerLeft/Right,
        // hapticLeft/Right) on its pacing thread. This feed routes it to
        // the slot's physical Sony pads:
        //   speaker ch  → additive mix into each pad Sink's render path
        //                 (SinkSource.Read), so it rides the existing BT
        //                 Opus / USB shared-mode transports beside macros
        //                 and system passthrough;
        //   haptic ch   → DS5 BT: its own report 0x32 (packets 0x11+0x12,
        //                 the SAxense/dualsense-bt-haptics proven shape,
        //                 never combined into the 0x35 speaker report);
        //                 DS5 USB: channels 2/3 of the real pad's UAC
        //                 endpoint (UsbFrameProvider), which the shaper
        //                 zeroed until now;
        //   microphone  ← the physical pad's own capture endpoint
        //                 (container-matched, USB pads only; BT pads have
        //                 no Windows mic and HM feeds silence when dry);
        //   ControlChanged → per-feed speaker/mic gain. A UAC endpoint's
        //                 volume is hardware volume: Windows sends
        //                 SET_CUR and does NOT attenuate the stream, so
        //                 honoring the mixer slider is our job.
        // The feed's presence is itself sink demand (like the remote-audio
        // demand): a composite slot with passthrough off and no macros
        // still builds its pads' transports.

        private sealed class PersonaFeed
        {
            public HIDMaestro.HMUsbAudio Audio;
            public volatile Guid[] Targets = Array.Empty<Guid>();
            public float SpeakerGain = 1f, MicGain = 1f;
            public bool SpeakerMuted, MicMuted;
            public int SpkL, SpkR, HapL, HapR;   // interleave indices from ChannelRoles
            public Action<HIDMaestro.HMAudioOutput, ReadOnlyMemory<byte>> FramesHandler;
            public EventHandler<HIDMaestro.HMAudioControlChangedEventArgs> ControlHandler;
            public WasapiCapture Mic;
            public Guid MicPadGuid;
            public byte[] MicScratch = Array.Empty<byte>();
            // BT mic reader (DS5 only): parallel sync HID handle; Windows
            // HIDClass queues input reports per file object, so this never
            // steals reports from SDL's reader.
            public System.Threading.Thread BtMicThread;
            public volatile bool BtMicStop;
            public IntPtr BtMicHandle;
            public Guid BtMicPadGuid;
            public long BtMicRxFrames;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, PersonaFeed> _personaFeeds = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, RemoteAudioRing> _personaSpeakerRings = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PersonaHapticRing> _personaHapticRings = new();

        /// <summary>s16 stereo 48 kHz ring for the haptic lanes. Writer is
        /// HM's pacing thread; readers are the BT tick (which decimates
        /// 16:1 to the pad's 3 kHz voice-coil format) and the USB shaper
        /// (which passes 48 kHz through to UAC channels 2/3).</summary>
        internal sealed class PersonaHapticRing
        {
            private readonly short[] _ring = new short[Rate]; // 0.5 s stereo
            private long _write;
            private long _read = -1;
            private readonly object _g = new();

            public void WriteS16Pairs(ReadOnlySpan<byte> src, int strideBytes, int offL, int offR)
            {
                lock (_g)
                {
                    for (int i = 0; i + strideBytes <= src.Length; i += strideBytes)
                    {
                        _ring[_write % _ring.Length] = (short)(src[i + offL] | (src[i + offL + 1] << 8));
                        _ring[(_write + 1) % _ring.Length] = (short)(src[i + offR] | (src[i + offR + 1] << 8));
                        _write += 2;
                    }
                }
            }

            public int FramesAvailable { get { lock (_g) return (int)((_write - Math.Max(_read, 0)) / 2); } }

            /// <summary>Drain up to <paramref name="frames"/> stereo frames.
            /// Returns frames actually read; the rest of the caller's
            /// buffer is zero-filled. Same cushion/catastrophe policy as
            /// the speaker rings.</summary>
            public int ReadFrames(short[] dst, int frames)
            {
                lock (_g)
                {
                    const int Cushion = 1920;      // 20 ms stereo samples
                    const int Catastrophe = 24000; // 250 ms
                    long avail = _write;
                    if (_read < 0 || _read > avail || avail - _read > Catastrophe)
                        _read = Math.Max(0, avail - Cushion);
                    int canRead = (int)Math.Min(frames * 2, avail - _read);
                    canRead -= canRead & 1;
                    for (int i = 0; i < canRead; i++)
                        dst[i] = _ring[(_read + i) % _ring.Length];
                    for (int i = canRead; i < frames * 2; i++) dst[i] = 0;
                    _read += canRead;
                    return canRead / 2;
                }
            }
        }

        /// <summary>Attach the composite persona's audio surfaces for a
        /// slot. Called by Step 5 after Connect; idempotent per slot.</summary>
        public static void AttachPersonaFeed(int slot, HIDMaestro.HMUsbAudio audio)
        {
            if (audio == null) return;
            DetachPersonaFeed(slot);

            var feed = new PersonaFeed { Audio = audio };
            var roles = audio.Output.ChannelRoles;
            feed.SpkL = IndexOfRole(roles, "speakerLeft", 0);
            feed.SpkR = IndexOfRole(roles, "speakerRight", 1);
            feed.HapL = IndexOfRole(roles, "hapticLeft", -1);
            feed.HapR = IndexOfRole(roles, "hapticRight", -1);

            feed.FramesHandler = (output, pcm) => OnPersonaFrames(feed, output, pcm);
            feed.ControlHandler = (_, e) =>
            {
                Engine.SdlDiagLog.WriteLine(
                    $"PERSONA ctrl fn={e.Function} mute={e.IsMute}/{e.MuteValue} dB={e.VolumeDb:F1} raw={e.RawValue}");
                // UAC1 s16 dB → linear. Mute and volume are separate
                // controls on the same feature unit, both honored.
                if (e.Function == "speaker")
                {
                    if (e.IsMute) feed.SpeakerMuted = e.MuteValue;
                    else feed.SpeakerGain = (float)Math.Pow(10.0, e.VolumeDb / 20.0);
                }
                else if (e.Function == "microphone")
                {
                    if (e.IsMute) feed.MicMuted = e.MuteValue;
                    // The DualSense mic feature unit spans 0..+48 dB, the
                    // pad's hardware BOOST range, with unity at the top:
                    // the reference emulation attenuates from max, never
                    // amplifies (hbashton audio_control.go, "attenuates
                    // the virtual microphone by exactly 48 dB"). A literal
                    // linear mapping made Windows' default +48 dB a x251
                    // multiplier and clipped every sample.
                    else feed.MicGain = (float)Math.Min(1.0, Math.Pow(10.0, (e.VolumeDb - 48.0) / 20.0));
                }
            };
            audio.Output.FramesReceived += feed.FramesHandler;
            audio.ControlChanged += feed.ControlHandler;
            audio.Microphone.StreamingChanged += (_, on) =>
                Engine.SdlDiagLog.WriteLine("PERSONA mic host capture pin " + (on ? "OPEN" : "CLOSED"));

            _personaFeeds[slot] = feed;
            Reconcile();
        }

        /// <summary>Detach and stop a slot's persona feed. Safe when none
        /// is attached. Rings persist until sink teardown, matching the
        /// remote-audio ring lifecycle.</summary>
        public static void DetachPersonaFeed(int slot)
        {
            if (!_personaFeeds.TryRemove(slot, out var feed)) return;
            try
            {
                if (feed.FramesHandler != null) feed.Audio.Output.FramesReceived -= feed.FramesHandler;
                if (feed.ControlHandler != null) feed.Audio.ControlChanged -= feed.ControlHandler;
            }
            catch { }
            StopPersonaMic(feed);
            StopBtMic(feed);
            foreach (var g in feed.Targets)
            {
                _personaSpeakerRings.TryRemove(g, out _);
                _personaHapticRings.TryRemove(g, out _);
            }
            Reconcile();
        }

        private static int IndexOfRole(System.Collections.Generic.IReadOnlyList<string> roles, string role, int fallback)
        {
            for (int i = 0; i < roles.Count; i++)
                if (string.Equals(roles[i], role, StringComparison.OrdinalIgnoreCase)) return i;
            return fallback;
        }

        /// <summary>HM pacing-thread callback: split one interleaved s16
        /// window into the per-pad speaker and haptic rings. No
        /// allocations in steady state.</summary>
        private static long _personaCbCount, _personaCbBytes, _personaCbLastLog;
        private static int _sniffHapPeak, _sniffSpkPeak, _sniffDistinct;
        private static bool _sniffActive;
        private static long _sniffLastSignalTicks, _sniffSummaryTicks;

        private static void OnPersonaFrames(PersonaFeed feed, HIDMaestro.HMAudioOutput output, ReadOnlyMemory<byte> pcm)
        {
            // Reception-layer heartbeat: proves host PCM reaches us at all,
            // independent of any decode or routing beyond this point.
            _personaCbCount++;
            _personaCbBytes += pcm.Length;
            long now = Environment.TickCount64;
            if (now - _personaCbLastLog >= 2000)
            {
                _personaCbLastLog = now;
                Engine.SdlDiagLog.WriteLine(
                    $"PERSONA rx cb={_personaCbCount} bytes={_personaCbBytes} targets={feed.Targets.Length} streaming={output.IsStreaming} spkGain={(feed.SpeakerMuted ? 0f : feed.SpeakerGain):F4} spkSends={_personaSpkSends} audible={_personaLastAudible}");
            }
            var targets = feed.Targets;
            if (targets.Length == 0) return;
            int ch = output.Channels;
            if (ch < 2) return;
            int stride = ch * 2;
            var span = pcm.Span;
            float spkGain = feed.SpeakerMuted ? 0f : feed.SpeakerGain;

            // ── Authored-haptics sniffer ──
            // Confirmed empirically 2026-07-31: Windows does NOT upmix
            // ordinary stereo into channels 3/4 (a full music-playback
            // window produced zero haptic signal), so ANY signal here is
            // a deliberate 4-channel render. `distinct` separates an
            // authored track from an app copying the speaker mix into
            // the rears: a copy differs by ~0 per sample.
            if (feed.HapL >= 0 && feed.HapR >= 0 && ch > Math.Max(feed.HapL, feed.HapR))
            {
                int hapPeak = 0, spkPeak = 0, distinct = 0;
                for (int i = 0; i + stride <= span.Length; i += stride)
                {
                    int sl = (short)(span[i + feed.SpkL * 2] | (span[i + feed.SpkL * 2 + 1] << 8));
                    int hl = (short)(span[i + feed.HapL * 2] | (span[i + feed.HapL * 2 + 1] << 8));
                    int hr = (short)(span[i + feed.HapR * 2] | (span[i + feed.HapR * 2 + 1] << 8));
                    int ah = Math.Max(Math.Abs(hl), Math.Abs(hr));
                    if (ah > hapPeak) hapPeak = ah;
                    int asp = Math.Abs(sl);
                    if (asp > spkPeak) spkPeak = asp;
                    int d = Math.Abs(hl - sl);
                    if (d > distinct) distinct = d;
                }
                if (hapPeak > _sniffHapPeak) _sniffHapPeak = hapPeak;
                if (spkPeak > _sniffSpkPeak) _sniffSpkPeak = spkPeak;
                if (distinct > _sniffDistinct) _sniffDistinct = distinct;

                long snow = Environment.TickCount64;
                bool signal = hapPeak > 256;   // ~0.8% FS: above ambient dither
                if (signal) _sniffLastSignalTicks = snow;
                if (signal && !_sniffActive)
                {
                    _sniffActive = true;
                    Engine.SdlDiagLog.WriteLine(
                        $"PERSONA haptics ONSET hapPeak={hapPeak} spkPeak={spkPeak} distinct={distinct}");
                }
                else if (_sniffActive && snow - _sniffLastSignalTicks > 2000)
                {
                    _sniffActive = false;
                    Engine.SdlDiagLog.WriteLine(
                        $"PERSONA haptics OFFSET maxHapPeak={_sniffHapPeak} maxSpkPeak={_sniffSpkPeak} maxDistinct={_sniffDistinct}");
                    _sniffHapPeak = _sniffSpkPeak = _sniffDistinct = 0;
                }
                else if (_sniffActive && snow - _sniffSummaryTicks >= 5000)
                {
                    _sniffSummaryTicks = snow;
                    Engine.SdlDiagLog.WriteLine(
                        $"PERSONA haptics LIVE maxHapPeak={_sniffHapPeak} maxSpkPeak={_sniffSpkPeak} maxDistinct={_sniffDistinct}");
                    _sniffHapPeak = _sniffSpkPeak = _sniffDistinct = 0;
                }
            }

            foreach (var guid in targets)
            {
                var sring = _personaSpeakerRings.GetOrAdd(guid, _ => new RemoteAudioRing());
                sring.WriteS16PairsScaled(span, stride, feed.SpkL * 2, feed.SpkR * 2, spkGain);
                if (feed.HapL >= 0 && feed.HapR >= 0 && ch > Math.Max(feed.HapL, feed.HapR))
                {
                    var hring = _personaHapticRings.GetOrAdd(guid, _ => new PersonaHapticRing());
                    hring.WriteS16Pairs(span, stride, feed.HapL * 2, feed.HapR * 2);
                }
            }
        }

        /// <summary>Called from the reconcile's desired-state pass with
        /// the slot's current Sony pad GUIDs, so the pacing-thread
        /// callback never walks settings. Also starts/moves the mic
        /// capture to the first USB pad in the set.</summary>
        private static void RefreshPersonaTargets(int slot, List<(Guid Guid, string Path, bool IsBt, bool IsDs4)> pads)
        {
            if (!_personaFeeds.TryGetValue(slot, out var feed)) return;
            var guids = new Guid[pads.Count];
            for (int i = 0; i < pads.Count; i++) guids[i] = pads[i].Guid;
            var prior = feed.Targets;
            feed.Targets = guids;
            if (prior.Length != guids.Length)
                Engine.SdlDiagLog.WriteLine(
                    $"PERSONA targets slot={slot} count={guids.Length}"
                    + (guids.Length > 0 ? $" first={guids[0].ToString("N").Substring(0, 8)}" : ""));

            // Microphone source, in preference order: a USB-connected pad's
            // real capture endpoint (WASAPI, container-matched), else a BT
            // DualSense via the 0x31 HasMic Opus stream (TechAntohere
            // protocol dump). The DS4 has no BT mic lane here.
            Guid micPad = Guid.Empty; string micPath = null;
            foreach (var p in pads)
                if (!p.IsBt) { micPad = p.Guid; micPath = p.Path; break; }
            if (micPad != feed.MicPadGuid)
            {
                StopPersonaMic(feed);
                if (micPad != Guid.Empty) StartPersonaMic(feed, micPad, micPath);
            }

            Guid btMicPad = Guid.Empty; string btMicPath = null;
            if (EnableBtMic && micPad == Guid.Empty)
                foreach (var p in pads)
                    if (p.IsBt && !p.IsDs4) { btMicPad = p.Guid; btMicPath = p.Path; break; }
            if (btMicPad != feed.BtMicPadGuid)
            {
                StopBtMic(feed);
                if (btMicPad != Guid.Empty) StartBtMic(feed, btMicPad, btMicPath);
            }
        }

        /// <summary>Start the BT DualSense mic reader: a second synchronous
        /// HID handle on the pad, filtering input report 0x31 for the
        /// HasMic bit and Opus-decoding the fixed 71-byte mono packet at
        /// [3..73] (48 kHz, 480 samples, 10 ms). The composite's capture
        /// endpoint is stereo, so the mono decode is duplicated. The mic
        /// OPEN command itself is sent by the BT tick (ManageDs5MicOpen)
        /// through the sink's writer, keeping one write lane.</summary>
        private static void StartBtMic(PersonaFeed feed, Guid padGuid, string hidPath)
        {
            feed.BtMicStop = false;
            feed.BtMicRxFrames = 0;
            feed.BtMicPadGuid = padGuid;
            var th = new System.Threading.Thread(() => BtMicLoop(feed, hidPath))
            {
                IsBackground = true,
                Name = "PersonaBtMic",
                Priority = System.Threading.ThreadPriority.AboveNormal,
            };
            feed.BtMicThread = th;
            th.Start();
            Engine.SdlDiagLog.WriteLine("PERSONA mic bt-reader start pad=" + padGuid.ToString("N").Substring(0, 8));
        }

        private static void StopBtMic(PersonaFeed feed)
        {
            if (feed.BtMicThread == null) { feed.BtMicPadGuid = Guid.Empty; return; }
            feed.BtMicStop = true;
            var h = feed.BtMicHandle;
            feed.BtMicHandle = IntPtr.Zero;
            // Send the mic CLOSE on the reader's own handle BEFORE closing
            // it. The BT tick's close path needs a live sink, and a slot
            // switched to a non-composite profile tears the sink down
            // before a tick can run, leaving the pad's mic session latched
            // open (observed 2026-07-31: a slot switch orphaned the
            // session and wedged the pad until power-cycle). The reader's
            // handle is still valid here and a synchronous WriteFile of
            // the 142-byte close report needs nothing from the sink.
            if (h != IntPtr.Zero)
            {
                try
                {
                    var close = new byte[Ds5HapticBtReportSize];
                    close[0] = 0x32;
                    close[2] = 0x11 | 0x80;
                    close[3] = 7;
                    close[4] = 0xFE;   // close
                    close[9] = 0xFF;
                    close[11] = 0x12 | 0x80;
                    close[12] = 64;
                    uint crc = Crc32(close, Ds5HapticBtReportSize - 4);
                    close[Ds5HapticBtReportSize - 4] = (byte)(crc & 0xFF);
                    close[Ds5HapticBtReportSize - 3] = (byte)((crc >> 8) & 0xFF);
                    close[Ds5HapticBtReportSize - 2] = (byte)((crc >> 16) & 0xFF);
                    close[Ds5HapticBtReportSize - 1] = (byte)(crc >> 24);
                    NativeMethods.WriteFileSyncBestEffort(h, close, Ds5HapticBtReportSize);
                    Engine.SdlDiagLog.WriteLine("PERSONA mic CLOSE sent (reader handle, stop path)");
                }
                catch { }
                NativeMethods.CloseHandle(h);
            }
            feed.BtMicThread = null;
            feed.BtMicPadGuid = Guid.Empty;
        }

        private static void BtMicLoop(PersonaFeed feed, string hidPath)
        {
            IntPtr h = NativeMethods.OpenHidSync(hidPath);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Engine.SdlDiagLog.WriteLine("PERSONA mic bt-reader open FAILED");
                return;
            }
            feed.BtMicHandle = h;
            // STEREO, per the wire, not the protocol dump. Every mic frame
            // carries Opus TOC 0xD4: config 26 (CELT super-wideband, 10 ms),
            // stereo bit SET, one frame per packet. Observed stable across
            // thousands of frames (tocVary=0). TechAntohere's dump calls the
            // mic "48 kHz mono"; the DualSense actually encodes stereo, and
            // a mono decoder fed a stereo packet yields noise. This hid
            // until the pad was unmuted because silence decodes to zeros
            // through either decoder.
            var dec = OpusCodecFactory.CreateDecoder(Rate, BtMicChannels);
            var report = new byte[547]; // BT DS5 input caps length; 0x31 arrives in the first 78
            var pcm = new short[BtMicFrameSamples * BtMicChannels];
            var outBuf = new byte[BtMicFrameSamples * 4];
            long lastLog = 0;
            while (!feed.BtMicStop)
            {
                if (!NativeMethods.ReadFileSync(feed.BtMicHandle, report, report.Length, out int got) || got < 78)
                {
                    if (feed.BtMicStop) break;
                    System.Threading.Thread.Sleep(50);
                    continue;
                }
                if (report[0] != 0x31) continue;
                if ((report[1] & 0x02) == 0)
                {
                    // A plain state report (no mic payload). Sample the
                    // pad's audio status byte while we have it: duaLib
                    // dataStructures.h /*53.0*/ PluggedHeadphones,
                    // /*53.1*/ PluggedMic, /*53.2*/ MicMuted ("muted by
                    // powersave/mute command"). Packet starts at data[2]
                    // on BT, so packet 53 is report[55].
                    if (got >= 56)
                    {
                        _btMicPadStatus = report[55];
                        // Feed the Follow Headphone Jack route (bit 0 =
                        // PluggedHeadphones). The resolver + the
                        // dispatcher's change gating do the re-arm.
                        NoteHeadphoneJack(feed.BtMicPadGuid, (report[55] & 0x01) != 0);
                    }
                    continue;
                }
                // Idle skip. With no consumer draining the capture
                // endpoint the HM ring saturates, and every frame we
                // decode is then thrown away by the whole-block guard
                // further down. Measured idling on hardware:
                // blocksDropped=13846 of ~15000 received, so ~92% of
                // the Opus decodes plus their gain, interleave and RMS
                // passes were pure waste at 100 frames/s. That is free
                // on a desktop and is not free on the Atom x5-Z8350
                // floor this app supports.
                //
                // Deliberately NOT gated on IsStreaming alone. If the
                // ring has room we decode, so a consumer that opens the
                // endpoint mid-stream is served immediately rather than
                // waiting for the next status poll. And if IsStreaming
                // were ever wrong, a real drain empties the ring and
                // MicSubmitFits goes true, so the skip self-corrects
                // instead of silencing the lane.
                //
                // The reader loop itself keeps running either way: the
                // session stays open and the pad's audio status byte
                // above stays fresh. Only the decode is skipped.
                var mic = feed.Audio.Microphone;
                int outCh = Math.Max(1, mic.Channels);
                if (!mic.IsStreaming
                    && !_micGuardDisabled
                    && !MicSubmitFits(mic.BufferedBytes, BtMicFrameSamples * outCh * 2))
                {
                    // Count the frame anyway. It arrived, we simply chose
                    // not to decode it, and rxFrames is how a reader tells
                    // a live lane from a dead one.
                    feed.BtMicRxFrames++;
                    _micBlocksDropped++;
                    _micDecoderStale = true;
                    // The periodic report below sits after the decode, so
                    // skipping it would take the whole mic lane dark in the
                    // log for as long as no consumer is listening. Emit the
                    // idle heartbeat on the same cadence instead.
                    long nowIdle = Environment.TickCount64;
                    if (nowIdle - lastLog >= 2000)
                    {
                        lastLog = nowIdle;
                        Engine.SdlDiagLog.WriteLine("PERSONA mic IDLE decode skipped (no consumer)"
                            + " rxFrames=" + feed.BtMicRxFrames
                            + " blocksDropped=" + _micBlocksDropped
                            + " buffered=" + mic.BufferedBytes + "/" + HmMicRingBytes
                            + " padMuted=" + ((_btMicPadStatus & 0x04) != 0));
                    }
                    continue;
                }

                // Opus carries state across packets, so resuming after a
                // skipped run must not feed the decoder a stream with a
                // hole in it. Reset once on resume and start clean.
                if (_micDecoderStale) { dec.ResetState(); _micDecoderStale = false; }

                int n;
                try { n = dec.Decode(report.AsSpan(3, BtMicPayloadBytes), pcm.AsSpan(), BtMicFrameSamples, false); }
                catch { continue; }
                if (n <= 0) continue;
                feed.BtMicRxFrames++;
                int samples = n * BtMicChannels;
                int peak = 0; long sumSq = 0;
                for (int i = 0; i < samples; i++) { int a = pcm[i]; if (a < 0) a = -a; if (a > peak) peak = a; sumSq += (long)pcm[i] * pcm[i]; }
                if (peak > _btMicPeak) _btMicPeak = peak;
                // Decode-correctness probe: the Opus TOC (first payload
                // byte) encodes config/stereo/frame-count and is near
                // constant for a fixed-format stream. A stable TOC means
                // the 71-byte frame is being read at the right offset; a
                // scattered TOC means we are feeding the decoder bytes
                // that are not the start of an Opus packet, which decodes
                // as noise while silence frames still decode to zeros.
                _btMicToc = report[3];
                if (report[3] != _btMicTocFirst) { if (_btMicTocFirst == 0xFFFF) _btMicTocFirst = report[3]; else _btMicTocVary++; }
                _btMicRmsAcc += sumSq / Math.Max(1, samples);
                _btMicRmsCount++;
                float gain = feed.MicMuted ? 0f : feed.MicGain;
                // The composite's capture endpoint is 2 ch / 48 kHz, the
                // same shape the pad encodes, so stereo passes straight
                // through. A mono endpoint gets the channel average.
                for (int i = 0; i < n; i++)
                {
                    if (outCh >= BtMicChannels)
                    {
                        for (int c = 0; c < outCh; c++)
                        {
                            int src = i * BtMicChannels + Math.Min(c, BtMicChannels - 1);
                            short s16 = (short)Math.Clamp(pcm[src] * gain, short.MinValue, short.MaxValue);
                            int o = (i * outCh + c) * 2;
                            outBuf[o] = (byte)s16;
                            outBuf[o + 1] = (byte)(s16 >> 8);
                        }
                    }
                    else
                    {
                        int mix = (pcm[i * BtMicChannels] + pcm[i * BtMicChannels + 1]) / 2;
                        short s16 = (short)Math.Clamp(mix * gain, short.MinValue, short.MaxValue);
                        outBuf[i * 2] = (byte)s16;
                        outBuf[i * 2 + 1] = (byte)(s16 >> 8);
                    }
                }
                // Bisect probe (PADFORGE_MICTONE=1): replace the decoded
                // capture with a known 440 Hz half-scale sine. If Windows
                // then receives a clean tone, our submit path and HM are
                // sound and the fault is upstream in the decode. If it
                // still receives noise, the corruption is below Submit.
                if (_micToneProbe)
                {
                    for (int i = 0; i < n; i++)
                    {
                        short s16 = (short)(Math.Sin(_micTonePhase) * _micToneAmp);
                        _micTonePhase += 2 * Math.PI * 440.0 / Rate;
                        if (_micTonePhase > 2 * Math.PI) _micTonePhase -= 2 * Math.PI;
                        for (int c = 0; c < outCh; c++)
                        {
                            int o = (i * outCh + c) * 2;
                            outBuf[o] = (byte)s16;
                            outBuf[o + 1] = (byte)(s16 >> 8);
                        }
                    }
                }
                // Measure EXACTLY what leaves us, post-gain and post-
                // interleave, so the submitted bytes can be compared with
                // a consumer-side capture without inference.
                int subBytes = n * outCh * 2;
                long subSq = 0; int subPeak = 0;
                for (int i = 0; i + 1 < subBytes; i += 2)
                {
                    short v = (short)(outBuf[i] | (outBuf[i + 1] << 8));
                    int a = v < 0 ? -v : v;
                    if (a > subPeak) subPeak = a;
                    subSq += (long)v * v;
                }
                _subRmsAcc += subSq / Math.Max(1, subBytes / 2);
                _subRmsCount++;
                if (subPeak > _subPeak) _subPeak = subPeak;
                // Submit WHOLE blocks only. HM's mic ring truncates a submit
                // to its free byte count, and that count is computed as
                // (capacity - 1 - buffered), so it can be ODD. A partial
                // copy ending mid-frame misaligns the ring permanently:
                // every later sample is then read one byte off, the low
                // byte becomes the high byte, and quiet audio arrives as a
                // full-scale sawtooth. Diagnosed 2026-07-31 by submitting a
                // known 440 Hz sine at amplitude 1000 and reading the
                // captured samples back: a ramp stepping +0.445 per sample
                // and wrapping at +/-1, which is exactly our per-sample
                // delta (57 units) promoted by 256. Dropping a whole block
                // costs 10 ms of capture and keeps the stream aligned;
                // letting it truncate costs every sample thereafter.
                // HM v1.4.1 (HM#41) fixed the ring-side truncation, so this
                // guard is redundant against that build and later. Kept as
                // defense in depth: it costs one comparison per 10 ms block
                // and it protects against an older SDK being dropped in.
                // PADFORGE_MICNOGUARD=1 disables it, which is how HM's fix
                // was verified here rather than merely assumed.
                if (_micGuardDisabled || MicSubmitFits(mic.BufferedBytes, subBytes))
                {
                    mic.Submit(outBuf.AsSpan(0, subBytes));
                }
                else
                {
                    _micBlocksDropped++;
                }
                long now2 = Environment.TickCount64;
                if (now2 - lastLog >= 2000)
                {
                    lastLog = now2;
                    int subRms = _subRmsCount > 0 ? (int)Math.Sqrt(_subRmsAcc / _subRmsCount) : 0;
                    Engine.SdlDiagLog.WriteLine("PERSONA mic blocksDropped=" + _micBlocksDropped
                        + " buffered=" + mic.BufferedBytes + "/" + HmMicRingBytes);
                    Engine.SdlDiagLog.WriteLine("PERSONA mic SUBMITTED rms=" + subRms
                        + " peak=" + _subPeak + "  (normalized rms=" + (subRms / 32768.0).ToString("F4") + ")");
                    _subRmsAcc = 0; _subRmsCount = 0; _subPeak = 0;
                    int rms = _btMicRmsCount > 0 ? (int)Math.Sqrt(_btMicRmsAcc / _btMicRmsCount) : 0;
                    _btMicRmsAcc = 0; _btMicRmsCount = 0;
                    Engine.SdlDiagLog.WriteLine("PERSONA mic rms=" + rms + " peak=" + _btMicPeak
                        + " toc=0x" + _btMicToc.ToString("X2")
                        + " tocFirst=0x" + (_btMicTocFirst == 0xFFFF ? 0 : _btMicTocFirst).ToString("X2")
                        + " tocVary=" + _btMicTocVary);
                    byte st = _btMicPadStatus;
                    Engine.SdlDiagLog.WriteLine("PERSONA mic padMuted=" + ((st & 0x04) != 0)
                        + " padMicPlugged=" + ((st & 0x02) != 0)
                        + " padHeadphones=" + ((st & 0x01) != 0)
                        + " statusByte=0x" + st.ToString("X2"));
                    Engine.SdlDiagLog.WriteLine("PERSONA mic rxFrames=" + feed.BtMicRxFrames
                        + " buffered=" + mic.BufferedBytes
                        + " hostStreaming=" + mic.IsStreaming
                        + " peak=" + _btMicPeak
                        + " gain=" + (feed.MicMuted ? 0f : feed.MicGain).ToString("F2"));
                    _btMicPeak = 0;
                }
            }
            var hh = feed.BtMicHandle;
            feed.BtMicHandle = IntPtr.Zero;
            if (hh != IntPtr.Zero) NativeMethods.CloseHandle(hh);
        }

        /// <summary>Find the feed whose BT mic source is this pad. Sinks are
        /// few and this runs once per 10.667 ms tick; a scan is fine.</summary>
        /// <summary>BT DualSense mic: DISABLED until the SDL fork filters
        /// non-HID 0x31 reports. Opening the mic makes the pad interleave
        /// 0x31 reports whose payload is a 71-byte Opus packet in place of
        /// controller state (header bit0 = HasHID, bit1 = HasMic), and
        /// SDL's DS5 driver parses those bytes as sticks/buttons: erratic
        /// input on the physical pad, observed on hardware 2026-07-31. The
        /// whole decode chain below is hardware-proven (OPEN ack 45 ms,
        /// 100 frames/s, real audio) and comes back when the fork skips
        /// HasMic reports. Until then the tick scrubs any latched mic-open
        /// state instead.
        ///
        /// RE-ENABLED 2026-07-31: the SDL fork filters HasMic 0x31 reports
        /// out of state parsing (hifihedgehog/SDL#20, fork cec3689a12),
        /// so the mic session no longer corrupts input. The close scrub
        /// stays as hygiene for pads left open by older builds.
        ///
        /// GATED AGAIN 2026-07-31 (same evening): Windows receives
        /// FULL-SCALE NOISE from the composite's capture endpoint. Proven
        /// to be below this code with a known-tone bisect: a clean 440 Hz
        /// half-scale sine submitted to HMMicrophoneInput.Submit arrives
        /// at WASAPI as peak 0.998 / rms 0.590 / 11.8% near full scale.
        /// Our decode is not implicated. Located in HM's ISO IN reply
        /// (UsbipServer.SendRetSubmitIso): the IN payload is packed
        /// COMPACTED at perPacketActual stride while each returned
        /// descriptor echoes the host's ORIGINAL offset (i * 196 for this
        /// endpoint's wMaxPacketSize), so for any URB with more than one
        /// packet the client reads the tail packets out of buffer regions
        /// that were never written. Left off until that contract is
        /// resolved: a dead mic beats one that blasts noise into a call.
        /// The decode path below is otherwise hardware-proven and flips
        /// back on with this one const.</summary>
        private const bool EnableBtMic = true;

        private static int _btMicPeak;
        /// <summary>Last audio-status byte seen on a plain state report
        /// from the BT mic pad (duaLib input offset 53).</summary>
        private static volatile byte _btMicPadStatus;
        /// <summary>DualSense BT mic frame shape: one 71-byte Opus packet
        /// per input report, CELT 10 ms.
        ///
        /// MONO, and do not "fix" this to stereo. The Opus TOC on every
        /// frame is 0xD4, whose stereo bit IS set, and reading that byte
        /// as authority is exactly the mistake made on 2026-07-31: the
        /// decoder was switched to stereo and Windows received full-scale
        /// noise. A mono decoder decoding a stereo-flagged packet is legal
        /// and yields a correct downmix, which is what this stream needs.
        /// Measured at the consumer (WASAPI probe on the composite's
        /// capture endpoint), same pad, same session:
        ///   stereo -> peak 1.0000, rms 0.5118, 6.5% near full scale
        ///   mono   -> peak 0.2207, rms 0.0208, 0% near full scale
        /// Change this constant only with a consumer-side measurement in
        /// hand, never from the TOC.</summary>
        internal const int BtMicChannels = 1;
        private const int BtMicFrameSamples = 480;   // 10 ms at 48 kHz
        private const int BtMicPayloadBytes = 71;

        private static readonly bool _micToneProbe =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PADFORGE_MICTONE"));
        private static double _micTonePhase;
        /// <summary>PADFORGE_MICTONE carries the amplitude, so transparency
        /// can be probed at the LEVEL real speech actually arrives at, not
        /// only at a loud one.</summary>
        private static readonly int _micToneAmp =
            int.TryParse(Environment.GetEnvironmentVariable("PADFORGE_MICTONE"), out var a) && a > 1 ? a : 16000;

        private static byte _btMicToc;
        private static int _btMicTocFirst = 0xFFFF, _btMicTocVary;
        private static long _btMicRmsAcc; private static int _btMicRmsCount;
        private static long _subRmsAcc; private static int _subRmsCount, _subPeak;
        private static long _micBlocksDropped;
        private static bool _micDecoderStale;
        private static readonly bool _micGuardDisabled =
            Environment.GetEnvironmentVariable("PADFORGE_MICNOGUARD") == "1";
        /// <summary>HM's microphone ring capacity: UsbAudioEngine sizes it
        /// micBytesPerInterval * 256, and the DualSense interval is
        /// 48 samples * 2 ch * 2 bytes = 192.</summary>
        internal const int HmMicRingBytes = 192 * 256;

        /// <summary>True when a whole block fits HM's mic ring, so the
        /// submit cannot be truncated mid-frame. HM computes its free
        /// space as (capacity - 1 - buffered) and silently copies only
        /// that many bytes, and the -1 makes the figure ODD, so a
        /// truncated submit ends mid-sample and misaligns the ring for
        /// good. Dropping a whole 10 ms block instead keeps every later
        /// sample aligned.</summary>
        internal static bool MicSubmitFits(int bufferedBytes, int blockBytes)
            => HmMicRingBytes - 1 - bufferedBytes >= blockBytes;

        private static PersonaFeed FindFeedForBtMicPad(Guid padGuid)
        {
            foreach (var kv in _personaFeeds)
                if (kv.Value.BtMicPadGuid == padGuid) return kv.Value;
            return null;
        }

        private static void StartPersonaMic(PersonaFeed feed, Guid padGuid, string hidPath)
        {
            try
            {
                Guid container = NativeMethods.GetContainerIdForDevicePath(hidPath);
                if (container == Guid.Empty) return;
                using var en = new MMDeviceEnumerator();
                MMDevice match = null;
                foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    if (GetEndpointContainerId(dev) == container) { match = dev; break; }
                if (match == null) return;

                var cap = new WasapiCapture(match);
                cap.DataAvailable += (_, a) =>
                {
                    var mic = feed.Audio.Microphone;
                    // Endpoint shared-mode float or s16 → the persona's
                    // declared s16 format. Rates match on the DualSense
                    // (48 kHz both sides); a mismatched pad is skipped
                    // rather than pitch-shifted.
                    if (cap.WaveFormat.SampleRate != mic.SampleRateHz) return;
                    float gain = feed.MicMuted ? 0f : feed.MicGain;
                    int inCh = cap.WaveFormat.Channels, outCh = mic.Channels;
                    // Shared-mode capture is the endpoint mix format:
                    // IeeeFloat directly, or Extensible wrapping the float
                    // subformat GUID (KSDATAFORMAT_SUBTYPE_IEEE_FLOAT).
                    bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                        || (cap.WaveFormat is WaveFormatExtensible wfx
                            && wfx.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
                    int inStride = inCh * (isFloat ? 4 : 2);
                    int frames = a.BytesRecorded / inStride;
                    int need = frames * outCh * 2;
                    if (feed.MicScratch.Length < need) feed.MicScratch = new byte[need];
                    for (int f = 0; f < frames; f++)
                    {
                        for (int c = 0; c < outCh; c++)
                        {
                            int ic = Math.Min(c, inCh - 1);
                            float v;
                            if (isFloat) v = BitConverter.ToSingle(a.Buffer, f * inStride + ic * 4);
                            else v = BitConverter.ToInt16(a.Buffer, f * inStride + ic * 2) / 32768f;
                            short s = (short)Math.Clamp(v * gain * 32767f, short.MinValue, short.MaxValue);
                            int o = (f * outCh + c) * 2;
                            feed.MicScratch[o] = (byte)s;
                            feed.MicScratch[o + 1] = (byte)(s >> 8);
                        }
                    }
                    mic.Submit(feed.MicScratch.AsSpan(0, need));
                };
                cap.StartRecording();
                feed.Mic = cap;
                feed.MicPadGuid = padGuid;
            }
            catch { StopPersonaMic(feed); }
        }

        private static void StopPersonaMic(PersonaFeed feed)
        {
            var cap = feed.Mic;
            feed.Mic = null;
            feed.MicPadGuid = Guid.Empty;
            if (cap == null) return;
            try { cap.StopRecording(); cap.Dispose(); } catch { }
        }

        /// <summary>Requests a sink reconcile and returns immediately. Call
        /// on device assignment changes and passthrough toggle changes.
        /// Safe from the UI thread: the worker does all device I/O. The
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
            // Remote-audio bookkeeping dies with the engine (restartable path):
            // a peer stream after the next start re-adds both entries.
            _remoteRings.Clear();
            _remoteAudioDemand.Clear();
            _workSignal.Set();
            foreach (var s in drop) DisposeTransport(s);
            foreach (var c in caps) StopCaptureEntry(c);
        }

        // ─────────────────────────────────────────────
        //  Sink lifecycle
        // ─────────────────────────────────────────────

        // A peer sink has no local transport; it's "alive" by virtue of being a
        // network shipper (its mix is pulled by the stream thread's peer lane).
        private static bool SinkAlive(Sink s) => s.Player != null || s.BtHandle != new IntPtr(-1) || s.IsPeer;

        /// <summary>Whether a slot's CURRENT macro configuration contains
        /// any PlaySound action. Wired by InputService to read the
        /// engine's MacroSnapshots (atomically swapped, safe from this
        /// worker). Demand is derived from this on every reconcile pass
        /// instead of latched at play time: the old HashSet latch was
        /// add-only, so one macro sound put the slot's transport into a
        /// keep-alive set for the rest of the process, surviving the
        /// macro's deletion and the device's unassignment. Config-derived
        /// demand keeps the property the latch was protecting (sinks
        /// persist across reconnects while a sound macro EXISTS) and adds
        /// the teardown it was missing, plus pre-building the transport so
        /// the first trigger doesn't fall into the pendingActivation drop.</summary>
        internal static Func<int, bool> SlotWantsMacroAudioProvider;

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
            var desired = new List<(int Slot, Guid Guid, string Path, bool IsBt, bool IsDs4, bool PtOn, string MirrorSrc, bool RemoteFed, bool IsPeer)>();
            for (int slot = 0; slot < MaxPads; slot++)
            {
                // Demand for macro audio is config-derived, never latched:
                // read outside _lock (the provider walks the engine's own
                // snapshot and takes no locks of ours).
                bool demand = SlotWantsMacroAudioProvider?.Invoke(slot) ?? false;
                // Persona demand: a composite VC on the slot builds its
                // pads' transports even with passthrough off and no
                // macros, exactly like the remote-audio demand. The same
                // walk refreshes the feed's target list so the pacing-
                // thread callback never touches settings.
                bool personaDemand = _personaFeeds.ContainsKey(slot);
                var personaPads = personaDemand ? new List<(Guid Guid, string Path, bool IsBt, bool IsDs4)>() : null;
                foreach (var (guid, ud) in EnumerateAssignedSonyPads(slot))
                {
                    var (ptOn, mirrorSrc) = ReadPassthroughConfig(slot, guid);
                    // Remote output relay (#138): a "peer://" pad lives on another PC, so
                    // it has NO local transport. Build the SAME per-pad sink a local Sony
                    // pad gets — MacroMixer (test tone + slot macros) + passthrough Capture
                    // + SinkSource — but mark it IsPeer so its "transport" is the network
                    // shipper (the stream thread's peer lane pulls SinkSource.Read and ships
                    // the mix to the owner, who re-renders to the real speaker). Gate
                    // identically to a local pad: passthrough on OR the slot's macros demand.
                    if ((ud.DevicePath ?? "").StartsWith("peer://", StringComparison.Ordinal))
                    {
                        if (!ptOn && !demand && !personaDemand) continue;
                        // A peer pad on a composite slot still receives the
                        // persona speaker mix (SinkSource reads the ring),
                        // shipped over the peer lane. Marked BT-shaped so
                        // the mic capture never binds to it.
                        // Peer pads are excluded from every mic role.
                        personaPads?.Add((guid, ud.DevicePath, true, true));
                        desired.Add((slot, guid, ud.DevicePath, false, false, ptOn, mirrorSrc, false, true));
                        continue;
                    }
                    bool isBt = (ud.DevicePath ?? "").IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isDs4 = Ds4Pids.Contains((ushort)ud.ProdId);
                    // DS4 audio is BLUETOOTH-ONLY: the wired DS4 exposes no
                    // USB audio interface at all (ds4mac docs §3.1 — HID
                    // endpoints only, no UAC descriptors). BT DS4 streams SBC
                    // over report 0x17. Exception: Sony's USB wireless
                    // adaptor (PID 0x0BA0) tunnels the radio link and exposes
                    // real UAC endpoints, so it keeps the USB container path.
                    if (isDs4 && !isBt && (ushort)ud.ProdId != 0x0BA0) continue;
                    // A sink exists while the device's mirror toggle is on,
                    // the slot's macros have asked for controller routing,
                    // or the slot's VC is a composite persona feeding audio.
                    // Pads using none get no transport and no firmware
                    // speaker-path assertion.
                    if (!ptOn && !demand && !personaDemand) continue;
                    personaPads?.Add((guid, ud.DevicePath, isBt, isDs4));
                    desired.Add((slot, guid, ud.DevicePath, isBt, isDs4, ptOn, mirrorSrc, false, false));
                }
                if (personaDemand) RefreshPersonaTargets(slot, personaPads);
            }

            // Owner: a paired peer is streaming speaker audio for one of OUR physical
            // pads. Build a sink whose PCM source is the network ring; the real device's
            // BT/USB transport renders it. Demand expires ~2 s after the audio stops.
            long nowDemand = Environment.TickCount64;
            foreach (var kv in _remoteAudioDemand)
            {
                if (nowDemand - kv.Value > 2000)
                {
                    // Prune, don't just skip: neither dictionary had any removal
                    // path, so BT re-pair guid churn grew orphans for the process
                    // lifetime. The conditional pair-remove keys on the exact
                    // observed timestamp so a demand FeedRemoteAudio just refreshed
                    // survives; a resumed stream re-adds via GetOrAdd. A Sink still
                    // holding the ring keeps it alive until its own teardown.
                    if (((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<Guid, long>>)_remoteAudioDemand)
                            .Remove(kv))
                        _remoteRings.TryRemove(kv.Key, out _);
                    continue;
                }
                var ud = SettingsManager.FindDeviceByInstanceGuid(kv.Key);
                if (ud == null || !ud.IsOnline || string.IsNullOrEmpty(ud.DevicePath)) continue;
                if ((ud.DevicePath ?? "").StartsWith("peer://", StringComparison.Ordinal)) continue;
                bool isBt = ud.DevicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isDs4 = Ds4Pids.Contains((ushort)ud.ProdId);
                if (isDs4 && !isBt && (ushort)ud.ProdId != 0x0BA0) continue;
                desired.Add((255, kv.Key, ud.DevicePath, isBt, isDs4, false, "", true, false));
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
                    // Transport-shape change (BT <-> USB reconnect of the SAME
                    // device identity, or a new HID path after re-pair). IsBt
                    // was only ever set at CREATION, so a pad that moved from
                    // Bluetooth to USB kept IsBt=true and the rebuild opened a
                    // BT write lane against a USB HID path: 0x35 reports at an
                    // interface that wants WASAPI, no audio until an app
                    // restart cleared the static sink map. Same latch family
                    // as the slot-reassign identity bug (9abc1467): state
                    // scoped "for the sink's life" with no answer for the
                    // device's transport changing under it. Owner-reported
                    // 2026-08-01 ("had to close and reopen PadForge for the
                    // DualSense to work over USB after Bluetooth").
                    bool transportShapeChanged = !d.IsPeer
                        && (sink.IsBt != d.IsBt
                            || !string.Equals(sink.HidPath, d.Path, StringComparison.OrdinalIgnoreCase));
                    sink.Slot = d.Slot;
                    sink.HidPath = d.Path;
                    sink.IsBt = d.IsBt;
                    sink.IsDs4 = d.IsDs4;
                    sink.PassthroughOn = d.PtOn;
                    sink.MirrorSourceId = d.MirrorSrc ?? "";
                    sink.RemoteFed = d.RemoteFed;
                    if (d.RemoteFed) sink.Remote = _remoteRings.TryGetValue(d.Guid, out var rr) ? rr : null;
                    // Set before the SinkAlive check below: a peer sink is "alive" with no
                    // transport, so it's never queued for a BT/USB build (toBuild).
                    sink.IsPeer = d.IsPeer;
                    if (sink.TransportFailed || (transportShapeChanged && SinkAlive(sink) && !sink.IsPeer))
                    {
                        // Detach clears the transport, so the SinkAlive check
                        // below queues the rebuild on the NEW shape this pass.
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
            var expiredTestSlots = new List<int>();
            lock (_lock)
            {
                foreach (var s in _sinks.Values)
                    if (SinkAlive(s) && (uint)s.Slot < MaxPads) routed[s.Slot] = true;

                // Vendor-audio-test expiry sweep: an abandoned test (tab
                // closed mid-tone) leaves the pad's mirror routing in the
                // restored headphone state with nothing re-dispatching the
                // assert — WantsSpeakerPath flips back silently but the
                // effects dispatcher only writes on events. Drop expired
                // entries here and nudge their slots below, outside the
                // lock.
                long now = Environment.TickCount64;
                foreach (var kv in _vendorAudioTests.ToList())
                {
                    if (now < kv.Value) continue;
                    _vendorAudioTests.Remove(kv.Key);
                    if (_sinks.TryGetValue(kv.Key, out var sink) && (uint)sink.Slot < MaxPads)
                        expiredTestSlots.Add(sink.Slot);
                }
            }
            for (int slot = 0; slot < MaxPads; slot++)
                SoundMacroService.SetSlotControllerRouted(slot, routed[slot]);
            foreach (int slot in expiredTestSlots.Distinct())
                UserEffectsDispatcher.NotifySoundRoutingChanged(slot);
        }

        /// <summary>Move a sink's transport onto a carrier so it can be
        /// disposed outside the lock; flags the headphone-path restore.</summary>
        private static Sink DetachTransport_NoLock(Sink s)
        {
            // A peer sink has no local firmware speaker path to restore — its guid is
            // a remote device the local DS5 effects dispatcher never touches.
            if (SinkAlive(s) && !s.IsPeer) _speakerPathCleared.Add(s.DeviceGuid);
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
                    var feed = new UsbFrameProvider(s.Source, feedFormat, s.DeviceGuid);
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
        // Ring-cushion drift trim, shared by the BT lanes and the peer ship lane:
        // steer the loopback cursor to a steady cushion by consuming a few frames
        // more/fewer per tick (inaudible ±0.8 % rate trim), never by skipping ticks.
        private const int BtTargetLag = 960;   // 20 ms ring cushion @ 48 kHz
        private const int LagDeadband = 240;   // ±5 ms before trimming

        // DualSense: one Opus frame per tick in a report 0x35, hard CBR so
        // every frame fills the 0x13 speaker-lane slot exactly.
        private const int Ds5OpusFrameSamples = 480;   // Opus frame samples per channel
        private const int Ds5OpusBytes = 200;          // hard-CBR frame size (160 kbps)
        private const int Ds5BtReportSize = 334;       // report 0x35 wire size

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
                var hapticPcm = new short[Ds5HapticFramesPerTick * 2];
                var hapticReport = new byte[Ds5HapticBtReportSize];
                const double CadenceMs = 10.0 + 2.0 / 3.0;
                // 20 ms cushion: just enough to absorb WASAPI loopback's
                // ~10 ms bursty delivery, bringing the mirror within ~15 ms
                // of the macro path (owner request 2026-06-12). The original
                // 45 ms was chosen mid-dropout-war, before the async write
                // pool / high-res timer / skip-not-burst fixes removed the
                // sender-side jitter it was also covering for.
                long cadTicks = (long)(CadenceMs * TimeSpan.TicksPerMillisecond);
                long next = DateTime.UtcNow.Ticks + cadTicks;
                var me = Thread.CurrentThread;

                // Reused scratch: the LINQ Where().ToList() pair allocated
                // an enumerator + list ~94 times a second on this
                // Highest-priority thread even with a stable sink set.
                var btSinks = new List<Sink>();
                var peerSinks = new List<Sink>();
                var minusOne = new IntPtr(-1);
                while (_running && ReferenceEquals(_btThread, me))
                {
                    btSinks.Clear();
                    lock (_lock)
                        foreach (var s in _sinks.Values)
                            if (s.IsBt && !s.TransportFailed && s.BtHandle != minusOne
                                && !VendorAudioTestActive_NoLock(s.DeviceGuid))
                                btSinks.Add(s);

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

                            // Authored haptics (HM#39): shipped ahead of the
                            // speaker idle gate, since a game can drive the
                            // actuators while the speaker mix is silent. A
                            // no-op when the slot has no composite persona
                            // or the ring lacks a whole tick.
                            if (!s.IsDs4)
                            {
                                ManageDs5MicOpen(s, hapticReport);
                                SendDs5BtHapticFrame(s, hapticPcm, hapticReport);
                            }

                            // Idle gate: after 2 s of silence stop sending so the
                            // pad's radio and our CPU rest; the read above keeps
                            // the ring cursor live and the activity stamp fresh.
                            bool audible = Environment.TickCount64 - s.LastAudibleTicks <= 2000;
                            _personaLastAudible = audible;
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
                            _personaSpkSends++;
                        }
                        catch
                        {
                            s.TransportFailed = true;
                        }
                    }

                    // Consumer network lane (#138): peer:// pads have no local
                    // transport, so the same per-pad mix (test tone + slot macros +
                    // system passthrough) is pulled here and shipped to the owner,
                    // who re-renders it to the real pad speaker. Ships continuously —
                    // silence included — at the same 48 kHz pull as the BT lanes, so
                    // the owner's ring stays primed and the next sound starts gapless;
                    // the owner's own idle gate rests the radio when the audio is silent.
                    peerSinks.Clear();
                    if (_running)
                        lock (_lock)
                            foreach (var s in _sinks.Values)
                                if (s.IsPeer) peerSinks.Add(s);
                    foreach (var s in peerSinks)
                    {
                        try { ShipPeerAudioTick(s, pull); }
                        catch { /* one bad tick never kills the lane */ }
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

        /// <summary>Consumer (#138): pull one tick of a peer pad's per-pad mix (test
        /// tone + slot macros + system passthrough) and ship it as s16 48 kHz stereo
        /// in sub-MTU 1024 B blocks to the owner. Full-scale PCM — speaker VOLUME is a
        /// firmware byte the OWNER's effects dispatcher asserts (WantsSpeakerPath),
        /// matching the local model where volume never lives in the samples.</summary>
        private static void ShipPeerAudioTick(Sink s, float[] pull)
        {
            // Same drift trim as the BT lanes: steer the consumer's loopback cursor to
            // a steady cushion by consuming a few frames more/fewer, never by skipping.
            int inFrames = BtPullFrames;
            int lag = s.Source.LoopbackLagFrames;          // -1 when passthrough is off
            if (lag >= 0)
            {
                if (lag > BtTargetLag + LagDeadband) inFrames += 4;
                else if (lag < BtTargetLag - LagDeadband) inFrames -= 4;
            }

            s.Source.Read(pull, 0, inFrames * 2);          // macros + test tone + passthrough

            // float -> s16 LE into a per-sink carry, flushed in exact 1024 B blocks.
            s.ShipBuf ??= new byte[(BtPullFrames + 8) * 2 * 2 + RemoteAudioBlockBytes];
            int n = inFrames * 2;
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp((int)(pull[i] * 32767f), short.MinValue, short.MaxValue);
                s.ShipBuf[s.ShipCount++] = (byte)v;
                s.ShipBuf[s.ShipCount++] = (byte)(v >> 8);
            }

            int off = 0;
            while (s.ShipCount - off >= RemoteAudioBlockBytes) // 1024 B = 256 frames; +30 B seal < MTU
            {
                var block = new byte[RemoteAudioBlockBytes];
                Buffer.BlockCopy(s.ShipBuf, off, block, 0, RemoteAudioBlockBytes);
                RemoteLinkOutputRouter.ShipAudio(s.HidPath, block);
                off += RemoteAudioBlockBytes;
            }
            if (off > 0)
            {
                Buffer.BlockCopy(s.ShipBuf, off, s.ShipBuf, 0, s.ShipCount - off);
                s.ShipCount -= off;
            }
        }

        /// <summary>The BT audio lane a path plays through. Over Bluetooth
        /// the sink is addressed by PACKET ID, not by OutputPathSelect:
        /// 0x13 is the internal speaker, 0x16 the headset jack
        /// (dualsense-bt-haptics HeadsetPlayMusic Program.cs:55, "Speaker:
        /// 0x13 Headset: 0x16"; that reference sends no path register at
        /// all, so the pid alone routes). PadForge hardcoded 0x13, which is
        /// why every headphone path was speaker-only over BT.
        /// Owner-reported 2026-08-01.</summary>
        internal static byte Ds5BtAudioLanePid(int outputPath) => outputPath switch
        {
            1 => 0x16,   // StereoHeadset
            2 => 0x16,   // MonoHeadset
            3 => 0x16,   // HeadsetAndSpeaker: headset lane, speaker lane added below
            _ => 0x13,   // Default / SpeakerOnly
        };

        /// <summary>Headset + Speaker over BT rides the HEADSET lane only,
        /// plus the OutputPathSelect register (path 2, L_L_R) the dispatcher
        /// already writes. The first cut sent the same frame on BOTH lanes,
        /// two 0x35 reports per tick sharing one seq counter, so each
        /// lane's stream saw +2 sequence jumps every tick. That is the
        /// documented warble signature from the 2026-07-31 bring-up ("the
        /// firmware drops/garbles on seq jumps, and a discontinuous Opus
        /// stream decodes as WARBLE"), and the owner heard exactly that:
        /// "garbled with headphone+speaker". Same mistake, one level down.
        /// One report id gets ONE stream, full stop.</summary>
        internal static bool Ds5BtWantsBothLanes(int outputPath) => false;

        /// <summary>Folds a stereo frame to mono in place, for the mono
        /// paths (both ears the same, and the split path's speaker copy).</summary>
        internal static void FoldFrameToMono(float[] frame)
        {
            for (int i = 0; i + 1 < frame.Length; i += 2)
            {
                float m = Math.Clamp((frame[i] + frame[i + 1]) * 0.5f, -1f, 1f);
                frame[i] = m;
                frame[i + 1] = m;
            }
        }

        /// <summary>Encode one 10 ms frame from <paramref name="pull"/> and
        /// send it as a 0x35 report on the lane the device's output path
        /// selects (speaker, headset, or both).</summary>
        private static void SendDs5BtFrame(Sink s, float[] pull, byte[] opus, byte[] report)
        {
            int outPath = 0;
            try { outPath = DeviceAudioOutputPathProvider?.Invoke(s.DeviceGuid) ?? 0; }
            catch { }
            // Mono headset plays both ears the same; the split path keeps
            // headset and speaker coherent by sharing one mono frame.
            if (outPath == 2 || outPath == 3) FoldFrameToMono(pull);

            s.Ds5OpusEncoder ??= CreateDs5OpusEncoder();
            int n;
            try { n = s.Ds5OpusEncoder.Encode(pull.AsSpan(), Ds5OpusFrameSamples, opus.AsSpan(), Ds5OpusBytes); }
            catch { s.Ds5OpusEncoder = null; return; }

            Array.Clear(report, 0, report.Length);
            report[0] = 0x35;
            report[1] = (byte)((s.Ds5Seq & 0x0F) << 4);
            s.Ds5Seq = (s.Ds5Seq + 1) & 0x0F;
            // packet 0x11: session header (SAxense default, no handshake).
            // Byte 4 is the mic session command: 0xFE closes, 0xFF opens.
            // It MUST track the live mic session, because every audio
            // report carries this header and a steady 0xFE re-closes a mic
            // the persona just opened. Proven by PersonaVerify on
            // 2026-07-31: rendering audio silenced the capture endpoint
            // (rms 0.0556 -> 0.0000) until this followed Ds5MicOpen. The
            // TechAntohere dump left this as an open question and the
            // answer is that open is NOT latched.
            report[2] = 0x11 | 0x80;
            report[3] = 7;
            report[4] = Ds5MicSessionByte(s.Ds5MicOpen == 1);
            report[9] = 0xFF;
            report[10] = s.Ds5PktCounter++;
            // Audio lane packet: 0x13 speaker / 0x16 headset, one Opus
            // frame filling the slot.
            report[11] = (byte)(Ds5BtAudioLanePid(outPath) | 0x80);
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

        private const int Ds5HapticFramesPerTick = 512;  // 48 kHz frames per 10.667 ms tick
        internal const int Ds5HapticBtReportSize = 142;   // report 0x32 wire size (Sony BT: 0x31=78, +64 per ID)

        /// <summary>Ship one tick of authored haptics as its own report
        /// 0x32 carrying packets 0x11 + 0x12, the exact shape SAxense and
        /// dualsense-bt-haptics proved on hardware. Deliberately NOT
        /// folded into the 0x35 speaker report: no reference emits 0x12
        /// and 0x13 together, so that combination stays unused. 48 kHz
        /// stereo s16 → 3 kHz stereo s8 by 16-sample block mean then high
        /// byte, matching the references' resample-then-high-byte
        /// pipeline (dualsense-bt-haptics Program.cs:208, SAxense's
        /// ffmpeg -ar 3000 -f s8). Shares the sink's rolling seq and
        /// packet counter with the speaker report so the multiplexed
        /// transport sees one monotonic sequence.</summary>
        /// <summary>Mic session command carried in every audio report's
        /// packet 0x11 header (payload byte 0). 0xFF opens, 0xFE closes.
        /// It MUST follow the live session: mic-open is NOT latched, so a
        /// steady 0xFE in the speaker/haptic streams re-closes a mic the
        /// persona just opened. Found by PersonaVerify 2026-07-31, capture
        /// rms fell 0.0556 to 0.0000 the moment rendering started.</summary>
        internal static byte Ds5MicSessionByte(bool micOpen) => micOpen ? (byte)0xFF : (byte)0xFE;

        /// <summary>Pure builder for the report 0x32 haptics frame
        /// (packets 0x11 + 0x12), CRC included. Returns true when the
        /// decimated block carries signal. Extracted as a test seam so the
        /// wire format is pinned by PadForge.Tests: the per-stream
        /// sequence, the mic-session byte, the 16-block-mean s8
        /// decimation, and the CRC placement have each regressed once.
        /// Deterministic: no clock, no I/O, no shared state.</summary>
        internal static bool BuildDs5BtHapticReport(byte[] report, int seq, byte pktCounter,
                                                    bool micOpen, ReadOnlySpan<short> pcm)
        {
            Array.Clear(report, 0, Ds5HapticBtReportSize);
            report[0] = 0x32;
            report[1] = (byte)((seq & 0x0F) << 4);
            // packet 0x11: session header (SAxense default, no handshake).
            // This stream carries its OWN counter, never the speaker's.
            report[2] = 0x11 | 0x80;
            report[3] = 7;
            report[4] = Ds5MicSessionByte(micOpen);
            report[9] = 0xFF;
            report[10] = pktCounter;
            // packet 0x12: 64 bytes of s8 stereo 3 kHz actuator PCM,
            // decimated 16:1 from the 48 kHz tick by block mean.
            report[11] = 0x12 | 0x80;
            report[12] = 64;
            bool signal = false;
            for (int o = 0; o < 32; o++)
            {
                int accL = 0, accR = 0;
                int b = o * 16 * 2;
                for (int k = 0; k < 16; k++) { accL += pcm[b + k * 2]; accR += pcm[b + k * 2 + 1]; }
                byte l = unchecked((byte)Math.Clamp((accL / 16) >> 8, -128, 127));
                byte r = unchecked((byte)Math.Clamp((accR / 16) >> 8, -128, 127));
                report[13 + o * 2] = l;
                report[14 + o * 2] = r;
                if (l != 0 || r != 0) signal = true;
            }
            uint c = Crc32(report, Ds5HapticBtReportSize - 4);
            report[Ds5HapticBtReportSize - 4] = (byte)(c & 0xFF);
            report[Ds5HapticBtReportSize - 3] = (byte)((c >> 8) & 0xFF);
            report[Ds5HapticBtReportSize - 2] = (byte)((c >> 16) & 0xFF);
            report[Ds5HapticBtReportSize - 1] = (byte)(c >> 24);
            return signal;
        }

        /// <summary>Classifies a consumer-side capture measurement. Full
        /// scale noise shows a HIGH rms with a LOW crest factor, because
        /// randomized samples fill the range uniformly, whereas real
        /// capture stays peaky even when quiet. Shared shape with
        /// tools/PersonaVerify.</summary>
        internal static string ClassifyCapture(double rms, double crest)
            => rms < 0.0005 ? "silence"
             : (rms > 0.25 && crest < 6.0) ? "noise"
             : "audio";

        private static void SendDs5BtHapticFrame(Sink s, short[] pcm, byte[] report)
        {
            if (!_personaHapticRings.TryGetValue(s.DeviceGuid, out var ring)) return;
            if (ring.FramesAvailable < Ds5HapticFramesPerTick) return; // whole ticks only
            ring.ReadFrames(pcm, Ds5HapticFramesPerTick);

            bool signal = BuildDs5BtHapticReport(report, s.Ds5HapticSeq, s.Ds5HapticPktCounter,
                                                 s.Ds5MicOpen == 1, pcm);

            // Silence gate, mirror of the speaker lane's: keep a 2 s
            // hangover so short gaps stay continuous, then stop sending
            // entirely. The ring was already drained above, so silence
            // costs no radio and never interleaves with the 0x35 stream.
            long nowTicks = Environment.TickCount64;
            if (signal) s.Ds5HapticAudibleTicks = nowTicks;
            else if (nowTicks - s.Ds5HapticAudibleTicks > 2000) return;
            // The builder already stamped the CRC over the final bytes.
            s.Ds5HapticSeq = (s.Ds5HapticSeq + 1) & 0x0F;
            s.Ds5HapticPktCounter++;

            bool hardFail = false;
            bool sent = s.Tx != null && s.Tx.TrySend(s.BtHandle, report, out hardFail);
            if (!sent && hardFail) s.TransportFailed = true;
            _personaHapticSends++;
            long hnow = Environment.TickCount64;
            if (hnow - _personaHapticLastLog >= 2000)
            {
                _personaHapticLastLog = hnow;
                Engine.SdlDiagLog.WriteLine($"PERSONA bt-haptic sends={_personaHapticSends} lastSent={sent}");
            }
        }

        private static long _personaHapticSends, _personaHapticLastLog, _personaSpkSends;
        private static volatile bool _personaLastAudible;

        /// <summary>BT mic session state machine, one pass per tick. Sends
        /// the mic OPEN toggle when this pad is the persona feed's BT mic
        /// source, retries every 2 s (5 tries) until decoded frames arrive,
        /// and sends CLOSE when the role goes away. Report layout from the
        /// TechAntohere protocol dump: our 0x32 stream with the 0x11
        /// packet's first payload byte 0xFF (open) / 0xFE (close), a zero
        /// 0x12 haptic packet, CRC32. Hypothesis-under-test: whether the
        /// steady-state 0xFE in the audio reports re-closes an opened mic
        /// is unknown; the dump's own working stream carries 0xFE, which
        /// suggests open is latched.</summary>
        private static void ManageDs5MicOpen(Sink s, byte[] report)
        {
            var feed = FindFeedForBtMicPad(s.DeviceGuid);
            bool want = EnableBtMic && feed != null;
            long now = Environment.TickCount64;
            // Scrub: mic-open is LATCHED on the pad across app restarts,
            // and a latched mic corrupts SDL's input parsing (see
            // EnableBtMic). One unconditional CLOSE per sink lifetime
            // restores the pure-HID 0x31 stream; harmless when the mic
            // was never opened.
            if (!want && s.Ds5MicOpen == 0 && !s.Ds5MicCloseScrubbed)
            {
                SendDs5BtMicToggle(s, report, open: false);
                s.Ds5MicCloseScrubbed = true;
                Engine.SdlDiagLog.WriteLine("PERSONA mic CLOSE scrub sent");
                return;
            }
            if (want && s.Ds5MicOpen == 0)
            {
                SendDs5BtMicToggle(s, report, open: true);
                s.Ds5MicOpen = 1;
                s.Ds5MicOpenSentTicks = now;
                s.Ds5MicOpenTries = 1;
                Engine.SdlDiagLog.WriteLine("PERSONA mic OPEN sent");
            }
            else if (want && s.Ds5MicOpen == 1 && feed.BtMicRxFrames == 0
                     && now - s.Ds5MicOpenSentTicks >= 2000 && s.Ds5MicOpenTries < 5)
            {
                SendDs5BtMicToggle(s, report, open: true);
                s.Ds5MicOpenSentTicks = now;
                s.Ds5MicOpenTries++;
                Engine.SdlDiagLog.WriteLine("PERSONA mic OPEN retry " + s.Ds5MicOpenTries);
            }
            else if (!want && s.Ds5MicOpen == 1)
            {
                SendDs5BtMicToggle(s, report, open: false);
                s.Ds5MicOpen = 0;
                Engine.SdlDiagLog.WriteLine("PERSONA mic CLOSE sent");
            }
        }

        private static void SendDs5BtMicToggle(Sink s, byte[] report, bool open)
        {
            Array.Clear(report, 0, Ds5HapticBtReportSize);
            report[0] = 0x32;
            report[1] = 0x00;                       // per the dump, not a seq nibble
            report[2] = 0x11 | 0x80;
            report[3] = 7;
            report[4] = open ? (byte)0xFF : (byte)0xFE;
            report[9] = 0xFF;
            report[10] = s.Ds5HapticPktCounter++;
            report[11] = 0x12 | 0x80;
            report[12] = 64;                        // 64 zero haptic bytes follow
            uint crc = Crc32(report, Ds5HapticBtReportSize - 4);
            report[Ds5HapticBtReportSize - 4] = (byte)(crc & 0xFF);
            report[Ds5HapticBtReportSize - 3] = (byte)((crc >> 8) & 0xFF);
            report[Ds5HapticBtReportSize - 2] = (byte)((crc >> 16) & 0xFF);
            report[Ds5HapticBtReportSize - 1] = (byte)(crc >> 24);
            if (s.Tx != null) s.Tx.TrySend(s.BtHandle, report, out _);
        }

        /// <summary>One DS4 tick: resample the tick's 48 kHz pull to 32 kHz
        /// s16 (persistent-phase linear, exact 3:2 so pitch is exact; the
        /// drift trim arrives through <paramref name="inFrames"/> like the
        /// DS5 lane), encode full 256-sample blocks to 109-byte SBC frames,
        /// then drain the queue the way ds4mac does: wait for four buffered
        /// frames, then ship reports while at least two remain, 4-frame 0x17
        /// preferred and 2-frame 0x14 as the fallback. That means a tick
        /// after a stall sends more than one report on purpose, which is the
        /// reference's proven recovery. The DS5's one-report-per-tick rule
        /// is a DS5 finding and is deliberately not projected here (see the
        /// drain loop's own note). Steady state: 2.67 frames produced per
        /// 10.667 ms tick, one report per ~16 ms.</summary>
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
            bool leak = false;
            try
            {
                for (int i = 0; i < 32; i += 8) System.Runtime.InteropServices.Marshal.WriteInt64(ol, i, 0);
                System.Runtime.InteropServices.Marshal.WriteIntPtr(ol, 24, ev); // OVERLAPPED.hEvent (x64)
                bool ok = NativeMethods.WriteFileRaw(h, pin.AddrOfPinnedObject(), (uint)report.Length, IntPtr.Zero, ol);
                if (!ok && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 997 /* ERROR_IO_PENDING */)
                {
                    ok = NativeMethods.WaitForSingleObject(ev, 1000) == 0;
                    if (!ok)
                    {
                        // CancelIo only requests cancellation: the kernel keeps
                        // referencing the pinned buffer and native OVERLAPPED until
                        // the cancelled completion fires, so drain on the event and,
                        // when even that times out, leak the trio (bounded,
                        // pathological-path-only) instead of freeing memory the
                        // completion will write into. Same discipline as
                        // BtWritePool.Dispose.
                        NativeMethods.CancelIo(h);
                        leak = NativeMethods.WaitForSingleObject(ev, 200) != 0;
                    }
                }
                return ok;
            }
            finally
            {
                if (!leak)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(ol);
                    NativeMethods.CloseHandle(ev);
                    pin.Free();
                }
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

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr overlapped);

            /// <summary>Blocking read on a synchronous HID handle. Aborted
            /// by closing the handle from another thread (the BT mic
            /// reader's stop path).</summary>
            public static bool ReadFileSync(IntPtr h, byte[] buf, int n, out int read)
            {
                read = 0;
                if (h == IntPtr.Zero) return false;
                try { return ReadFile(h, buf, n, out read, IntPtr.Zero); }
                catch { return false; }
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr overlapped);

            /// <summary>One synchronous write on the mic reader's handle,
            /// best effort: the stop path must never throw.</summary>
            public static void WriteFileSyncBestEffort(IntPtr h, byte[] buf, int n)
            {
                try { WriteFile(h, buf, n, out _, IntPtr.Zero); } catch { }
            }

            /// <summary>OpenHid without FILE_FLAG_OVERLAPPED, for the
            /// blocking-read mic loop.</summary>
            public static IntPtr OpenHidSync(string path)
            {
                return CreateFileW(path,
                    0x40000000u | 0x80000000u,
                    0x1u | 0x2u,
                    IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            }

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
