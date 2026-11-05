using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Kraken V3 headsets, lit as OpenRGB's RazerKrakenV3Controller
    /// lights them (#557): the Kraken V3 HyperSense (0533), V3 X (0537), V3
    /// (0549) and Kitty V2 Pro (0554), each on the consumer collection of
    /// interface 3 (RazerControllerDetect.cpp:438-441, read into
    /// <see cref="RazerHidTable"/>). openrazer has no driver for them.
    ///
    /// <para>The wire. Every report is the 15-byte output report 40: the
    /// command, then 13 arguments (RazerKrakenV3Controller.h:19-39), written
    /// with hid_write (RazerKrakenV3Controller.cpp:105, 128, 142 and 157).
    /// The commands are 01 the mode, 02 the brightness and 03 the colors
    /// (h:21-26). OpenRGB's Direct mode sends the mode report with 0F and
    /// mode 08 in arguments 1 and 2, then the brightness, 255 for every
    /// product (SetModeDirect, cpp:93-106, SetBrightness, 131-143,
    /// GetMaxBrightness, 81-91, and DeviceUpdateMode,
    /// RGBController_RazerKrakenV3.cpp:159-166, with the Direct mode's
    /// brightness at its maximum, line 51). Each picture after is SetDirect:
    /// the colors from argument 1, red, green and blue for each of the
    /// product's columns (cpp:108-129, RGBController_RazerKrakenV3.cpp:141-147).
    /// A session sends the mode and the brightness before its first
    /// picture.</para>
    ///
    /// <para>The lights are the product's razer_device zones: one Headset
    /// light on the three V3 headsets, and on the Kitty V2 Pro the two cat
    /// ears and the two logos (RazerDevices.cpp:8469-8641). SetDirect sends
    /// as many colors as the entry has columns, 1 and 4.</para>
    ///
    /// <para>The hand-back. No reference reads the mode back. OpenRGB says
    /// "By default, the device starts as Wave/Spectrum Cycle"
    /// (RGBController_RazerKrakenV3.cpp:29), so the hand-back is its Wave
    /// mode: the mode report with 0F and mode 03, then the brightness at
    /// 255 (SetModeWave, cpp:145-158, and DeviceUpdateMode, lines 168-171,
    /// Wave's brightness at its maximum, line 40). A headset whose own
    /// lighting was another effect shows Wave after it. Nothing in OpenRGB
    /// says the mode report is stored on the headset.</para>
    ///
    /// <para>The headsets are Razer's, so the linker's Razer row reaches
    /// them and closes their path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).
    /// The model names Synapse too, so the worker sends them nothing while
    /// it runs, and no hand-back lands on Synapse's colors.</para>
    /// </summary>
    internal sealed class RazerKrakenV3Lighting : IHidLightingFamily
    {
        public static readonly RazerKrakenV3Lighting Instance = new();

        internal const string Detector = "DetectRazerKrakenV3Controllers";

        /// <summary>The report ID and the report's length with it
        /// (RazerKrakenV3Controller.h:19 and 34-39).</summary>
        internal const byte ReportId = 0x40;
        internal const int ReportLength = 15;

        /// <summary>The commands and modes (RazerKrakenV3Controller.h:21-32).</summary>
        internal const byte CommandMode = 0x01;
        internal const byte CommandBrightness = 0x02;
        internal const byte CommandColors = 0x03;
        internal const byte ModeDirect = 0x08;
        internal const byte ModeWave = 0x03;

        /// <summary>GetMaxBrightness (RazerKrakenV3Controller.cpp:81-91).</summary>
        internal const byte MaxBrightness = 0xFF;

        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer Kraken V3";

        private readonly Dictionary<(string, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null || collection.OutputReportLength < ReportLength) return null;
            lock (_models)
            {
                var key = (registration.Name, collection.ProductId);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration.Name, collection.ProductId);
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId)
        {
            var map = RazerHidTable.MapOf(productId);
            int columns = RazerHidTable.Device(productId)?.Columns ?? 1;
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Programs = VendorPrograms.Razer,
                Map = map,
                RepaintMs = RepaintMs,
                Begin = (io, wait) => io == null ? null : new Session(columns),
            };
        }

        /// <summary>razer_kraken_create_v3_report with the command and its
        /// arguments from argument 0 (RazerKrakenV3Controller.cpp:185-197).</summary>
        internal static byte[] Report(byte command, ReadOnlySpan<byte> arguments)
        {
            var report = new byte[ReportLength];
            report[0] = ReportId;
            report[1] = command;
            arguments.CopyTo(report.AsSpan(2));
            return report;
        }

        /// <summary>SetModeDirect and SetModeWave: 0F and the mode in
        /// arguments 1 and 2 (cpp:93-106 and 145-158).</summary>
        internal static byte[] ModeReport(byte mode) => Report(CommandMode, stackalloc byte[] { 0x00, 0x0F, mode });

        /// <summary>SetBrightness: the brightness in argument 2 (cpp:131-143).</summary>
        internal static byte[] BrightnessReport(byte brightness)
            => Report(CommandBrightness, stackalloc byte[] { 0x00, 0x00, brightness });

        /// <summary>SetDirect: red, green and blue for each column from
        /// argument 1 (cpp:108-129). A color the picture lacks is black.</summary>
        internal static byte[] ColorsReport(int columns, IReadOnlyList<int> colors)
        {
            var arguments = new byte[13];
            for (int i = 0; i < columns && 3 + 3 * i < arguments.Length; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                arguments[1 + 3 * i] = (byte)(rgb >> 16);
                arguments[2 + 3 * i] = (byte)(rgb >> 8);
                arguments[3 + 3 * i] = (byte)rgb;
            }
            return Report(CommandColors, arguments);
        }

        /// <summary>The Direct mode's two reports, and the Wave mode's, the
        /// hand-back.</summary>
        internal static byte[][] DirectMode() => new[] { ModeReport(ModeDirect), BrightnessReport(MaxBrightness) };

        internal static byte[][] WaveMode() => new[] { ModeReport(ModeWave), BrightnessReport(MaxBrightness) };

        internal sealed class Session : HidLightingSession
        {
            private readonly int _columns;
            private bool _direct;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(int columns) => _columns = columns;

            public override bool Feature => false;

            /// <summary>The Wave mode, once the Direct mode may have gone
            /// out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct)
                {
                    // Marked first: a write that reports a failure may still
                    // have landed.
                    _crash = WaveMode();
                    foreach (var report in DirectMode())
                        if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    _direct = true;
                }
                return io.WriteOutput(ColorsReport(_columns, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                foreach (var report in _crash)
                    if (!io.WriteOutput(report)) return false;
                _direct = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
