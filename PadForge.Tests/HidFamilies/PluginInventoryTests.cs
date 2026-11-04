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
            ("AMD/", InsideAPc), ("Aqua Computer/", InsideAPc), ("AsRock/", InsideAPc),
            ("Asus/Motherboard", InsideAPc), ("CoolerMaster/Intel_Arc", InsideAPc), ("Corsair/Corsair_Elite_Capellix", InsideAPc),
            ("Corsair/Legacy_Lighting_Controllers/", InsideAPc), ("Corsair/Modern_Corsair/Corsair_Lighting_Commander", InsideAPc),
            ("Corsair/Platinum_Pro_XT_AIO/", InsideAPc),
            ("GigaByte/", InsideAPc), ("Lian Li/", InsideAPc), ("Msi/", InsideAPc), ("Nzxt/NZXT_Motherboard", InsideAPc),
            ("Nzxt/NZXT_Smart_Device", InsideAPc), ("Nzxt/Nzxt_Kraken", InsideAPc),
            ("Razer/Razer_PWM_Controller", InsideAPc), ("Razer/Razer_Tomahawk_Case", InsideAPc), ("Thermal Take/ThermalTake_LEDBOX", InsideAPc),
            ("Thermal Take/ThermalTake_Riing", InsideAPc), ("Thermal Take/ThermalTake_Ring", InsideAPc), ("Thermal Take/Thermaltake_DPSG", InsideAPc),
            ("Zalman/", InsideAPc),
            ("SRGBmods.net/SRGBmods_Wifi_LC", NetworkLights),
            ("HyperX/HyperX_Cloud_Flight", "The plugin says it has no lights and reads the battery alone."),
            ("Razer/Razer_Info_Finder", "A developer's tool, not a device."),
            ("Sony/", "DualSense and DualShock 4 lights go through PadForge's controller path."),
        };

        private const string InsideAPc = "A part inside a PC.";
        private const string NetworkLights = "Lights on the network.";

        private static bool IsLeftOut(string plugin)
            => LeftOut.Any(l => plugin.StartsWith(l.Path, StringComparison.Ordinal));

        private static bool IsLit(ushort vendor, ushort product)
        {
            if (PluginDeviceLighting.Devices.Any(d => d.VendorId == vendor && d.ProductId == product)) return true;
            if (OpenRgbRegistrations.All.Any(r => r.VendorId == vendor && r.ProductId == product)) return true;
            switch (vendor)
            {
                // HID++ asks each device for its lighting features, so no table
                // of products bounds it.
                case PeripheralLinker.LogitechVid:
                    return true;
                case PeripheralLinker.RazerVid:
                    return PeripheralProductIds.RazerMice.Contains(product) || PeripheralProductIds.RazerKeyboards.Contains(product)
                        || PeripheralProductIds.RazerAccessories.Contains(product) || PeripheralProductIds.RazerMousepads.Contains(product)
                        || PeripheralProductIds.RazerHeadsets.Contains(product) || RazerLightTable.Rows.Any(r => r.ProductId == product)
                        || RazerHidTable.MapOf(product) != null;
                case PeripheralLinker.SteelSeriesVid:
                    return SteelSeriesDirectProtocol.Models.Any(m => m.ProductId == product && m.Family != SteelSeriesFamily.None);
                case CorsairLights.VendorId:
                    return CorsairPeripheralLighting.OtherIds.ContainsKey(product) || CorsairPeripheralV2Lighting.DeviceOf(product) != null
                        || CorsairPeripheralV2Lighting.IsReceiver(product);
                case CoolerMasterLighting.VendorId:
                    return product == CoolerMasterLighting.Sk650Other;
                case LgMonitorLighting.VendorId:
                    return product == LgMonitorLighting.Ultragear38;
                default:
                    return false;
            }
        }

        [Fact]
        public void EveryProductOfEveryPlugin_IsLit_OrLeftOutForAStatedReason()
        {
            var missed = PluginInventory.All
                .Where(row => !IsLeftOut(row.Plugin) && !IsLit(row.Vendor, row.Product))
                .Select(row => $"{row.Vendor:X4}:{row.Product:X4} {row.Plugin}")
                .ToList();
            Assert.True(missed.Count == 0, missed.Count + " products no family lights:\n" + string.Join("\n", missed));
        }

        [Fact]
        public void TheInventory_IsWhole()
        {
            Assert.True(PluginInventory.All.Length > 500);
            Assert.Contains(PluginInventory.All, r => r.Vendor == 0x1E7D && r.Product == 0x2D5C);
            // A plugin that keeps its products in a library is read too.
            Assert.Contains(PluginInventory.All, r => r.Vendor == 0x1B1C && r.Product == 0x1BAB);
        }

        [Fact]
        public void EveryLeftOutPath_StillMatchesAPlugin()
        {
            foreach (var (path, _) in LeftOut)
                Assert.True(PluginInventory.All.Any(r => r.Plugin.StartsWith(path, StringComparison.Ordinal)), path);
        }
    }
}
