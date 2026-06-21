using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
    /// git show): I2C slave 0x51, register map speaker_data@0x00 / format@0x02
    /// (0x00 = 4-bit Yamaha ADPCM) / sample_rate u16 LE @0x03 / volume@0x05,
    /// ADPCM playback Hz = 6000000 / sample_rate. Encoding is the verified
    /// <see cref="WiiSpeakerAdpcm"/>. Output reports go via HidD_SetOutputReport
    /// (the MS Bluetooth stack rejects WriteFile for Wii output reports; same
    /// constraint the #116 pairing work hit), on a second handle that coexists
    /// with SDL's hidapi_wii handle (output reports do not fight the report-mode
    /// machine, unlike IR input).
    ///
    /// Runtime is hypothesis-under-test: no Wii Remote hardware has validated
    /// this. The encoder + register layout are grounded; the exact sample-rate
    /// value/endianness, the volume level, and the rumble-bit coexistence with
    /// SDL are the residuals a hardware pass resolves.
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
        // Speaker playback rate: 6000000 / 2000 = 3000 Hz (Speaker.cpp:98/135).
        private const ushort SampleRateReg = 2000;
        private const int WiiRate = 3000;
        private const int Decim = MixRate / WiiRate;        // 16 input frames per output sample
        private const int FrameSamples = 40;                // 40 samples -> 20 ADPCM bytes (DATA_SIZE)
        private const int FramePeriodMs = FrameSamples * 1000 / WiiRate; // ~13 ms
        private const byte SpeakerVolume = 0x40;            // WiiBrew-typical; tune on hardware
        // Stop sending data after this much continuous silence so the radio
        // link is not spammed (and SDL rumble is left alone). Re-arms on audio.
        private const int IdleStopMs = 1500;

        private sealed class Sink
        {
            public Guid DeviceGuid;
            public int Slot;
            public string HidPath;
            public IntPtr Handle = IntPtr.Zero;
            public MixingSampleProvider MacroMixer;
            public Thread Thread;
            public volatile bool Running;
            public WiiSpeakerAdpcm.State Adpcm = WiiSpeakerAdpcm.State.Initial;
            public long LastAudibleTicks;
            public bool Streaming;
        }

        private static readonly object _lock = new();
        private static readonly List<Sink> _sinks = new();
        private static volatile bool _suppressed;
        private static Timer _reconcileTimer;

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
        private static readonly IntPtr INVALID = new IntPtr(-1);
        // Wiimote output report buffer length (report id + 21 payload bytes).
        private const int ReportLen = 22;

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

            lock (_lock)
            {
                // Tear down sinks no longer desired.
                for (int i = _sinks.Count - 1; i >= 0; i--)
                {
                    var s = _sinks[i];
                    bool keep = desired.Exists(d => d.Guid == s.DeviceGuid && d.Slot == s.Slot);
                    if (!keep) { TeardownSink(s); _sinks.RemoveAt(i); }
                }
                // Build newly-desired sinks.
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
                    BuildSink(sink);
                    _sinks.Add(sink);
                }
            }
        }

        private static void BuildSink(Sink s)
        {
            try
            {
                s.Handle = CreateFileW(s.HidPath, GENERIC_WRITE | GENERIC_READ, SHARE_RW,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (s.Handle == INVALID || s.Handle == IntPtr.Zero)
                {
                    s.Handle = IntPtr.Zero;
                    return; // device busy / no raw access; sink stays mixer-only (inert)
                }
                InitSpeaker(s.Handle);
                s.Running = true;
                s.Thread = new Thread(() => StreamLoop(s)) { IsBackground = true, Name = "PadForge Wii Speaker" };
                s.Thread.Start();
            }
            catch
            {
                TeardownSink(s);
            }
        }

        private static void TeardownSink(Sink s)
        {
            s.Running = false;
            try { s.Thread?.Join(500); } catch { }
            if (s.Handle != IntPtr.Zero)
            {
                try { WriteReport(s.Handle, 0x19, 0x04); } catch { } // mute
                try { WriteReport(s.Handle, 0x14, 0x00); } catch { } // disable speaker
                try { CloseHandle(s.Handle); } catch { }
                s.Handle = IntPtr.Zero;
            }
        }

        // ── Wii speaker init: the WiiBrew sequence, register offsets grounded
        //    in dolphin Speaker.h Register{} ──
        private static void InitSpeaker(IntPtr h)
        {
            WriteReport(h, 0x14, 0x04);                       // enable speaker (bit2)
            WriteReport(h, 0x19, 0x04);                       // mute while configuring
            WriteRegister(h, 0xa20009, new byte[] { 0x01 });
            WriteRegister(h, 0xa20001, new byte[] { 0x08 });
            // 7-byte config written to register 0xa20001 -> offsets 0x01..0x07:
            // [unk_1, format(0x02), rate_lo(0x03), rate_hi(0x04), volume(0x05), unk, unk].
            // sample_rate is little-endian (dolphin reads reg_data.sample_rate as
            // a native u16 with no swap on its LE host).
            WriteRegister(h, 0xa20001, new byte[]
            {
                0x00,
                WiiSpeakerAdpcm_FormatAdpcm,               // 0x00 = 4-bit Yamaha ADPCM
                (byte)(SampleRateReg & 0xFF),
                (byte)(SampleRateReg >> 8),
                SpeakerVolume,
                0x00, 0x00,
            });
            WriteRegister(h, 0xa20008, new byte[] { 0x01 });
            WriteReport(h, 0x19, 0x00);                       // unmute
        }

        private const byte WiiSpeakerAdpcm_FormatAdpcm = 0x00;

        // 0x16 WriteData: [0x16, 0x04|rumble, addr(3 BE), len, data(<=16)].
        // byte1 bit2 (0x04) selects the control-register address space; rumble
        // bit kept 0 (see class-doc residual on SDL coexistence).
        private static void WriteRegister(IntPtr h, int offset24, byte[] data)
        {
            var buf = new byte[ReportLen];
            buf[0] = 0x16;
            buf[1] = 0x04;
            buf[2] = (byte)((offset24 >> 16) & 0xFF);
            buf[3] = (byte)((offset24 >> 8) & 0xFF);
            buf[4] = (byte)(offset24 & 0xFF);
            buf[5] = (byte)data.Length;
            Array.Copy(data, 0, buf, 6, Math.Min(data.Length, 16));
            HidD_SetOutputReport(h, buf, buf.Length);
        }

        private static void WriteReport(IntPtr h, byte reportId, byte value)
        {
            var buf = new byte[ReportLen];
            buf[0] = reportId;
            buf[1] = value;
            HidD_SetOutputReport(h, buf, buf.Length);
        }

        // 0x18 SpeakerData: [0x18, (len<<3)|rumble, <up to 20 ADPCM bytes>].
        private static void WriteSpeakerData(IntPtr h, byte[] adpcm, int len)
        {
            var buf = new byte[ReportLen];
            buf[0] = 0x18;
            buf[1] = (byte)((len << 3) & 0xF8); // rumble bit 0 left clear
            Array.Copy(adpcm, 0, buf, 2, Math.Min(len, 20));
            HidD_SetOutputReport(h, buf, buf.Length);
        }

        // ── Stream thread: pull 48k stereo from the macro mixer, decimate to
        //    3k mono, ADPCM-encode carrying state, frame into paced 0x18 ──
        private static void StreamLoop(Sink s)
        {
            var stereo = new float[FrameSamples * Decim * 2]; // 40*16*2 floats per frame
            var mono = new short[FrameSamples];
            long nextTick = Environment.TickCount64;

            while (s.Running)
            {
                int got = 0;
                try { got = s.MacroMixer.Read(stereo, 0, stereo.Length); } catch { }
                // ReadFully mixers return the full count (zero-filled when idle).
                bool audible = false;
                for (int i = 0; i < FrameSamples; i++)
                {
                    double acc = 0;
                    int baseIdx = i * Decim * 2;
                    for (int k = 0; k < Decim; k++)
                    {
                        int idx = baseIdx + k * 2;
                        float l = idx < got ? stereo[idx] : 0f;
                        float r = idx + 1 < got ? stereo[idx + 1] : 0f;
                        acc += (l + r) * 0.5;
                    }
                    double avg = acc / Decim;
                    if (avg > 0.0008 || avg < -0.0008) audible = true;
                    int v = (int)Math.Round(avg * 32767.0);
                    mono[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
                }

                long now = Environment.TickCount64;
                if (audible) s.LastAudibleTicks = now;
                bool wantStream = (now - s.LastAudibleTicks) < IdleStopMs;

                if (wantStream && s.Handle != IntPtr.Zero)
                {
                    byte[] enc = WiiSpeakerAdpcm.Encode(mono, ref s.Adpcm); // 40 -> 20 bytes, state carried
                    try { WriteSpeakerData(s.Handle, enc, enc.Length); } catch { }
                    s.Streaming = true;
                }
                else if (s.Streaming)
                {
                    // Reset the encoder so the next cue starts from a clean
                    // predictor/step (the decoder resets on a gap of no data).
                    s.Adpcm = WiiSpeakerAdpcm.State.Initial;
                    s.Streaming = false;
                }

                nextTick += FramePeriodMs;
                long sleep = nextTick - Environment.TickCount64;
                if (sleep < 1) { sleep = 1; nextTick = Environment.TickCount64; }
                Thread.Sleep((int)sleep);
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
