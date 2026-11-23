using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Royuan keyboards OpenRGB's RoyuanKeyboardController lights (#557):
    /// the Akko 3068B Plus and PC75B Plus on interface 0 and on the vendor
    /// collection of interface 1, page FFFF, the Skyloong GK68HE Pro, the
    /// Epomaker TH80 Pro on cable and dongle and the Attack Shark K86 on
    /// interface 2 (RoyuanKeyboardControllerDetect.cpp:125-130 at 6b681e3c).
    /// Each takes one color for the whole board.
    ///
    /// <para>The Dukharo VN66 has the Akko's product ID and profile. OpenRGB
    /// makes no controller of its interface 0, whose product string holds
    /// "Dukharo" or "VN66", and lights it through the vendor collection
    /// (lines 27-81, RoyuanKeyboardController.cpp:98-105, d91bff77). A session
    /// on interface 0 reads the product string first and is not made for
    /// it.</para>
    ///
    /// <para>The report. SET_LEDPARAM, a 65-byte feature report 0: 07, the
    /// mode, the speed, the brightness, the option, red, green and blue, and
    /// a checksum of the eight bytes after the ID (RoyuanKeyboardController.cpp:312-364),
    /// the ones' complement of their sum on the Akko and the Skyloong and
    /// the distance below the next power of two on the Epomaker and Attack
    /// Shark (lines 26-96 and 249-271).</para>
    ///
    /// <para>The Akko lights its Static mode with a single color: mode 01,
    /// option 0x07, its speed byte reversed, 4 for Static's speed 0
    /// (RGBController_AkkoKeyboard.cpp, the Static mode and DeviceUpdateLEDs,
    /// RoyuanKeyboardController.cpp:319-337), and brightness 4, its most
    /// (RoyuanKeyboardController.h:99), since PadForge dims a color itself.
    /// OpenRGB's default is 3. It locks up its control endpoint when a SET is
    /// followed too quickly by another transfer, so OpenRGB sends the latest
    /// one 500 ms after it was asked for and at least 1.5 s after the one
    /// before, and waits out those 1.5 s before it closes, since the next open
    /// reads the state right away (lines 19-24, 164-172 and 477-516). A session
    /// keeps the same spacing between pictures by its hold and its repaint
    /// interval, with one difference: the hold starts over at each change, so a
    /// color that keeps changing goes out not at all, as for every model that
    /// stores its pictures. The family keeps each keyboard's next transfer as
    /// OpenRGB keeps next_transfer: 500 ms after the state's read and 1.5 s
    /// after a SET (lines 159 and 523). A hand-back and the read a new session
    /// starts with wait for it, and a picture asked for sooner goes out then.
    /// A keyboard is its Windows container, so an Akko lit on both of its
    /// collections keeps one spacing: every feature report reaches the one
    /// control endpoint, whatever the interface. OpenRGB paces each of its two
    /// controllers alone. A collection Windows names no container for is a
    /// keyboard of its own. The crash path cannot wait, so its one frame can
    /// land inside the spacing.</para>
    ///
    /// <para>The Skyloong GK68HE Pro has a Direct mode, 0x15, whose color
    /// goes in its own report: 0E, red, green and blue, and the checksum at
    /// byte 8 (lines 379-427, RGBController_SkyloongGK68HEPro.cpp:77-88). A
    /// session sends SET_LEDPARAM with mode 0x15, its brightness 4 and its
    /// speed byte 4, once, then the color report for each picture. The mode
    /// is the write OpenRGB flags as saving (MODE_FLAG_AUTOMATIC_SAVE, line
    /// 80). The color report is its Direct write, sent on every update (lines
    /// 393-403), so pictures go out at the realtime rate. OpenRGB
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
    /// OpenRGB's page says the keyboard saves on every update ("@save :robot:",
    /// RGBController_EpomakerController.cpp:19, the meaning from its commit
    /// 8f876423). The mode carries no save flag (line 47), and no reference
    /// sends it as a stream, so each picture is taken to land in the keyboard's
    /// memory, and pictures are held and spaced
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

        private readonly Func<long> _clock;
        private readonly Func<string, Guid> _containerOf;

        /// <summary>When each Akko may next take a transfer, by its keyboard
        /// (<see cref="KeyboardOf"/>).</summary>
        private readonly Dictionary<string, long> _nextTransfer = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Test seam: the clock the Akko's spacing is measured by and
        /// the container lookup.</summary>
        internal RoyuanLighting(Func<long> clock = null, Func<string, Guid> containerOf = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
        }

        /// <summary>The key of the keyboard a collection belongs to: its Windows
        /// container, or its path where Windows names none, or names the PC's
        /// own. Asked anew at each SET and each wait for one, so a keyboard
        /// plugged in where another was is never taken for it.</summary>
        private string KeyboardOf(IHidReportChannel io)
        {
            string path = io.Path ?? "";
            Guid container = path.Length == 0 ? Guid.Empty : _containerOf(path);
            return container == Guid.Empty || container == PeripheralLinker.LocalMachineContainer
                ? path
                : "container:" + container.ToString("D");
        }

        /// <summary>The time left before this keyboard's next transfer, 0 when
        /// it may take one now.</summary>
        private int LeftBeforeTransfer(IHidReportChannel io)
        {
            string keyboard = KeyboardOf(io);
            long next;
            lock (_nextTransfer)
            {
                if (!_nextTransfer.TryGetValue(keyboard, out next)) return 0;
            }
            return (int)Math.Max(0, next - _clock());
        }

        /// <summary>Waits for this keyboard's next transfer (sleep_until(next_transfer),
        /// lines 180 and 512).</summary>
        private void WaitForTransfer(IHidReportChannel io, Action<int> wait)
        {
            int left = LeftBeforeTransfer(io);
            if (left > 0) wait(left);
        }

        /// <summary>Puts this keyboard's next transfer <paramref name="ms"/> from now:
        /// ROYUAN_PRE_SET_DELAY_MS after the state's read, ROYUAN_POST_SET_DELAY_MS
        /// after a SET.</summary>
        private void NoteTransfer(IHidReportChannel io, int ms)
        {
            string keyboard = KeyboardOf(io);
            lock (_nextTransfer) _nextTransfer[keyboard] = _clock() + ms;
        }

        internal const string FamilyName = "RoyuanKeyboardController";
        internal const string AkkoDetector = "DetectRoyuanAkkoKeyboardControllers";
        internal const string SkyloongDetector = "DetectRoyuanGK68HEProKeyboardControllers";

        /// <summary>ROYUAN_REPORT_LENGTH and the report ID (RoyuanKeyboardController.h:9).</summary>
        internal const int ReportLength = 65;

        /// <summary>The commands (RoyuanKeyboardController.h:12-15).</summary>
        internal const byte SetLedParams = 0x07;
        internal const byte GetLedParams = 0x87;
        internal const byte SetDirectColor = 0x0E;

        /// <summary>The modes and options (RoyuanKeyboardController.h:20-60,
        /// EpomakerController.h). ROYUAN_AKKO_COLOR_SINGLE is 07 and RANDOM 08
        /// since d91bff77 (lines 54-55 at 6b681e3c).</summary>
        internal const byte AkkoStatic = 0x01;
        internal const byte AkkoColorSingle = 0x07;
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

        private readonly Dictionary<(ushort, string), HidLightingModel> _models = new();

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
            bool akkoFirst = board == Board.Akko && registration.Interface == 0;
            lock (_models)
            {
                var key = (collection.ProductId, registration.Name);
                if (_models.TryGetValue(key, out var model)) return model;
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registration.Name,
                    Map = new LightMap(SinowealthLighting.OneLight())
                    {
                        Product = $"royuan:{collection.VendorId:X4}:{collection.ProductId:X4}",
                    },
                    HoldMs = board switch { Board.Akko => AkkoPreSetMs, Board.Legacy => SinowealthLighting.StoredHoldMs, _ => 0 },
                    RepaintMs = board switch { Board.Akko => AkkoPostSetMs, Board.Legacy => SinowealthLighting.StoredRepaintMs, _ => RepaintMs },
                    Begin = (io, wait) => akkoFirst && IsDukharoVn66(io?.ProductString)
                        ? null
                        : Session.Begin(io, wait, board, board == Board.Akko ? this : null),
                };
                _models[key] = model;
                return model;
            }
        }

        /// <summary>The product string OpenRGB takes for a Dukharo VN66
        /// (RoyuanKeyboardControllerDetect.cpp:33, 47 and 56).</summary>
        internal static bool IsDukharoVn66(string product)
            => product != null && (product.Contains("Dukharo", StringComparison.Ordinal) || product.Contains("VN66", StringComparison.Ordinal));

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
            // Static: speed 0 reversed from 4, brightness at its most, option single.
            ? LedParams(board, AkkoStatic, 4 - 0, 4, AkkoColorSingle, rgb)
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
        /// (lines 50-96). The brightness is at most 4. Both send option 0x07
        /// for their Static mode whatever was read, the solid option of their
        /// profiles (solid_option_overrides_static, lines 67-68, 91-92 and
        /// 346-353, the Akko's since d91bff77).</summary>
        internal static byte[] RestoreReport(Board board, byte[] state)
        {
            byte brightness = Math.Min(state[2], (byte)4);
            byte speed = state[1] <= 4 ? state[1] : (byte)(board == Board.Akko ? 4 : 2);
            byte option = board != Board.Legacy && state[0] == AkkoStatic ? AkkoColorSingle : state[3];
            int rgb = state[4] << 16 | state[5] << 8 | state[6];
            return LedParams(board, state[0], speed, brightness, option, rgb);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Board _board;
            private readonly byte[] _state;
            private readonly byte[][] _crash;
            private readonly RoyuanLighting _spacing;
            private bool _direct;

            private Session(Board board, byte[] state, RoyuanLighting spacing)
            {
                _board = board;
                _state = state;
                _spacing = spacing;
                _crash = state == null ? Array.Empty<byte[]>() : new[] { RestoreReport(board, state) };
            }

            public override bool Feature => true;

            public override byte[][] CrashFrames => _crash;

            /// <param name="spacing">The family that keeps the spacing around
            /// this board's SETs, or null for a board that needs none.</param>
            public static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, Board board, RoyuanLighting spacing)
            {
                if (board == Board.Legacy) return new Session(board, null, spacing);
                // OpenRGB makes no controller of a board that does not answer
                // (RoyuanKeyboardControllerDetect.cpp:22-37 and 43-57, line 146).
                spacing?.WaitForTransfer(io, wait);
                var state = ReadState(io, wait);
                spacing?.NoteTransfer(io, AkkoPreSetMs);
                return state == null ? null : new Session(board, state, spacing);
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
                // The other collection of the keyboard may have taken a SET since
                // this session's last.
                int left = _spacing?.LeftBeforeTransfer(io) ?? 0;
                if (left > 0)
                {
                    LaterMs = left;
                    return HidLightingPaint.Later;
                }
                // Noted either way: a SET that reports a failure may have landed.
                bool sent = io.SetFeature(Picture(_board, rgb));
                _spacing?.NoteTransfer(io, AkkoPostSetMs);
                return sent ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                _spacing?.WaitForTransfer(io, wait);
                bool sent = io.SetFeature(_crash[0]);
                _spacing?.NoteTransfer(io, AkkoPostSetMs);
                return sent;
            }
        }
    }
}
