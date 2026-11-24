using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Whether Windows Dynamic Lighting lights a HID LampArray collection
    /// (#494). While it does, Windows repaints the device, so a direct
    /// path to it is closed: the LampArray worker stands down, and the
    /// linker withdraws a Logitech device's HID++ lighting path too
    /// (<see cref="PeripheralLinker.ChooseLighting"/>).
    ///
    /// <para>Windows keeps the switches in the registry, under
    /// Software\Microsoft\Lighting of the signed-in user: one value,
    /// AmbientLightingEnabled, for every device, and one of the same name
    /// under Devices\&lt;interface path&gt; for a device the user set on its
    /// own. Both names were read on the bench and are strings in
    /// Windows.Devices.Lights.dll and SettingsHandlers_Lighting.dll. The
    /// device's value decides where it is there, since "those settings
    /// override the global settings for that specific device" (Microsoft
    /// Support, Control Dynamic Lighting Devices in Windows). A device with
    /// no value follows the global one, and Windows turns Dynamic Lighting
    /// on by itself (ASUS FAQ 1051416: from build 22621.2361 "Windows
    /// enables the Dynamic Lighting automatically"), so a switch nobody
    /// touched reads as on. Each doubt is settled toward lit: standing down
    /// for nothing costs a color, and writing beside Windows makes two
    /// writers fight over the device.</para>
    ///
    /// <para>On a Windows without the controller that lights devices in
    /// the background nothing does, and the path is open: before build
    /// 22621, or with no SettingsHandlers_Lighting.dll.</para>
    ///
    /// <para>The app runs elevated. When a standard user elevates with
    /// another account's credentials, HKEY_CURRENT_USER is that account's
    /// hive and holds the wrong switches. The switches that count are the
    /// ones of the user whose desktop this is, so the user is taken from
    /// the token of the shell's process and the key is opened under that
    /// user's SID in HKEY_USERS. With no shell window, the process's own
    /// hive is read. The values are read and never written: writing one
    /// would take a device from Windows without the user's hand, in a
    /// store Microsoft documents only through Settings.</para>
    /// </summary>
    internal sealed class WindowsLightingState
    {
        internal const string LightingKey = @"Software\Microsoft\Lighting";
        internal const string DevicesKey = "Devices";
        internal const string EnabledValue = "AmbientLightingEnabled";

        /// <summary>The first Windows 11 build with Dynamic Lighting, and
        /// the Settings handler that comes with it.</summary>
        internal const int FirstBuild = 22621;
        internal const string SettingsHandler = "SettingsHandlers_Lighting.dll";

        private static readonly Lazy<bool> s_hasController = new(() =>
        {
            try
            {
                return Environment.OSVersion.Version.Build >= FirstBuild
                       && File.Exists(Path.Combine(Environment.SystemDirectory, SettingsHandler));
            }
            catch
            {
                return true;
            }
        });

        private readonly Func<bool> _hasController;
        private readonly Func<string, int?> _read;

        public WindowsLightingState()
            : this(null, null) { }

        /// <summary>Test seam: whether this Windows lights devices in the
        /// background at all, and the read of AmbientLightingEnabled under
        /// the shell user's lighting key, for every device when handed
        /// null and else for the device whose key name it is handed. A
        /// value that is not there answers null.</summary>
        internal WindowsLightingState(Func<bool> hasController, Func<string, int?> read)
        {
            _hasController = hasController ?? (() => s_hasController.Value);
            _read = read ?? ReadShellUser;
        }

        /// <summary>Whether Windows lights the collection at this interface
        /// path now. Read from the registry on every call.</summary>
        public bool Lights(string interfacePath)
        {
            try
            {
                if (!_hasController()) return false;
                string key = DeviceKey(interfacePath);
                int? device = key == null ? null : _read(key);
                // The global value is read only for a device with none of
                // its own.
                return Lit(true, device.HasValue ? null : _read(null), device);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>The rule: the device's own switch where it has one,
        /// else the global one, else on.</summary>
        internal static bool Lit(bool hasController, int? global, int? device)
        {
            if (!hasController) return false;
            if (device.HasValue) return device.Value != 0;
            return !global.HasValue || global.Value != 0;
        }

        /// <summary>The name of a collection's key under Devices: its
        /// interface path without the leading "\\?\". The registry compares
        /// key names without regard to case, so the path is used as it
        /// comes. Null for a path that is no interface path.</summary>
        internal static string DeviceKey(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return null;
            string key = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal)
                         || interfacePath.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? interfacePath.Substring(4)
                : interfacePath;
            // A key name holds no backslash. An interface path has none
            // past its prefix.
            return key.Length == 0 || key.Contains('\\') ? null : key;
        }

        /// <summary>AmbientLightingEnabled under the lighting key of the
        /// user whose desktop this is: the global value for null, else the
        /// value of the device with that key name. Null when the key or
        /// the value is not there, or holds no number.</summary>
        internal static int? ReadShellUser(string deviceKey)
            => ReadFor(ShellUserSid(), deviceKey, ReadHive);

        /// <summary>One read of AmbientLightingEnabled in one user's hive:
        /// the user's SID, or null for the hive of the account this process
        /// runs as, and the key under it. <c>HiveThere</c> is false when no
        /// hive of that SID is loaded. A value that is not there is
        /// null.</summary>
        internal delegate (bool HiveThere, int? Value) HiveRead(string sid, string subKey);

        /// <summary>
        /// Whose hive answers, apart from the registry behind it. The
        /// shell user's does where there is a shell and that user's hive
        /// is loaded, and what it holds stands, a value that is not there
        /// included: the other account's value says nothing about this
        /// desktop. With no shell, or no hive loaded for its user, the
        /// process's own hive is read.
        /// </summary>
        internal static int? ReadFor(string shellSid, string deviceKey, HiveRead read)
        {
            string subKey = deviceKey == null ? LightingKey : LightingKey + @"\" + DevicesKey + @"\" + deviceKey;
            try
            {
                if (shellSid != null)
                {
                    var (hiveThere, value) = read(shellSid, subKey);
                    if (hiveThere) return value;
                }
                return read(null, subKey).Value;
            }
            catch
            {
                return null;
            }
        }

        private static (bool HiveThere, int? Value) ReadHive(string sid, string subKey)
        {
            if (sid == null)
            {
                using var own = Registry.CurrentUser.OpenSubKey(subKey);
                return (true, own?.GetValue(EnabledValue) is int ownValue ? ownValue : null);
            }
            using var hive = Registry.Users.OpenSubKey(sid);
            if (hive == null) return (false, null);
            using var key = hive.OpenSubKey(subKey);
            return (true, key?.GetValue(EnabledValue) is int value ? value : null);
        }

        /// <summary>The SID of the user the shell runs as, from the token
        /// of the process that owns the shell's window, or null when there
        /// is no shell window or its token cannot be read.</summary>
        internal static string ShellUserSid()
        {
            try
            {
                IntPtr shell = GetShellWindow();
                if (shell == IntPtr.Zero) return null;
                if (GetWindowThreadProcessId(shell, out uint processId) == 0 || processId == 0) return null;
                IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (process == IntPtr.Zero) return null;
                try
                {
                    if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
                    try
                    {
                        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint needed);
                        if (needed == 0 || needed > 1024) return null;
                        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                        try
                        {
                            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
                            // TOKEN_USER begins with the pointer to the SID,
                            // which lies in the same buffer.
                            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                }
                finally
                {
                    CloseHandle(process);
                }
            }
            catch
            {
                return null;
            }
        }

        // winnt.h (Windows SDK 10.0.26100.0): the right to ask a process
        // for its token and no more, the right to read a token, and the
        // class of the token's user.
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenUser = 1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information,
            uint length, out uint returnedLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
