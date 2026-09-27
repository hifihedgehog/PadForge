using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace PadForge.Common.Input
{
    /// <summary>A controller on a COM port that the user added: the port and
    /// the protocol the SDL fork's serial driver speaks on it
    /// (hifihedgehog/SDL#33, the fork's docs/README-serial-joysticks.md).</summary>
    public sealed class SerialControllerEntry
    {
        /// <summary>The port as SDL_JOYSTICK_SERIAL names it: the port's
        /// device instance ID, which follows a USB adapter to a new COM
        /// number, or COMn when the ID cannot be written into the
        /// hint.</summary>
        [XmlAttribute] public string Port { get; set; }

        /// <summary>The port's COM name when the controller was added, for
        /// display.</summary>
        [XmlAttribute] public string PortName { get; set; }

        /// <summary>The fork's protocol token, such as spaceball.</summary>
        [XmlAttribute] public string Protocol { get; set; }

        /// <summary>The device name the protocol table gives the token.</summary>
        [XmlIgnore]
        public string ControllerName => SerialControllers.NameOf(Protocol) ?? Protocol;

        /// <summary>One line for the dialog's list: the controller and its
        /// port.</summary>
        [XmlIgnore]
        public string Display => $"{ControllerName} ({PortName})";
    }

    /// <summary>A present COM port: its name, the name Device Manager shows,
    /// and its device instance ID.</summary>
    public sealed record ComPort(string PortName, string FriendlyName, string InstanceId)
    {
        public override string ToString() => FriendlyName;
    }

    /// <summary>The protocols PadForge offers, the hint it writes, and the
    /// COM ports it lists. An adapter carries its maker's IDs and says
    /// nothing about the device behind it, so the user names the port and
    /// the controller, and SDL opens that port from then on.</summary>
    public static class SerialControllers
    {
        /// <summary>SDL's serial driver holds at most this many named ports
        /// (SDL_serial_engine.h, SDL_SERIAL_MAX_PORTS).</summary>
        public const int MaxEntries = 16;

        /// <summary>Every token of the fork's serial module table
        /// (SDL_serialjoystick.c, serial_modules) but bio2, whose cabinet
        /// comes from a hint, with the devices the fork's table lists for
        /// it. The BIO2 appears here twice, once per cabinet, and a BIO2 the
        /// driver also opens on its own keeps the protocol named
        /// here.</summary>
        public static readonly (string Token, string Name)[] Protocols =
        {
            ("spaceball", "SpaceTec Spaceball 1003, 2003, 3003, 4000 FLX"),
            ("spaceorb", "SpaceTec SpaceOrb 360, SpaceBall Avenger"),
            ("magellan", "Magellan, SpaceMouse, Spaceball 5000, CadMan"),
            ("stinger", "Gravis Stinger"),
            ("warrior", "Logitech WingMan Warrior"),
            ("cyberman", "Logitech CyberMan"),
            ("zhenhua", "Zhen Hua RC"),
            ("ibus", "FlySky i-BUS"),
            ("jvs", "JVS I/O"),
            ("vrinsight", "VRinsight CDU II, MCP Combo I"),
            ("kettler", "Kettler Ergometer"),
            ("iforce", "I-Force"),
            ("mastercontroller", "Pony Canyon Master Controller"),
            ("dji", "DJI RC-N1"),
            ("djimavicmini", "DJI Mavic Mini"),
            ("djiphantom3", "DJI Phantom 3"),
            ("djiphantom2", "DJI Phantom 2"),
            ("bio2iidx", "Konami BIO2 (beatmania IIDX)"),
            ("bio2sdvx", "Konami BIO2 (SOUND VOLTEX)"),
            ("kfca", "Konami KFCA (SOUND VOLTEX)"),
            ("panb", "Konami PANB (Nostalgia)"),
            ("rvol", "Konami RVOL (MUSECA)"),
            ("mdxf", "Konami MDXF (DanceDanceRevolution A)"),
        };

        public static string NameOf(string token)
        {
            foreach (var p in Protocols)
                if (string.Equals(p.Token, token, StringComparison.OrdinalIgnoreCase))
                    return p.Name;
            return null;
        }

        private static readonly Regex ComName = new(@"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Whether SDL's hint parser takes the text as a port
        /// (SDL_serial_engine.c, Serial_ValidKey): COM1 to COM9999, or an
        /// instance ID of at most 200 characters, with a backslash, in 0x21
        /// to 0x7E, and with no comma or equals sign, since the hint splits
        /// entries at commas and each entry at its first equals sign.</summary>
        public static bool IsValidPort(string port)
        {
            if (string.IsNullOrEmpty(port) || port.Length > 200) return false;
            if (ComName.IsMatch(port)) return true;
            if (port.IndexOf('\\') < 0) return false;
            foreach (char c in port)
                if (c < 0x21 || c > 0x7E || c == ',' || c == '=') return false;
            return true;
        }

        /// <summary>What an entry for this port names: the instance ID when
        /// the hint can carry it, the COM name when it cannot, null when
        /// neither works.</summary>
        public static string PortKey(ComPort port)
        {
            if (port == null) return null;
            if (IsValidPort(port.InstanceId)) return port.InstanceId;
            return IsValidPort(port.PortName) ? port.PortName : null;
        }

        /// <summary>SDL_JOYSTICK_SERIAL's value: PORT=PROTOCOL for each
        /// usable entry, the first 16 in order. An entry with an unusable
        /// port or an unknown protocol is left out, not passed on for SDL to
        /// log and skip.</summary>
        public static string HintValue(IEnumerable<SerialControllerEntry> entries)
        {
            if (entries == null) return string.Empty;
            var parts = new List<string>();
            foreach (var e in entries)
            {
                if (e == null || !IsValidPort(e.Port) || NameOf(e.Protocol) == null) continue;
                parts.Add(e.Port + "=" + e.Protocol.ToLowerInvariant());
                if (parts.Count == MaxEntries) break;
            }
            return string.Join(",", parts);
        }

        // ── COM ports ─────────────────────────────────────────────────────
        //
        // The ports the fork's serial driver sees: every present interface
        // of GUID_DEVINTERFACE_COMPORT, named by the PortName value in the
        // device's hardware key (SDL_serialjoystick.c, SERIAL_PortName).

        private static readonly Guid ComPortInterface = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

        public static List<ComPort> ListComPorts()
        {
            var ports = new List<ComPort>();
            var guid = ComPortInterface;
            IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return ports;
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
                {
                    string name = PortNameOf(set, ref dev);
                    if (name == null || !ComName.IsMatch(name)) continue;
                    string id = InstanceIdOf(set, ref dev) ?? string.Empty;
                    string friendly = FriendlyNameOf(set, ref dev);
                    if (string.IsNullOrWhiteSpace(friendly)) friendly = name;
                    else if (friendly.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                        friendly = $"{friendly} ({name})";
                    ports.Add(new ComPort(name.ToUpperInvariant(), friendly, id));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return ports.OrderBy(p => ComNumber(p.PortName)).ToList();
        }

        private static int ComNumber(string name)
            => int.TryParse(name.AsSpan(3), out int n) ? n : int.MaxValue;

        private static string PortNameOf(IntPtr set, ref SP_DEVINFO_DATA dev)
        {
            IntPtr key = SetupDiOpenDevRegKey(set, ref dev, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
            if (key == IntPtr.Zero || key == new IntPtr(-1)) return null;
            try
            {
                var buffer = new byte[64];
                int size = buffer.Length;
                if (RegQueryValueExW(key, "PortName", IntPtr.Zero, out uint type, buffer, ref size) != 0
                    || type != REG_SZ || size <= 0)
                    return null;
                return Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        private static string InstanceIdOf(IntPtr set, ref SP_DEVINFO_DATA dev)
        {
            var buffer = new char[512];
            return SetupDiGetDeviceInstanceIdW(set, ref dev, buffer, buffer.Length, out int needed) && needed > 0
                ? new string(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0')
                : null;
        }

        private static string FriendlyNameOf(IntPtr set, ref SP_DEVINFO_DATA dev)
        {
            foreach (uint prop in new[] { SPDRP_FRIENDLYNAME, SPDRP_DEVICEDESC })
            {
                SetupDiGetDeviceRegistryPropertyW(set, ref dev, prop, out _, null, 0, out uint needed);
                if (needed == 0 || needed > 4096) continue;
                var buffer = new byte[needed];
                if (SetupDiGetDeviceRegistryPropertyW(set, ref dev, prop, out _, buffer, needed, out _))
                {
                    string s = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                    if (s.Length > 0) return s;
                }
            }
            return null;
        }

        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint SPDRP_DEVICEDESC = 0x00;
        private const uint SPDRP_FRIENDLYNAME = 0x0C;
        private const int DICS_FLAG_GLOBAL = 0x1;
        private const int DIREG_DEV = 0x1;
        private const int KEY_READ = 0x20019;
        private const uint REG_SZ = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr set, ref SP_DEVINFO_DATA data, char[] id, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint regType,
            byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(
            IntPtr set, ref SP_DEVINFO_DATA data, int scope, int hwProfile, int keyType, int samDesired);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(
            IntPtr key, string valueName, IntPtr reserved, out uint type, byte[] data, ref int size);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr key);
    }
}
