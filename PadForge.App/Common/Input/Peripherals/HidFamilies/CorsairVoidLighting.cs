using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Corsair Void Pro headsets, lit as OpenRGB's CorsairVoidController
    /// lights them: the collection on usage page FFC5, usage 1, of 1B1C:0A14,
    /// 0A1A, 0A17 and 0A1D (CorsairVoidControllerDetect.cpp:25-35 and 55-58). Its two
    /// lights are the two earcups, each a zone of one LED
    /// (RGBController_CorsairVoid.cpp:57-80).
    ///
    /// <para>The frames. The lights are taken from the headset's firmware with
    /// report C8 and 01, and a picture is report CB, 06, then three pairs of a
    /// register and its value for each earcup: 1C red, 16 green and 17 blue on
    /// the left, 1D red, 18 green and 19 blue on the right
    /// (SetLightingControl and SetColors, CorsairVoidController.cpp:61-113,
    /// the registers in CorsairVoidController.h:45-50). The lights are taken
    /// before the first picture and again before a picture once a second has
    /// passed since the last time, in case the headset was switched off and on
    /// in the meantime (CORSAIR_VOID_LIGHTING_REFRESH_MS, line 34). SignalRGB's
    /// plugins send the same reports (Corsair_Void_Pro_HeadSet_0x0A14.js and
    /// its siblings), and light the IDs OpenRGB lacks
    /// (<see cref="PluginDeviceLighting"/>).</para>
    ///
    /// <para>Hand-back. C8 and 00 give the lights back to the firmware, which
    /// brings back the headset's own effects and its mute light (the
    /// destructor, CorsairVoidController.cpp:24-36). It is also the crash
    /// frame once the lights were taken.</para>
    ///
    /// <para>No reference bounds how often a picture may go out. The bound is
    /// SignalRGB's 30 ms after each report (<see cref="PluginDeviceLighting.VoidWaitMs"/>).
    /// Corsair iCUE lights these headsets while it runs.</para>
    /// </summary>
    internal sealed class CorsairVoidLighting : IHidLightingFamily
    {
        public static readonly CorsairVoidLighting Instance = new();

        internal const string OpenRgbFamily = "CorsairVoidController";

        /// <summary>CORSAIR_VOID_LIGHTING_REPORT_ID and its two owners
        /// (CorsairVoidController.h:24-27).</summary>
        internal const byte ControlReport = 0xC8;
        internal const byte Firmware = 0x00;
        internal const byte Host = 0x01;

        /// <summary>CORSAIR_VOID_REGISTER_REPORT_ID and CORSAIR_VOID_REGISTER_REPORT_SIZE
        /// (lines 40-41).</summary>
        internal const byte ColorReport = 0xCB;
        internal const int ColorReportSize = 20;

        /// <summary>CORSAIR_VOID_LIGHTING_REPORT_SIZE (line 25).</summary>
        internal const int ControlReportSize = 3;

        /// <summary>CORSAIR_VOID_LIGHTING_REFRESH_MS (line 34).</summary>
        internal const int RefreshMs = 1000;

        public string Name => "Corsair (Void Pro)";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null || collection.OutputReportLength < ColorReportSize) return null;
            return _models.Get(r.Name + ":" + r.ProductId, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
        };

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_CorsairVoid.cpp") { Product = $"corsair:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.CorsairIcue },
            RepaintMs = PluginDeviceLighting.VoidWaitMs,
            Begin = (_, _) => new Session(() => Environment.TickCount64),
        };

        /// <summary>SetLightingControl: C8, the owner.</summary>
        internal static byte[] Control(byte owner) => new byte[] { ControlReport, owner, 0x00 };

        /// <summary>SetColors: CB 06, then each register and its value.</summary>
        internal static byte[] Colors(int left, int right)
        {
            var report = new byte[ColorReportSize];
            report[0] = ColorReport;
            report[1] = 0x06;
            report[2] = 0x1C; report[3] = LightReports.Red(left);
            report[4] = 0x16; report[5] = LightReports.Green(left);
            report[6] = 0x17; report[7] = LightReports.Blue(left);
            report[8] = 0x1D; report[9] = LightReports.Red(right);
            report[10] = 0x18; report[11] = LightReports.Green(right);
            report[12] = 0x19; report[13] = LightReports.Blue(right);
            return report;
        }

        /// <summary>A Void Pro's session: the lights taken before a picture when
        /// they were never taken or a second has passed, the picture, and the
        /// lights given back.</summary>
        internal sealed class Session : HidLightingSession
        {
            // Made once: the backend tells an unchanged record by reference
            // (HidLightingBackend.Track), and a new array at every read had
            // it take the headset off the record and put it back after
            // every picture.
            private static readonly byte[][] s_handBack = { Control(Firmware) };

            private readonly Func<long> _now;
            private bool _taken;
            private long _takenAt;

            public Session(Func<long> now) => _now = now;

            public override bool Feature => false;

            public override byte[][] CrashFrames => _taken ? s_handBack : Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                long now = _now();
                if (!_taken || now - _takenAt >= RefreshMs)
                {
                    if (!io.WriteOutput(Control(Host))) return HidLightingPaint.Failed;
                    _taken = true;
                    _takenAt = now;
                }
                var byAddress = KeyboardLedNames.ByAddress(lights, colors, 2);
                return io.WriteOutput(Colors(byAddress[0], byAddress[1])) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
                => !_taken || io.WriteOutput(Control(Firmware));
        }
    }
}
