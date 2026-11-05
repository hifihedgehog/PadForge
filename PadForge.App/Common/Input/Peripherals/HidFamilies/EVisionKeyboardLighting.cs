using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The EVision keyboards, Sonix-made boards sold under many names, lit
    /// as OpenRGB's EVisionKeyboardController lights them (#557): the
    /// collection on interface 1 and usage page 0xFF1C of each registered
    /// product (EVisionKeyboardControllerDetect.cpp:69-81,
    /// EVisionV2KeyboardControllerDetect.cpp:89-90). Two protocols.
    ///
    /// <para>The first generation (13 products) is the Cherry's protocol
    /// with its own command numbers: OpenRGB has no direct mode for it
    /// (@direct :x:, RGBController_EVisionKeyboard.cpp:23-32) and lights it
    /// in its Custom mode 0x14, the mode report once and then the colors in
    /// seven reports of command 0x11 (lines 336-389,
    /// EVisionKeyboardController.cpp:52-259). The keyboard saves both, as
    /// the Cherry does (MODE_FLAG_AUTOMATIC_SAVE and @save :robot:), so each
    /// picture is held and written rarely, and nothing goes out at the
    /// hand-back: no reference reads the mode or the colors back, and the
    /// keyboard keeps the last colors PadForge sent in its Custom mode. The
    /// sum is added unsigned, as the second generation's code adds it
    /// (EVisionV2KeyboardController.cpp:132-138) and as CHERRY Utility does
    /// on the same protocol (<see cref="CherryKeyboardLighting"/>), where
    /// OpenRGB's first-generation code adds signed chars
    /// (EVisionKeyboardController.cpp:122-136).</para>
    ///
    /// <para>The second generation, the BYGG CSB ICL01 (320F:5041) and the
    /// Endorfy Omnis, takes colors as they come. A session reads the
    /// capabilities report, whose byte 5 is the size of the color frame in
    /// keys (EVisionV2KeyboardController.cpp:51-86), and sends each picture
    /// as dynamic colors, command 0x12, each LED's color at the place its
    /// key value map gives (SetLedsDirect, lines 371-389). Every report is a
    /// query the keyboard answers with an echo of the command, the offset
    /// and the sum, and a status byte 0 (Query, lines 116-177). A query the
    /// keyboard refuses ends that write, as Write stops at its first error
    /// (lines 210-225), and costs that frame alone: OpenRGB drops Write's and
    /// Query's results and goes on at the next frame (lines 386, 404 and 409),
    /// and the session sends the picture again after the refresh interval. An
    /// answer that does not come in time costs nothing but its wait
    /// (<see cref="SonixKeyboard.AnswerMs"/>). Only a write or a read that fails
    /// ends the session. The capabilities have to answer. Dynamic
    /// colors are not stored. While they show, OpenRGB sends one byte of
    /// them again whenever 200 ms pass without a picture or a byte
    /// (RGBController_EVisionV2Keyboard.cpp:1009-1031), and the worker sends
    /// that byte on the same terms as the session's keep-alive
    /// (<see cref="RefreshMs"/>, <see cref="HidLightingModel.KeepAliveMs"/>). Hand-back: the end
    /// of dynamic colors, command 0x13 (EndLedsDirect, line 407-410), which
    /// OpenRGB sends when its Direct mode ends, and the keyboard shows its
    /// own effect again. The board's logo and edge lights take stored modes
    /// only (SetupLogoEdgeModes and SetMode, lines 301-369), which the family
    /// does not write.</para>
    ///
    /// <para>The boards are sold under many brands, with no one program that
    /// lights them all, so the family names none.</para>
    /// </summary>
    internal sealed class EVisionKeyboardLighting : IHidLightingFamily
    {
        public static readonly EVisionKeyboardLighting Instance = new();

        internal const string FamilyName = "EVisionKeyboardController";
        internal const string V2Detector = "DetectEVisionV2Keyboards";
        internal const string EndorfyDetector = "DetectEndorfyKeyboards";

        /// <summary>The first generation's Custom mode, brightness and color
        /// command (EVisionKeyboardController.h:19-62,
        /// RGBController_EVisionKeyboard.cpp:48-56).</summary>
        internal const byte ModeCustom = 0x14;
        internal const byte BrightnessHighest = 0x04;
        internal const byte WriteCustomColors = 0x11;
        internal const byte SetParameter = 0x06;

        /// <summary>The second generation's commands (EVisionV2KeyboardController.h:72-84).</summary>
        internal const byte ReadCapabilities = 0x03;
        internal const byte SendDynamicColors = 0x12;
        internal const byte EndDynamicColors = 0x13;
        internal const int CapabilitiesLength = 7;

        /// <summary>The byte RefreshLedDirect sends: one byte at the first
        /// blank place, key 6 (EVisionV2KeyboardController.cpp:17 and
        /// 401-405).</summary>
        internal const int BlankSpace = 6;

        /// <summary>How long dynamic colors go without a byte before one is
        /// sent again (RGBController_EVisionV2Keyboard.cpp:1016).</summary>
        internal const int RefreshMs = 200;

        /// <summary>The shortest time between two pictures of the second
        /// generation. A picture is up to seven queries and no reference
        /// bounds the rate, so the bound is PadForge's.</summary>
        internal const int RepaintMs = 50;

        public string Name => "EVision";

        private readonly Dictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            foreach (var registration in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                lock (_gate)
                {
                    if (!_models.TryGetValue(registration.Name, out var model))
                        _models[registration.Name] = model = Model(registration);
                    return model;
                }
            }
            return null;
        }

        private HidLightingModel Model(OpenRgbRegistration registration)
        {
            string product = $"evision:{registration.VendorId:X4}:{registration.ProductId:X4}";
            if (registration.Detector is V2Detector or EndorfyDetector)
            {
                bool endorfy = registration.Detector == EndorfyDetector;
                var layout = endorfy ? MatrixKeyboardLayouts.Endorfy : MatrixKeyboardLayouts.EVisionV2;
                byte[] values = endorfy ? MatrixKeyboardLayouts.EndorfyValues : MatrixKeyboardLayouts.EVisionV2Values;
                return new HidLightingModel
                {
                    Family = this,
                    Name = registration.Name,
                    Map = new LightMap(MatrixKeyboardLayouts.Lights(layout), "OpenRGB " + layout.Source) { Product = product },
                    RepaintMs = RepaintMs,
                    KeepAliveMs = RefreshMs,
                    KeepAliveIdleMs = RefreshMs,
                    Begin = (io, wait) => BeginV2(io, values, layout.Count),
                };
            }
            var first = MatrixKeyboardLayouts.EVision;
            return new HidLightingModel
            {
                Family = this,
                Name = registration.Name,
                Map = new LightMap(MatrixKeyboardLayouts.Lights(first), "OpenRGB " + first.Source) { Product = product },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = (io, wait) => new SonixCustomSession(
                    SonixKeyboard.Parameter(SetParameter, new byte[] { ModeCustom, BrightnessHighest, 0, 0, 0, 0, 0, 0 }, cherry: false),
                    WriteCustomColors, first.Count),
            };
        }

        // ─────────────────────────────────────────────
        //  The second generation
        // ─────────────────────────────────────────────

        /// <summary>What came of a query.</summary>
        internal enum V2Answer
        {
            /// <summary>The keyboard answered it with status 0.</summary>
            Good,

            /// <summary>No answer came in time.</summary>
            Silent,

            /// <summary>The keyboard answered otherwise.</summary>
            Refused,

            /// <summary>The write or the read failed.</summary>
            Failed,
        }

        /// <summary>One query (EVisionV2KeyboardController.cpp:116-177): the
        /// report, then the keyboard's answer, read past any other report and
        /// past a late answer to an earlier query, which must be 64 bytes of
        /// report 04 that echo the sum, the command and the offset, with status
        /// 0. <paramref name="data"/> is the answer's data when it is
        /// <see cref="V2Answer.Good"/>. <paramref name="silent"/> says the last
        /// answer did not come, and then this read does not wait
        /// (<see cref="SonixKeyboard.AnswerMs"/>).</summary>
        internal static V2Answer Query(IHidReportChannel io, byte command, ref bool silent, out byte[] data, int offset = 0,
            ReadOnlySpan<byte> sent = default, int size = 0)
        {
            data = null;
            var packet = SonixKeyboard.Packet(command, size, offset, sent);
            if (!io.WriteOutput(packet)) return V2Answer.Failed;
            var answer = new byte[SonixKeyboard.PacketSize + 1];
            int read;
            int tries = 0;
            bool echoes;
            do
            {
                read = io.ReadInput(answer, silent ? 0 : SonixKeyboard.AnswerMs);
                if (read < 0) return V2Answer.Failed;
                silent = read == 0;
                if (silent) return V2Answer.Silent;
                echoes = answer[0] == SonixKeyboard.ReportId && answer[1] == packet[1] && answer[2] == packet[2]
                    && answer[3] == command && answer[5] == packet[5] && answer[6] == packet[6];
            } while (!echoes && ++tries < 16);
            if (!echoes || read < SonixKeyboard.PacketSize || answer[7] != 0) return V2Answer.Refused;
            int length = answer[4];
            if (length > SonixKeyboard.PacketSize - 8) return V2Answer.Refused;
            data = answer.AsSpan(8, length).ToArray();
            return V2Answer.Good;
        }

        /// <summary>A write cut into queries of up to 56 bytes, Write
        /// (lines 210-225). It stops at a query the keyboard refused or one
        /// that failed, and goes on past an answer that did not come.</summary>
        internal static V2Answer Write(IHidReportChannel io, byte command, int offset, byte[] data, ref bool silent)
        {
            for (int at = 0; at < data.Length;)
            {
                int size = Math.Min(data.Length - at, SonixKeyboard.PacketSize - 8);
                var answer = Query(io, command, ref silent, out _, offset + at, data.AsSpan(at, size), size);
                if (answer is V2Answer.Refused or V2Answer.Failed) return answer;
                at += size;
            }
            return V2Answer.Good;
        }

        /// <summary>The dynamic colors of a picture, SetLedsDirect (lines
        /// 371-389): three bytes per key of the frame, each LED's color at
        /// the place its key value gives.</summary>
        internal static byte[] DynamicColors(IReadOnlyList<int> colors, byte[] values, int count, int mapSize)
        {
            var buffer = new byte[3 * mapSize];
            for (int i = 0; i < count && i < (colors?.Count ?? 0) && i < values.Length; i++)
            {
                int j = values[i] * 3;
                if (j + 2 >= buffer.Length) continue;
                buffer[j + 0] = (byte)(colors[i] >> 16);
                buffer[j + 1] = (byte)(colors[i] >> 8);
                buffer[j + 2] = (byte)colors[i];
            }
            return buffer;
        }

        /// <summary>The end of dynamic colors as a report of its own, for the
        /// crash path.</summary>
        internal static byte[] EndFrame() => SonixKeyboard.Packet(EndDynamicColors, 0, 0, default);

        private static HidLightingSession BeginV2(IHidReportChannel io, byte[] values, int count)
        {
            // Read(READ_CAPABILITIES, 0, 7) and its check, which OpenRGB
            // passes unless both of the first two bytes differ from AA 55
            // (lines 61-72).
            bool silent = false;
            if (Query(io, ReadCapabilities, ref silent, out var capabilities, 0, default, CapabilitiesLength) != V2Answer.Good
                || capabilities.Length < CapabilitiesLength)
                return null;
            if (capabilities[0] != 0xAA && capabilities[1] != 0x55) return null;
            return new V2Session(values, count, capabilities[5]);
        }

        internal sealed class V2Session : HidLightingSession
        {
            private readonly byte[] _values;
            private readonly int _count;
            private byte[][] _crash = Array.Empty<byte[]>();
            private bool _silent;

            public V2Session(byte[] values, int count, int mapSize)
            {
                _values = values;
                _count = count;
                MapSize = mapSize;
            }

            /// <summary>The frame's size in keys, the capabilities' byte 5.</summary>
            public int MapSize { get; }

            public override bool Feature => false;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // Marked first: a write that reports a failure may still
                // have landed.
                if (_crash.Length == 0) _crash = new[] { EndFrame() };
                switch (Write(io, SendDynamicColors, 0, DynamicColors(colors, _values, _count, MapSize), ref _silent))
                {
                    case V2Answer.Failed:
                        return HidLightingPaint.Failed;
                    case V2Answer.Refused:
                        LaterMs = RefreshMs;
                        return HidLightingPaint.Later;
                    default:
                        return HidLightingPaint.Sent;
                }
            }

            /// <summary>RefreshLedDirect, one byte of the dynamic colors at
            /// key 6, never the picture again.</summary>
            public override bool KeepAlive(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => Query(io, SendDynamicColors, ref _silent, out _, BlankSpace * 3, new byte[1], 1) != V2Answer.Failed;

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (Query(io, EndDynamicColors, ref _silent, out _) == V2Answer.Failed) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
