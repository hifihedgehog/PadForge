using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's AsusMonitorController family (#557): the ROG Strix
    /// XG27AQDMG, XG27ACDNG and XG27UCG, the ROG Swift PG32UCDM, PG32UCDMR
    /// and PG32UCDP, and the ROG Aura Monitor Light Bar, each on usage page
    /// 0xFF72, usage 0xA1, interface 1, the light bar on interface 2
    /// (AsusMonitorControllerDetect.cpp:50-59).
    ///
    /// <para>The protocol. Every frame is a 65-byte output report with
    /// report ID EC (ASUS_MONITOR_REPORT_SIZE, AsusMonitorController.h:18).
    /// A session starts as OpenRGB's controller does: EC B0, then the answer,
    /// whose byte 32 is the number of LEDs (GetNumberOfLEDs,
    /// AsusMonitorController.cpp:51-67), then EC 35 with FF at 5 and 01 at 8
    /// (SendInit, lines 69-81, sent from RGBController_AsusMonitor.cpp:36).
    /// The LEDs are one line, "Monitor", and the session's lights
    /// (<see cref="HidLightingSession.Map"/>, SetupZones, lines 54-75), edge
    /// lights here, as PadForge lights a Cooler Master monitor's back LEDs.
    /// OpenRGB reads the answer with no bound (hid_read), PadForge for at
    /// most <see cref="AnswerMs"/>, as <see cref="AsusAuraUsbLighting.AnswerMs"/>
    /// does.</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode, its only mode: EC 40 84 00,
    /// the number of colors, and each color's red, green and blue from byte 5
    /// (SetDirect, AsusMonitorController.cpp:83-102). The frame holds 20
    /// colors. OpenRGB writes past its buffer for a monitor that reports
    /// more, so PadForge lights the first 20.</para>
    ///
    /// <para>What the writes store, and the hand-back. OpenRGB marks the
    /// controller as one that saves nothing ("@save :x:",
    /// RGBController_AsusMonitor.cpp:18) and has no save frame. No reference
    /// reads the monitor's own effect or returns it, so nothing goes out at
    /// hand-back, and the monitor shows PadForge's last colors until Armoury
    /// Crate or its own menu sets others. ASUS lights its monitors through
    /// Armoury Crate ("The Aura Sync feature is supported by Armoury Crate
    /// application", ASUS FAQ 1044426), and while it runs they are left to it
    /// (<see cref="VendorPrograms.ArmouryCrate"/>).</para>
    /// </summary>
    internal sealed class AsusMonitorLighting : IHidLightingFamily
    {
        public static readonly AsusMonitorLighting Instance = new();

        internal const string OpenRgbFamily = "AsusMonitorController";

        /// <summary>ASUS_VID (AsusMonitorControllerDetect.cpp:19).</summary>
        internal const ushort VendorId = 0x0B05;

        /// <summary>ASUS_MONITOR_REPORT_SIZE (AsusMonitorController.h:18)
        /// and the report ID every frame starts with.</summary>
        internal const int ReportSize = 65;
        internal const byte ReportId = 0xEC;

        /// <summary>The byte of the answer to EC B0 that holds the LED
        /// count (AsusMonitorController.cpp:66).</summary>
        internal const int CountAt = 32;

        /// <summary>The colors one frame holds: three bytes each from byte
        /// 5 of 65.</summary>
        internal const int MaxLeds = (ReportSize - 5) / 3;

        /// <summary>How long the count read waits. OpenRGB's hid_read waits
        /// with no bound.</summary>
        internal const int AnswerMs = AsusAuraUsbLighting.AnswerMs;

        /// <summary>The shortest time between two pictures. Direct saves
        /// nothing and no reference bounds the rate, so the bound is
        /// PadForge's (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "ASUS Monitor";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            return _models.Get(registration.Name, () => Model(registration.Name, collection.ProductId));
        }

        internal static string Product(ushort productId) => $"asus:{VendorId:X4}:{productId:X4}";

        /// <summary>The map of a monitor with <paramref name="count"/> LEDs,
        /// at most <see cref="MaxLeds"/>, each an edge light at its place in
        /// the line.</summary>
        internal static LightMap MapOf(ushort productId, int count)
        {
            count = Math.Clamp(count, 0, MaxLeds);
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return new LightMap(lights, "OpenRGB RGBController_AsusMonitor.cpp") { Product = Product(productId) + ":" + count };
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            // One light until a session reads the count.
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Edge, 0, LightKeySource.None) },
                "OpenRGB RGBController_AsusMonitor.cpp, LED count not asked yet")
            {
                Product = Product(productId),
            },
            Programs = new[] { VendorPrograms.ArmouryCrate },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Begin(io, productId),
        };

        /// <summary>EC B0, the count read (GetNumberOfLEDs).</summary>
        internal static byte[] CountRequest()
        {
            var frame = new byte[ReportSize];
            frame[0] = ReportId;
            frame[1] = 0xB0;
            return frame;
        }

        /// <summary>SendInit's frame: EC 35, FF at 5, 01 at 8.</summary>
        internal static byte[] InitFrame()
        {
            var frame = new byte[ReportSize];
            frame[0] = ReportId;
            frame[1] = 0x35;
            frame[5] = 0xFF;
            frame[8] = 0x01;
            return frame;
        }

        /// <summary>SetDirect's frame for <paramref name="colors"/> in LED
        /// order.</summary>
        internal static byte[] DirectFrame(IReadOnlyList<int> colors)
        {
            var frame = new byte[ReportSize];
            frame[0] = ReportId;
            frame[1] = 0x40;
            frame[2] = 0x84;
            int count = Math.Min(colors?.Count ?? 0, MaxLeds);
            frame[4] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                frame[5 + 3 * i] = (byte)(colors[i] >> 16);
                frame[5 + 3 * i + 1] = (byte)(colors[i] >> 8);
                frame[5 + 3 * i + 2] = (byte)colors[i];
            }
            return frame;
        }

        /// <summary>The count read, then SendInit. Null when the monitor did
        /// not answer the read or a write failed, and then SendInit is not
        /// sent.</summary>
        internal static HidLightingSession Begin(IHidReportChannel io, ushort productId)
        {
            if (io == null || !io.WriteOutput(CountRequest())) return null;
            var answer = new byte[ReportSize];
            if (io.ReadInput(answer, AnswerMs) <= 0) return null;
            int count = answer[CountAt];
            if (!io.WriteOutput(InitFrame())) return null;
            return new Session(MapOf(productId, count));
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(LightMap map) => _map = map;

            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var byLed = KeyboardLedNames.ByAddress(lights, colors, _map.Lights.Count);
                return io.WriteOutput(DirectFrame(byLed)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
