using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Every product SignalRGB's plugins name is lit by a family of
    /// PadForge's or stands in the list below with its reason, so a device a new
    /// plugin adds fails here by name when the clone is pulled and the generator
    /// rerun (#557).</summary>
    public class PluginInventoryTests
    {
        /// <summary>What the issue leaves out of the direct path, by the plugin's
        /// path. These parts light through the Aura, Mystic Light and OpenRGB
        /// routes where those apply. Plugins for memory, graphics cards and network
        /// services name no USB product, so they never reach the inventory.</summary>
        private static readonly (string Path, string Reason)[] LeftOut =
        {
            ("AMD/", InsideAPc), ("Airgoo/", InsideAPc), ("Aqua Computer/", InsideAPc), ("AsRock/", InsideAPc),
            ("Asus/ASUS_Ryujin", InsideAPc), ("Asus/ASUS_Ryuo", InsideAPc), ("Asus/ASUS_ROG_Strix_LC", InsideAPc),
            ("Asus/Motherboard", InsideAPc), ("CoolerMaster/ACER_P09", InsideAPc), ("CoolerMaster/Intel_Arc", InsideAPc),
            ("Corsair/Corsair_Elite_Capellix", InsideAPc), ("Corsair/Corsair_ICUE_Link", InsideAPc), ("Corsair/Corsair_Nautilus", InsideAPc),
            ("Corsair/Corsair_XC7", InsideAPc), ("Corsair/Corsair_XD6", InsideAPc),
            ("Corsair/Legacy_Lighting_Controllers/", InsideAPc), ("Corsair/Modern_Corsair/Corsair_ICUE_Link", InsideAPc),
            ("Corsair/Modern_Corsair/Corsair_Lighting_Commander", InsideAPc), ("Corsair/Platinum_Pro_XT_AIO/", InsideAPc),
            ("GigaByte/", InsideAPc), ("ID-Cooling/", InsideAPc), ("Lian Li/", InsideAPc), ("Msi/", InsideAPc), ("Nollie/", InsideAPc),
            ("Nzxt/NZXT_Kraken", InsideAPc), ("Nzxt/NZXT_Motherboard", InsideAPc), ("Nzxt/NZXT_Smart_Device", InsideAPc), ("Nzxt/Nzxt_Kraken", InsideAPc),
            ("PrismRGB/", InsideAPc), ("Razer/Razer_PWM_Controller", InsideAPc), ("Razer/Razer_Tomahawk_Case", InsideAPc),
            ("Thermal Take/ThermalTake_LEDBOX", InsideAPc), ("Thermal Take/ThermalTake_Riing", InsideAPc), ("Thermal Take/ThermalTake_Ring", InsideAPc),
            ("Thermal Take/Thermaltake_DPSG", InsideAPc), ("Thermalright/", InsideAPc), ("Turzx/", InsideAPc), ("Zalman/", InsideAPc), ("Zhi Yan/", InsideAPc),
            ("SRGBmods.net/SRGBmods_Wifi_LC", NetworkLights),
            ("HyperX/HyperX_Cloud_Flight", "The plugin says it has no lights and reads the battery alone."),
            ("Razer/Razer_Info_Finder", "A developer's tool, not a device."),
            ("Sony/", "DualSense and DualShock 4 lights go through PadForge's controller path."),
            // master's ProductId() returns the five-digit literal 0x0A346, and beta dropped the
            // plugin. The Virtuoso SE is 0A3D and 0A3E, which the headset plugin lights, and
            // linuxhw/LsUSB holds 30 probes of 1b1c:0a3e and none of 1b1c:a346 (2026-11-09).
            ("Corsair/Modern_Corsair/Virtuoso_HeadSets/Corsair_Virtuoso_SE_Gaming_Headset.js",
                "Its product ID is a five-digit typo that names no product."),
        };

        /// <summary>The hubs for fans and strips here are parts inside a case too,
        /// like OpenRGB's Nollie and DRGB families, which stay out by the same
        /// rule, and the screens are on coolers.</summary>
        private const string InsideAPc = "A part inside a PC.";
        private const string NetworkLights = "Lights on the network.";

        private static bool IsLeftOut(string plugin)
            => LeftOut.Any(l => plugin.StartsWith(l.Path, StringComparison.Ordinal));

        /// <summary>Products of a plugin with others that are lit: the M75 Air,
        /// whose entry in the Bragi plugin's library lists no lights.</summary>
        private static readonly (ushort Vendor, ushort Product)[] NoLights = { (0x1B1C, 0x1BF2) };

        private static bool IsLit(ushort vendor, ushort product)
        {
            // A row counts where the whole family list brings a device to it, by name in
            // PluginRowBehaviorTests when it does not.
            if (PluginDeviceLighting.Devices.Any(d => d.VendorId == vendor && d.ProductId == product && d.OnlyWhen == null && PluginRowReach.Unreached(d) == null)) return true;
            if (PluginDeviceLighting.Devices.Any(d => d.VendorId == vendor && d.ProductId == product && d.OnlyWhen != null)) return true;
            if (OpenRgbRegistrations.All.Any(r => r.VendorId == vendor && r.ProductId == product)) return true;
            if (SinowealthMouseLighting.LibratbagMice.Any(m => m.Vendor == vendor && m.Product == product)) return true;
            switch (vendor)
            {
                case AsusLaptopAura.VendorId:
                    return product == AsusLaptopAura.RearLightProduct || AsusLaptopAura.Products.Contains(product)
                        || AsusAuraUsbLighting.GHelperProducts.ContainsKey(product);
                case RoccatKoneEmpLighting.VendorId when product == RoccatKoneEmpLighting.ProductId:
                    return true;
                case 0x048D:
                    return ClevoLighting.Other8291Pids.Contains(product);
                // HID++ asks each device for its lighting features, so no table
                // of products bounds it.
                case PeripheralLinker.LogitechVid:
                    return true;
                case PeripheralLinker.RazerVid:
                    return PeripheralProductIds.RazerMice.Contains(product) || PeripheralProductIds.RazerKeyboards.Contains(product)
                        || PeripheralProductIds.RazerAccessories.Contains(product) || PeripheralProductIds.RazerMousepads.Contains(product)
                        || PeripheralProductIds.RazerHeadsets.Contains(product)
                        || RazerHidTable.MapOf(product) != null;
                case PeripheralLinker.SteelSeriesVid:
                    return SteelSeriesDirectProtocol.Models.Any(m => m.ProductId == product && m.Family != SteelSeriesFamily.None);
                case CorsairLights.VendorId:
                    return CorsairPeripheralLighting.OtherIds.ContainsKey(product) || CorsairPeripheralV2Lighting.OtherIds.ContainsKey(product)
                        || CorsairPeripheralV2Lighting.DeviceOf(product) != null || CorsairPeripheralV2Lighting.IsReceiver(product);
                case CoolerMasterLighting.VendorId:
                    return product is CoolerMasterLighting.Sk650Other or CoolerMasterLighting.Sk621;
                case LgMonitorLighting.VendorId:
                    return product == LgMonitorLighting.Ultragear38;
                case AlienwareMonitorLighting.VendorId:
                    return product == AlienwareMonitorLighting.Aw2724Dm;
                case HyperXMicrophoneLighting.KingstonVid:
                    return product == HyperXMicrophoneLighting.OtherQuadcastS;
                case RoccatVulcanLighting.TurtleBeachVendorId:
                    return product is RoccatVulcanLighting.TurtleBeachVulcanIIMax or RoccatLighting.TurtleBeachAimoWide;
                case LogitechX56Lighting.VendorId:
                    return product is LogitechX56Lighting.JoystickProductId or LogitechX56Lighting.ThrottleProductId;
                default:
                    return false;
            }
        }

        [Fact]
        public void EveryProductOfEveryPlugin_IsLit_OrLeftOutForAStatedReason()
        {
            var missed = PluginInventory.All
                .Where(row => !IsLeftOut(row.Plugin) && !NoLights.Contains((row.Vendor, row.Product))
                    && !PluginCommentedOut.ContainsKey((row.Vendor, row.Product)) && !IsLit(row.Vendor, row.Product))
                .Select(row => $"{row.Vendor:X4}:{row.Product:X4} {row.Plugin}")
                .ToList();
            Assert.True(missed.Count == 0, missed.Count + " products no family lights:\n" + string.Join("\n", missed));
        }

        /// <summary>The entries a plugin keeps in a comment and says why: the generator reads
        /// these from the comment, and stops when the comment no longer says so.</summary>
        private static readonly Dictionary<(ushort Vendor, ushort Product), string> PluginCommentedOut = new()
        {
            // Corsair_Headset_Controller.js:520 and 597, "PID for wired WHILE wireless dongle
            // plugged in, doesnt control the headset".
            [(0x1B1C, 0x0A5B)] = WiredWhileDongled,
            [(0x1B1C, 0x0A6A)] = WiredWhileDongled,
        };

        private const string WiredWhileDongled = "The wired ID while the dongle is plugged in, which does not control the headset.";

        [Fact]
        public void TheInventory_HoldsThePluginsCommentedOutHeadsets()
        {
            foreach (var (vendor, product) in PluginCommentedOut.Keys)
            {
                Assert.Contains(PluginInventory.All, r => r.Vendor == vendor && r.Product == product
                    && r.Plugin == "Corsair/Modern_Corsair/Corsair_Headset_Controller.js");
                Assert.False(IsLit(vendor, product), $"{vendor:X4}:{product:X4} is lit, so it no longer belongs in the list");
            }
        }

        /// <summary>What the other projects name that PadForge does not light, and why.</summary>
        private static readonly Dictionary<(ushort Vendor, ushort Product), string> OthersLeftOut = new()
        {
            // ckb-next: usb_legacy.c drives these with vendor control transfers, which take a
            // driver other than Windows' HID driver.
            [(0x1B1C, 0x1B02)] = LegacyTransfers,
            [(0x1B1C, 0x1B06)] = LegacyTransfers,
            [(0x1B1C, 0x1B07)] = LegacyTransfers,
            [(0x1B1C, 0x1B08)] = LegacyTransfers,
            [(0x1B1C, 0x1B09)] = LegacyTransfers,
            // HAS_NO_LIGHTS (usb.h:226).
            [(0x1B1C, 0x1B41)] = "The K66 has no lights.",
            // updatergb_wireless returns at once for these (led_wireless.c:43-46).
            [(0x1B1C, 0x1B45)] = K63Wireless,
            [(0x1B1C, 0x1B50)] = K63Wireless,
            [(0x1B1C, 0x1B8C)] = K63Wireless,
            [(0x1B1C, 0x1B8F)] = K63Wireless,
            // A dot-matrix screen on the lid, and Slash, a bar of white segments: they show
            // pictures and do not light a keyboard (asusctl rog-anime, rog-slash).
            [(0x0B05, 0x193B)] = LidScreen,
            [(0x0B05, 0x19B3)] = LidScreen,
            // Behind the Omni receiver, as asus-rog-strix-impact3-wireless.device says.
            [(0x0B05, 0x1AD7)] = "A mouse behind the Omni receiver, lit through 1ACE.",
            // aura/device.py:27-34: page 0x59, which the LampArray family lights on any device.
            [(0x048D, 0xC693)] = "A LampArray, which the LampArray family lights.",
            // An external disk's light, through SCSI (asusctl rog-scsi).
            [(0x0B05, 0x1932)] = "ROG Strix Arion, an external disk lit through SCSI commands.",
        };

        private const string LegacyTransfers = "Lit through vendor control transfers, which need a driver swap.";
        private const string K63Wireless = "ckb-next has no way to color it: a TODO in led_wireless.c.";
        private const string LidScreen = "A display on the lid, not a keyboard light.";

        /// <summary>The other projects as the plugins above: alienfx-tools goes by vendor and
        /// report length, and so does <see cref="AlienFxLighting"/>, so it names no products.</summary>
        [Fact]
        public void EveryProductOfTheOtherProjects_IsLit_OrLeftOutForAStatedReason()
        {
            var missed = OtherProjectsInventory.All
                .Where(row => !OthersLeftOut.ContainsKey((row.Vendor, row.Product)) && !IsLit(row.Vendor, row.Product))
                .Select(row => $"{row.Vendor:X4}:{row.Product:X4} {row.Source}")
                .Distinct()
                .ToList();
            Assert.True(missed.Count == 0, missed.Count + " products no family lights:\n" + string.Join("\n", missed));
        }

        [Fact]
        public void EveryOtherLeftOut_StillNamesAProduct()
        {
            foreach (var (vendor, product) in OthersLeftOut.Keys)
                Assert.True(OtherProjectsInventory.All.Any(r => r.Vendor == vendor && r.Product == product), $"{vendor:X4}:{product:X4}");
        }

        [Fact]
        public void TheOtherInventory_ReadsEachProject()
        {
            foreach (string source in new[] { "ckb-next", "libratbag", "g-helper", "asusctl", "tuxedo-drivers", "ite8291r3-ctl", "aura-loq" })
                Assert.Contains(OtherProjectsInventory.All, r => r.Source.StartsWith(source, StringComparison.Ordinal));
        }

        [Fact]
        public void TheInventory_IsWhole()
        {
            Assert.True(PluginInventory.All.Length > 500);
            Assert.Contains(PluginInventory.All, r => r.Vendor == 0x1E7D && r.Product == 0x2D5C);
            // A plugin that keeps its products in a library is read too.
            Assert.Contains(PluginInventory.All, r => r.Vendor == 0x1B1C && r.Product == 0x1BAB);
        }

        /// <summary>The Logitech plugin returns <c>LogitechDevice.ProductIDs</c>, a list a class
        /// holds, from its ProductId. The generator read it as nothing and left the plugin's 45
        /// products out, receivers and wired G devices among them.</summary>
        [Fact]
        public void TheInventory_HoldsTheLogitechPluginsClassList()
        {
            var rows = PluginInventory.All
                .Where(r => r.Plugin == "Logitech/Logitech_Modern_Device.js").ToList();
            Assert.True(rows.Count >= 45, rows.Count + " products");
            Assert.All(rows, r => Assert.Equal(PeripheralLinker.LogitechVid, r.Vendor));
            foreach (ushort product in new ushort[] { 0xC539, 0xC547, 0xC081, 0xC35B })
                Assert.Contains(rows, r => r.Product == product);
        }

        [Fact]
        public void EveryLeftOutPath_StillMatchesAPlugin()
        {
            foreach (var (path, _) in LeftOut)
                Assert.True(PluginInventory.All.Any(r => r.Plugin.StartsWith(path, StringComparison.Ordinal)), path);
        }

        /// <summary>Writes every device a lighting family lights, one row per USB identity:
        /// vendor, product, a name and where the name came from, for the docs site's
        /// supported-devices page, its device search and its device count. A device counts
        /// only where this class's other tests prove it lit: an OpenRGB registration of a
        /// ported family, a row of the plugins' or the other projects' inventories that a
        /// family lights, and the vendor tables those families read. Does nothing unless the
        /// release prep names a file in PADFORGE_LIGHTING_DEVICES_TSV.</summary>
        [Fact]
        public void WriteTheLitDevices()
        {
            string path = Environment.GetEnvironmentVariable("PADFORGE_LIGHTING_DEVICES_TSV");
            if (string.IsNullOrEmpty(path)) return;
            var named = new Dictionary<(ushort Vendor, ushort Product), (string Name, string Source)>();
            void Add(int vendor, int product, string name, string source)
            {
                if (vendor <= 0 || vendor > 0xFFFF || product < 0 || product > 0xFFFF || string.IsNullOrWhiteSpace(name)) return;
                if (!IsLit((ushort)vendor, (ushort)product)) return;
                named.TryAdd(((ushort)vendor, (ushort)product), (name.Trim(), source));
            }
            foreach (var r in OpenRgbRegistrations.All.Where(r => r.Transport == OpenRgbTransport.Hid && !r.FromSettings))
                Add(r.VendorId, r.ProductId, r.Name, "OpenRGB " + r.Family);
            foreach (var r in RazerDirectTable.Rows.Where(r => r.Family != RazerFamily.None))
                Add(PeripheralLinker.RazerVid, r.ProductId, r.Name, "openrazer");
            foreach (var (product, name, source) in SteelSeriesNames.All)
                Add(PeripheralLinker.SteelSeriesVid, product, name, source);
            foreach (var d in PluginDeviceLighting.Devices)
                Add(d.VendorId, d.ProductId, d.Name, d.Plugin);
            foreach (var d in CorsairPeripheralV2Lighting.PluginDevices.Value)
                Add(CorsairLights.VendorId, d.ProductId, d.Name, "SignalRGB Corsair_Bragi_Device.js");
            foreach (var (name, _, product, _, _) in CorsairLights.V1Sections)
                Add(CorsairLights.VendorId, product, name, "OpenRGB CorsairPeripheralController");
            foreach (var (product, (_, name)) in AsusAuraUsbLighting.GHelperProducts)
                Add(AsusLaptopAura.VendorId, product, name, "G-Helper");
            // G-Helper's MAIN_AURA_PIDS and REAR_LIGHT_PIDS name no model, and a laptop of any of
            // them carries the keyboard (AsusLaptopAura.Products).
            foreach (ushort product in AsusLaptopAura.Products.Append(AsusLaptopAura.RearLightProduct))
                Add(AsusLaptopAura.VendorId, product, "ASUS laptop keyboard", "G-Helper MAIN_AURA_PIDS");
            // What is lit through a path with no table of names, HID++ among them, is named
            // after the file that lists it.
            foreach (var row in PluginInventory.All.Where(row => !IsLeftOut(row.Plugin) && !NoLights.Contains((row.Vendor, row.Product))
                         && !PluginCommentedOut.ContainsKey((row.Vendor, row.Product))))
                Add(row.Vendor, row.Product, FileTitle(row.Plugin), row.Plugin);
            foreach (var row in OtherProjectsInventory.All.Where(row => !OthersLeftOut.ContainsKey((row.Vendor, row.Product))))
                Add(row.Vendor, row.Product, FileTitle(row.Source), row.Source);
            var lines = named.OrderBy(p => p.Key.Vendor).ThenBy(p => p.Key.Product)
                .Select(p => $"{p.Key.Vendor:X4}\t{p.Key.Product:X4}\t{p.Value.Name}\t{p.Value.Source}");
            System.IO.File.WriteAllLines(path, lines);
        }

        /// <summary>A plugin's or a project's file as words: "Plugins/Corsair/Corsair_K70_Keyboard.js"
        /// reads "Corsair K70 Keyboard".</summary>
        private static string FileTitle(string source)
        {
            string file = source.Split('/', '\\', ' ').Last();
            int dot = file.LastIndexOf('.');
            if (dot > 0) file = file.Substring(0, dot);
            return file.Replace('_', ' ').Replace('-', ' ').Trim();
        }
    }
}
