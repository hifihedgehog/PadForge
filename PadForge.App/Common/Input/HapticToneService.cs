using System;
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
    /// Plays macro sounds as HD-haptic TONES through Nintendo Switch HD Rumble
    /// (Joy-Con L/R, Switch Pro) and the Steam Controller 2015 haptic actuator
    /// (issue #147). The Valve/Nintendo analogue of the Sony speaker path
    /// (<see cref="AudioPassthroughService"/>) and the Wii speaker path
    /// (<see cref="WiiSpeakerService"/>): a controller assigned to a slot becomes
    /// an output sink whose <see cref="Sink.MacroMixer"/> is returned to
    /// <see cref="SoundMacroService"/> alongside the Sony and Wii sinks, so a
    /// macro PlaySound fans out to it with no macro-layer change.
    ///
    /// Fidelity ceiling, stated up front: a Switch HD Rumble coil and a Steam
    /// Controller actuator are single LRAs, so they play a TONE with an amplitude
    /// envelope, not PCM. Beeps, alerts, and melodic cues land. Speech and music
    /// do not. The macro mix is reduced per rumble tick to one (dominant
    /// frequency, amplitude) by <see cref="HapticToneReducer"/> and encoded by
    /// <see cref="HapticToneEncoder"/> to each device's wire bytes.
    ///
    /// Why raw HID (SDL is amplitude-only): the bundled SDL fork's Switch and
    /// Steam drivers hard-code the rumble carrier and vary only amplitude
    /// (SDL_hidapi_switch.c ActuallyRumble, SDL_hidapi_steam.c RumbleJoystick
    /// returns SDL_Unsupported). Tone playback needs the raw-HID writer pattern
    /// PadForge already built for the Wii speaker and the Sony speaker.
    ///
    /// Switch 2 is deliberately NOT here: no reference plays an audible tone on a
    /// Switch 2 actuator (controller.py defines en_tone/lf_freq/hf_freq but never
    /// sets them; the one PC project that drives Switch 2 frequency, TommyWabg's
    /// switch2-controllers fork, uses it to shape RUMBLE feel, never a melody), so
    /// the Switch 2 tone path was dropped from #147 rather than ship a tab that
    /// can never make sound.
    ///
    /// Protocol bytes are facts from the cloned references (joycon-singer
    /// rumble.h / main_pc.cpp, SteamControllerSinger main.cpp,
    /// Nintendo_Switch_Reverse_Engineering); the C# is original.
    ///
    /// Status: hardware-gated. The encoder cores are unit-tested, but the
    /// streaming sink, the PCM->tone reduction, and SDL coexistence (a raw writer
    /// racing SDL's amplitude-rumble thread on the same handle) are
    /// hypothesis-under-test until a Joy-Con / Pro / Steam controller validates
    /// them.
    /// </summary>
    internal static class HapticToneService
    {
        private const ushort NintendoVid = 0x057E;
        private const ushort ValveVid = 0x28DE;

        // Device families and the EXACT PIDs SDL sorts into each, mirrored from the
        // bundled SDL's controller_list.h (the authoritative VID/PID -> controller-type
        // table). Mirror that table rather than hand-picking PIDs, so a new transport or
        // revision of the same controller is never silently dropped (which is exactly
        // how a Triton-over-BLE Steam Controller 2026, 0x1303, got no Audio tab before).
        // Protocol per family is verified against SteamHapticsSinger / SteamControllerSinger
        // / joycon-singer; same-family PIDs are the same controller over a different
        // transport (wired / BLE / dongle) and share the report format.
        private enum Family { None, JoyConL, JoyConR, Pro, JoyConPair, Steam, Steam2026, SteamDeck }

        private static bool IsJoyConGen1(Family f) => f == Family.JoyConL || f == Family.JoyConR || f == Family.Pro || f == Family.JoyConPair;

        private static Family FamilyOf(Engine.Data.UserDevice ud)
        {
            if (ud == null) return Family.None;
            if (ud.VendorId == NintendoVid)
            {
                switch (ud.ProdId)
                {
                    case 0x2006: return Family.JoyConL;   // Switch Joy-Con (Left)
                    case 0x2007: return Family.JoyConR;   // Switch Joy-Con (Right)
                    // Combined gen-1 Joy-Con pair (SwitchJoyConPair, controller_list.h:589).
                    // SDL combines two Joy-Cons by default (HIDAPI_COMBINE_JOY_CONS = "1",
                    // SDL_hints.h:1623), so a paired set enumerates as 0x2008 -- without
                    // this it would get no Audio tab. Two coils, same 0x10 packet as the
                    // Pro (both halves filled). The handle reaches the Joy-Con its
                    // DevicePath resolves to; full dual-coil drive would need the second
                    // Joy-Con's path too, which the one-handle sink does not carry yet.
                    case 0x2008: return Family.JoyConPair;
                    case 0x2009: return Family.Pro;       // Switch Pro Controller
                    // Switch 2 (0x2066/0x2067/0x2068/0x2069) intentionally excluded:
                    // no reference plays a tone on its actuator (see class doc).
                }
            }
            else if (ud.VendorId == ValveVid)
            {
                switch (ud.ProdId)
                {
                    // Steam Controller, 2015 gen (k_eControllerType_SteamController):
                    // CHELL 0x1101, wired D0G 0x1102, BT D0G 0x1105/0x1106, dongle 0x1142.
                    case 0x1101: case 0x1102: case 0x1105: case 0x1106: case 0x1142:
                    // SteamControllerV2 (HEADCRAB prototype) 0x1201/0x1202: no dedicated
                    // reference, so it rides the closest documented protocol (2015).
                    // Unverified on hardware; rare prototype.
                    case 0x1201: case 0x1202:
                        return Family.Steam;
                    // Steam Deck built-in (k_eControllerType_SteamControllerNeptune).
                    case 0x1205:
                        return Family.SteamDeck;
                    // Steam Controller 2026 / Triton (k_eControllerType_SteamControllerTriton):
                    // controller 0x1302, its BLE id 0x1303, Proteus dongle 0x1304, Nereid
                    // dongle 0x1305. Same controller, same report format.
                    case 0x1302: case 0x1303: case 0x1304: case 0x1305:
                        return Family.Steam2026;
                    // 0x11ff = Steam Virtual Gamepad (not real hardware) -> None.
                }
            }
            return Family.None;
        }

        /// <summary>True when the device can play HD-haptic tones (gates the Audio
        /// tab, mirrors the Sony/Wii speaker checks).</summary>
        public static bool DeviceHasHaptics(Engine.Data.UserDevice ud) => FamilyOf(ud) != Family.None;

        /// <summary>Plays a fixed, known test tone (880 Hz, off the LRA resonance) on
        /// the device's haptics for <paramref name="durationMs"/>, driven straight to
        /// the encoder. It bypasses the mixer / resampler / pitch reducer entirely, so
        /// the pure tone is never pitch-detected (no garble) and the reducer never
        /// caches it (no bleed into a following macro). Matched by device GUID, not
        /// slot, because a device has exactly one sink and its mapped slot may differ
        /// from the pad index. Forces a clean re-arm so the tone attacks at 880
        /// regardless of prior state. Returns true if a haptic sink received it; the
        /// caller plays the audio beep only when this returns false (speaker devices).</summary>
        public static bool TriggerTestTone(Guid deviceGuid, float freqHz = 880f, int durationMs = 350)
        {
            if (deviceGuid == Guid.Empty) return false;
            bool found = false;
            lock (_lock)
            {
                foreach (var s in _sinks)
                    if (s.DeviceGuid == deviceGuid)
                    {
                        s.TestHz = freqHz;
                        s.TestUntilMs = Environment.TickCount64 + durationMs;
                        s.SteamOn = false;   // force the override's first tick to re-arm at 880
                        found = true;
                    }
            }
            return found;
        }

        // On-demand RemoteDriven sink creation state: one pending build at a
        // time per device, and a backoff window after a failed open so a
        // 100 Hz tone stream cannot spin CreateFile retries (audit F4).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _remoteSinkPending = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _remoteSinkFailUntil = new();

        /// <summary>A paired peer shipped one reduced haptic-tone frame for a local
        /// device it consumes over Remote Link (#138 x #147). Sets the direct-drive
        /// override on the device's sink; when the device has no locally-assigned
        /// sink (the owner may not map it to any slot), a RemoteDriven sink is
        /// created on demand and reaped by Reconcile once frames stop. Runs on the
        /// UDP receive thread, so it must NEVER block: sink construction (raw HID
        /// open + per-family init sleeps, up to ~800 ms) is queued to the thread
        /// pool and frames simply drop until the sink is live (audit F4). The
        /// field writes follow the TestHz idiom (value fields first, the Until
        /// gate last).</summary>
        public static void ApplyRemoteTone(Engine.Data.UserDevice ud, float toneHz, float amplitude)
        {
            if (ud == null || _suppressed) return;
            var fam = FamilyOf(ud);
            if (fam == Family.None) return;

            long now = Environment.TickCount64;
            long until = now + 250;
            bool found = false;
            lock (_lock)
            {
                foreach (var s in _sinks)
                    if (s.DeviceGuid == ud.InstanceGuid)
                    {
                        s.RemoteHz = toneHz;
                        s.RemoteAmp = Math.Clamp(amplitude, 0f, 1f);
                        s.RemoteUntilMs = until;
                        found = true;
                    }
            }
            if (found || amplitude <= 0f) return;

            // No sink yet: queue the build off this thread. One in flight per
            // device, and a failed open backs off before the next attempt.
            if (_remoteSinkFailUntil.TryGetValue(ud.InstanceGuid, out long failUntil) && now < failUntil)
                return;
            if (!_remoteSinkPending.TryAdd(ud.InstanceGuid, 0)) return;

            var guid = ud.InstanceGuid;
            var path = ud.DevicePath;
            var gamepad = ud.Device?.GamepadHandle ?? IntPtr.Zero;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sink = new Sink
                    {
                        DeviceGuid = guid,
                        Slot = -1,
                        Family = fam,
                        HidPath = path,
                        GamepadHandle = gamepad,
                        RemoteDriven = true,
                        RemoteHz = toneHz,
                        RemoteAmp = Math.Clamp(amplitude, 0f, 1f),
                        RemoteUntilMs = Environment.TickCount64 + 250,
                        MacroMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 2)) { ReadFully = true },
                    };
                    lock (_lock)
                    {
                        if (_suppressed) return;
                        // A slot sink may have appeared while we queued
                        // (Reconcile ran): it supersedes; do not add a second
                        // writer for the same handle (audit F5).
                        if (_sinks.Exists(s => s.DeviceGuid == guid)) return;
                        _sinks.Add(sink);
                    }
                    if (!BuildSink(sink))
                    {
                        lock (_lock) _sinks.Remove(sink);
                        _remoteSinkFailUntil[guid] = Environment.TickCount64 + 3000;
                    }
                    else
                    {
                        _remoteSinkFailUntil.TryRemove(guid, out _);
                    }
                }
                catch { _remoteSinkFailUntil[guid] = Environment.TickCount64 + 3000; }
                finally { _remoteSinkPending.TryRemove(guid, out _); }
            });
        }

        // Mixer rate matches SoundMacroService so decoded PCM mixes in cleanly.
        private const int MixRate = 48000;

        // Tone-reduction analysis runs on an anti-aliased mono downmix at this
        // rate. 8 kHz spans the full Joy-Con/Steam playable band (41..1252 Hz)
        // with headroom and keeps the per-tick autocorrelation cheap.
        private const int ReduceRate = 8000;
        // 100 Hz rumble cadence (10 ms). Matches the Wii speaker cadence: gentle
        // on the SDL-shared BT link, and within the Joy-Con HD Rumble update rate
        // joycon-singer streams at. One report per tick, never bursting.
        private const int TickHz = 100;
        private const int SamplesPerTick = ReduceRate / TickHz; // 80

        // Once a cue starts, keep streaming for this long after the last frame
        // above the content threshold, so quiet dips inside a cue do not break the
        // stream. Same idea as the Wii speaker HangoverMs.
        private const int HangoverMs = 300;

        // Joy-Con / Pro HD Rumble output report id (joycon-singer main_pc.cpp:62).
        private const byte JoyConRumbleReportId = 0x10;
        // Joy-Con subcommand (command) output report id (main_pc.cpp:92,105).
        private const byte JoyConCommandReportId = 0x01;

        private sealed class Sink
        {
            public Guid DeviceGuid;
            public int Slot;
            public Family Family;
            public string HidPath;
            public IntPtr Handle = IntPtr.Zero;
            // SDL gamepad handle, for Steam Controllers whose tone is sent through
            // SDL's connection (SDL_SendGamepadEffect) rather than a raw write handle.
            public IntPtr GamepadHandle = IntPtr.Zero;
            public MixingSampleProvider MacroMixer;
            // Anti-aliased mono downmix of MacroMixer to ReduceRate, so the stream
            // thread reads ReduceRate mono directly (same pattern as the Wii
            // speaker's WdlResamplingSampleProvider mono source).
            public ISampleProvider MonoSource;
            public HapticToneReducer Reducer;
            public Thread Thread;
            public volatile bool Running;

            // Per-sink rolling 4-bit timer for Joy-Con packets (pkt[1] = timer &
            // 0x0F, incremented per packet across init AND stream). joycon-singer
            // uses one global g_timer; PadForge keeps it PER-SINK so a second
            // controller never makes the firmware see the counter jump by two and
            // drop frames (the same per-device-state rule the Sony Ds5Seq follows;
            // never static).
            public byte JoyConTimer;

            // Output report length from HID caps (HidD_SetOutputReport / WriteFile
            // require EXACTLY this length; a short buffer is rejected with
            // ERROR_INVALID_PARAMETER). Queried, never hardcoded.
            public int OutLen = 64;
            // Feature report length (Steam 0x8F SET_FEATURE).
            public int FeatLen = 65;
            // Joy-Con write path probe: overlapped WriteFile when the BT/USB stack
            // accepts it, else synchronous HidD_SetOutputReport (the BT-Joy-Con
            // err-87 case, same split as the Wii speaker).
            public bool UseWriteFile;

            public long LastContentMs = long.MinValue / 2;

            // Joy-Con stream-edge state: while a cue is active we write a rumble
            // packet every tick, but when it ends we send ONE neutral and then go
            // quiet, instead of writing neutral at 100 Hz forever (which would
            // fight SDL's input reporting on the shared link). Mirrors the Wii
            // speaker, which only writes while streaming.
            public bool JoyConWasStreaming;

            // Steam re-arm state: the 0x8F square wave sustains (repeat 0x7FFF),
            // so we only re-send when the tone crosses on/off or shifts pitch,
            // not every tick (a control transfer per 10 ms would saturate).
            public bool SteamOn;
            public float SteamLastFreq;
            // Last amplitude sent on the gain-carrying paths (Triton 0x83), so a
            // sustained note re-arms when the envelope steps, not every tick. The
            // 0x8f square (2015 + Deck) has no working gain, so it ignores this.
            public float SteamLastAmp;
            // Wall-clock of the last re-arm burst on the Steam-family paths.
            // Busy audio (a macro's wobbling pitch) crosses the 3% pitch gate on
            // many consecutive ticks; the resulting edge flood is what wedged the
            // Triton's haptic engine into a garbled state that persisted into
            // later cues (observed on hardware, 2026-07-01). Bursts are capped at
            // SDL's own write cadence for this pad (40 ms,
            // TRITON_RUMBLE_RESEND_INTERVAL_MS in SDL_hidapi_steam_triton.c).
            public long SteamLastBurstMs;

            // Direct test-tone window (Audio-tab Test button). While TestUntilMs is in
            // the future the stream loop drives TestHz at full amplitude straight to
            // the encoder, NEVER through the mixer / resampler / pitch reducer. So a
            // pure tone is never pitch-detected (no garble) and the reducer never sees
            // it (no held-pitch bleed into the next macro). No beep is injected either.
            public float TestHz;
            public long TestUntilMs;

            // Remote Link lanes (#138 x #147).
            // Consumer side: the device lives on another machine (peer:// path).
            // The sink runs the mixer + reducer as usual but SHIPS the per-tick
            // (freq, amp) pair over the link instead of writing hardware.
            public bool Remote;
            // Owner side: a paired peer is driving this local device's tone.
            // Filled by ApplyRemoteTone from the UDP receive thread; the stream
            // loop plays it via the same direct-drive idiom as the test tone
            // (same benign torn-read tolerance: Until is written last). The
            // consumer already applied ITS slot volume, so the owner must not
            // scale again. RemoteDriven marks a sink created on demand for a
            // device with no local slot assignment; Reconcile keeps it alive
            // while frames stay fresh instead of tearing it down.
            public bool RemoteDriven;
            public float RemoteHz;
            public float RemoteAmp;
            public long RemoteUntilMs;

            // System-audio passthrough mirror (same option DualSense/Wii expose).
            public bool MirrorOn;
            public string MirrorSourceId = "";
            public WasapiLoopbackCapture MirrorCapture;
            public ISampleProvider MirrorInput;
        }

        private static readonly object _lock = new();
        private static readonly List<Sink> _sinks = new();
        private static volatile bool _suppressed;
        private static Timer _reconcileTimer;
        private static int _reconcileBusy;

        /// <summary>Starts the periodic reconcile so a Joy-Con/Pro/Steam controller
        /// assigned (or removed) mid-session builds/tears down its tone sink,
        /// mirroring the Sony and Wii services. Idempotent.</summary>
        public static void EnsureStarted()
        {
            lock (_lock)
            {
                _suppressed = false;
                if (_reconcileTimer != null) return;
                _reconcileTimer = new Timer(_ => { try { Reconcile(); } catch { } }, null, 0, 3000);
            }
        }

        // ── HID P/Invoke (same surface as the Wii speaker service) ──
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetOutputReport(IntPtr h, byte[] buffer, int bufferLength);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr h, byte[] buffer, int bufferLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr h, IntPtr buf, uint n, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr h, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attr, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        private const uint GENERIC_WRITE = 0x40000000, GENERIC_READ = 0x80000000;
        private const uint SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private static readonly IntPtr INVALID = new IntPtr(-1);

        // ── Combined Joy-Con child-path resolution (issue #184) ──
        // The combined pair (0x057E/0x2008) has the synthetic SDL path
        // "nintendo_joycons_combined", which CreateFileW cannot open. Enumerate HID
        // to find a real child Joy-Con path (Left 0x2006, then Right 0x2007).
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
        // Native return is BOOLEAN (1 byte), not BOOL. Marshal as U1 like the
        // XboxImpulseHidWriter sibling so a stale upper EAX can't read as true.
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attr);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }
        private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        /// <summary>Resolves a real HID path for one of the combined pair's child
        /// Joy-Cons (Left 0x2006 preferred, then Right 0x2007). Null if neither is
        /// found, in which case BuildSink's CreateFileW fails as before.</summary>
        private static string ResolveJoyConChildPath()
        {
            foreach (ushort pid in new ushort[] { 0x2006, 0x2007 })
            {
                string p = FindHidPath(NintendoVid, pid);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Enumerates present HID device interfaces and returns the first
        /// whose HIDD_ATTRIBUTES match vid/pid. Standard SetupDi walk. Internal so
        /// BluetoothLinkHelper can resolve the combined pair's children too (#184):
        /// the pair's SDL serial is empty (the combined driver's serial join reads
        /// joystick-&gt;serial, which the Switch driver stopped setting in 2022), so
        /// the disconnect path reads each child's own HID serial instead.</summary>
        internal static string FindHidPath(ushort vid, ushort pid)
        {
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID || set == IntPtr.Zero) return null;
            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref did); i++)
                {
                    // Zero 'need' each iteration: a failed size call that never
                    // writes RequiredSize must not reuse the previous device's size.
                    uint need = 0;
                    SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64, 6 on x86.
                        // The WCHAR DevicePath[] starts at offset 4 (right after cbSize).
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, detail, need, out _, IntPtr.Zero)) continue;
                        string devPath = Marshal.PtrToStringUni(detail + 4);
                        if (string.IsNullOrEmpty(devPath)) continue;
                        IntPtr h = CreateFileW(devPath, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == INVALID || h == IntPtr.Zero) continue;
                        try
                        {
                            var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                            if (HidD_GetAttributes(h, ref attr) && attr.VendorID == vid && attr.ProductID == pid)
                                return devPath;
                        }
                        finally { CloseHandle(h); }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            catch { /* enumeration failure -> null, sink falls back to failing open */ }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        private static (int outLen, int featLen) QueryReportLens(IntPtr h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp) || pp == IntPtr.Zero) return (0, 0);
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) < 0) return (0, 0);
                return (caps.OutputReportByteLength, caps.FeatureReportByteLength);
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        /// <summary>Returns the live macro-sink mixers for the slot's
        /// haptic-tone controllers, so SoundMacroService routes macro sounds in.
        /// Mirrors AudioPassthroughService/WiiSpeakerService.GetSlotSinkMixers.</summary>
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
        /// Called wherever the Sony/Wii reconciles are.</summary>
        public static void Reconcile()
        {
            if (_suppressed) return;
            if (Interlocked.Exchange(ref _reconcileBusy, 1) == 1) return;
            try
            {
                var desired = new List<(int Slot, Guid Guid, string Path, Family Fam, IntPtr Gamepad)>();
                var settings = SettingsManager.UserSettings;
                if (settings != null)
                {
                    var seen = new HashSet<Guid>();
                    var assigned = new List<(int MapTo, Guid Guid)>();
                    lock (settings.SyncRoot)
                    {
                        foreach (var us in settings.Items)
                        {
                            if (us == null || us.MapTo < 0) continue;
                            if (!seen.Add(us.InstanceGuid)) continue;
                            assigned.Add((us.MapTo, us.InstanceGuid));
                        }
                    }
                    // Resolve devices OUTSIDE the UserSettings lock. FindDeviceByInstanceGuid
                    // takes UserDevices.SyncRoot, and holding UserSettings.SyncRoot while
                    // acquiring it inverts UpdateDashboard's order (UserDevices then
                    // UserSettings) and deadlocks. Same snapshot-then-resolve shape as
                    // AudioPassthroughService.EnumerateAssignedSonyPads.
                    foreach (var (mapTo, guid) in assigned)
                    {
                        var ud = SettingsManager.FindDeviceByInstanceGuid(guid);
                        if (ud == null || !ud.IsOnline || string.IsNullOrEmpty(ud.DevicePath)) continue;
                        var fam = FamilyOf(ud);
                        if (fam == Family.None) continue;
                        // Capture the SDL gamepad handle: only the 2015 Steam path
                        // (Family.Steam) still sends its 0x8f tone through SDL's
                        // connection (SteamSendBlob). Joy-Con, Deck, and the 2026
                        // Triton all write their own raw HID handle opened from
                        // DevicePath; the handle is captured but unused for those.
                        desired.Add((mapTo, guid, ud.DevicePath, fam, ud.Device?.GamepadHandle ?? IntPtr.Zero));
                    }
                }

                var toBuild = new List<Sink>();
                var toTeardown = new List<Sink>();
                long staleNow = Environment.TickCount64;

                // Resolve RemoteDriven owners OUTSIDE _lock: the lookup takes
                // UserDevices.SyncRoot, and the rest of this file's discipline
                // is snapshot-then-resolve to keep lock orders one-way.
                List<Guid> remoteGuids;
                lock (_lock) remoteGuids = _sinks.Where(s => s.RemoteDriven).Select(s => s.DeviceGuid).ToList();
                var remoteOnline = new HashSet<Guid>();
                foreach (var g in remoteGuids)
                {
                    var owner = SettingsManager.FindDeviceByInstanceGuid(g);
                    if (owner != null && owner.IsOnline) remoteOnline.Add(g);
                }

                lock (_lock)
                {
                    if (_suppressed) return;
                    for (int i = _sinks.Count - 1; i >= 0; i--)
                    {
                        var s = _sinks[i];
                        if (s.RemoteDriven)
                        {
                            // Owner-side link-driven sink: not slot-derived, so
                            // the desired list never contains it. Alive while
                            // frames stay fresh (10 s) and the device is online.
                            // A slot assignment supersedes it: two sinks on one
                            // handle would interleave writes with independent
                            // rolling timers (audit F5). The slot sink takes
                            // over and ApplyRemoteTone drives it instead.
                            bool superseded = desired.Exists(d => d.Guid == s.DeviceGuid);
                            bool stale = !remoteOnline.Contains(s.DeviceGuid)
                                || (staleNow - s.RemoteUntilMs) > 10_000;
                            if (!superseded && !stale) continue;
                        }
                        else if (desired.Exists(d => d.Guid == s.DeviceGuid && d.Slot == s.Slot))
                        {
                            continue;
                        }
                        toTeardown.Add(s);
                        _sinks.RemoveAt(i);
                    }
                    foreach (var d in desired)
                    {
                        if (_sinks.Exists(s => s.DeviceGuid == d.Guid && s.Slot == d.Slot)) continue;
                        var sink = new Sink
                        {
                            DeviceGuid = d.Guid,
                            Slot = d.Slot,
                            Family = d.Fam,
                            HidPath = d.Path,
                            GamepadHandle = d.Gamepad,
                            // Consumer lane: a linked pad reduces locally and
                            // ships the tone pair; no hardware handle exists.
                            Remote = d.Path != null && d.Path.StartsWith("peer://", StringComparison.Ordinal),
                            MacroMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 2)) { ReadFully = true },
                        };
                        _sinks.Add(sink);
                        toBuild.Add(sink);
                    }
                }

                foreach (var s in toTeardown) TeardownSink(s);
                foreach (var s in toBuild)
                    if (!BuildSink(s))
                        lock (_lock) _sinks.Remove(s);

                ReconcileMirrors();
            }
            finally { Interlocked.Exchange(ref _reconcileBusy, 0); }
        }

        // ── Haptic mirror engage gate (#185) ──
        // The mirror buzzes the pad with everything the system plays, so it can
        // gate on a held input or on game rumble instead of running always. The
        // gate is PER (slot, device), matching ReconcileMirrors' own
        // c.Device == s.DeviceGuid scoping: each sink is gated only by ITS
        // device's config. The first cut keyed one bit per SLOT and walked the
        // slot's configs "first non-Always wins", which let a stale
        // passthrough-enabled config on ANOTHER device GUID (an old instance
        // resurrected at load, or a paste fan-out) mute every sink on the slot
        // even with the selected device on Always (the Steam Controller
        // Always-silent report). Cells are settled once per poll by
        // InputManager's UpdateHapticMirrorEngageStates (third member of the
        // engage family beside gyro and trigger-route), including the per-cell
        // release-delay hold, so the stream thread only reads one bool field.
        // Default true = Always. Macro sounds are never gated: the gate wraps
        // ONLY the mirror's mixer input.

        /// <summary>One device's engage state. Engaged is written by the poll
        /// thread and read by the sink's stream thread (plain volatile bool,
        /// same torn-read tolerance as the rest of the file). LastActiveTick
        /// backs the release-delay hold and is per cell, so two gated devices
        /// on one slot hold independently.</summary>
        internal sealed class EngageCell
        {
            public volatile bool Engaged = true;
            public long LastActiveTick;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Slot, Guid Device), EngageCell>
            _engageCells = new();

        /// <summary>Resolves the engage cell for a (slot, device) pair,
        /// creating it engaged. Called by StartMirror (once per mirror start)
        /// and by InputManager's 4 Hz config refresh, never on the per-poll or
        /// audio paths.</summary>
        internal static EngageCell GetOrCreateEngageCell(int slot, Guid device)
            => _engageCells.GetOrAdd((slot, device), _ => new EngageCell());

        /// <summary>The engage-hold decision shared by the poll-thread updater
        /// and the unit tests: engaged while the source is active, and for
        /// <paramref name="releaseMs"/> after it drops (the release delay that
        /// stops the tone clipping off instantly). Clamps the delay to the
        /// UI's documented 0..10000 range.</summary>
        internal static bool HoldEngaged(bool active, long nowTick, ref long lastActiveTick, int releaseMs)
        {
            if (active) lastActiveTick = nowTick;
            int clamped = Math.Clamp(releaseMs, 0, 10000);
            return active || (nowTick - lastActiveTick) <= clamped;
        }

        /// <summary>Wraps the mirror's sample provider: keeps draining the
        /// loopback buffer while disengaged (so audio never backs up or bursts
        /// stale samples on re-engage) but zeroes the output, which the reducer
        /// reads as silence and the tone stops through the existing
        /// stop-of-stream neutral. Always returns the inner count, so the
        /// MixingSampleProvider never auto-removes the input. Holds its
        /// device's EngageCell directly: zero lookups on the audio thread.
        /// Internal (visible to the tests via InternalsVisibleTo). Only
        /// StartMirror constructs it in production.</summary>
        internal sealed class GatedMirrorSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _inner;
            private readonly EngageCell _cell;
            public GatedMirrorSampleProvider(ISampleProvider inner, EngageCell cell)
            {
                _inner = inner;
                _cell = cell;
            }
            public WaveFormat WaveFormat => _inner.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            {
                int n = _inner.Read(buffer, offset, count);
                if (_cell != null && !_cell.Engaged)
                    Array.Clear(buffer, offset, n);
                return n;
            }
        }

        // System-audio loopback mirror, identical shape to WiiSpeakerService.
        private static void ReconcileMirrors()
        {
            if (_suppressed) return;
            var provider = AudioPassthroughService.PassthroughConfigProvider;
            List<Sink> live;
            lock (_lock) live = _suppressed ? new List<Sink>() : _sinks.ToList();
            foreach (var s in live)
            {
                if (_suppressed) break;
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
                    StopMirror(s);
                    bool stillLive;
                    lock (_lock) stillLive = !_suppressed && _sinks.Contains(s);
                    if (stillLive) StartMirror(s, wantSrc);
                }
                else if (!wantOn && s.MirrorOn) StopMirror(s);
            }
        }

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
                dev.Dispose();
                s.MirrorCapture = cap;

                ISampleProvider sp = buf.ToSampleProvider();
                if (sp.WaveFormat.SampleRate != MixRate) sp = new WdlResamplingSampleProvider(sp, MixRate);
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                else if (sp.WaveFormat.Channels != 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2);

                // #185: the engage gate wraps ONLY the mirror branch, never the
                // mixer itself, so macro sounds keep playing while disengaged.
                // The cell is this DEVICE's, so another device's config on the
                // same slot can never gate this sink.
                sp = new GatedMirrorSampleProvider(sp, GetOrCreateEngageCell(s.Slot, s.DeviceGuid));

                s.MacroMixer.AddMixerInput(sp);
                s.MirrorInput = sp;
                s.MirrorOn = true;
                s.MirrorSourceId = endpointId ?? "";
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

        /// <summary>Opens the device, runs the per-family init, starts the stream
        /// thread. Returns false if the handle could not be opened (caller drops
        /// the sink to retry). Runs OUTSIDE _lock. Same commit/race discipline as
        /// WiiSpeakerService.BuildSink.</summary>
        private static bool BuildSink(Sink s)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                // The 2026 Triton is driven directly, like every other device:
                // its tone is an OUTPUT report (0x83 LFO tone), which SDL's Steam
                // effect API does not forward (it only sends FEATURE reports). The
                // old "route through SDL, no raw handle" path was built on the
                // belief that a second BT handle caused the ~985 Hz poll dip; that
                // dip is the upstream SDL lizard-mode feature write every 3 s,
                // independent of any second handle (and PadForge already opens its
                // own write handle to a BT DualSense that SDL also owns). Only the
                // 2015 Steam path (Family.Steam, classic 0x8f via SteamSendBlob)
                // still has an SDL lane.
                bool steamViaSdl = s.Family == Family.Steam && s.GamepadHandle != IntPtr.Zero;
                if (s.Remote)
                {
                    // Consumer lane: no hardware here. The peer's machine owns
                    // the handle; this sink only reduces and ships.
                }
                else if (!steamViaSdl)
                {
                    // The combined Joy-Con pair's SDL path is a synthetic placeholder
                    // ("nintendo_joycons_combined", SDL_hidapijoystick.c:1088), not a
                    // real \\?\HID#... path, so CreateFileW fails and the pair gets no
                    // tone (issue #184). Both physical Joy-Cons are still present as
                    // real HID devices (0x057E/0x2006 L, 0x2007 R); resolve one real
                    // path so the sink opens and plays on that coil. One handle drives
                    // one coil; full dual-coil is a documented follow-up.
                    string path = s.HidPath;
                    if (s.Family == Family.JoyConPair &&
                        (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal)))
                    {
                        string child = ResolveJoyConChildPath();
                        if (child != null) path = child;
                    }
                    h = CreateFileW(path, GENERIC_WRITE | GENERIC_READ, SHARE_RW,
                        IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                    if (h == INVALID || h == IntPtr.Zero) return false;

                    var (capOut, capFeat) = QueryReportLens(h);
                    s.OutLen = capOut > 0 ? capOut : 64;
                    s.FeatLen = capFeat > 0 ? capFeat : 65;

                    if (IsJoyConGen1(s.Family))
                    {
                        // Joy-Con gen-1 init: set input report mode 0x30 then enable
                        // vibration 0x48, each an 0x01-prefixed 12-byte command packet
                        // with the rolling timer byte, 50 ms apart. open_controller
                        // does set_input_mode (main_pc.cpp:131) then enable_vibration
                        // (132); reproduce that order.
                        s.UseWriteFile = ProbeWriteFile(h, s.OutLen);
                        JoyConSendCommand(h, s, subcommand: 0x03, arg: 0x30); // input report mode 0x30
                        Thread.Sleep(50);
                        JoyConSendCommand(h, s, subcommand: 0x48, arg: 0x01); // enable vibration
                        Thread.Sleep(50);
                    }
                    // Steam 2015 (no SDL gamepad) / Deck need no init: each feature
                    // write (HidD_SetFeature, report id 0x00) is self-contained.
                }

                var mono = new StereoToMonoSampleProvider(s.MacroMixer) { LeftVolume = 0.5f, RightVolume = 0.5f };
                var resampled = new WdlResamplingSampleProvider(mono, ReduceRate);
                lock (_lock)
                {
                    if (_suppressed || !_sinks.Contains(s))
                    {
                        try { CloseHandle(h); } catch { }
                        h = IntPtr.Zero;
                        return true; // race lost: sink already dropped
                    }
                    s.Handle = h;
                    h = IntPtr.Zero;
                    s.MonoSource = resampled;
                    s.Reducer = new HapticToneReducer(ReduceRate);
                    s.Running = true;
                    s.Thread = new Thread(() => StreamLoop(s)) { IsBackground = true, Name = "PadForge HD Haptic" };
                    s.Thread.Start();
                }
                return true;
            }
            catch
            {
                if (h != IntPtr.Zero && h != INVALID) { try { CloseHandle(h); } catch { } }
                TeardownSink(s);
                return false;
            }
        }

        private static void TeardownSink(Sink s)
        {
            try { StopMirror(s); } catch { }
            s.Running = false;
            bool exited = true;
            try { exited = s.Thread?.Join(3000) ?? true; } catch { exited = true; }
            if (exited && (s.Handle != IntPtr.Zero || s.GamepadHandle != IntPtr.Zero))
            {
                // Quiet the actuator on the way out, per family.
                try
                {
                    switch (s.Family)
                    {
                        case Family.Steam: SteamStop(s); break;     // 2015: classic 0x8f feature stop (both haptics)
                        case Family.Steam2026: TritonStop(s); break; // Triton: 0x83 stop on all 4 actuators
                        case Family.SteamDeck:                       // Deck: 0x8F note-off on both haptics (same as 2015)
                            SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 0));
                            SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 1));
                            break;
                        default: JoyConWriteRumble(s, HapticToneEncoder.JoyConNeutral()); break;
                    }
                }
                catch { }
                if (s.Handle != IntPtr.Zero)
                {
                    try { CancelIo(s.Handle); } catch { }
                    try { CloseHandle(s.Handle); } catch { }
                }
            }
            s.Handle = IntPtr.Zero;
            s.MonoSource = null;
        }

        // One benign overlapped WriteFile (a neutral rumble packet) to learn
        // whether this stack accepts the fast path (else HidD_SetOutputReport,
        // the BT-Joy-Con err-87 fallback). Same probe shape as the Wii speaker.
        private static bool ProbeWriteFile(IntPtr h, int outLen)
        {
            int n = outLen < 10 ? 10 : outLen;
            var buf = new byte[n];
            buf[0] = JoyConRumbleReportId;
            buf[1] = 0x00;
            var neutral = HapticToneEncoder.JoyConNeutral();
            Array.Copy(neutral, 0, buf, 2, 4);
            Array.Copy(neutral, 0, buf, 6, 4);
            GCHandle pin = default; IntPtr ev = IntPtr.Zero, ol = IntPtr.Zero;
            try
            {
                pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                ev = CreateEventW(IntPtr.Zero, true, false, null);
                ol = Marshal.AllocHGlobal(32);
                for (int o = 0; o < 24; o += 8) Marshal.WriteInt64(ol, o, 0);
                Marshal.WriteIntPtr(ol, 24, ev);
                if (WriteFile(h, pin.AddrOfPinnedObject(), (uint)n, IntPtr.Zero, ol)) return true;
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (WaitForSingleObject(ev, 500) != 0)
                {
                    // Drain the cancelled write before the finally frees the
                    // pinned buffer + OVERLAPPED (use-after-free otherwise; see
                    // OverlappedWrite and WiiSpeakerService BtWritePool.Dispose).
                    try { CancelIo(h); } catch { }
                    try { WaitForSingleObject(ev, 200); } catch { }
                    return false;
                }
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

        // ── Joy-Con / Pro packet builders (joycon-singer main_pc.cpp) ──

        // Command packet: [0x01, timer&0x0F, neutral(4), neutral(4), subcommand,
        // arg] padded to OutLen (main_pc.cpp:90-99 enable_vibration / 103-113
        // set_input_mode). The rolling timer advances per packet.
        private static void JoyConSendCommand(IntPtr h, Sink s, byte subcommand, byte arg)
        {
            var buf = new byte[s.OutLen < 12 ? 12 : s.OutLen];
            buf[0] = JoyConCommandReportId;
            buf[1] = (byte)(s.JoyConTimer++ & 0x0F);
            var neutral = HapticToneEncoder.JoyConNeutral();
            Array.Copy(neutral, 0, buf, 2, 4);
            Array.Copy(neutral, 0, buf, 6, 4);
            buf[10] = subcommand;
            buf[11] = arg;
            // Fire-and-forget init write: a failed subcommand just means this cue
            // won't vibrate, never a crash (same swallow idiom as the Wii/Sony
            // hardware writes). The stream's first rumble packet re-asserts state.
            try { HidD_SetOutputReport(h, buf, buf.Length); } catch { }
        }

        // Rumble packet: [0x10, timer&0x0F, left4, right4] padded to OutLen
        // (main_pc.cpp:54-86). Single Joy-Cons get their 4 bytes in the correct
        // half and neutral in the other; Pro sends the same tone in both halves.
        private static void JoyConWriteRumble(Sink s, byte[] group4)
        {
            if (s.Handle == IntPtr.Zero) return;
            var buf = new byte[s.OutLen < 10 ? 10 : s.OutLen];
            buf[0] = JoyConRumbleReportId;
            buf[1] = (byte)(s.JoyConTimer++ & 0x0F);
            var neutral = HapticToneEncoder.JoyConNeutral();
            byte[] left = s.Family == Family.JoyConR ? neutral : group4;
            byte[] right = s.Family == Family.JoyConL ? neutral : group4;
            Array.Copy(left, 0, buf, 2, 4);
            Array.Copy(right, 0, buf, 6, 4);
            HidOutputWrite(s, buf);
        }

        // Output write with the probed path: overlapped WriteFile, else
        // synchronous HidD_SetOutputReport (BT-Joy-Con err-87 fallback). Used by
        // the Joy-Con 0x10 rumble lane and the Triton 0x80/0x83 output lane.
        private static void HidOutputWrite(Sink s, byte[] buf)
        {
            if (s.UseWriteFile)
            {
                if (OverlappedWrite(s.Handle, buf)) return;
                s.UseWriteFile = false; // fall back for the rest of the cue
            }
            // Fire-and-forget rumble write: a dropped tick is inaudible, never a
            // crash. Same swallow idiom as the OverlappedWrite/ProbeWriteFile path.
            try { HidD_SetOutputReport(s.Handle, buf, buf.Length); } catch { }
        }

        // Single in-order overlapped WriteFile that blocks until the write
        // completes (the same one-at-a-time discipline the Wii WiiWritePool uses,
        // so the slow shared BT link never reorders packets).
        private static bool OverlappedWrite(IntPtr h, byte[] buf)
        {
            GCHandle pin = default; IntPtr ev = IntPtr.Zero, ol = IntPtr.Zero;
            try
            {
                pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                ev = CreateEventW(IntPtr.Zero, true, false, null);
                ol = Marshal.AllocHGlobal(32);
                for (int o = 0; o < 24; o += 8) Marshal.WriteInt64(ol, o, 0);
                Marshal.WriteIntPtr(ol, 24, ev);
                if (WriteFile(h, pin.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ol)) return true;
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (WaitForSingleObject(ev, 1000) != 0)
                {
                    // Timed out with the write still in flight. CancelIo only
                    // REQUESTS cancellation; the kernel/BT stack keeps a reference
                    // to the pinned buffer + native OVERLAPPED until the (cancelled)
                    // completion fires, so the finally must NOT free them yet.
                    // Drain on the event first, exactly as WiiSpeakerService
                    // BtWritePool.Dispose (which cites the Sony BtWritePool).
                    try { CancelIo(h); } catch { }
                    try { WaitForSingleObject(ev, 200); } catch { }
                    return false;
                }
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

        // ── Steam Controller 2015 (0x8F SET_FEATURE via SDL) ──
        // Only the 2015 (Family.Steam) uses this now. The Triton drives its own raw
        // handle (TritonSend). SteamControllerSinger sends the 64-byte 0x8F blob via
        // libusb SET_REPORT (feature, id 0); on Windows HID that is the report-id
        // byte (0x00) prepended, a 65-byte feature report.
        private static void SteamSendBlob(Sink s, byte[] blob64)
        {
            if (s.GamepadHandle != IntPtr.Zero)
            {
                // SDL's 2015 Steam driver forwards the feature ONLY at size == 65
                // (SDL_hidapi_steam.c:1307: SetFeatureReport of report-id 0x00 + the
                // 64-byte 0x8f blob). Any other size returns SDL_Unsupported and
                // silently drops the tone. The previous 64-byte send both missed that
                // gate AND truncated blob64's last byte (Min(64, 63)).
                var eff = new byte[65];
                eff[0] = 0x00;
                Array.Copy(blob64, 0, eff, 1, Math.Min(blob64.Length, eff.Length - 1));
                try { SDL3.SDL.SDL_SendGamepadEffect(s.GamepadHandle, eff, 0, eff.Length); } catch { }
                return;
            }
            // Fallback: a raw feature write (no SDL gamepad handle, e.g. a 2015 pad
            // SDL did not open as a gamepad).
            if (s.Handle == IntPtr.Zero) return;
            int n = s.FeatLen > 0 ? s.FeatLen : blob64.Length + 1;
            var buf = new byte[n];
            buf[0] = 0x00;
            Array.Copy(blob64, 0, buf, 1, Math.Min(blob64.Length, n - 1));
            try { HidD_SetFeature(s.Handle, buf, buf.Length); } catch { }
        }

        private static void SteamStop(Sink s)
        {
            // EncodeSteamClassic(<=0 Hz) emits the reference NOTE_STOP blob
            // (note 0, duration 0) per main.cpp:114-117. Stop both haptics.
            SteamSendBlob(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 0));
            SteamSendBlob(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 1));
            s.SteamOn = false;
        }

        // ── Stream thread: reduce the mono mix to (freq, amp) per tick, encode
        //    per family, write one report. One report per tick, never bursting. ──
        private static void StreamLoop(Sink s)
        {
            var monoF = new float[SamplesPerTick];
            // Idle at BelowNormal so a silent haptic sink never preempts the input
            // poll loop (that was occasionally dropping it to ~985 Hz). Only raise to
            // AboveNormal + the 1 ms global timer while a tone is actually streaming.
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            bool fast = false;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long freq = System.Diagnostics.Stopwatch.Frequency;
                long intervalTicks = freq / TickHz;
                long next = sw.ElapsedTicks + intervalTicks;

                while (s.Running)
                {
                    int got = 0;
                    try { got = s.MonoSource.Read(monoF, 0, SamplesPerTick); } catch { }
                    for (int i = got; i < SamplesPerTick; i++) monoF[i] = 0f;

                    // Cheap silence gate: an idle sink must not run the per-tick
                    // frequency reduction (an autocorrelation/FFT) on pure silence.
                    // That was ~14% of a core per assigned haptic pad doing nothing.
                    // Scan the peak first; only reduce when there is real signal.
                    long nowMs = Environment.TickCount64;
                    float toneHz, amp;
                    bool testActive = s.TestUntilMs > nowMs;
                    bool remoteActive = !testActive && s.RemoteUntilMs > nowMs;
                    if (testActive)
                    {
                        // Direct fixed test tone: a KNOWN frequency driven straight to
                        // the encoder, never through the mixer/resampler/pitch reducer.
                        // A pure tone is never detected (no garble) and the reducer never
                        // caches it (no bleed). The mono mix is drained but unused; no
                        // beep is injected for haptic devices (see the Test button).
                        toneHz = s.TestHz; amp = 1.0f;
                    }
                    else if (remoteActive)
                    {
                        // Owner lane (#138 x #147): a linked consumer reduced its
                        // macro mix and shipped the pair; drive it straight to the
                        // encoder, same direct idiom as the test tone.
                        toneHz = s.RemoteHz; amp = s.RemoteAmp;
                    }
                    else
                    {
                        // Cheap silence gate: an idle sink must not run the per-tick
                        // frequency reduction (autocorrelation) on pure silence.
                        float peak = 0f;
                        for (int i = 0; i < SamplesPerTick; i++) { float a = monoF[i]; if (a < 0f) a = -a; if (a > peak) peak = a; }
                        if (peak <= 0.002f) { toneHz = 0f; amp = 0f; }
                        else { (toneHz, amp) = s.Reducer.Push(monoF, SamplesPerTick); }
                    }

                    // On-controller sinks own their loudness (SoundMacroService
                    // deliberately skips master-volume scaling for OnController
                    // placements; the Sony dispatcher applies the slot volume as
                    // the firmware volume byte per report). Apply the same slot
                    // volume here at the sink: it reaches the Triton/Joy-Con gain
                    // and gates the pitch-only 0x8F square. Covers macro sounds,
                    // the system-audio mirror, and the Test button alike. Remote
                    // frames are exempt: the consumer applied ITS slot volume
                    // before shipping, and the owner is a pure transcoder.
                    if (!remoteActive)
                        amp *= Math.Clamp(SoundMacroService.GetSlotVolume(s.Slot) / 100f, 0f, 1f);

                    bool audible = amp > 0.02f;
                    if (audible) s.LastContentMs = nowMs;
                    bool streaming = testActive || (nowMs - s.LastContentMs) < HangoverMs;

                    if (s.Remote)
                    {
                        // Consumer lane: ship the reduced pair to the owner while
                        // anything plays; the zero frame that ends the stream is
                        // sent once (the router dedups silent steady state).
                        if (streaming || amp > 0f)
                            RemoteLinkOutputRouter.ShipHapticTone(s.HidPath, toneHz, amp);
                    }
                    // Run when we have either a raw write handle or an SDL gamepad
                    // handle (the Steam SDL_SendGamepadEffect path needs no raw handle).
                    else if (s.Handle != IntPtr.Zero || s.GamepadHandle != IntPtr.Zero)
                    {
                        switch (s.Family)
                        {
                            // 2015 Steam Controller: classic 0x8f TriggerHapticPulse FEATURE report.
                            case Family.Steam: StreamSteamTick(s, toneHz, amp, streaming); break;
                            // SC2026 (Triton): the 0x83 LFO-tone OUTPUT report written directly to our
                            // own HID handle. The Triton does not use 0x8f (confirmed: Valve's SDL
                            // driver and OpenPuck's real-capture both drive it via output reports only).
                            case Family.Steam2026: StreamTritonTick(s, toneHz, amp, streaming); break;
                            case Family.SteamDeck: StreamSteamDeckTick(s, toneHz, amp, streaming); break;
                            default: StreamJoyConTick(s, toneHz, amp, streaming); break;
                        }
                    }

                    if (streaming)
                    {
                        if (!fast)
                        {
                            timeBeginPeriod(1);
                            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
                            fast = true;
                        }
                        long spin = freq / 2000; // ~0.5 ms
                        while (s.Running && (next - sw.ElapsedTicks) > spin) Thread.Sleep(1);
                        while (s.Running && sw.ElapsedTicks < next) Thread.SpinWait(16);
                        next += intervalTicks;
                    }
                    else
                    {
                        if (fast)
                        {
                            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                            timeEndPeriod(1);
                            fast = false;
                        }
                        Thread.Sleep(15); // idle: coarse timer, minimal CPU, no poll preemption
                        next = sw.ElapsedTicks + intervalTicks;
                    }
                }
            }
            finally { if (fast) timeEndPeriod(1); }
        }

        private static void StreamJoyConTick(Sink s, float toneHz, float amp, bool streaming)
        {
            if (streaming)
            {
                // Cue active: drive the coil every tick (quiet dips included) so
                // the firmware FIFO stays fed.
                JoyConWriteRumble(s, HapticToneEncoder.EncodeJoyConRumble(toneHz, amp));
                s.JoyConWasStreaming = true;
            }
            else if (s.JoyConWasStreaming)
            {
                // Cue just ended: stop the coil with ONE neutral, then go quiet
                // (no 100 Hz neutral spam fighting SDL on the shared link).
                JoyConWriteRumble(s, HapticToneEncoder.JoyConNeutral());
                s.JoyConWasStreaming = false;
            }
        }

        private static void StreamSteamTick(Sink s, float toneHz, float amp, bool streaming)
        {
            // The 0x8F square wave sustains (repeat 0x7FFF), so re-arm only on an
            // on->off / off->on edge or a pitch shift, not every tick. The 2015 pad
            // has TWO haptics (left + right grip); SteamHapticsSinger drives channels
            // 0 and 1, so play the tone on both for full output (the 0x8F square has
            // no working gain, so amplitude is not encoded -- pitch only).
            if (streaming)
            {
                bool firstArm = !s.SteamOn;
                bool pitchShift = firstArm || Math.Abs(toneHz - s.SteamLastFreq) > s.SteamLastFreq * 0.03f + 1f;
                // Same 40 ms re-arm cap as the Triton path: a pitch-edge flood is
                // a feature-report control transfer per tick here, which the
                // stream comments already flag as saturating. A fresh cue always
                // arms immediately.
                if (pitchShift && (firstArm || Environment.TickCount64 - s.SteamLastBurstMs >= 40))
                {
                    SteamSendBlob(s, HapticToneEncoder.EncodeSteamClassic(toneHz, durationSeconds: -1.0, haptic: 0));
                    SteamSendBlob(s, HapticToneEncoder.EncodeSteamClassic(toneHz, durationSeconds: -1.0, haptic: 1));
                    s.SteamOn = true;
                    s.SteamLastFreq = toneHz;
                    s.SteamLastBurstMs = Environment.TickCount64;
                }
            }
            else if (s.SteamOn)
            {
                SteamStop(s);
            }
        }

        // ── Steam Controller 2026 / Triton: 0x83 LFO-tone OUTPUT report ──
        // Mirrors SteamHapticsSinger's Triton path: drive ALL FOUR LRAs (trackpad
        // L/R + grip L/R, actuator ids 0,1,3,4), each its own 0x83 command, with a
        // 0x7FFF (sustain) duration so a held note needs no re-send. Re-arm only on a
        // pitch change or a meaningful amplitude step (the gain byte tracks the
        // envelope), not every tick -- each 0x83 re-triggers the attack, so a 100 Hz
        // flood latches into a stuck tone. Writes go direct to our own handle.
        private static void StreamTritonTick(Sink s, float toneHz, float amp, bool streaming)
        {
            if (streaming)
            {
                bool firstArm = !s.SteamOn;
                bool pitchShift = firstArm || Math.Abs(toneHz - s.SteamLastFreq) > s.SteamLastFreq * 0.03f + 1f;
                bool ampStep = Math.Abs(amp - s.SteamLastAmp) > 0.10f;
                long nowMs = Environment.TickCount64;
                // Re-arm bursts are capped at 40 ms (SDL's TRITON_RUMBLE_RESEND_
                // INTERVAL_MS, Valve's own write cadence for this pad). Without the
                // cap, busy audio re-arms 4 actuators at up to 100 Hz and the flood
                // wedges the haptic engine into a garbled state that persists into
                // later cues (observed on hardware, 2026-07-01). A fresh cue always
                // arms immediately.
                if ((pitchShift || ampStep) && (firstArm || nowMs - s.SteamLastBurstMs >= 40))
                {
                    if (firstArm)
                    {
                        // A plain zero rumble command resets the haptic engine out
                        // of the wedged state (observed on hardware: a normal
                        // rumble signal clears the garble). Same zero form SDL's
                        // RumbleJoystick sends (report 0x80, payload all zero).
                        TritonSend(s, HapticToneEncoder.EncodeTritonRumbleClear());
                    }
                    foreach (int hap in HapticToneEncoder.TritonActuators)
                        TritonSend(s, HapticToneEncoder.EncodeTritonTone(hap, toneHz, amp));
                    s.SteamOn = true;
                    s.SteamLastFreq = toneHz;
                    s.SteamLastAmp = amp;
                    s.SteamLastBurstMs = nowMs;
                }
            }
            else if (s.SteamOn)
            {
                TritonStop(s);
                s.SteamOn = false;
                s.SteamLastAmp = 0f;
            }
        }

        private static void TritonSend(Sink s, byte[] report)
        {
            if (s.Handle == IntPtr.Zero) return;
            // Padded to the queried OutputReportByteLength (HID output writes require
            // exactly that; SteamHapticsSinger writes the full 64-byte report).
            HidOutputWrite(s, ResizeOut(report, Math.Max(report.Length, s.OutLen)));
        }

        private static void TritonStop(Sink s)
        {
            if (s.Handle == IntPtr.Zero) return;
            // Per-actuator 0x83 stop form (gain 0x80), the reference note-off.
            foreach (int hap in HapticToneEncoder.TritonActuators)
                TritonSend(s, HapticToneEncoder.EncodeTritonTone(hap, 0f, 0f));
        }


        // ── Steam Deck (Jupiter): report 0xEA SET_FEATURE ──
        private static void StreamSteamDeckTick(Sink s, float toneHz, float amp, bool streaming)
        {
            // The Deck's built-in controller IS the Steam Controller 0x8F path.
            // SteamControllerSinger opens 0x1205 (main.cpp:58) and drives it with the
            // same SteamController_PlayNote 0x8F square wave as the wired pad (README:
            // "the Steam Deck is also supported... very similar to the Steam
            // Controller"), with a real period frequency. The earlier 0xEA path came
            // from SteamHapticsSinger's Jupiter report, whose freq table was a stub
            // (midiFrequencyDk = {440,0,0...}), so it never played a real note.
            // Transport: our own feature handle (SteamFeatureWrite = SET_REPORT,
            // report id 0, byte-identical to SteamControllerSinger's
            // libusb_control_transfer(0x21,9,0x0300,2,blob,64)). NOT SDL -- the Deck's
            // SDL driver (SDL_hidapi_steamdeck.c) only forwards 0xEB rumble, not 0x8F.
            // The 0x8F square is pitch-only (no working gain), so amp just gates.
            if (streaming)
            {
                bool firstArm = !s.SteamOn;
                bool pitchShift = firstArm || Math.Abs(toneHz - s.SteamLastFreq) > s.SteamLastFreq * 0.03f + 1f;
                // Same 40 ms re-arm cap as the Triton/2015 paths (see those ticks).
                if (pitchShift && (firstArm || Environment.TickCount64 - s.SteamLastBurstMs >= 40))
                {
                    SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(toneHz, durationSeconds: -1.0, haptic: 0));
                    SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(toneHz, durationSeconds: -1.0, haptic: 1));
                    s.SteamOn = true;
                    s.SteamLastFreq = toneHz;
                    s.SteamLastBurstMs = Environment.TickCount64;
                }
            }
            else if (s.SteamOn)
            {
                SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 0));
                SteamFeatureWrite(s, HapticToneEncoder.EncodeSteamClassic(0f, 0.0, haptic: 1));
                s.SteamOn = false;
            }
        }

        // Steam Deck 0xEA feature write: prepend the report-id byte, size to
        // FeatureReportByteLength (same translation as the 2015 0x8F path).
        private static void SteamFeatureWrite(Sink s, byte[] blob)
        {
            if (s.Handle == IntPtr.Zero) return;
            int n = s.FeatLen > 0 ? s.FeatLen : blob.Length + 1;
            var buf = new byte[n];
            buf[0] = 0x00;
            Array.Copy(blob, 0, buf, 1, Math.Min(blob.Length, n - 1));
            try { HidD_SetFeature(s.Handle, buf, buf.Length); } catch { }
        }

        private static byte[] ResizeOut(byte[] report, int outLen)
        {
            if (report.Length >= outLen) return report;
            var buf = new byte[outLen];
            Array.Copy(report, buf, report.Length);
            return buf;
        }

        /// <summary>Tears down every haptic-tone sink. Call on app shutdown
        /// alongside the Sony/Wii service shutdowns.</summary>
        public static void Shutdown()
        {
            _suppressed = true;
            // Snapshot under the lock, tear down OUTSIDE it. TeardownSink does a
            // Thread.Join(3000) and a WASAPI capture dispose; holding _lock across
            // those stalls every other _lock caller (GetSlotSinkMixers macro playback,
            // Reconcile) for up to 3 s per sink. This matches AudioPassthroughService.
            // Shutdown, WiiSpeakerService.Shutdown, and this service's own Reconcile,
            // which all tear down outside the lock.
            List<Sink> drop;
            lock (_lock)
            {
                try { _reconcileTimer?.Dispose(); } catch { }
                _reconcileTimer = null;
                drop = _sinks.ToList();
                _sinks.Clear();
            }
            foreach (var s in drop) TeardownSink(s);
        }
    }
}
