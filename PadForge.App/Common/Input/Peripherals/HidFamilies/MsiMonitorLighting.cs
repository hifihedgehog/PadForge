using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's MSIMonitorController family (#557): MSI's monitors behind
    /// the "MSI Gaming Controller" (1462:3FA4) on interface 0, usage page 1,
    /// usage 0 (MSIMonitorControllerDetect.cpp:15-16 and 163). The same ID
    /// pair has a vendor collection on usage page 0xFF00, which OpenRGB's
    /// MSIOptixController lights (<see cref="MsiOptixLighting"/>).
    ///
    /// <para>The layout. One ID pair covers several buffer layouts, so a
    /// session asks the monitor first, as OpenRGB's detector does: the
    /// queued input reports drained, then output report 01 B0, and an
    /// answer within 500 ms of 01 5A with the layout version at byte 3
    /// (GetLayoutVersion, MSIMonitorController.cpp:145-188, and
    /// MSIMonitorController.h:61-65). A version in OpenRGB's table selects
    /// the 168-byte 0x72 layout with its LED count and fill direction
    /// (<see cref="Layouts"/>, MSIMonitorControllerDetect.cpp:46-112). Any
    /// other answer, or none, keeps the 78-byte 0x71 layout with nine LEDs
    /// (lines 135-157, RGBController_MSIMonitor.cpp:116-136). The LEDs are
    /// one line on the back, "Rear", edge lights here, and the session's
    /// lights (<see cref="HidLightingSession.Map"/>).</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode, one feature report followed
    /// by 5 ms (SendFeatureReport, MSIMonitorController.cpp:31-36, and
    /// MSI_MONITOR_PIPE_IDLE_MS, MSIMonitorController.h:29). On the 0x71
    /// layout it is Set with the static mode and the last byte 00 (lines
    /// 73-143, RGBController_MSIMonitor.cpp:35-40 and 138-141). On the
    /// 0x72 layout it is SetMode72 with the static mode, speed 0, brightness
    /// 100, the first LED's color as color 1, color 2 black, the user
    /// palette flag 80 in both control blocks, the colors in the second array
    /// at byte 95, reversed for a monitor filled right to left, and the store
    /// byte 00 (MSIMonitorController.cpp:190-263,
    /// RGBController_MSIMonitor72.cpp:44-52 and 508-565).</para>
    ///
    /// <para>What the writes store, and the hand-back. Both layouts' last
    /// byte is the store to flash, and Direct sends 00, "applies without a
    /// flash write" (MSIMonitorController.cpp:131-136 and 255-260), so a
    /// power cycle brings back the effect the monitor stored. No reference
    /// reads that effect or returns it while the monitor stays on, so
    /// nothing goes out at hand-back, and the monitor shows PadForge's last
    /// colors until then or until MSI's software sets others. MSI sets the
    /// LED effects of its gaming monitors through Mystic Light (MSI Remote
    /// Display, "choose the LED effects for the selected gaming monitor with
    /// MSI Mystic Light"), and while Mystic Light runs the monitor is left to
    /// it (<see cref="VendorPrograms.MsiMysticLight"/>).</para>
    ///
    /// <para>Not ported, as in OpenRGB: the dual-block versions 11, 13 and 15
    /// and the 0x82 versions 31 and 47, which OpenRGB leaves out
    /// (MSIMonitorControllerDetect.cpp:84-98). They light as the 0x71 layout
    /// there and here.</para>
    /// </summary>
    internal sealed class MsiMonitorLighting : IHidLightingFamily
    {
        public static readonly MsiMonitorLighting Instance = new();

        internal const string OpenRgbFamily = "MSIMonitorController";

        /// <summary>MSI_USB_VID (MSIMonitorControllerDetect.cpp:15).</summary>
        internal const ushort VendorId = 0x1462;

        /// <summary>MSI_MONITOR_LEDS and MSI_MONITOR_PACKET_SIZE, the 0x71
        /// layout (MSIMonitorController.h:19-20).</summary>
        internal const int Leds71 = 9;
        internal const int PacketSize71 = 78;

        /// <summary>The 0x72 layout (MSIMonitorController.h:34-38).</summary>
        internal const int PacketSize72 = 168;
        internal const int StoreIndex72 = 167;
        internal const int ArrayBIndex72 = 95;
        internal const int ArraySlots72 = 24;

        /// <summary>The version query (MSIMonitorController.h:61-64).</summary>
        internal const int QuerySize = 64;
        internal const byte QueryReportId = 0x01;
        internal const byte QueryAck = 0x5A;
        internal const byte GetVersion = 0xB0;

        /// <summary>How long the answer to the query is waited for
        /// (MSIMonitorController.cpp:177).</summary>
        internal const int AnswerMs = 500;

        /// <summary>How many queued reports the drain reads at most. OpenRGB
        /// reads until none is there (lines 160-163). The bound is
        /// PadForge's, <see cref="AsusAuraUsbLighting.ClearLimit"/>.</summary>
        internal const int DrainLimit = AsusAuraUsbLighting.ClearLimit;

        /// <summary>MSI_MONITOR_PIPE_IDLE_MS (MSIMonitorController.h:29).</summary>
        internal const int PipeIdleMs = 5;

        /// <summary>The static mode, OpenRGB's Direct value on both layouts
        /// (MSI_MONITOR_STATIC_MODE_VALUE and MSI_MONITOR_72_MODE_STATIC,
        /// MSIMonitorController.h:70 and 92).</summary>
        internal const byte StaticMode = 0x01;

        /// <summary>Direct's brightness on the 0x72 layout
        /// (RGBController_MSIMonitor72.cpp:48-50).</summary>
        internal const byte Brightness72 = 100;

        /// <summary>The fill directions (MSIMonitorController.h:48-55).</summary>
        internal const int RightToLeft = 0, LeftToRight = 1, Circle = 3, CType = 4;

        /// <summary>The shortest time between two pictures. Direct stores
        /// nothing and no reference bounds the rate, so the bound is
        /// PadForge's (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        /// <summary>One row of OpenRGB's 0x72 layout table: the version the
        /// monitor answers, its LED count and its fill direction, all single
        /// block (msi_monitor_72_layouts, MSIMonitorControllerDetect.cpp:54-82).</summary>
        internal readonly record struct Layout72(byte Version, int Leds, int Direction);

        internal static readonly Layout72[] Layouts =
        {
            new(34, 9, LeftToRight),
            new(5, 12, RightToLeft),
            new(8, 24, Circle),
            new(10, 9, LeftToRight),
            new(12, 9, LeftToRight),
            new(14, 14, LeftToRight),
            new(17, 8, RightToLeft),
            new(20, 12, LeftToRight),
            new(21, 18, LeftToRight),
            new(22, 24, LeftToRight),
            new(24, 12, LeftToRight),
            new(25, 24, LeftToRight),
            new(28, 9, LeftToRight),
            new(29, 24, RightToLeft),
            new(30, 21, RightToLeft),
            new(32, 7, LeftToRight),
            new(33, 7, LeftToRight),
            new(36, 9, LeftToRight),
            new(37, 22, LeftToRight),
            new(38, 20, LeftToRight),
            new(39, 8, RightToLeft),
            new(40, 12, LeftToRight),
            new(41, 14, RightToLeft),
            new(42, 22, CType),
            new(43, 21, LeftToRight),
            new(44, 9, LeftToRight),
            new(46, 9, LeftToRight),
        };

        /// <summary>The 0x72 layout of a version, or null for the 0x71
        /// layout (MSIMonitorLayout, MSIMonitorControllerDetect.cpp:101-112).</summary>
        internal static Layout72? LayoutOf(int version)
        {
            foreach (var layout in Layouts)
                if (layout.Version == version) return layout;
            return null;
        }

        public string Name => "MSI Monitor";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            // The shorter layout's report has to fit the collection's feature
            // reports.
            if (collection.FeatureReportLength < PacketSize71) return null;
            var registration = registrations[0];
            return _models.Get(registration.Name, () => Model(registration.Name, collection.ProductId));
        }

        internal static string Product(ushort productId) => $"msi:{VendorId:X4}:{productId:X4}";

        /// <summary>The map of a layout: nine LEDs for the 0x71 layout, the
        /// table's count for a 0x72 one, each an edge light at its place in
        /// OpenRGB's line.</summary>
        internal static LightMap MapOf(ushort productId, Layout72? layout)
        {
            int count = layout?.Leds ?? Leds71;
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return new LightMap(lights, layout == null ? "OpenRGB RGBController_MSIMonitor.cpp" : "OpenRGB RGBController_MSIMonitor72.cpp")
            {
                Product = Product(productId) + (layout == null ? ":71" : $":72v{layout.Value.Version}"),
            };
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            // The 0x71 layout's nine LEDs until a session asks.
            Map = MapOf(productId, null),
            Programs = new[] { VendorPrograms.MsiMysticLight },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Begin(io, productId),
        };

        /// <summary>The version query's output report, 01 B0
        /// (lines 165-168).</summary>
        internal static byte[] VersionQuery()
        {
            var request = new byte[QuerySize];
            request[0] = QueryReportId;
            request[1] = GetVersion;
            return request;
        }

        /// <summary>GetLayoutVersion (MSIMonitorController.cpp:145-188): the
        /// version, or 0, MSI_MONITOR_VERSION_UNKNOWN, when the query did not
        /// go out, nothing answered in time or the answer is not 01 5A.</summary>
        internal static int AskVersion(IHidReportChannel io)
        {
            var reply = new byte[QuerySize];
            for (int i = 0; i < DrainLimit && io.ReadInput(reply, 0) > 0; i++) { }
            if (!io.WriteOutput(VersionQuery())) return 0;
            Array.Clear(reply);
            if (io.ReadInput(reply, AnswerMs) <= 0) return 0;
            return reply[1] == QueryAck ? reply[3] : 0;
        }

        /// <summary>Set's 78-byte report for the nine colors, mode 01 and last
        /// byte 00 (MSIMonitorController.cpp:73-143).</summary>
        internal static byte[] Frame71(IReadOnlyList<int> colors)
        {
            var data = new byte[PacketSize71];
            int offset = 0;
            data[offset++] = 0x71;
            data[offset++] = 0x01;
            offset += 3;
            data[offset++] = 0x01;
            data[offset++] = 0x64;
            offset += 5;
            data[offset++] = StaticMode;
            offset += 3;
            data[offset++] = 0x01;
            data[offset++] = 0x64;
            offset += 5;
            for (int i = 0; i < 9; i++)
            {
                data[offset++] = 0xFF;
                data[offset++] = 0x00;
                data[offset++] = 0x00;
            }
            for (int i = 0; i < Leds71; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                data[offset++] = (byte)(rgb >> 16);
                data[offset++] = (byte)(rgb >> 8);
                data[offset++] = (byte)rgb;
            }
            data[offset] = 0x00;
            return data;
        }

        /// <summary>SetMode72's 168-byte report for Direct
        /// (MSIMonitorController.cpp:190-263, with ApplyMode's arguments,
        /// RGBController_MSIMonitor72.cpp:508-565): <paramref name="colors"/>
        /// in OpenRGB's LED order, reversed here for a right-to-left
        /// fill.</summary>
        internal static byte[] Frame72(IReadOnlyList<int> colors, int leds, int direction)
        {
            var led = new int[leds];
            for (int i = 0; i < leds; i++) led[i] = colors != null && i < colors.Count ? colors[i] & 0xFFFFFF : 0;
            int color1 = leds > 0 ? led[0] : 0;
            if (direction == RightToLeft) Array.Reverse(led);
            var data = new byte[PacketSize72];
            data[0] = 0x72;
            for (int b = 1; b <= 12; b += 11)
            {
                data[b + 0] = StaticMode;
                data[b + 1] = (byte)(color1 >> 16);
                data[b + 2] = (byte)(color1 >> 8);
                data[b + 3] = (byte)color1;
                data[b + 4] = 0x00;
                data[b + 5] = Brightness72;
                data[b + 6] = 0x00;
                data[b + 7] = 0x00;
                data[b + 8] = 0x00;
                data[b + 9] = 0x80;
                data[b + 10] = 0x00;
            }
            for (int slot = 0; slot < ArraySlots72 && slot < leds; slot++)
            {
                int at = ArrayBIndex72 + slot * 3;
                data[at] = (byte)(led[slot] >> 16);
                data[at + 1] = (byte)(led[slot] >> 8);
                data[at + 2] = (byte)led[slot];
            }
            data[StoreIndex72] = 0x00;
            return data;
        }

        /// <summary>The version query, then a session on the layout it
        /// selects, the 0x71 layout when nothing answered, as OpenRGB's
        /// detector falls back to it.</summary>
        internal static HidLightingSession Begin(IHidReportChannel io, ushort productId)
        {
            if (io == null) return null;
            var layout = LayoutOf(AskVersion(io));
            return new Session(layout, MapOf(productId, layout));
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(Layout72? layout, LightMap map)
            {
                Layout = layout;
                _map = map;
            }

            /// <summary>The 0x72 layout, or null for the 0x71 one.</summary>
            public Layout72? Layout { get; }

            public override LightMap Map => _map;

            public override bool Feature => true;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                int count = _map.Lights.Count;
                var byLed = KeyboardLedNames.ByAddress(lights, colors, count);
                var frame = Layout is { } layout ? Frame72(byLed, layout.Leds, layout.Direction) : Frame71(byLed);
                if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                wait(PipeIdleMs);
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
