using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ENE lighting controller of Acer Nitro laptops (0CF2:5130) that
    /// OpenRGB's AcerNitroHidKeyboardController lights (#557). The
    /// collection is OpenRGB's registration
    /// (AcerHidKeyboardControllerDetect.cpp:50), usage page 0xFF5A, with
    /// feature reports of <see cref="PacketSize"/> bytes. One controller
    /// drives two devices, OpenRGB says: the keyboard's four zones (device
    /// 0x21) and a light on the laptop (device 0x65)
    /// (AcerHidKeyboardControllerDetect.cpp:35-44,
    /// RGBController_AcerNitroHidKeyboard.cpp:28,
    /// RGBController_AcerNitroHidLed.cpp:28), so the unit has five lights.
    ///
    /// <para>A picture goes out as OpenRGB's Direct mode sends it: one
    /// report per light, A4, the device, the static mode, brightness 100,
    /// 00 01, the color and the light's zone bit (SetLEDs,
    /// AcerNitroHidKeyboardController.cpp:40-60, with the Direct mode's
    /// brightness of 100, RGBController_AcerNitroHidKeyboard.cpp:36-45).
    /// OpenRGB marks the mode as one that saves nothing ("@save :x:").</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the
    /// controller's mode back. The keyboard shows PadForge's last colors
    /// until Acer's program sets others or the laptop restarts. No source
    /// names a process of Acer's NitroSense or PredatorSense, so none is
    /// listed.</para>
    /// </summary>
    internal sealed class AcerNitroLighting : IHidLightingFamily
    {
        public static readonly AcerNitroLighting Instance = new();

        internal const string OpenRgbFamily = "AcerNitroHidKeyboardController";

        /// <summary>ACER_HID_PACKET_SIZE, the report with its ID 0xA4
        /// (AcerNitroHidKeyboardController.h:30 and .cpp:57).</summary>
        internal const int PacketSize = 11;
        internal const byte ReportId = 0xA4;

        /// <summary>The keyboard's and the light's device IDs.</summary>
        internal const byte KeyboardDevice = 0x21;
        internal const byte LedDevice = 0x65;

        /// <summary>ACER_HID_MODE_STATIC, which SetLEDs sends for the Direct
        /// mode (AcerNitroHidKeyboardController.h:17).</summary>
        internal const byte StaticMode = 0x02;

        /// <summary>The Direct mode's brightness.</summary>
        internal const byte Brightness = 100;

        /// <summary>The address of the light on the laptop, after the four
        /// keyboard zones 0 to 3.</summary>
        internal const int LedAddress = 0x100;

        /// <summary>The shortest time between two pictures, PadForge's bound
        /// for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Acer Nitro";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "AcerNitroHidKeyboardController",
            Map = new LightMap(Lights(), "OpenRGB AcerNitroHidKeyboardController") { Product = "acer:0CF2:5130" },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < PacketSize) return null;
            return OpenRgbRegistrations.Match(OpenRgbFamily, collection).Count > 0 ? s_model.Value : null;
        }

        /// <summary>"Keyboard Backlight Zone 1" to 4, each a zone whose
        /// address is its place, then the light OpenRGB names "LED", an
        /// Other light (RGBController_AcerNitroHidKeyboard.cpp:174-197,
        /// RGBController_AcerNitroHidLed.cpp:101-118).</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
            new LightInfo(2, LightKind.Zone, 0, LightKeySource.None) { Zone = 3 },
            new LightInfo(3, LightKind.Zone, 0, LightKeySource.None) { Zone = 4 },
            new LightInfo(LedAddress, LightKind.Other, 0, LightKeySource.None),
        };

        /// <summary>SetLEDs' report for one light: the zone bit is
        /// 1 shifted by the light's place in its device.</summary>
        internal static byte[] Frame(byte device, int place, int rgb)
            => new byte[] { ReportId, device, StaticMode, Brightness, 0, 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)(1 << place), 0 };

        /// <summary>The reports of a picture, the keyboard's zones then the
        /// light, in the order of the lights.</summary>
        internal static List<byte[]> Frames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int address = lights[n].Address;
                if (address == LedAddress) frames.Add(Frame(LedDevice, 0, colors[n]));
                else if (address >= 0 && address < 4) frames.Add(Frame(KeyboardDevice, address, colors[n]));
            }
            return frames;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Frames(lights, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
