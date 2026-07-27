using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>Detector core for the virtual-controller output freeze.
    /// Pure state machine, no I/O, so the trip/clear contract is unit-tested.
    ///
    /// <para>The bug (OPEN, root cause unknown, twice reported): the HM
    /// virtual controller stays present in joy.cpl while its OUTPUT freezes
    /// at rest; PadForge's own preview keeps showing live output; toggling
    /// the VC or restarting PadForge clears it. It strikes at random in
    /// long sessions, and by the time it is reported it has been cleared,
    /// so no live repro has ever been inspected. This probe exists to make
    /// the NEXT occurrence name its own failing layer.</para></summary>
    internal sealed class VcFreezeDetector
    {
        /// <summary>Consecutive bad observations (~2 s apart) before the
        /// freeze trips. Three keeps a one-tick hiccup from alarming while
        /// still catching a real freeze inside ~6 s.</summary>
        internal const int TicksToTrip = 3;

        private int _badTicks;

        internal bool IsFrozen { get; private set; }

        /// <summary>Feed one probe observation. Returns true exactly once,
        /// on the tick the freeze TRIPS (edge, not level), so the caller
        /// logs and alerts once per episode.
        ///
        /// <para><paramref name="outputsChanging"/>: some live slot's
        /// combined output signature moved since the last observation.
        /// <paramref name="driverChanged"/>: some HM HID device's input
        /// report, read back from the driver the same way joy.cpl reads it,
        /// moved since the last observation. A freeze is sustained
        /// outputs-changing with a static driver; anything else resets the
        /// count, and a driver that moves again clears the episode.</para></summary>
        internal bool Observe(bool outputsChanging, bool driverChanged)
        {
            if (driverChanged)
            {
                _badTicks = 0;
                IsFrozen = false;
                return false;
            }

            if (!outputsChanging)
            {
                // Idle pad, static driver: nothing to judge. Deliberately
                // does NOT clear an existing episode: a frozen VC stays
                // frozen through user pauses, and clearing here would
                // re-fire the alert every time they resumed moving.
                _badTicks = 0;
                return false;
            }

            if (IsFrozen) return false;

            _badTicks++;
            if (_badTicks < TicksToTrip) return false;

            IsFrozen = true;
            return true;
        }
    }

    /// <summary>Background probe that reads PadForge's own HM virtual
    /// controllers back through their HID interfaces, the same vantage
    /// joy.cpl uses, and compares that against what the engine is
    /// submitting. On a sustained divergence it logs one loud VCFREEZE line
    /// with enough per-layer telemetry to name the failing seam:
    ///
    /// <para>submit counters advancing + driver static = the freeze lives at
    /// or past the shared-memory boundary (the driver's strictly-greater
    /// SeqNo delivery gate is the standing suspect). Submit counters static
    /// while combined outputs change = Step 5 or the wrapper stopped
    /// submitting (a null controller no-ops silently). Either way the line
    /// says which, which no earlier occurrence ever recorded.</para>
    ///
    /// <para>Detection only, deliberately: the automated heal (programmatic
    /// VC recreate, the user's manual workaround) lands only after one
    /// confirmed capture, so the first capture's evidence is not destroyed
    /// by the cure.</para></summary>
    internal sealed class VcFreezeProbe : IDisposable
    {
        private readonly InputManager _inputManager;
        private readonly Action<string> _alert;
        private readonly VcFreezeDetector _detector = new();
        private Thread _thread;
        private volatile bool _running;

        private readonly ulong[] _lastSlotSig = new ulong[InputManager.MaxPads];
        private readonly long[] _lastSubmitCount = new long[InputManager.MaxPads];
        private readonly Dictionary<string, ulong> _lastDeviceSig = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _hmPaths = new();
        private int _ticksSinceEnum = int.MaxValue;

        internal VcFreezeProbe(InputManager inputManager, Action<string> alert)
        {
            _inputManager = inputManager;
            _alert = alert;
        }

        internal void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "VcFreezeProbe",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;
            try { _thread?.Join(500); } catch { /* teardown */ }
            _thread = null;
        }

        private void Loop()
        {
            while (_running)
            {
                try { Tick(); }
                catch { /* the probe must never take the app down */ }
                for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
            }
        }

        private void Tick()
        {
            // Re-enumerate HM device paths every 5 ticks (~10 s): VCs come
            // and go with slot toggles and the bubble-down cascade.
            if (++_ticksSinceEnum >= 5)
            {
                _ticksSinceEnum = 0;
                _hmPaths = EnumerateHmHidPaths();
                foreach (var stale in new List<string>(_lastDeviceSig.Keys))
                    if (!_hmPaths.Contains(stale)) _lastDeviceSig.Remove(stale);
            }

            var vcs = _inputManager.GetVirtualControllers();

            bool outputsChanging = false;
            bool submitsAdvancing = false;
            int changingSlot = -1;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (vcs[i] == null) { _lastSlotSig[i] = 0; continue; }

                ulong sig = SlotSignature(i);
                if (_lastSlotSig[i] != 0 && sig != _lastSlotSig[i])
                {
                    outputsChanging = true;
                    changingSlot = i;
                }
                _lastSlotSig[i] = sig;

                if (vcs[i] is HMaestroVirtualController hm)
                {
                    long count = Interlocked.Read(ref hm.SubmitCounter);
                    if (count != _lastSubmitCount[i]) submitsAdvancing = true;
                    _lastSubmitCount[i] = count;
                }
            }

            bool driverChanged = false;
            var outcomes = new List<string>(_hmPaths.Count);
            foreach (var path in _hmPaths)
            {
                var (ok, sig, note) = ReadDeviceReport(path);
                if (ok)
                {
                    if (!_lastDeviceSig.TryGetValue(path, out var prev) || prev != sig)
                        driverChanged = true;
                    _lastDeviceSig[path] = sig;
                    outcomes.Add(Tail(path) + "=r" + (sig & 0xFFFF).ToString("x4"));
                }
                else
                {
                    outcomes.Add(Tail(path) + "=" + note);
                }
            }

            if (_detector.Observe(outputsChanging, driverChanged))
            {
                string layer = submitsAdvancing
                    ? "submits ADVANCING, driver report STATIC (driver-boundary freeze)"
                    : "submits STATIC while outputs change (submit-side stall)";
                string detail = $"VCFREEZE slot={changingSlot} {layer} devices=[{string.Join(" ", outcomes)}]";
                PadForge.Engine.SdlDiagLog.WriteLine(detail);
                _alert?.Invoke(detail);
            }
            else if (!_detector.IsFrozen && outcomes.Count > 0 && driverChanged)
            {
                // Quiet path: nothing logged. The probe's whole output is
                // one line per episode, not a heartbeat.
            }
        }

        private ulong SlotSignature(int i)
        {
            // Coarse, torn-read-tolerant FNV over the combined surfaces the
            // preview shows. A torn read at worst delays detection one tick.
            ulong h = 1469598103934665603UL;
            void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }

            var gp = _inputManager.CombinedOutputStates[i];
            Mix(gp.Buttons);
            Mix((ulong)(ushort)gp.LeftTrigger);
            Mix((ulong)(ushort)gp.RightTrigger);
            Mix((ulong)(ushort)(short)(gp.ThumbLX >> 8));
            Mix((ulong)(ushort)(short)(gp.ThumbLY >> 8));
            Mix((ulong)(ushort)(short)(gp.ThumbRX >> 8));
            Mix((ulong)(ushort)(short)(gp.ThumbRY >> 8));

            var raw = _inputManager.CombinedRawHidStates[i];
            var axes = raw.Axes;
            if (axes != null)
                for (int a = 0; a < axes.Length; a++) Mix((ulong)(ushort)(axes[a] >> 8));
            var btns = raw.Buttons;
            if (btns != null)
                for (int b = 0; b < btns.Length; b++) Mix(btns[b]);
            return h == 0 ? 1UL : h;
        }

        private static string Tail(string path)
        {
            int i = path.LastIndexOf('\\');
            var t = i >= 0 ? path.Substring(i + 1) : path;
            return t.Length > 18 ? t.Substring(t.Length - 18) : t;
        }

        // ── HID read-back (the joy.cpl vantage) ──────────────────────────

        private static (bool Ok, ulong Sig, string Note) ReadDeviceReport(string path)
        {
            using var handle = CreateFileW(path, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) return (false, 0, "open-err" + Marshal.GetLastWin32Error());

            var buf = new byte[1024];
            using var evt = new ManualResetEvent(false);
            var ov = new NativeOverlapped
            {
                EventHandle = evt.SafeWaitHandle.DangerousGetHandle(),
            };

            if (!ReadFile(handle, buf, buf.Length, out _, ref ov))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return (false, 0, "read-err" + err);
                if (!evt.WaitOne(250))
                {
                    // No report inside the window. On an idle pad that is
                    // normal (the driver completes reads on change); the
                    // detector only weighs this when outputs are changing.
                    CancelIoEx(handle, ref ov);
                    evt.WaitOne(100);
                    return (false, 0, "timeout");
                }
            }

            if (!GetOverlappedResult(handle, ref ov, out int transferred, false) || transferred <= 0)
                return (false, 0, "ovl-err" + Marshal.GetLastWin32Error());

            ulong h = 1469598103934665603UL;
            for (int i = 0; i < transferred; i++) h = (h ^ buf[i]) * 1099511628211UL;
            return (true, h == 0 ? 1UL : h, null);
        }

        private static List<string> EnumerateHmHidPaths()
        {
            var result = new List<string>();
            HidD_GetHidGuid(out var hidGuid);
            IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == INVALID_HANDLE_VALUE || devs == IntPtr.Zero) return result;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int index = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, index, ref ifData); index++)
                {
                    SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                    if (needed <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        // cbSize is the STRUCT size (8 on x64), not the buffer size.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, needed, out _, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(detail + 4);
                            if (path != null && path.IndexOf("hidmaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                                result.Add(path);
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devs); }
            return result;
        }

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int ERROR_IO_PENDING = 997;
        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int bytesToRead, out int bytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle handle, ref NativeOverlapped overlapped, out int bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle handle, ref NativeOverlapped overlapped);
    }
}
