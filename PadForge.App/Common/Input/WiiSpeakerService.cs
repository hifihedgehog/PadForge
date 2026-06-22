using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PadForge.Engine.Haptics;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Plays macro sounds through the Wii Remote's built-in speaker
    /// (issue #146, sub-feature 2), the Nintendo analogue of the Sony speaker
    /// path in <see cref="AudioPassthroughService"/>. A Wii Remote assigned to
    /// a slot becomes an output sink: its <see cref="Sink.MacroMixer"/> is
    /// returned to <see cref="SoundMacroService"/> alongside the Sony sinks, so
    /// a macro PlaySound fans out to it with no macro-layer change.
    ///
    /// The wire protocol is the public WiiBrew speaker protocol, grounded in
    /// dolphin (Source/Core/Core/HW/WiimoteEmu/Speaker.cpp + Speaker.h, read via
    /// git show): I2C slave 0x51, register map speaker_data@0x00 / format@0x02 /
    /// sample_rate u16 LE @0x03 / volume@0x05.
    ///
    /// Format is signed 8-bit PCM at 2000 Hz -- the WiiBrew-recommended config for
    /// slow Bluetooth stacks (config bytes 00 40 70 17 60 00 00). PCM is MEMORYLESS
    /// (each sample independent), so a dropped or late 0x18 report on the slow,
    /// SDL-shared BT link is a single click, not the cascading garble that 4-bit
    /// Yamaha ADPCM (differential) produced. Touchmote uses ADPCM at 6 kHz because
    /// WiimoteLib owns the Wiimote exclusively with a tight MicroTimer (no drops);
    /// PadForge shares the link with SDL's continuous input reporting, so the
    /// robust PCM path fits, and it matches the user's finding that the speaker
    /// worked at the lower 2400 Hz but garbled at 6000 Hz. The 48 kHz macro mix is
    /// resampled to 2000 Hz mono by an anti-aliased resampler
    /// (WdlResamplingSampleProvider); each 20-sample frame is written as ONE 0x18
    /// report (20 bytes) per tick at the WiimoteLib MicroTimer cadence
    /// (high-resolution Stopwatch busy-wait, 10 ms = 100 reports/s). The
    /// <see cref="WiiSpeakerAdpcm"/> codec stays available/tested but is off the
    /// live path. The ADPCM playback formula, for reference, is
    /// (6000000 / sample_rate_reg) * 2 Hz; PCM is 12000000 / sample_rate_reg.
    ///
    /// Output write path is chosen per device by a probe in BuildSink: overlapped
    /// WriteFile when the BT stack accepts it (sub-millisecond, paces the 10 ms
    /// cadence easily), else synchronous HidD_SetOutputReport (~12 ms/write here,
    /// SDL fix #2). PadForge owns Wii OUTPUT (SDL is input-only) but SDL still
    /// writes occasional control reports + continuous input reporting on the same
    /// link, which is why memoryless PCM matters.
    ///
    /// Residual: the rumble bit (every output report carries it; our writes leave
    /// it 0, so a cue briefly suppresses SDL-driven rumble).
    /// </summary>
    internal static class WiiSpeakerService
    {
        private const ushort NintendoVid = 0x057E;
        // Wii Remote (RVL-CNT-01) 0x0306, Wii Remote Plus / -TR 0x0330.
        private static bool IsWiiSpeakerDevice(Engine.Data.UserDevice ud)
            => ud != null && ud.VendorId == NintendoVid && (ud.ProdId == 0x0306 || ud.ProdId == 0x0330);

        /// <summary>True when the device is a Wii Remote PadForge can drive a
        /// speaker on (gates the Audio tab, mirrors the Sony check).</summary>
        public static bool DeviceHasSpeaker(Engine.Data.UserDevice ud) => IsWiiSpeakerDevice(ud);

        // Mixer rate matches SoundMacroService so decoded PCM mixes in cleanly.
        private const int MixRate = 48000;
        // ── 8-bit signed PCM at 2000 Hz: the WiiBrew-recommended speaker config
        //    for slow Bluetooth stacks (config bytes 00 40 70 17 60 00 00). ──
        // Why PCM, not 4-bit ADPCM: ADPCM is DIFFERENTIAL -- each sample depends
        // on the prior predictor/step, so a single 0x18 report dropped or delayed
        // on a slow/shared BT link desyncs the decoder and cascades into full
        // garble for the rest of the cue. PCM is MEMORYLESS -- every sample is
        // independent -- so a dropped report is one click, not garble. Touchmote
        // gets away with ADPCM at 6 kHz because WiimoteLib owns the Wiimote
        // exclusively with a tight MicroTimer (no drops); PadForge shares the link
        // with SDL (continuous input reporting), so the robust PCM path fits. This
        // also matches the user's own finding that the speaker worked at the lower
        // 2400 Hz (60 reports/s, fewer drops) but garbled at 6000 Hz (150/s).
        // WiiBrew: pcm_sample_rate = 12000000 / rate_value, so 2000 Hz needs
        // rate_value 6000 = 0x1770 (little-endian RR RR = 70 17). For 8-bit PCM a
        // 0x18 report's 20 payload bytes are 20 samples, so 2000 Hz = 100
        // reports/s (10 ms cadence) -- lighter on the BT than ADPCM's 150/s.
        private const byte SpeakerFormatPcm = 0x40; // dolphin DATA_FORMAT_PCM / WiiBrew FF
        private const ushort PcmRateReg = 6000;     // 12000000 / 2000 Hz = 0x1770
        private const int PcmRate = 2000;           // playback Hz
        private const int SamplesPerReport = 20;    // 8-bit PCM: 20 bytes = 20 samples per 0x18 report
        // Full hardware volume. PadForge scales loudness in SOFTWARE -- the macro
        // VolumeSampleProvider applies the user's volume settings to the float
        // samples before the 8-bit quantize -- so the speaker's own volume byte
        // stays at max and the user's setting carries the actual level. PCM uses
        // the full 0x00-0xFF range (dolphin volume_divisor 0xFF); WiiBrew's sample
        // config shows 0x60 but that is just one example, not a ceiling.
        private const byte SpeakerVolume = 0xFF;
        // Once a cue starts, keep streaming for this long after the last frame
        // that crossed the content threshold, so quiet dips inside the cue do
        // not break the stream (which the Wii FIFO hears as gaps/garble). Long
        // enough to bridge inter-syllable silence, short enough that the
        // near-silent tail after a cue (which encodes to a faint buzz, the codec
        // has no "hold") is brief. Streaming stops cleanly after this.
        private const int HangoverMs = 300;

        private sealed class Sink
        {
            public Guid DeviceGuid;
            public int Slot;
            public string HidPath;
            public IntPtr Handle = IntPtr.Zero;
            public MixingSampleProvider MacroMixer;
            // Properly anti-aliased mono resample of MacroMixer down to WiiRate
            // (NAudio WdlResamplingSampleProvider), so the stream thread reads
            // WiiRate mono directly instead of box-averaging 48k. Box averaging a
            // 20:1 ratio aliased badly; a real resampler is what ffmpeg -ar (the
            // Touchmote path) and the Sony lane's persistent-phase resampler do.
            public NAudio.Wave.ISampleProvider MonoSource;
            public Thread Thread;          // single cadence/encode/write thread
            public volatile bool Running;
            public WiiSpeakerAdpcm.State Adpcm = WiiSpeakerAdpcm.State.Initial;
            // Per-sink write path, chosen by the BuildSink probe. When the BT
            // stack accepts pipelined overlapped WriteFile we run the native
            // 6000 Hz through Pool; otherwise the synchronous HidD path at the
            // lower rate. Rate/RateReg drive the resampler, the config register,
            // and the cadence.
            public int Rate = PcmRate;
            public ushort RateReg = PcmRateReg;
            public bool UseWriteFile;
            public WiiWritePool Pool;
            // Wall-clock (Environment.TickCount64) of the last frame with real
            // content. While within HangoverMs of it we stream EVERY frame
            // (including quiet inter-syllable dips) so the speaker gets a
            // continuous stream; gating per-frame on the audible threshold
            // punched holes in real audio = garble.
            public long LastContentMs = long.MinValue / 2;
            // Output report buffer length. HidD_SetOutputReport, like WriteFile,
            // requires the buffer to be EXACTLY the device's OutputReportByteLength
            // (caps); a short buffer fails with ERROR_INVALID_PARAMETER. Queried
            // from the device, never hardcoded (matches RawHidOutput / SDL
            // hid_write_output_report). >= ReportLen always.
            public int PadLen = ReportLen;
            // System-audio passthrough (the "mirror", same option DualSense uses):
            // when AudioPassthroughEnabled is set for this Wii device, a
            // WasapiLoopbackCapture of the chosen endpoint is added to MacroMixer
            // so the speaker plays system audio. Reconciled each pass from
            // AudioPassthroughService.PassthroughConfigProvider (covers all devices
            // in the slot, Wii included).
            public bool MirrorOn;
            public string MirrorSourceId = "";   // "" = system default endpoint
            public WasapiLoopbackCapture MirrorCapture;
            public ISampleProvider MirrorInput;   // the provider added to MacroMixer
            // Diagnostics (per-second snapshot to the log): did the writes land?
            public long WrOk, WrFail, Dropped, Audible, LoopTicks, WriteMicros;
            public int LastErr;
        }

        private static readonly object _lock = new();
        private static readonly List<Sink> _sinks = new();
        private static volatile bool _suppressed;
        private static Timer _reconcileTimer;
        private static int _reconcileBusy;

        /// <summary>Starts the periodic reconcile so a Wii Remote assigned (or
        /// removed) mid-session builds/tears down its speaker sink without a
        /// per-assignment hook, mirroring the Sony service's self-healing
        /// worker. Idempotent; call once at engine start.</summary>
        public static void EnsureStarted()
        {
            lock (_lock)
            {
                _suppressed = false;
                if (_reconcileTimer != null) return;
                _reconcileTimer = new Timer(_ => { try { Reconcile(); } catch { } },
                    null, 0, 3000);
            }
        }

        // ── HID output via HidD_SetOutputReport (Wii output reports need it) ──
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(IntPtr h, byte[] buffer, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sa,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint GENERIC_WRITE = 0x40000000, GENERIC_READ = 0x80000000;
        private const uint SHARE_RW = 0x3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        // Exact WiimoteLib open flags: Temporary | Write_Through | Overlapped |
        // NoBuffering (decompiled WiimoteLib OpenWiimoteDeviceHandle). For a HID
        // device these cache/file hints are effectively no-ops but we match the
        // proven library rather than diverge.
        private const uint WII_OPEN_FLAGS = 0x40000000 /*OVERLAPPED*/ | 0x80000000 /*WRITE_THROUGH*/
                                          | 0x20000000 /*NO_BUFFERING*/ | 0x00000100 /*TEMPORARY*/;
        private const int ERROR_IO_PENDING = 997;
        private const int ERROR_INVALID_PARAMETER = 87;
        private static readonly IntPtr INVALID = new IntPtr(-1);

        // Overlapped WriteFile path (the fast Wii output the BT stack may accept).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr h, IntPtr buf, uint n, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr h, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr h);

        // Serial in-order overlapped writer (NOT pipelined). It writes ONE 22-byte
        // report and BLOCKS until that write completes before the caller sends the
        // next, exactly like the proven WiimoteLib (mStream = FileStream(handle,
        // isAsync:true); mStream.Write(mBuff,0,22) blocks per report). Pipelining
        // multiple writes in flight (the earlier 6-slot version) let the BT stack
        // reorder or burst the reports, and since Yamaha ADPCM is differential,
        // out-of-order delivery scrambles the decoder = the garble. Strict
        // one-at-a-time in-order delivery is required. The cadence thread is the
        // only caller (teardown CancelIo's the handle then disposes).
        internal sealed class WiiWritePool : IDisposable
        {
            private const int Slots = 1;
            private const int OverlappedSize = 32; // x64 OVERLAPPED
            private readonly byte[][] _buf = new byte[Slots][];
            private readonly GCHandle[] _pin = new GCHandle[Slots];
            private readonly IntPtr[] _ev = new IntPtr[Slots];
            private readonly IntPtr[] _ol = new IntPtr[Slots];
            private bool _disposed;

            public WiiWritePool(int reportSize)
            {
                for (int i = 0; i < Slots; i++)
                {
                    _buf[i] = new byte[reportSize];
                    _pin[i] = GCHandle.Alloc(_buf[i], GCHandleType.Pinned);
                    _ev[i] = CreateEventW(IntPtr.Zero, true, true, null); // signaled = free
                    _ol[i] = Marshal.AllocHGlobal(OverlappedSize);
                }
            }

            // Writes ONE report and BLOCKS until it actually completes, so the
            // next report is sent strictly after this one (in order, never
            // concurrent). Returns 1 = delivered, -1 = hard fail (rejected/timeout).
            // Mirrors WiimoteLib's blocking mStream.Write(mBuff,0,22).
            public int TrySend(IntPtr handle, byte[] report, int len, out int err)
            {
                err = 0;
                if (_disposed) return -1;
                int n = Math.Min(len, _buf[0].Length);
                Buffer.BlockCopy(report, 0, _buf[0], 0, n);
                ResetEvent(_ev[0]);
                for (int o = 0; o < OverlappedSize - 8; o += 8) Marshal.WriteInt64(_ol[0], o, 0);
                Marshal.WriteIntPtr(_ol[0], 24, _ev[0]); // OVERLAPPED.hEvent (x64 offset)
                if (!WriteFile(handle, _pin[0].AddrOfPinnedObject(), (uint)n, IntPtr.Zero, _ol[0]))
                {
                    err = Marshal.GetLastWin32Error();
                    if (err != ERROR_IO_PENDING) { SetEvent(_ev[0]); return -1; }
                    // BLOCK until the write completes (in-order delivery).
                    if (WaitForSingleObject(_ev[0], 1000) != 0) { return -1; }
                    if (!GetOverlappedResult(handle, _ol[0], out _, false)) { err = Marshal.GetLastWin32Error(); return -1; }
                }
                return 1;
            }

            // Caller must CancelIo the handle first. CancelIo only REQUESTS
            // cancellation; a write submitted with ERROR_IO_PENDING may still be
            // in flight when CancelIo returns, and the kernel/BT stack holds a
            // reference to the pinned data buffer and the native OVERLAPPED until
            // that write actually completes (cancelled or not). Freeing them
            // before the completion is a use-after-free on memory the kernel is
            // still writing into. So wait on each slot's event (signaled on
            // completion, including a cancelled completion) before freeing that
            // slot, exactly as the proven Sony BtWritePool.Dispose does
            // (AudioPassthroughService.cs).
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                for (int i = 0; i < Slots; i++)
                {
                    try { if (_ev[i] != IntPtr.Zero) WaitForSingleObject(_ev[i], 100); } catch { }
                    try { if (_pin[i].IsAllocated) _pin[i].Free(); } catch { }
                    try { if (_ev[i] != IntPtr.Zero) CloseHandle(_ev[i]); } catch { }
                    try { if (_ol[i] != IntPtr.Zero) Marshal.FreeHGlobal(_ol[i]); } catch { }
                }
            }
        }
        // Wiimote logical output report (report id + 21 payload bytes). The
        // actual on-the-wire buffer is sized to the device's
        // OutputReportByteLength (Sink.PadLen), which may be larger.
        private const int ReportLen = 22;

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        // Returns (outputReportByteLength, inputReportByteLength) from the
        // device's HID caps, or (0,0) if unavailable. The output length is the
        // size HidD_SetOutputReport demands; a short buffer is rejected with
        // ERROR_INVALID_PARAMETER (SDL hid_write_output_report, RawHidOutput).
        private static (int outLen, int inLen) QueryReportLens(IntPtr h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp) || pp == IntPtr.Zero) return (0, 0);
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) < 0) return (0, 0);
                return (caps.OutputReportByteLength, caps.InputReportByteLength);
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        // Lightweight diagnostic log so a single hardware test gives ground
        // truth (did the writes land, what is the report length, how many frames
        // dropped) instead of another blind guess. Best-effort, never throws.
        // Ground-truth capture: append the exact ADPCM payload bytes (in wire
        // order, only frames actually written to the device) so the stream can
        // be decoded offline and compared to the source. Capped so the file
        // stays small. This is the decisive "is our data correct or is the
        // hardware mangling it" test.
        private static readonly object _capLock = new object();
        private static long _capBytes;
        private const long CapMaxBytes = 12000; // ~100 s at 2400 Hz / 60 fps
        private static void CaptureSent(byte[] adpcm)
        {
            try
            {
                lock (_capLock)
                {
                    if (_capBytes >= CapMaxBytes) return;
                    using var fs = new System.IO.FileStream(@"C:\tmp\wii-capture.adpcm",
                        System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
                    fs.Write(adpcm, 0, adpcm.Length);
                    _capBytes += adpcm.Length;
                }
            }
            catch { }
        }

        private static readonly object _logLock = new object();
        private static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                    System.IO.File.AppendAllText(@"C:\tmp\wii-speaker.log",
                        DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        // ── Precise pacing (1 ms timer + high-res waitable timer), the same
        //    mechanism AudioPassthroughService.BtThreadMain uses. Thread.Sleep
        //    has ~15 ms granularity, which underruns the speaker, and bursting
        //    catch-up frames overflows its shallow buffer: both crackle. ──
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
            IntPtr completionRoutine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
        // TIMER_ALL_ACCESS (0x1F0003) is REJECTED together with the high-res
        // flag on some Windows builds (CreateWaitableTimerExW returns Zero), per
        // the field telemetry in AudioPassthroughService. Use the minimal mask
        // (TIMER_MODIFY_STATE | SYNCHRONIZE) the proven streamer uses so the
        // high-res timer actually gets created instead of silently degrading to
        // the Thread.Sleep cadence that crackles.
        private const uint TIMER_MODIFY_STATE = 0x0002;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint TIMER_ACCESS = TIMER_MODIFY_STATE | SYNCHRONIZE;

        // Creates the pacing timer: high-resolution if the OS allows it, else a
        // plain waitable timer, else Zero (HighResWait then uses Thread.Sleep).
        private static IntPtr CreatePacingTimer()
        {
            IntPtr t = CreateWaitableTimerExW(IntPtr.Zero, null,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ACCESS);
            if (t == IntPtr.Zero)
                t = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ACCESS);
            return t;
        }

        private static void HighResWait(IntPtr timer, double ms)
        {
            if (timer == IntPtr.Zero) { Thread.Sleep((int)Math.Max(1, ms)); return; }
            long due = -(long)(ms * 10000.0); // 100ns units, negative = relative
            if (due >= 0) due = -1;
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(timer, (uint)Math.Max(1, ms + 50));
            else
                Thread.Sleep((int)Math.Max(1, ms));
        }

        /// <summary>Returns the live macro-sink mixers for the slot's Wii
        /// Remotes, so SoundMacroService routes macro sounds into the speaker.
        /// Mirrors AudioPassthroughService.GetSlotSinkMixers.</summary>
        public static List<MixingSampleProvider> GetSlotSinkMixers(int slot, Guid? deviceFilter = null)
        {
            var list = new List<MixingSampleProvider>();
            lock (_lock)
            {
                foreach (var s in _sinks)
                {
                    if (s.Slot != slot) continue;
                    if (deviceFilter.HasValue && s.DeviceGuid != deviceFilter.Value) continue;
                    if (s.MacroMixer != null) list.Add(s.MacroMixer);
                }
            }
            return list;
        }

        /// <summary>Rebuilds the sink set from the current slot assignments.
        /// Called wherever AudioPassthroughService.Reconcile is.</summary>
        public static void Reconcile()
        {
            if (_suppressed) return;
            // Re-entrancy guard: the 3 s timer fires on arbitrary pool threads,
            // and a blocking BT build/teardown can outlast the interval, so
            // overlapping reconciles would pile up. Run at most one at a time.
            if (Interlocked.Exchange(ref _reconcileBusy, 1) == 1) return;
            try
            {
                // Desired: one sink per online Wii Remote assigned to a slot.
                var desired = new List<(int Slot, Guid Guid, string Path)>();
                var settings = SettingsManager.UserSettings;
                if (settings != null)
                {
                    var seen = new HashSet<Guid>();
                    lock (settings.SyncRoot)
                    {
                        foreach (var us in settings.Items)
                        {
                            if (us == null || us.MapTo < 0) continue;
                            if (!seen.Add(us.InstanceGuid)) continue;
                            var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                            if (ud == null || !ud.IsOnline || string.IsNullOrEmpty(ud.DevicePath)) continue;
                            if (!IsWiiSpeakerDevice(ud)) continue;
                            desired.Add((us.MapTo, us.InstanceGuid, ud.DevicePath));
                        }
                    }
                }

                // Diff under _lock (fast, no I/O). Defer the blocking BT I/O
                // (CreateFile / InitSpeaker / Join) to OUTSIDE the lock so macro
                // routing (GetSlotSinkMixers) and Shutdown never block on it.
                var toBuild = new List<Sink>();
                var toTeardown = new List<Sink>();
                lock (_lock)
                {
                    if (_suppressed) return;
                    for (int i = _sinks.Count - 1; i >= 0; i--)
                    {
                        var s = _sinks[i];
                        if (!desired.Exists(d => d.Guid == s.DeviceGuid && d.Slot == s.Slot))
                        {
                            toTeardown.Add(s);
                            _sinks.RemoveAt(i);
                        }
                    }
                    foreach (var d in desired)
                    {
                        if (_sinks.Exists(s => s.DeviceGuid == d.Guid && s.Slot == d.Slot)) continue;
                        var sink = new Sink
                        {
                            DeviceGuid = d.Guid,
                            Slot = d.Slot,
                            HidPath = d.Path,
                            MacroMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 2)) { ReadFully = true },
                        };
                        _sinks.Add(sink);   // visible to routing immediately; thread starts below
                        toBuild.Add(sink);
                    }
                }

                foreach (var s in toTeardown) TeardownSink(s);
                foreach (var s in toBuild)
                {
                    if (!BuildSink(s))
                    {
                        // Open/init failed (device busy with SDL, etc.). Drop the
                        // sink so the next reconcile retries it.
                        lock (_lock) _sinks.Remove(s);
                    }
                }

                ReconcileMirrors();
            }
            finally { Interlocked.Exchange(ref _reconcileBusy, 0); }
        }

        /// <summary>For each live sink, start or stop its system-audio loopback
        /// mirror to match the per-device passthrough config (the same option the
        /// DualSense path reads). Runs OUTSIDE _lock since WASAPI start/stop is
        /// COM device I/O; snapshots the sink set first.</summary>
        private static void ReconcileMirrors()
        {
            var provider = AudioPassthroughService.PassthroughConfigProvider;
            List<Sink> live;
            lock (_lock) live = _sinks.ToList();
            foreach (var s in live)
            {
                bool wantOn = false; string wantSrc = "";
                try
                {
                    var cfg = provider?.Invoke(s.Slot);
                    if (cfg != null)
                        foreach (var c in cfg)
                            if (c.Device == s.DeviceGuid) { wantOn = c.PassthroughOn; wantSrc = c.MirrorSource ?? ""; break; }
                }
                catch { }

                if (wantOn && (!s.MirrorOn || s.MirrorSourceId != wantSrc))
                {
                    StopMirror(s);          // rebuild on a source change
                    StartMirror(s, wantSrc);
                }
                else if (!wantOn && s.MirrorOn)
                {
                    StopMirror(s);
                }
            }
        }

        /// <summary>Opens a WasapiLoopbackCapture on the chosen render endpoint and
        /// feeds it (resampled to the 48 kHz stereo mixer format) into the sink's
        /// MacroMixer, so the Wii speaker plays system audio. Best-effort: on any
        /// failure the mirror simply stays off.</summary>
        private static void StartMirror(Sink s, string endpointId)
        {
            if (s.MacroMixer == null) return;
            try
            {
                MMDevice dev;
                using (var en = new MMDeviceEnumerator())
                    dev = string.IsNullOrEmpty(endpointId)
                        ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : en.GetDevice(endpointId);
                if (dev == null || dev.State != DeviceState.Active) { dev?.Dispose(); return; }

                var cap = new WasapiLoopbackCapture(dev);
                // ReadFully keeps the provider returning `count` (zero-filled) during
                // silence so the mixer never drops it as a finished one-shot.
                var buf = new BufferedWaveProvider(cap.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                };
                cap.DataAvailable += (o, e) =>
                {
                    try { if (e.BytesRecorded > 0) buf.AddSamples(e.Buffer, 0, e.BytesRecorded); } catch { }
                };
                cap.StartRecording();
                dev.Dispose();   // WasapiLoopbackCapture holds its own device ref

                ISampleProvider sp = buf.ToSampleProvider();
                if (sp.WaveFormat.SampleRate != MixRate) sp = new WdlResamplingSampleProvider(sp, MixRate);
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);

                s.MacroMixer.AddMixerInput(sp);   // MixingSampleProvider locks internally
                s.MirrorCapture = cap;
                s.MirrorInput = sp;
                s.MirrorOn = true;
                s.MirrorSourceId = endpointId ?? "";
                Log($"mirror on slot={s.Slot} src={(string.IsNullOrEmpty(endpointId) ? "default" : endpointId)} fmt={cap.WaveFormat}");
            }
            catch { try { StopMirror(s); } catch { } }
        }

        private static void StopMirror(Sink s)
        {
            try { if (s.MirrorInput != null && s.MacroMixer != null) s.MacroMixer.RemoveMixerInput(s.MirrorInput); } catch { }
            try { s.MirrorCapture?.StopRecording(); } catch { }
            try { s.MirrorCapture?.Dispose(); } catch { }
            s.MirrorCapture = null;
            s.MirrorInput = null;
            s.MirrorOn = false;
        }

        /// <summary>Opens the device, runs the speaker init, and starts the
        /// stream thread. Returns false if the handle could not be opened (the
        /// caller drops the sink to retry later). Runs OUTSIDE _lock.</summary>
        private static bool BuildSink(Sink s)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = CreateFileW(s.HidPath, GENERIC_WRITE | GENERIC_READ, SHARE_RW,
                    IntPtr.Zero, OPEN_EXISTING, WII_OPEN_FLAGS, IntPtr.Zero);
                if (h == INVALID || h == IntPtr.Zero)
                {
                    Log($"open FAILED err={Marshal.GetLastWin32Error()} path={s.HidPath}");
                    return false; // device busy / no raw access; retry next reconcile
                }
                var (capOut, capIn) = QueryReportLens(h);
                int outLen = capOut >= ReportLen ? capOut : ReportLen;
                s.PadLen = outLen;

                // Run 8-bit PCM at 2000 Hz = 100 reports/s (WiiBrew-recommended for
                // slow BT). See the constants block for why PCM over ADPCM.
                //
                // DELIVERY: overlapped WriteFile. Measured on this BT stack,
                // HidD_SetOutputReport takes ~12 ms per write (live log avgWriteUs
                // 11000-12500) and could not even sustain the old 6.67 ms ADPCM
                // cadence (loop went erratic, writes failed lastErr 21). WriteFile
                // completes sub-millisecond, so it paces the 10 ms PCM cadence with
                // margin to spare. HidD stays the fallback if WriteFile is rejected.
                bool wf = ProbeWriteFile(h, outLen, out int probeErr);
                s.UseWriteFile = wf;
                s.Rate = PcmRate;
                s.RateReg = PcmRateReg;
                Log($"open ok slot={s.Slot} capOut={capOut} padLen={outLen} writeFile={wf} probeErr={probeErr} rate={s.Rate} fmt=PCM8");
                InitSpeaker(h, outLen, s.RateReg);

                // Commit under _lock, but only if a concurrent Shutdown/teardown
                // has not dropped this sink while we ran the lock-free open+init.
                // Mirrors AudioPassthroughService.BuildTransportOnWorker: open into
                // a LOCAL handle, publish to the sink only under _lock after
                // re-checking the sink is still live, else close the orphan. The
                // open+init is lock-free (the 3 s timer must not block macro
                // routing on BT I/O), so Shutdown can run between _sinks.Add and
                // here; without this recheck we would resurrect a torn-down sink as
                // an orphan thread + open handle that nothing tracks (the leak the
                // cite-verify caught). The thread is started under _lock too.
                // StreamLoop never takes _lock, so there is no deadlock.
                var mono = new StereoToMonoSampleProvider(s.MacroMixer) { LeftVolume = 0.5f, RightVolume = 0.5f };
                var resampled = new WdlResamplingSampleProvider(mono, s.Rate);
                var pool = wf ? new WiiWritePool(outLen) : null;
                lock (_lock)
                {
                    if (_suppressed || !_sinks.Contains(s))
                    {
                        try { CloseHandle(h); } catch { }
                        try { pool?.Dispose(); } catch { }
                        h = IntPtr.Zero;
                        return true; // race lost: sink already dropped, nothing to retry
                    }
                    s.Handle = h;
                    h = IntPtr.Zero;     // ownership transferred to the sink
                    s.MonoSource = resampled;
                    s.Pool = pool;
                    s.Running = true;
                    s.Thread = new Thread(() => StreamLoop(s)) { IsBackground = true, Name = "PadForge Wii Speaker" };
                    s.Thread.Start();
                }
                return true;
            }
            catch
            {
                // Threw before commit: close the local handle ourselves (it was
                // never published to s.Handle, so TeardownSink would not). If the
                // throw landed after commit, h is already Zero and TeardownSink
                // closes s.Handle instead, so there is no double close.
                if (h != IntPtr.Zero && h != INVALID) { try { CloseHandle(h); } catch { } }
                TeardownSink(s);
                return false;
            }
        }

        private static void TeardownSink(Sink s)
        {
            // Stop the system-audio mirror capture first (its DataAvailable feeds
            // the MacroMixer the stream thread reads).
            try { StopMirror(s); } catch { }
            // Stop the stream thread and wait for it to ACTUALLY exit before
            // touching the handle. That thread calls the synchronous
            // HidD_SetOutputReport, which can block past a short join on a
            // disconnecting BT Wiimote; closing the handle under an in-flight
            // write is a use-after-close. Only close once the thread confirms
            // exit; if it does not exit in time, leave the handle for the OS to
            // reclaim (teardown is rare and the thread is a background thread).
            s.Running = false;
            bool exited = true;
            try { exited = s.Thread?.Join(3000) ?? true; } catch { exited = true; }
            if (exited)
            {
                // The thread has stopped submitting. Cancel any in-flight
                // overlapped pool writes BEFORE freeing the pool's pinned buffers
                // and OVERLAPPED memory (a live write must not touch freed memory),
                // then dispose the pool.
                if (s.Handle != IntPtr.Zero) { try { CancelIo(s.Handle); } catch { } }
                try { s.Pool?.Dispose(); } catch { }
                s.Pool = null;
                if (s.Handle != IntPtr.Zero)
                {
                    try { WriteReport(s.Handle, 0x19, 0x04, s.PadLen); } catch { } // mute
                    try { WriteReport(s.Handle, 0x14, 0x00, s.PadLen); } catch { } // disable speaker
                    try { CloseHandle(s.Handle); } catch { }
                }
            }
            // If the thread did not exit, leave the handle and pool for the OS to
            // reclaim rather than free memory a live write might still touch.
            s.Handle = IntPtr.Zero;
            s.MonoSource = null;
        }

        // ── Wii speaker init: the WiiBrew sequence, register offsets grounded
        //    in dolphin Speaker.h Register{} ──
        // Each register write is followed by a short delay. A real Wiimote ACKs
        // every 0x16 register write (dolphin HandleWriteData SendAck; WiimoteLib
        // blocks on the ack per write). PadForge's handle is write-only (SDL owns
        // the input pipe), so it cannot read the 0x22 ack; firing the config
        // writes back-to-back lets the unmute + 0x18 stream race ahead of the
        // I2C register engine, leaving the decoder on stale format/rate/volume
        // (a misconfig that crackles). A ~one-BT-round-trip delay between writes
        // serializes them in place of the ack.
        private const int InitWriteDelayMs = 8;
        private static void InitSpeaker(IntPtr h, int outLen, ushort rateReg)
        {
            WriteReport(h, 0x14, 0x04, outLen); Thread.Sleep(InitWriteDelayMs);   // enable speaker (bit2)
            WriteReport(h, 0x19, 0x04, outLen); Thread.Sleep(InitWriteDelayMs);   // mute while configuring
            WriteRegister(h, 0xa20009, new byte[] { 0x01 }, outLen); Thread.Sleep(InitWriteDelayMs);
            WriteRegister(h, 0xa20001, new byte[] { 0x08 }, outLen); Thread.Sleep(InitWriteDelayMs);
            // 7-byte config written to register 0xa20001 -> offsets 0x01..0x07:
            // [unk_1, format(0x02), rate_lo(0x03), rate_hi(0x04), volume(0x05), unk, unk].
            // sample_rate is little-endian (dolphin reads reg_data.sample_rate as
            // a native u16 with no swap on its LE host). WiiBrew 8-bit-PCM-at-2000-Hz
            // config (format 0x40 PCM, rate 0x1770 = 6000 -> 12000000/6000 = 2000 Hz)
            // with the volume byte at full 0xFF instead of WiiBrew's 0x60 example,
            // because PadForge scales loudness in software.
            WriteRegister(h, 0xa20001, new byte[]
            {
                0x00,
                SpeakerFormatPcm,                          // 0x40 = signed 8-bit PCM
                (byte)(rateReg & 0xFF),
                (byte)(rateReg >> 8),
                SpeakerVolume,
                0x00, 0x00,
            }, outLen);
            Thread.Sleep(InitWriteDelayMs);
            WriteRegister(h, 0xa20008, new byte[] { 0x01 }, outLen); Thread.Sleep(InitWriteDelayMs);
            WriteReport(h, 0x19, 0x00, outLen);               // unmute
        }

        // One benign overlapped WriteFile (0x14 speaker-enable, which init
        // re-sends) to learn whether this BT stack accepts the fast path. Returns
        // true if the write completes, false if rejected (ERROR_INVALID_PARAMETER,
        // the SDL-fix-#2 case) or errors.
        private static bool ProbeWriteFile(IntPtr h, int outLen, out int err)
        {
            err = 0;
            int n = outLen < ReportLen ? ReportLen : outLen;
            var buf = new byte[n];
            buf[0] = 0x14; buf[1] = 0x04;
            GCHandle pin = default;
            IntPtr ev = IntPtr.Zero, ol = IntPtr.Zero;
            try
            {
                pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                ev = CreateEventW(IntPtr.Zero, true, false, null);
                ol = Marshal.AllocHGlobal(32);
                for (int o = 0; o < 24; o += 8) Marshal.WriteInt64(ol, o, 0);
                Marshal.WriteIntPtr(ol, 24, ev);
                if (WriteFile(h, pin.AddrOfPinnedObject(), (uint)n, IntPtr.Zero, ol))
                    return true; // completed synchronously
                err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return false; // rejected (87) or other error
                if (WaitForSingleObject(ev, 500) != 0) { try { CancelIo(h); } catch { } return false; }
                return GetOverlappedResult(h, ol, out _, false);
            }
            catch { return false; }
            finally
            {
                try { if (pin.IsAllocated) pin.Free(); } catch { }
                try { if (ev != IntPtr.Zero) CloseHandle(ev); } catch { }
                try { if (ol != IntPtr.Zero) Marshal.FreeHGlobal(ol); } catch { }
            }
        }

        private const byte WiiSpeakerAdpcm_FormatAdpcm = 0x00;

        // 0x16 WriteData: [0x16, 0x04|rumble, addr(3 BE), len, data(<=16)].
        // byte1 bit2 (0x04) selects the control-register address space; rumble
        // bit kept 0 (see class-doc residual on SDL coexistence).
        // Buffer sized to the device's OutputReportByteLength (>= ReportLen),
        // zero-padded after the logical content. HidD_SetOutputReport rejects a
        // short buffer with ERROR_INVALID_PARAMETER (SDL hid_write_output_report,
        // RawHidOutput.ResizeForDevice).
        private static void WriteRegister(IntPtr h, int offset24, byte[] data, int outLen)
        {
            var buf = new byte[outLen < ReportLen ? ReportLen : outLen];
            buf[0] = 0x16;
            buf[1] = 0x04;
            buf[2] = (byte)((offset24 >> 16) & 0xFF);
            buf[3] = (byte)((offset24 >> 8) & 0xFF);
            buf[4] = (byte)(offset24 & 0xFF);
            buf[5] = (byte)data.Length;
            Array.Copy(data, 0, buf, 6, Math.Min(data.Length, 16));
            HidD_SetOutputReport(h, buf, buf.Length);
        }

        private static void WriteReport(IntPtr h, byte reportId, byte value, int outLen)
        {
            var buf = new byte[outLen < ReportLen ? ReportLen : outLen];
            buf[0] = reportId;
            buf[1] = value;
            HidD_SetOutputReport(h, buf, buf.Length);
        }

        // 0x18 SpeakerData: [0x18, (len<<3)|rumble, <up to 20 payload bytes>],
        // sized to the device's output report length (zero-padded). For 8-bit PCM
        // the 20 payload bytes are 20 samples; len is the byte count (WiiBrew LL).
        private static byte[] BuildSpeakerReport(byte[] adpcm, int len, int outLen)
        {
            var buf = new byte[outLen < ReportLen ? ReportLen : outLen];
            buf[0] = 0x18;
            buf[1] = (byte)((len << 3) & 0xF8); // rumble bit 0 left clear
            Array.Copy(adpcm, 0, buf, 2, Math.Min(len, 20));
            return buf;
        }

        private static bool HidWriteReport(IntPtr h, byte[] report)
        {
            try { return HidD_SetOutputReport(h, report, report.Length); } catch { return false; }
        }

        // ── Stream thread: read WiiRate mono from the properly-resampled source,
        //    ADPCM-encode carrying state, and write ONE 0x18 report per tick,
        //    synchronously, NEVER bursting. This mirrors the two proven
        //    references exactly: dolphin's real-Wiimote writer (one report, then
        //    sleep_until the next) and the Sony BtThreadMain firmware rule ("one
        //    report per tick, never burst. Faster delivery or back-to-back
        //    catch-up frames overflow the pad's shallow receive buffer and it
        //    drops audio"). The write (~11 ms) fits inside the 16.667 ms tick, so
        //    there is no queue, no second thread, no burst, and the encoder state
        //    advances in-order only on a confirmed write. ──
        private static void StreamLoop(Sink s)
        {
            var monoF = new float[SamplesPerReport];
            var pcm8 = new byte[SamplesPerReport];

            // Pace on a high-resolution Stopwatch (the old DateTime.UtcNow clock
            // only advanced at the ~1-15 ms system timer tick, which wobbled the
            // cadence). For 8-bit PCM the interval is SamplesPerReport / rate =
            // 20 / 2000 Hz = 10 ms (100 reports/s). Wait by SLEEPING most of the
            // interval and spinning only the last ~0.5 ms: PCM is memoryless so it
            // does not need WiimoteLib's continuous Highest-priority busy-spin, and
            // a busy-spin here starved the DualSense audio-passthrough BtThread
            // (also ThreadPriority.Highest) and broke that audio. AboveNormal keeps
            // this thread below the audio path.
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
            timeBeginPeriod(1);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
                long intervalUs = (long)SamplesPerReport * 1_000_000L / s.Rate; // 20 / 2000 Hz = 10000 us
                long intervalTicks = intervalUs * stopwatchFreq / 1_000_000L;
                long next = sw.ElapsedTicks + intervalTicks;
                long nextLogAt = Environment.TickCount64 + 1000;

                while (s.Running)
                {
                    s.LoopTicks++;
                    int got = 0;
                    try { got = s.MonoSource.Read(monoF, 0, SamplesPerReport); } catch { }
                    bool audible = false;
                    for (int i = 0; i < SamplesPerReport; i++)
                    {
                        float v = i < got ? monoF[i] : 0f;
                        if (v > 0.0008f || v < -0.0008f) audible = true;
                        // Signed 8-bit PCM: the Wii plays each report byte as
                        // (s16)(s8)data[i] * 0x100 (dolphin Speaker.cpp PCM branch).
                        // Memoryless -- no codec state -- so a dropped or late report
                        // on the slow shared BT link is a single click, not the
                        // cascading desync that differential ADPCM produced.
                        int iv = (int)Math.Round(v * 127.0);
                        pcm8[i] = (byte)(sbyte)(iv > 127 ? 127 : iv < -128 ? -128 : iv);
                    }

                    // Stream while a cue is active. A frame above the threshold
                    // (re)arms a HangoverMs window; while it is open we send every
                    // frame (quiet dips included) so the FIFO stays fed, and after
                    // HangoverMs of true silence we stop so we are not pushing noise
                    // between cues. 8-bit PCM is memoryless, so there is no codec
                    // state to reset at a cue boundary (the differential-ADPCM reset
                    // dance is gone) and a skipped report never desyncs the rest.
                    long nowMs = Environment.TickCount64;
                    if (audible) s.LastContentMs = nowMs;
                    bool streaming = (nowMs - s.LastContentMs) < HangoverMs;
                    if (streaming && s.Handle != IntPtr.Zero)
                    {
                        Interlocked.Increment(ref s.Audible);
                        byte[] report = BuildSpeakerReport(pcm8, SamplesPerReport, s.PadLen);
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        bool ok, dropped = false;
                        if (s.UseWriteFile && s.Pool != null)
                        {
                            // Overlapped WriteFile (the only delivery that paces 100
                            // reports/s here; HidD measured ~12 ms/write). On a hard
                            // failure fall back to HidD for the rest of the cue.
                            int r = s.Pool.TrySend(s.Handle, report, report.Length, out int werr);
                            if (r < 0)
                            {
                                s.UseWriteFile = false; s.LastErr = werr;
                                ok = HidWriteReport(s.Handle, report);
                            }
                            else { ok = (r == 1); dropped = (r == 0); }
                        }
                        else ok = HidWriteReport(s.Handle, report);
                        long us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                        Interlocked.Add(ref s.WriteMicros, us);
                        if (ok) { Interlocked.Increment(ref s.WrOk); CaptureSent(pcm8); }
                        else if (dropped) Interlocked.Increment(ref s.Dropped);
                        else { s.LastErr = Marshal.GetLastWin32Error(); Interlocked.Increment(ref s.WrFail); }
                    }

                    if (Environment.TickCount64 >= nextLogAt)
                    {
                        long ok = Interlocked.Read(ref s.WrOk), fail = Interlocked.Read(ref s.WrFail);
                        long drop = Interlocked.Read(ref s.Dropped), aud = Interlocked.Read(ref s.Audible);
                        long ticks = Interlocked.Read(ref s.LoopTicks), wus = Interlocked.Read(ref s.WriteMicros);
                        long avgWriteUs = (ok + fail) > 0 ? wus / (ok + fail) : 0;
                        if (ok + fail + aud > 0)
                            Log($"slot={s.Slot} rate={s.Rate} wf={s.UseWriteFile} loopTicks={ticks} streamedFrames={aud} writesOk={ok} dropped={drop} writesFail={fail} lastErr={s.LastErr} avgWriteUs={avgWriteUs}");
                        Interlocked.Exchange(ref s.WrOk, 0); Interlocked.Exchange(ref s.WrFail, 0);
                        Interlocked.Exchange(ref s.Dropped, 0);
                        Interlocked.Exchange(ref s.Audible, 0); Interlocked.Exchange(ref s.LoopTicks, 0);
                        Interlocked.Exchange(ref s.WriteMicros, 0);
                        nextLogAt = Environment.TickCount64 + 1000;
                    }

                    // Sleep most of the way to the next 10 ms grid point (yielding
                    // the CPU so the Highest-priority audio threads are never
                    // starved), then spin only the final ~0.5 ms for accuracy.
                    // Accumulative (next += interval) so a long write is caught up
                    // rather than re-snapped. When idle, just sleep and re-base.
                    if (streaming)
                    {
                        long spinTicks = stopwatchFreq / 2000; // ~0.5 ms
                        while (s.Running && (next - sw.ElapsedTicks) > spinTicks) Thread.Sleep(1);
                        while (s.Running && sw.ElapsedTicks < next) Thread.SpinWait(16);
                        next += intervalTicks;
                    }
                    else
                    {
                        Thread.Sleep(2);
                        next = sw.ElapsedTicks + intervalTicks;
                    }
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        /// <summary>Tears down every Wii speaker sink. Call on app shutdown
        /// alongside AudioPassthroughService.Shutdown.</summary>
        public static void Shutdown()
        {
            _suppressed = true;
            lock (_lock)
            {
                try { _reconcileTimer?.Dispose(); } catch { }
                _reconcileTimer = null;
                foreach (var s in _sinks) TeardownSink(s);
                _sinks.Clear();
            }
        }
    }
}
