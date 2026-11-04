using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// OpenRGB's detector registrations for the families #557 ports, read by
    /// tools/gen_openrgb_registrations.py, and OpenRGB's rules for which of
    /// them a HID collection answers to. The main test holds every row to
    /// what OpenRGB's own detector objects registered when its families'
    /// *Detect.cpp files were compiled (tools/openrgb_registrations). The
    /// samples and the rule tests are written from the source lines and
    /// DetectionManager.cpp at the pinned commit.
    /// </summary>
    public class OpenRgbRegistrationsTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static OpenRgbRegistration Row(string source, string name = null)
            => OpenRgbRegistrations.All
                .Single(r => r.Source == source && (name == null || r.Name == name));

        private static OpenRgbRegistration Hid(int vid, int pid, int iface = -1, int page = -1, int usage = -1,
                                               int bus = -1, string macro = "REGISTER_HID_DETECTOR", bool enabled = true,
                                               string family = "Test")
            => new(family, "T", "Detect", macro, OpenRgbTransport.Hid, vid, pid, iface, page, usage, bus, enabled, false, "t:1");

        private static HidCollectionInfo Collection(ushort vid, ushort pid, int iface = -1, ushort page = 0xFF00, ushort usage = 1)
            => new() { VendorId = vid, ProductId = pid, InterfaceNumber = iface, UsagePage = page, Usage = usage };

        // ── Against OpenRGB's own detector objects ──

        /// <summary>Per family, the rows that are not read from the settings
        /// are, field for field, the registrations OpenRGB's detector
        /// objects made when the family's own files were built and run
        /// (registrations_dump.tsv).</summary>
        [Fact]
        public void EveryRegistration_IsWhatOpenRgbsDetectorObjectsRegister()
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "openrgb_registrations", "registrations_dump.tsv"),
                                          new UTF8Encoding(false));
            Assert.Equal("openrgb\t" + OpenRgbRegistrations.OpenRgbCommit, lines[0]);
            var dumped = lines.Skip(1).Select(l => l.Split('\t')).ToList();

            static string Kind(OpenRgbRegistration r) => r.Macro switch
            {
                "REGISTER_DETECTOR" => "Device",
                "REGISTER_DYNAMIC_DETECTOR" => "Dynamic",
                "REGISTER_USB_SERIAL_DETECTOR" => "UsbSerial",
                _ => r.IsWrapped ? "HidWrapped" : "Hid",
            };
            static string Key(OpenRgbRegistration r) => string.Join("|", r.Family, Kind(r), r.Name, r.VendorId, r.ProductId,
                                                                    r.Interface, r.UsagePage, r.Usage, r.Bus, r.EnabledByDefault ? 1 : 0);

            var families = OpenRgbRegistrations.All.Select(r => r.Family).Distinct().ToList();
            // The 54 families of the issue's table and the 43 peripheral families.
            Assert.Equal(97, families.Count);
            foreach (var family in families)
            {
                var mine = OpenRgbRegistrations.Of(family).Where(r => !r.FromSettings).Select(Key).OrderBy(x => x, StringComparer.Ordinal);
                var theirs = dumped.Where(p => p[0] == family).Select(p => string.Join("|", p)).OrderBy(x => x, StringComparer.Ordinal);
                Assert.Equal(theirs, mine);
            }
        }

        /// <summary>
        /// The count per family is the inventory's (C:/tmp/pf555/rgb538/
        /// openrgb_families.json, the issue's coverage table) where the
        /// Windows build compiles what the inventory counted. Where it does
        /// not: the ASUS TUF laptop's second registration is in
        /// AsusTUFLaptopDetect_Linux.cpp; Cooler Master's MM531 line is
        /// commented out (CoolerMasterControllerDetect.cpp:422); seven HyperX
        /// lines are the #else branches of #ifdef _WIN32 blocks
        /// (HyperXAlloyOriginsControllerDetect.cpp:45-51 and its siblings);
        /// two Sinowealth lines are commented out
        /// (SinowealthControllerDetect.cpp:458-459); and QMK's two
        /// registrations inside functions name their device with a variable,
        /// which the inventory did not count (QMKOpenRGBControllerDetect.cpp:172,
        /// QMKVialRGBControllerDetect.cpp:85). Wooting's 11
        /// REGISTER_WOOTING_DETECTOR lines make three detectors each
        /// (WootingKeyboardControllerDetect.cpp:182-185).
        /// </summary>
        [Fact]
        public void EachFamily_HasTheInventorysRegistrations()
        {
            var inventory = new Dictionary<string, int>
            {
                ["A4TechController"] = 6, ["AOCKeyboardController"] = 2, ["AcerNitroHidKeyboardController"] = 1,
                ["AlienwareController"] = 2, ["AlienwareKeyboardController"] = 2, ["AnnePro2Controller"] = 5,
                ["AsusAuraCoreController"] = 5, ["AsusAuraUSBController"] = 101, ["AsusLegacyUSBController"] = 3,
                ["AsusTUFLaptopController"] = 2, ["CherryKeyboardController"] = 25, ["ClevoKeyboardController"] = 2,
                ["CoolerMasterController"] = 38, ["CorsairPeripheralController"] = 39, ["CorsairPeripheralV2Controller"] = 38,
                ["CougarController"] = 2, ["DarkProject"] = 1, ["DasKeyboardController"] = 3, ["DuckyKeyboardController"] = 2,
                ["DygmaRaiseController"] = 1, ["EVGAUSBController"] = 7, ["EVisionKeyboardController"] = 15,
                ["FnaticStreakController"] = 3, ["GaiZongGaiKeyboardController"] = 9, ["GigabyteAorusLaptopController"] = 4,
                ["HPOmenLaptopController"] = 1, ["HYTEKeyboardController"] = 1, ["HyperXKeyboardController"] = 20,
                ["IonicoController"] = 2, ["KeychronKeyboardController"] = 1, ["LenovoControllers"] = 24,
                ["LightSaltController"] = 1, ["MNTKeyboardController"] = 2, ["MSI3ZoneController"] = 1,
                ["MSIKeyboardController"] = 4, ["MSILaptopController"] = 4, ["MSIVigorController"] = 1,
                ["MintakaKeyboardController"] = 1, ["MountainKeyboardController"] = 3, ["OKSController"] = 1,
                ["QMKController"] = 5, ["RedSquareKeyroxController"] = 2, ["RoccatController"] = 28,
                ["RoyuanKeyboardController"] = 5, ["SayoDeviceController"] = 1, ["SinowealthController"] = 13,
                ["SkyloongController"] = 1, ["ThermaltakePoseidonZRGBController"] = 1, ["ValkyrieKeyboardController"] = 2,
                ["WinbondGamingKeyboardController"] = 1, ["WitmodKeyboardController"] = 1, ["WootingKeyboardController"] = 13,
                ["XPGSummonerKeyboardController"] = 1, ["ZETKeyboardController"] = 1,
            };
            var windowsBuild = new Dictionary<string, int>
            {
                ["AsusTUFLaptopController"] = 1, ["CoolerMasterController"] = 37, ["HyperXKeyboardController"] = 13,
                ["SinowealthController"] = 11, ["QMKController"] = 7,
            };
            Assert.Equal(54, inventory.Count);
            foreach (var (family, count) in inventory)
            {
                var rows = OpenRgbRegistrations.Of(family).ToList();
                int lines = rows.Select(r => r.Source).Distinct().Count();
                Assert.True(windowsBuild.GetValueOrDefault(family, count) == lines, $"{family}: {lines} lines");
                Assert.Equal(family == "WootingKeyboardController" ? 2 + 3 * 11 : lines, rows.Count);
            }
            Assert.Equal(473, OpenRgbRegistrations.All.Count(r => inventory.ContainsKey(r.Family)));
            Assert.Equal(568, OpenRgbRegistrations.All.Length);
            Assert.DoesNotContain(OpenRgbRegistrations.All, r => r.Family is "HIDLampArrayController" or "LogitechController"
                                                               or "RazerController" or "SteelSeriesController");
        }

        // ── Each macro form, against its source line ──

        /// <summary>REGISTER_HID_DETECTOR leaves interface, usage page and
        /// usage "any" (DetectionManager.h:540): the Dell G Series at
        /// 187C:0550 (AlienwareControllerDetect.cpp:17, 22 and 43).</summary>
        [Fact]
        public void APlainHidRegistration_LeavesTheRestAny()
        {
            Assert.Equal(new OpenRgbRegistration("AlienwareController", "Dell G Series LED Controller", "DetectAlienwareControllers",
                "REGISTER_HID_DETECTOR", OpenRgbTransport.Hid, 0x187C, 0x0550, -1, -1, -1, -1, true, false,
                "AlienwareControllerDetect.cpp:43"), Row("AlienwareControllerDetect.cpp:43"));
        }

        /// <summary>The _I, _IP, _IPU and _P forms (DetectionManager.h:542,
        /// 544, 546 and 548): the Anne Pro 2 on interface 1
        /// (AnnePro2ControllerDetect.cpp:20, 26 and 50), the TUF Gaming K1 on
        /// interface 2 and page FF00 (AsusAuraKeyboardControllerDetect.cpp:18,
        /// 46 and 97), the Bloody B820R on interface 2, page FF52, usage 0210
        /// (BloodyB820RControllerDetect.cpp:20 and 40, BloodyB820RController.h:32),
        /// the MM800 on page FFC2 (CorsairPeripheralControllerDetect.cpp:30, 90
        /// and 224).</summary>
        [Fact]
        public void TheInterfacePageAndUsageForms_TakeTheirOwnFields()
        {
            var anne = Row("AnnePro2ControllerDetect.cpp:50");
            Assert.Equal(("REGISTER_HID_DETECTOR_I", 0x04D9, 0x8008, 1, -1, -1), (anne.Macro, anne.VendorId, anne.ProductId, anne.Interface, anne.UsagePage, anne.Usage));
            var k1 = Row("AsusAuraKeyboardControllerDetect.cpp:97");
            Assert.Equal(("REGISTER_HID_DETECTOR_IP", "ASUS TUF Gaming K1", 0x0B05, 0x1945, 2, 0xFF00, -1), (k1.Macro, k1.Name, k1.VendorId, k1.ProductId, k1.Interface, k1.UsagePage, k1.Usage));
            var bloody = Row("BloodyB820RControllerDetect.cpp:40");
            Assert.Equal(("REGISTER_HID_DETECTOR_IPU", "DetectBloodyB820R", 0x09DA, 0xFA10, 2, 0xFF52, 0x0210), (bloody.Macro, bloody.Detector, bloody.VendorId, bloody.ProductId, bloody.Interface, bloody.UsagePage, bloody.Usage));
            var mm800 = Row("CorsairPeripheralControllerDetect.cpp:224");
            Assert.Equal(("REGISTER_HID_DETECTOR_P", 0x1B1C, 0x1B3B, -1, 0xFFC2, -1), (mm800.Macro, mm800.VendorId, mm800.ProductId, mm800.Interface, mm800.UsagePage, mm800.Usage));
        }

        /// <summary>The _PU form with names each file defines its own way: the
        /// Legion 7 Gen 10, 048D:C197, on page FF89 usage 07 from the enum of
        /// LenovoUSBControllerDetect.cpp:21 and 26-30 (line 79,
        /// LenovoDevices.h:40); the Legion 5 2023, C985, on usage CC from the
        /// enum of Lenovo4ZoneUSBControllerDetect.cpp:24-28 (line 49,
        /// LenovoDevices4Zone.h:21); the M300, 17EF:60E4, on page FF01 usage 01
        /// from the #define lines of LenovoM300ControllerDetect.cpp:19-22
        /// (line 42).</summary>
        [Fact]
        public void EachFile_ReadsItsOwnNames()
        {
            var gen10 = Row("LenovoUSBControllerDetect.cpp:79");
            Assert.Equal(("REGISTER_HID_DETECTOR_PU", "Lenovo Legion 7 Gen 10", 0x048D, 0xC197, -1, 0xFF89, 0x07),
                         (gen10.Macro, gen10.Name, gen10.VendorId, gen10.ProductId, gen10.Interface, gen10.UsagePage, gen10.Usage));
            var fourZone = Row("Lenovo4ZoneUSBControllerDetect.cpp:49");
            Assert.Equal((0x048D, 0xC985, 0xFF89, 0xCC), (fourZone.VendorId, fourZone.ProductId, fourZone.UsagePage, fourZone.Usage));
            var m300 = Row("LenovoM300ControllerDetect.cpp:42");
            Assert.Equal((0x17EF, 0x60E4, 0xFF01, 0x01), (m300.VendorId, m300.ProductId, m300.UsagePage, m300.Usage));
        }

        /// <summary>Of an #ifdef _WIN32 block only the Windows branch counts:
        /// the Alloy Origins on interface 3, not 0
        /// (HyperXAlloyOriginsControllerDetect.cpp:18-19 and 45-51).</summary>
        [Fact]
        public void AWindowsBlock_KeepsItsWindowsBranch()
        {
            var origins = OpenRgbRegistrations.Of("HyperXKeyboardController").Where(r => r.Name == "HyperX Alloy Origins").ToList();
            Assert.Single(origins);
            Assert.Equal(("HyperXAlloyOriginsControllerDetect.cpp:46", 0x0951, 0x16E5, 3), (origins[0].Source, origins[0].VendorId, origins[0].ProductId, origins[0].Interface));
        }

        /// <summary>REGISTER_WOOTING_DETECTOR makes three detectors: the base
        /// product ID with 2, 0 and 1 set in it
        /// (WootingKeyboardControllerDetect.cpp:52-54 and 182-185), so the
        /// Wooting One, 31E3:1100 (lines 27, 34 and 196), answers on 1102,
        /// 1100 and 1101.</summary>
        [Fact]
        public void AWootingLine_MakesThreeDetectors()
        {
            var one = OpenRgbRegistrations.Of("WootingKeyboardController").Where(r => r.Name == "Wooting One").ToList();
            Assert.Equal(new[] { 0x1102, 0x1100, 0x1101 }, one.Select(r => r.ProductId));
            Assert.All(one, r => Assert.Equal(("WootingKeyboardControllerDetect.cpp:196", 0x31E3, -1, -1, -1, "DetectWootingControllers"),
                                              (r.Source, r.VendorId, r.Interface, r.UsagePage, r.Usage, r.Detector)));
        }

        /// <summary>A plain REGISTER_DETECTOR has no IDs
        /// (DetectionManager.h:531), and its transport is what its function
        /// opens: Dygma's a serial port (DygmaRaiseControllerDetect.cpp:22-40),
        /// the TUF laptop's and the Omen's WMI
        /// (AsusTUFLaptopDetect_Windows.cpp:13-49,
        /// HPOmenLaptopWMIDetect_Windows.cpp:14-34).</summary>
        [Fact]
        public void APlainDetector_TakesTheTransportItsFunctionOpens()
        {
            Assert.Equal(new OpenRgbRegistration("DygmaRaiseController", "Dygma Raise", "DetectDygmaRaiseControllers",
                "REGISTER_DETECTOR", OpenRgbTransport.UsbSerial, -1, -1, -1, -1, -1, -1, true, false,
                "DygmaRaiseControllerDetect.cpp:42"), Row("DygmaRaiseControllerDetect.cpp:42"));
            Assert.Equal(OpenRgbTransport.Wmi, Row("AsusTUFLaptopDetect_Windows.cpp:49").Transport);
            Assert.Equal(OpenRgbTransport.Wmi, Row("HPOmenLaptopWMIDetect_Windows.cpp:34").Transport);
        }

        /// <summary>QMK's dynamic detector registers others from the user's
        /// settings, so its own row has no transport of a device, and the
        /// rows it makes carry the interface, page and usage of the source
        /// with the name and IDs from the settings
        /// (QMKOpenRGBControllerDetect.cpp:39-40, 147-178). Keychron's
        /// _PU line takes any product (QMKKeychronController.h:23-29,
        /// QMKKeychronControllerDetect.cpp:55).</summary>
        [Fact]
        public void QmksRows_KeepWhatTheSourceFixes()
        {
            var dynamic = Row("QMKOpenRGBControllerDetect.cpp:178");
            Assert.Equal(("REGISTER_DYNAMIC_DETECTOR", "QMK OpenRGB Devices", "RegisterQMKDetectors", OpenRgbTransport.Other),
                         (dynamic.Macro, dynamic.Name, dynamic.Detector, dynamic.Transport));
            var fromSettings = Row("QMKOpenRGBControllerDetect.cpp:172");
            Assert.Equal(("REGISTER_DYNAMIC_HID_DETECTOR_IPU", "", -1, -1, 1, 0xFF60, 0x61, true),
                         (fromSettings.Macro, fromSettings.Name, fromSettings.VendorId, fromSettings.ProductId, fromSettings.Interface,
                          fromSettings.UsagePage, fromSettings.Usage, fromSettings.FromSettings));
            var keychron = Row("QMKKeychronControllerDetect.cpp:55");
            Assert.Equal((0x3434, -1, -1, 0xFF60, 0x61, false), (keychron.VendorId, keychron.ProductId, keychron.Interface, keychron.UsagePage, keychron.Usage, keychron.FromSettings));
        }

        // ── The matching rules ──

        /// <summary>BasicHIDBlock::compare (DetectionManager.cpp:106-121):
        /// each field "any" or equal, the interface against the collection's
        /// (-1 with no "&amp;mi_"), and a bus only against a known one.</summary>
        [Fact]
        public void Compare_TakesAnyOrEqualForEachField()
        {
            var c = Collection(0x1234, 0x5678, iface: 2, page: 0xFF00, usage: 1);
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678), c));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, -1, 2, 0xFF00, 1), c));
            Assert.True(OpenRgbRegistrations.Compare(Hid(-1, -1, -1, 0xFF00, -1), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1235, 0x5678), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5679), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 1), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, page: 0xFF01), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, usage: 2), c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 0), Collection(0x1234, 0x5678)));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: -1), Collection(0x1234, 0x5678)));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, bus: 1), c));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, bus: 1), c, busType: 1));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, bus: 1), c, busType: 2));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678) with { Transport = OpenRgbTransport.UsbSerial }, c));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678) with { FromSettings = true }, c));
        }

        /// <summary>hidapi's Windows backend numbers a USB device with one
        /// interface, whose path has no "&amp;mi_", interface 0
        /// (windows/hid.c:515-521 and 568-570), and leaves a Bluetooth one
        /// at -1 (line 758). The bus is read from the path: "HID#VID_" under
        /// USB, the HID and HID-over-GATT service UUIDs under Bluetooth.
        /// A bus given to Compare wins over the path.</summary>
        [Fact]
        public void TheInterface_IsHidapis()
        {
            var usb = Collection(0x1234, 0x5678) with { Path = @"\\?\HID#VID_1234&PID_5678#7&2a3b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
            var usbSecond = usb with { Path = @"\\?\hid#vid_1234&pid_5678&mi_01#8&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", InterfaceNumber = 1 };
            var bt = Collection(0x1234, 0x5678) with { Path = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&00021234_PID&5678#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
            var ble = Collection(0x1234, 0x5678) with { Path = @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&021234_PID&5678_REV&0001#a&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}" };
            Assert.Equal((1, 1, 2, 2, -1), (OpenRgbRegistrations.BusOf(usb), OpenRgbRegistrations.BusOf(usbSecond),
                OpenRgbRegistrations.BusOf(bt), OpenRgbRegistrations.BusOf(ble), OpenRgbRegistrations.BusOf(Collection(0x1234, 0x5678))));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 0), usb));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 1), usb));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 1), usbSecond));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 0), bt));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 0), ble));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678), bt));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, bus: 2), bt));
            Assert.False(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, bus: 1), bt));
            Assert.True(OpenRgbRegistrations.Compare(Hid(0x1234, 0x5678, iface: 0), bt, busType: 1));
        }

        /// <summary>RunHIDDetector (DetectionManager.cpp:1515-1628): a
        /// specific detector whose IDs match turns the generic ones off for
        /// the collection even when its other fields do not compare, a
        /// disabled one does not, a generic one runs when no specific IDs
        /// matched, and the wrapped detectors keep a pass of their own
        /// (lines 1631-1730).</summary>
        [Fact]
        public void Match_TriesSpecificDetectorsFirst()
        {
            var specific = Hid(0x1234, 0x5678, page: 0xFF01);
            var generic = Hid(-1, -1, page: 0xFF00);
            var wrappedGeneric = Hid(-1, -1, page: 0xFF00, macro: "REGISTER_HID_WRAPPED_DETECTOR_IPU_ONLY");
            var table = new[] { generic, specific, wrappedGeneric };
            Assert.Equal(new[] { wrappedGeneric }, OpenRgbRegistrations.Match(Collection(0x1234, 0x5678), table: table));
            Assert.Equal(new[] { generic, wrappedGeneric }, OpenRgbRegistrations.Match(Collection(0x9999, 0x5678), table: table));
            var disabled = specific with { EnabledByDefault = false };
            Assert.Equal(new[] { generic, wrappedGeneric }, OpenRgbRegistrations.Match(Collection(0x1234, 0x5678), table: new[] { generic, disabled, wrappedGeneric }));
            var anyProduct = Hid(0x1234, -1, page: 0xFF00);
            Assert.Equal(new[] { anyProduct, wrappedGeneric }, OpenRgbRegistrations.Match(Collection(0x1234, 0x1111), table: new[] { generic, anyProduct, wrappedGeneric }));
        }

        /// <summary>On the real table: the Legion 7 Gen 10's FF89/07
        /// collection answers to its Lenovo registration and its FF89/CC
        /// one to none, a Wooting One in its no-gamepad mode to the Wooting
        /// family, and a QMK keyboard named only in the settings to
        /// nothing.</summary>
        [Fact]
        public void Match_FindsTheFamilyOnTheRealTable()
        {
            var gen10 = OpenRgbRegistrations.Match("LenovoControllers", Collection(0x048D, 0xC197, page: 0xFF89, usage: 0x07));
            Assert.Equal(new[] { "Lenovo Legion 7 Gen 10" }, gen10.Select(r => r.Name));
            Assert.Empty(OpenRgbRegistrations.Match("LenovoControllers", Collection(0x048D, 0xC197, page: 0xFF89, usage: 0xCC)));
            Assert.Equal(new[] { "Wooting One" }, OpenRgbRegistrations.Match(Collection(0x31E3, 0x1102)).Select(r => r.Name));
            Assert.Empty(OpenRgbRegistrations.Match(Collection(0xFEED, 0x0001, iface: 1, page: 0xFF60, usage: 0x61)));
        }
    }
}
