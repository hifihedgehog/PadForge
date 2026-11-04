using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Red Square Keyrox TKL and TKL V2, lit as OpenRGB's
    /// RedSquareKeyroxController lights them (#557), on interface 3, page
    /// 0xFF00, usage 2 (RedSquareKeyroxControllerDetect.cpp:46-47).
    ///
    /// <para>OpenRGB lights the keyboard per key in its Custom mode
    /// (RGBController_RedSquareKeyrox.cpp:172-177 and 350-368). Every report
    /// is a feature report 0 of 521 bytes, then 10 ms
    /// (RedSquareKeyroxController.cpp:256-269): the mode report, 01 at
    /// byte 4, 04 at 6 and the mode 0x0C at 8 (lines 106-122), and the colors
    /// report, B0 01 07 at bytes 4 to 6 and each light's red, green, blue and
    /// brightness at 8 + 4 x its place in the keyboard's sequence (lines
    /// 223-254, led_sequence_positions at RGBController_RedSquareKeyrox.cpp:140-147).
    /// Custom's brightness is its highest, 0xFF, where the keyboard keeps it
    /// in each light's fourth byte (lines 274-284). A session sends the mode
    /// report before its first picture and the colors report for each.</para>
    ///
    /// <para>No reference reads the keyboard's mode back, OpenRGB marks no
    /// mode as saved or not, and no other source drives this keyboard, so
    /// the hand-back sends nothing and the keyboard stays in its Custom
    /// mode with the last picture.</para>
    ///
    /// <para>No program is named: no source names the processes of Red
    /// Square's software.</para>
    /// </summary>
    internal sealed class RedSquareKeyroxLighting : IHidLightingFamily
    {
        public static readonly RedSquareKeyroxLighting Instance = new();

        internal const string FamilyName = "RedSquareKeyroxController";

        /// <summary>PACKET_DATA_LENGTH and the report ID (RedSquareKeyroxController.h:16,
        /// RedSquareKeyroxController.cpp:260).</summary>
        internal const int DataLength = 520;
        internal const int ReportLength = DataLength + 1;

        /// <summary>CUSTOM_MODE_VALUE (RedSquareKeyroxController.h:28-42).</summary>
        internal const byte CustomMode = 0x0C;

        /// <summary>Custom's brightness, its highest (RGBController_RedSquareKeyrox.cpp:282-283).</summary>
        internal const byte CustomBrightness = 0xFF;

        /// <summary>The wait after each report (RedSquareKeyroxController.cpp:268).</summary>
        internal const int ReportWaitMs = 10;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it.</summary>
        internal const int RepaintMs = 33;

        public string Name => "Red Square Keyrox";

        private readonly Dictionary<ushort, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            lock (_models)
            {
                if (_models.TryGetValue(collection.ProductId, out var model)) return model;
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registrations[0].Name,
                    Map = new LightMap(Lights(), "OpenRGB RGBController_RedSquareKeyrox.cpp")
                    {
                        Product = $"keyrox:{collection.VendorId:X4}:{collection.ProductId:X4}",
                    },
                    RepaintMs = RepaintMs,
                    Begin = (io, wait) => new Session(),
                };
                _models[collection.ProductId] = model;
                return model;
            }
        }

        /// <summary>The 87 lights in OpenRGB's order, each addressed by its
        /// place in the colors report's sequence.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var table = AnnePro2KeyroxLayouts.KeyroxTkl;
            var lights = new List<LightInfo>(table.Length);
            foreach (var led in table)
                lights.Add(SinowealthLighting.Named(led.Place, led.Name) with
                {
                    X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true,
                });
            return lights;
        }

        /// <summary>The Custom mode report, SetMode (lines 106-122).</summary>
        internal static byte[] ModeReport()
        {
            var report = new byte[ReportLength];
            report[1 + 4] = 0x01;
            report[1 + 6] = 0x04;
            report[1 + 8] = CustomMode;
            return report;
        }

        /// <summary>The colors report, SetLEDsData (lines 223-254).</summary>
        internal static byte[] ColorsReport(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var report = new byte[ReportLength];
            report[1 + 4] = 0xB0;
            report[1 + 5] = 0x01;
            report[1 + 6] = 0x07;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = 1 + 7 + lights[n].Address * 4;
                if (lights[n].Address < 0 || at + 4 >= report.Length) continue;
                int rgb = colors[n];
                report[at + 1] = (byte)(rgb >> 16);
                report[at + 2] = (byte)(rgb >> 8);
                report[at + 3] = (byte)rgb;
                report[at + 4] = CustomBrightness;
            }
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            private bool _custom;

            public override bool Feature => true;

            /// <summary>Nothing: no reference returns the keyboard to its own
            /// effect.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_custom)
                {
                    if (!io.SetFeature(ModeReport())) return HidLightingPaint.Failed;
                    wait(ReportWaitMs);
                    _custom = true;
                }
                if (!io.SetFeature(ColorsReport(lights, colors))) return HidLightingPaint.Failed;
                wait(ReportWaitMs);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
