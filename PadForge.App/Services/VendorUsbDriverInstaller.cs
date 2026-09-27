using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace PadForge.Services
{
    /// <summary>
    /// Binds the inbox WinUSB driver to the controllers the SDL fork reads
    /// through libusb (hifihedgehog/SDL#33, the fork's
    /// docs/README-vendor-usb.md). Windows gives these devices no driver, or
    /// its generic HID driver, which reads nothing from them, so each one sits
    /// dead until WinUSB serves it. SDL then opens it at its next device
    /// change.
    ///
    /// <para>The DualShock 3 binding in <see cref="Ds3DriverInstaller"/> is
    /// the template: a package signed on this PC with this PC's certificate,
    /// stocked with DiInstallDriver, forced onto the device with
    /// UpdateDriverForPlugAndPlayDevices, taken only from the no-driver or
    /// HidUsb state and never from another vendor's driver. The package is
    /// what differs. The DS3 package is one fixed INF, and a fixed INF that
    /// names a whole-device ID also matches that device's composite parent
    /// when Windows lists the device as composite, which no source records for
    /// several of these. So each package names the one ID of the node it
    /// binds, as libwdi (Zadig) writes one INF per device: the VID and PID,
    /// plus MI for an interface of a composite device (libwdi.c:1051-1093,
    /// :1420-1425). A composite parent is never a target (libwdi.c:970-978),
    /// since usbccgp is not a state this binds from.</para>
    ///
    /// <para>Only devices whose binding takes nothing that PadForge does not
    /// give back are here. The Intel base station's interface 0 is also its
    /// keyboard, the Prodikeys' interface 1 carries its media and sleep keys,
    /// and the Xbox 360 pads leave XInput by the owner's opt-in decision in
    /// #33 Part 15, so none of those is bound on sight.</para>
    /// </summary>
    internal static class VendorUsbDriverInstaller
    {
        /// <summary>The device interface GUID every package here writes. It
        /// marks a node as bound by PadForge, and libusb reads it to open an
        /// interface of a composite device.</summary>
        internal static readonly Guid InterfaceGuid = new Guid("FD826C66-3556-4845-9436-0FE7580DED02");

        internal const string InfName = "padforge_vendorusb.inf";
        internal const string CatalogName = "padforge_vendorusb.cat";

        /// <summary>The compatible ID Windows builds from an original Xbox XID
        /// interface, class 0x58, subclass 0x42 (the fork's
        /// docs/README-xid.md).</summary>
        internal const string XidCompatibleId = @"USB\Class_58&SubClass_42";

        internal enum Match
        {
            /// <summary>The device node, which has no MI.</summary>
            Device,
            /// <summary>The child for one interface number.</summary>
            Interface,
            /// <summary>Every interface child.</summary>
            AnyInterface,
            /// <summary>An interface child whose compatible IDs name a class,
            /// subclass and protocol.</summary>
            InterfaceClass,
        }

        internal readonly record struct Target(
            ushort Vid, ushort Pid, string Name, Match Match, int Interface = -1, string CompatibleId = null);

        /// <summary>The fork's vendor-USB table, each row as its "Bind WinUSB
        /// to" column states it, less the three devices named above.</summary>
        internal static readonly Target[] Targets =
        {
            new(0x057E, 0x0337, "Wii U GameCube adapter", Match.Device),
            new(0x045E, 0x02A0, "Xbox 360 Big Button receiver", Match.Device),
            new(0x045E, 0x02A0, "Xbox 360 Big Button receiver", Match.InterfaceClass,
                CompatibleId: @"USB\Class_FF&SubClass_5D&Prot_04"),
            new(0x14B7, 0x0982, "Gametrak", Match.Device),
            new(0x2CA3, 0x1023, "DJI RC", Match.Interface, 1),
            // I-Force wheels and joysticks, the whole device (README-iforce.md).
            new(0x044F, 0xA01C, "Thrustmaster Motor Sport GT", Match.Device),
            new(0x046D, 0xC281, "Logitech WingMan Force", Match.Device),
            new(0x046D, 0xC291, "Logitech WingMan Formula Force", Match.Device),
            new(0x05EF, 0x020A, "AVB Top Shot Pegasus", Match.Device),
            new(0x05EF, 0x8884, "AVB Mag Turbo Force", Match.Device),
            new(0x05EF, 0x8888, "AVB Top Shot Force Feedback Racing Wheel", Match.Device),
            new(0x061C, 0xC084, "ACT LABS Force RS", Match.Device),
            new(0x061C, 0xC094, "ACT LABS Force RS", Match.Device),
            new(0x061C, 0xC0A4, "ACT LABS Force RS", Match.Device),
            new(0x06A3, 0xFF04, "Saitek R440 Force Wheel", Match.Device),
            new(0x06F8, 0x0001, "Guillemot Race Leader Force Feedback", Match.Device),
            new(0x06F8, 0x0003, "Guillemot Jet Leader Force Feedback", Match.Device),
            new(0x06F8, 0x0004, "Guillemot Force Feedback Racing Wheel", Match.Device),
            new(0x06F8, 0xA302, "Guillemot Jet Leader 3D", Match.Device),
            // Never 0B9A:0800, the GunCon 3: SDL does not read it, and a
            // binding would take it from the tools that do (#33 Part 9).
            new(0x0B9A, 0x016A, "Namco GunCon 2", Match.Device),
            // Train controllers, one interface each (README-train.md).
            new(0x0AE4, 0x0004, "Densha de GO! controller", Match.Device),
            new(0x0AE4, 0x0005, "Densha de GO! Shinkansen controller", Match.Device),
            new(0x0AE4, 0x0007, "Densha de GO! Ryojohen controller", Match.Device),
            new(0x0AE4, 0x0101, "Multi Train Controller", Match.Device),
            new(0x1C06, 0x77A7, "Train Mascon", Match.Device),
            new(0x0B9A, 0x0910, "Namco USIO", Match.Device),
            new(0x0B9A, 0x0900, "Namco USIO", Match.Device),
            new(0x1CCF, 0x8008, "Konami P3IO", Match.Device),
            new(0x1CCF, 0x8008, "Konami P3IO", Match.AnyInterface),
            new(0x1CCF, 0x8010, "Konami P4IO", Match.Device),
            new(0x1CCF, 0x8010, "Konami P4IO", Match.Interface, 0),
            new(0x068E, 0x00F0, "CH Products Multi-Function Panel", Match.Device),
            // Interface 1 only. Interface 0, the keyboard, stays with Windows.
            new(0x1603, 0x0002, "Ergodex DX1", Match.Interface, 1),
            new(0x131D, 0x0150, "NaturalPoint TrackIR 2", Match.Device),
            new(0x131D, 0x0155, "NaturalPoint TrackIR 3", Match.Device),
            new(0x3561, 0x1904, "Tacx T1904", Match.Device),
            new(0x3561, 0x1932, "Tacx T1932", Match.Device),
        };

        /// <summary>The IDs of the fork's XID table
        /// (src/joystick/hidapi/SDL_hidapi_xid_proto.c, xid_known_devices) and
        /// the Steel Battalion's. README-xid.md binds by these only when
        /// Windows lists no class 0x58 compatible ID for a device.</summary>
        internal static readonly (ushort Vid, ushort Pid)[] XidIdentities =
        {
            (0x044F, 0x0F00), (0x044F, 0x0F03), (0x044F, 0x0F07), (0x044F, 0x0F10),
            (0x045E, 0x0202), (0x045E, 0x0285), (0x045E, 0x0287), (0x045E, 0x0288), (0x045E, 0x0289),
            (0x046D, 0xCA84), (0x046D, 0xCA88), (0x046D, 0xCA8A),
            (0x05FD, 0x1007), (0x05FD, 0x107A), (0x05FE, 0x3030), (0x05FE, 0x3031),
            (0x062A, 0x0020), (0x062A, 0x0033), (0x06A3, 0x0200), (0x06A3, 0x0201),
            (0x0738, 0x4506), (0x0738, 0x4516), (0x0738, 0x4520), (0x0738, 0x4522), (0x0738, 0x4526),
            (0x0738, 0x4530), (0x0738, 0x4536), (0x0738, 0x4540), (0x0738, 0x4556), (0x0738, 0x4586),
            (0x0738, 0x4588), (0x0738, 0x45FF), (0x0738, 0x4743), (0x0738, 0x6040),
            (0x0B9A, 0x016B), (0x0C12, 0x0005), (0x0C12, 0x8801), (0x0C12, 0x8802), (0x0C12, 0x8809),
            (0x0C12, 0x880A), (0x0C12, 0x8810), (0x0C12, 0x9902), (0x0D2F, 0x0002),
            (0x0E4C, 0x1097), (0x0E4C, 0x1103), (0x0E4C, 0x2390), (0x0E4C, 0x3240), (0x0E4C, 0x3510),
            (0x0E6F, 0x0003), (0x0E6F, 0x0005), (0x0E6F, 0x0006), (0x0E6F, 0x0008),
            (0x0E8F, 0x0201), (0x0E8F, 0x3008), (0x0F30, 0x010B), (0x0F30, 0x0202), (0x0F30, 0x8888),
            (0x102C, 0xFF0C), (0x1292, 0x3006), (0x12AB, 0x8809), (0x1430, 0x8888),
            (0x3767, 0x0101), (0xFFFF, 0xFFFF),
            (0x0A7B, 0xD000), // Steel Battalion
        };

        /// <summary>One present node of the USB enumerator. Interface is the
        /// MI number, or -1 for a device node.</summary>
        internal readonly record struct UsbNode(
            string InstanceId, ushort Vid, ushort Pid, int Interface, string Service,
            string[] HardwareIds, string[] CompatibleIds);

        /// <summary>What to bind: the hardware or compatible ID the package
        /// names and UpdateDriverForPlugAndPlayDevices matches.</summary>
        internal readonly record struct BindPlan(string BindId, string Name);

        /// <summary>The binding a node needs, or null. A node qualifies only
        /// with no driver or on HidUsb, the allowlist of
        /// <see cref="Ds3DriverInstaller.IsUsbPadNeedingWinUsb"/>. usbccgp, a
        /// vendor driver, and WinUSB itself are left alone.</summary>
        internal static BindPlan? Plan(in UsbNode node)
        {
            string service = node.Service ?? string.Empty;
            bool noDriver = service.Length == 0;

            // Windows has no driver for an XID interface, whatever its IDs, so
            // a class 0x58 node is bound from the no-driver state alone. The
            // package names the compatible ID, which also lets Windows bind
            // any later XID device to it on its own.
            if (HasCompatibleId(node, XidCompatibleId))
                return noDriver ? new BindPlan(XidCompatibleId, "Original Xbox controller") : null;

            if (!noDriver && !service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var t in Targets)
            {
                if (t.Vid == node.Vid && t.Pid == node.Pid && Matches(t, node))
                    return new BindPlan(BindId(node), t.Name);
            }

            if (noDriver && node.Interface < 0 && Array.IndexOf(XidIdentities, (node.Vid, node.Pid)) >= 0)
                return new BindPlan(BindId(node), "Original Xbox controller");
            return null;
        }

        private static bool Matches(in Target t, in UsbNode node) => NodeMatches(t.Match, t.Interface, t.CompatibleId, node);

        private static bool NodeMatches(Match match, int iface, string compatibleId, in UsbNode node) => match switch
        {
            Match.Device => node.Interface < 0,
            Match.Interface => node.Interface == iface,
            Match.AnyInterface => node.Interface >= 0,
            Match.InterfaceClass => node.Interface >= 0 && HasCompatibleId(node, compatibleId),
            _ => false,
        };

        // ── Opt-in bindings ──────────────────────────────────────────────
        //
        // WinUSB takes something from these that PadForge does not give back,
        // so each is bound only when the user asks, from its row on the
        // Devices page, and one click gives Windows its driver back. Rules 1
        // to 5 of #33 Part 15's Decision: opt-in with the cost stated, the
        // binding persistent on the device node, owned through PadForge's
        // interface GUID, checked at every start, and reversed through
        // DiUninstallDriver.

        internal enum OptInKind { Xbox360Pad, Xbox360Receiver, IntelBaseStation, Prodikeys }

        /// <summary>One opt-in device. Revision names the bcdDevice a whole
        /// device must carry, and From the drivers it is bound from.</summary>
        internal readonly record struct OptInTarget(
            ushort Vid, ushort Pid, Match Match, int Interface, string Revision, OptInKind Kind, string[] From);

        internal static readonly OptInTarget[] OptIns =
        {
            // The wired pads whose chatpad the fork reads, bcdDevice 1.10 and
            // 1.14 (#33 Part 15, xboxdrv's check), and the wireless receiver,
            // each from xusb22.
            new(0x045E, 0x028E, Match.Device, -1, "0110", OptInKind.Xbox360Pad, new[] { "xusb22" }),
            new(0x045E, 0x028E, Match.Device, -1, "0114", OptInKind.Xbox360Pad, new[] { "xusb22" }),
            new(0x045E, 0x0719, Match.Device, -1, "0100", OptInKind.Xbox360Receiver, new[] { "xusb22" }),
            // The Intel base station's interface 0 is also its keyboard (#33
            // Part 1), whether Windows lists the station as composite or not.
            new(0x8086, 0xC013, Match.Interface, 0, null, OptInKind.IntelBaseStation, new[] { "", "HidUsb" }),
            new(0x8086, 0xC013, Match.Device, -1, null, OptInKind.IntelBaseStation, new[] { "", "HidUsb" }),
            // The Prodikeys' interface 1 carries its media and sleep keys (#33 Part 7).
            new(0x041E, 0x2801, Match.Interface, 1, null, OptInKind.Prodikeys, new[] { "", "HidUsb" }),
        };

        /// <summary>What the Devices page offers for a device: to bind it,
        /// to restore Windows' driver, or nothing (null).</summary>
        internal sealed record OptInOffer(OptInKind Kind, BindPlan? Bind, string[] From, string NodeId, bool Restore);

        /// <summary>The device name an opt-in package gives Device Manager.</summary>
        internal static string NameOf(OptInKind kind) => kind switch
        {
            OptInKind.Xbox360Pad => "Xbox 360 Controller",
            OptInKind.Xbox360Receiver => "Xbox 360 Wireless Receiver",
            OptInKind.IntelBaseStation => "Intel Wireless Series base station",
            _ => "Creative Prodikeys PC-MIDI",
        };

        /// <summary>Whether a Devices-page row with these IDs is the target's
        /// device. A pad on the wireless receiver reports the wireless
        /// controller's 045E:02A1 through XInput, not the receiver's
        /// 0719.</summary>
        private static bool RowIsTarget(in OptInTarget t, ushort vid, ushort pid)
            => t.Vid == vid && (t.Pid == pid || (t.Kind == OptInKind.Xbox360Receiver && pid == 0x02A1));

        /// <summary>The offer for a device row with these IDs, from the
        /// present USB nodes: a bind when a node sits on a driver the target
        /// is bound from, a restore when a node is on WinUSB with PadForge's
        /// interface active. A node on anything else is not PadForge's to
        /// touch.</summary>
        internal static OptInOffer QueryOptIn(ushort vid, ushort pid, IReadOnlyList<UsbNode> nodes,
            Func<string, bool> ours = null)
        {
            ours ??= HasActiveInterface;
            foreach (var t in OptIns)
            {
                if (!RowIsTarget(t, vid, pid)) continue;
                foreach (var n in nodes)
                {
                    if (n.Vid != t.Vid || n.Pid != t.Pid || !NodeMatches(t.Match, t.Interface, null, n)) continue;
                    string revisionId = t.Revision == null ? null : $@"USB\VID_{t.Vid:X4}&PID_{t.Pid:X4}&REV_{t.Revision}";
                    if (revisionId != null && !Names(n, revisionId)) continue;

                    string service = n.Service ?? string.Empty;
                    if (t.From.Any(f => f.Equals(service, StringComparison.OrdinalIgnoreCase)))
                        return new OptInOffer(t.Kind, new BindPlan(revisionId ?? BindId(n), NameOf(t.Kind)), t.From, n.InstanceId, false);
                    if (service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase) && ours(n.InstanceId))
                        return new OptInOffer(t.Kind, null, t.From, n.InstanceId, true);
                }
            }
            return null;
        }

        /// <summary>Whether a Devices-page row can carry an opt-in offer at
        /// all, checked before any USB node is read.</summary>
        internal static bool IsOptInRow(ushort vid, ushort pid)
            => OptIns.Any(t => RowIsTarget(t, vid, pid));

        /// <summary>The kind an opted-in ID belongs to, or null.</summary>
        internal static OptInKind? KindOfId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var t in OptIns)
                if (id.StartsWith($@"USB\VID_{t.Vid:X4}&PID_{t.Pid:X4}", StringComparison.OrdinalIgnoreCase))
                    return t.Kind;
            return null;
        }

        /// <summary>The opted-in IDs whose devices Windows has put back on
        /// the driver they were taken from, for Part 15's rule 4: PadForge
        /// says so at start. A node on another port is a new node, and
        /// Windows gives it its own driver too.</summary>
        internal static List<string> MovedBack(IEnumerable<string> optedIn, IReadOnlyList<UsbNode> nodes)
        {
            var moved = new List<string>();
            if (optedIn == null) return moved;
            foreach (string id in optedIn.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = OptIns.FirstOrDefault(t => id.StartsWith($@"USB\VID_{t.Vid:X4}&PID_{t.Pid:X4}", StringComparison.OrdinalIgnoreCase));
                if (target.From == null) continue;
                if (nodes.Any(n => Names(n, id)
                        && target.From.Any(f => f.Equals(n.Service ?? string.Empty, StringComparison.OrdinalIgnoreCase))))
                    moved.Add(id);
            }
            return moved;
        }

        /// <summary>Gives the node back to Windows: DiUninstallDriver on the
        /// package bound to it installs the best remaining driver on every
        /// device that package serves, xusb22 or HidUsb here, and the package
        /// leaves the driver store (Nefarius Devcon.DeleteDriver). Only a
        /// package PadForge wrote is removed.</summary>
        internal static bool Restore(string instanceId, Action<string> log, CancellationToken ct)
        {
            try
            {
                var dev = PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal);
                string inf = dev.GetProperty<string>(DevicePropertyKey.Device_DriverInfPath);
                string provider = dev.GetProperty<string>(DevicePropertyKey.Device_DriverProvider);
                if (string.IsNullOrEmpty(inf) || !string.Equals(provider, "PadForge", StringComparison.Ordinal))
                {
                    log($"{instanceId}: its driver is {inf ?? "(none)"} from {provider ?? "(unknown)"}, not PadForge's, so it stays.");
                    return false;
                }
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
                Devcon.DeleteDriver(inf, path, forceDelete: true);

                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    string service = ServiceOf(instanceId);
                    if (service != null && !service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"{instanceId} is back on {(service.Length == 0 ? "no driver" : service)}.");
                        return true;
                    }
                    Thread.Sleep(250);
                }
                log($"{instanceId}: {inf} was removed, and the node still reports {ServiceOf(instanceId) ?? "(unknown)"}.");
                return false;
            }
            catch (Exception ex)
            {
                log($"{instanceId}: restoring the Windows driver failed: {ex.Message}");
                return false;
            }
        }

        internal static bool HasCompatibleId(in UsbNode node, string id)
            => node.CompatibleIds != null
               && node.CompatibleIds.Any(c => c.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The node's own ID without REV, as libwdi forms it.</summary>
        internal static string BindId(in UsbNode node) => node.Interface < 0
            ? $@"USB\VID_{node.Vid:X4}&PID_{node.Pid:X4}"
            : $@"USB\VID_{node.Vid:X4}&PID_{node.Pid:X4}&MI_{node.Interface:X2}";

        /// <summary>Reads VID, PID and MI from a USB instance ID such as
        /// <c>USB\VID_8086&amp;PID_C013&amp;MI_00\7&amp;1A2B3C&amp;0&amp;0000</c>.
        /// Only the device part, before the second backslash, is read. A node
        /// without a VID and PID there, a root hub for one, is no target, and
        /// neither is a device part with any other token, such as the
        /// <c>&amp;LAMPARRAY</c> children a Logitech receiver lists: the table
        /// describes devices and their interfaces, nothing else.</summary>
        internal static bool TryParseUsbInstanceId(string instanceId, out ushort vid, out ushort pid, out int mi)
        {
            vid = 0;
            pid = 0;
            mi = -1;
            if (string.IsNullOrEmpty(instanceId)
                || !instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                return false;
            string device = instanceId.Substring(4);
            int end = device.IndexOf('\\');
            if (end >= 0) device = device.Substring(0, end);

            bool haveVid = false, havePid = false;
            foreach (string token in device.Split('&'))
            {
                if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    haveVid = ushort.TryParse(token.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out vid);
                else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                    havePid = ushort.TryParse(token.AsSpan(4), System.Globalization.NumberStyles.HexNumber, null, out pid);
                else if (token.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
                         && byte.TryParse(token.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out byte n))
                    mi = n;
                else
                    return false;
            }
            return haveVid && havePid;
        }

        /// <summary>The package's INF: WinUSB for the one ID, on x64 and
        /// ARM64, with PadForge's interface GUID. The DS3 package's INF
        /// (Resources/BthPS3/WinUSB/ds3_winusb.inf) with its models replaced,
        /// ASCII with CRLF line ends.</summary>
        internal static string BuildInf(string bindId, string name)
        {
            string device = new string((name + " (WinUSB)")
                .Where(c => c >= 0x20 && c < 0x7F && c != '"' && c != '%').ToArray());
            var lines = new[]
            {
                "; " + InfName,
                "; Binds the inbox WinUSB driver to one controller the SDL fork reads through",
                "; libusb. PadForge wrote this INF and signed its catalog on this PC.",
                "",
                "[Version]",
                "Signature   = \"$Windows NT$\"",
                "Class       = USBDevice",
                "ClassGUID   = {88BAE032-5A81-49F0-BC3D-A4FF138216D6}",
                "Provider    = %ProviderName%",
                "CatalogFile = " + CatalogName,
                "DriverVer   = 09/26/2026,1.0.0.0",
                "PnpLockdown = 1",
                "",
                "[Manufacturer]",
                "%ProviderName% = Standard,NTamd64,NTarm64",
                "",
                "[Standard.NTamd64]",
                "%DeviceName% = USB_Install, " + bindId,
                "",
                "[Standard.NTarm64]",
                "%DeviceName% = USB_Install, " + bindId,
                "",
                "[USB_Install]",
                "Include = winusb.inf",
                "Needs   = WINUSB.NT",
                "",
                "[USB_Install.Services]",
                "Include = winusb.inf",
                "Needs   = WINUSB.NT.Services",
                "",
                "[USB_Install.HW]",
                "AddReg = Dev_AddReg",
                "",
                "[Dev_AddReg]",
                "HKR,,DeviceInterfaceGUIDs,0x10000,\"{" + InterfaceGuid.ToString().ToUpperInvariant() + "}\"",
                "",
                "[Strings]",
                "ProviderName = \"PadForge\"",
                "DeviceName   = \"" + device + "\"",
            };
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>Where a package is staged: its own folder under the temp
        /// directory, named for the ID.</summary>
        internal static string StagingDirectory(string bindId)
        {
            var name = new StringBuilder();
            foreach (char c in bindId) name.Append(char.IsLetterOrDigit(c) ? c : '_');
            return Path.Combine(Path.GetTempPath(), "PadForge", "VendorUsb", name.ToString());
        }

        /// <summary>Binds WinUSB to the nodes the plan's ID names, and says
        /// whether the planned node is on WinUSB with PadForge's interface
        /// active afterward. <paramref name="from"/> names the drivers a node
        /// may be taken from: no driver and HidUsb unless an opt-in says
        /// otherwise.</summary>
        internal static bool Bind(BindPlan plan, string instanceId, IReadOnlyList<UsbNode> nodes,
            Action<string> log, CancellationToken ct, string[] from = null)
        {
            from ??= new[] { "", "HidUsb" };
            try
            {
                // The forced update below takes EVERY present node the ID
                // names, so a second unit of the same model on another
                // vendor's driver would go with it. Refuse the ID instead, as
                // the DS3 bind refuses a pad a third-party driver owns.
                foreach (var n in nodes)
                {
                    if (!Names(n, plan.BindId)) continue;
                    string s = n.Service ?? string.Empty;
                    if (!from.Any(f => f.Equals(s, StringComparison.OrdinalIgnoreCase))
                        && !s.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
                    {
                        log($"{plan.Name}: {n.InstanceId} is on {s}, so {plan.BindId} stays as it is.");
                        return false;
                    }
                }

                string dir = StagingDirectory(plan.BindId);
                Directory.CreateDirectory(dir);
                string inf = Path.Combine(dir, InfName);
                File.WriteAllText(inf, BuildInf(plan.BindId, plan.Name), Encoding.ASCII);

                // Signed fresh on every bind, as the DS3 package is, so a
                // catalog left from an earlier INF never covers this one.
                if (!Ds3DriverInstaller.SignDriverPackage(dir, CatalogName, log))
                    return false;
                if (!Ds3DriverInstaller.IsCatalogTrusted(Path.Combine(dir, CatalogName), out string signer))
                {
                    log($"{plan.Name}: the package is still untrusted (signer: {signer ?? "unknown"}), "
                        + "so Windows would refuse it.");
                    return false;
                }

                // DiInstallDriver stocks the driver store, so a later plug-in
                // with no driver takes the package on its own. The forced
                // update binds the node now: ranking prefers an inbox driver,
                // HidUsb here, over a package signed on this PC, which is why
                // the DS3 bind forces it too.
                if (!Devcon.Install(inf, out bool reboot))
                    log($"{plan.Name}: DiInstallDriver declined {plan.BindId}.");
                else if (reboot)
                    log($"{plan.Name}: Windows asked for a restart after the install.");
                if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, plan.BindId, inf,
                        INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out _))
                    log($"{plan.Name}: forced WinUSB bind for {plan.BindId} returned err={Marshal.GetLastWin32Error()}.");

                // Done means the node itself is on WinUSB and PadForge's
                // interface on it is active, not that a call returned true.
                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    if (string.Equals(ServiceOf(instanceId), "WINUSB", StringComparison.OrdinalIgnoreCase)
                        && HasActiveInterface(instanceId))
                    {
                        log($"{plan.Name} ({instanceId}) is on WinUSB.");
                        return true;
                    }
                    Thread.Sleep(250);
                }
                log($"{plan.Name}: {instanceId} is still on {ServiceOf(instanceId) ?? "(unknown)"} after the bind.");
                return false;
            }
            catch (Exception ex)
            {
                log($"{plan.Name}: WinUSB bind failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>True when an interface with PadForge's GUID is active on
        /// the node, so winusb.sys serves it now. A service name alone also
        /// names a device that failed to start. This is the DS3 bind's proof
        /// (Ds3DriverInstaller.HasActiveWinUsbInterface), matched to the node
        /// by its instance ID, which the interface path carries with # for
        /// each backslash.</summary>
        internal static bool HasActiveInterface(string instanceId)
        {
            string token = instanceId.Replace('\\', '#');
            var guid = InterfaceGuid;
            IntPtr set = SetupDiGetClassDevsGuid(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
            try
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
                {
                    if ((iface.Flags & SPINT_ACTIVE) == 0) continue;
                    int required = 0;
                    SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, required, ref required, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(detail + 4);
                            if (path != null && path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>Whether one of the node's hardware or compatible IDs is
        /// exactly <paramref name="id"/>, the match
        /// UpdateDriverForPlugAndPlayDevices makes.</summary>
        internal static bool Names(in UsbNode node, string id)
            => (node.HardwareIds != null && node.HardwareIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase)))
               || HasCompatibleId(node, id);

        /// <summary>The node's bound service, "" with no driver, or null when
        /// the node cannot be read.</summary>
        private static string ServiceOf(string instanceId)
        {
            try
            {
                var dev = PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal);
                return dev.GetProperty<string>(DevicePropertyKey.Device_Service) ?? string.Empty;
            }
            catch { return null; }
        }

        // ── Enumeration ────────────────────────────────────────────────────
        //
        // Every present node of the USB enumerator in one SetupAPI pass, all
        // classes, as libwdi lists devices (libwdi.c:909-1035).

        internal static List<UsbNode> ListPresentUsbNodes()
        {
            var nodes = new List<UsbNode>();
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return nodes;
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
                {
                    string id = InstanceIdOf(set, ref dev);
                    if (!TryParseUsbInstanceId(id, out ushort vid, out ushort pid, out int mi))
                        continue;
                    string[] service = MultiString(set, ref dev, SPDRP_SERVICE);
                    nodes.Add(new UsbNode(id, vid, pid, mi,
                        service.Length > 0 ? service[0] : string.Empty,
                        MultiString(set, ref dev, SPDRP_HARDWAREID),
                        MultiString(set, ref dev, SPDRP_COMPATIBLEIDS)));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return nodes;
        }

        private static string InstanceIdOf(IntPtr set, ref SP_DEVINFO_DATA dev)
        {
            var buffer = new char[512];
            return SetupDiGetDeviceInstanceIdW(set, ref dev, buffer, buffer.Length, out int needed) && needed > 0
                ? new string(buffer, 0, Math.Min(needed, buffer.Length)).TrimEnd('\0')
                : null;
        }

        /// <summary>A REG_SZ or REG_MULTI_SZ property as its strings. None
        /// when the node has no such property, as a driverless node has no
        /// service.</summary>
        private static string[] MultiString(IntPtr set, ref SP_DEVINFO_DATA dev, uint prop)
        {
            SetupDiGetDeviceRegistryPropertyW(set, ref dev, prop, out _, null, 0, out uint needed);
            if (needed == 0 || needed > 16384) return Array.Empty<string>();
            var buffer = new byte[needed];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref dev, prop, out _, buffer, needed, out _))
                return Array.Empty<string>();
            return Encoding.Unicode.GetString(buffer)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        private const int SPINT_ACTIVE = 0x1;
        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_ALLCLASSES = 0x4;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private const uint SPDRP_HARDWAREID = 0x01;
        private const uint SPDRP_COMPATIBLEIDS = 0x02;
        private const uint SPDRP_SERVICE = 0x04;
        private const uint INSTALLFLAG_FORCE = 0x1;
        private const uint INSTALLFLAG_NONINTERACTIVE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        private static extern IntPtr SetupDiGetClassDevsGuid(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr set, IntPtr devInfo, ref Guid interfaceGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(
            IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, ref int required, IntPtr devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr set, ref SP_DEVINFO_DATA data, char[] id, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(
            IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint regType,
            byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);
    }
}
