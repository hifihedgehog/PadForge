using System;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;
using Node = PadForge.Services.VendorUsbDriverInstaller.UsbNode;

namespace PadForge.Tests
{
    /// <summary>
    /// The WinUSB binder for the controllers the SDL fork reads through
    /// libusb (hifihedgehog/SDL#33). Which node it binds, from which driver
    /// state, and the package it writes for it. The fork's
    /// docs/README-vendor-usb.md is the table these rows come from.
    /// </summary>
    public class VendorUsbBindingTests
    {
        private readonly ITestOutputHelper _output;
        public VendorUsbBindingTests(ITestOutputHelper output) => _output = output;

        private static Node DeviceNode(ushort vid, ushort pid, string service, params string[] compatible)
            => new($@"USB\VID_{vid:X4}&PID_{pid:X4}\5&1A2B3C4D&0&1", vid, pid, -1, service,
                new[] { $@"USB\VID_{vid:X4}&PID_{pid:X4}&REV_0100", $@"USB\VID_{vid:X4}&PID_{pid:X4}" },
                compatible);

        private static Node InterfaceNode(ushort vid, ushort pid, int mi, string service, params string[] compatible)
            => new($@"USB\VID_{vid:X4}&PID_{pid:X4}&MI_{mi:X2}\6&1A2B3C4D&0&000{mi}", vid, pid, mi, service,
                new[] { $@"USB\VID_{vid:X4}&PID_{pid:X4}&REV_0100&MI_{mi:X2}", $@"USB\VID_{vid:X4}&PID_{pid:X4}&MI_{mi:X2}" },
                compatible);

        private static string Plan(Node node) => VendorUsbDriverInstaller.Plan(node)?.BindId;

        [Theory]
        [InlineData(@"USB\VID_057E&PID_0337\5&2E1A&0&4", 0x057E, 0x0337, -1)]
        [InlineData(@"USB\VID_8086&PID_C013&MI_00\7&1A2B3C&0&0000", 0x8086, 0xC013, 0)]
        [InlineData(@"usb\vid_1603&pid_0002&mi_01\7&abc&0&0001", 0x1603, 0x0002, 1)]
        public void AUsbInstanceId_GivesItsVidPidAndInterface(string id, int vid, int pid, int mi)
        {
            Assert.True(VendorUsbDriverInstaller.TryParseUsbInstanceId(id, out ushort v, out ushort p, out int m));
            Assert.Equal(vid, v);
            Assert.Equal(pid, p);
            Assert.Equal(mi, m);
        }

        [Theory]
        [InlineData(@"USB\ROOT_HUB30\4&1234&0&0")]
        [InlineData(@"HID\VID_057E&PID_0337\7&1&0&0000")]
        [InlineData(@"USB\VID_ZZZZ&PID_0337\5&1")]
        [InlineData(@"USB\VID_046D&PID_C54D&LAMPARRAY\8&340F50E3&0&336534863234_SLOT00")]
        [InlineData(@"USB\VID_057E&PID_0337&MI_ZZ\5&1")]
        [InlineData("")]
        [InlineData(null)]
        public void ANodeWithoutAUsbVidAndPid_IsNoTarget(string id)
            => Assert.False(VendorUsbDriverInstaller.TryParseUsbInstanceId(id, out _, out _, out _));

        [Fact]
        public void TheInstanceSegment_NeverSuppliesAnInterface()
        {
            // "MI_" in the instance part after the second backslash is not
            // the device's interface number.
            Assert.True(VendorUsbDriverInstaller.TryParseUsbInstanceId(
                @"USB\VID_14B7&PID_0982\MI_05&1&0", out _, out _, out int mi));
            Assert.Equal(-1, mi);
        }

        [Theory]
        [InlineData("")]
        [InlineData("HidUsb")]
        [InlineData("hidusb")]
        public void ADeviceTarget_IsBoundFromNoDriverOrHidUsb(string service)
            => Assert.Equal(@"USB\VID_057E&PID_0337", Plan(DeviceNode(0x057E, 0x0337, service)));

        [Theory]
        [InlineData("usbccgp")]   // a composite parent: never replaced
        [InlineData("WINUSB")]    // bound already, by PadForge or by Zadig
        [InlineData("WUDFRd")]    // a user-mode vendor driver
        [InlineData("Jungo")]     // Tacx's driver
        [InlineData("xusb22")]
        public void ADeviceOnAnyOtherDriver_IsLeftAlone(string service)
        {
            Assert.Null(Plan(DeviceNode(0x057E, 0x0337, service)));
            Assert.Null(Plan(DeviceNode(0x3561, 0x1904, service)));
        }

        [Fact]
        public void AnInterfaceTarget_BindsOnlyItsInterface()
        {
            Assert.Equal(@"USB\VID_2CA3&PID_1023&MI_01", Plan(InterfaceNode(0x2CA3, 0x1023, 1, "")));
            Assert.Null(Plan(InterfaceNode(0x2CA3, 0x1023, 0, "")));
            Assert.Null(Plan(DeviceNode(0x2CA3, 0x1023, "")));

            // The Ergodex keyboard on interface 0 stays with Windows.
            Assert.Equal(@"USB\VID_1603&PID_0002&MI_01", Plan(InterfaceNode(0x1603, 0x0002, 1, "")));
            Assert.Null(Plan(InterfaceNode(0x1603, 0x0002, 0, "HidUsb")));
        }

        [Fact]
        public void TheBigButtonReceiver_IsTheDeviceOrItsProtocol4Interface()
        {
            Assert.Equal(@"USB\VID_045E&PID_02A0", Plan(DeviceNode(0x045E, 0x02A0, "")));
            Assert.Equal(@"USB\VID_045E&PID_02A0&MI_00", Plan(InterfaceNode(0x045E, 0x02A0, 0, "",
                @"USB\Class_FF&SubClass_5D&Prot_04", @"USB\Class_FF&SubClass_5D", @"USB\Class_FF")));
            // Protocol 1 is a wired pad's layout, which SDL does not read as
            // a receiver.
            Assert.Null(Plan(InterfaceNode(0x045E, 0x02A0, 0, "",
                @"USB\Class_FF&SubClass_5D&Prot_01", @"USB\Class_FF&SubClass_5D", @"USB\Class_FF")));
        }

        [Fact]
        public void TheKonamiBoards_TakeTheirComposites()
        {
            Assert.Equal(@"USB\VID_1CCF&PID_8008&MI_02", Plan(InterfaceNode(0x1CCF, 0x8008, 2, "")));
            Assert.Equal(@"USB\VID_1CCF&PID_8010&MI_00", Plan(InterfaceNode(0x1CCF, 0x8010, 0, "")));
            Assert.Null(Plan(InterfaceNode(0x1CCF, 0x8010, 1, "")));
        }

        [Fact]
        public void AnXidInterface_IsBoundByItsClassFromNoDriverOnly()
        {
            string[] xid = { @"USB\Class_58&SubClass_42&Prot_00", @"USB\Class_58&SubClass_42", @"USB\Class_58" };
            // Any IDs at all: the class decides.
            Assert.Equal(VendorUsbDriverInstaller.XidCompatibleId, Plan(DeviceNode(0x1234, 0x5678, "", xid)));
            Assert.Null(Plan(DeviceNode(0x1234, 0x5678, "HidUsb", xid)));
            Assert.Null(Plan(DeviceNode(0x1234, 0x5678, "WINUSB", xid)));

            // The ID table covers a pad whose compatible IDs Windows did not
            // build from the class, from the no-driver state only, since no
            // Windows driver serves XID.
            Assert.Equal(@"USB\VID_045E&PID_0202", Plan(DeviceNode(0x045E, 0x0202, "")));
            Assert.Equal(@"USB\VID_0A7B&PID_D000", Plan(DeviceNode(0x0A7B, 0xD000, "")));
            Assert.Null(Plan(DeviceNode(0x045E, 0x0202, "HidUsb")));
        }

        [Fact]
        public void TheDevicesWhoseBindingTakesSomethingAway_AreNeverBoundOnSight()
        {
            // The Intel base station's interface 0 is also its keyboard.
            Assert.Null(Plan(InterfaceNode(0x8086, 0xC013, 0, "HidUsb")));
            Assert.Null(Plan(DeviceNode(0x8086, 0xC013, "")));
            // The Prodikeys' interface 1 carries its media and sleep keys.
            Assert.Null(Plan(InterfaceNode(0x041E, 0x2801, 1, "HidUsb")));
            // Xbox 360 pads and receivers leave XInput only on request (#33 Part 15).
            Assert.Null(Plan(DeviceNode(0x045E, 0x028E, "xusb22")));
            Assert.Null(Plan(DeviceNode(0x045E, 0x0719, "")));
            // SDL does not read the GunCon 3, and a binding takes it from the
            // tools that do (#33 Part 9).
            Assert.Null(Plan(DeviceNode(0x0B9A, 0x0800, "")));
        }

        [Fact]
        public void TheTable_MatchesTheForkRows()
        {
            var t = VendorUsbDriverInstaller.Targets;
            Assert.All(t, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
            Assert.Equal(t.Length, t.Distinct().Count());
            // 14 I-Force IDs (README-iforce.md), 5 train controllers
            // (README-train.md).
            ushort[] iforceVendors = { 0x044F, 0x046D, 0x05EF, 0x061C, 0x06A3, 0x06F8 };
            Assert.Equal(14, t.Count(r => iforceVendors.Contains(r.Vid) && r.Match == VendorUsbDriverInstaller.Match.Device));
            Assert.Equal(5, t.Count(r => r.Vid == 0x0AE4 || r.Vid == 0x1C06));
            // 63 XID IDs from the fork's table plus the Steel Battalion.
            Assert.Equal(64, VendorUsbDriverInstaller.XidIdentities.Length);
            Assert.Equal(64, VendorUsbDriverInstaller.XidIdentities.Distinct().Count());
        }

        [Fact]
        public void ThePackage_NamesOneIdOnBothArchitectures()
        {
            string inf = VendorUsbDriverInstaller.BuildInf(@"USB\VID_2CA3&PID_1023&MI_01", "DJI RC");
            Assert.Contains("[Standard.NTamd64]\r\n%DeviceName% = USB_Install, USB\\VID_2CA3&PID_1023&MI_01\r\n", inf);
            Assert.Contains("[Standard.NTarm64]\r\n%DeviceName% = USB_Install, USB\\VID_2CA3&PID_1023&MI_01\r\n", inf);
            Assert.Contains("CatalogFile = " + VendorUsbDriverInstaller.CatalogName, inf);
            Assert.Contains("{FD826C66-3556-4845-9436-0FE7580DED02}", inf);
            Assert.Contains("Include = winusb.inf", inf);
            Assert.Contains("DeviceName   = \"DJI RC (WinUSB)\"", inf);
            Assert.All(inf, c => Assert.True(c < 0x80, "non-ASCII character in the INF"));
            Assert.DoesNotContain("\n", inf.Replace("\r\n", ""));
            // A name can never break out of its string.
            Assert.Contains("DeviceName   = \"A B (WinUSB)\"",
                VendorUsbDriverInstaller.BuildInf(@"USB\VID_0001&PID_0002", "A \"%B"));
        }

        /// <summary>The check Windows' catalog tool makes of the package, with
        /// the /os value the signing step passes on each architecture, and no
        /// certificate and no install.</summary>
        [Fact]
        public void Inf2Cat_AcceptsThePackageOnBothArchitectures()
            => Inf2Cat.AssertCatalogs(VendorUsbDriverInstaller.InfName,
                VendorUsbDriverInstaller.BuildInf(@"USB\VID_8086&PID_C013&MI_00", "Test interface"),
                VendorUsbDriverInstaller.CatalogName, _output);

        // ── xusb22 for the Xbox 360 devices xusb22.inf does not name ──

        [Theory]
        [InlineData(0x045E, 0x0291)]
        [InlineData(0x045E, 0x02A9)]
        [InlineData(0x05C6, 0x9244)]
        [InlineData(0x1430, 0x070B)]
        public void ACloneReceiverOrTheGhlDongle_TakesXusb22FromNoDriver(int vid, int pid)
        {
            var node = DeviceNode((ushort)vid, (ushort)pid, "");
            var plan = VendorUsbDriverInstaller.Plan(node);
            Assert.NotNull(plan);
            Assert.Equal(VendorUsbDriverInstaller.BindDriver.Xusb22, plan.Value.Driver);
            Assert.Equal($@"USB\VID_{vid:X4}&PID_{pid:X4}", plan.Value.BindId);

            // Any driver at all keeps it where it is, xusb22 itself included,
            // and an interface child is never the target.
            foreach (string service in new[] { "xusb22", "WINUSB", "HidUsb", "usbccgp", "xusb21" })
                Assert.Null(VendorUsbDriverInstaller.Plan(DeviceNode((ushort)vid, (ushort)pid, service)));
            Assert.Null(VendorUsbDriverInstaller.Plan(InterfaceNode((ushort)vid, (ushort)pid, 0, "")));
        }

        /// <summary>Microsoft's own receiver and pad stay on the xusb22.inf
        /// Windows gives them. Only the opt-in moves those.</summary>
        [Fact]
        public void MicrosoftsOwnReceiverAndPad_AreNeverXusb22Targets()
        {
            Assert.DoesNotContain(VendorUsbDriverInstaller.Xusb22Targets, t => t.Vid == 0x045E && (t.Pid == 0x0719 || t.Pid == 0x028E));
            Assert.Null(VendorUsbDriverInstaller.Plan(DeviceNode(0x045E, 0x0719, "")));
        }

        [Fact]
        public void TheXusb22Package_BorrowsXusb22InfsSections()
        {
            string inf = VendorUsbDriverInstaller.BuildXusbInf(@"USB\VID_1430&PID_070B", "Guitar Hero Live Xbox 360 dongle");
            Assert.Contains("Class       = XnaComposite\r\nClassGUID   = {D61CA365-5AF4-4486-998B-9DB4734C6CA3}", inf);
            Assert.Contains("[Standard.NTamd64]\r\n%DeviceName% = Xusb_Install, USB\\VID_1430&PID_070B\r\n", inf);
            Assert.Contains("[Standard.NTarm64]\r\n%DeviceName% = Xusb_Install, USB\\VID_1430&PID_070B\r\n", inf);
            Assert.Contains("[Xusb_Install]\r\nInclude = xusb22.inf\r\nNeeds   = CC_Install\r\n", inf);
            Assert.Contains("[Xusb_Install.Services]\r\nInclude = xusb22.inf\r\nNeeds   = CC_Install.Services\r\n", inf);
            Assert.Contains("CatalogFile = " + VendorUsbDriverInstaller.XusbCatalogName, inf);
            Assert.DoesNotContain("SourceDisksFiles", inf);   // no file of Microsoft's is carried
            Assert.All(inf, c => Assert.True(c < 0x80, "non-ASCII character in the INF"));
            Assert.DoesNotContain("\n", inf.Replace("\r\n", ""));
            Assert.Contains("DeviceName   = \"A B\"", VendorUsbDriverInstaller.BuildXusbInf(@"USB\VID_0001&PID_0002", "A \"%B"));
        }

        [Fact]
        public void Inf2Cat_AcceptsTheXusb22PackageOnBothArchitectures()
            => Inf2Cat.AssertCatalogs(VendorUsbDriverInstaller.XusbInfName,
                VendorUsbDriverInstaller.BuildXusbInf(@"USB\VID_045E&PID_02A9", "Xbox 360 Wireless Receiver for Windows"),
                VendorUsbDriverInstaller.XusbCatalogName, _output);

        /// <summary>This PC's xusb22.inf still installs through the two sections
        /// the package names, and names none of the four IDs, which is why
        /// they need the package.</summary>
        [Fact]
        public void ThisPcsXusb22Inf_HasTheSectionsAndLacksTheIds()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "xusb22.inf");
            Assert.True(File.Exists(path), path);
            string text = File.ReadAllText(path);   // UTF-16 with its byte order mark
            _output.WriteLine(text.Split('\n').FirstOrDefault(l => l.StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains("[CC_Install]", text);
            Assert.Contains("[CC_Install.Services]", text);
            Assert.Contains("Class=XnaComposite", text);
            foreach (var t in VendorUsbDriverInstaller.Xusb22Targets)
                Assert.DoesNotContain($"Vid_{t.Vid:X4}&Pid_{t.Pid:X4}", text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The sweep on this machine: every present USB node, read
        /// only. Whatever it finds is written out, and a node it would bind is
        /// named, so a bench run shows the verdicts.</summary>
        [Fact]
        public void TheSweep_ReadsThePresentUsbNodes()
        {
            var nodes = VendorUsbDriverInstaller.ListPresentUsbNodes();
            _output.WriteLine($"{nodes.Count} USB nodes");
            foreach (var n in nodes)
            {
                Assert.StartsWith(@"USB\", n.InstanceId, StringComparison.OrdinalIgnoreCase);
                Assert.NotNull(n.Service);
                Assert.NotNull(n.HardwareIds);
                Assert.NotNull(n.CompatibleIds);
                var plan = VendorUsbDriverInstaller.Plan(n);
                _output.WriteLine($"{n.InstanceId} on {(n.Service.Length == 0 ? "(no driver)" : n.Service)}"
                    + (plan is { } p ? $" -> bind {p.BindId} ({p.Name})" : ""));
            }
        }

        // ── Opt-in bindings (#33 Part 15's Decision) ──

        private static Node Pad360(string revision, string service)
            => new($@"USB\VID_045E&PID_028E\6&1&0&{revision}", 0x045E, 0x028E, -1, service,
                new[] { $@"USB\VID_045E&PID_028E&REV_{revision}", @"USB\VID_045E&PID_028E" },
                new[] { @"USB\MS_COMP_XUSB10", @"USB\Class_FF&SubClass_5D&Prot_01" });

        [Fact]
        public void AWiredXbox360Pad_IsOfferedFromXusb22_ForTheRevisionsWithAChatpad()
        {
            var offer = VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0114", "xusb22") }, _ => false);
            Assert.NotNull(offer);
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.Xbox360Pad, offer.Kind);
            Assert.Equal(@"USB\VID_045E&PID_028E&REV_0114", offer.Bind?.BindId);
            Assert.Equal(new[] { "xusb22" }, offer.From);
            Assert.False(offer.Restore);

            Assert.Equal(@"USB\VID_045E&PID_028E&REV_0110",
                VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0110", "xusb22") }, _ => false)?.Bind?.BindId);
            // Another revision, or another driver, gets nothing.
            Assert.Null(VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0105", "xusb22") }, _ => false));
            Assert.Null(VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0114", "HidUsb") }, _ => false));
        }

        [Fact]
        public void APadOnPadForgesWinUsb_IsOfferedTheRestore_AndOneOnAnotherWinUsbIsNot()
        {
            var ours = VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0114", "WINUSB") }, _ => true);
            Assert.NotNull(ours);
            Assert.True(ours.Restore);
            Assert.Null(ours.Bind);
            Assert.Null(VendorUsbDriverInstaller.QueryOptIn(0x045E, 0x028E, new[] { Pad360("0114", "WINUSB") }, _ => false));
        }

        [Fact]
        public void AWirelessPad_OffersItsReceiver()
        {
            var receiver = new Node(@"USB\VID_045E&PID_0719\5&1&0&2", 0x045E, 0x0719, -1, "xusb22",
                new[] { @"USB\VID_045E&PID_0719&REV_0100", @"USB\VID_045E&PID_0719" }, Array.Empty<string>());
            foreach (ushort rowPid in new ushort[] { 0x02A1, 0x0719 })
            {
                var offer = VendorUsbDriverInstaller.QueryOptIn(0x045E, rowPid, new[] { receiver }, _ => false);
                Assert.Equal(VendorUsbDriverInstaller.OptInKind.Xbox360Receiver, offer?.Kind);
                Assert.Equal(@"USB\VID_045E&PID_0719&REV_0100", offer?.Bind?.BindId);
            }
        }

        [Fact]
        public void TheIntelBaseStation_IsOfferedByTheNodeThatCarriesInterface0()
        {
            var parent = DeviceNode(0x8086, 0xC013, "usbccgp");
            var keyboard = InterfaceNode(0x8086, 0xC013, 0, "HidUsb");
            var mouse = InterfaceNode(0x8086, 0xC013, 1, "HidUsb");
            Assert.Equal(@"USB\VID_8086&PID_C013&MI_00",
                VendorUsbDriverInstaller.QueryOptIn(0x8086, 0xC013, new[] { parent, mouse, keyboard }, _ => false)?.Bind?.BindId);
            // Listed as one device, the device itself.
            Assert.Equal(@"USB\VID_8086&PID_C013",
                VendorUsbDriverInstaller.QueryOptIn(0x8086, 0xC013, new[] { DeviceNode(0x8086, 0xC013, "HidUsb") }, _ => false)?.Bind?.BindId);
        }

        [Fact]
        public void TheProdikeys_IsOfferedByItsMusicKeysOnly()
        {
            Assert.Equal(@"USB\VID_041E&PID_2801&MI_01", VendorUsbDriverInstaller.QueryOptIn(0x041E, 0x2801,
                new[] { InterfaceNode(0x041E, 0x2801, 0, "HidUsb"), InterfaceNode(0x041E, 0x2801, 1, "HidUsb") }, _ => false)?.Bind?.BindId);
            Assert.Null(VendorUsbDriverInstaller.QueryOptIn(0x041E, 0x2801,
                new[] { InterfaceNode(0x041E, 0x2801, 0, "HidUsb") }, _ => false));
        }

        [Fact]
        public void OnlyTheOptInDevices_CarryAnOffer()
        {
            Assert.True(VendorUsbDriverInstaller.IsOptInRow(0x045E, 0x028E));
            Assert.True(VendorUsbDriverInstaller.IsOptInRow(0x045E, 0x02A1));
            Assert.True(VendorUsbDriverInstaller.IsOptInRow(0x8086, 0xC013));
            Assert.True(VendorUsbDriverInstaller.IsOptInRow(0x041E, 0x2801));
            Assert.False(VendorUsbDriverInstaller.IsOptInRow(0x045E, 0x0B12));
            Assert.False(VendorUsbDriverInstaller.IsOptInRow(0x057E, 0x0337));
            Assert.Equal(VendorUsbDriverInstaller.OptInKind.Prodikeys, VendorUsbDriverInstaller.KindOfId(@"USB\VID_041E&PID_2801&MI_01"));
            Assert.Null(VendorUsbDriverInstaller.KindOfId(@"USB\VID_057E&PID_0337"));
        }

        [Fact]
        public void TheStartCheck_NamesTheOptInsWindowsPutBack()
        {
            string[] optedIn = { @"USB\VID_045E&PID_028E&REV_0114", @"USB\VID_041E&PID_2801&MI_01" };
            var moved = VendorUsbDriverInstaller.MovedBack(optedIn, new[]
            {
                Pad360("0114", "xusb22"),                              // put back
                InterfaceNode(0x041E, 0x2801, 1, "WINUSB"),            // still PadForge's
            });
            Assert.Equal(new[] { @"USB\VID_045E&PID_028E&REV_0114" }, moved);
            Assert.Empty(VendorUsbDriverInstaller.MovedBack(optedIn, Array.Empty<Node>()));   // unplugged
            Assert.Empty(VendorUsbDriverInstaller.MovedBack(null, new[] { Pad360("0114", "xusb22") }));
        }

        [Fact]
        public void Inf2Cat_AcceptsARevisionPackage()
            => Inf2Cat.AssertCatalogs(VendorUsbDriverInstaller.InfName,
                VendorUsbDriverInstaller.BuildInf(@"USB\VID_045E&PID_028E&REV_0114",
                    VendorUsbDriverInstaller.NameOf(VendorUsbDriverInstaller.OptInKind.Xbox360Pad)),
                VendorUsbDriverInstaller.CatalogName, _output);

        /// <summary>The bind's proof reads interfaces with PadForge's GUID.
        /// A node that has none, here one that does not exist, is never
        /// counted as bound.</summary>
        [Fact]
        public void ANodeWithoutPadForgesInterface_IsNotBound()
            => Assert.False(VendorUsbDriverInstaller.HasActiveInterface(@"USB\VID_0000&PID_0000\0&0&0"));
    }
}
