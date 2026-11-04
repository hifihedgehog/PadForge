using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// HyperX's QuadCast 2 S microphone, lit as OpenRGB's
    /// HyperXMicrophoneV2Controller lights it (#557), LED by LED. The
    /// collection is OpenRGB's registration, interface 1, usage page 0xFF13,
    /// usage 0xFF00 (HyperXMicrophoneV2ControllerDetect.cpp:41).
    ///
    /// <para>The lights. 108 LEDs in a matrix of 9 rows and 12 columns, the
    /// front's six columns and the rear's six, each cell holding the LED
    /// OpenRGB's led_mapping places there (RGBController_HyperXMicrophoneV2.cpp:65-101).
    /// The microphone takes the colors in LED order, 0 to 107
    /// (SendColorPackets, HyperXMicrophoneV2Controller.cpp:152-180), so each
    /// light's address is its LED's number and its place is the cell that
    /// holds it, one unit a cell. They are lights of the microphone's
    /// body, neither keys nor zones.</para>
    ///
    /// <para>The frames. A picture is OpenRGB's Direct mode (SendDirect,
    /// lines 182-213): an output report of 64 bytes, 44 01 06, then six of
    /// 44 02, the report's number, 00 and up to twenty colors, and after
    /// each report a read of the microphone's answer, which echoes the
    /// report's first two bytes at bytes 14 and 15 (WaitForResponse, lines
    /// 113-150). OpenRGB waits up to two seconds for an answer and goes on
    /// after a wrong one or none, and after ten errors in ten seconds it
    /// pauses five seconds on the guess that NGENUITY holds the microphone
    /// (TrackCommunicationError, lines 85-111). Here the read is bounded the
    /// same, an answer that does not echo the report goes by as OpenRGB lets
    /// it, and no answer at all fails the picture, so the worker closes the
    /// channel and tries again later rather than stall its other devices
    /// for fourteen seconds a picture. The save (03 01 06 and the reports
    /// after it, SaveColors, lines 215-271) goes out only for OpenRGB's
    /// manual Save, and never here. The microphone keeps the colors only
    /// while they keep coming: the controller sends the last picture again
    /// once none went out for a second, checked every 250 ms
    /// (KeepaliveThread, RGBController_HyperXMicrophoneV2.cpp:127-137),
    /// which the worker does as the session's keep-alive.</para>
    ///
    /// <para>Hand-back: none. OpenRGB's Shutdown sends nothing, and no other
    /// reference drives this microphone. When the frames stop it is taken
    /// to go back to the lighting it stores, as the keep-alive implies. That
    /// has not been seen on a device.</para>
    /// </summary>
    internal sealed class HyperXMicrophoneV2Lighting : IHidLightingFamily
    {
        public static readonly HyperXMicrophoneV2Lighting Instance = new();

        internal const string OpenRgbFamily = "HyperXMicrophoneV2Controller";

        /// <summary>HYPERX_HP_VID (HyperXMicrophoneV2ControllerDetect.cpp:20).</summary>
        internal const ushort HpVid = 0x03F0;

        /// <summary>HYPERX_QUADCAST_2S_PACKET_SIZE, _REPORT_ID,
        /// _LEDS_PER_PACKET, _MATRIX_WIDTH, _MATRIX_HEIGHT and _TOTAL_LEDS
        /// (HyperXMicrophoneV2Controller.h:19-24).</summary>
        internal const int ReportSize = 64;
        internal const byte ReportId = 0x44;
        internal const int LedsPerReport = 20;
        internal const int Columns = 12;
        internal const int Rows = 9;
        internal const int LedCount = 108;

        /// <summary>The color reports a picture sends
        /// (HyperXMicrophoneV2Controller.cpp:157).</summary>
        internal const int ColorReports = 6;

        /// <summary>WaitForResponse's bound (HyperXMicrophoneV2Controller.h:47).</summary>
        internal const int AnswerMs = 2000;

        /// <summary>The keep-alive (RGBController_HyperXMicrophoneV2.cpp:131).</summary>
        internal const int KeepAliveMs = 1000;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it beyond the keep-alive, so the bound is PadForge's.</summary>
        internal const int RepaintMs = HyperXKeyboardLighting.RepaintMs;

        public string Name => "HyperX QuadCast 2 S";

        /// <summary>led_mapping: the LED in each cell, row by row
        /// (RGBController_HyperXMicrophoneV2.cpp:67-79).</summary>
        internal static readonly int[] Matrix =
        {
            26, 27, 44, 45, 62, 63,   80, 81, 98,  99, 8,  9,
            25, 28, 43, 46, 61, 64,   79, 82, 97, 100, 7, 10,
            24, 29, 42, 47, 60, 65,   78, 83, 96, 101, 6, 11,
            23, 30, 41, 48, 59, 66,   77, 84, 95, 102, 5, 12,
            22, 31, 40, 49, 58, 67,   76, 85, 94, 103, 4, 13,
            21, 32, 39, 50, 57, 68,   75, 86, 93, 104, 3, 14,
            20, 33, 38, 51, 56, 69,   74, 87, 92, 105, 2, 15,
            19, 34, 37, 52, 55, 70,   73, 88, 91, 106, 1, 16,
            18, 35, 36, 53, 54, 71,   72, 89, 90, 107, 0, 17,
        };

        private readonly object _gate = new();
        private HidLightingModel _model;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != HpVid) return null;
            if (collection.OutputReportLength < ReportSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            lock (_gate) return _model ??= Model(registrations[0], collection.ProductId);
        }

        internal static HidLightingModel Model(OpenRgbRegistration registration, ushort productId) => new()
        {
            Family = Instance,
            Name = registration.Name,
            Map = new LightMap(Lights(), "OpenRGB HyperXMicrophoneV2Controller")
            {
                Product = $"hyperx:{registration.VendorId:X4}:{productId:X4}",
            },
            Programs = new[] { VendorPrograms.Ngenuity },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            KeepAliveIdleMs = KeepAliveMs,
            Begin = (io, wait) => io == null ? null : new Session(),
        };

        /// <summary>The 108 lights by LED number, each placed at the cell
        /// that holds it.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[LedCount];
            for (int cell = 0; cell < Matrix.Length; cell++)
            {
                int led = Matrix[cell];
                lights[led] = new LightInfo(led, LightKind.Other, 0, LightKeySource.None)
                {
                    X = cell % Columns, Y = cell / Columns, Width = 1, Height = 1, HasPosition = true,
                };
            }
            return lights;
        }

        /// <summary>The direct header, 44 01 06 (HyperXMicrophoneV2Controller.cpp:200-204).</summary>
        internal static byte[] Header()
        {
            var frame = new byte[ReportSize];
            frame[0] = ReportId;
            frame[1] = 0x01;
            frame[2] = 0x06;
            return frame;
        }

        /// <summary>The six color reports, 44 02, the report's number, 00,
        /// then twenty colors of the 108 in order, the last report eight
        /// (SendColorPackets, lines 152-180).</summary>
        internal static byte[][] ColorFrames(int[] colors)
        {
            var frames = new byte[ColorReports][];
            int sent = 0;
            for (int report = 0; report < ColorReports; report++)
            {
                var frame = new byte[ReportSize];
                frame[0] = ReportId;
                frame[1] = 0x02;
                frame[2] = (byte)report;
                for (int c = 0; c < LedsPerReport && sent < LedCount; c++, sent++)
                {
                    frame[4 + 3 * c] = (byte)(colors[sent] >> 16);
                    frame[5 + 3 * c] = (byte)(colors[sent] >> 8);
                    frame[6 + 3 * c] = (byte)colors[sent];
                }
                frames[report] = frame;
            }
            return frames;
        }

        /// <summary>Whether an answer echoes the report it follows: its bytes
        /// 14 and 15 are the report's first two (lines 131-134).</summary>
        internal static bool Echoes(byte[] answer, int length, byte[] sent)
            => length > 15 && answer[14] == sent[0] && answer[15] == sent[1];

        internal sealed class Session : HidLightingSession
        {
            private readonly byte[] _answer = new byte[ReportSize];

            public override bool Feature => false;

            /// <summary>Nothing hands the microphone back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>How many answers did not echo their report, for the
            /// log and the tests.</summary>
            public int Mismatches { get; private set; }

            /// <summary>One report and the answer read after it. False when
            /// the write failed or no answer came.</summary>
            private bool Exchange(IHidReportChannel io, byte[] frame)
            {
                if (!io.WriteOutput(frame)) return false;
                int read = io.ReadInput(_answer, AnswerMs);
                if (read <= 0) return false;
                if (!Echoes(_answer, read, frame)) Mismatches++;
                return true;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (!Exchange(io, Header())) return HidLightingPaint.Failed;
                foreach (var frame in ColorFrames(CoolerMasterLighting.ByAddress(lights, colors, LedCount)))
                    if (!Exchange(io, frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
