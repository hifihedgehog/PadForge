using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine;
using SDL3;

namespace PadForge.Common.Input
{
    /// <summary>
    /// 3Dconnexion SpaceMouse bridge (#288): opens 6DoF HID pucks directly and
    /// streams them into SDL as virtual joysticks so the whole existing pipeline
    /// (mapping grid, macros, curves, DSU, per-app profiles) consumes them
    /// unchanged.
    ///
    /// Why a direct reader at all: a SpaceMouse enumerates as HID usage page
    /// 0x01 Generic Desktop, usage 0x08 Multi-axis Controller. SDL's raw-input
    /// backend subscribes to the GAMEPAD usage only
    /// (SDL_rawinputjoystick.c:173-179, MULTIAXISCONTROLLER commented out), and
    /// the hidapi enumeration path that would accept it (SDL_hidapi.c:1204) has
    /// no 3Dconnexion driver, so the device is never turned into an SDL
    /// joystick. Same shape as the Bluetooth DS3 before Ds3DirectService, and
    /// this service follows that template (monitor loop, virtual attach,
    /// serialized teardown) with SonyHeadsetMotionDevice's overlapped-read
    /// pattern for the HID transport.
    ///
    /// Concurrency with 3DxWare: Windows HIDClass gives every open file object
    /// its own input-report ring buffer (HidD_SetNumInputBuffers is per-handle),
    /// so reading here never starves or fights the vendor driver; both receive
    /// every report.
    /// </summary>
    public sealed class SpaceMouseService
    {
        // ─── device identity ────────────────────────────────────────────────
        //
        // Two vendor IDs cover the whole family: 0x046D (Logitech era) and
        // 0x256F (3Dconnexion proper). The legacy 0x046D set is CLOSED (the
        // vendor moved to 0x256F), so those PIDs are pinned to spacenavd's
        // canonical table (spacenavd/src/dev.c:64-76). For 0x256F the usage
        // gate below decides membership on its own: every 6DoF device
        // enumerates usage 0x08, while the CadMouse/Keyboard/Numpad families
        // sharing the VID enumerate as mouse/keyboard usages and are excluded
        // structurally (spacenavd's devid_blacklist exists because its matcher
        // is VID-based; a usage-gated matcher does not need one). The disputed
        // 0xC641 (blacklisted "scout(?)" by spacenavd, shipped as a 6DoF config
        // by pyspacemouse) is deliberately absent from any static list: the
        // usage gate adjudicates it at runtime.

        private const ushort VidLogitech = 0x046D;
        private const ushort Vid3Dconnexion = 0x256F;

        private static readonly HashSet<ushort> LegacyLogitechPids = new()
        {
            0xC603, // SpaceMouse Plus XT
            0xC605, // CadMan
            0xC606, // SpaceMouse Classic
            0xC621, // Spaceball 5000
            0xC623, // Space Traveler
            0xC625, // Space Pilot
            0xC626, // Space Navigator
            0xC627, // Space Explorer
            0xC628, // Space Navigator for Notebooks
            0xC629, // Space Pilot Pro
            0xC62B, // SpaceMouse Pro
            0xC640, // NuLOOQ
        };

        private const ushort UsagePageGenericDesktop = 0x01;
        private const ushort UsageMultiAxisController = 0x08;

        // HID usages for rotation axes inside report 1: Rx/Ry/Rz. Their
        // presence under report ID 1 is what marks the combined-report shape
        // (see SpaceMouseDecoder).
        private const ushort UsageRx = 0x33;
        private const ushort UsageRz = 0x35;

        private const int SweepMs = 2000;

        private readonly Action<string> _log;
        private volatile bool _running;
        private Thread _monitor;

        // Sessions keyed by interface path; verdict cache so a non-SpaceMouse
        // HID node is probed once per appearance, not every sweep
        // (SonyHeadsetMotionRuntime's _verdicts pattern).
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);

        public SpaceMouseService(Action<string> log = null) => _log = log ?? (_ => { });

        /// <summary>Begin watching for SpaceMouse devices. Call after SDL has
        /// been initialized (SDL_INIT_JOYSTICK), like Ds3DirectService.Start.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "SpaceMouseMonitor" };
            _monitor.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _monitor?.Join(3000); } catch { }
            _monitor = null;
            lock (_sessions)
            {
                foreach (var s in _sessions.Values) s.Close();
                _sessions.Clear();
                _rejected.Clear();
            }
        }

        // ─── monitor: sweep, probe, open ────────────────────────────────────

        private void MonitorLoop()
        {
            while (_running)
            {
                try { Sweep(); }
                catch (Exception ex) { _log("sweep failed: " + ex.Message); }

                // Sleep in slices so Stop() is never stranded behind a full
                // sweep interval.
                for (int waited = 0; _running && waited < SweepMs; waited += 100)
                    Thread.Sleep(100);
            }
        }

        private void Sweep()
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SonyHeadsetHid.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SonyHeadsetHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                SonyHeadsetHid.DIGCF_PRESENT | SonyHeadsetHid.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
            try
            {
                var iface = new SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA>()
                };
                for (uint index = 0; SonyHeadsetHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero,
                        ref hidGuid, index, ref iface); index++)
                {
                    string path = GetInterfacePath(set, ref iface);
                    if (string.IsNullOrEmpty(path)) continue;
                    present.Add(path);

                    lock (_sessions)
                    {
                        if (_rejected.Contains(path)) continue;
                        if (_sessions.TryGetValue(path, out var existing))
                        {
                            if (existing.IsAlive) continue;
                            // Reader died (unplug, transient error): reap and
                            // let the probe below decide a fresh open.
                            existing.Close();
                            _sessions.Remove(path);
                        }
                    }

                    var session = TryOpen(path, out bool retryable);
                    lock (_sessions)
                    {
                        if (session != null)
                        {
                            // Stop() may have cleared the table while this
                            // sweep was mid-open; never insert past shutdown.
                            if (_running) _sessions[path] = session;
                            else session.Close();
                        }
                        else if (!retryable)
                        {
                            // Only DETERMINISTIC verdicts are cached (wrong
                            // vendor, wrong usage): a SpaceMouse whose read-
                            // open or SDL attach failed transiently (plug
                            // settle, another app holding it exclusive) must
                            // be retried on the next sweep, not written off
                            // until unplug.
                            _rejected.Add(path);
                        }
                    }
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }

            // Prune vanished paths: dead sessions are closed, and rejected
            // verdicts are dropped so a re-created node is probed fresh.
            lock (_sessions)
            {
                List<string> gone = null;
                foreach (var key in _sessions.Keys)
                    if (!present.Contains(key)) (gone ??= new List<string>()).Add(key);
                if (gone != null)
                    foreach (var key in gone)
                    {
                        _sessions[key].Close();
                        _sessions.Remove(key);
                        _log($"detached ({key})");
                    }
                _rejected.RemoveWhere(p => !present.Contains(p));
            }
        }

        private static string GetInterfacePath(IntPtr set, ref SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA iface)
        {
            SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
            if (needed == 0 || needed > 4096) return null;
            IntPtr detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                // cbSize is the FIXED part of SP_DEVICE_INTERFACE_DETAIL_DATA_W:
                // 4-byte cbSize + one wchar, padded (8 on x64).
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        /// <summary>
        /// Probe one HID interface path and, when it is a SpaceMouse, return a
        /// live session (device open, virtual joystick attached, reader
        /// running). Null with <paramref name="retryable"/> false is a
        /// deterministic non-candidate verdict (cacheable); null with true is
        /// a transient failure on a real candidate (retry next sweep).
        /// </summary>
        private Session TryOpen(string path, out bool retryable)
        {
            retryable = false;

            // Metadata-only probe first (access 0 opens even exclusive nodes;
            // SonyHeadsetMotionRuntime.Probe's fallback chain), so the sweep
            // never takes read access on unrelated keyboards and mice.
            var probe = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (probe.IsInvalid) { probe.Dispose(); return null; }

            ushort vid, pid, inputLen;
            bool combined;
            string name;
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                var attributes = new SonyHeadsetHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<SonyHeadsetHid.HIDD_ATTRIBUTES>()
                };
                if (!SonyHeadsetHid.HidD_GetAttributes(probe, ref attributes)) return null;
                vid = attributes.VendorID;
                pid = attributes.ProductID;

                if (vid != Vid3Dconnexion
                    && !(vid == VidLogitech && LegacyLogitechPids.Contains(pid)))
                    return null;

                if (!SonyHeadsetHid.HidD_GetPreparsedData(probe, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
                if (caps.UsagePage != UsagePageGenericDesktop || caps.Usage != UsageMultiAxisController)
                    return null;
                inputLen = caps.InputReportByteLength;
                if (inputLen < 7) return null;

                // Combined-vs-split shape from the descriptor, decided once:
                // does report ID 1 define any rotation usage (Rx..Rz)? See
                // SpaceMouseDecoder for why per-report lengths cannot decide
                // this on Windows.
                combined = false;
                var inputValues = SonyHeadsetHid.GetValueCaps(
                    SonyHeadsetHid.HidP_Input, preparsed, caps.NumberInputValueCaps);
                foreach (var c in inputValues)
                {
                    if (c.ReportID != 1 || c.UsagePage != UsagePageGenericDesktop) continue;
                    ushort lo = c.UsageMin;
                    ushort hi = c.IsRange ? c.UsageMax : c.UsageMin;
                    if (hi >= UsageRx && lo <= UsageRz) { combined = true; break; }
                }

                name = ReadProductString(probe);
                if (string.IsNullOrWhiteSpace(name)) name = "SpaceMouse";
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                probe.Dispose();
            }

            // From here on the path IS a SpaceMouse: any failure is transient
            // and must be retried, never cached as a rejection.
            retryable = true;

            // Real open: overlapped read handle (SonyHeadsetMotionDevice:180
            // pattern; shared so 3DxWare keeps working beside us).
            var handle = SonyHeadsetHid.CreateFile(path, SonyHeadsetHid.GENERIC_READ,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING,
                SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                _log($"probe matched {vid:X4}:{pid:X4} '{name}' but read-open failed (Win32 {Marshal.GetLastWin32Error()}); will retry");
                return null;
            }

            var session = new Session(path, handle, inputLen, combined, vid, pid, name, _log);
            if (!session.AttachAndStart())
            {
                session.Close();
                return null;
            }
            _log($"attached '{name}' {vid:X4}:{pid:X4} shape={(combined ? "combined" : "split")} reportLen={inputLen} ({path})");
            return session;
        }

        private static string ReadProductString(SafeFileHandle handle)
        {
            var buffer = new byte[512];
            if (!SonyHeadsetHid.HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                return null;
            string s = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        // ─── one open device: virtual joystick + reader thread ──────────────

        private sealed class Session
        {
            /// <summary>Buttons exposed on the virtual joystick. The wire
            /// carries up to 48 (6 bytes x 8 bits); 32 covers every shipping
            /// device (SpaceMouse Enterprise, the largest, has 31).</summary>
            private const int VirtualButtons = 32;

            private readonly string _path;
            private readonly SafeFileHandle _handle;
            private readonly ushort _inputReportLength;
            private readonly SpaceMouseDecoder _decoder;
            private readonly ushort _vid, _pid;
            private readonly string _name;
            private readonly Action<string> _log;

            private uint _instanceId;
            private IntPtr _joystick;
            private Thread _reader;
            private ManualResetEvent _readEvent;
            private volatile bool _alive;
            private readonly object _closeLock = new();
            private bool _closed;

            public Session(string path, SafeFileHandle handle, ushort inputReportLength,
                bool combined, ushort vid, ushort pid, string name, Action<string> log)
            {
                _path = path;
                _handle = handle;
                _inputReportLength = inputReportLength;
                _decoder = new SpaceMouseDecoder(combined);
                _vid = vid;
                _pid = pid;
                _name = name;
                _log = log;
            }

            /// <summary>Reader still pumping. False once the device is gone or
            /// errored, telling the sweep to reap this session.</summary>
            public bool IsAlive => _alive;

            public bool AttachAndStart()
            {
                // Plain joystick, NOT a gamepad: six bipolar axes (0-2
                // translation X/Y/Z, 3-5 rotation pitch/roll/yaw) plus buttons.
                // SdlDeviceWrapper.Open falls back to raw-joystick mode for
                // non-gamepads (SdlDeviceWrapper.cs:319-324) and Step1 admits
                // every SDL joystick without a gamepad gate
                // (InputManager.Step1.UpdateDevices.cs:110-140), so the axes
                // surface in the mapping grid as Axis 0..5 with no new target
                // family. The name folds into the SDL joystick GUID, giving
                // each model a stable identity (Ds3DirectService:708-712).
                var namePtr = Marshal.StringToHGlobalAnsi(_name);
                try
                {
                    var desc = new SDL.SDL_VirtualJoystickDesc
                    {
                        type = (ushort)SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN,
                        vendor_id = _vid,
                        product_id = _pid,
                        naxes = 6,
                        nbuttons = VirtualButtons,
                        nhats = 0,
                        name = namePtr,
                    };
                    desc.version = (uint)Marshal.SizeOf<SDL.SDL_VirtualJoystickDesc>();

                    _instanceId = SDL.SDL_AttachVirtualJoystick(ref desc);
                    if (_instanceId == 0)
                    {
                        _log("SDL_AttachVirtualJoystick failed for " + _name);
                        return false;
                    }
                    _joystick = SDL.SDL_OpenJoystick(_instanceId);
                    if (_joystick == IntPtr.Zero) return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }

                _readEvent = new ManualResetEvent(false);
                _alive = true;
                _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "SpaceMouseRead" };
                _reader.Start();
                return true;
            }

            /// <summary>Serialized + idempotent teardown (Ds3DirectService
            /// Teardown discipline): cancel I/O, join the reader, then close
            /// SDL and the handle. The reader thread never calls this; it
            /// only clears <see cref="_alive"/> and exits.</summary>
            public void Close()
            {
                lock (_closeLock)
                {
                    if (_closed) return;
                    _closed = true;
                    _alive = false;
                    try { if (!_handle.IsInvalid) SonyHeadsetHid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
                    try { if (_reader != null && _reader != Thread.CurrentThread) _reader.Join(1500); } catch { }
                    _reader = null;

                    if (_joystick != IntPtr.Zero) { SDL.SDL_CloseJoystick(_joystick); _joystick = IntPtr.Zero; }
                    if (_instanceId != 0) { SDL.SDL_DetachVirtualJoystick(_instanceId); _instanceId = 0; }

                    _readEvent?.Dispose();
                    _readEvent = null;
                    _handle.Dispose();
                }
            }

            // Overlapped read loop, ported from SonyHeadsetMotionDevice
            // ReaderLoop/ReadPackets: the kernel owns the buffer and the
            // OVERLAPPED until each I/O completes, so both stay pinned for the
            // thread's whole lifetime, and a pending read is drained before
            // either is released.

            private void ReaderLoop()
            {
                var report = new byte[_inputReportLength];
                var reportPin = GCHandle.Alloc(report, GCHandleType.Pinned);
                IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
                bool ioPending = false;
                try
                {
                    var overlapped = new NativeOverlapped
                    {
                        EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
                    };
                    try
                    {
                        ReadPackets(report, overlapped, overlappedPtr, reportPin.AddrOfPinnedObject(), ref ioPending);
                    }
                    catch
                    {
                        // A vanishing device can surface as an exception from
                        // any native call here; the sweep reaps via IsAlive.
                        // Never let the reader take the process down.
                    }
                }
                finally
                {
                    DrainPendingRead(overlappedPtr, ref ioPending);
                    Marshal.FreeHGlobal(overlappedPtr);
                    reportPin.Free();
                    _alive = false;
                }
            }

            private void ReadPackets(byte[] report, NativeOverlapped overlapped,
                IntPtr overlappedPtr, IntPtr reportPtr, ref bool ioPending)
            {
                while (_alive)
                {
                    _readEvent.Reset();
                    Marshal.StructureToPtr(overlapped, overlappedPtr, false);
                    ioPending = false;
                    if (!SonyHeadsetHid.ReadFile(_handle, reportPtr,
                            (uint)report.Length, out uint bytes, overlappedPtr))
                    {
                        int readError = Marshal.GetLastWin32Error();
                        if (readError != SonyHeadsetHid.ERROR_IO_PENDING)
                        {
                            _log($"'{_name}' ReadFile failed (Win32 {readError}); reader exiting");
                            break;
                        }
                        ioPending = true;
                    }
                    // Bounded waits so teardown is never stranded behind an
                    // idle puck (a SpaceMouse sends nothing at rest).
                    while (_alive)
                    {
                        if (_readEvent.WaitOne(100)) break;
                    }
                    if (!_alive)
                    {
                        DrainPendingRead(overlappedPtr, ref ioPending);
                        break;
                    }
                    if (!SonyHeadsetHid.GetOverlappedResult(_handle, overlappedPtr, out bytes, false))
                    {
                        _log($"'{_name}' read completion failed (Win32 {Marshal.GetLastWin32Error()}); reader exiting");
                        DrainPendingRead(overlappedPtr, ref ioPending);
                        break;
                    }
                    ioPending = false;
                    if (bytes == 0) continue;

                    if (_decoder.Process(report, (int)bytes))
                        PushState();
                }
            }

            private void DrainPendingRead(IntPtr overlappedPtr, ref bool ioPending)
            {
                if (!ioPending) return;
                try
                {
                    SonyHeadsetHid.CancelIoEx(_handle, overlappedPtr);
                    SonyHeadsetHid.GetOverlappedResult(_handle, overlappedPtr, out _, true);
                }
                catch { }
                ioPending = false;
            }

            /// <summary>Publish the assembled 6DoF frame. One lock acquisition
            /// for the whole frame (Ds3DirectService.PushState:832-835): the
            /// per-call locks nest, the frame publishes atomically, and
            /// contention with the 1000 Hz polling thread costs one wait.</summary>
            private void PushState()
            {
                IntPtr j = _joystick;
                if (j == IntPtr.Zero) return;

                SDL.SDL_LockJoysticks();
                try
                {
                    SDL.SDL_SetJoystickVirtualAxis(j, 0, SpaceMouseDecoder.ToSdlAxis(_decoder.TranslateX));
                    SDL.SDL_SetJoystickVirtualAxis(j, 1, SpaceMouseDecoder.ToSdlAxis(_decoder.TranslateY));
                    SDL.SDL_SetJoystickVirtualAxis(j, 2, SpaceMouseDecoder.ToSdlAxis(_decoder.TranslateZ));
                    SDL.SDL_SetJoystickVirtualAxis(j, 3, SpaceMouseDecoder.ToSdlAxis(_decoder.RotateX));
                    SDL.SDL_SetJoystickVirtualAxis(j, 4, SpaceMouseDecoder.ToSdlAxis(_decoder.RotateY));
                    SDL.SDL_SetJoystickVirtualAxis(j, 5, SpaceMouseDecoder.ToSdlAxis(_decoder.RotateZ));
                    for (int i = 0; i < VirtualButtons; i++)
                        SDL.SDL_SetJoystickVirtualButton(j, i, _decoder.GetButton(i));
                }
                finally { SDL.SDL_UnlockJoysticks(); }
            }
        }
    }
}
