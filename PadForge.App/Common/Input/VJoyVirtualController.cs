using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    // CfgMgr32 P/Invoke for direct device node management.
    internal static class CfgMgr32
    {
        private const string Dll = "cfgmgr32.dll";

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        internal static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);

        [DllImport(Dll)]
        internal static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);


        internal const int CM_LOCATE_DEVNODE_NORMAL = 0;
        internal const int CM_LOCATE_DEVNODE_PHANTOM = 1;
        internal const int CM_DISABLE_HARDWARE = 0x4;
        internal const int CR_SUCCESS = 0;
    }

    // SetupAPI P/Invoke for DICS_PROPCHANGE device restart.
    // This is the mechanism used by "devcon.exe restart" — it tells PnP that a device
    // property changed and the device should be restarted. Unlike CM_Disable_DevNode,
    // DICS_PROPCHANGE does NOT go through the full disable/enable veto path.
    internal static class SetupApiRestart
    {
        private const string Dll = "setupapi.dll";

        // SP_DEVINFO_DATA shared via SetupApiInterop.SP_DEVINFO_DATA

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_CLASSINSTALL_HEADER
        {
            public int cbSize;
            public int InstallFunction; // DIF_PROPERTYCHANGE
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER ClassInstallHeader;
            public int StateChange;  // DICS_PROPCHANGE
            public int Scope;        // DICS_FLAG_CONFIGSPECIFIC
            public int HwProfile;    // 0 = current
        }

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);

        [DllImport(Dll, SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData);

        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData, char[] DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport(Dll, SetLastError = true)]
        internal static extern bool SetupDiSetClassInstallParamsW(IntPtr DeviceInfoSet, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData, ref SP_PROPCHANGE_PARAMS ClassInstallParams, int ClassInstallParamsSize);

        [DllImport(Dll, SetLastError = true)]
        internal static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData);

        [DllImport(Dll, SetLastError = true)]
        internal static extern bool SetupDiRemoveDevice(IntPtr DeviceInfoSet, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData);

        [DllImport(Dll, SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        internal const int DIGCF_PRESENT = 0x2;
        internal const int DIF_REMOVE = 0x05;
        internal const int DIF_PROPERTYCHANGE = 0x12;
        internal const int DICS_PROPCHANGE = 0x3;
        internal const int DICS_DISABLE = 0x2;
        internal const int DICS_ENABLE = 0x1;
        internal const int DICS_FLAG_CONFIGSPECIFIC = 0x2;
        internal const int DICS_FLAG_GLOBAL = 0x1;

        // HIDClass GUID: {745a17a0-74d3-11d0-b6fe-00a0c90f57da}
        internal static readonly Guid GUID_DEVCLASS_HIDCLASS = new Guid(
            0x745a17a0, 0x74d3, 0x11d0, 0xb6, 0xfe, 0x00, 0xa0, 0xc9, 0x0f, 0x57, 0xda);

        /// <summary>
        /// Restart a device via DICS_PROPCHANGE (same as devcon.exe restart).
        /// Returns true if the restart succeeded.
        /// </summary>
        internal static bool RestartDevice(string instanceId)
        {
            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == new IntPtr(-1)) return false;

            try
            {
                var devInfoData = new SetupApiInterop.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApiInterop.SP_DEVINFO_DATA>() };
                char[] idBuf = new char[256];
                for (int i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, idBuf, idBuf.Length, out int reqSize))
                        continue;
                    string devId = new string(idBuf, 0, reqSize - 1); // strip null terminator
                    if (!devId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Found the device — apply DICS_PROPCHANGE.
                    var propChangeParams = new SP_PROPCHANGE_PARAMS
                    {
                        ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                        {
                            cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                            InstallFunction = DIF_PROPERTYCHANGE
                        },
                        StateChange = DICS_PROPCHANGE,
                        Scope = DICS_FLAG_CONFIGSPECIFIC,
                        HwProfile = 0
                    };

                    if (!SetupDiSetClassInstallParamsW(devInfoSet, ref devInfoData, ref propChangeParams,
                            Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
                    {
                        VJoyVirtualController.DiagLog($"SetupDiSetClassInstallParams failed: {Marshal.GetLastWin32Error()}");
                        return false;
                    }

                    bool ok = SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref devInfoData);
                    if (!ok)
                        VJoyVirtualController.DiagLog($"SetupDiCallClassInstaller(DIF_PROPERTYCHANGE) failed: {Marshal.GetLastWin32Error()}");
                    return ok;
                }
                VJoyVirtualController.DiagLog($"RestartDevice: instance '{instanceId}' not found");
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        /// <summary>
        /// Disable a device via DICS_DISABLE (SetupAPI path, different from CM_Disable_DevNode).
        /// </summary>
        internal static bool DisableDevice(string instanceId)
        {
            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == new IntPtr(-1)) return false;

            try
            {
                var devInfoData = new SetupApiInterop.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApiInterop.SP_DEVINFO_DATA>() };
                char[] idBuf = new char[256];
                for (int i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, idBuf, idBuf.Length, out int reqSize))
                        continue;
                    string devId = new string(idBuf, 0, reqSize - 1);
                    if (!devId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var propChangeParams = new SP_PROPCHANGE_PARAMS
                    {
                        ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                        {
                            cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                            InstallFunction = DIF_PROPERTYCHANGE
                        },
                        StateChange = DICS_DISABLE,
                        Scope = DICS_FLAG_CONFIGSPECIFIC,
                        HwProfile = 0
                    };

                    if (!SetupDiSetClassInstallParamsW(devInfoSet, ref devInfoData, ref propChangeParams,
                            Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
                        return false;

                    bool ok = SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref devInfoData);
                    if (!ok)
                        VJoyVirtualController.DiagLog($"SetupDiCallClassInstaller(DICS_DISABLE) failed: {Marshal.GetLastWin32Error()}");
                    return ok;
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        /// <summary>
        /// Remove a device via SetupDiRemoveDevice (more forceful than pnputil).
        /// Works best on already-disabled devices where the driver is unloaded.
        /// </summary>
        internal static bool RemoveDevice(string instanceId)
        {
            var guid = GUID_DEVCLASS_HIDCLASS;
            // Include non-present devices (disabled devices aren't DIGCF_PRESENT)
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0);
            if (devInfoSet == new IntPtr(-1)) return false;

            try
            {
                var devInfoData = new SetupApiInterop.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApiInterop.SP_DEVINFO_DATA>() };
                char[] idBuf = new char[256];
                for (int i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, idBuf, idBuf.Length, out int reqSize))
                        continue;
                    string devId = new string(idBuf, 0, reqSize - 1);
                    if (!devId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool ok = SetupDiRemoveDevice(devInfoSet, ref devInfoData);
                    if (!ok)
                        VJoyVirtualController.DiagLog($"SetupDiRemoveDevice failed: {Marshal.GetLastWin32Error()}");
                    else
                        VJoyVirtualController.DiagLog($"SetupDiRemoveDevice succeeded for {instanceId}");
                    return ok;
                }
                VJoyVirtualController.DiagLog($"RemoveDevice: instance '{instanceId}' not found");
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }

        /// <summary>
        /// Enable a device via DICS_ENABLE.
        /// </summary>
        internal static bool EnableDevice(string instanceId)
        {
            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0); // 0 = include non-present
            if (devInfoSet == new IntPtr(-1)) return false;

            try
            {
                var devInfoData = new SetupApiInterop.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SetupApiInterop.SP_DEVINFO_DATA>() };
                char[] idBuf = new char[256];
                for (int i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, idBuf, idBuf.Length, out int reqSize))
                        continue;
                    string devId = new string(idBuf, 0, reqSize - 1);
                    if (!devId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var propChangeParams = new SP_PROPCHANGE_PARAMS
                    {
                        ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                        {
                            cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                            InstallFunction = DIF_PROPERTYCHANGE
                        },
                        StateChange = DICS_ENABLE,
                        Scope = DICS_FLAG_CONFIGSPECIFIC,
                        HwProfile = 0
                    };

                    if (!SetupDiSetClassInstallParamsW(devInfoSet, ref devInfoData, ref propChangeParams,
                            Marshal.SizeOf<SP_PROPCHANGE_PARAMS>()))
                        return false;

                    bool ok = SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devInfoSet, ref devInfoData);
                    if (!ok)
                        VJoyVirtualController.DiagLog($"SetupDiCallClassInstaller(DICS_ENABLE) failed: {Marshal.GetLastWin32Error()}");
                    return ok;
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }
    }

    /// <summary>
    /// Virtual joystick controller via the vJoy driver.
    /// Uses direct P/Invoke to vJoyInterface.dll (no NuGet dependency).
    /// vJoy devices are configurable (axes, buttons, hats) via the
    /// vJoy Configuration utility — this controller maps the standard
    /// Gamepad struct to whatever axes/buttons the device supports.
    /// </summary>
    internal sealed class VJoyVirtualController : IVirtualController
    {
        private static bool _dllLoaded;

        /// <summary>
        /// Number of DeviceNN registry descriptors currently written.
        /// The vJoy driver reads ALL DeviceNN keys from a single device node
        /// to create that many virtual joysticks. Tracked so we know when a
        /// device restart is needed (descriptor count changed).
        /// </summary>
        private static int _currentDescriptorCount;

        /// <summary>
        /// The current number of vJoy registry descriptors (Device01..DeviceNN).
        /// Used by Step 5 to determine whether EnsureDevicesAvailable needs to be
        /// called for scale-down (deletion) as well as scale-up (creation).
        /// </summary>
        public static int CurrentDescriptorCount => _currentDescriptorCount;

        /// <summary>Whether we've already ensured the driver is in the Windows driver store this session.</summary>
        private static bool _driverStoreChecked;

        /// <summary>
        /// Incremented whenever the device node is restarted. Each VJoyVirtualController
        /// instance captures the generation at Connect() time; if a newer generation exists
        /// during SubmitGamepadState, the controller re-acquires its device handle.
        /// </summary>
        private static int _generation;

        /// <summary>Whether vJoyInterface.dll has been successfully loaded into the process.</summary>
        public static bool IsDllLoaded => _dllLoaded;

        /// <summary>
        /// Resets all cached static state so the next operation re-discovers
        /// the vJoy driver from scratch. Called after driver reinstall so the
        /// engine picks up the new driver without restarting PadForge.
        /// </summary>
        internal static void ResetState()
        {
            _dllLoaded = false;
            _currentDescriptorCount = 0;
            _driverStoreChecked = false;
            _generation++;
        }

        /// <summary>
        /// Preloads vJoyInterface.dll from the vJoy installation directory.
        /// Once loaded into the process, all [DllImport] calls resolve to it.
        /// Only caches success — retries on next call if the DLL wasn't found
        /// (e.g., user installed vJoy after app startup).
        /// </summary>
        internal static void EnsureDllLoaded()
        {
            if (_dllLoaded) return;

            // Already loadable from default search paths?
            if (NativeLibrary.TryLoad("vJoyInterface.dll", out _))
            {
                _dllLoaded = true;
                return;
            }

            // Try vJoy installation directory (root first, then arch subdirectory for legacy installs).
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            string vjoyPath = Path.Combine(vjoyDir, "vJoyInterface.dll");
            if (!File.Exists(vjoyPath))
            {
                string arch = Environment.Is64BitProcess ? "x64" : "x86";
                vjoyPath = Path.Combine(vjoyDir, arch, "vJoyInterface.dll");
            }
            if (File.Exists(vjoyPath) && NativeLibrary.TryLoad(vjoyPath, out _))
                _dllLoaded = true;
        }


        private readonly uint _deviceId;
        private bool _connected;
        private int _connectedGeneration;
        private int _reacquireFailCount;
        private const int MaxReacquireRetries = 50; // ~50ms at 1kHz before giving up

        public VirtualControllerType Type => VirtualControllerType.Extended;
        public bool IsConnected => _connected;
        public int FeedbackPadIndex { get; set; }

        public VJoyVirtualController(uint deviceId)
        {
            if (deviceId < 1 || deviceId > 16)
                throw new ArgumentOutOfRangeException(nameof(deviceId), "vJoy device ID must be 1–16.");
            _deviceId = deviceId;
        }

        public void Connect()
        {
            EnsureDllLoaded();
            var status = VJoyNative.GetVJDStatus(_deviceId);
            DiagLog($"Connect: deviceId={_deviceId}, status={status}, dllLoaded={_dllLoaded}");

            if (status != VjdStat.VJD_STAT_FREE)
                throw new InvalidOperationException($"vJoy device {_deviceId} is not available (status: {status}).");

            bool acquired = VJoyNative.AcquireVJD(_deviceId);
            DiagLog($"AcquireVJD({_deviceId}): {acquired}");
            if (!acquired)
                throw new InvalidOperationException($"Failed to acquire vJoy device {_deviceId}.");

            VJoyNative.ResetVJD(_deviceId);
            _connected = true;
            _connectedGeneration = _generation;
            _reacquireFailCount = 0;

            // Verify output works: send a single test frame with non-zero axes.
            var testPos = new JoystickPositionV2 { bDevice = (byte)_deviceId, wAxisX = 16383, wAxisY = 16383 };
            testPos.bHats = 0xFFFF_FFFFu;
            testPos.bHatsEx1 = 0xFFFF_FFFFu;
            testPos.bHatsEx2 = 0xFFFF_FFFFu;
            testPos.bHatsEx3 = 0xFFFF_FFFFu;
            bool testOk = VJoyNative.UpdateVJD(_deviceId, ref testPos);
            DiagLog($"Post-connect test UpdateVJD({_deviceId}): {testOk}");
        }

        /// <summary>The vJoy device ID (1–16) this controller was created with.</summary>
        public uint DeviceId => _deviceId;

        public void Disconnect()
        {
            if (_connected)
            {
                DiagLog($"Disconnect: deviceId={_deviceId}, submitCalls={_submitCallCount}, submitFails={_submitFailCount}");

                // Remove FFB routing for this device.
                lock (_ffbLock)
                {
                    _ffbDeviceMap.Remove(_deviceId);
                    _ffbDeviceStates.Remove(_deviceId);
                }

                VJoyNative.ResetVJD(_deviceId);
                VJoyNative.RelinquishVJD(_deviceId);
                _connected = false;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private int _submitCallCount;
        private int _submitFailCount;

        /// <summary>Enable to write vJoy diagnostic log to vjoy_diag.log.</summary>
        internal static bool DiagLogEnabled { get; set; }

        private static readonly string _logPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "vjoy_diag.log");

        internal static void DiagLog(string msg)
        {
            if (!DiagLogEnabled) return;
            var line = $"[vJoy {DateTime.Now:HH:mm:ss.fff}] {msg}";
            try { System.IO.File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }

        /// <summary>
        /// Re-acquires the vJoy device if the device node was restarted
        /// (generation mismatch). Called by Step 5 after EnsureDevicesAvailable
        /// to ensure existing controllers re-claim their device IDs BEFORE
        /// new controllers are created via FindFreeDeviceId.
        /// </summary>
        public void ReAcquireIfNeeded()
        {
            if (!_connected || _connectedGeneration == _generation)
                return;

            // If our device ID exceeds the current descriptor count, this device
            // no longer exists in the registry. Don't attempt RelinquishVJD on a
            // non-existent device — it can corrupt the DLL's internal state and
            // cause FindFreeDeviceId to fail. Immediately disconnect so Step 5's
            // ID ordering fix can recreate us with a valid lower ID.
            if (_deviceId > (uint)CurrentDescriptorCount)
            {
                DiagLog($"ReAcquireIfNeeded: device {_deviceId} exceeds descriptor count {CurrentDescriptorCount}, disconnecting for ID reassignment");
                Disconnect();
                return;
            }

            _reacquireFailCount++;
            if (_reacquireFailCount > MaxReacquireRetries)
            {
                // Give up — disconnect so Step 5 can recreate a fresh controller.
                if (_reacquireFailCount == MaxReacquireRetries + 1)
                    DiagLog($"ReAcquireIfNeeded: giving up after {MaxReacquireRetries} retries for device {_deviceId}, disconnecting");
                Disconnect();
                return;
            }

            if (_reacquireFailCount == 1)
                DiagLog($"ReAcquireIfNeeded: generation mismatch ({_connectedGeneration}→{_generation}), re-acquiring device {_deviceId}");
            try
            {
                VJoyNative.RelinquishVJD(_deviceId);
                bool acquired = VJoyNative.AcquireVJD(_deviceId);
                if (!acquired) return; // retry next cycle
                DiagLog($"Re-AcquireVJD({_deviceId}): success after {_reacquireFailCount} attempts");
                VJoyNative.ResetVJD(_deviceId);
                _connectedGeneration = _generation;
                _reacquireFailCount = 0;
            }
            catch { /* retry next cycle */ }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            // If the device node was restarted (descriptor count changed),
            // our AcquireVJD handle is stale. ReAcquireIfNeeded handles retries
            // and will disconnect after MaxReacquireRetries failures.
            if (_connectedGeneration != _generation)
            {
                ReAcquireIfNeeded();
                if (!_connected || _connectedGeneration != _generation) return;
            }

            uint id = _deviceId;

            // Axes: signed short (-32768..32767) → vJoy range (0..32767)
            int lx = (gp.ThumbLX + 32768) / 2;
            int ly = 32767 - (gp.ThumbLY + 32768) / 2;   // Y inverted (HID Y-down=max)
            int rx = (gp.ThumbRX + 32768) / 2;
            int ry = 32767 - (gp.ThumbRY + 32768) / 2;   // Y inverted
            int lt = gp.LeftTrigger * 32767 / 65535;
            int rt = gp.RightTrigger * 32767 / 65535;

            // Buttons 1–11 bitmask (Xbox 360 layout: A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide)
            int buttons = 0;
            if (gp.IsButtonPressed(Gamepad.A))              buttons |= 1 << 0;
            if (gp.IsButtonPressed(Gamepad.B))              buttons |= 1 << 1;
            if (gp.IsButtonPressed(Gamepad.X))              buttons |= 1 << 2;
            if (gp.IsButtonPressed(Gamepad.Y))              buttons |= 1 << 3;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  buttons |= 1 << 4;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) buttons |= 1 << 5;
            if (gp.IsButtonPressed(Gamepad.BACK))           buttons |= 1 << 6;
            if (gp.IsButtonPressed(Gamepad.START))          buttons |= 1 << 7;
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     buttons |= 1 << 8;
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    buttons |= 1 << 9;
            if (gp.IsButtonPressed(Gamepad.GUIDE))          buttons |= 1 << 10;

            // D-Pad → continuous POV hat (hundredths of degrees).
            // Supports 8-way diagonals: 0=N, 4500=NE, 9000=E, 13500=SE, etc.
            // -1 = centered.
            bool up    = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            bool down  = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left  = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            int pov;
            if      (up && right)   pov = 4500;
            else if (up && left)    pov = 31500;
            else if (down && right) pov = 13500;
            else if (down && left)  pov = 22500;
            else if (up)            pov = 0;
            else if (right)         pov = 9000;
            else if (down)          pov = 18000;
            else if (left)          pov = 27000;
            else                    pov = -1;

            // Single UpdateVJD call per frame (1 kernel IOCTL) instead of individual
            // SetAxis/SetBtn/SetDiscPov calls (18+ IOCTLs). Critical for multi-controller.
            var pos = new JoystickPositionV2
            {
                bDevice = (byte)id,
                wAxisX = lx,
                wAxisY = ly,
                wAxisZ = lt,
                wAxisXRot = rx,
                wAxisYRot = ry,
                wAxisZRot = rt,
                lButtons = buttons,
                bHats = pov < 0 ? 0xFFFF_FFFFu : (uint)pov,
                bHatsEx1 = 0xFFFF_FFFFu,
                bHatsEx2 = 0xFFFF_FFFFu,
                bHatsEx3 = 0xFFFF_FFFFu,
            };

            bool ok = VJoyNative.UpdateVJD(id, ref pos);
            _submitCallCount++;
            if (!ok) _submitFailCount++;

            // Log first call and periodic status (every ~5 seconds at 1000Hz)
            if (_submitCallCount == 1 || _submitCallCount % 5000 == 0)
            {
                DiagLog($"SubmitGamepadState(UpdateVJD) devId={id} call#{_submitCallCount} fails={_submitFailCount} X={lx} Y={ly} btns=0x{buttons:X} pov={pov}");
            }
        }

        /// <summary>
        /// Submits a VJoyRawState directly to the vJoy device, bypassing the Gamepad struct.
        /// Used for custom vJoy configurations with arbitrary axis/button/POV counts.
        /// Axes are signed short → converted to vJoy unsigned range (0..32767).
        /// </summary>
        public void SubmitRawState(VJoyRawState raw)
        {
            if (!_connected) return;

            if (_connectedGeneration != _generation)
            {
                ReAcquireIfNeeded();
                if (!_connected || _connectedGeneration != _generation) return;
            }

            var pos = new JoystickPositionV2 { bDevice = (byte)_deviceId };

            // Map axes: signed short (-32768..32767) → unsigned (0..32767)
            // JoystickPositionV2 field order matches HID axis order in BuildHidDescriptor:
            // X(0), Y(1), Z(2), RX(3), RY(4), RZ(5), Slider(6), Dial(7), Wheel(8),
            // then VX/VY/VZ/VBRX/VBRY/VBRZ map to remaining HID usages (Accel/Brake/etc.)
            // wThrottle at offset 4 maps to axis index 15 (last usage in the descriptor).
            if (raw.Axes != null)
            {
                for (int i = 0; i < raw.Axes.Length && i < 8; i++)
                {
                    int v = (raw.Axes[i] + 32768) / 2;
                    switch (i)
                    {
                        case 0:  pos.wAxisX    = v; break;
                        case 1:  pos.wAxisY    = v; break;
                        case 2:  pos.wAxisZ    = v; break;
                        case 3:  pos.wAxisXRot = v; break;
                        case 4:  pos.wAxisYRot = v; break;
                        case 5:  pos.wAxisZRot = v; break;
                        case 6:  pos.wSlider   = v; break;
                        case 7:  pos.wDial     = v; break;
                    }
                }
            }

            // Map buttons: uint[] words → lButtons/lButtonsEx1/Ex2/Ex3
            if (raw.Buttons != null)
            {
                if (raw.Buttons.Length > 0) pos.lButtons = (int)raw.Buttons[0];
                if (raw.Buttons.Length > 1) pos.lButtonsEx1 = (int)raw.Buttons[1];
                if (raw.Buttons.Length > 2) pos.lButtonsEx2 = (int)raw.Buttons[2];
                if (raw.Buttons.Length > 3) pos.lButtonsEx3 = (int)raw.Buttons[3];
            }

            // Map POVs: -1=centered → 0xFFFFFFFF, else direct value
            pos.bHats = (raw.Povs != null && raw.Povs.Length > 0 && raw.Povs[0] >= 0)
                ? (uint)raw.Povs[0] : 0xFFFF_FFFFu;
            pos.bHatsEx1 = (raw.Povs != null && raw.Povs.Length > 1 && raw.Povs[1] >= 0)
                ? (uint)raw.Povs[1] : 0xFFFF_FFFFu;
            pos.bHatsEx2 = (raw.Povs != null && raw.Povs.Length > 2 && raw.Povs[2] >= 0)
                ? (uint)raw.Povs[2] : 0xFFFF_FFFFu;
            pos.bHatsEx3 = (raw.Povs != null && raw.Povs.Length > 3 && raw.Povs[3] >= 0)
                ? (uint)raw.Povs[3] : 0xFFFF_FFFFu;

            VJoyNative.UpdateVJD(_deviceId, ref pos);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
            // Register this device for FFB routing: vJoy device ID → pad index + vibration array.
            lock (_ffbLock)
            {
                _ffbDeviceMap[_deviceId] = (padIndex, vibrationStates);

                // Register the global FFB callback once (shared across all vJoy devices).
                if (!_ffbCallbackRegistered)
                {
                    try
                    {
                        // Must keep a strong reference to the delegate to prevent GC.
                        _ffbCallbackDelegate = FfbCallback;
                        VJoyNative.FfbRegisterGenCB(_ffbCallbackDelegate, IntPtr.Zero);
                        _ffbCallbackRegistered = true;
                        DiagLog($"FFB callback registered (device {_deviceId}, pad {padIndex})");
                    }
                    catch (DllNotFoundException)
                    {
                        DiagLog("FFB callback registration failed: DLL not found");
                    }
                    catch (Exception ex)
                    {
                        DiagLog($"FFB callback registration failed: {ex.Message}");
                    }
                }
                else
                {
                    DiagLog($"FFB callback already registered, added device {_deviceId} → pad {padIndex}");
                }
            }
        }

        // ─────────────────────────────────────────────
        //  FFB (Force Feedback) passthrough
        //
        //  vJoy's FfbRegisterGenCB fires on a thread pool thread whenever a
        //  game sends a DirectInput force feedback effect to the virtual joystick.
        //  We parse the FFB packets and map them to left/right motor vibration,
        //  matching the ViGEm FeedbackReceived pattern.
        // ─────────────────────────────────────────────

        private static readonly object _ffbLock = new object();
        private static bool _ffbCallbackRegistered;
        private static VJoyNative.FfbGenCB _ffbCallbackDelegate;

        /// <summary>Maps vJoy device ID → (padIndex, vibrationStates array).</summary>
        private static readonly System.Collections.Generic.Dictionary<uint, (int padIndex, Vibration[] states)>
            _ffbDeviceMap = new();

        /// <summary>
        /// Updates FFB device map entries after a slot swap. Any entry that
        /// referenced slotA now references slotB, and vice versa.
        /// </summary>
        internal static void UpdateFfbPadIndex(int slotA, int slotB)
        {
            lock (_ffbLock)
            {
                foreach (var key in new System.Collections.Generic.List<uint>(_ffbDeviceMap.Keys))
                {
                    var entry = _ffbDeviceMap[key];
                    if (entry.padIndex == slotA)
                        _ffbDeviceMap[key] = (slotB, entry.states);
                    else if (entry.padIndex == slotB)
                        _ffbDeviceMap[key] = (slotA, entry.states);
                }
            }
        }

        /// <summary>
        /// Per-device FFB effect state. Tracks the most recently set constant/periodic
        /// magnitude and gain so we can compute motor output when effects start/stop.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<uint, FfbDeviceState>
            _ffbDeviceStates = new();

        private class FfbDeviceState
        {
            /// <summary>Device-level gain (0–255, default 255 = 100%).</summary>
            public byte DeviceGain = 255;
            /// <summary>Per-effect: last known magnitude (0–10000 absolute).</summary>
            public System.Collections.Generic.Dictionary<byte, FfbEffectState> Effects = new();
        }

        private class FfbEffectState
        {
            public FFBEType Type;
            public int Magnitude;           // signed for constant (-10000..+10000), absolute for others (0..10000)
            public byte Gain = 255;         // per-effect gain from effect report (0–255)
            public ushort Duration;         // ms, 0xFFFF=infinite
            public bool Running;
            public ushort Direction;        // polar direction 0–32767 (HID logical units, maps to 0–360°)
            public uint Period;             // ms, for periodic effects

            /// <summary>Per-axis condition parameters. Index 0=X, 1=Y.
            /// vJoy sends one CONDREP per axis.</summary>
            public FfbConditionAxis[] ConditionAxes = new FfbConditionAxis[2];
            public int ConditionAxisCount;
        }

        private struct FfbConditionAxis
        {
            public short CenterPointOffset;      // -10000 to +10000
            public short PosCoeff;               // -10000 to +10000
            public short NegCoeff;               // -10000 to +10000
            public uint PosSatur;                // 0–10000
            public uint NegSatur;                // 0–10000
            public int DeadBand;                 // 0–10000
            public bool IsY;
        }

        /// <summary>
        /// Global FFB callback invoked by vJoyInterface.dll on its thread pool.
        /// Routes FFB packets by device ID to the correct VibrationStates[] slot.
        /// </summary>
        private static void FfbCallback(IntPtr data, IntPtr userData)
        {
            try
            {
                uint deviceId = 0;
                if (VJoyNative.Ffb_h_DeviceID(data, ref deviceId) != 0)
                    return;

                FFBPType packetType = 0;
                if (VJoyNative.Ffb_h_Type(data, ref packetType) != 0)
                    return;

                // Get or create per-device state.
                FfbDeviceState devState;
                lock (_ffbLock)
                {
                    if (!_ffbDeviceStates.TryGetValue(deviceId, out devState))
                    {
                        devState = new FfbDeviceState();
                        _ffbDeviceStates[deviceId] = devState;
                    }
                }

                switch (packetType)
                {
                    case FFBPType.PT_EFFREP: // Set Effect Report — contains effect type, gain, direction, duration
                    {
                        var eff = new FFB_EFF_REPORT();
                        if (VJoyNative.Ffb_h_Eff_Report(data, ref eff) == 0)
                        {
                            byte ebi = eff.EffectBlockIndex;
                            if (!devState.Effects.TryGetValue(ebi, out var es))
                            {
                                es = new FfbEffectState();
                                devState.Effects[ebi] = es;
                            }
                            es.Type = eff.EffectType;
                            es.Gain = eff.Gain;
                            es.Duration = eff.Duration;
                            es.Direction = eff.Direction;
                            DiagLog($"FFB PT_EFFREP dev{deviceId} ebi={ebi} type={eff.EffectType} gain={eff.Gain} dir={eff.Direction} dur={eff.Duration}");
                            // Re-apply motor output if the effect is already running (live gain/direction updates).
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_CONSTREP: // Set Constant Force
                    {
                        var cst = new FFB_EFF_CONSTANT();
                        if (VJoyNative.Ffb_h_Eff_Constant(data, ref cst) == 0)
                        {
                            int rawMag = cst.Magnitude;
                            bool found = devState.Effects.TryGetValue(cst.EffectBlockIndex, out var es);
                            if (found)
                                es.Magnitude = rawMag; // Keep signed: -10000..+10000 (sign flips direction)
                            DiagLog($"FFB PT_CONSTREP dev{deviceId} ebi={cst.EffectBlockIndex} mag={rawMag} effectFound={found}");
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_PRIDREP: // Set Periodic (Sine, Square, Triangle, etc.)
                    {
                        var prd = new FFB_EFF_PERIOD();
                        if (VJoyNative.Ffb_h_Eff_Period(data, ref prd) == 0)
                        {
                            bool found = devState.Effects.TryGetValue(prd.EffectBlockIndex, out var es);
                            if (found)
                            {
                                es.Magnitude = (int)prd.Magnitude; // 0..10000
                                es.Period = prd.Period;             // ms
                            }
                            DiagLog($"FFB PT_PRIDREP dev{deviceId} ebi={prd.EffectBlockIndex} mag={prd.Magnitude} period={prd.Period}ms effectFound={found}");
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_RAMPREP: // Set Ramp Force
                    {
                        var ramp = new FFB_EFF_RAMP();
                        if (VJoyNative.Ffb_h_Eff_Ramp(data, ref ramp) == 0)
                        {
                            if (devState.Effects.TryGetValue(ramp.EffectBlockIndex, out var es))
                                es.Magnitude = Math.Max(Math.Abs(ramp.Start), Math.Abs(ramp.End));
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_CONDREP: // Set Condition (Spring, Damper, Friction, Inertia)
                    {
                        var cond = new FFB_EFF_COND();
                        if (VJoyNative.Ffb_h_Eff_Cond(data, ref cond) == 0)
                        {
                            if (devState.Effects.TryGetValue(cond.EffectBlockIndex, out var es))
                            {
                                // Store full per-axis condition data
                                int axisIdx = cond.IsY ? 1 : 0;
                                es.ConditionAxes[axisIdx] = new FfbConditionAxis
                                {
                                    CenterPointOffset = cond.CenterPointOffset,
                                    PosCoeff = cond.PosCoeff,
                                    NegCoeff = cond.NegCoeff,
                                    PosSatur = cond.PosSatur,
                                    NegSatur = cond.NegSatur,
                                    DeadBand = cond.DeadBand,
                                    IsY = cond.IsY
                                };
                                if (axisIdx + 1 > es.ConditionAxisCount)
                                    es.ConditionAxisCount = axisIdx + 1;

                                // Fallback magnitude for rumble path
                                es.Magnitude = Math.Max(Math.Abs(cond.PosCoeff), Math.Abs(cond.NegCoeff));
                            }
                            DiagLog($"FFB PT_CONDREP dev{deviceId} ebi={cond.EffectBlockIndex} isY={cond.IsY} posC={cond.PosCoeff} negC={cond.NegCoeff} center={cond.CenterPointOffset} dead={cond.DeadBand}");
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_EFOPREP: // Effect Operation (Start/Stop/Solo)
                    {
                        var op = new FFB_EFF_OP();
                        if (VJoyNative.Ffb_h_EffOp(data, ref op) == 0)
                        {
                            byte ebi = op.EffectBlockIndex;
                            DiagLog($"FFB PT_EFOPREP dev{deviceId} ebi={ebi} op={op.EffectOp}");
                            if (op.EffectOp == FFBOP.EFF_START || op.EffectOp == FFBOP.EFF_SOLO)
                            {
                                if (op.EffectOp == FFBOP.EFF_SOLO)
                                {
                                    // Solo: stop all other effects.
                                    foreach (var kv in devState.Effects)
                                        if (kv.Key != ebi) kv.Value.Running = false;
                                }
                                if (devState.Effects.TryGetValue(ebi, out var es))
                                {
                                    es.Running = true;
                                    DiagLog($"FFB EFF_START dev{deviceId} ebi={ebi} mag={es.Magnitude} gain={es.Gain} dir={es.Direction} type={es.Type}");
                                }
                            }
                            else if (op.EffectOp == FFBOP.EFF_STOP)
                            {
                                if (devState.Effects.TryGetValue(ebi, out var es))
                                    es.Running = false;
                            }
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_GAINREP: // Device Gain
                    {
                        byte gain = 0;
                        if (VJoyNative.Ffb_h_DevGain(data, ref gain) == 0)
                        {
                            DiagLog($"FFB PT_GAINREP dev{deviceId} gain={gain}");
                            devState.DeviceGain = gain;
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_CTRLREP: // Device Control (Reset, Stop All, etc.)
                    {
                        FFB_CTRL ctrl = 0;
                        if (VJoyNative.Ffb_h_DevCtrl(data, ref ctrl) == 0)
                        {
                            if (ctrl == FFB_CTRL.CTRL_STOPALL || ctrl == FFB_CTRL.CTRL_DEVRST)
                            {
                                foreach (var kv in devState.Effects)
                                    kv.Value.Running = false;
                                if (ctrl == FFB_CTRL.CTRL_DEVRST)
                                {
                                    devState.Effects.Clear();
                                    devState.DeviceGain = 255;
                                }
                            }
                            else if (ctrl == FFB_CTRL.CTRL_DISACT)
                            {
                                foreach (var kv in devState.Effects)
                                    kv.Value.Running = false;
                            }
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }

                    case FFBPType.PT_BLKFRREP: // Block Free (delete effect)
                    {
                        uint blockIndex = 0;
                        if (VJoyNative.Ffb_h_EffectBlockIndex(data, ref blockIndex) == 0)
                        {
                            devState.Effects.Remove((byte)blockIndex);
                            ApplyMotorOutput(deviceId, devState);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy FFB] Callback exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Computes aggregate motor output from all running effects and writes to VibrationStates[].
        /// Uses polar direction mapping: effects pointing left bias the left motor, right → right motor.
        /// Also populates directional FFB data for haptic devices (joysticks/wheels).
        /// </summary>
        private static void ApplyMotorOutput(uint deviceId, FfbDeviceState devState)
        {
            int padIndex;
            Vibration[] states;
            lock (_ffbLock)
            {
                if (!_ffbDeviceMap.TryGetValue(deviceId, out var entry))
                    return;
                padIndex = entry.padIndex;
                states = entry.states;
            }

            if (padIndex < 0 || padIndex >= states.Length)
                return;

            // Accumulate per-motor values using polar direction split.
            double leftSum = 0, rightSum = 0;

            // Track the dominant effect for directional haptic passthrough.
            FFBEType dominantType = FFBEType.ET_NONE;
            double dominantMag = 0;
            short dominantSignedMag = 0;
            ushort dominantDir = 0;
            uint dominantPeriod = 0;

            // Track if any running condition effect exists.
            bool hasCondition = false;
            FfbEffectState conditionEffect = null;

            foreach (var kv in devState.Effects)
            {
                var es = kv.Value;
                if (!es.Running) continue;

                double absMag = Math.Abs(es.Magnitude);
                if (absMag == 0) continue;

                double mag = absMag * (es.Gain / 255.0);

                // Track dominant effect (strongest) for directional passthrough.
                if (mag > dominantMag)
                {
                    dominantMag = mag;
                    dominantType = es.Type;
                    dominantSignedMag = (short)Math.Clamp(es.Magnitude, -10000, 10000);
                    dominantDir = es.Direction;
                    dominantPeriod = es.Period;
                }

                // Track condition effects separately.
                bool isCondition = es.Type == FFBEType.ET_SPRNG || es.Type == FFBEType.ET_DMPR
                    || es.Type == FFBEType.ET_INRT || es.Type == FFBEType.ET_FRCTN;
                if (isCondition && es.ConditionAxisCount > 0)
                {
                    hasCondition = true;
                    conditionEffect = es;
                }

                // ── Polar direction → left/right motor split ──
                // DirectInput/HID PID convention: direction = where force COMES FROM.
                // East (90°) = force from East, pushes stick West (left motor).
                // +180° converts "from" to "toward" so the motor matches the push direction.
                double angleDeg = ((es.Direction / 32767.0) * 360.0 + 180.0) % 360.0;

                double angleRad = angleDeg * Math.PI / 180.0;

                // sin(0°)=0 (both equal), sin(90°)=1 (right bias), sin(270°)=-1 (left bias)
                double sinVal = Math.Sin(angleRad);
                double leftScale = Math.Clamp(0.5 - sinVal * 0.5, 0.0, 1.0);
                double rightScale = Math.Clamp(0.5 + sinVal * 0.5, 0.0, 1.0);

                leftSum += mag * leftScale;
                rightSum += mag * rightScale;
            }

            // Apply device-level gain.
            double gainFactor = devState.DeviceGain / 255.0;
            leftSum *= gainFactor;
            rightSum *= gainFactor;

            // Scale from 0..10000 → 0..65535 (ushort).
            ushort leftVal = (ushort)Math.Min(65535, (int)(leftSum * 65535.0 / 10000.0));
            ushort rightVal = (ushort)Math.Min(65535, (int)(rightSum * 65535.0 / 10000.0));

            var vib = states[padIndex];
            ushort oldL = vib.LeftMotorSpeed;
            ushort oldR = vib.RightMotorSpeed;

            // Scalar motor values (always set — used by rumble devices).
            vib.LeftMotorSpeed = leftVal;
            vib.RightMotorSpeed = rightVal;

            // Directional data (for haptic FFB devices — joysticks/wheels).
            vib.HasDirectionalData = dominantMag > 0;
            if (vib.HasDirectionalData)
            {
                vib.EffectType = (uint)dominantType;
                vib.SignedMagnitude = dominantSignedMag;
                vib.Direction = dominantDir;
                vib.Period = dominantPeriod;
                vib.DeviceGain = devState.DeviceGain;
            }
            else
            {
                vib.EffectType = 0;
                vib.SignedMagnitude = 0;
                vib.Direction = 0;
                vib.Period = 0;
            }

            // Condition data (for spring/damper/friction/inertia on FFB devices).
            if (hasCondition && conditionEffect != null)
            {
                vib.HasConditionData = true;
                if (vib.ConditionAxes == null || vib.ConditionAxes.Length < conditionEffect.ConditionAxisCount)
                    vib.ConditionAxes = new ConditionAxisData[conditionEffect.ConditionAxisCount];
                vib.ConditionAxisCount = conditionEffect.ConditionAxisCount;
                for (int i = 0; i < conditionEffect.ConditionAxisCount; i++)
                {
                    var src = conditionEffect.ConditionAxes[i];
                    vib.ConditionAxes[i] = new ConditionAxisData
                    {
                        PositiveCoefficient = src.PosCoeff,
                        NegativeCoefficient = src.NegCoeff,
                        Offset = src.CenterPointOffset,
                        DeadBand = (uint)src.DeadBand,
                        PositiveSaturation = src.PosSatur,
                        NegativeSaturation = src.NegSatur
                    };
                }
            }
            else
            {
                vib.HasConditionData = false;
                vib.ConditionAxisCount = 0;
            }

            if (leftVal != oldL || rightVal != oldR)
            {
                DiagLog($"FFB Motor dev{deviceId} pad{padIndex} L:{oldL}->{leftVal} R:{oldR}->{rightVal} (lSum={leftSum:F0} rSum={rightSum:F0} devGain={devState.DeviceGain})");
                RumbleLogger.Log($"[vJoy FFB] Dev{deviceId} Pad{padIndex} L:{oldL}->{leftVal} R:{oldR}->{rightVal}");
            }
        }

        // ─────────────────────────────────────────────
        //  Static helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks if the vJoy driver is installed and enabled.
        /// Gracefully returns false if vJoyInterface.dll is not found.
        /// </summary>
        public static bool CheckVJoyInstalled()
        {
            try
            {
                EnsureDllLoaded();
                return VJoyNative.vJoyEnabled();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks that all vJoy device IDs 1..count report FREE status.
        /// Used by the wait loop in EnsureDevicesAvailable to confirm ALL
        /// devices are initialized after a node restart, not just the first one.
        /// </summary>
        private static bool AllDevicesReady(int count)
        {
            try
            {
                for (uint id = 1; id <= (uint)count; id++)
                {
                    if (VJoyNative.GetVJDStatus(id) != VjdStat.VJD_STAT_FREE)
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns the next available vJoy device ID (1–16), or 0 if none available.
        /// This is a fast, non-blocking scan — safe to call from the engine thread.
        /// </summary>
        public static uint FindFreeDeviceId()
        {
            try
            {
                EnsureDllLoaded();
                if (!_dllLoaded) return 0;

                for (uint id = 1; id <= 16; id++)
                {
                    var status = VJoyNative.GetVJDStatus(id);
                    if (status == VjdStat.VJD_STAT_FREE)
                        return id;
                }
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[vJoy] DllNotFoundException in FindFreeDeviceId: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] Exception in FindFreeDeviceId: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Checks whether the vjoy service is stuck in STOP_PENDING (zombie state).
        /// This happens when a previous uninstall failed to remove device nodes
        /// before stopping the service, and Windows Fast Startup preserved the
        /// broken kernel state across shutdowns. Only a full restart clears it.
        /// </summary>
        public static bool IsServiceStuck()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query vjoy",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
                return output.Contains("STOP_PENDING");
            }
            catch { return false; }
        }

        /// <summary>
        /// Ensures the vJoy driver INF is in the Windows driver store.
        /// Without it, PnP won't apply UpperFilters=mshidkmdf from the INF
        /// when binding new device nodes — vjoy.sys handles IOCTLs but no
        /// HID reports reach Windows (joy.cpl shows no output).
        /// Safe to call once at session start, before any device nodes exist.
        /// </summary>
        private static void EnsureDriverInStore()
        {
            try
            {
                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
                string infPath = Path.Combine(vjoyDir, "vjoy.inf");
                if (!File.Exists(infPath))
                {
                    DiagLog("EnsureDriverInStore: vjoy.inf not found, skipping");
                    return;
                }

                // Check if vjoy is already in the driver store.
                var checkPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var checkProc = Process.Start(checkPsi);
                if (checkProc == null) return;
                string output = checkProc.StandardOutput.ReadToEnd();
                checkProc.WaitForExit(5_000);

                if (output.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DiagLog("EnsureDriverInStore: driver already in store");
                    return;
                }

                // Driver not in store — add it.
                DiagLog("EnsureDriverInStore: driver NOT in store, adding...");
                var addPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/add-driver \"{infPath}\" /install",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var addProc = Process.Start(addPsi);
                if (addProc == null) return;
                string addOutput = addProc.StandardOutput.ReadToEnd();
                addProc.WaitForExit(10_000);
                DiagLog($"EnsureDriverInStore: pnputil exit={addProc.ExitCode}, output={addOutput.Trim()}");
            }
            catch (Exception ex)
            {
                DiagLog($"EnsureDriverInStore exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes the OEMForceFeedback registry keys that DirectInput needs to
        /// recognize vJoy as an FFB-capable device. Without these, DirectInput's
        /// ForceFeedback enumeration flag won't find the device even though
        /// vjoy.sys creates COL02 (the PID collection) with PID_BEAD.
        /// Keys go under HKCU — no elevation needed.
        /// </summary>
        private static void EnsureFfbRegistryKeys()
        {
            try
            {
                const string basePath =
                    @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD";

                using var oemKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(basePath);
                if (oemKey == null) return;
                if (oemKey.GetValue("OEMName") == null)
                    oemKey.SetValue("OEMName", "vJoy Device");

                using var ffbKey = oemKey.CreateSubKey("OEMForceFeedback");
                if (ffbKey == null) return;
                // Standard HID PID FFB class driver CLSID.
                ffbKey.SetValue("CLSID", "{EEC6993A-B3FD-11D2-A916-00C04FB98638}");
                ffbKey.SetValue("CreatedBy", new byte[] { 0x00, 0x08, 0x00, 0x00 },
                    Microsoft.Win32.RegistryValueKind.Binary);
                // Attributes: flags=0, maxForce=1000000 (0x000F4240), minForce=1000000
                ffbKey.SetValue("Attributes",
                    new byte[] { 0x00, 0x00, 0x00, 0x00, 0x40, 0x42, 0x0F, 0x00, 0x40, 0x42, 0x0F, 0x00 },
                    Microsoft.Win32.RegistryValueKind.Binary);

                using var effectsKey = ffbKey.CreateSubKey("Effects");
                if (effectsKey == null) return;

                // Effect GUIDs and their attribute data (from AddvJoyFFB.reg).
                var effects = new (string guid, string name, byte[] attrs)[]
                {
                    ("{13541C20-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_ConstantForce",
                     new byte[] { 0x26, 0x00, 0x0F, 0x00, 0x01, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C21-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_RampForce",
                     new byte[] { 0x27, 0x00, 0x0F, 0x00, 0x02, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C22-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Square",
                     new byte[] { 0x30, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C23-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Sine",
                     new byte[] { 0x31, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C24-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Triangle",
                     new byte[] { 0x32, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C25-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_SawtoothUp",
                     new byte[] { 0x33, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C26-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_SawtoothDown",
                     new byte[] { 0x34, 0x00, 0x0F, 0x00, 0x03, 0x86, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0xFD, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C27-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Spring",
                     new byte[] { 0x40, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C28-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Damper",
                     new byte[] { 0x41, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C29-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Inertia",
                     new byte[] { 0x42, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                    ("{13541C2A-8E33-11D0-9AD0-00A0C9A06E35}", "GUID_Friction",
                     new byte[] { 0x43, 0x00, 0x0F, 0x00, 0x04, 0xC8, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x65, 0x03, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00 }),
                };

                foreach (var (guid, name, attrs) in effects)
                {
                    using var effKey = effectsKey.CreateSubKey(guid);
                    if (effKey == null) continue;
                    effKey.SetValue("", name); // default value
                    effKey.SetValue("Attributes", attrs, Microsoft.Win32.RegistryValueKind.Binary);
                }

                DiagLog("EnsureFfbRegistryKeys: OEMForceFeedback keys written");
            }
            catch (Exception ex)
            {
                DiagLog($"EnsureFfbRegistryKeys exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Counts existing vJoy device nodes by querying PnP (pnputil).
        /// More reliable than GetVJDStatus which can return stale data.
        /// </summary>
        public static int CountExistingDevices()
        {
            return EnumerateVJoyInstanceIds().Count;
        }

        /// <summary>
        /// Ensures <paramref name="requiredCount"/> vJoy virtual joysticks are available.
        ///
        /// vJoy architecture: ONE device node + N registry descriptor keys.
        /// The driver reads ALL DeviceNN keys from HKLM\..\vjoy\Parameters\ and
        /// creates one HID top-level collection per key — all within a single
        /// ROOT\HIDCLASS device node. Multiple device nodes cause phantom
        /// controllers (each node reads ALL keys → N nodes × N keys = N² joysticks).
        ///
        /// To change the count: update registry keys, then restart the device node
        /// so the driver re-reads the report descriptor.
        /// </summary>
        /// <summary>Per-device HID descriptor configuration for WriteDeviceDescriptors.</summary>
        public struct VJoyDeviceConfig
        {
            public int Axes;
            public int Buttons;
            public int Povs;
            /// <summary>Number of thumbsticks (each uses 2 axes: X+Y).</summary>
            public int Sticks;
            /// <summary>Number of triggers (each uses 1 axis).</summary>
            public int Triggers;
        }

        /// <summary>Cached per-device configs from last EnsureDevicesAvailable call.</summary>
        private static VJoyDeviceConfig[] _lastDeviceConfigs;

        /// <summary>
        /// Cached vJoy instance IDs from the last successful enumeration.
        /// Used by shutdown to skip the expensive pnputil enumeration.
        /// </summary>
        private static System.Collections.Generic.List<string> _cachedInstanceIds;

        public static bool EnsureDevicesAvailable(int requiredCount, VJoyDeviceConfig[] perDeviceConfigs)
        {
            return EnsureDevicesAvailableCore(requiredCount, perDeviceConfigs);
        }



        private static bool EnsureDevicesAvailableCore(int requiredCount, VJoyDeviceConfig[] perDeviceConfigs)
        {
            if (!_driverStoreChecked)
            {
                _driverStoreChecked = true;
                EnsureDriverInStore();
                EnsureFfbRegistryKeys();
            }

            // Detect whether per-device configs have changed from last call.
            bool configsChanged = false;
            if (perDeviceConfigs != null)
            {
                if (_lastDeviceConfigs == null || _lastDeviceConfigs.Length != perDeviceConfigs.Length)
                    configsChanged = true;
                else
                {
                    for (int i = 0; i < perDeviceConfigs.Length; i++)
                    {
                        if (_lastDeviceConfigs[i].Axes != perDeviceConfigs[i].Axes ||
                            _lastDeviceConfigs[i].Buttons != perDeviceConfigs[i].Buttons ||
                            _lastDeviceConfigs[i].Povs != perDeviceConfigs[i].Povs)
                        { configsChanged = true; break; }
                    }
                }
            }

            // Fast path: if the count hasn't changed and configs match, skip expensive
            // pnputil enumeration and registry writes.
            if (_currentDescriptorCount == requiredCount && !configsChanged &&
                (requiredCount == 0 || _dllLoaded))
            {
                // Only log fast-path occasionally to avoid log spam at 1000Hz
                return true;
            }

            EnsureDllLoaded();
            int existingNodes = CountExistingDevices();

            DiagLog($"EnsureDevicesAvailable: required={requiredCount}, nodes={existingNodes}, descriptors={_currentDescriptorCount}, dllLoaded={_dllLoaded}");

            // Write registry descriptors for the required count.
            // WriteDeviceDescriptors returns true only if actual registry changes occurred.
            bool registryChanged = WriteDeviceDescriptors(requiredCount, perDeviceConfigs);
            bool countMismatch = _currentDescriptorCount != requiredCount;
            _currentDescriptorCount = requiredCount;
            _lastDeviceConfigs = perDeviceConfigs != null
                ? (VJoyDeviceConfig[])perDeviceConfigs.Clone()
                : null;

            // Need restart if registry actually changed, OR if this is the first call
            // in the process and the node is disabled (DLL can't communicate).
            // Don't restart just because _currentDescriptorCount was 0 at process start
            // when the registry already has the correct descriptors — that causes an
            // unnecessary restart that disrupts live devices.
            bool descriptorsChanged = registryChanged || (countMismatch && !_dllLoaded);

            // If no vJoy devices are needed, fully remove the device node.
            // This ensures child PDOs (VJOYRAWPDO) are gone from the PnP tree,
            // not just disabled. A mere DICS_DISABLE leaves them visible in WMI.
            // The stale-DLL-handle concern (cached device namespace) is resolved
            // by the modified vJoyInterface.dll (StatNS_global cleared on QUERYREMOVE).
            // On next EnsureDevicesAvailable(N>0), the existingNodes==0 path
            // creates a fresh node.
            if (requiredCount == 0)
            {
                DiagLog($"requiredCount=0 path: descriptorsChanged={descriptorsChanged}, registryChanged={registryChanged}, countMismatch={countMismatch}, _dllLoaded={_dllLoaded}, existingNodes={existingNodes}");
                if (descriptorsChanged && existingNodes >= 1)
                {
                    DiagLog("Disabling device node (all vJoy slots inactive)");
                    DisableDeviceNode();
                    _dllLoaded = false;
                }
                else
                {
                    DiagLog($"SKIPPED DisableDeviceNode: descriptorsChanged={descriptorsChanged}, existingNodes={existingNodes}");
                }
                return true;
            }

            // Ensure exactly 1 device node exists.
            if (existingNodes == 0)
            {
                DiagLog("Creating single vJoy device node");
                if (!CreateVJoyDevices(1))
                {
                    DiagLog("CreateVJoyDevices FAILED");
                    return false;
                }

                // Wait for PnP to bind the driver and make ALL devices available.
                _dllLoaded = false;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(250);
                    EnsureDllLoaded();
                    if (_dllLoaded && AllDevicesReady(requiredCount))
                    {
                        DiagLog($"All {requiredCount} devices ready after {(attempt + 1) * 250}ms");
                        return true;
                    }
                }
                DiagLog("Device created but not ready after 5 seconds");
                return false;
            }

            // Remove excess device nodes (should be exactly 1).
            if (existingNodes > 1)
            {
                DiagLog($"Removing {existingNodes - 1} excess device node(s)");
                var instanceIds = EnumerateVJoyInstanceIds();
                for (int i = instanceIds.Count - 1; i >= 1; i--)
                    RemoveDeviceNode(instanceIds[i]);

                // Restart the remaining node so the driver re-reads descriptors.
                descriptorsChanged = true;
                _dllLoaded = false;
            }

            // If descriptor count changed, restart the device node so the driver
            // re-reads the registry and creates the right number of collections.
            if (descriptorsChanged && existingNodes >= 1)
            {
                // HID descriptors define button/axis/POV counts — these are only parsed
                // during device node creation (EvtDeviceAdd). DICS_PROPCHANGE restarts
                // the driver stack in-place but does NOT re-parse HID descriptors.
                // Always do full remove+create to apply descriptor changes.
                DiagLog($"Restarting device node (descriptors changed, count={requiredCount}, countMismatch={countMismatch})");
                RestartDeviceNode(countChanged: true);
                _dllLoaded = false;

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(250);
                    EnsureDllLoaded();
                    if (_dllLoaded && AllDevicesReady(requiredCount))
                    {
                        DiagLog($"All {requiredCount} devices ready after restart ({(attempt + 1) * 250}ms)");
                        return true;
                    }
                }
                DiagLog("Device not ready after restart (5 seconds)");
                return false;
            }

            return _dllLoaded;
        }

        /// <summary>
        /// Enumerates vJoy device instance IDs via pnputil.
        /// Looks for ROOT\HIDCLASS\* devices whose description contains "vJoy".
        /// </summary>
        internal static System.Collections.Generic.List<string> EnumerateVJoyInstanceIds()
        {
            var results = new System.Collections.Generic.List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-devices /class HIDClass",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return results;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);

                // Parse pnputil output: blocks separated by blank lines.
                // Each block has "Instance ID:", "Device Description:", etc.
                string currentInstanceId = null;
                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.Contains(":"))
                    {
                        currentInstanceId = line.Substring(line.IndexOf(':') + 1).Trim();
                    }
                    else if (currentInstanceId != null &&
                             line.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             currentInstanceId.StartsWith("ROOT\\HIDCLASS\\", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(currentInstanceId);
                        currentInstanceId = null;
                    }
                    else if (string.IsNullOrEmpty(line))
                    {
                        currentInstanceId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] EnumerateVJoyInstanceIds exception: {ex.Message}");
            }
            if (results.Count > 0)
                _cachedInstanceIds = new System.Collections.Generic.List<string>(results);
            return results;
        }

        /// <summary>
        /// Removes the vJoy device node entirely. All HID children (VJOYRAWPDO)
        /// are fully removed from the PnP tree, so they disappear from both
        /// joy.cpl AND WMI/CIM queries. On next EnsureDevicesAvailable(N>0),
        /// the existingNodes==0 path creates a fresh node.
        /// The stale-DLL-handle issue (cached device namespace) is fixed in the
        /// modified vJoyInterface.dll (StatNS_global cleared on QUERYREMOVE).
        /// </summary>
        internal static void DisableDeviceNode()
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                DiagLog($"DisableDeviceNode: found {instanceIds.Count} node(s)");
                if (instanceIds.Count == 0)
                {
                    DiagLog("DisableDeviceNode: NO NODES FOUND — nothing to do");
                    return;
                }

                string id = instanceIds[0];

                // Release all device handles before any node operation.
                RelinquishAllDevices();
                // Close vJoyInterface.dll's internal h0 handle to prevent blocking
                // the disable operation (otherwise takes ~5s for handle timeout).
                RefreshVJoyDllHandles();

                // Step 1: Disable the device node to unload the driver.
                bool disabled = SetupApiRestart.DisableDevice(id);
                DiagLog($"DisableDeviceNode: SetupAPI DICS_DISABLE={disabled}");

                if (!disabled)
                {
                    // Fallback: CfgMgr32 CM_Disable.
                    int cr = CfgMgr32.CM_Locate_DevNodeW(out int devInst, id, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL);
                    if (cr == CfgMgr32.CR_SUCCESS && devInst != 0)
                    {
                        cr = CfgMgr32.CM_Disable_DevNode(devInst, CfgMgr32.CM_DISABLE_HARDWARE);
                        disabled = cr == CfgMgr32.CR_SUCCESS;
                        DiagLog($"DisableDeviceNode: CM_Disable_DevNode={cr}");
                    }
                }

                // Step 2: Fully remove the node so child PDOs (VJOYRAWPDO) are gone
                // from the PnP tree. A mere DICS_DISABLE leaves them enumerable in WMI.
                bool removed = false;
                if (disabled)
                {
                    Thread.Sleep(500);
                    removed = SetupApiRestart.RemoveDevice(id);
                    DiagLog($"DisableDeviceNode: SetupDiRemoveDevice={removed}");
                }

                if (!removed)
                {
                    // Fallback: pnputil /remove-device.
                    int exitCode = RunPnputil($"/remove-device \"{id}\" /subtree");
                    removed = exitCode == 0 || exitCode == 3010;
                    DiagLog($"DisableDeviceNode: pnputil /remove-device exit={exitCode}");
                }

                if (!removed && !disabled)
                {
                    // Last resort: try direct remove without prior disable.
                    removed = SetupApiRestart.RemoveDevice(id);
                    DiagLog($"DisableDeviceNode: SetupDiRemoveDevice (no disable)={removed}");
                }

                _generation++;
                _dllLoaded = false;

                if (removed)
                {
                    DiagLog($"DisableDeviceNode: node fully removed, generation={_generation}");
                    // Scan for hardware changes to clean up ghost child PDOs.
                    // Must be synchronous — an async scan races with ViGEm VC creation
                    // on the next polling cycle, causing IoInvalidateDeviceRelations to
                    // hit the ViGEm bus mid-creation and duplicate existing Xbox 360 devices.
                    try { RunPnputil("/scan-devices"); }
                    catch { /* best effort */ }
                }
                else
                {
                    DiagLog($"DisableDeviceNode: remove failed, node only disabled, generation={_generation}");
                }
            }
            catch (Exception ex)
            {
                DiagLog($"DisableDeviceNode EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>Runs a pnputil command as a fallback, logging output.</summary>
        private static int RunPnputil(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return -1;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5_000);
                DiagLog($"pnputil {arguments}: exit={proc.ExitCode}, stdout={stdout.Trim()}, stderr={stderr.Trim()}");
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                DiagLog($"pnputil EXCEPTION: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Restarts the single vJoy device node (disable → enable) so the driver
        /// re-reads registry descriptors and creates the correct number of HID
        /// top-level collections. This invalidates all AcquireVJD handles — callers
        /// must re-acquire after restart.
        /// </summary>
        /// <summary>
        /// Relinquishes all vJoy device IDs (1–16) so the driver releases its handles.
        /// Must be called before disabling/removing the device node, otherwise
        /// CM_Disable_DevNode returns CR_REMOVE_VETOED (23) because vJoyInterface.dll
        /// still holds the device open.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEQUERYREMOVE = 0x8001;

        /// <summary>
        /// Forces vJoyInterface.dll to close its stale internal control device handle (h0)
        /// and clear its cached device namespace by sending WM_DEVICECHANGE / DBT_DEVICEQUERYREMOVE
        /// to the DLL's hidden window. The Brunner fork creates a hidden desktop window with
        /// class "win32app_vJoyInterface_DLL" on a dedicated thread during InitDll(). Its WndProc
        /// handles DBT_DEVICEQUERYREMOVE by closing all handles, clearing namespace cache, and
        /// setting h0 = INVALID_HANDLE_VALUE. After this, the next GetVJDStatus/AcquireVJD call
        /// triggers GetGenControlHadle() which lazily re-opens h0 for the current device node.
        /// Note: only works after the first vJoy API call (which triggers InitDll/window creation).
        /// Before that, the window doesn't exist yet — this is fine because the DLL has no stale
        /// state to clear on a fresh start.
        /// </summary>
        private static void RefreshVJoyDllHandles()
        {
            try
            {
                IntPtr vjoyWnd = FindWindowW("win32app_vJoyInterface_DLL", null);
                if (vjoyWnd == IntPtr.Zero)
                    return; // Window not created yet (InitDll not called) — no stale state to clear

                SendMessageW(vjoyWnd, WM_DEVICECHANGE, (IntPtr)DBT_DEVICEQUERYREMOVE, IntPtr.Zero);
                DiagLog("RefreshVJoyDllHandles: sent QUERYREMOVE — h0 + namespace cleared");
            }
            catch (Exception ex)
            {
                DiagLog($"RefreshVJoyDllHandles EXCEPTION: {ex.Message}");
            }
        }

        internal static void RelinquishAllDevices()
        {
            if (!_dllLoaded) return;
            for (uint i = 1; i <= 16; i++)
            {
                try { VJoyNative.RelinquishVJD(i); }
                catch { /* best effort */ }
            }
        }

        private static void RestartDeviceNode(bool countChanged = true)
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                if (instanceIds.Count == 0) return;

                string id = instanceIds[0];
                DiagLog($"RestartDeviceNode: restarting {id} (countChanged={countChanged})");

                // Release all device handles before any restart operation.
                RelinquishAllDevices();

                // Pre-close vJoyInterface.dll's internal h0 handle so it doesn't block
                // the subsequent operations. Without this, DICS_DISABLE/PROPCHANGE
                // takes ~5 seconds waiting for the handle to time out.
                RefreshVJoyDllHandles();

                // Strategy depends on whether the descriptor count changed:
                //
                // Content-only change (same device count): Use DICS_PROPCHANGE to restart
                // the driver stack in-place. This re-reads the HID descriptor from registry
                // without recreating child PDOs. Works on Win11 builds where DICS_DISABLE
                // fails (e.g., Build 26200+).
                //
                // Count change: Must fully remove + recreate the device node because
                // HIDCLASS only creates child PDOs during EvtDeviceAdd (device creation).
                // A stop/start re-reads descriptors but does NOT recreate child PDOs.

                if (!countChanged)
                {
                    // Fast path: DICS_PROPCHANGE restarts the driver without removing the node.
                    if (SetupApiRestart.RestartDevice(id))
                    {
                        DiagLog("RestartDeviceNode: DICS_PROPCHANGE succeeded");
                        _generation++;
                        _dllLoaded = false;
                        Thread.Sleep(500);
                        RefreshVJoyDllHandles();
                        return;
                    }
                    DiagLog("RestartDeviceNode: DICS_PROPCHANGE failed, falling through to full restart");
                }

                // Full restart: disable → remove → create.
                bool disabled = false;

                if (SetupApiRestart.DisableDevice(id))
                {
                    DiagLog("RestartDeviceNode: SetupAPI DICS_DISABLE succeeded");
                    disabled = true;
                    Thread.Sleep(500);
                }
                else
                {
                    DiagLog("RestartDeviceNode: SetupAPI DICS_DISABLE failed, trying CfgMgr32");
                    int cr = CfgMgr32.CM_Locate_DevNodeW(out int devInst, id, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL);
                    if (cr == CfgMgr32.CR_SUCCESS && devInst != 0)
                    {
                        cr = CfgMgr32.CM_Disable_DevNode(devInst, CfgMgr32.CM_DISABLE_HARDWARE);
                        DiagLog($"RestartDeviceNode: CM_Disable_DevNode={cr}");
                        if (cr == CfgMgr32.CR_SUCCESS)
                        {
                            disabled = true;
                            Thread.Sleep(500);
                        }
                    }
                }

                bool removed = false;
                if (disabled)
                {
                    removed = SetupApiRestart.RemoveDevice(id);
                    DiagLog($"RestartDeviceNode: SetupDiRemoveDevice={removed}");
                }

                if (!removed)
                {
                    int exitCode = RunPnputil($"/remove-device \"{id}\" /subtree");
                    DiagLog($"RestartDeviceNode: pnputil /remove-device exit={exitCode}");
                    removed = exitCode == 0 || exitCode == 3010;
                }

                if (!removed)
                {
                    removed = SetupApiRestart.RemoveDevice(id);
                    DiagLog($"RestartDeviceNode: SetupDiRemoveDevice (no disable)={removed}");
                }

                if (!removed)
                {
                    // All remove attempts failed. Try DICS_PROPCHANGE as last resort —
                    // won't change PDO count but at least re-reads descriptor contents.
                    DiagLog("RestartDeviceNode: remove failed — trying DICS_PROPCHANGE fallback");
                    if (SetupApiRestart.RestartDevice(id))
                    {
                        DiagLog("RestartDeviceNode: DICS_PROPCHANGE fallback succeeded");
                        _generation++;
                        _dllLoaded = false;
                        Thread.Sleep(500);
                        RefreshVJoyDllHandles();
                        return;
                    }

                    // Truly stuck — re-enable if we disabled.
                    if (disabled)
                    {
                        bool enabled = SetupApiRestart.EnableDevice(id);
                        DiagLog($"RestartDeviceNode: DICS_ENABLE fallback={enabled}");
                    }
                    _generation++;
                    _dllLoaded = false;
                    return;
                }

                // Create a fresh device node.
                _dllLoaded = false;
                _generation++;
                DiagLog($"RestartDeviceNode: node removed, creating fresh node, generation={_generation}");

                Thread.Sleep(500);
                RunPnputil("/scan-devices");
                Thread.Sleep(500);

                if (!CreateVJoyDevices(1))
                {
                    DiagLog("RestartDeviceNode: CreateVJoyDevices FAILED after remove");
                    return;
                }

                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(250);
                    EnsureDllLoaded();
                    if (_dllLoaded)
                    {
                        DiagLog($"RestartDeviceNode: new node ready after {(attempt + 1) * 250 + 500}ms");
                        RefreshVJoyDllHandles();
                        return;
                    }
                }
                DiagLog("RestartDeviceNode: new node not ready after timeout");
            }
            catch (Exception ex)
            {
                DiagLog($"RestartDeviceNode EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a single device node by instance ID via pnputil.
        /// App must be running elevated (which it is when vJoy is installed).
        /// </summary>
        internal static bool RemoveDeviceNode(string instanceId, bool skipScanDevices = false)
        {
            try
            {
                DiagLog($"RemoveDeviceNode: removing {instanceId}");
                int exitCode = RunPnputil($"/remove-device \"{instanceId}\" /subtree");
                // Exit code 3010 = success but reboot required — still counts as removed.
                bool removed = exitCode == 0 || exitCode == 3010;

                if (removed && !skipScanDevices)
                {
                    // Scan for hardware changes to clean up ghost child PDOs (VJOYRAWPDO
                    // entries) that linger in joy.cpl after removal on Win11 26200+.
                    // Run async to avoid blocking the polling thread (scan takes 5-10 seconds).
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { RunPnputil("/scan-devices"); }
                        catch { /* best effort */ }
                    });
                }

                return removed;
            }
            catch (Exception ex)
            {
                DiagLog($"RemoveDeviceNode EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes ALL vJoy device nodes via direct pnputil calls.
        /// App must be running elevated (auto-elevation happens at startup when vJoy is installed).
        /// No PowerShell, no UAC prompt — fast and synchronous.
        /// </summary>
        internal static bool RemoveAllDeviceNodes()
        {
            try
            {
                // Use cached instance IDs when available to skip the expensive
                // pnputil /enum-devices enumeration (saves ~5s on shutdown).
                var instanceIds = _cachedInstanceIds ?? EnumerateVJoyInstanceIds();
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes: found {instanceIds.Count} device(s), fromCache={_cachedInstanceIds != null}");

                // Release all device handles before removal to avoid blocking.
                RelinquishAllDevices();
                RefreshVJoyDllHandles();

                int removed = 0;
                foreach (var id in instanceIds)
                {
                    // Try SetupAPI first (direct API call, no process spawn).
                    // Falls back to pnputil if SetupAPI fails.
                    bool ok = SetupApiRestart.RemoveDevice(id);
                    if (!ok)
                        ok = RemoveDeviceNode(id, skipScanDevices: true);
                    if (ok)
                        removed++;
                }

                Debug.WriteLine($"[vJoy] Removed {removed}/{instanceIds.Count} device node(s)");
                _dllLoaded = false;
                _currentDescriptorCount = 0;
                _cachedInstanceIds = null;

                // Fire-and-forget scan to clean up ghost PDOs in joy.cpl.
                // Don't block shutdown waiting for this.
                if (removed > 0)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { RunPnputil("/scan-devices"); }
                        catch { /* best effort */ }
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates one or more vJoy device nodes using SetupAPI in a single
        /// elevated PowerShell script (one UAC prompt for the whole batch).
        /// Each node gets DICD_GENERATE_ID so Windows picks a unique instance ID.
        /// </summary>
        internal static bool CreateVJoyDevices(int count = 1)
        {
            if (count < 1) return true;
            try
            {
                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.ps1");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.log");

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");

                File.WriteAllText(scriptPath, $@"
$ErrorActionPreference = 'Continue'
$log = '{logPath.Replace("'", "''")}'
try {{
    $svcPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\vjoy'
    if (-not (Test-Path $svcPath)) {{
        New-Item -Path $svcPath -Force | Out-Null
        Set-ItemProperty $svcPath -Name 'Type' -Value 1 -Type DWord
        Set-ItemProperty $svcPath -Name 'Start' -Value 3 -Type DWord
        Set-ItemProperty $svcPath -Name 'ErrorControl' -Value 0 -Type DWord
        Set-ItemProperty $svcPath -Name 'ImagePath' -Value 'System32\DRIVERS\vjoy.sys' -Type ExpandString
    }}
    $src = '{vjoyDir.Replace("'", "''")}\vjoy.sys'
    $dst = ""$env:SystemRoot\System32\drivers\vjoy.sys""
    if (-not (Test-Path $dst) -and (Test-Path $src)) {{ Copy-Item $src $dst -Force }}

" + SetupApiInterop.GetPsSetupApiSnippet() + $@"
    $hidGuid = [Guid]::new('{{745a17a0-74d3-11d0-b6fe-00a0c90f57da}}')
    $hwid = 'root\VID_1234&PID_BEAD&REV_0222'
    $infPath = '{vjoyDir.Replace("'", "''")}\vjoy.inf'
    $hwidBytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + [char]0 + [char]0)
    $created = 0
    for ($i = 0; $i -lt {count}; $i++) {{
        $dis = [PF_SetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
        if ($dis -eq [IntPtr]::new(-1)) {{ continue }}
        $did = New-Object PF_SetupApi+SP_DEVINFO_DATA
        $did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][PF_SetupApi+SP_DEVINFO_DATA])
        $ok = [PF_SetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [PF_SetupApi]::DICD_GENERATE_ID, [ref]$did)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [PF_SetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiCallClassInstaller([PF_SetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
        if ($ok) {{ $created++ }}
        [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null
    }}
    if ($created -eq 0) {{ 'FAIL: No devices created' | Out-File $log -Force; exit 1 }}
    # Bind driver to new device nodes. Flag=0 (no INSTALLFLAG_FORCE) so already-bound
    # devices are left alone — only unmatched nodes get the driver installed.
    # INSTALLFLAG_FORCE (1) would re-bind ALL matching devices, creating duplicate
    # HID children and invalidating existing controller handles.
    $reboot = $false
    $ok = [PF_SetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 0, [ref]$reboot)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: UpdateDriver err=$e (created=$created)"" | Out-File $log -Force; exit 1 }}
    ""OK:$created"" | Out-File $log -Force
}} catch {{
    ""EXCEPTION: $_"" | Out-File $log -Force
    exit 1
}}
");
                try { File.Delete(logPath); } catch { }

                // App runs elevated when vJoy is installed (auto-elevation in App.xaml.cs),
                // so no Verb="runas" needed. Use redirected output for better control.
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Debug.WriteLine($"[vJoy] Creating {count} device node(s) via SetupAPI (PowerShell)...");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(30_000);

                string result = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "NO_LOG";
                Debug.WriteLine($"[vJoy] CreateVJoyDevices result: {result} (exit code: {proc.ExitCode})");

                try { File.Delete(scriptPath); } catch { }
                try { File.Delete(logPath); } catch { }

                return result.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] CreateVJoyDevices exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes HID report descriptors for the required number of vJoy devices.
        /// The descriptor matches the vJoyConf format exactly:
        ///   16 axes × 32-bit (active = Data, inactive = Constant padding)
        ///   + 128-bit POV area (4 × 32-bit continuous DWORDs, range 0–35900)
        ///   + 128-bit button area (1-bit per button, padded to 128 bits)
        /// Total report: 1 byte report ID + 96 bytes data = 97 bytes always.
        /// </summary>
        /// <summary>
        /// Returns true if any registry keys were actually modified (written or deleted).
        /// </summary>
        private static bool WriteDeviceDescriptors(int requiredCount, VJoyDeviceConfig[] perDeviceConfigs)
        {
            DiagLog($"WriteDeviceDescriptors: requiredCount={requiredCount}, perDeviceConfigs={(perDeviceConfigs != null ? perDeviceConfigs.Length.ToString() : "null")}");
            bool anyChanged = false;
            try
            {
                // Use CreateSubKey so the Parameters key is created if it doesn't exist.
                // After a fresh driver install, vjoy.sys uses compiled-in defaults (8/8/0)
                // and does NOT create the Parameters key or DeviceNN subkeys. OpenSubKey
                // would return null here, silently skipping descriptor writes and leaving
                // the device with the wrong configuration.
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\services\vjoy\Parameters");
                if (baseKey == null)
                {
                    DiagLog("WriteDeviceDescriptors: FAILED — cannot create/open Parameters registry key");
                    return false;
                }

                DiagLog($"WriteDeviceDescriptors: existing subkeys=[{string.Join(",", baseKey.GetSubKeyNames())}]");

                // Remove DeviceNN keys beyond the required count.
                foreach (string subKeyName in baseKey.GetSubKeyNames())
                {
                    if (subKeyName.StartsWith("Device", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(subKeyName.Substring(6), out int keyNum) &&
                        keyNum > requiredCount)
                    {
                        try
                        {
                            DiagLog($"WriteDeviceDescriptors: deleting {subKeyName} (keyNum={keyNum} > requiredCount={requiredCount})");
                            baseKey.DeleteSubKeyTree(subKeyName, false);
                            anyChanged = true;
                        }
                        catch (Exception delEx)
                        {
                            DiagLog($"WriteDeviceDescriptors: FAILED to delete {subKeyName}: {delEx.Message}");
                        }
                    }
                }

                // Write descriptor for each device (Device01..DeviceNN).
                // Only overwrite if the existing descriptor doesn't match — avoids
                // disturbing live device nodes whose driver has already read the registry.
                for (int i = 1; i <= requiredCount; i++)
                {
                    int nAxes = perDeviceConfigs[i - 1].Axes;
                    int nButtons = perDeviceConfigs[i - 1].Buttons;
                    int nPovs = perDeviceConfigs[i - 1].Povs;

                    byte[] descriptor = BuildHidDescriptor((byte)i, nAxes, nButtons, nPovs);
                    string keyName = $"Device{i:D2}";
                    using var devKey = baseKey.CreateSubKey(keyName);

                    // Check if existing descriptor already matches.
                    bool needsWrite = true;
                    try
                    {
                        if (devKey.GetValue("HidReportDescriptor") is byte[] existing &&
                            existing.Length == descriptor.Length)
                        {
                            needsWrite = false;
                            for (int b = 0; b < descriptor.Length; b++)
                            {
                                if (existing[b] != descriptor[b]) { needsWrite = true; break; }
                            }
                        }
                    }
                    catch { needsWrite = true; }

                    if (needsWrite)
                    {
                        devKey.SetValue("HidReportDescriptor", descriptor,
                            Microsoft.Win32.RegistryValueKind.Binary);
                        devKey.SetValue("HidReportDescriptorSize", descriptor.Length,
                            Microsoft.Win32.RegistryValueKind.DWord);
                        DiagLog($"Wrote {keyName}: {descriptor.Length} bytes ({nAxes} axes, {nButtons} buttons, {nPovs} POVs)");
                        anyChanged = true;
                    }
                    else
                    {
                        DiagLog($"{keyName}: descriptor already correct, skipping");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog($"WriteDeviceDescriptors EXCEPTION: {ex.Message}");
            }
            DiagLog($"WriteDeviceDescriptors: returning anyChanged={anyChanged}");
            return anyChanged;
        }

        /// <summary>
        /// Builds a HID Report Descriptor matching the vJoyConf format.
        /// The report always has a fixed 97-byte layout:
        ///   1 byte report ID + 16 axes × 4 bytes + 4 POV DWORDs + 128 button bits (16 bytes).
        /// Disabled axes/POVs/buttons are constant padding so offsets always match.
        /// </summary>
        internal static byte[] BuildHidDescriptor(byte reportId, int nAxes, int nButtons, int nPovs)
        {
            nAxes = Math.Clamp(nAxes, 0, 8);
            nButtons = Math.Clamp(nButtons, 0, 128);
            nPovs = Math.Clamp(nPovs, 0, 4);

            byte[] axisUsages = {
                0x30, 0x31, 0x32, 0x33, 0x34, 0x35,   // X, Y, Z, RX, RY, RZ
                0x36, 0x37, 0x38,                       // Slider, Dial, Wheel
                0xC4, 0xC5, 0xC6, 0xC8,                // Accelerator, Brake, Clutch, Steering
                0xB0, 0xBA, 0xBB                        // Aileron, Rudder, Throttle
            };

            var d = new System.Collections.Generic.List<byte>();

            // ── Outer header ──
            d.AddRange(new byte[] { 0x05, 0x01 });         // USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x15, 0x00 });         // LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x09, 0x04 });         // USAGE (Joystick)
            d.AddRange(new byte[] { 0xA1, 0x01 });         // COLLECTION (Application)

            // ── Axes collection ──
            d.AddRange(new byte[] { 0x05, 0x01 });         //   USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x85, reportId });      //   REPORT_ID
            d.AddRange(new byte[] { 0x09, 0x01 });         //   USAGE (Pointer)
            d.AddRange(new byte[] { 0x15, 0x00 });         //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x26, 0xFF, 0x7F });   //   LOGICAL_MAXIMUM (32767)
            d.AddRange(new byte[] { 0x75, 0x20 });         //   REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x01 });         //   REPORT_COUNT (1)
            d.AddRange(new byte[] { 0xA1, 0x00 });         //   COLLECTION (Physical)

            for (int i = 0; i < 16; i++)
            {
                if (i < nAxes)
                {
                    d.AddRange(new byte[] { 0x09, axisUsages[i] });
                    d.AddRange(new byte[] { 0x81, 0x02 });  // INPUT (Data, Var, Abs)
                }
                else
                {
                    d.AddRange(new byte[] { 0x81, 0x01 });  // INPUT (Cnst, Ary, Abs)
                }
            }

            d.Add(0xC0);                                    //   END_COLLECTION (Physical)

            // ── Continuous POV hats — always 128 bits (4 × 32-bit DWORDs) ──
            // Continuous POV uses degree values × 100 (0–35900), enabling 8-way diagonals.
            // Matches vJoyConf continuous POV format.
            if (nPovs > 0)
            {
                d.AddRange(new byte[] { 0x15, 0x00 });              // LOGICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x27, 0x3C, 0x8C, 0x00, 0x00 }); // LOGICAL_MAXIMUM (35900)
                d.AddRange(new byte[] { 0x35, 0x00 });              // PHYSICAL_MINIMUM (0)
                d.AddRange(new byte[] { 0x47, 0x3C, 0x8C, 0x00, 0x00 }); // PHYSICAL_MAXIMUM (35900)
                d.AddRange(new byte[] { 0x65, 0x14 });              // UNIT (Eng Rot:Angular Pos)
                d.AddRange(new byte[] { 0x75, 0x20 });              // REPORT_SIZE (32)
                d.AddRange(new byte[] { 0x95, 0x01 });              // REPORT_COUNT (1)

                for (int p = 0; p < nPovs; p++)
                {
                    d.AddRange(new byte[] { 0x09, 0x39 });          // USAGE (Hat Switch)
                    d.AddRange(new byte[] { 0x81, 0x02 });          // INPUT (Data, Var, Abs)
                }

                if (nPovs < 4)
                {
                    d.AddRange(new byte[] { 0x95, (byte)(4 - nPovs) }); // REPORT_COUNT (remaining)
                    d.AddRange(new byte[] { 0x81, 0x01 });          // INPUT (Cnst, Ary, Abs)
                }
            }
            else
            {
                d.AddRange(new byte[] { 0x75, 0x20 });              // REPORT_SIZE (32)
                d.AddRange(new byte[] { 0x95, 0x04 });              // REPORT_COUNT (4)
                d.AddRange(new byte[] { 0x81, 0x01 });              // INPUT (Cnst, Ary, Abs)
            }

            // ── Buttons — always 128 bits ──
            byte usageMin = (byte)(nButtons > 0 ? 0x01 : 0x00);
            d.AddRange(new byte[] { 0x05, 0x09 });
            d.AddRange(new byte[] { 0x15, 0x00 });
            d.AddRange(new byte[] { 0x25, 0x01 });
            d.AddRange(new byte[] { 0x55, 0x00 });
            d.AddRange(new byte[] { 0x65, 0x00 });
            d.AddRange(new byte[] { 0x19, usageMin });
            d.Add(0x29); d.Add((byte)nButtons);
            d.AddRange(new byte[] { 0x75, 0x01 });
            d.Add(0x95); d.Add((byte)nButtons);
            d.AddRange(new byte[] { 0x81, 0x02 });

            if (nButtons < 128)
            {
                int padBits = 128 - nButtons;
                d.Add(0x75); d.Add((byte)padBits);
                d.AddRange(new byte[] { 0x95, 0x01 });
                d.AddRange(new byte[] { 0x81, 0x01 });
            }

            // ── FFB/PID section (Usage Page 0x0F Physical Interface) ──
            // Appended inside the Application collection so DirectInput
            // discovers FFB actuators and can create effects.
            // Report IDs are offset by 0x10 * reportId to avoid
            // collisions with the joystick input report ID.
            AppendFfbDescriptor(d, reportId);

            d.Add(0xC0);                                    // END_COLLECTION (Application)
            return d.ToArray();
        }

        /// <summary>
        /// Appends the full PID (Physical Interface Device) HID descriptor
        /// for force feedback support. Transcribed from vJoy's hidReportDescFfb.h.
        /// Report IDs are offset by 0x10 * (deviceIndex) to support multi-device.
        /// </summary>
        private static void AppendFfbDescriptor(System.Collections.Generic.List<byte> d, byte reportId)
        {
            // vJoyConf uses: HID_ID_EFFREP + 0x10 * ReportId (1-based).
            // Device 1 → FFB IDs start at 0x11, device 2 → 0x21, etc.
            // This avoids collisions with the joystick input report ID (0x01..0x10).
            int tlid = reportId;
            const byte MAX_EBI = 0x64; // VJOY_FFB_MAX_EFFECTS_BLOCK_INDEX = 100

            // Report ID helpers: base + 0x10 * tlid
            byte rid(int baseId) => (byte)(baseId + 0x10 * tlid);

            d.AddRange(new byte[] { 0x05, 0x0F });         // USAGE_PAGE (Physical Interface)

            // ── Set Effect Report (Output, Report ID 1) ──
            d.AddRange(new byte[] { 0x09, 0x21 });         // Usage Set Effect Report
            d.AddRange(new byte[] { 0xA1, 0x02 });         // Collection Datalink
            d.AddRange(new byte[] { 0x85, rid(0x01) });    // Report ID
            d.AddRange(new byte[] { 0x09, 0x22 });         // Usage Effect Block Index
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 }); // Output 8-bit

            // Effect Type subcollection
            d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 }); // Usage Effect Type, Collection
            d.AddRange(new byte[] {
                0x09, 0x26,  // ET Constant Force
                0x09, 0x27,  // ET Ramp
                0x09, 0x30,  // ET Square
                0x09, 0x31,  // ET Sine
                0x09, 0x32,  // ET Triangle
                0x09, 0x33,  // ET Sawtooth Up
                0x09, 0x34,  // ET Sawtooth Down
                0x09, 0x40,  // ET Spring
                0x09, 0x41,  // ET Damper
                0x09, 0x42,  // ET Inertia
                0x09, 0x43,  // ET Friction
                0x09, 0x29,  // ET Reserved
            });
            d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 }); // Output
            d.Add(0xC0); // End Effect Type collection

            // Duration, Trigger Repeat, Sample Period, Start Delay (4 × 16-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x50, 0x09, 0x54, 0x09, 0x51, 0x09, 0xA7 });
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD }); // Unit: ms
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x04, 0x91, 0x02 });

            // Gain (8-bit)
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 }); // reset unit
            d.AddRange(new byte[] { 0x09, 0x52 }); // Usage Gain
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });

            // Trigger Button (8-bit)
            d.AddRange(new byte[] { 0x09, 0x53 }); // Usage Trigger Button
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x08, 0x35, 0x01, 0x45, 0x08 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });

            // Axes Enable (X, Y actuators)
            d.AddRange(new byte[] { 0x09, 0x55, 0xA1, 0x02 }); // Usage Axes Enable, Collection
            d.AddRange(new byte[] { 0x05, 0x01 }); // Usage Page Generic Desktop
            d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31 }); // Usage X, Usage Y
            d.AddRange(new byte[] { 0x15, 0x00, 0x25, 0x01 }); // 0-1
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x91, 0x02 }); // 2 bits
            d.Add(0xC0); // End Axes Enable

            // Direction Enable + padding
            d.AddRange(new byte[] { 0x05, 0x0F }); // Usage Page Physical Interface
            d.AddRange(new byte[] { 0x09, 0x56, 0x95, 0x01, 0x91, 0x02 }); // Direction Enable, 1 bit
            d.AddRange(new byte[] { 0x95, 0x05, 0x91, 0x03 }); // 5 bits padding (7-2 axes=5)

            // Direction (2 ordinals, 16-bit each, degrees)
            d.AddRange(new byte[] { 0x09, 0x57, 0xA1, 0x02 }); // Usage Direction, Collection
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 }); // Ordinal Instance 1
            d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 }); // Ordinal Instance 2
            d.AddRange(new byte[] { 0x66, 0x14, 0x00, 0x55, 0xFE }); // Unit: degrees ×10^-2
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 }); // LogMax 32767
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xA0, 0x8C, 0x00, 0x00 }); // PhyMax 36000
            d.AddRange(new byte[] { 0x66, 0x00, 0x00 }); // Unit 0
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 }); // 2×16-bit
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 }); // reset
            d.Add(0xC0); // End Direction

            // Type Specific Block Offset (2 ordinals, 16-bit)
            d.AddRange(new byte[] { 0x05, 0x0F, 0x09, 0x58, 0xA1, 0x02 });
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x26, 0xFD, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Type Specific Block Offset
            d.Add(0xC0); // End Set Effect Report

            // ── Set Envelope Report (Output, Report ID 2) ──
            d.AddRange(new byte[] { 0x09, 0x5A, 0xA1, 0x02, 0x85, rid(0x02) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Attack/Fade Level (2×16-bit, 0-10000)
            d.AddRange(new byte[] { 0x09, 0x5B, 0x09, 0x5D });
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            // Attack/Fade Time (2×32-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x5C, 0x09, 0x5E });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x27, 0xFF, 0x7F, 0x00, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x20, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x45, 0x00, 0x66, 0x00, 0x00, 0x55, 0x00 });
            d.Add(0xC0); // End Envelope

            // ── Set Condition Report (Output, Report ID 3) ──
            d.AddRange(new byte[] { 0x09, 0x5F, 0xA1, 0x02, 0x85, rid(0x03) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Parameter Block Offset (4-bit)
            d.AddRange(new byte[] { 0x09, 0x23, 0x15, 0x00, 0x25, 0x03, 0x35, 0x00, 0x45, 0x03 });
            d.AddRange(new byte[] { 0x75, 0x04, 0x95, 0x01, 0x91, 0x02 });
            // Type Specific Block Offset (2 ordinals, 2-bit each)
            d.AddRange(new byte[] { 0x09, 0x58, 0xA1, 0x02 });
            d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00, 0x0B, 0x02, 0x00, 0x0A, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x02, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0);
            // CP Offset, Pos/Neg Coefficient (signed 16-bit)
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x60, 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x61, 0x09, 0x62, 0x95, 0x02, 0x91, 0x02 });
            // Pos/Neg Saturation (unsigned 16-bit, 0-10000)
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x09, 0x63, 0x09, 0x64, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            // Dead Band
            d.AddRange(new byte[] { 0x09, 0x65 });
            d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0); // End Condition

            // ── Set Periodic Report (Output, Report ID 4) ──
            d.AddRange(new byte[] { 0x09, 0x6E, 0xA1, 0x02, 0x85, rid(0x04) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            // Magnitude (16-bit, 0-10000)
            d.AddRange(new byte[] { 0x09, 0x70, 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            // Offset (signed 16-bit)
            d.AddRange(new byte[] { 0x09, 0x6F, 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x95, 0x01, 0x75, 0x10, 0x91, 0x02 });
            // Phase (16-bit, degrees × 10^-2)
            d.AddRange(new byte[] { 0x09, 0x71, 0x66, 0x14, 0x00, 0x55, 0xFE });
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0x9F, 0x8C, 0x00, 0x00, 0x35, 0x00, 0x47, 0x9F, 0x8C, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            // Period (32-bit, ms)
            d.AddRange(new byte[] { 0x09, 0x72, 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x75, 0x20, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x66, 0x00, 0x00, 0x55, 0x00 });
            d.Add(0xC0); // End Periodic

            // ── Set Constant Force Report (Output, Report ID 5) ──
            d.AddRange(new byte[] { 0x09, 0x73, 0xA1, 0x02, 0x85, rid(0x05) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x70 }); // Magnitude (signed 16-bit)
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0); // End Constant Force

            // ── Set Ramp Force Report (Output, Report ID 6) ──
            d.AddRange(new byte[] { 0x09, 0x74, 0xA1, 0x02, 0x85, rid(0x06) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x75, 0x09, 0x76 }); // Ramp Start/End
            d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Ramp

            // ── Custom Force Data Report (Output, Report ID 7) ──
            d.AddRange(new byte[] { 0x09, 0x68, 0xA1, 0x02, 0x85, rid(0x07) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x6C, 0x15, 0x00, 0x26, 0x10, 0x27, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x69, 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x0C, 0x92, 0x02, 0x01 }); // Output Buffered
            d.Add(0xC0); // End Custom Force

            // ── Download Force Sample (Output, Report ID 8) ──
            d.AddRange(new byte[] { 0x09, 0x66, 0xA1, 0x02, 0x85, rid(0x08) });
            d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x30, 0x09, 0x31 }); // X, Y
            d.AddRange(new byte[] { 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x02, 0x91, 0x02 });
            d.Add(0xC0); // End Download Force Sample

            // ── Effect Operation Report (Output, Report ID 0x0A) ──
            d.AddRange(new byte[] { 0x05, 0x0F });
            d.AddRange(new byte[] { 0x09, 0x77, 0xA1, 0x02, 0x85, rid(0x0A) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x78, 0xA1, 0x02 }); // Effect Operation
            d.AddRange(new byte[] { 0x09, 0x79, 0x09, 0x7A, 0x09, 0x7B }); // Start/StartSolo/Stop
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x03, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x09, 0x7C }); // Loop Count
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x91, 0x02 });
            d.Add(0xC0); // End Effect Operation

            // ── PID Block Free Report (Output, Report ID 0x0B) ──
            d.AddRange(new byte[] { 0x09, 0x90, 0xA1, 0x02, 0x85, rid(0x0B) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x25, MAX_EBI, 0x15, 0x01, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0);

            // ── PID Device Control (Output, Report ID 0x0C) ──
            d.AddRange(new byte[] { 0x09, 0x96, 0xA1, 0x02, 0x85, rid(0x0C) });
            d.AddRange(new byte[] { 0x09, 0x97, 0x09, 0x98, 0x09, 0x99, 0x09, 0x9A, 0x09, 0x9B, 0x09, 0x9C });
            d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x06, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
            d.Add(0xC0);

            // ── Device Gain Report (Output, Report ID 0x0D) ──
            d.AddRange(new byte[] { 0x09, 0x7D, 0xA1, 0x02, 0x85, rid(0x0D) });
            d.AddRange(new byte[] { 0x09, 0x7E, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.Add(0xC0);

            // ── Set Custom Force Report (Output, Report ID 0x0E) ──
            d.AddRange(new byte[] { 0x09, 0x6B, 0xA1, 0x02, 0x85, rid(0x0E) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x6D, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x51, 0x66, 0x03, 0x10, 0x55, 0xFD });
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
            d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
            d.Add(0xC0);

            // ── Create New Effect Report (Feature, Report ID 0x01+0x10*tlid but uses same RID space) ──
            // Note: NEWEFREP uses same base ID as EFFREP (0x01) — vJoy quirk.
            // Feature reports use different HID report type so IDs can overlap with Output.
            d.AddRange(new byte[] { 0x09, 0xAB, 0xA1, 0x02, 0x85, rid(0x01) });
            d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 }); // Effect Type
            d.AddRange(new byte[] {
                0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
                0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
                0x09, 0x43, 0x09, 0x29
            });
            d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 }); // Feature
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x3B }); // Usage Page Generic Desktop, Reserved
            d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x01, 0x35, 0x00, 0x46, 0xFF, 0x01 });
            d.AddRange(new byte[] { 0x75, 0x0A, 0x95, 0x01, 0xB1, 0x02 }); // Feature 10-bit
            d.AddRange(new byte[] { 0x75, 0x06, 0xB1, 0x01 }); // Feature padding
            d.Add(0xC0);

            // ── Block Load Report (Feature, Report ID 0x02) ──
            d.AddRange(new byte[] { 0x05, 0x0F });
            d.AddRange(new byte[] { 0x09, 0x89, 0xA1, 0x02, 0x85, rid(0x02) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x25, MAX_EBI, 0x15, 0x01, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 }); // Feature
            d.AddRange(new byte[] { 0x09, 0x8B, 0xA1, 0x02 }); // Block Load Status
            d.AddRange(new byte[] { 0x09, 0x8C, 0x09, 0x8D, 0x09, 0x8E }); // Success/Full/Error
            d.AddRange(new byte[] { 0x25, 0x03, 0x15, 0x01, 0x35, 0x01, 0x45, 0x03 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 });
            d.Add(0xC0);
            d.AddRange(new byte[] { 0x09, 0xAC }); // RAM Pool Available
            d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0xB1, 0x00 });
            d.Add(0xC0);

            // ── PID Pool Report (Feature, Report ID 0x03) ──
            d.AddRange(new byte[] { 0x09, 0x7F, 0xA1, 0x02, 0x85, rid(0x03) });
            d.AddRange(new byte[] { 0x09, 0x80, 0x75, 0x10, 0x95, 0x01 }); // RAM Pool Size
            d.AddRange(new byte[] { 0x15, 0x00, 0x35, 0x00 });
            d.AddRange(new byte[] { 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x47, 0xFF, 0xFF, 0x00, 0x00 });
            d.AddRange(new byte[] { 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x09, 0x83 }); // Simultaneous Effects Max
            d.AddRange(new byte[] { 0x26, 0xFF, 0x00, 0x46, 0xFF, 0x00 });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x09, 0xA9, 0x09, 0xAA }); // Device Managed Pool, Shared Param
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x15, 0x00, 0x25, 0x01, 0x35, 0x00, 0x45, 0x01 });
            d.AddRange(new byte[] { 0xB1, 0x02 });
            d.AddRange(new byte[] { 0x75, 0x06, 0x95, 0x01, 0xB1, 0x03 }); // Padding
            d.Add(0xC0);

            // ── PID State Report (Feature, Report ID 0x04) ──
            d.AddRange(new byte[] { 0x09, 0x92, 0xA1, 0x02, 0x85, rid(0x04) });
            d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, MAX_EBI, 0x35, 0x01, 0x45, MAX_EBI });
            d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02 });
            d.AddRange(new byte[] { 0xA1, 0x02 }); // Subcollection
            d.AddRange(new byte[] { 0x09, 0x94, 0x09, 0x9F, 0x09, 0xA0, 0x09, 0xA4, 0x09, 0xA5, 0x09, 0xA6 });
            d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x06, 0x81, 0x02 }); // Input 6 bits
            d.AddRange(new byte[] { 0x95, 0x02, 0x81, 0x03 }); // 2-bit padding
            d.Add(0xC0);
            d.Add(0xC0);
        }
    }

    // ─────────────────────────────────────────────
    //  vJoy P/Invoke declarations
    // ─────────────────────────────────────────────

    internal enum VjdStat
    {
        VJD_STAT_OWN = 0,
        VJD_STAT_FREE = 1,
        VJD_STAT_BUSY = 2,
        VJD_STAT_MISS = 3,
        VJD_STAT_UNKN = 4
    }

    /// <summary>
    /// JOYSTICK_POSITION_V2 — matches public.h _JOYSTICK_POSITION_V2 struct (108 bytes).
    /// Used by UpdateVJD for single-IOCTL-per-frame output.
    /// Verified working against vJoyInterface.dll v2.2.2 by standalone test tool.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 108)]
    internal struct JoystickPositionV2
    {
        [FieldOffset(0)]  public byte bDevice;       // 1-based device index
        [FieldOffset(4)]  public int wThrottle;
        [FieldOffset(8)]  public int wRudder;
        [FieldOffset(12)] public int wAileron;
        [FieldOffset(16)] public int wAxisX;
        [FieldOffset(20)] public int wAxisY;
        [FieldOffset(24)] public int wAxisZ;
        [FieldOffset(28)] public int wAxisXRot;
        [FieldOffset(32)] public int wAxisYRot;
        [FieldOffset(36)] public int wAxisZRot;
        [FieldOffset(40)] public int wSlider;
        [FieldOffset(44)] public int wDial;
        [FieldOffset(48)] public int wWheel;
        [FieldOffset(52)] public int wAxisVX;
        [FieldOffset(56)] public int wAxisVY;
        [FieldOffset(60)] public int wAxisVZ;
        [FieldOffset(64)] public int wAxisVBRX;
        [FieldOffset(68)] public int wAxisVBRY;
        [FieldOffset(72)] public int wAxisVBRZ;
        [FieldOffset(76)] public int lButtons;       // Buttons 1-32 bitmask
        [FieldOffset(80)] public uint bHats;          // Discrete POV 1 (low nibble)
        [FieldOffset(84)] public uint bHatsEx1;
        [FieldOffset(88)] public uint bHatsEx2;
        [FieldOffset(92)] public uint bHatsEx3;
        [FieldOffset(96)]  public int lButtonsEx1;
        [FieldOffset(100)] public int lButtonsEx2;
        [FieldOffset(104)] public int lButtonsEx3;
    }

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Uses UpdateVJD for single-IOCTL-per-frame output (fastest path).
    /// </summary>
    internal static class VJoyNative
    {
        private const string DLL = "vJoyInterface.dll";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool vJoyEnabled();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern VjdStat GetVJDStatus(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AcquireVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void RelinquishVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateVJD(uint rID, ref JoystickPositionV2 pData);

        // HID Usage IDs for axes (Generic Desktop page 0x01)
        public const uint HID_USAGE_X  = 0x30;
        public const uint HID_USAGE_Y  = 0x31;
        public const uint HID_USAGE_Z  = 0x32;
        public const uint HID_USAGE_RX = 0x33;
        public const uint HID_USAGE_RY = 0x34;
        public const uint HID_USAGE_RZ = 0x35;

        // ── Force Feedback (FFB) ──

        /// <summary>Callback delegate for FfbRegisterGenCB.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FfbGenCB(IntPtr data, IntPtr userData);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void FfbRegisterGenCB(FfbGenCB cb, IntPtr data);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DeviceID(IntPtr packet, ref uint deviceId);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Type(IntPtr packet, ref FFBPType type);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_EffectBlockIndex(IntPtr packet, ref uint index);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Report(IntPtr packet, ref FFB_EFF_REPORT effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Constant(IntPtr packet, ref FFB_EFF_CONSTANT effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Ramp(IntPtr packet, ref FFB_EFF_RAMP effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Period(IntPtr packet, ref FFB_EFF_PERIOD effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_Eff_Cond(IntPtr packet, ref FFB_EFF_COND effect);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_EffOp(IntPtr packet, ref FFB_EFF_OP operation);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DevCtrl(IntPtr packet, ref FFB_CTRL control);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Ffb_h_DevGain(IntPtr packet, ref byte gain);
    }

    // ─────────────────────────────────────────────
    //  FFB enums and structs (matching vJoy SDK public.h)
    // ─────────────────────────────────────────────

    internal enum FFBEType : uint
    {
        ET_NONE  = 0,
        ET_CONST = 1,   // Constant Force
        ET_RAMP  = 2,   // Ramp
        ET_SQR   = 3,   // Square
        ET_SINE  = 4,   // Sine
        ET_TRNGL = 5,   // Triangle
        ET_STUP  = 6,   // Sawtooth Up
        ET_STDN  = 7,   // Sawtooth Down
        ET_SPRNG = 8,   // Spring
        ET_DMPR  = 9,   // Damper
        ET_INRT  = 10,  // Inertia
        ET_FRCTN = 11,  // Friction
        ET_CSTM  = 12,  // Custom Force Data
    }

    internal enum FFBPType : uint
    {
        PT_EFFREP   = 0x01,  // Set Effect Report
        PT_ENVREP   = 0x02,  // Set Envelope Report
        PT_CONDREP  = 0x03,  // Set Condition Report
        PT_PRIDREP  = 0x04,  // Set Periodic Report
        PT_CONSTREP = 0x05,  // Set Constant Force Report
        PT_RAMPREP  = 0x06,  // Set Ramp Force Report
        PT_CSTMREP  = 0x07,  // Custom Force Data Report
        PT_SMPLREP  = 0x08,  // Download Force Sample
        PT_EFOPREP  = 0x0A,  // Effect Operation Report
        PT_BLKFRREP = 0x0B,  // PID Block Free Report
        PT_CTRLREP  = 0x0C,  // PID Device Control
        PT_GAINREP  = 0x0D,  // Device Gain Report
        PT_SETCREP  = 0x0E,  // Set Custom Force Report
        PT_NEWEFREP = 0x11,  // Create New Effect Report
        PT_BLKLDREP = 0x12,  // Block Load Report
        PT_POOLREP  = 0x13,  // PID Pool Report
        PT_STATEREP = 0x14,  // PID State Report
    }

    internal enum FFB_CTRL : uint
    {
        CTRL_ENACT   = 1,  // Enable Actuators
        CTRL_DISACT  = 2,  // Disable Actuators
        CTRL_STOPALL = 3,  // Stop All Effects
        CTRL_DEVRST  = 4,  // Device Reset
        CTRL_DEVPAUSE = 5, // Device Pause
        CTRL_DEVCONT = 6,  // Device Continue
    }

    internal enum FFBOP : uint
    {
        EFF_START = 1,
        EFF_SOLO  = 2,
        EFF_STOP  = 3,
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_REPORT
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public FFBEType EffectType;
        [FieldOffset(8)]  public ushort Duration;
        [FieldOffset(10)] public ushort TrigerRpt;
        [FieldOffset(12)] public ushort SamplePrd;
        [FieldOffset(14)] public ushort StartDelay;
        [FieldOffset(16)] public byte Gain;
        [FieldOffset(17)] public byte TrigerBtn;
        [FieldOffset(18)] public byte AxesEnabledDirection;
        [FieldOffset(20)] public bool Polar;
        [FieldOffset(24)] public ushort Direction;   // Polar: 0–35999 hundredths of degrees
        [FieldOffset(24)] public ushort DirX;        // Cartesian (overlapped with Direction)
        [FieldOffset(26)] public ushort DirY;        // Cartesian
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_CONSTANT
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public short Magnitude;     // -10000..+10000
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_RAMP
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public short Start;         // -10000..+10000
        [FieldOffset(8)] public short End;           // -10000..+10000
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_PERIOD
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public uint Magnitude;      // 0..10000
        [FieldOffset(8)]  public short Offset;        // -10000..+10000
        [FieldOffset(12)] public uint Phase;           // 0..35999
        [FieldOffset(16)] public uint Period;          // ms
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_COND
    {
        [FieldOffset(0)]  public byte EffectBlockIndex;
        [FieldOffset(4)]  public bool IsY;
        [FieldOffset(8)]  public short CenterPointOffset;
        [FieldOffset(12)] public short PosCoeff;       // -10000..+10000
        [FieldOffset(16)] public short NegCoeff;       // -10000..+10000
        [FieldOffset(20)] public uint PosSatur;
        [FieldOffset(24)] public uint NegSatur;
        [FieldOffset(28)] public int DeadBand;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FFB_EFF_OP
    {
        [FieldOffset(0)] public byte EffectBlockIndex;
        [FieldOffset(4)] public FFBOP EffectOp;
        [FieldOffset(8)] public byte LoopCount;
    }

}
