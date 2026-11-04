using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The GameSir Nova 2 Lite gamepad, wired (3537:100F) and on its
    /// wireless dongle (3537:1098), lit as OpenRGB's GameSirController
    /// lights it (#557): interface 2, usage page 0xFF7A, usage 1
    /// (GameSirControllerDetect.cpp:15-36). Its one light is the pad's,
    /// OpenRGB's "Main LED" (RGBController_GameSir.cpp:40-55).
    ///
    /// <para>The model of the device. A picture is one output report of 65
    /// bytes with report ID 0: 05 08 0A 01 03, red, green and blue, 00, and
    /// the low byte of the sum of bytes 1 to 9 at 10 (SetColor,
    /// GameSirController.cpp:46-71; DeviceUpdateLEDs,
    /// RGBController_GameSir.cpp:57-60). OpenRGB's file has no save field,
    /// and its one mode carries no save flag (lines 25-30).</para>
    ///
    /// <para>Hand-back. No source reads the color the pad showed before, and
    /// OpenRGB sends nothing when it closes, so the pad keeps PadForge's
    /// last color. GameSir configures the pad through GameSir Connect
    /// (GameSir's app page lists the Nova 2 Lite under it), and no source
    /// names that program's processes, so no program is named.</para>
    /// </summary>
    internal sealed class GameSirLighting : IHidLightingFamily
    {
        public static readonly GameSirLighting Instance = new();

        internal const string OpenRgbFamily = "GameSirController";
        internal const ushort VendorId = 0x3537;

        internal const int ReportSize = 65;

        public string Name => "GameSir";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_GameSir.cpp") { Product = $"gamesir:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SetColor (GameSirController.cpp:46-71).</summary>
        internal static byte[] Frame(int rgb)
        {
            var report = new byte[ReportSize];
            report[1] = 0x05;
            report[2] = 0x08;
            report[3] = 0x0A;
            report[4] = 0x01;
            report[5] = 0x03;
            report[6] = (byte)(rgb >> 16);
            report[7] = (byte)(rgb >> 8);
            report[8] = (byte)rgb;
            report[9] = 0x00;
            int checksum = 0;
            for (int i = 1; i < 10; i++) checksum += report[i];
            report[10] = (byte)(checksum & 0xFF);
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                return io.WriteOutput(Frame(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
