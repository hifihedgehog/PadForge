using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of a Wooting keyboard as OpenRGB's SetupZones makes
    /// it: its row and column in the type's grid, its index in the 6 by 21
    /// frame buffer, and OpenRGB's name for it
    /// (RGBController_WootingKeyboard.cpp:690-716).</summary>
    internal readonly record struct WootingLed(byte Row, byte Col, byte Value, string Name);

    /// <summary>
    /// The Wooting keyboards, lit as OpenRGB's WootingKeyboardController
    /// lights them (#557): the One and Two with the first firmware, under
    /// Atmel's vendor ID, and every keyboard under Wooting's own, the One,
    /// Two, Two Lekker Edition, Two HE, 60HE, 60HE+, 60HEv2, 80HE and the
    /// UwU RGB keypad, each in its three product IDs, one per gamepad mode
    /// (WootingKeyboardControllerDetect.cpp:46-54 and 182-206).
    ///
    /// <para>The collection and the protocol. OpenRGB opens the collection
    /// on Wooting's configuration usage page and picks the protocol by it
    /// (lines 121-171): the first firmware's under Atmel's vendor ID on
    /// page 0x1337, the second on page 0x1337, and the third, from firmware
    /// 2.12, on page 0xFF55. Any other collection of these products makes
    /// no controller. The product ID with its last nibble masked picks the
    /// keyboard's type, and an unknown one is taken for a full-size board
    /// (lines 71-117).</para>
    ///
    /// <para>The frames. A session starts with the color init command, an
    /// 8-byte feature report 00 D0 DA 21, and 50 ms
    /// (WootingKeyboardController.cpp:64-90), as OpenRGB's controllers do
    /// when they open the keyboard, whose answer OpenRGB does not read. A
    /// picture is the 126 colors of the frame buffer, each light at the
    /// index its table gives (RGBController_WootingKeyboard.cpp:732-743),
    /// sent as one output report or more. The first protocol: five reports
    /// of 129 bytes, 00 D0 DA 0B, the LED driver and the register, 96 bytes
    /// of the driver's PWM memory and a CRC-16/CCITT of the 127 bytes before
    /// it, low byte first (WootingV1KeyboardController.cpp:85-213). The
    /// second: one report of 257 bytes, 00 D0 DA 0B and each color in 16
    /// bits, red 5, green 6 and blue 5, low byte first
    /// (WootingV2KeyboardController.cpp:17-72). The third: the same colors
    /// in a report 0x04 of 2046 bytes, 04 D1 DA 0B
    /// (WootingV3KeyboardController.cpp:16-75).</para>
    ///
    /// <para>Where OpenRGB and Wooting's own SDK differ. A keyboard whose
    /// configuration collection takes output reports of 64 bytes takes the
    /// second protocol's report in four, each a report 0 with the next 64
    /// bytes of the frame after its ID byte (wooting-rgb-sdk
    /// wooting-usb.c:444-475 reads that from the report descriptor, and
    /// 620-640 sends it). OpenRGB writes the 257 bytes whole, which such a
    /// collection cannot take, so a session splits the frame as the SDK
    /// does when the collection's output report is shorter than the frame.
    /// The SDK sends the third protocol's frame as report 0x05 of 2047 bytes
    /// (lines 656-682) and its commands as report 0x01 with D1 DA
    /// (lines 684-701). A session sends OpenRGB's frame and init. No
    /// reference reads the third protocol's report descriptor.</para>
    ///
    /// <para>What the writes do to the keyboard. OpenRGB's Direct mode is
    /// the keyboard's only one and is stored nowhere ("@save :x:",
    /// RGBController_WootingKeyboard.cpp:602-611). OpenRGB sends nothing
    /// when it lets a keyboard go. Wooting's SDK does: its reset command 32
    /// puts back the colors the keyboard had before the SDK wrote any, and
    /// the SDK sends it when an application closes
    /// (wooting-rgb-sdk.h:69-93, wooting-rgb-sdk.c:106-124,
    /// wooting-usb.h:96). So the hand-back and the crash path send that
    /// command, in the SDK's form for the protocol: 00 D0 DA 20 for the
    /// first two, 01 D1 DA 20 for the third (wooting-usb.c:684-701).</para>
    ///
    /// <para>Wootility lights these keyboards, and while it runs the family
    /// leaves them to it (<see cref="VendorPrograms.WootingWootility"/>).</para>
    /// </summary>
    internal sealed class WootingKeyboardLighting : IHidLightingFamily
    {
        public static readonly WootingKeyboardLighting Instance = new();

        internal const string FamilyName = "WootingKeyboardController";

        /// <summary>WOOTING_OLD_VID and WOOTING_NEW_VID
        /// (WootingKeyboardControllerDetect.cpp:26-27).</summary>
        internal const ushort AtmelVendorId = 0x03EB;
        internal const ushort WootingVendorId = 0x31E3;

        /// <summary>WOOTING_CONFIG_USAGE_PAGE_V2 and _V3 (lines 20-21).</summary>
        internal const ushort PageV2 = 0x1337;
        internal const ushort PageV3 = 0xFF55;

        /// <summary>The product IDs (lines 32-44).</summary>
        internal const ushort OneLegacy = 0xFF01;
        internal const ushort TwoLegacy = 0xFF02;
        internal const ushort One = 0x1100;
        internal const ushort Two = 0x1200;
        internal const ushort TwoLe = 0x1210;
        internal const ushort TwoHe = 0x1220;
        internal const ushort TwoHeArm = 0x1230;
        internal const ushort SixtyHe = 0x1300;
        internal const ushort SixtyHeArm = 0x1310;
        internal const ushort SixtyHePlus = 0x1320;
        internal const ushort SixtyHeV2 = 0x1340;
        internal const ushort EightyHe = 0x1400;
        internal const ushort UwuRgb = 0x1510;

        /// <summary>WOOTING_COMMAND_SIZE and the commands
        /// (WootingKeyboardController.h:20-25).</summary>
        internal const int CommandSize = 8;
        internal const byte RawColorsReport = 11;
        internal const byte ResetAllCommand = 32;
        internal const byte ColorInitCommand = 33;

        /// <summary>The wait after the color init command
        /// (WootingKeyboardController.cpp:68).</summary>
        internal const int InitWaitMs = 50;

        /// <summary>The frame buffer, 6 rows of 21 (WootingKeyboardController.h:28-29).</summary>
        internal const int Rows = 6;
        internal const int Columns = 21;
        internal const int FrameColors = Rows * Columns;

        /// <summary>The report sizes, the report ID included
        /// (WootingV1KeyboardController.h:16, WootingV2KeyboardController.cpp:17,
        /// WootingV3KeyboardController.cpp:16).</summary>
        internal const int V1ReportSize = 129;
        internal const int V2ReportSize = 257;
        internal const int V3ReportSize = 2046;

        /// <summary>The first protocol's driver buffer and the highest LED
        /// driver index OpenRGB sends, WOOTING_TWO_KEY_CODE_LIMIT
        /// (WootingV1KeyboardController.cpp:21 and 63, .h:18).</summary>
        internal const int V1BufferSize = 96;
        internal const int V1KeyCodeLimit = 116;

        /// <summary>WOOTING_SMALL_PACKET_SIZE and _COUNT
        /// (wooting-rgb-sdk wooting-usb.c:25-26).</summary>
        internal const int SmallPacketSize = 64;
        internal const int SmallPacketCount = 4;

        /// <summary>The shortest time between two pictures. Neither OpenRGB
        /// nor the SDK bounds it, so the bound is PadForge's.</summary>
        internal const int RepaintMs = 33;

        public string Name => "Wooting";

        /// <summary>The protocol a collection speaks.</summary>
        internal enum Protocol
        {
            V1,
            V2,
            V3,
        }

        /// <summary>WOOTING_DEVICE_TYPE (WootingKeyboardController.h:41-48).</summary>
        internal enum KeyboardType
        {
            Tkl = 0,
            Full = 1,
            Sixty = 2,
            ThreePad = 3,
            Eighty = 4,
        }

        private readonly Dictionary<(ushort, ushort, Protocol), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            if (ProtocolOf(collection.VendorId, collection.UsagePage) is not Protocol protocol) return null;
            var key = (collection.VendorId, collection.ProductId, protocol);
            lock (_models)
            {
                if (_models.TryGetValue(key, out var model)) return model;
                model = Model(registrations[0].Name, collection.VendorId, collection.ProductId, protocol);
                _models[key] = model;
                return model;
            }
        }

        /// <summary>The protocol of a collection, or null for one OpenRGB
        /// makes no controller of (WootingKeyboardControllerDetect.cpp:121-171).</summary>
        internal static Protocol? ProtocolOf(ushort vendorId, ushort usagePage)
        {
            if (vendorId == AtmelVendorId && usagePage == PageV2) return Protocol.V1;
            if (usagePage == PageV2) return Protocol.V2;
            if (usagePage == PageV3) return Protocol.V3;
            return null;
        }

        /// <summary>The keyboard's type by its product ID (lines 71-117). The
        /// masked switch lists the two legacy IDs too, as OpenRGB's does,
        /// where a masked ID never equals them.</summary>
        internal static KeyboardType TypeOf(ushort productId)
        {
            if (productId == OneLegacy) return KeyboardType.Tkl;
            if (productId == TwoLegacy) return KeyboardType.Full;
            return (productId & 0xFFF0) switch
            {
                OneLegacy or One => KeyboardType.Tkl,
                TwoLegacy or Two or TwoLe or TwoHe or TwoHeArm => KeyboardType.Full,
                SixtyHe or SixtyHeArm or SixtyHePlus or SixtyHeV2 => KeyboardType.Sixty,
                EightyHe => KeyboardType.Eighty,
                UwuRgb => KeyboardType.ThreePad,
                _ => KeyboardType.Full,
            };
        }

        /// <summary>The product a map is stored under: one per keyboard,
        /// whichever gamepad mode its product ID carries.</summary>
        internal static string Product(ushort vendorId, ushort productId)
            => vendorId == AtmelVendorId
                ? $"wooting:{vendorId:X4}:{productId:X4}"
                : $"wooting:{vendorId:X4}:{productId & 0xFFF0:X4}";

        private HidLightingModel Model(string name, ushort vendorId, ushort productId, Protocol protocol)
        {
            string product = Product(vendorId, productId);
            var type = TypeOf(productId);
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = new LightMap(Lights(type), "OpenRGB RGBController_WootingKeyboard.cpp") { Product = product },
                Programs = VendorPrograms.Wooting,
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Begin(io, wait, protocol),
            };
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The table SetupZones takes for a type
        /// (RGBController_WootingKeyboard.cpp:654-688).</summary>
        internal static WootingLed[] Table(KeyboardType type) => type switch
        {
            KeyboardType.Tkl => WootingLayouts.Tkl,
            KeyboardType.Eighty => WootingLayouts.EightyHe,
            KeyboardType.Sixty => WootingLayouts.Sixty,
            KeyboardType.ThreePad => WootingLayouts.ThreePad,
            _ => WootingLayouts.Full,
        };

        /// <summary>
        /// The lights of a type, in OpenRGB's order, each addressed by its
        /// frame buffer index and placed at its grid cell. A key's usage
        /// comes from OpenRGB's name for it (<see cref="OpenRgbKeys"/>), with
        /// the names these tables use that the table does not hold: "Key: +"
        /// is the key between - and Backspace (lines 69-72, 353-357 and
        /// 464-467), the = key. "Spacebar LED 1" to 4 light the space bar
        /// beside its own light (lines 427-431 and 526-530). "Key: JIS
        /// Specific TODO" marks the 80HE's places for the JIS keys, which
        /// OpenRGB leaves unnamed (lines 356, 416, 426 and 432), and the
        /// UwU's "Keypad 1" to 3 are its three keys, whose usages the
        /// keypad's own setup decides (lines 572-576): each is a key no
        /// source names. The UwU's lightbar lights are edge lights, OpenRGB
        /// calling the first "Lighbar 1" (lines 547-599). The Two's three
        /// keys over the number pad and its Mode key, "Key: Custom1" to 3
        /// and "Key: Mode", send no usage and are other lights, as the
        /// layouts' lights without a usage are (KeyboardLayoutManager.Lights).
        /// </summary>
        internal static IReadOnlyList<LightInfo> Lights(KeyboardType type)
        {
            var table = Table(type);
            var lights = new List<LightInfo>(table.Length);
            foreach (var led in table)
            {
                LightInfo light;
                if (led.Name == "Key: +")
                    light = new LightInfo(led.Value, LightKind.Key, 0x2E, LightKeySource.Generated);
                else if (led.Name.StartsWith("Spacebar LED ", StringComparison.Ordinal))
                    light = new LightInfo(led.Value, LightKind.Key, 0x2C, LightKeySource.Generated);
                else if (led.Name == "Key: JIS Specific TODO" || led.Name.StartsWith("Keypad ", StringComparison.Ordinal))
                    light = new LightInfo(led.Value, LightKind.Key, 0, LightKeySource.None);
                else if (led.Name.StartsWith("Lightbar ", StringComparison.Ordinal)
                         || led.Name.StartsWith("Lighbar ", StringComparison.Ordinal))
                    light = new LightInfo(led.Value, LightKind.Edge, 0, LightKeySource.None);
                else if (OpenRgbKeys.TryGet(led.Name, out ushort usage, out var label, out int number) && usage != 0)
                    light = new LightInfo(led.Value, LightKind.Key, usage, LightKeySource.Generated);
                else
                    light = new LightInfo(led.Value, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
                lights.Add(light with { X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true });
            }
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>A command as OpenRGB sends it, wooting_usb_send_feature:
        /// 00 D0 DA, the command, then the parameters from the fourth down
        /// (WootingKeyboardController.cpp:71-90).</summary>
        internal static byte[] Command(byte command)
            => new byte[] { 0x00, 0xD0, 0xDA, command, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>A command in the SDK's form for the protocol: report 0x01
        /// and D1 DA for the third, report 0 and D0 DA before it
        /// (wooting-rgb-sdk wooting-usb.c:684-701).</summary>
        internal static byte[] SdkCommand(Protocol protocol, byte command)
            => protocol == Protocol.V3
                ? new byte[] { 0x01, 0xD1, 0xDA, command, 0x00, 0x00, 0x00, 0x00 }
                : Command(command);

        /// <summary>The 126 frame buffer colors of a picture, each light's at
        /// its index, black where no light is
        /// (RGBController_WootingKeyboard.cpp:732-743).</summary>
        internal static int[] FrameBuffer(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var buffer = new int[FrameColors];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int index = lights[n].Address;
                if ((uint)index < FrameColors) buffer[index] = colors[n] & 0xFFFFFF;
            }
            return buffer;
        }

        /// <summary>A 0xRRGGBB color in 16 bits, red 5, green 6 and blue 5,
        /// RGB32ToRGBcolor16 (WootingV2KeyboardController.cpp:26-27).</summary>
        internal static ushort Color16(int rgb)
            => (ushort)((((rgb >> 16) & 0xF8) << 8) | (((rgb >> 8) & 0xFC) << 3) | ((rgb & 0xF8) >> 3));

        /// <summary>The second and third protocols' frame: the report ID and
        /// the magic, then each color in 16 bits, low byte first
        /// (WootingV2KeyboardController.cpp:57-72,
        /// WootingV3KeyboardController.cpp:56-75).</summary>
        internal static byte[] ColorFrame(Protocol protocol, int[] buffer)
        {
            bool v3 = protocol == Protocol.V3;
            var frame = new byte[v3 ? V3ReportSize : V2ReportSize];
            frame[0] = (byte)(v3 ? 0x04 : 0x00);
            frame[1] = (byte)(v3 ? 0xD1 : 0xD0);
            frame[2] = 0xDA;
            frame[3] = RawColorsReport;
            for (int i = 0; i < FrameColors; i++)
            {
                ushort color = Color16(buffer[i]);
                frame[4 + i * 2] = (byte)color;
                frame[5 + i * 2] = (byte)(color >> 8);
            }
            return frame;
        }

        /// <summary>The second protocol's frame in four reports of 65 bytes,
        /// each a report 0 with the next 64 bytes after the frame's ID byte
        /// (wooting-rgb-sdk wooting-usb.c:620-640).</summary>
        internal static List<byte[]> SmallPackets(byte[] frame)
        {
            var packets = new List<byte[]>(SmallPacketCount);
            for (int i = 0; i < SmallPacketCount; i++)
            {
                var packet = new byte[SmallPacketSize + 1];
                Array.Copy(frame, i * SmallPacketSize + 1, packet, 1, SmallPacketSize);
                packets.Add(packet);
            }
            return packets;
        }

        /// <summary>getCrc16ccitt (WootingV1KeyboardController.cpp:33-55).</summary>
        internal static ushort Crc16Ccitt(byte[] buffer, int count)
        {
            ushort crc = 0;
            for (int n = 0; n < count; n++)
            {
                crc ^= (ushort)(buffer[n] << 8);
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        /// <summary>The first protocol's five reports: each frame buffer
        /// cell's color goes to its LED's driver buffer, red, green and blue
        /// 0x10 apart at the LED's place, a cell whose LED index is past the
        /// limit nowhere, and each buffer goes out with its driver and
        /// register and a CRC (WootingV1KeyboardController.cpp:85-213).</summary>
        internal static List<byte[]> V1Frames(int[] buffer)
        {
            var parts = new byte[5][];
            for (int p = 0; p < parts.Length; p++) parts[p] = new byte[V1BufferSize];
            for (int index = 0; index < FrameColors; index++)
            {
                int led = WootingLayouts.V1LedIndex[index];
                if (led > V1KeyCodeLimit) continue;
                var part = parts[led >= 96 ? 4 : led >= 72 ? 3 : led >= 48 ? 2 : led >= 24 ? 1 : 0];
                int at = WootingLayouts.V1PwmMap[led % 24];
                int rgb = buffer[index];
                part[at] = (byte)(rgb >> 16);
                part[at + 0x10] = (byte)(rgb >> 8);
                part[at + 0x20] = (byte)rgb;
            }
            var frames = new List<byte[]>(parts.Length);
            for (int p = 0; p < parts.Length; p++)
            {
                var report = new byte[V1ReportSize];
                report[1] = 0xD0;
                report[2] = 0xDA;
                report[3] = RawColorsReport;
                // Driver 0 for parts 0 and 1, 1 for 2 and 3, 2 for part 4,
                // and the register 0 or 96 (lines 163-193).
                report[4] = (byte)(p / 2);
                report[5] = (byte)(p % 2 == 1 ? V1BufferSize : 0);
                Array.Copy(parts[p], 0, report, 6, V1BufferSize);
                ushort crc = Crc16Ccitt(report, V1ReportSize - 2);
                report[127] = (byte)crc;
                report[128] = (byte)(crc >> 8);
                frames.Add(report);
            }
            return frames;
        }

        /// <summary>The output reports of a picture in a protocol, for a
        /// collection whose output reports are <paramref name="outputLength"/>
        /// bytes long, 0 when unknown.</summary>
        internal static List<byte[]> Frames(Protocol protocol, int outputLength, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            var buffer = FrameBuffer(lights, colors);
            if (protocol == Protocol.V1) return V1Frames(buffer);
            var frame = ColorFrame(protocol, buffer);
            if (protocol == Protocol.V2 && outputLength > 0 && outputLength < V2ReportSize)
                return SmallPackets(frame);
            return new List<byte[]> { frame };
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        /// <summary>Starts a session: the color init command and the wait
        /// after it. OpenRGB sends the command and goes on whatever the
        /// keyboard does with it (WootingKeyboardController.cpp:64-69), and
        /// so does a session.</summary>
        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, Protocol protocol)
        {
            io.SetFeature(Command(ColorInitCommand));
            wait(InitWaitMs);
            return new Session(protocol);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly byte[][] _crash;

            public Session(Protocol protocol)
            {
                Protocol = protocol;
                _crash = new[] { SdkCommand(protocol, ResetAllCommand) };
            }

            public Protocol Protocol { get; }

            /// <summary>The crash path's frame is a feature report.</summary>
            public override bool Feature => true;

            /// <summary>The reset command, from the session's start, since
            /// the keyboard shows the session's colors from its first
            /// picture.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Frames(Protocol, io.OutputReportLength, lights, colors))
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
                => io.SetFeature(_crash[0]);
        }
    }
}
