using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of a Valkyrie VK99: OpenRGB's name for it, the key
    /// code its color goes out with, and its cell in the 6 by 22 matrix
    /// (RGBController_ValkyrieKeyboard.cpp:19-261,
    /// ValkyrieKeyboardController.h:55-74).</summary>
    internal readonly record struct ValkyrieLed(string Name, byte Code, byte Row, byte Col);

    /// <summary>
    /// The Valkyrie VK99 and VK99 Pro keyboards, lit as OpenRGB's
    /// ValkyrieKeyboardController lights them (#557). Both answer as
    /// 0x05AC:0x024F on page 0xFF13, usage 1: the Pro on interface 3, the
    /// VK99 on interface 2, and the interface picks the board's lights
    /// (ValkyrieKeyboardControllerDetect.cpp:35-36,
    /// RGBController_ValkyrieKeyboard.cpp:301-366): 98 on the Pro, 102 on
    /// the VK99.
    ///
    /// <para>A picture is OpenRGB's SendColors, every report a 65-byte
    /// feature report 0 (ValkyrieKeyboardController.cpp:64-166): the start
    /// report 04 20 with 08 at byte 9 and a feature read, then seven
    /// reports of 16 lights at most, each light's key code, red, green and
    /// blue, then a zero report, the end report 04 02 and a feature read,
    /// 1 ms after each report and 33 ms after the picture. The keyboard's
    /// only mode in OpenRGB is this Direct one, stored nowhere ("@save :x:",
    /// RGBController_ValkyrieKeyboard.cpp:262-292), and OpenRGB sends
    /// nothing when it lets the keyboard go. No other source drives these
    /// boards, so no reference returns the keyboard to its own effect: the
    /// hand-back sends nothing and the keyboard keeps the last picture
    /// until it restarts.</para>
    ///
    /// <para>No program is named: no source names the processes of
    /// Valkyrie's software.</para>
    /// </summary>
    internal sealed class ValkyrieKeyboardLighting : IHidLightingFamily
    {
        public static readonly ValkyrieKeyboardLighting Instance = new();

        internal const string FamilyName = "ValkyrieKeyboardController";

        /// <summary>The report length, the report ID included
        /// (ValkyrieKeyboardController.cpp:102 and 139).</summary>
        internal const int ReportLength = 65;

        /// <summary>The lights a report carries (lines 95-101).</summary>
        internal const int PerReport = 16;
        internal const int Reports = 7;

        /// <summary>The waits (lines 131, 134 and 147-165).</summary>
        internal const int ReportWaitMs = 1;
        internal const int PictureWaitMs = 33;

        /// <summary>The Pro's interface (line 72 and
        /// RGBController_ValkyrieKeyboard.cpp:304-312).</summary>
        internal const int ProInterface = 3;

        /// <summary>The shortest time between two pictures: OpenRGB's wait
        /// after one.</summary>
        internal const int RepaintMs = PictureWaitMs;

        public string Name => "Valkyrie";

        private readonly Dictionary<(ushort, bool), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            int bus = OpenRgbRegistrations.BusOf(collection);
            bool pro = OpenRgbRegistrations.InterfaceOf(collection, bus) == ProInterface;
            var key = (collection.ProductId, pro);
            lock (_models)
            {
                if (_models.TryGetValue(key, out var model)) return model;
                string product = $"valkyrie:{collection.VendorId:X4}:{collection.ProductId:X4}:{(pro ? "pro" : "99")}";
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registrations[0].Name,
                    Map = new LightMap(Lights(pro), "OpenRGB RGBController_ValkyrieKeyboard.cpp") { Product = product },
                    RepaintMs = RepaintMs,
                    Begin = (io, wait) => new Session(pro),
                };
                _models[key] = model;
                return model;
            }
        }

        internal static ValkyrieLed[] Table(bool pro) => pro ? ValkyrieLayouts.Pro : ValkyrieLayouts.Normal;

        /// <summary>A board's lights, each addressed by its place in light
        /// order and placed at its matrix cell. OpenRGB names the key
        /// between - and Backspace "Key: +" (lines 82-84 and 188-190): the
        /// = key.</summary>
        internal static IReadOnlyList<LightInfo> Lights(bool pro)
        {
            var table = Table(pro);
            var lights = new List<LightInfo>(table.Length);
            for (int i = 0; i < table.Length; i++)
            {
                var led = table[i];
                var light = led.Name == "Key: +"
                    ? new LightInfo(i, LightKind.Key, 0x2E, LightKeySource.Generated)
                    : SinowealthLighting.Named(i, led.Name);
                lights.Add(light with { X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true });
            }
            return lights;
        }

        /// <summary>SendInitializeColorPacket's report (lines 137-150).</summary>
        internal static byte[] StartReport()
        {
            var report = new byte[ReportLength];
            report[1] = 0x04;
            report[2] = 0x20;
            report[9] = 0x08;
            return report;
        }

        /// <summary>SendTerminateColorPacket's two reports (lines 152-166).</summary>
        internal static byte[][] EndReports()
        {
            var end = new byte[ReportLength];
            end[1] = 0x04;
            end[2] = 0x02;
            return new[] { new byte[ReportLength], end };
        }

        /// <summary>The seven color reports: light 16 x i + n at byte 1 + 4 n
        /// of report i, its key code then red, green and blue (lines 64-132).
        /// A light the picture does not name is black.</summary>
        internal static byte[][] ColorReports(bool pro, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var table = Table(pro);
            var byIndex = new int[table.Length];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
                if ((uint)lights[n].Address < (uint)table.Length) byIndex[lights[n].Address] = colors[n];
            var reports = new byte[Reports][];
            for (int i = 0; i < Reports; i++)
            {
                var report = new byte[ReportLength];
                int inReport = i == Reports - 1 ? table.Length - PerReport * (Reports - 1) : PerReport;
                for (int n = 0; n < inReport; n++)
                {
                    int led = i * PerReport + n;
                    int rgb = byIndex[led];
                    report[n * 4 + 1] = table[led].Code;
                    report[n * 4 + 2] = (byte)(rgb >> 16);
                    report[n * 4 + 3] = (byte)(rgb >> 8);
                    report[n * 4 + 4] = (byte)rgb;
                }
                reports[i] = report;
            }
            return reports;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _pro;

            public Session(bool pro) => _pro = pro;

            public override bool Feature => true;

            /// <summary>Nothing: no reference returns the keyboard to its
            /// own effect.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var read = new byte[Math.Max(ReportLength, io.FeatureReportLength)];
                if (!io.SetFeature(StartReport())) return HidLightingPaint.Failed;
                wait(ReportWaitMs);
                // OpenRGB reads the answer and does not look at it.
                io.GetFeature(0x00, read);
                wait(ReportWaitMs);
                foreach (var report in ColorReports(_pro, lights, colors))
                {
                    if (!io.SetFeature(report)) return HidLightingPaint.Failed;
                    wait(ReportWaitMs);
                }
                foreach (var report in EndReports())
                {
                    if (!io.SetFeature(report)) return HidLightingPaint.Failed;
                    wait(ReportWaitMs);
                }
                io.GetFeature(0x00, read);
                wait(ReportWaitMs);
                wait(PictureWaitMs);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
