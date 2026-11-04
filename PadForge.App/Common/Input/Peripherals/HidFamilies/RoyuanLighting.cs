using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Royuan keyboards OpenRGB's RoyuanKeyboardController lights (#557):
    /// the Akko 3068B Plus and PC75B Plus on interface 0, the Skyloong
    /// GK68HE Pro, the Epomaker TH80 Pro on cable and dongle and the Attack
    /// Shark K86 on interface 2 (RoyuanKeyboardControllerDetect.cpp:92-96).
    /// Each takes one color for the whole board.
    ///
    /// <para>The report. SET_LEDPARAM, a 65-byte feature report 0: 07, the
    /// mode, the speed, the brightness, the option, red, green and blue, and
    /// a checksum of the eight bytes after the ID (RoyuanKeyboardController.cpp:312-364),
    /// the ones' complement of their sum on the Akko and the Skyloong and
    /// the distance below the next power of two on the Epomaker and Attack
    /// Shark (lines 26-96 and 249-271).</para>
    ///
    /// <para>The Akko lights its Static mode with a single color: mode 01,
    /// option 0x08, brightness 3, and its speed byte reversed, 4 for
    /// Static's speed 0 (RGBController_AkkoKeyboard.cpp, the Static mode and
    /// DeviceUpdateLEDs, RoyuanKeyboardController.cpp:319-337). It locks up
    /// when SETs come close together, so OpenRGB sends the latest one 500 ms
    /// after it was asked for and at least 1.5 s after the one before
    /// (lines 19-24 and 477-516), and so does a session, by its hold and
    /// its repaint interval.</para>
    ///
    /// <para>The Skyloong GK68HE Pro has a Direct mode, 0x15, whose color
    /// goes in its own report: 0E, red, green and blue, and the checksum at
    /// byte 8 (lines 379-427, RGBController_SkyloongGK68HEPro.cpp:77-88). A
    /// session sends SET_LEDPARAM with mode 0x15, its brightness 4 and its
    /// speed byte 4, once, then the color report for each picture. OpenRGB
    /// also lights the board key by key through four Custom Static profiles
    /// (lines 295-345), each a picture written over one of the board's
    /// stored profiles (SetUserPic, RoyuanKeyboardController.cpp:429-475),
    /// which no reference reads back. A session does not write those, since
    /// nothing could give the user's own profile back.</para>
    ///
    /// <para>The Epomaker TH80 Pro and Attack Shark K86 light their Always
    /// On mode, 01, OpenRGB's "Direct", with speed 0, brightness 4 and the
    /// option 0x07 for a set color (RGBController_EpomakerController.cpp:
    /// the Always On mode and lines 282-290, RoyuanKeyboardController.cpp:366-372).
    /// The mode is stored ("@save :robot:"), so pictures are held and spaced
    /// as the Lenovo Gen 10 keyboard's stored writes are
    /// (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>The hand-back. The Akko and the Skyloong answer GET_LEDPARAM
    /// (0x87, then up to five reads 80 ms apart, lines 273-310), and a
    /// session reads it first and writes it back whole at the hand-back and
    /// on the crash path, which returns the keyboard's own mode and color.
    /// The Epomaker and Attack Shark do not answer it (supports_readback is
    /// false, lines 26-48), and no reference returns them to their own
    /// lighting: the hand-back sends nothing and the keyboard keeps the last
    /// color in its Always On mode.</para>
    ///
    /// <para>No program is named: no source names the processes of Akko's,
    /// Skyloong's, Epomaker's or Attack Shark's software.</para>
    /// </summary>
    internal sealed class RoyuanLighting : IHidLightingFamily
    {
        public static readonly RoyuanLighting Instance = new();

        internal const string FamilyName = "RoyuanKeyboardController";
        internal const string AkkoDetector = "DetectRoyuanAkkoKeyboardControllers";
        internal const string SkyloongDetector = "DetectRoyuanGK68HEProKeyboardControllers";
        internal const string LegacyDetector = "DetectRoyuanLegacyKeyboardControllers";

        /// <summary>ROYUAN_REPORT_LENGTH and the report ID (RoyuanKeyboardController.h:9).</summary>
        internal const int ReportLength = 65;

        /// <summary>The commands (RoyuanKeyboardController.h:12-15).</summary>
        internal const byte SetLedParams = 0x07;
        internal const byte GetLedParams = 0x87;
        internal const byte SetDirectColor = 0x0E;

        /// <summary>The modes and options (RoyuanKeyboardController.h:20-60,
        /// EpomakerController.h).</summary>
        internal const byte AkkoStatic = 0x01;
        internal const byte AkkoColorSingle = 0x08;
        internal const byte SkyloongDirect = 0x15;
        internal const byte EpomakerAlwaysOn = 0x01;
        internal const byte EpomakerDazzleOff = 0x07;

        /// <summary>GET_LEDPARAM's reads (RoyuanKeyboardController.cpp:288-306).</summary>
        internal const int ReadTries = 5;
        internal const int ReadWaitMs = 80;

        /// <summary>The Akko's pacing (lines 23-24).</summary>
        internal const int AkkoPreSetMs = 500;
        internal const int AkkoPostSetMs = 1500;

        /// <summary>The shortest time between two Skyloong pictures. No
        /// reference bounds it.</summary>
        internal const int RepaintMs = 33;

        internal enum Board
        {
            Akko,
            Skyloong,
            Legacy,
        }

        public string Name => "Royuan";

        private readonly Dictionary<ushort, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength != ReportLength) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            var board = registration.Detector switch
            {
                AkkoDetector => Board.Akko,
                SkyloongDetector => Board.Skyloong,
                _ => Board.Legacy,
            };
            lock (_models)
            {
                if (_models.TryGetValue(collection.ProductId, out var model)) return model;
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registration.Name,
                    Map = new LightMap(SinowealthLighting.OneLight(), "OpenRGB RoyuanKeyboardController")
                    {
                        Product = $"royuan:{collection.VendorId:X4}:{collection.ProductId:X4}",
                    },
                    HoldMs = board switch { Board.Akko => AkkoPreSetMs, Board.Legacy => SinowealthLighting.StoredHoldMs, _ => 0 },
                    RepaintMs = board switch { Board.Akko => AkkoPostSetMs, Board.Legacy => SinowealthLighting.StoredRepaintMs, _ => RepaintMs },
                    Begin = (io, wait) => Session.Begin(io, wait, board),
                };
                _models[collection.ProductId] = model;
                return model;
            }
        }

        /// <summary>CalculateChecksum (RoyuanKeyboardController.cpp:249-271).</summary>
        internal static byte Checksum(byte[] report, int from, int count, bool onesComplement)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum += report[from + i];
            if (onesComplement) return (byte)(0xFF - (sum & 0xFF));
            int power = 1;
            while (power < sum) power <<= 1;
            return (byte)((power - sum - 1) & 0xFF);
        }

        /// <summary>The board's SET_LEDPARAM, SendUpdate (lines 312-342),
        /// with the bytes as they go out.</summary>
        internal static byte[] LedParams(Board board, byte mode, byte speedByte, byte brightness, byte option, int rgb)
        {
            var report = new byte[ReportLength];
            report[1] = SetLedParams;
            report[2] = mode;
            report[3] = speedByte;
            report[4] = brightness;
            report[5] = option;
            report[6] = (byte)(rgb >> 16);
            report[7] = (byte)(rgb >> 8);
            report[8] = (byte)rgb;
            report[9] = Checksum(report, 1, 8, board != Board.Legacy);
            return report;
        }

        /// <summary>A picture's SET_LEDPARAM for the Akko and the Epomaker.</summary>
        internal static byte[] Picture(Board board, int rgb) => board == Board.Akko
            // Static: speed 0 reversed from 4, brightness 3, option single.
            ? LedParams(board, AkkoStatic, 4 - 0, 3, AkkoColorSingle, rgb)
            // Always On: speed 0, brightness 4, dazzle off.
            : LedParams(board, EpomakerAlwaysOn, 0, 4, EpomakerDazzleOff, rgb);

        /// <summary>The Skyloong's Direct mode report: speed 0 reversed from 4,
        /// brightness 4, and the option read, Direct not being Static
        /// (lines 330-337).</summary>
        internal static byte[] SkyloongDirectMode(byte option, int rgb) => LedParams(Board.Skyloong, SkyloongDirect, 4, 4, option, rgb);

        /// <summary>SendDirectColor (lines 408-427).</summary>
        internal static byte[] DirectColor(int rgb)
        {
            var report = new byte[ReportLength];
            report[1] = SetDirectColor;
            report[2] = (byte)(rgb >> 16);
            report[3] = (byte)(rgb >> 8);
            report[4] = (byte)rgb;
            report[8] = Checksum(report, 1, 7, true);
            return report;
        }

        /// <summary>GET_LEDPARAM's request (lines 275-278).</summary>
        internal static byte[] ReadRequest(bool onesComplement)
        {
            var report = new byte[ReportLength];
            report[1] = GetLedParams;
            report[8] = Checksum(report, 1, 7, onesComplement);
            return report;
        }

        /// <summary>ReadState: the request, then up to five reads 80 ms apart
        /// for an answer that starts 87 (lines 273-310). The eight bytes from
        /// the mode to blue, or null.</summary>
        internal static byte[] ReadState(IHidReportChannel io, Action<int> wait)
        {
            if (!io.SetFeature(ReadRequest(true))) return null;
            var buffer = new byte[Math.Max(ReportLength, io.FeatureReportLength)];
            for (int attempt = 0; attempt < ReadTries; attempt++)
            {
                wait(ReadWaitMs);
                Array.Clear(buffer);
                if (io.GetFeature(0x00, buffer) && buffer[1] == GetLedParams)
                {
                    var state = new byte[7];
                    Array.Copy(buffer, 2, state, 0, 7);
                    return state;
                }
            }
            return null;
        }

        /// <summary>The SET_LEDPARAM that writes a read state back, as
        /// SendUpdate builds it from what ReadState kept (lines 297-303 and
        /// 316-341). Both boards reverse their speed against a maximum of 4,
        /// so a speed byte of 4 or less goes back as it came, and a larger one
        /// as the board's default reversed: 4 on the Akko, 2 on the Skyloong
        /// (lines 50-96). The brightness is at most 4. The Skyloong sends
        /// option 0x07 for its Static mode whatever was read.</summary>
        internal static byte[] RestoreReport(Board board, byte[] state)
        {
            byte brightness = Math.Min(state[2], (byte)4);
            byte speed = state[1] <= 4 ? state[1] : (byte)(board == Board.Akko ? 4 : 2);
            byte option = board == Board.Skyloong && state[0] == AkkoStatic ? (byte)0x07 : state[3];
            int rgb = state[4] << 16 | state[5] << 8 | state[6];
            return LedParams(board, state[0], speed, brightness, option, rgb);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Board _board;
            private readonly byte[] _state;
            private readonly byte[][] _crash;
            private bool _direct;

            private Session(Board board, byte[] state)
            {
                _board = board;
                _state = state;
                _crash = state == null ? Array.Empty<byte[]>() : new[] { RestoreReport(board, state) };
            }

            public override bool Feature => true;

            public override byte[][] CrashFrames => _crash;

            public static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, Board board)
            {
                if (board == Board.Legacy) return new Session(board, null);
                // OpenRGB makes no controller of a board that does not answer
                // (RoyuanKeyboardControllerDetect.cpp:22-37 and 43-57, line 146).
                var state = ReadState(io, wait);
                return state == null ? null : new Session(board, state);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                if (_board == Board.Skyloong)
                {
                    if (!_direct)
                    {
                        if (!io.SetFeature(SkyloongDirectMode(_state[3], rgb))) return HidLightingPaint.Failed;
                        _direct = true;
                    }
                    return io.SetFeature(DirectColor(rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
                }
                return io.SetFeature(Picture(_board, rgb)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
                => _crash.Length == 0 || io.SetFeature(_crash[0]);
        }
    }
}
