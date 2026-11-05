using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The MSI Clutch GM11 mouse (#557), lit as OpenRGB's MSIClutchController
    /// lights it: 0DB0:0D11 on interface 2, usage page 0xFF00, usage 0xFF00
    /// (MSIClutchControllerDetect.cpp:16-34). Its one light is the logo and
    /// the underglow, lit as one (RGBController_MSIClutchGM11.cpp:172-188).
    ///
    /// <para>The model of the device. The mouse has no Direct mode ("@direct
    /// :x:", RGBController_MSIClutchGM11.cpp:14-22). The one mode that takes
    /// any color is Static, and OpenRGB sets it this way, each feature
    /// report 9 bytes, report ID 0 and eight bytes whose last is 0xFF minus
    /// the sum of the seven before, each followed by 5 ms: the handshake
    /// 03 03 0F F2 40, the mode trigger 85 02, the brightness 05 02 00 and
    /// Static's brightness, the highest (0xFF), the palette preparation
    /// 10 00 18, then the palette as an output report of 33 bytes with
    /// report ID 0, seven fixed colors and the color at 22, and 0xD0 at 32,
    /// then 5 ms, then the parameters 05 02 08 07, the custom palette slot
    /// (SetMode, SendPacket, SendHandshake, SendModeTrigger,
    /// SendBrightness, SendCustomPalette and SendModeParameters,
    /// MSIClutchGM11Controller.cpp:88-271; MSIClutchGM11Controller.h:18-60;
    /// RGBController_MSIClutchGM11.cpp:38-48 and 190-213).</para>
    ///
    /// <para>Every mode carries MODE_FLAG_AUTOMATIC_SAVE and the header says
    /// "@save :robot:" (lines 18 and 41): the mouse keeps each color it is
    /// sent. A picture is held before it goes out and pictures are spaced,
    /// the bounds the stored writes of <see cref="SinowealthLighting"/>
    /// keep.</para>
    ///
    /// <para>Hand-back. No source reads the mode the mouse ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// mouse keeps the last color in its Static mode. MSI lights the mouse
    /// through Dragon Center's Mystic Light (MSI's product page for the
    /// Clutch GM11), and PadForge leaves the mouse to Mystic Light while it
    /// runs (<see cref="VendorPrograms.MsiMysticLight"/>).</para>
    /// </summary>
    internal sealed class MsiClutchLighting : IHidLightingFamily
    {
        public static readonly MsiClutchLighting Instance = new();

        internal const string OpenRgbFamily = "MSIClutchController";
        internal const ushort VendorId = 0x0DB0;

        /// <summary>MSI_CLUTCH_GM11_REPORT_SIZE, _PALETTE_SIZE and
        /// _PACKET_DELAY (MSIClutchGM11Controller.h:18-21).</summary>
        internal const int PacketSize = 8;
        internal const int PaletteSize = 33;
        internal const int PacketDelayMs = 5;

        /// <summary>The commands, the registers and the custom slot
        /// (MSIClutchGM11Controller.h:23-33), and the Static mode with its
        /// default brightness (lines 38-54, RGBController_MSIClutchGM11.cpp:47).</summary>
        internal const byte Handshake = 0x03;
        internal const byte LedControl = 0x05;
        internal const byte PalettePrep = 0x10;
        internal const byte ModeTrigger = 0x85;
        internal const byte RegBrightness = 0x00;
        internal const byte RegParams = 0x08;
        internal const byte SlotCustom = 0x07;
        internal const byte PaletteTerminator = 0xD0;
        internal const byte StaticMode = 0x02;
        internal const byte BrightnessMax = 0xFF;

        public string Name => "MSI Clutch";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < PacketSize + 1) return null;
            if (collection.OutputReportLength < PaletteSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_MSIClutchGM11.cpp") { Product = $"msiclutch:{VendorId:X4}:{productId:X4}" },
            HoldMs = SinowealthLighting.StoredHoldMs,
            RepaintMs = SinowealthLighting.StoredRepaintMs,
            Programs = VendorPrograms.MsiClutch,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SendPacket's report: report ID 0, the eight bytes, the
        /// last 0xFF minus the sum of the seven before
        /// (MSIClutchGM11Controller.cpp:88-112).</summary>
        internal static byte[] Packet(params byte[] data)
        {
            var packet = new byte[PacketSize];
            Array.Copy(data, packet, Math.Min(data.Length, PacketSize - 1));
            byte sum = 0;
            for (int i = 0; i < PacketSize - 1; i++) sum += packet[i];
            packet[PacketSize - 1] = (byte)(0xFF - sum);
            var report = new byte[PacketSize + 1];
            Array.Copy(packet, 0, report, 1, PacketSize);
            return report;
        }

        /// <summary>SendCustomPalette's output report (lines 163-203).</summary>
        internal static byte[] Palette(int rgb)
        {
            var palette = new byte[PaletteSize];
            byte[] fixedColors =
            {
                0xFF, 0x00, 0x00,
                0xFF, 0xFF, 0x00,
                0x00, 0xFF, 0x00,
                0x00, 0xFF, 0xFF,
                0x00, 0x00, 0xFF,
                0xFF, 0x00, 0xFF,
                0xFF, 0xFF, 0xFF,
            };
            Array.Copy(fixedColors, 0, palette, 1, fixedColors.Length);
            palette[22] = (byte)(rgb >> 16);
            palette[23] = (byte)(rgb >> 8);
            palette[24] = (byte)rgb;
            palette[32] = PaletteTerminator;
            return palette;
        }

        /// <summary>SetMode for the Static mode (lines 211-249): each report
        /// in order, true for a feature report, false for the palette's
        /// output report.</summary>
        internal static List<(bool Feature, byte[] Report)> StaticFrames(int rgb) => new()
        {
            (true, Packet(Handshake, 0x03, 0x0F, 0xF2, 0x40, 0x00, 0x00)),
            (true, Packet(ModeTrigger, StaticMode)),
            (true, Packet(LedControl, StaticMode, RegBrightness, BrightnessMax)),
            (true, Packet(PalettePrep, 0x00, 0x18)),
            (false, Palette(rgb)),
            (true, Packet(LedControl, StaticMode, RegParams, SlotCustom)),
        };

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes the Static mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                foreach (var (feature, report) in StaticFrames(rgb))
                {
                    if (!(feature ? io.SetFeature(report) : io.WriteOutput(report))) return HidLightingPaint.Failed;
                    wait(PacketDelayMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
