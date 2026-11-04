using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Lego Dimensions Toypad Base (#557), lit as OpenRGB's
    /// LegoDimensionsToypadBaseController lights it: 0E6F:0241, any
    /// collection (LegoDimensionsToypadBaseControllerDetect.cpp:19-44). Its
    /// three lights are the center, left and right pads, OpenRGB's zones
    /// in that order, numbered 1 to 3 in the reports
    /// (RGBController_LegoDimensionsToypadBase.cpp:80-120). The base has no
    /// input row, so it gets the shared worker's unit row.
    ///
    /// <para>The model of the device. Every report is an output report of
    /// 32 bytes with report ID 0, whose first byte after the ID is 0x55
    /// (LegoDimensionsToypadBaseController.h:18-19). OpenRGB's controller
    /// sends the activation 55 0F B0 01 "(c) LEGO 2014" F7 when it is made
    /// (Activate, LegoDimensionsToypadBaseController.cpp:16-23 and 53-79),
    /// and a session sends it when it begins. A picture is OpenRGB's Direct
    /// mode: one report per pad, 55 06 C0 02, the pad, red, green and blue,
    /// and the low byte of the sum of bytes 1 to 8 at 9 (SetDirect, lines
    /// 81-102; DeviceUpdateLEDs, RGBController_LegoDimensionsToypadBase.cpp:109-120).
    /// Choosing Direct sends nothing (DeviceUpdateMode, lines 127-133).
    /// OpenRGB marks nothing as saving ("@save :x:", line 20).</para>
    ///
    /// <para>Hand-back. No source reads the colors the base showed before,
    /// and OpenRGB sends nothing when it closes, so the base keeps
    /// PadForge's last picture. No program lights it on a PC.</para>
    /// </summary>
    internal sealed class LegoDimensionsToypadLighting : IHidLightingFamily
    {
        public static readonly LegoDimensionsToypadLighting Instance = new();

        internal const string OpenRgbFamily = "LegoDimensionsToypadBaseController";
        internal const ushort VendorId = 0x0E6F;

        /// <summary>LEGO_DIMENSIONS_TOYPAD_BASE_REPORT_ID, _PACKET_LENGTH and
        /// the activate and direct commands (LegoDimensionsToypadBaseController.h:18-27).</summary>
        internal const byte Marker = 0x55;
        internal const int ReportSize = 32;
        internal const byte ActivateCommand = 0xB0;
        internal const byte DirectCommand = 0xC0;

        internal const int Pads = 3;

        public string Name => "Lego Dimensions";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        /// <summary>The center, left and right pads, zones 1 to 3, each
        /// addressed by the pad number its report carries.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[Pads];
            for (int i = 0; i < Pads; i++) lights[i] = new LightInfo(i + 1, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_LegoDimensionsToypadBase.cpp")
            {
                Product = $"legotoypad:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = AocMouseLighting.RepaintMs,
            Begin = (io, _) => io != null && io.WriteOutput(Activate()) ? new Session() : null,
        };

        /// <summary>Activate (LegoDimensionsToypadBaseController.cpp:53-79).</summary>
        internal static byte[] Activate()
        {
            var report = new byte[ReportSize];
            byte[] body =
            {
                Marker, 0x0F, ActivateCommand, 0x01,
                0x28, 0x63, 0x29, 0x20, 0x4C, 0x45, 0x47, 0x4F, 0x20, 0x32, 0x30, 0x31, 0x34,
                0xF7,
            };
            Array.Copy(body, 0, report, 1, body.Length);
            return report;
        }

        /// <summary>SetDirect (lines 81-102).</summary>
        internal static byte[] Direct(int pad, int rgb)
        {
            var report = new byte[ReportSize];
            report[1] = Marker;
            report[2] = 0x06;
            report[3] = DirectCommand;
            report[4] = 0x02;
            report[5] = (byte)pad;
            report[6] = (byte)(rgb >> 16);
            report[7] = (byte)(rgb >> 8);
            report[8] = (byte)rgb;
            byte sum = 0;
            for (int i = 1; i < 9; i++) sum += report[i];
            report[9] = sum;
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
                var byPad = KeyboardLedNames.ByAddress(lights, colors, Pads + 1);
                for (int pad = 1; pad <= Pads; pad++)
                    if (!io.WriteOutput(Direct(pad, byPad[pad]))) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
