using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Lexip Np93 ALPHA mouse (#557), lit as OpenRGB's
    /// LexipMouseController lights it: 04D8:FD0A on interface 0, usage page
    /// 1, usage 2 (LexipMouseControllerDetect.cpp:19-44). It has one light
    /// (RGBController_LexipMouse.cpp:55-71).
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode:
    /// one output report of 64 bytes with report ID 0, 00 24 01, red, green
    /// and blue, 00 64 80 (SetDirect, LexipMouseController.cpp:51-75,
    /// LexipMouseController.h:18). OpenRGB marks nothing as saving ("@save
    /// :x:", RGBController_LexipMouse.cpp:20).</para>
    ///
    /// <para>What Windows changes. OpenRGB registers the mouse collection
    /// itself, page 1 usage 2, which Windows holds: it opens for another
    /// program with no access right only, and there feature reports pass
    /// and output reports do not, by WriteFile or by HidD_SetOutputReport
    /// (measured on a virtual mouse, and hidapi windows/hid.c:1009-1026). The
    /// color goes out as an output report, an interrupt OUT in OpenRGB's capture
    /// (LexipMouseController.cpp:54-58). The captures attached to OpenRGB issue
    /// 2245 name the collection: ReportDescriptor.pcapng has one of page 1 usage
    /// 0x80 with a 64-byte output report and no report ID, and colors.pcapng has
    /// the frames on OUT endpoint 0x05. Any collection of this mouse Windows does
    /// not hold, with an output report that holds the frame, is the unit in place
    /// of the mouse collection (<see cref="Lit"/>). A mouse with no such collection
    /// keeps the registered one, whose row says Windows holds it, and nothing
    /// is written (<see cref="DirectUnit.WindowsHolds"/>).</para>
    ///
    /// <para>Hand-back. No source reads the color the mouse showed before,
    /// and OpenRGB sends nothing when it closes, so the mouse keeps
    /// PadForge's last color. No source names the processes of Lexip's
    /// software, so no program is named.</para>
    /// </summary>
    internal sealed class LexipMouseLighting : IHidLightingFamily
    {
        public static readonly LexipMouseLighting Instance = new();

        internal const string OpenRgbFamily = "LexipMouseController";
        internal const ushort VendorId = 0x04D8;
        internal const ushort ProductId = 0xFD0A;

        /// <summary>PACKET_DATA_LENGTH (LexipMouseController.h:18).</summary>
        internal const int ReportSize = 64;

        public string Name => "Lexip";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId) return null;
            if (!Lit(collection) && OpenRgbRegistrations.Match(OpenRgbFamily, collection).Count == 0) return null;
            string name = OpenRgbRegistrations.Of(OpenRgbFamily).First().Name;
            return _models.Get(name, () => Model(name, ProductId));
        }

        /// <summary>A collection the frame can go out on: one Windows does not
        /// hold, with an output report that holds it.</summary>
        internal static bool Lit(HidCollectionInfo collection)
            => !collection.WindowsHolds && collection.OutputReportLength >= ReportSize;

        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_LexipMouse.cpp") { Product = $"lexip:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Outranks = (first, second) => first.VendorId == VendorId && first.ProductId == second.ProductId && Lit(first)
                                          && (!Lit(second) || string.CompareOrdinal(first.Path, second.Path) < 0),
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>SetDirect (LexipMouseController.cpp:51-75).</summary>
        internal static byte[] Frame(int rgb)
        {
            var report = new byte[ReportSize];
            report[0x01] = 0x24;
            report[0x02] = 0x01;
            report[0x03] = (byte)(rgb >> 16);
            report[0x04] = (byte)(rgb >> 8);
            report[0x05] = (byte)rgb;
            report[0x07] = 0x64;
            report[0x08] = 0x80;
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
