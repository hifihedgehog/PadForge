using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The keyboard (048D:CE00) and the front light bar (048D:6005) of
    /// PCSpecialist's Ionico laptops that OpenRGB's IonicoController lights
    /// (#557). The collections are OpenRGB's registrations
    /// (IonicoControllerDetect.cpp:48-49), with feature reports of
    /// <see cref="ReportSize"/> bytes and, for the light bar, output reports
    /// of <see cref="BarReportSize"/>.
    ///
    /// <para>The keyboard has four zones and the light bar 22 lights
    /// (IonicoController.h:21-22, RGBController_Ionico.cpp:138-180). A
    /// session's first picture sets the Direct mode once, as OpenRGB's
    /// DeviceUpdateMode does when the mode is picked: mode 1 for the
    /// keyboard and 0x33 for the light bar, at the Direct mode's full
    /// brightness of 50 (RGBController_Ionico.cpp:44-53 and 212-221,
    /// SetMode in IonicoController.cpp:63-74). The colors then go out as
    /// SetColors sends them: one report per keyboard zone, or for the light
    /// bar a start report, the 22 colors in red, blue, green order in an
    /// output report, and an end report (IonicoController.cpp:76-121).
    /// Direct saves nothing until OpenRGB's separate save command
    /// (SaveBios, lines 52-61), which PadForge never sends.</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the mode back
    /// or returns the keyboard to its saved effect. It shows PadForge's last
    /// colors until the laptop's own program sets others or the laptop
    /// restarts. No source names that program's process, so none is
    /// listed.</para>
    /// </summary>
    internal sealed class IonicoLighting : IHidLightingFamily
    {
        public static readonly IonicoLighting Instance = new();

        internal const string OpenRgbFamily = "IonicoController";

        internal const ushort KeyboardPid = 0xCE00;
        internal const ushort BarPid = 0x6005;

        /// <summary>IONICO_REPORT_SIZE and IONICO_DIRECT_REPORT_SIZE, each with
        /// the report ID 0 (IonicoController.h:18-19).</summary>
        internal const int ReportSize = 9;
        internal const int BarReportSize = 65;

        /// <summary>IONICO_DIRECT_CMD (IonicoController.h:20).</summary>
        internal const byte DirectCommand = 0x14;

        /// <summary>The keyboard's and the light bar's Direct modes, and the
        /// Direct mode's brightness, IONICO_DIRECT_BRIGHTNESS_MAX
        /// (IonicoController.h:24 and 32, RGBController_Ionico.cpp:212-221).</summary>
        internal const byte KeyboardDirect = 0x01;
        internal const byte BarDirect = 0x33;
        internal const byte Brightness = 50;

        internal const int KeyboardLeds = 4;
        internal const int BarLeds = 22;

        /// <summary>The shortest time between two pictures, PadForge's bound
        /// for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Ionico";

        private static readonly Lazy<HidLightingModel> s_keyboard = new(() => Model("Ionico Keyboard", KeyboardPid));
        private static readonly Lazy<HidLightingModel> s_bar = new(() => Model("Ionico Light Bar", BarPid));

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < ReportSize) return null;
            if (OpenRgbRegistrations.Match(OpenRgbFamily, collection).Count == 0) return null;
            if (collection.ProductId == KeyboardPid) return s_keyboard.Value;
            if (collection.ProductId == BarPid && collection.OutputReportLength >= BarReportSize) return s_bar.Value;
            return null;
        }

        private static HidLightingModel Model(string name, ushort productId)
        {
            bool bar = productId == BarPid;
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(Lights(bar), "OpenRGB IonicoController") { Product = $"ionico:048D:{productId:X4}" },
                RepaintMs = RepaintMs,
                Begin = (_, _) => new Session(bar),
            };
        }

        /// <summary>"Keyboard Zone 1" to 4, each a zone, or "Bar Led 1" to
        /// 22, each an edge light, the address its place.</summary>
        internal static IReadOnlyList<LightInfo> Lights(bool bar)
        {
            var lights = new LightInfo[bar ? BarLeds : KeyboardLeds];
            for (int i = 0; i < lights.Length; i++)
                lights[i] = bar
                    ? new LightInfo(i, LightKind.Edge, 0, LightKeySource.None)
                    : new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        /// <summary>SetMode's report: 08 02, the mode, the speed 0, the
        /// brightness and 08.</summary>
        internal static byte[] ModeFrame(byte mode)
            => new byte[] { 0, 0x08, 0x02, mode, 0, Brightness, 0x08, 0, 0 };

        /// <summary>A keyboard zone's report: 14, 00, the zone from 1, the
        /// color.</summary>
        internal static byte[] ZoneFrame(int zone, int rgb)
            => new byte[] { 0, DirectCommand, 0, (byte)zone, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0, 0 };

        /// <summary>The light bar's start and end reports, 12 with 00 or
        /// 01 in byte 3.</summary>
        internal static byte[] BarMark(bool end) => new byte[] { 0, 0x12, 0, (byte)(end ? 1 : 0), 0, 0, 0, 0, 0 };

        /// <summary>The light bar's output report: red, blue and green of
        /// each light from byte 1. The 22nd light's red is the report's last
        /// byte: OpenRGB writes its blue and green past the end of its
        /// 65-byte buffer (IonicoController.cpp:105-112), so no report
        /// carries them and they are left out here.</summary>
        internal static byte[] BarColors(IReadOnlyList<int> colors)
        {
            var report = new byte[BarReportSize];
            int count = Math.Min(colors?.Count ?? 0, BarLeds);
            for (int i = 0; i < count; i++)
            {
                Put(report, 1 + 3 * i, (byte)(colors[i] >> 16));
                Put(report, 2 + 3 * i, (byte)colors[i]);
                Put(report, 3 + 3 * i, (byte)(colors[i] >> 8));
            }
            return report;
        }

        private static void Put(byte[] report, int at, byte value)
        {
            if (at < report.Length) report[at] = value;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _bar;
            private bool _direct;

            public Session(bool bar) => _bar = bar;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct)
                {
                    if (!io.SetFeature(ModeFrame(_bar ? BarDirect : KeyboardDirect))) return HidLightingPaint.Failed;
                    _direct = true;
                }
                if (_bar)
                {
                    var ordered = new int[BarLeds];
                    int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                    for (int n = 0; n < count; n++)
                        if (lights[n].Address >= 0 && lights[n].Address < BarLeds) ordered[lights[n].Address] = colors[n];
                    return io.SetFeature(BarMark(false)) && io.WriteOutput(BarColors(ordered)) && io.SetFeature(BarMark(true))
                        ? HidLightingPaint.Sent
                        : HidLightingPaint.Failed;
                }
                int zones = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int n = 0; n < zones; n++)
                {
                    int zone = lights[n].Address;
                    if (zone < 0 || zone >= KeyboardLeds) continue;
                    if (!io.SetFeature(ZoneFrame(zone + 1, colors[n]))) return HidLightingPaint.Failed;
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
