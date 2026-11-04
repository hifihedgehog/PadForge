using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Peripherals
{
    using PadForge.Common.Input.Peripherals.HidFamilies;

    /// <summary>Devices neither OpenRGB nor SignalRGB lights, from the other open
    /// projects that do (#557). Each row names its source.</summary>
    internal sealed partial class PluginDeviceLighting
    {
        private static IEnumerable<PluginDevice> OtherProjectDevices()
            => GHelperMice().Concat(GHelperDock()).Concat(GHelperHeadsets()).Concat(LibratbagMice()).Concat(TuxedoLights());

        // ── ASUS mice only G-Helper names (app/Peripherals/Mouse/AsusMouse.cs) ──

        private const byte GHelperLogo = 0x00, GHelperWheel = 0x01, GHelperUnderglow = 0x02, GHelperAll = 0x03;

        /// <summary>GetUpdateLightingModePacket (AsusMouse.cs:2067-2090): the report, 51 28,
        /// the zone, 00, the Static mode, the brightness at its maximum of 100, and the color.
        /// Nothing is stored until 50 03 (GetSaveProfilePacket, line 2249), which PadForge
        /// does not send.</summary>
        internal static byte[] GHelperMouseFrame(int length, byte report, byte zone, int color)
            => Frame(length, report, 0x51, 0x28, zone, 0x00, 0x00, 0x64, R(color), G(color), B(color));

        private static PluginDevice GHelperMouse(string name, ushort product, string file, params (byte Zone, LightInfo Light)[] lights)
            => new()
            {
                Name = name,
                VendorId = 0x0B05,
                ProductId = product,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Mouse/Models/" + file,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 0,
                UsagePage = 0xFF01,
                Usage = 0x0001,
                OutputLength = 65,
                Lights = lights.Select(l => l.Light).ToArray(),
                Slots = lights.Length,
                Picture = colors => lights.Select((l, i) => PluginStep.Output(GHelperMouseFrame(65, 0x00, l.Zone, colors[i]))),
            };

        private static IEnumerable<PluginDevice> GHelperMice()
        {
            yield return GHelperMouse("ASUS ROG Gladius III Aimpoint EVA-02", 0x1B0A, "GladiusIIIAimpoint.cs", (GHelperLogo, Logo(0)));
            yield return GHelperMouse("ASUS ROG Harpe Ace Extreme", 0x1B67, "HarpeAceAimLab.cs", (GHelperWheel, Wheel(0)));
            yield return GHelperMouse("ASUS ROG Harpe Ace Mini", 0x1B63, "HarpeAceMini.cs", (GHelperWheel, Wheel(0)));
            yield return GHelperMouse("ASUS ROG Harpe Mini Core", 0x1C8F, "HarpeAceMini.cs", (GHelperWheel, Wheel(0)));
            yield return GHelperMouse("ASUS ROG Harpe II Ace", 0x1C69, "HarpeIIAce.cs", (GHelperWheel, Wheel(0)));
            yield return GHelperMouse("ASUS ROG Keris II Ace", 0x1B16, "KerisIIAce.cs", (GHelperAll, Zone(0, 1)));
            yield return GHelperMouse("ASUS ROG Keris II Origin KJP", 0x1D4C, "KerisIIOrigin.cs",
                (GHelperLogo, Logo(0)), (GHelperWheel, Wheel(1)), (GHelperUnderglow, Edge(2)));
            yield return GHelperMouse("ASUS TUF Gaming Mini Miku", 0x1C56, "TUFM4Wireless.cs", (GHelperAll, Zone(0, 1)));
            yield return GHelperMouse("ASUS TUF Gaming Mini Miku", 0x1C57, "TUFM4Wireless.cs", (GHelperAll, Zone(0, 1)));
            // The same frame as report 03, on the collection the Omni receiver's mice use
            // (HarpeIIAce.cs, "mi_02&col03").
            yield return new PluginDevice
            {
                Name = "ASUS ROG Harpe II Ace",
                VendorId = 0x0B05,
                ProductId = 0x1AD0,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Mouse/Models/HarpeIIAce.cs",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 2,
                UsagePage = 0xFF01,
                Usage = 0x0001,
                OutputLength = 64,
                Lights = new[] { Wheel(0) },
                Slots = 1,
                Picture = colors => new[] { PluginStep.Output(GHelperMouseFrame(64, 0x03, GHelperWheel, colors[0])) },
            };
        }

        /// <summary>The ROG Bulwark Dock, report EC on interface 1 (BulwarkDock.cs): 51, three
        /// zeros, the Static mode, the brightness, the color, three zeros, speed FF, two zeros
        /// and direction FF.</summary>
        private static IEnumerable<PluginDevice> GHelperDock()
        {
            yield return new PluginDevice
            {
                Name = "ASUS ROG Bulwark Dock",
                VendorId = 0x0B05,
                ProductId = 0x1C7F,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Mouse/Models/BulwarkDock.cs",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 1,
                OutputLength = 65,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                Picture = colors => new[]
                {
                    PluginStep.Output(Frame(65, 0xEC, 0x51, 0x00, 0x00, 0x00, 0x00, 0x64, R(colors[0]), G(colors[0]), B(colors[0]),
                        0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF)),
                },
            };
        }

        // ── Mice only libratbag names ──

        private static IEnumerable<PluginDevice> LibratbagMice()
        {
            // driver-marsgaming/marsgaming-command.c:44-51, the active profile's write: the
            // color inverted, the brightness of 3 and no breathing. Only the second write
            // there, not sent here, goes to the stored profile.
            yield return new PluginDevice
            {
                Name = "Mars Gaming MM4",
                VendorId = 0x04D9,
                ProductId = 0xFA58,
                Source = "libratbag",
                Plugin = "src/driver-marsgaming/marsgaming-command.c",
                FeatureLength = 16,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                Picture = colors => new[]
                {
                    PluginStep.Feature(Frame(16, 0x02, 0x04, (byte)(0xFF - R(colors[0])), (byte)(0xFF - G(colors[0])), (byte)(0xFF - B(colors[0])), 0x03, 0x00)),
                },
            };
            // driver-sinowealth-nubwo.c:245-261: report 03, its command, the color, mode 01,
            // brightness 3 and tempo 01. No reference streams it, so it takes the bounds of a
            // stored write.
            yield return new PluginDevice
            {
                Name = "Nubwo X7 Spectrum",
                VendorId = 0x258A,
                ProductId = 0x0012,
                Source = "libratbag",
                Plugin = "src/driver-sinowealth-nubwo.c",
                FeatureLength = 64,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Picture = colors => new[]
                {
                    PluginStep.Feature(Frame(64, 0x03, 0x06, 0xBB, 0xAA, 0x2A, 0x00, 0x0A, 0x00, R(colors[0]), G(colors[0]), B(colors[0]), 0x01, 0x00, 0x03, 0x01)),
                },
            };
            // driver-steelseries.c:674-745, version 1 with the SenseiRAW quirk. Its one LED is
            // white with three levels: 1 is off, and a brightness takes value/86 + 2.
            yield return new PluginDevice
            {
                Name = "SteelSeries Sensei Raw",
                VendorId = 0x1038,
                ProductId = 0x1369,
                Source = "libratbag",
                Plugin = "src/driver-steelseries.c",
                OutputLength = 32,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                Programs = new[] { VendorPrograms.SteelSeriesGG },
                Start = new[] { PluginStep.Output(Frame(32, 0x00, 0x07, 0x01, 0x01)) },
                Picture = colors => new[] { PluginStep.Output(Frame(32, 0x00, 0x05, 0x01, SenseiRawLevel(colors[0]))) },
            };
        }

        internal static byte SenseiRawLevel(int color)
        {
            int level = Math.Max(Math.Max(R(color), G(color)), B(color));
            return (byte)(level == 0 ? 1 : level / 86 + 2);
        }

        // ── TUXEDO's lights (tuxedo-drivers src/ite_8291_lb, src/ite_8297) ──

        /// <summary>color_scaling (ite_8291_lb.c:102-119): green and blue at 100/255 on three
        /// SKUs of 6010, and green at 200/255 on one board, read once.</summary>
        private static readonly Lazy<(string Sku, string Board)> s_tuxedoMachine =
            new(() => (HidFamilyDmi.ProductSku() ?? "", AsusAuraCoreLighting.MainboardName() ?? ""));

        internal static (byte G, byte B) LightbarScaled(ushort product, string sku, string board, byte g, byte b)
        {
            if (product == 0x6010 && sku is "STEPOL1XA04" or "STELLARIS1XI05" or "STELLARIS17I06")
                return ((byte)(100 * g / 255), (byte)(100 * b / 255));
            if (board is "XxAF5xxx" or "XxAF5xxx_mLED") return ((byte)(200 * g / 255), b);
            return (g, b);
        }

        /// <summary>ite8291_write_lightbar_mono (ite_8291_lb.c:198-226): the color, then the
        /// mono mode at full brightness, eight bytes each and each its own report ID.</summary>
        internal static IEnumerable<PluginStep> TuxedoLightbarPicture(ushort product, int color, string sku, string board)
        {
            var (g, b) = LightbarScaled(product, sku, board, G(color), B(color));
            yield return PluginStep.Feature(new byte[] { 0x14, (byte)(product == 0x7000 ? 0x01 : 0x00), 0x01, R(color), g, b, 0x00, 0x00 });
            yield return PluginStep.Feature(product == 0x7000
                ? new byte[] { 0x08, 0x21, 0x01, 0x01, 0x64, 0x01, 0x00, 0x00 }
                : new byte[] { 0x08, 0x02, 0x01, 0x01, 0x64, 0x08, 0x00, 0x00 });
        }

        /// <summary>Whether this PC is TUXEDO's, as tuxedo_compatibility_check asks. ITE's 8297
        /// is also the chip of Gigabyte's motherboard lighting, under the same IDs, and parts
        /// inside a PC stay out of the direct path, so only TUXEDO's own laptops take this row.</summary>
        internal static bool IsTuxedo(string manufacturer, string product)
            => (manufacturer ?? "").Contains("TUXEDO", StringComparison.OrdinalIgnoreCase)
               || (product ?? "").Contains("TUXEDO", StringComparison.OrdinalIgnoreCase);

        private static readonly Lazy<bool> s_onTuxedo = new(() => IsTuxedo(HidFamilyDmi.Manufacturer(), HidFamilyDmi.ProductName()));

        private static IEnumerable<PluginDevice> TuxedoLights()
        {
            foreach (ushort product in new ushort[] { 0x6010, 0x7000 })
                yield return new PluginDevice
                {
                    Name = "TUXEDO Light Bar",
                    VendorId = 0x048D,
                    ProductId = product,
                    Source = "tuxedo-drivers",
                    Plugin = "src/ite_8291_lb/ite_8291_lb.c",
                    FeatureLength = 8,
                    Lights = new[] { Edge(0) },
                    Slots = 1,
                    Picture = colors => TuxedoLightbarPicture(product, colors[0], s_tuxedoMachine.Value.Sku, s_tuxedoMachine.Value.Board),
                };
            yield return new PluginDevice
            {
                Name = "TUXEDO Keyboard Light",
                VendorId = 0x048D,
                ProductId = 0x8297,
                Source = "tuxedo-drivers",
                Plugin = "src/ite_8297/ite_8297.c",
                FeatureLength = 64,
                OnlyWhen = () => s_onTuxedo.Value,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                Picture = colors => new[] { PluginStep.Feature(Frame(64, 0xCC, 0xB0, 0x01, 0x01, R(colors[0]), G(colors[0]), B(colors[0]))) },
            };
        }

        // ── ASUS headsets (app/Peripherals/Headset) ──

        private const byte HeadsetReport = 0xCC;

        /// <summary>A headset's color as G-Helper's music mode streams it, the Static packet
        /// with no save after it (AsusHeadset.cs, WriteMusicColor and WriteStream): the
        /// firmware echoes each one, and the echoes are left unread as G-Helper leaves them.
        /// No reference reads a color that goes back on hand-back, so a headset shows the
        /// last color until its next start, when the saved one returns.</summary>
        private static PluginDevice GHelperHeadset(string name, ushort product, string file, Func<int, byte[]> packet,
            int page = 0xFF00, int length = 64, IReadOnlyList<PluginStep> start = null)
            => new()
            {
                Name = name,
                VendorId = 0x0B05,
                ProductId = product,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Headset/Models/" + file,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                UsagePage = page,
                OutputLength = length,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                Start = start ?? Array.Empty<PluginStep>(),
                Picture = colors => new[] { PluginStep.Output(packet(colors[0])) },
            };

        /// <summary>LightingPacket and TurnLightingOn (AsusHeadset.cs:479-497).</summary>
        private static byte[] DeltaIIPacket(int color)
            => Frame(64, HeadsetReport, 0x51, 0x28, 0x00, 0x00, 0x01, 0x64, R(color), G(color), B(color));

        private static IReadOnlyList<PluginStep> HeadsetOn()
            => new[] { PluginStep.Output(Frame(64, HeadsetReport, 0x51, 0x10, 0x00, 0x00, 0x01)) };

        /// <summary>WriteRegister (DeltaS.cs:38-41).</summary>
        internal static byte[] DeltaSRegister(byte controller, byte register, params byte[] data)
        {
            var frame = Frame(64, HeadsetReport, 0x80, 0x00, controller, 0x00, 0x01, register, (byte)data.Length);
            data.CopyTo(frame, 8);
            return frame;
        }

        /// <summary>PauseBus (DeltaS.cs:43-46).</summary>
        internal static byte[] DeltaSPause(bool paused) => Frame(64, HeadsetReport, 0x60, 0x10, 0x00, 0x00, (byte)(paused ? 0 : 1));

        /// <summary>SetDirectMode (DeltaS.cs:97-106): each earcup's controller on the I2C
        /// bridge.</summary>
        internal static IEnumerable<byte[]> DeltaSDirect(bool on)
        {
            foreach (byte controller in new byte[] { 0xCC, 0xCE })
            {
                yield return DeltaSRegister(controller, 0x00, 0x80, 0x20);
                yield return DeltaSRegister(controller, 0x01, (byte)(on ? 1 : 0));
                yield return DeltaSRegister(controller, 0x00, 0x80, 0x2F);
                yield return DeltaSRegister(controller, 0x01, 0x01);
            }
        }

        /// <summary>WriteMusicColor (DeltaS.cs:121-126): one packet with the eight LEDs of
        /// each earcup, red, blue, green.</summary>
        internal static byte[] DeltaSPicture(int[] colors)
        {
            var frame = Frame(64, HeadsetReport, 0x60, 0x20, 0x00, 0x00);
            for (int i = 0; i < 16; i++)
            {
                frame[5 + i * 3] = R(colors[i]);
                frame[6 + i * 3] = B(colors[i]);
                frame[7 + i * 3] = G(colors[i]);
            }
            return frame;
        }

        private static IEnumerable<PluginDevice> GHelperHeadsets()
        {
            yield return GHelperHeadset("ASUS ROG Delta II", 0x1AFA, "DeltaII.cs", DeltaIIPacket, start: HeadsetOn());
            yield return GHelperHeadset("ASUS ROG Delta II", 0x1D41, "DeltaII.cs", DeltaIIPacket, start: HeadsetOn());
            yield return GHelperHeadset("ASUS ROG Pelta", 0x1B84, "Pelta.cs", DeltaIIPacket, start: HeadsetOn());
            // Clavis.cs:105-109. Its LED has no brightness.
            yield return GHelperHeadset("ASUS ROG Clavis", 0x195B, "Clavis.cs",
                color => Frame(64, HeadsetReport, 0x51, 0x28, 0x00, 0x00, 0x01, 0x00, 0x04, 0x00, 0x00, R(color), G(color), B(color)));
            // CetraRgb.cs:90-96, on its own usage page with 32-byte reports (lines 14-22).
            yield return GHelperHeadset("ASUS ROG Cetra RGB", 0x1965, "CetraRgb.cs",
                color => Frame(32, HeadsetReport, 0x51, 0x28, 0x00, 0x00, 0x01, 0x00, 0x00, 0xFF, 0x00, R(color), G(color), B(color)),
                page: 0xFF53, length: 32);
            // CetraSpeedNova.cs:59-67. G-Helper has no music mode for it, so each color goes
            // out with its save, under the bounds of a stored write.
            yield return new PluginDevice
            {
                Name = "ASUS ROG Cetra SpeedNova",
                VendorId = 0x0B05,
                ProductId = 0x1AD3,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Headset/Models/CetraSpeedNova.cs",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                UsagePage = 0xFF00,
                OutputLength = 64,
                Lights = new[] { Zone(0, 1) },
                Slots = 1,
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Picture = colors => new[]
                {
                    PluginStep.Output(Frame(64, HeadsetReport, 0x51, 0x28, 0x00, 0x00, 0x01, 0x01, R(colors[0]), G(colors[0]), B(colors[0]))),
                    PluginStep.Output(Frame(64, HeadsetReport, 0x50, 0x55)),
                },
            };
            // DeltaS.cs:108-126: the music mode's direct stream, with sixteen LEDs of its
            // own. Its exit is the hand-back.
            yield return new PluginDevice
            {
                Name = "ASUS ROG Delta S",
                VendorId = 0x0B05,
                ProductId = 0x1943,
                Source = "G-Helper",
                Plugin = "app/Peripherals/Headset/Models/DeltaS.cs",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                UsagePage = 0xFF00,
                OutputLength = 64,
                Lights = Edges(16),
                Slots = 16,
                Start = new[] { DeltaSPause(true) }.Concat(DeltaSDirect(true)).Select(PluginStep.Output).ToArray(),
                HandBack = new[] { DeltaSPause(true) }.Concat(DeltaSDirect(false)).Append(DeltaSPause(false)).Select(PluginStep.Output).ToArray(),
                Picture = colors => new[] { PluginStep.Output(DeltaSPicture(colors)) },
            };
        }
    }
}
