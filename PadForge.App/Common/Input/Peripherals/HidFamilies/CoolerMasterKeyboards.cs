using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's MasterKeys keyboards (#557), as OpenRGB's
    /// CMKeyboardV1Controller and CMKeyboardV2Controller light them, key by
    /// key, with the keys of OpenRGB's layouts (CMKeyboardDevices.cpp) in
    /// the region the keyboard's firmware names.
    ///
    /// <para>Every command is a write and a read of the answer
    /// (SendCommand, CMKeyboardAbstractController.cpp:88-126, and libcmmk's
    /// send_command, libcmmk.c:179-200). A session starts as OpenRGB's
    /// controller does: firmware control (41 00), then the firmware version
    /// (01 02 on V1, 12 20 on V2), whose first figure names the region. A V2
    /// session also reads the active profile (52 00), since a V2 picture
    /// switches the profile on most models (Initialize,
    /// CMKeyboardV2Controller.cpp:109-163).</para>
    ///
    /// <para>A picture goes out as OpenRGB's Direct mode sends it: V1 takes
    /// manual control (41 02) and eight frames of sixteen colors
    /// (CMKeyboardV1Controller.cpp:377-412). V2 takes control (41 05), runs
    /// the model's start sequence, sends ten frames of colors and applies
    /// them (51 28 00 00 FF, CMKeyboardV2Controller.cpp:197-276). None of it
    /// is the save command (50 55, CMKeyboardAbstractController.cpp:174-177),
    /// so nothing is stored.</para>
    ///
    /// <para>Hand-back. OpenRGB's Shutdown does nothing for either kind
    /// (CMKeyboardV1Controller.cpp:465-468, CMKeyboardV2Controller.cpp:165-168).
    /// libcmmk hands a keyboard back by setting firmware control, 41 00,
    /// before it lets go (test.c:175-176, cmmk_set_control_mode,
    /// libcmmk.c:484-489), and that is the frame OpenRGB itself sends for
    /// "FW" control (SetLEDControl(false)). A V2 session first sets the
    /// profile it read back (51 00 00 00 p, SetActiveProfile,
    /// CMKeyboardAbstractController.cpp:143-146), while it still has
    /// control, since its pictures may have switched it.</para>
    ///
    /// <para>The region. A V1 keyboard's firmware string starts with the
    /// region's figure, a V2's with "V" and then the figure: 1 ANSI, 2 ISO,
    /// 3 JIS, anything else ANSI (CMKeyboardV1Controller.cpp:470-500,
    /// CMKeyboardV2Controller.cpp:35-62). OpenRGB also takes "JP" and then
    /// "EU" in a V2 keyboard's product string over the firmware (lines
    /// 64-76; the string it calls the serial number is the product string,
    /// CMKeyboardAbstractController.cpp:26-28), and so does a session here,
    /// reading that string before anything is written, as OpenRGB's
    /// constructor does.</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        /// <summary>The layout family's name in KeyboardLayouts.g.cs.</summary>
        internal const string LayoutFamily = "CoolerMaster";

        /// <summary>CM_MAX_LEDS, the size of OpenRGB's color map
        /// (CMKeyboardAbstractController.h:28).</summary>
        internal const int MaxLeds = 255;

        /// <summary>The number of LEDs V2's first frame announces
        /// (nLEDs, CMKeyboardV2Controller.cpp:228).</summary>
        internal const byte V2LedCount = 0xC1;

        // The product IDs of CMKeyboardDevices.h:96-123.
        internal const ushort Ck530 = 0x009F;
        internal const ushort Ck530V2 = 0x0147;
        internal const ushort Ck550V2 = 0x0145;
        internal const ushort Ck552V2 = 0x007F;
        internal const ushort ProL = 0x003B;
        internal const ushort ProLWhite = 0x0047;
        internal const ushort ProS = 0x003C;
        internal const ushort Mk730 = 0x008F;
        internal const ushort Mk750 = 0x0067;
        internal const ushort Mk850 = 0x0069;
        internal const ushort Sk620B = 0x0157;
        internal const ushort Sk620W = 0x0159;
        internal const ushort Sk622B = 0x0149;
        internal const ushort Sk622W = 0x014B;
        internal const ushort Sk630 = 0x0089;
        internal const ushort Sk650 = 0x008D;
        internal const ushort Sk652 = 0x015D;
        internal const ushort Sk653 = 0x01AB;

        /// <summary>The keyboards DetectCoolerMasterV1Keyboards and the first
        /// case of DetectCoolerMasterV2Keyboards make a V1 controller for
        /// (CoolerMasterControllerDetect.cpp:160-162 and 191-193).</summary>
        internal static readonly ushort[] V1Keyboards = { ProL, ProLWhite, ProS };

        /// <summary>The keyboards DetectCoolerMasterV2Keyboards makes a V2
        /// controller for (CoolerMasterControllerDetect.cpp:202-213).</summary>
        internal static readonly ushort[] V2Keyboards =
            { Sk622B, Sk622W, Sk630, Sk650, Sk652, Sk653, Ck530, Ck530V2, Ck550V2, Ck552V2, Mk730, Mk750 };

        private static HidLightingModel KeyboardModel(string name, ushort productId)
        {
            bool v1 = Array.IndexOf(V1Keyboards, productId) >= 0;
            if (!v1 && Array.IndexOf(V2Keyboards, productId) < 0) return null;
            var lights = KeyboardLights(productId, KeyboardLayout.AnsiQwerty);
            if (lights == null) return null;
            return NewModel(name, productId, lights, "OpenRGB CMKeyboardDevices.cpp",
                (io, wait) => BeginKeyboard(io, productId, v1));
        }

        /// <summary>The keys of a keyboard in a region: the layout OpenRGB
        /// builds for the model (RGBController_CMKeyboardController.cpp:83-113),
        /// each light's address its LED value. Null for a product with no
        /// layout.</summary>
        internal static IReadOnlyList<LightInfo> KeyboardLights(ushort productId, KeyboardLayout region)
        {
            var row = KeyboardLayouts.ModelsOf(LayoutFamily, productId).FirstOrDefault();
            var layout = row == null ? null : KeyboardLayouts.Build(row.Recipe, region);
            return layout?.Lights((_, key) => (int)key.Value);
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>SetControlMode, 41 and the mode
        /// (CMKeyboardAbstractController.cpp:131-134).</summary>
        internal static byte[] ControlMode(byte mode) => Packet(stackalloc byte[] { 0x41, mode });

        /// <summary>SetActiveProfile, 51 00 00 00 and the profile
        /// (CMKeyboardAbstractController.cpp:143-146).</summary>
        internal static byte[] ActiveProfile(byte profile) => Packet(stackalloc byte[] { 0x51, 0x00, 0x00, 0x00, profile });

        /// <summary>GetActiveProfile's request (line 150).</summary>
        internal static byte[] ActiveProfileQuery() => Packet(stackalloc byte[] { 0x52, 0x00 });

        /// <summary>V1's firmware query (CMKeyboardV1Controller.cpp:445).</summary>
        internal static byte[] V1FirmwareQuery() => Packet(stackalloc byte[] { 0x01, 0x02 });

        /// <summary>V2's firmware query (CMKeyboardV2Controller.cpp:91).</summary>
        internal static byte[] V2FirmwareQuery() => Packet(stackalloc byte[] { 0x12, 0x20 });

        /// <summary>MODE_FIRMWARE, the firmware in control
        /// (CMKeyboardAbstractController.h:48).</summary>
        internal const byte FirmwareControl = 0x00;

        /// <summary>V1's manual control (CMKeyboardV1Controller.cpp:371).</summary>
        internal const byte V1Manual = 0x02;

        /// <summary>V2's manual control (CMKeyboardV2Controller.cpp:191).</summary>
        internal const byte V2Manual = 0x05;

        /// <summary>V1's eight color frames: C0 02, the frame's number times
        /// two, 00, then sixteen colors of the map
        /// (CMKeyboardV1Controller.cpp:393-411).</summary>
        internal static List<byte[]> V1ColorFrames(int[] map)
        {
            var frames = new List<byte[]>(8);
            for (int i = 0; i < 8; i++)
            {
                var frame = Packet(new byte[] { 0xC0, 0x02, (byte)(i * 2), 0x00 });
                for (int j = 0; j < 16; j++)
                {
                    int color = map[i * 16 + j];
                    frame[5 + 3 * j] = R(color);
                    frame[6 + 3 * j] = G(color);
                    frame[7 + 3 * j] = B(color);
                }
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>MagicCommand (CMKeyboardV2Controller.cpp:282-289).</summary>
        internal static byte[] Magic(byte profile) => Packet(stackalloc byte[]
        {
            0x56, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, profile, 0x00, 0x00, 0x00, 0xBB, 0xBB, 0xBB, 0xBB,
        });

        /// <summary>MagicStartupPacket (CMKeyboardV2Controller.cpp:173-183).</summary>
        internal static IEnumerable<byte[]> MagicStartup()
        {
            yield return Packet(new byte[] { 0x12 });
            yield return Packet(new byte[] { 0x12, 0x20 });
            yield return Packet(new byte[] { 0x12, 0x01 });
            yield return Packet(new byte[] { 0x12, 0x22 });
            yield return Packet(new byte[] { 0x42, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x01 });
            yield return Packet(new byte[] { 0x43, 0x00, 0x00, 0x00, 0x01 });
        }

        /// <summary>V2's Initialize for a model, which SetLeds runs ahead of
        /// every picture (CMKeyboardV2Controller.cpp:109-163 and 200).</summary>
        internal static List<byte[]> V2Initialize(ushort productId)
        {
            var frames = new List<byte[]>();
            switch (productId)
            {
                case Sk620B:
                case Sk620W:
                    frames.Add(Magic(0x09));
                    break;
                case Sk622B:
                case Sk622W:
                    frames.AddRange(MagicStartup());
                    frames.Add(Magic(0x09));
                    break;
                case Sk630:
                case Sk650:
                    frames.Add(ActiveProfile(0x0C));
                    break;
                case Sk652:
                case Sk653:
                    frames.AddRange(MagicStartup());
                    frames.Add(Magic(0x0C));
                    break;
                case Ck530:
                case Ck530V2:
                    frames.Add(ActiveProfile(0x05));
                    frames.Add(Magic(0x0A));
                    break;
                case Ck550V2:
                    frames.Add(ActiveProfile(0x05));
                    frames.Add(Magic(0x09));
                    break;
                case Mk730:
                    frames.Add(ActiveProfile(0x05));
                    frames.Add(Magic(0x0A));
                    break;
                case Ck552V2:
                    frames.Add(ControlMode(0x80));
                    frames.Add(Magic(0x01));
                    break;
                default:
                    frames.Add(Packet(stackalloc byte[]
                    {
                        0x56, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0xBB, 0xBB,
                    }));
                    break;
            }
            return frames;
        }

        /// <summary>V2's ten color frames: the first with its fixed 24 bytes
        /// and 40 bytes of the linear map, then nine of 56 83, the frame's
        /// number, 00 and 60 bytes each (CMKeyboardV2Controller.cpp:215-273).</summary>
        internal static List<byte[]> V2ColorFrames(int[] map)
        {
            var linear = new byte[MaxLeds * 3];
            for (int i = 0; i < MaxLeds; i++)
            {
                linear[i * 3] = R(map[i]);
                linear[i * 3 + 1] = G(map[i]);
                linear[i * 3 + 2] = B(map[i]);
            }
            var frames = new List<byte[]>(10);
            var first = Packet(stackalloc byte[]
            {
                0x56, 0x83, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                0x80, 0x01, 0x00, V2LedCount, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
            });
            linear.AsSpan(0, 40).CopyTo(first.AsSpan(25));
            frames.Add(first);
            int at = 40;
            for (int i = 1; i < 10; i++)
            {
                var frame = Packet(new byte[] { 0x56, 0x83, (byte)i, 0x00 });
                linear.AsSpan(at, 60).CopyTo(frame.AsSpan(5));
                at += 60;
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>SendApplyPacket(0xFF) (CMKeyboardV2Controller.cpp:275 and
        /// 349-352).</summary>
        internal static byte[] V2Apply() => Packet(stackalloc byte[] { 0x51, 0x28, 0x00, 0x00, 0xFF });

        // ─────────────────────────────────────────────
        //  The answers
        // ─────────────────────────────────────────────

        /// <summary>V1's firmware string: the answer from byte 4 to the first
        /// zero (CMKeyboardV1Controller.cpp:447-458).</summary>
        internal static string V1Firmware(byte[] answer)
        {
            if (answer == null || answer.Length <= 4) return string.Empty;
            int end = 4;
            while (end < answer.Length && end < PacketLength - 1 && answer[end] != 0) end++;
            return Encoding.ASCII.GetString(answer, 4, end - 4);
        }

        /// <summary>V2's firmware string: UTF-16 from byte 8 to the first zero
        /// character (CMKeyboardV2Controller.cpp:93-102).</summary>
        internal static string V2Firmware(byte[] answer)
        {
            if (answer == null || answer.Length < 10) return string.Empty;
            var text = new StringBuilder();
            for (int at = 8; at + 1 < answer.Length; at += 2)
            {
                char c = (char)(answer[at] | answer[at + 1] << 8);
                if (c == '\0') break;
                text.Append(c);
            }
            return text.ToString();
        }

        /// <summary>The region a firmware string names, its figure at
        /// <paramref name="at"/>: 2 ISO, 3 JIS, anything else, or no string,
        /// ANSI (CMKeyboardV1Controller.cpp:470-500 at 0,
        /// CMKeyboardV2Controller.cpp:35-62 at 1).</summary>
        internal static KeyboardLayout RegionOf(string firmware, int at)
        {
            if (string.IsNullOrEmpty(firmware) || firmware.Length <= at) return KeyboardLayout.AnsiQwerty;
            return firmware[at] switch
            {
                '2' => KeyboardLayout.IsoQwerty,
                '3' => KeyboardLayout.Jis,
                _ => KeyboardLayout.AnsiQwerty,
            };
        }

        /// <summary>A V2 keyboard's region: the firmware's, unless the product
        /// string holds "JP", which makes it JIS, or "EU", which makes it ISO
        /// and wins over "JP" (CMKeyboardV2Controller.cpp:64-76).</summary>
        internal static KeyboardLayout V2Region(KeyboardLayout firmware, string product)
        {
            var region = firmware;
            if (product != null && product.Contains("JP", StringComparison.Ordinal)) region = KeyboardLayout.Jis;
            if (product != null && product.Contains("EU", StringComparison.Ordinal)) region = KeyboardLayout.IsoQwerty;
            return region;
        }

        /// <summary>The profile in an answer to <see cref="ActiveProfileQuery"/>,
        /// byte 4 (CMKeyboardAbstractController.cpp:148-158, and libcmmk's
        /// cmmk_get_active_profile, libcmmk.c:498-509). Null for an answer
        /// that does not carry the request's second byte at byte 1, as the
        /// family's answers carry it (CMMMController.cpp:94-97), since a
        /// profile read wrong would be set on the hand-back.</summary>
        internal static byte? ProfileOf(byte[] answer)
            => answer != null && answer.Length > 4 && answer[1] == 0x00 ? answer[4] : null;

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        private static HidLightingSession BeginKeyboard(IHidReportChannel io, ushort productId, bool v1)
        {
            if (io == null) return null;
            string product = v1 ? null : io.ProductString;
            if (Exchange(io, ControlMode(FirmwareControl), required: true) == null) return null;
            var firmware = Exchange(io, v1 ? V1FirmwareQuery() : V2FirmwareQuery(), required: true);
            if (firmware == null) return null;
            var region = v1 ? RegionOf(V1Firmware(firmware), 0) : V2Region(RegionOf(V2Firmware(firmware), 1), product);
            byte? profile = null;
            if (!v1)
            {
                var answer = Exchange(io, ActiveProfileQuery(), required: true);
                if (answer == null) return null;
                profile = ProfileOf(answer);
            }
            return new KeyboardSession(productId, v1, region, profile);
        }

        internal sealed class KeyboardSession : Session
        {
            private readonly ushort _productId;
            private readonly LightMap _map;

            public KeyboardSession(ushort productId, bool v1, KeyboardLayout region, byte? profile)
            {
                _productId = productId;
                V1 = v1;
                Region = region;
                Profile = profile;
                if (region != KeyboardLayout.AnsiQwerty && KeyboardLights(productId, region) is { } lights)
                    _map = new LightMap(lights, "OpenRGB CMKeyboardDevices.cpp") { Product = Product(productId) };
                var handBack = new List<byte[]>(2);
                if (profile is byte p) handBack.Add(ActiveProfile(p));
                handBack.Add(ControlMode(FirmwareControl));
                SetHandBack(handBack.ToArray());
            }

            public bool V1 { get; }
            public KeyboardLayout Region { get; }
            public byte? Profile { get; }

            public override LightMap Map => _map;

            /// <summary>Every frame of one picture, in order.</summary>
            internal List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var map = ByAddress(lights, colors, MaxLeds);
                var frames = new List<byte[]>();
                if (V1)
                {
                    frames.Add(ControlMode(V1Manual));
                    frames.AddRange(V1ColorFrames(map));
                }
                else
                {
                    frames.Add(ControlMode(V2Manual));
                    frames.AddRange(V2Initialize(_productId));
                    frames.AddRange(V2ColorFrames(map));
                    frames.Add(V2Apply());
                }
                return frames;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // Owed first: a write that reports a failure may still have
                // landed.
                Owe();
                return ExchangeAll(io, PictureFrames(lights, colors), required: true)
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }
        }
    }
}
