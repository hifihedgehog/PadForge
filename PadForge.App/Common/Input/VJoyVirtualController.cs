using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PadForge.Engine;

namespace PadForge.Common.Input
{
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

            // Try vJoy installation directory.
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string vjoyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "vJoy", arch, "vJoyInterface.dll");
            if (File.Exists(vjoyPath) && NativeLibrary.TryLoad(vjoyPath, out _))
                _dllLoaded = true;
        }

        private readonly uint _deviceId;
        private bool _connected;

        public VirtualControllerType Type => VirtualControllerType.VJoy;
        public bool IsConnected => _connected;

        public VJoyVirtualController(uint deviceId)
        {
            if (deviceId < 1 || deviceId > 16)
                throw new ArgumentOutOfRangeException(nameof(deviceId), "vJoy device ID must be 1–16.");
            _deviceId = deviceId;
        }

        public void Connect()
        {
            EnsureDllLoaded();
            // Ensure the device exists (auto-create if missing).
            var status = VJoyNative.GetVJDStatus(_deviceId);
            if (status == VjdStat.VJD_STAT_MISS)
            {
                // Device not configured — create it on the fly.
                CreateVJoyDevice(_deviceId);
                status = VJoyNative.GetVJDStatus(_deviceId);
            }

            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
                throw new InvalidOperationException($"vJoy device {_deviceId} is not available (status: {status}).");

            if (status == VjdStat.VJD_STAT_FREE)
            {
                if (!VJoyNative.AcquireVJD(_deviceId))
                    throw new InvalidOperationException($"Failed to acquire vJoy device {_deviceId}.");
            }

            VJoyNative.ResetVJD(_deviceId);
            _connected = true;
        }

        /// <summary>The vJoy device ID (1–16) this controller was created with.</summary>
        public uint DeviceId => _deviceId;

        public void Disconnect()
        {
            if (_connected)
            {
                VJoyNative.RelinquishVJD(_deviceId);
                _connected = false;
            }

            // Delete the vJoy device so it doesn't linger in Windows game controllers.
            DeleteVJoyDevice(_deviceId);
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            // Axes: Gamepad thumbsticks are signed short (-32768 to 32767).
            // vJoy axes are 0 to 32767 (default range).
            // Convert: (short + 32768) / 2 → 0..32767
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbLX), _deviceId, HID_USAGES.HID_USAGE_X);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbLY), _deviceId, HID_USAGES.HID_USAGE_Y);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbRX), _deviceId, HID_USAGES.HID_USAGE_RX);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbRY), _deviceId, HID_USAGES.HID_USAGE_RY);

            // Triggers: 0–255 byte → 0–32767 vJoy axis
            VJoyNative.SetAxis(gp.LeftTrigger * 32767 / 255, _deviceId, HID_USAGES.HID_USAGE_Z);
            VJoyNative.SetAxis(gp.RightTrigger * 32767 / 255, _deviceId, HID_USAGES.HID_USAGE_RZ);

            // Buttons (1-based for vJoy)
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.A), _deviceId, 1);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.B), _deviceId, 2);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.X), _deviceId, 3);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.Y), _deviceId, 4);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_SHOULDER), _deviceId, 5);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER), _deviceId, 6);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.BACK), _deviceId, 7);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.START), _deviceId, 8);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_THUMB), _deviceId, 9);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_THUMB), _deviceId, 10);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.GUIDE), _deviceId, 11);

            // D-Pad → discrete POV hat (0=Up, 1=Right, 2=Down, 3=Left, -1=centered)
            int povValue = GamepadDPadToDiscPov(gp.Buttons);
            VJoyNative.SetDiscPov(povValue, _deviceId, 1);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // vJoy has no rumble/force feedback callback — no-op.
        }

        /// <summary>Converts signed short (-32768..32767) to vJoy range (0..32767).</summary>
        private static int ShortToVJoy(short value)
        {
            return (value + 32768) / 2;
        }

        /// <summary>Extracts D-Pad from Gamepad buttons to vJoy discrete POV value.</summary>
        private static int GamepadDPadToDiscPov(ushort buttons)
        {
            bool up = (buttons & Gamepad.DPAD_UP) != 0;
            bool down = (buttons & Gamepad.DPAD_DOWN) != 0;
            bool left = (buttons & Gamepad.DPAD_LEFT) != 0;
            bool right = (buttons & Gamepad.DPAD_RIGHT) != 0;

            // vJoy discrete POV: 0=Up, 1=Right, 2=Down, 3=Left, -1=centered
            // Diagonals not supported in discrete mode — prioritize cardinal directions.
            if (up) return 0;
            if (right) return 1;
            if (down) return 2;
            if (left) return 3;
            return -1; // centered
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
        /// Returns the next available vJoy device ID (1–16), or 0 if none available.
        /// If all devices are missing (unconfigured), auto-creates the first one
        /// using vJoyConfig.exe with the axes/buttons/POV PadForge needs.
        /// </summary>
        public static uint FindFreeDeviceId()
        {
            try
            {
                EnsureDllLoaded();
                // Scan all 16 device slots for a free one.
                // Don't gate on vJoyEnabled() — it can return false even when
                // the driver is functional (e.g., after clean reinstall).
                bool anyExist = false;
                for (uint id = 1; id <= 16; id++)
                {
                    var status = VJoyNative.GetVJDStatus(id);
                    Debug.WriteLine($"[vJoy] Device {id} status: {status}");
                    if (status == VjdStat.VJD_STAT_FREE)
                        return id;
                    if (status != VjdStat.VJD_STAT_MISS)
                        anyExist = true;
                }

                Debug.WriteLine($"[vJoy] No free devices found. anyExist={anyExist}. Attempting auto-create...");

                // All devices are MISS (unconfigured) — create device 1
                // using vJoyConfig.exe which works independently of the DLL.
                if (!anyExist)
                {
                    bool created = CreateVJoyDevice(1);
                    Debug.WriteLine($"[vJoy] CreateVJoyDevice(1) = {created}");
                    if (created)
                        return 1;
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
        /// Deletes a vJoy device using vJoyConfig.exe so it no longer appears
        /// in Windows game controllers.
        /// </summary>
        internal static bool DeleteVJoyDevice(uint deviceId)
        {
            return RunVJoyConfig($"-d {deviceId}");
        }

        /// <summary>
        /// Creates a vJoy device using vJoyConfig.exe with the layout PadForge needs:
        /// 6 axes (X, Y, Z, RX, RY, RZ), 11 buttons, 1 discrete POV.
        /// </summary>
        internal static bool CreateVJoyDevice(uint deviceId)
        {
            return RunVJoyConfig($"{deviceId} -f -a x y z rx ry rz -b 11 -s 1");
        }

        /// <summary>
        /// Runs vJoyConfig.exe with the given arguments from its own directory
        /// (required — it loads vJoyInstall.dll and other DLLs from its working directory).
        /// </summary>
        private static bool RunVJoyConfig(string arguments)
        {
            string configExe = FindVJoyConfigExe();
            if (configExe == null)
            {
                Debug.WriteLine($"[vJoy] vJoyConfig.exe not found");
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = configExe,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(configExe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                Debug.WriteLine($"[vJoy] Running: \"{configExe}\" {arguments} (cwd: {psi.WorkingDirectory})");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15_000);
                Debug.WriteLine($"[vJoy] Exit code: {proc.ExitCode}, stdout: {stdout.Trim()}, stderr: {stderr.Trim()}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RunVJoyConfig exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Locates vJoyConfig.exe in the vJoy installation directory.
        /// </summary>
        private static string FindVJoyConfigExe()
        {
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");

            string arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            string path = Path.Combine(vjoyDir, arch, "vJoyConfig.exe");
            if (File.Exists(path)) return path;

            // Fallback: check directly in vJoy dir.
            path = Path.Combine(vjoyDir, "vJoyConfig.exe");
            if (File.Exists(path)) return path;

            return null;
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

    internal enum HID_USAGES : uint
    {
        HID_USAGE_X = 0x30,
        HID_USAGE_Y = 0x31,
        HID_USAGE_Z = 0x32,
        HID_USAGE_RX = 0x33,
        HID_USAGE_RY = 0x34,
        HID_USAGE_RZ = 0x35,
        HID_USAGE_SL0 = 0x36,
        HID_USAGE_SL1 = 0x37,
    }

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Only the minimal set of functions needed for virtual controller output.
    /// The DLL is loaded from the vJoy installation directory (must be in PATH
    /// or placed alongside the application).
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
        public static extern bool SetAxis(int value, uint rID, HID_USAGES axis);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDiscPov(int value, uint rID, byte nPov);
    }

}
