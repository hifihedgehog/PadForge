using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// Any keyboard on QMK's raw HID collection, page FF60, usage 61, whatever its IDs
    /// (#557). OpenRGB and SignalRGB name the keyboards they know, and OpenRGB takes the
    /// rest from its user's settings. The firmware itself says what it speaks, so nothing
    /// needs listing.
    ///
    /// <para>A 64-byte collection is the QMK-OpenRGB firmware, which answers its protocol
    /// version at once (QMKOpenRGBControllerDetect.cpp:40-62). A 32-byte collection is VIA's,
    /// and a Vial firmware with its RGB extension takes each LED
    /// (<see cref="QmkLighting.VialSession"/>). A VIA firmware alone takes the whole
    /// keyboard's color through its custom values, which most boards with VIA on them ship
    /// with: no reflash, and nothing stored, since VIA's setters are the _noeeprom ones until
    /// a save (qmk_firmware quantum/via.c:544-562 and 716-738, keyboards/wilba_tech
    /// wt_rgb_backlight.c:1805-1890).</para>
    ///
    /// <para>This family comes last on the worker's list, and a row of OpenRGB's or SignalRGB's
    /// falls back to it where its firmware answers neither protocol. A collection on this page
    /// with none of these answers stays dark.</para>
    /// </summary>
    internal sealed class QmkRawHidLighting : IHidLightingFamily
    {
        public static readonly QmkRawHidLighting Instance = new();

        /// <summary>RAW_USAGE_PAGE and RAW_USAGE_ID (tmk_core/protocol/usb_descriptor.h).</summary>
        internal const ushort RawPage = 0xFF60, RawUsage = 0x61;

        internal const int OpenRgbPacket = 64;

        public string Name => "QMK";

        private readonly Dictionary<(ushort, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.UsagePage != RawPage || collection.Usage != RawUsage) return null;
            if (collection.OutputReportLength < QmkLighting.PacketSize) return null;
            ushort vendor = collection.VendorId, product = collection.ProductId;
            lock (_models)
            {
                if (_models.TryGetValue((vendor, product), out var known)) return known;
                string key = QmkLighting.Product(vendor, product);
                return _models[(vendor, product)] = new HidLightingModel
                {
                    Family = this,
                    Name = "QMK Keyboard",
                    Map = new LightMap(new[] { WholeLight() }, "QMK, from the keyboard") { Product = key },
                    RepaintMs = QmkLighting.RepaintMs,
                    Begin = (io, wait) => Begin(io, wait, key),
                };
            }
        }

        internal static LightInfo WholeLight() => new(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 };

        /// <summary>Asks the firmware what it speaks. Null when it answers none of these.</summary>
        internal static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, string product)
        {
            if (io == null) return null;
            if (io.OutputReportLength > QmkLighting.ReportSize) return OpenRgbProtocolSession.Begin(io, product);
            return QmkLighting.VialSession.Begin(io, product) ?? ViaSession.Begin(io, product);
        }

        // ── VIA's custom values ──

        /// <summary>
        /// The whole keyboard's color through VIA (qmk_firmware quantum/via.h and via.c, at HEAD
        /// and at tags 0.18.0 and 0.19.0, vial-qmk quantum/via.c, keyboards/wilba_tech).
        ///
        /// <para>From protocol 11 (QMK 0.19.0) a value goes by channel: 3 the RGB matrix, 2
        /// rgblight and 1 a one-color backlight, with values 1, 2 and 4, and a channel the firmware
        /// lacks answers FF (via.c:220-225). Channel 0 is the board's own and its values mean what
        /// that board says, so only Wilba's, Rama's and Zeal's boards are asked, which take 9, 0A
        /// and 0C there (wt_main.c, wt_rgb_backlight_api.h).</para>
        ///
        /// <para>Before protocol 11 there are no channels and the values share one list. rgblight
        /// and QMK's RGB matrix take 80, 81 and 83 (0.18.0's via.c:576-618). Vial, still at protocol
        /// 9, moves its RGB matrix to 1, 2 and 4 (vial-qmk via.h:104-114). Wilba's lighting takes 9,
        /// 0A and 0C, and a backlight 9 and 0A alone.</para>
        ///
        /// <para>A firmware sends back the packet it got, with the value written in where it has
        /// that value (raw_hid_send at the end of via.c's raw_hid_receive). So a value it lacks
        /// comes back as asked, and only what it has is ever written. The effect goes to solid color
        /// once, 1 on all of these but a backlight, where 0 stops its breathing, and the color and
        /// brightness follow each picture. The session reads them all first, and the hand-back sets
        /// them again.</para>
        /// </summary>
        internal sealed class ViaSession : HidLightingSession
        {
            private const byte GetVersion = 0x01, ViaSet = QmkLighting.ViaSetValue, ViaGet = QmkLighting.ViaGetValue, Unhandled = 0xFF;

            /// <summary>The first protocol with channels: 0.19.0's via.h has them at 000B, and
            /// 0.18.0's at 000A has none.</summary>
            private const int ChannelsFrom = 0x000B;

            /// <summary>The protocol of QMK's oldest via.h, and OpenRGB's floor for a Vial board
            /// (QMKVialRGBController.cpp:83-88).</summary>
            private const int OldestVersion = 9;

            private const byte CustomChannel = 0x00, BacklightChannel = 0x01, RgbLightChannel = 0x02;

            /// <summary>The older values: rgblight's and the matrix's (via.h of 0.18.0:100-103),
            /// and Wilba's (wt_rgb_backlight_api.h).</summary>
            private const byte OldBrightness = 0x80, OldEffect = 0x81, OldColor = 0x83;
            private const byte WilbaBrightness = 0x09, WilbaEffect = 0x0A, WilbaColor = 0x0C;

            private const byte SolidColor = 0x01, NoBreathing = 0x00;

            /// <summary>Two different bytes, so a value that holds one still shows.</summary>
            private static readonly byte[] Probes = { 0x5A, 0xA5 };

            /// <summary>The vendors of every board in keyboards/wilba_tech.</summary>
            private static readonly string[] WilbaVendors = { "qmk:6582:", "qmk:5241:", "qmk:5A45:" };

            /// <summary>The wait for a picture's echo. The firmware answers every write
            /// (via.c:290-297), and a board that stops answering must not hold the worker for
            /// a second a frame, as it may while the session asks at the start.</summary>
            private const int PaintAnswerMs = 20;

            /// <summary>The head of a value's frame and its value IDs, by channel or by ID alone.
            /// A backlight has no color.</summary>
            internal sealed record Target(byte[] Head, byte Brightness, byte Effect, byte? Color, byte Solid, byte OldBrightness, byte OldEffect, byte OldHue, byte OldSaturation);

            private readonly List<Target> _targets;
            private readonly byte[][] _restore;
            private bool _started;

            private ViaSession(List<Target> targets, LightMap map)
            {
                _targets = targets;
                Map = map;
                _restore = Restore(targets).ToArray();
            }

            public override bool Feature => false;

            public override LightMap Map { get; }

            public override int? HoldMs => 0;

            public override int? RepaintMs => QmkLighting.RepaintMs;

            public override byte[][] CrashFrames => _started ? _restore : Array.Empty<byte[]>();

            internal static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                var version = QmkLighting.Exchange(io, QmkLighting.Packet(GetVersion), out int got);
                if (version == null || got <= 0 || version[0] != GetVersion) return null;
                int protocol = version[1] << 8 | version[2];
                if (protocol < OldestVersion) return null;
                var targets = (protocol >= ChannelsFrom ? ByChannel(io, product) : WithoutChannels(io))
                    .Where(target => target != null).ToList();
                if (targets.Count == 0) return null;
                return new ViaSession(targets, new LightMap(new[] { WholeLight() }, "QMK VIA, from the keyboard") { Product = product });
            }

            private static IEnumerable<Target> ByChannel(IHidReportChannel io, string product)
            {
                byte brightness = QmkLighting.RgbBrightness, effect = QmkLighting.RgbEffect, color = QmkLighting.RgbColor;
                yield return Read(io, new[] { QmkLighting.RgbMatrixChannel }, brightness, effect, color, SolidColor);
                yield return Read(io, new[] { RgbLightChannel }, brightness, effect, color, SolidColor);
                yield return Read(io, new[] { BacklightChannel }, brightness, effect, null, NoBreathing);
                if (product != null && WilbaVendors.Any(vendor => product.StartsWith(vendor, StringComparison.Ordinal)))
                    yield return Read(io, new[] { CustomChannel }, WilbaBrightness, WilbaEffect, WilbaColor, SolidColor);
            }

            private static IEnumerable<Target> WithoutChannels(IHidReportChannel io)
            {
                var none = Array.Empty<byte>();
                yield return Read(io, none, OldBrightness, OldEffect, OldColor, SolidColor);
                if (IsVial(io))
                    yield return Read(io, none, QmkLighting.RgbBrightness, QmkLighting.RgbEffect, QmkLighting.RgbColor, SolidColor);
                yield return Read(io, none, WilbaBrightness, WilbaEffect, WilbaColor, SolidColor)
                    ?? Read(io, none, WilbaBrightness, WilbaEffect, null, NoBreathing);
            }

            /// <summary>Vial answers its ID with its protocol, low byte first (vial-qmk
            /// quantum/vial.c:94-102). Any other firmware answers FF or sends the packet back.</summary>
            private static bool IsVial(IHidReportChannel io)
            {
                var id = QmkLighting.Exchange(io, QmkLighting.Packet(QmkLighting.VialCommand, QmkLighting.VialGetKeyboardId), out int got);
                return id != null && got > 0 && id[0] != Unhandled && id[0] != QmkLighting.VialCommand;
            }

            /// <summary>A value's answer, or null where the firmware does not have the value.</summary>
            private static byte[] Value(IHidReportChannel io, byte[] head, byte id)
            {
                int at = 1 + head.Length + 1;
                foreach (byte probe in Probes)
                {
                    var packet = new byte[at + 1];
                    packet[0] = ViaGet;
                    head.CopyTo(packet, 1);
                    packet[at - 1] = id;
                    packet[at] = probe;
                    var answer = QmkLighting.Exchange(io, QmkLighting.Packet(packet), out int got);
                    if (answer == null || got <= 0 || answer[0] == Unhandled) return null;
                    if (answer[at] != probe) return answer;
                }
                return null;
            }

            /// <summary>Reads what a target shows now, or null where the firmware does not have it.</summary>
            private static Target Read(IHidReportChannel io, byte[] head, byte brightness, byte effect, byte? color, byte solid)
            {
                int at = 1 + head.Length + 1;
                var effects = Value(io, head, effect);
                if (effects == null) return null;
                var colors = color is { } id ? Value(io, head, id) : null;
                if (color != null && colors == null) return null;
                var levels = Value(io, head, brightness);
                return levels == null ? null : new Target(head, brightness, effect, color, solid,
                    levels[at], effects[at], colors?[at] ?? 0, colors?[at + 1] ?? 0);
            }

            internal static byte[] Set(Target target, byte id, params byte[] values)
            {
                var packet = new byte[1 + target.Head.Length + 1 + values.Length];
                packet[0] = ViaSet;
                target.Head.CopyTo(packet, 1);
                packet[1 + target.Head.Length] = id;
                values.CopyTo(packet, 2 + target.Head.Length);
                return QmkLighting.Packet(packet);
            }

            internal static IEnumerable<byte[]> Restore(IEnumerable<Target> targets)
            {
                foreach (var target in targets)
                {
                    yield return Set(target, target.Effect, target.OldEffect);
                    if (target.Color is { } color) yield return Set(target, color, target.OldHue, target.OldSaturation);
                    yield return Set(target, target.Brightness, target.OldBrightness);
                }
            }

            internal IReadOnlyList<Target> Targets => _targets;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] : 0;
                var (h, s, v) = QmkLighting.Hsv(rgb);
                foreach (var target in _targets)
                {
                    if (!_started && !Send(io, Set(target, target.Effect, target.Solid))) return HidLightingPaint.Failed;
                    if (target.Color is { } color && !Send(io, Set(target, color, h, s))) return HidLightingPaint.Failed;
                    if (!Send(io, Set(target, target.Brightness, v))) return HidLightingPaint.Failed;
                }
                _started = true;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_started) return true;
                foreach (var frame in _restore)
                    if (!Send(io, frame)) return false;
                _started = false;
                return true;
            }

            internal static bool Send(IHidReportChannel io, byte[] frame)
            {
                if (!io.WriteOutput(frame)) return false;
                io.ReadInput(new byte[Math.Max(QmkLighting.ReportSize, io.InputReportLength)], PaintAnswerMs);
                return true;
            }
        }

        // ── The QMK-OpenRGB firmware ──

        /// <summary>
        /// OpenRGB's QMK protocol at revisions 9, B, C, D and E (Controllers/QMKController/
        /// QMKOpenRGBController). OpenRGB takes these keyboards from its user's settings
        /// (QMKOpenRGBControllerDetect.cpp:172) and matches them by ID, and the firmware says all
        /// of the same things when asked: the protocol, the LED count, each LED's place, flags
        /// and keycode, and which modes it has.
        ///
        /// <para>Direct is mode 1 on revision 9 and the last enabled mode from B, as OpenRGB
        /// numbers them (RGBController_QMKOpenRGBRev9.cpp:41-45,
        /// RGBController_QMKOpenRGBRevD.cpp:236-239 and 467-478). It is set with no save, and
        /// the hand-back sets the mode, speed and color the session read first
        /// (QMKOpenRGBBaseController.cpp:232-256 and 262-290).</para>
        ///
        /// <para>Every revision asks about the larger of the firmware's two LED counts and drops
        /// what it calls a failure, which revision 9 marks at byte 62 and the others in the LED's
        /// flags (RGBController_QMKOpenRGBRev9.cpp:278-281, QMKOpenRGBRev9Controller.cpp:51,
        /// QMKOpenRGBRevDController.cpp:75).</para>
        /// </summary>
        internal sealed class OpenRgbProtocolSession : HidLightingSession
        {
            private const byte CmdVersion = 1, CmdDeviceInfo = 3, CmdModeInfo = 4, CmdLedInfo = 5, CmdModes = 6, CmdSetMode = 7, CmdSetLeds = 9;
            private const byte Failure = 25, Success = 50;
            private const byte Rev9 = 0x09, RevB = 0x0B, RevC = 0x0C, RevD = 0x0D, RevE = 0x0E;
            private const byte DirectModeId = 1;

            /// <summary>Where revision 9 marks an LED it does not have.</summary>
            private const int Rev9FailureAt = 62;

            /// <summary>QMKOpenRGBRevD and Rev9 and RevB's constructors.</summary>
            internal const int LedsPerFrameD = 15, LedsPerFrameOlder = 20, LedInfoPerFrame = 8;

            private readonly byte _revision;
            private readonly byte[] _direct;
            private readonly byte[][] _restore;
            private bool _started;

            private OpenRgbProtocolSession(byte revision, LightMap map, byte direct, byte[] mode)
            {
                _revision = revision;
                Map = map;
                _direct = SetMode(0, 255, 255, direct, 127);
                _restore = new[] { SetMode(mode[3], mode[4], mode[5], mode[1], mode[2]) };
            }

            public override bool Feature => false;

            public override LightMap Map { get; }

            public override int? HoldMs => 0;

            public override int? RepaintMs => QmkLighting.RepaintMs;

            public override byte[][] CrashFrames => _started ? _restore : Array.Empty<byte[]>();

            /// <summary>hid_write's buffer: report 0, the command and its bytes, 65 in all.</summary>
            private static byte[] Packet(params byte[] bytes)
            {
                var packet = new byte[OpenRgbPacket + 1];
                bytes.CopyTo(packet, 1);
                return packet;
            }

            internal static byte[] SetMode(byte hue, byte saturation, byte value, byte mode, byte speed)
                => Packet(CmdSetMode, hue, saturation, value, mode, speed, 0x00);

            /// <summary>A command and its answer as hidapi hands it, the first data byte at 0.</summary>
            private static byte[] Ask(IHidReportChannel io, params byte[] bytes)
            {
                if (!io.WriteOutput(Packet(bytes))) return null;
                var buffer = new byte[Math.Max(OpenRgbPacket + 1, io.InputReportLength)];
                int read = io.ReadInput(buffer, QmkLighting.ReadTimeoutMs);
                if (read <= 1) return null;
                var answer = new byte[OpenRgbPacket];
                Array.Copy(buffer, 1, answer, 0, Math.Min(OpenRgbPacket, read - 1));
                return answer;
            }

            /// <summary>The Direct mode's number, or 0 where the firmware has none.</summary>
            private static byte DirectMode(IHidReportChannel io, byte revision)
            {
                if (revision == Rev9)
                    return Ask(io, CmdModes, DirectModeId) is { } enabled && enabled[1] == Success ? DirectModeId : (byte)0;
                var modes = Ask(io, CmdModes);
                if (modes == null) return 0;
                int count = 0;
                bool direct = false;
                for (int i = 1; i < modes.Length && modes[i] != 0; i++)
                {
                    count++;
                    direct |= modes[i] == DirectModeId;
                }
                return direct ? (byte)count : (byte)0;
            }

            internal static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                var version = Ask(io, CmdVersion);
                if (version == null || version[1] is not (Rev9 or RevB or RevC or RevD or RevE)) return null;
                byte revision = version[1];
                var info = Ask(io, CmdDeviceInfo);
                var mode = Ask(io, CmdModeInfo);
                if (info == null || mode == null) return null;
                byte direct = DirectMode(io, revision);
                if (direct == 0) return null;
                var lights = new List<LightInfo>();
                int total = Math.Max(info[1], info[2]);
                int perAnswer = revision == Rev9 ? 1 : LedInfoPerFrame;
                for (int sent = 0; sent < total; sent += perAnswer)
                {
                    int count = Math.Min(perAnswer, total - sent);
                    var answer = revision == Rev9 ? Ask(io, CmdLedInfo, (byte)sent) : Ask(io, CmdLedInfo, (byte)sent, (byte)count);
                    if (answer == null) return null;
                    for (int i = 0; i < count; i++)
                    {
                        int offset = i * 7;
                        byte flags = answer[offset + 3];
                        if ((revision == Rev9 ? answer[Rev9FailureAt] : flags) == Failure) continue;
                        byte keycode = answer[offset + 7];
                        lights.Add(QmkLighting.Light(lights.Count, keycode != 0, keycode, flags) with
                        {
                            X = answer[offset + 1], Y = answer[offset + 2], HasPosition = true,
                        });
                    }
                }
                if (lights.Count == 0) return null;
                return new OpenRgbProtocolSession(revision, new LightMap(lights, "QMK OpenRGB, from the keyboard") { Product = product }, direct, mode);
            }

            /// <summary>Revision D and E number each LED in its frame, four bytes each. Earlier
            /// ones give the first LED and the count, three bytes each.</summary>
            internal static List<byte[]> Frames(byte revision, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int total = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                bool numbered = revision is RevD or RevE;
                int per = numbered ? LedsPerFrameD : LedsPerFrameOlder;
                if (!numbered)
                {
                    var byAddress = new int[total];
                    for (int i = 0; i < total; i++)
                        if ((uint)lights[i].Address < (uint)total) byAddress[lights[i].Address] = colors[i];
                    colors = byAddress;
                }
                var frames = new List<byte[]>();
                for (int sent = 0; sent < total; sent += per)
                {
                    int count = Math.Min(per, total - sent);
                    var frame = numbered ? Packet(CmdSetLeds, (byte)count) : Packet(CmdSetLeds, (byte)sent, (byte)count);
                    for (int i = 0; i < count; i++)
                    {
                        int color = colors[sent + i];
                        int at = numbered ? 3 + i * 4 : 4 + i * 3;
                        if (numbered) frame[at++] = (byte)lights[sent + i].Address;
                        frame[at] = (byte)(color >> 16);
                        frame[at + 1] = (byte)(color >> 8);
                        frame[at + 2] = (byte)color;
                    }
                    frames.Add(frame);
                }
                return frames;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_started)
                {
                    if (!io.WriteOutput(_direct)) return HidLightingPaint.Failed;
                    // QMK_OPENRGB_HID_READ_TIMEOUT (QMKOpenRGBController.h:23).
                    io.ReadInput(new byte[Math.Max(OpenRgbPacket + 1, io.InputReportLength)], 50);
                    _started = true;
                }
                foreach (var frame in Frames(_revision, lights, colors))
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_started) return true;
                if (!io.WriteOutput(_restore[0])) return false;
                _started = false;
                return true;
            }
        }
    }
}
