using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>A Keychron matrix patch's cell: the LED found at the source
    /// row and column is drawn at the row and column
    /// (QMKKeychronController_Devices.h:452-458).</summary>
    internal readonly record struct KeychronPatchCell(byte SourceRow, byte SourceCol, byte Row, byte Col);

    /// <summary>
    /// The QMK keyboards OpenRGB's QMKController lights by a fixed
    /// registration (#557): every Keychron keyboard on QMK or ZMK firmware
    /// with Keychron's RGB protocol, and the VialRGB boards OpenRGB names,
    /// the Raspberry Pi 500+ and the YMDK 84, all on QMK's raw HID
    /// collection, page 0xFF60, usage 0x61 (QMKKeychronControllerDetect.cpp:55,
    /// QMKVialRGBControllerDetect.cpp:20-34 and 93-94). OpenRGB's other QMK
    /// detectors take their devices from the user's OpenRGB settings
    /// (QMKOpenRGBControllerDetect.cpp:172, QMKVialRGBControllerDetect.cpp:60-91)
    /// and match nothing here.
    ///
    /// <para>The exchange. Every command is a 33-byte output report, report
    /// ID 0 and 32 bytes, and its answer an input report read within a
    /// second (QMKKeychronController.cpp:653-738,
    /// QMKVialRGBController.cpp:422-476). Windows delivers the input report
    /// with its ID byte first, so the answer hidapi gives at byte n is the
    /// channel's byte n + 1.</para>
    ///
    /// <para>The keys come from the keyboard. Each LED's place in the key
    /// matrix is read, and the keycode at that place in layer 0, which
    /// OpenRGB names by its table (RGBController_QMKKeychron.cpp:194-200,
    /// RGBController_QMKVialRGB.cpp:230-236, qmk_keynames in
    /// QMKKeycodes.cpp). A light is a key by that name's usage
    /// (<see cref="OpenRgbKeys"/>), a light with a label where the name has
    /// one (a transparent key is "Key: Right Fn"), and a key no source names
    /// where the LED sits on a key whose keycode names nothing. An LED on
    /// no key is an edge light where the board flags it underglow and a
    /// status light where it flags it an indicator (VialRGB's flags, QMK's
    /// LED_FLAG_UNDERGLOW 0x02 and LED_FLAG_INDICATOR 0x08,
    /// Keychron-qmk_firmware quantum/rgb_matrix/rgb_matrix_types.h:66-69),
    /// and another light otherwise.</para>
    ///
    /// <para>Keychron. A session asks what OpenRGB asks before it lights a
    /// board (QMKKeychronController.cpp:24-231): the wireless keyboard
    /// behind a dongle (0xB2), the supported features (0xA2) and the RGB
    /// protocol version (0xA8 0x01), and lights nothing unless the features
    /// carry Keychron RGB or the version is not 0 (lines 286-293). Then the
    /// LED count (0xA8 0x05), the LED at each column of rows 0 to 31
    /// (0xA8 0x06), the keycode under each LED (0x04), and the product's
    /// matrix patch, which moves a few LEDs where the wiring matrix puts
    /// them off their keys and changes no address (lines 199-230). It skips
    /// OpenRGB's version and firmware reads, which only fill its info
    /// panel. OpenRGB's Direct mode is the RGB matrix effect 23 and the
    /// per-key type Solid (lines 333-347), and its colors go nine LEDs to a
    /// report in hue, saturation and value (lines 315-331 and 567-597), the
    /// hue scaled from degrees to 0-255 (OpenRGB qt/hsv.cpp rgb2hsv). The
    /// firmware keeps all of that in memory until a save command, which
    /// OpenRGB sends only for its other modes (MODE_FLAG_MANUAL_SAVE,
    /// RGBController_QMKKeychron.cpp:37-62), and so does a session: VIA's
    /// set commands use the _noeeprom setters, and Keychron's per-key
    /// commands change RAM until RGB_SAVE writes the EEPROM
    /// (Keychron-qmk_firmware quantum/via.c:710-737,
    /// keyboards/keychron/common/rgb/keychron_rgb.c:164-178 and 283-291).
    /// OpenRGB reads nothing to hand back. A session reads the RGB matrix's
    /// effect, color, brightness and speed through VIA's get command
    /// (via.c:684-708) and the per-key type (keychron_rgb.c:164-169) before
    /// its first picture, and the hand-back sets them again, which returns
    /// the keyboard to its own effect. Its per-key colors stay the last
    /// picture until the keyboard restarts, since no read of them reaches
    /// the host whole (keychron_rgb.c:181-195 and 401 write the status byte
    /// over the first LED's saturation).</para>
    ///
    /// <para>VialRGB. A session asks what OpenRGB asks
    /// (QMKVialRGBController.cpp:23-120): the VIA protocol, which has to be 9
    /// or more, Vial's keyboard ID with its protocol, 4 or more, and the
    /// VialRGB flag, then VialRGB's info, its supported effects, the LED
    /// count, each LED's position, flags and matrix place, the keycode at
    /// that place, and the current mode (RGBController_QMKVialRGB.cpp:127-136).
    /// A board without the Direct effect among its supported ones has no
    /// Direct mode in OpenRGB (lines 85-125) and is not lit. Direct is mode
    /// 1 set with speed and color 0 (lines 256-272), and the colors go nine
    /// LEDs to a report in hue, saturation and value, the first index in two
    /// bytes (QMKVialRGBController.cpp:366-399). vial-qmk keeps the mode in
    /// memory (quantum/vialrgb.c:66-74 and 161-182), and the hand-back sets
    /// the mode, speed and color the session read first. OpenRGB reads a
    /// keycode with its low byte where its high byte would be
    /// (QMKVialRGBController.cpp:243-261), which names a basic key right and
    /// a layered key by the key it taps, and so does a session.</para>
    ///
    /// <para>No program is named: VIA and Vial are web and desktop editors
    /// that open the keyboard only while their window is used, and no source
    /// names Keychron Launcher's processes.</para>
    /// </summary>
    internal sealed class QmkLighting : IHidLightingFamily
    {
        public static readonly QmkLighting Instance = new();

        internal const string FamilyName = "QMKController";
        internal const string KeychronDetector = "DetectQMKKeychronControllers";
        internal const string VialDetector = "DetectQMKVialRGBControllers";

        /// <summary>KEYCHRON_QHE_PACKET_SIZE and MSG_LEN: 32 bytes and the
        /// report ID (QMKKeychronController.h:34, QMKVialRGBController.h:19).</summary>
        internal const int PacketSize = 32;
        internal const int ReportSize = PacketSize + 1;

        /// <summary>The read's wait (QMKKeychronController.h:35,
        /// QMKVialRGBController.cpp:443).</summary>
        internal const int ReadTimeoutMs = 1000;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it.</summary>
        internal const int RepaintMs = 33;

        public string Name => "QMK";

        private readonly Dictionary<(ushort, ushort, string), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            foreach (var registration in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                if (registration.Detector is not (KeychronDetector or VialDetector)) continue;
                var key = (collection.VendorId, collection.ProductId, registration.Detector);
                lock (_models)
                {
                    if (_models.TryGetValue(key, out var model)) return model;
                    model = Model(registration, collection.VendorId, collection.ProductId);
                    _models[key] = model;
                    return model;
                }
            }
            return null;
        }

        internal static string Product(ushort vendorId, ushort productId) => $"qmk:{vendorId:X4}:{productId:X4}";

        private HidLightingModel Model(OpenRgbRegistration registration, ushort vendorId, ushort productId)
        {
            bool keychron = registration.Detector == KeychronDetector;
            string product = Product(vendorId, productId);
            return new HidLightingModel
            {
                Family = this,
                Name = registration.Name,
                Map = new LightMap(Array.Empty<LightInfo>(), keychron ? "QMK Keychron, from the keyboard" : "QMK VialRGB, from the keyboard")
                {
                    Product = product,
                },
                RepaintMs = RepaintMs,
                Begin = (io, wait) => keychron ? KeychronSession.Begin(io, vendorId, productId) : VialSession.Begin(io, product),
            };
        }

        // ─────────────────────────────────────────────
        //  The exchange
        // ─────────────────────────────────────────────

        /// <summary>A command: report ID 0, then the bytes given, padded to
        /// 33.</summary>
        internal static byte[] Packet(params byte[] bytes)
        {
            var packet = new byte[ReportSize];
            Array.Copy(bytes, 0, packet, 1, Math.Min(bytes.Length, PacketSize));
            return packet;
        }

        /// <summary>Writes a command and reads its answer as hidapi gives it:
        /// the 32 bytes after the report ID, and the count hidapi would
        /// return, 0 when nothing came and -1 when the read failed. Null
        /// when the write failed.</summary>
        internal static byte[] Exchange(IHidReportChannel io, byte[] packet, out int received)
        {
            received = -1;
            if (!io.WriteOutput(packet)) return null;
            var buffer = new byte[Math.Max(ReportSize, io.InputReportLength)];
            int read = io.ReadInput(buffer, ReadTimeoutMs);
            var answer = new byte[PacketSize];
            if (read > 1) Array.Copy(buffer, 1, answer, 0, Math.Min(PacketSize, read - 1));
            received = read > 0 ? read - 1 : read;
            return answer;
        }

        /// <summary>rgb2hsv as OpenRGB's commands use it: the hue in degrees
        /// scaled by 256 / 360 and cut to a byte, the saturation
        /// (256 x delta - 8) / max and the value max (qt/hsv.cpp:33-71,
        /// QMKKeychronController.cpp:588-593). A gray has no hue, which
        /// OpenRGB stores as -1 and whose scaled byte its compiler leaves
        /// to chance, and goes as hue 0.</summary>
        internal static (byte H, byte S, byte V) Hsv(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            int m = Math.Min(r, Math.Min(g, b));
            int max = Math.Max(r, Math.Max(g, b));
            int delta = max - m;
            if (delta == 0) return (0, 0, (byte)max);
            int h;
            if (r == max) h = (g - b) * 60 / delta;
            else if (g == max) h = (b - r) * 60 / delta + 120;
            else h = (r - g) * 60 / delta + 240;
            if (h < 0) h += 360;
            byte s = (byte)((256 * delta - 8) / max);
            return ((byte)(int)(h * (256.0f / 360.0f)), s, (byte)max);
        }

        private static Dictionary<ushort, string> s_keyNames;

        /// <summary>OpenRGB's name for a keycode, "" for one its table does
        /// not hold (std::map operator[], RGBController_QMKKeychron.cpp:197).</summary>
        internal static string KeyName(ushort keycode)
        {
            var names = System.Threading.Volatile.Read(ref s_keyNames);
            if (names == null)
            {
                names = new Dictionary<ushort, string>();
                foreach (var (code, name) in QmkTables.KeyNames) names[code] = name;
                System.Threading.Volatile.Write(ref s_keyNames, names);
            }
            return names.TryGetValue(keycode, out var found) ? found : "";
        }

        /// <summary>QMK's LED flags (Keychron-qmk_firmware
        /// quantum/rgb_matrix/rgb_matrix_types.h:66-69).</summary>
        internal const byte FlagUnderglow = 0x02;
        internal const byte FlagIndicator = 0x08;

        /// <summary>One LED as a light: a key by its keycode's name where it
        /// sits on a key, else by its flags.</summary>
        internal static LightInfo Light(int index, bool onKey, ushort keycode, byte flags)
        {
            if (!onKey)
            {
                var kind = (flags & FlagUnderglow) != 0 ? LightKind.Edge
                    : (flags & FlagIndicator) != 0 ? LightKind.Status : LightKind.Other;
                return new LightInfo(index, kind, 0, LightKeySource.None);
            }
            OpenRgbKeys.TryGet(KeyName(keycode), out ushort usage, out var label, out int number);
            if (usage != 0) return new LightInfo(index, LightKind.Key, usage, LightKeySource.Device);
            if (label != LightLabel.None)
                return new LightInfo(index, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
            return new LightInfo(index, LightKind.Key, 0, LightKeySource.None);
        }

        // ─────────────────────────────────────────────
        //  Keychron
        // ─────────────────────────────────────────────

        /// <summary>The Keychron commands (QMKKeychronController.h:40-98) and
        /// VIA's (QMKViaCommands.h:14-58).</summary>
        internal const byte ViaGetKeycode = 0x04;
        internal const byte ViaSetValue = 0x07;
        internal const byte ViaGetValue = 0x08;
        internal const byte RgbMatrixChannel = 0x03;
        internal const byte RgbBrightness = 0x01;
        internal const byte RgbEffect = 0x02;
        internal const byte RgbSpeed = 0x03;
        internal const byte RgbColor = 0x04;
        internal const byte KcSupportFeature = 0xA2;
        internal const byte KcRgb = 0xA8;
        internal const byte KcWirelessDeviceInfo = 0xB2;
        internal const byte KcRgbProtocolVersion = 0x01;
        internal const byte KcRgbLedCount = 0x05;
        internal const byte KcRgbLedIndex = 0x06;
        internal const byte KcPerKeyGetType = 0x07;
        internal const byte KcPerKeySetType = 0x08;
        internal const byte KcPerKeySetColor = 0x0A;
        internal const ushort KcFeatureRgb = 1 << 7;
        internal const byte KcPerKeyEffect = 23;
        internal const byte KcPerKeySolid = 0;
        internal const int KcRows = 32;
        internal const int LedsPerReport = 9;

        /// <summary>ViaSendCommand: the answer's byte 0 has to be the
        /// command, and the data follows it (QMKKeychronController.cpp:653-693).
        /// Null when the write failed, the answer was another command's, or
        /// it carried no data, where OpenRGB's callers give up
        /// (lines 363, 376, 463, 489 and 516).</summary>
        internal static byte[] KeychronCommand(IHidReportChannel io, byte cmd, params byte[] data)
        {
            var bytes = new byte[1 + data.Length];
            bytes[0] = cmd;
            Array.Copy(data, 0, bytes, 1, data.Length);
            var answer = Exchange(io, Packet(bytes), out int received);
            if (answer == null || answer[0] != cmd || received - 1 <= 0) return null;
            return answer;
        }

        /// <summary>ViaSendCommandSub: byte 0 the command and byte 1 the sub
        /// command (lines 695-738). The answer's data starts at byte 2.</summary>
        internal static byte[] KeychronSub(IHidReportChannel io, byte cmd, byte sub, params byte[] data)
        {
            var bytes = new byte[2 + data.Length];
            bytes[0] = cmd;
            bytes[1] = sub;
            Array.Copy(data, 0, bytes, 2, data.Length);
            var answer = Exchange(io, Packet(bytes), out int received);
            if (answer == null || answer[0] != cmd || answer[1] != sub || received - 2 <= 0) return null;
            return answer;
        }

        /// <summary>A Keychron per-key colors report: start, count and nine
        /// hue, saturation and value triplets at most, in 30 bytes after the
        /// command (lines 567-597).</summary>
        internal static byte[] KeychronColors(byte start, byte count, IReadOnlyList<int> colors, int from)
        {
            var bytes = new byte[2 + PacketSize - 2];
            bytes[0] = KcRgb;
            bytes[1] = KcPerKeySetColor;
            bytes[2] = start;
            bytes[3] = count;
            int n = Math.Min((int)count, LedsPerReport);
            for (int i = 0; i < n; i++)
            {
                var (h, s, v) = Hsv(colors[from + i]);
                bytes[4 + i * 3] = h;
                bytes[5 + i * 3] = s;
                bytes[6 + i * 3] = v;
            }
            return Packet(bytes);
        }

        /// <summary>SendLEDs: the colors of LEDs 0 to <paramref name="count"/>
        /// - 1 in reports of nine (lines 315-331).</summary>
        internal static List<byte[]> KeychronFrames(int count, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int start = 0;
            while (start < count)
            {
                int packet = Math.Min(LedsPerReport, count - start);
                frames.Add(KeychronColors((byte)start, (byte)packet, colors, start));
                start += packet;
            }
            return frames;
        }

        /// <summary>The Direct mode, SetMode(0xFFFF) (lines 333-339): the
        /// per-key effect, then the Solid per-key type.</summary>
        internal static byte[][] KeychronDirect() => new[]
        {
            Packet(ViaSetValue, RgbMatrixChannel, RgbEffect, KcPerKeyEffect),
            Packet(KcRgb, KcPerKeySetType, KcPerKeySolid),
        };

        /// <summary>What the keyboard showed before: the effect, hue and
        /// saturation, brightness and speed, and the per-key type.</summary>
        internal readonly record struct KeychronState(byte Effect, byte Hue, byte Saturation, byte Brightness,
            byte Speed, byte PerKeyType)
        {
            /// <summary>The reports that set it again: the per-key type, then
            /// the effect, the color, the brightness and the speed, in
            /// OpenRGB's SetMode order (lines 340-346).</summary>
            public byte[][] Restore() => new[]
            {
                Packet(KcRgb, KcPerKeySetType, PerKeyType),
                Packet(ViaSetValue, RgbMatrixChannel, RgbEffect, Effect),
                Packet(ViaSetValue, RgbMatrixChannel, RgbColor, Hue, Saturation),
                Packet(ViaSetValue, RgbMatrixChannel, RgbBrightness, Brightness),
                Packet(ViaSetValue, RgbMatrixChannel, RgbSpeed, Speed),
            };
        }

        /// <summary>The matrix patch of a product, the first one listed for
        /// it (lines 204-230), or null.</summary>
        internal static KeychronPatchCell[] PatchOf(ushort productId)
        {
            foreach (var (pid, cells) in QmkTables.KeychronPatches)
                if (pid == productId) return cells;
            return null;
        }

        internal sealed class KeychronSession : HidLightingSession
        {
            private readonly int _count;
            private readonly byte[][] _restore;
            private bool _direct;

            private KeychronSession(int count, LightMap map, KeychronState state)
            {
                _count = count;
                Map = map;
                State = state;
                _restore = state.Restore();
            }

            public KeychronState State { get; }

            public override LightMap Map { get; }

            public override bool Feature => false;

            /// <summary>The reports that set the keyboard's own effect again,
            /// from the first picture on.</summary>
            public override byte[][] CrashFrames => _direct ? _restore : Array.Empty<byte[]>();

            public static HidLightingSession Begin(IHidReportChannel io, ushort vendorId, ushort productId)
            {
                // The keyboard behind a dongle names its own product
                // (lines 89-103, 500-538).
                ushort pid = productId;
                var wireless = KeychronCommand(io, KcWirelessDeviceInfo);
                if (wireless != null && wireless[1] == 0x01)
                {
                    ushort wVid = (ushort)(wireless[2] | wireless[3] << 8);
                    ushort wPid = (ushort)(wireless[4] | wireless[5] << 8);
                    if (wVid != 0 && wPid != 0) pid = wPid;
                }
                // The supported features, QMK and ZMK putting them a byte
                // apart (lines 457-480).
                ushort features = 0;
                var support = KeychronCommand(io, KcSupportFeature);
                if (support != null)
                    features = support[1] == 0
                        ? (ushort)(support[3] << 8 | support[2])
                        : (ushort)(support[2] << 8 | support[1]);
                ushort rgbVersion = 0;
                var version = KeychronSub(io, KcRgb, KcRgbProtocolVersion);
                if (version != null) rgbVersion = (ushort)(version[2] << 8 | version[3]);
                if ((features & KcFeatureRgb) == 0 && rgbVersion == 0) return null;

                var countAnswer = KeychronSub(io, KcRgb, KcRgbLedCount);
                int count = countAnswer == null ? 0 : countAnswer[2] << 8 | countAnswer[3];
                // Each LED's first place in rows 0 to 31 (lines 172-185).
                var rows = new int[count];
                var cols = new int[count];
                var valid = new bool[count];
                for (int row = 0; row < KcRows; row++)
                {
                    var exchange = Exchange(io, Packet(KcRgb, KcRgbLedIndex, (byte)row, 0xFF, 0xFF, 0xFF), out int received);
                    if (exchange == null || exchange[0] != KcRgb || exchange[1] != KcRgbLedIndex) continue;
                    int bytes = received - 2;
                    for (int i = 1; i < bytes; i++)
                    {
                        int led = exchange[2 + i];
                        int col = i - 1;
                        if (led == 0xFF || led >= count || valid[led]) continue;
                        valid[led] = true;
                        rows[led] = row;
                        cols[led] = col;
                    }
                }
                var keycodes = new ushort[count];
                for (int led = 0; led < count; led++)
                {
                    if (!valid[led]) continue;
                    var answer = KeychronCommand(io, ViaGetKeycode, 0, (byte)rows[led], (byte)cols[led]);
                    keycodes[led] = answer == null ? (ushort)0 : (ushort)(answer[4] << 8 | answer[5]);
                }
                // The patch moves LEDs on the picture only (lines 202-230).
                var drawRows = (int[])rows.Clone();
                var drawCols = (int[])cols.Clone();
                var patch = PatchOf(pid);
                if (patch != null)
                {
                    foreach (var cell in patch)
                        for (int led = 0; led < count; led++)
                        {
                            if (!valid[led] || rows[led] != cell.SourceRow || cols[led] != cell.SourceCol) continue;
                            drawRows[led] = cell.Row;
                            drawCols[led] = cell.Col;
                            break;
                        }
                }
                var lights = new List<LightInfo>(count);
                for (int led = 0; led < count; led++)
                {
                    var light = Light(led, valid[led], keycodes[led], 0);
                    if (valid[led]) light = light with { X = drawCols[led], Y = drawRows[led], Width = 1, Height = 1, HasPosition = true };
                    lights.Add(light);
                }
                var map = new LightMap(lights, "QMK Keychron, from the keyboard") { Product = Product(vendorId, pid) };

                // What the keyboard shows now (via.c:684-708,
                // keychron_rgb.c:164-169).
                byte effect = ValueOf(io, RgbEffect, out _);
                byte hue = ValueOf(io, RgbColor, out byte saturation);
                byte brightness = ValueOf(io, RgbBrightness, out _);
                byte speed = ValueOf(io, RgbSpeed, out _);
                byte type = KeychronSub(io, KcRgb, KcPerKeyGetType)?[3] ?? KcPerKeySolid;
                var state = new KeychronState(effect, hue, saturation, brightness, speed, type);
                return new KeychronSession(count, map, state);
            }

            /// <summary>A VIA RGB matrix value's two data bytes, 0 when it did
            /// not answer.</summary>
            private static byte ValueOf(IHidReportChannel io, byte id, out byte second)
            {
                second = 0;
                var answer = KeychronCommand(io, ViaGetValue, RgbMatrixChannel, id);
                if (answer == null || answer[1] != RgbMatrixChannel || answer[2] != id) return 0;
                second = answer[4];
                return answer[3];
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct)
                {
                    foreach (var packet in KeychronDirect())
                        if (Exchange(io, packet, out _) == null) return HidLightingPaint.Failed;
                    _direct = true;
                }
                // One color per LED index, black for an LED the picture
                // does not name. OpenRGB passes the count in a byte
                // (RGBController_QMKKeychron.cpp:205-208).
                int count = (byte)_count;
                var byIndex = new int[count];
                int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int i = 0; i < n; i++)
                    if ((uint)lights[i].Address < (uint)count) byIndex[lights[i].Address] = colors[i];
                foreach (var frame in KeychronFrames(count, byIndex))
                    if (Exchange(io, frame, out _) == null) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_direct) return true;
                foreach (var packet in _restore)
                    if (Exchange(io, packet, out _) == null) return false;
                _direct = false;
                return true;
            }
        }

        // ─────────────────────────────────────────────
        //  VialRGB
        // ─────────────────────────────────────────────

        /// <summary>The Vial and VialRGB commands (QMKVialRGBController.h:21-100).</summary>
        internal const byte VialCommand = 0xFE;
        internal const byte VialGetKeyboardId = 0x00;
        internal const byte GetProtocolVersion = 0x01;
        internal const byte VialRgbGetInfo = 0x40;
        internal const byte VialRgbGetMode = 0x41;
        internal const byte VialRgbGetSupported = 0x42;
        internal const byte VialRgbGetNumberLeds = 0x43;
        internal const byte VialRgbGetLedInfo = 0x44;
        internal const byte VialRgbSetMode = 0x41;
        internal const byte VialRgbDirectFastSet = 0x42;
        internal const ushort VialEffectDirect = 1;
        internal const ushort VialEffectSkip = 0xFFFF;

        /// <summary>SendCheckCommand: the command, the sub command and the
        /// data, and the answer from byte 2 (QMKVialRGBController.cpp:450-476).
        /// Null when the write failed.</summary>
        internal static byte[] VialCheck(IHidReportChannel io, byte cmd, byte sub, params byte[] data)
        {
            var bytes = new byte[2 + data.Length];
            bytes[0] = cmd;
            bytes[1] = sub;
            Array.Copy(data, 0, bytes, 2, data.Length);
            var answer = Exchange(io, Packet(bytes), out _);
            if (answer == null) return null;
            var tail = new byte[PacketSize - 2];
            Array.Copy(answer, 2, tail, 0, tail.Length);
            return tail;
        }

        /// <summary>SendCommand: the same, with the answer whole
        /// (lines 422-448).</summary>
        internal static byte[] VialSend(IHidReportChannel io, byte cmd, byte sub, params byte[] data)
        {
            var bytes = new byte[2 + data.Length];
            bytes[0] = cmd;
            bytes[1] = sub;
            Array.Copy(data, 0, bytes, 2, data.Length);
            return Exchange(io, Packet(bytes), out _);
        }

        /// <summary>The mode report, CmdSetMode (lines 401-420): the mode in
        /// two bytes, low first, the speed, hue, saturation and value.</summary>
        internal static byte[] VialMode(ushort mode, byte speed, byte hue, byte sat, byte val)
            => Packet(LightingSet, VialRgbSetMode, (byte)mode, (byte)(mode >> 8), speed, hue, sat, val);

        private const byte LightingSet = 0x07;
        private const byte LightingGet = 0x08;

        /// <summary>A direct colors report, CmdSendLEDs (lines 366-399): the
        /// first index in two bytes, low first, the count, then up to nine
        /// hue, saturation and value triplets, 30 bytes in all.</summary>
        internal static byte[] VialColors(ushort start, byte count, IReadOnlyList<int> colors, int from)
        {
            var bytes = new byte[2 + 30];
            bytes[0] = LightingSet;
            bytes[1] = VialRgbDirectFastSet;
            bytes[2] = (byte)start;
            bytes[3] = (byte)(start >> 8);
            bytes[4] = count;
            int n = Math.Min((int)count, LedsPerReport);
            for (int i = 0; i < n; i++)
            {
                var (h, s, v) = Hsv(colors[from + i]);
                bytes[5 + i * 3] = h;
                bytes[6 + i * 3] = s;
                bytes[7 + i * 3] = v;
            }
            return Packet(bytes);
        }

        /// <summary>SendLEDs (lines 209-229).</summary>
        internal static List<byte[]> VialFrames(int count, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int start = 0;
            while (start < count)
            {
                int packet = Math.Min(LedsPerReport, count - start);
                frames.Add(VialColors((ushort)start, (byte)packet, colors, start));
                start += packet;
            }
            return frames;
        }

        internal sealed class VialSession : HidLightingSession
        {
            private readonly int _count;
            private readonly byte[][] _restore;
            private bool _direct;

            private VialSession(int count, LightMap map, (ushort Mode, byte Speed, byte H, byte S, byte V) original)
            {
                _count = count;
                Map = map;
                Original = original;
                _restore = new[] { VialMode(original.Mode, original.Speed, original.H, original.S, original.V) };
            }

            public (ushort Mode, byte Speed, byte H, byte S, byte V) Original { get; }

            public override LightMap Map { get; }

            public override bool Feature => false;

            public override byte[][] CrashFrames => _direct ? _restore : Array.Empty<byte[]>();

            public static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                // VIA 9 or more (lines 83-88). OpenRGB copies four bytes
                // from byte 2 into a two-byte field: the low two count.
                var via = VialCheck(io, GetProtocolVersion, 0);
                if (via == null || (via[0] | via[1] << 8) < 9) return null;
                // Vial's ID answer is the protocol in four bytes, the
                // keyboard's eight-byte UID and the flags (lines 90-96, 328-342).
                var id = VialSend(io, VialCommand, VialGetKeyboardId);
                if (id == null) return null;
                uint vial = (uint)(id[0] | id[1] << 8 | id[2] << 16 | id[3] << 24);
                if (vial < 4 || (id[12] & 1) == 0) return null;
                if (VialCheck(io, LightingGet, VialRgbGetInfo) == null) return null;
                // The supported effects, 15 a report after the highest so
                // far, to the end marker (lines 303-326).
                var effects = new List<ushort> { 0 };
                ushort highest = 0;
                bool done = false;
                for (int guard = 0; !done && highest < VialEffectSkip && guard < 64; guard++)
                {
                    var list = VialCheck(io, LightingGet, VialRgbGetSupported, (byte)highest, (byte)(highest >> 8));
                    if (list == null) return null;
                    for (int i = 0; i < 15; i++)
                    {
                        ushort effect = (ushort)(list[i * 2] | list[i * 2 + 1] << 8);
                        if (effect == VialEffectSkip)
                        {
                            done = true;
                            break;
                        }
                        effects.Add(effect);
                        highest = effect;
                    }
                }
                if (!effects.Contains(VialEffectDirect)) return null;
                var number = VialCheck(io, LightingGet, VialRgbGetNumberLeds);
                int count = number == null ? 0 : number[0] | number[1] << 8;
                var lights = new List<LightInfo>(count);
                for (int led = 0; led < count; led++)
                {
                    var info = VialCheck(io, LightingGet, VialRgbGetLedInfo, (byte)led, (byte)(led >> 8)) ?? new byte[6];
                    byte x = info[0], y = info[1], flags = info[2], row = info[3], col = info[4];
                    // The keycode at the LED's place, read with OpenRGB's
                    // byte order (lines 243-261).
                    var key = VialCheck(io, ViaGetKeycode, 0, row, col) ?? new byte[5];
                    ushort keycode = (ushort)(key[3] | key[4] << 8);
                    bool onKey = row != 0xFF && col != 0xFF;
                    lights.Add(Light(led, onKey, keycode, flags) with { X = x, Y = y, HasPosition = true });
                }
                var mode = VialCheck(io, LightingGet, VialRgbGetMode) ?? new byte[6];
                var original = ((ushort)(mode[0] | mode[1] << 8), mode[2], mode[3], mode[4], mode[5]);
                var map = new LightMap(lights, "QMK VialRGB, from the keyboard") { Product = product };
                return new VialSession(count, map, original);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct)
                {
                    // Direct with speed and color 0 (RGBController_QMKVialRGB.cpp:256-272).
                    if (Exchange(io, VialMode(VialEffectDirect, 0, 0, 0, 0), out _) == null) return HidLightingPaint.Failed;
                    _direct = true;
                }
                var byIndex = new int[_count];
                int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int i = 0; i < n; i++)
                    if ((uint)lights[i].Address < (uint)_count) byIndex[lights[i].Address] = colors[i];
                foreach (var frame in VialFrames(_count, byIndex))
                    if (Exchange(io, frame, out _) == null) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_direct) return true;
                if (Exchange(io, _restore[0], out _) == null) return false;
                _direct = false;
                return true;
            }
        }
    }
}
