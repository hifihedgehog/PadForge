using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The VSG Mintaka keyboard (#557), lit key by key as OpenRGB's
    /// MintakaKeyboardController lights it: 05AC:0256, interface 0, usage
    /// page 1, usage 6 (MintakaKeyboardControllerDetect.cpp:44), 65-byte
    /// feature reports with report ID 0 (MintakaKeyboardController.h:18-19,
    /// Send and Read, MintakaKeyboardController.cpp:276-302). Its keys are
    /// OpenRGB's 60% ISO layout of mintaka_offset_values, each at the place
    /// in the color pages that list gives it (RGBController_MintakaKeyboard.cpp:21-59
    /// and 253-286, SetLedSequencePositions).
    ///
    /// <para>The model of the device. OpenRGB lights it only in its Custom
    /// mode, and each picture rewrites the keyboard's whole LED effect area,
    /// all 18 pages (SetMode, MintakaKeyboardController.cpp:63-219): customization
    /// on (04 18), the start of the effect pages (04 13, 0x12 pages), five
    /// pages of the 20 effects with OpenRGB's defaults, three empty pages,
    /// nine pages of key colors, the page of the active effect, the end of
    /// the pages (04 02) and the start of the effect (04 F0). OpenRGB marks
    /// every mode of this keyboard as one that saves by itself
    /// (MODE_FLAG_AUTOMATIC_SAVE, RGBController_MintakaKeyboard.cpp:93 and
    /// 100-202), so each picture lands in the keyboard's memory, and the
    /// effects the user had set on the keyboard are replaced from the first
    /// picture on, which the row's line says
    /// (<see cref="HidLightingModel.ReplacesStoredEffects"/>). The new effects
    /// are OpenRGB's defaults with one change. OpenRGB's user sets each effect's
    /// color over a default of black, and PadForge has no such control, so an
    /// effect that takes a color is stored white, and the keyboard's own
    /// lighting key still shows it. A picture goes out once it has held for 150
    /// ms and no sooner than 500 ms after the one before, the spacing PadForge
    /// keeps for every write that lands in a keyboard's memory
    /// (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Windows. The collection OpenRGB registers is the keyboard
    /// collection itself, page 1 usage 6 (MintakaKeyboardControllerDetect.cpp:44),
    /// which Windows opens for itself, so it opens for another program with
    /// no access right only. Feature reports pass on such a handle (hidapi
    /// windows/hid.c:1009-1026), and they are all this keyboard takes, so
    /// its model tells the worker to keep it
    /// (<see cref="HidLightingModel.HeldByWindows"/>).</para>
    ///
    /// <para>Hand-back. No source reads the effect area back, and OpenRGB
    /// sends nothing when it closes, so the keyboard keeps PadForge's last
    /// picture in its Custom mode until another program or its own controls
    /// change it, and the effects it held before the first picture do not
    /// come back. No source names a program that lights it.</para>
    /// </summary>
    internal sealed class MintakaKeyboardLighting : IHidLightingFamily
    {
        public static readonly MintakaKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "MintakaKeyboardController";
        internal const ushort VendorId = 0x05AC;

        /// <summary>PACKET_DATA_LENGTH and the report ID byte (MintakaKeyboardController.h:18-19).</summary>
        internal const int DataLength = 64;
        internal const int PacketSize = DataLength + 1;

        /// <summary>COLOR_BUF_SIZE, 144 colors of 4 bytes, and EFFECT_PAGE_LENGTH (lines 20-21).</summary>
        internal const int ColorBufSize = 576;
        internal const int EffectPageLength = 16;

        /// <summary>The header and the commands (lines 23 and 30-49).</summary>
        internal const byte Header = 0x04;
        internal const byte CommunicationEnd = 0x02;
        internal const byte WriteEffectArea = 0x13;
        internal const byte CustomizationOn = 0x18;
        internal const byte EffectStart = 0xF0;
        internal const byte EffectPages = 0x12;

        /// <summary>The 20 effects OpenRGB lists after Custom, by value, in
        /// order (RGBController_MintakaKeyboard.cpp:100-202, the values in
        /// MintakaKeyboardController.h:54-77).</summary>
        internal static ReadOnlySpan<byte> EffectValues => new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,
            0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x80,
        };

        /// <summary>MINTAKA_MAX_BRIGHTNESS, every effect's brightness and the
        /// Custom mode's (MintakaKeyboardController.h:86-87, RGBController_MintakaKeyboard.cpp:97 and 233-238).</summary>
        internal const byte Brightness = 0x0F;

        /// <summary>The wait after each report and each read (lines 285 and 301).</summary>
        internal const int GapMs = 10;

        public string Name => "VSG Mintaka";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>OpenRGB's layout: mintaka_offset_values in ISO
        /// (RGBController_MintakaKeyboard.cpp:255).</summary>
        internal static KeyboardLayoutManager Layout() => KeyboardLayouts.Build("mintaka_offset_values", KeyboardLayout.IsoQwerty);

        /// <summary>The keys in the layout's order. OpenRGB colors the key
        /// at each place of that order by the value at the same place of
        /// mintaka_offset_values, not by the key's own value
        /// (SetLedSequencePositions and SetMode, MintakaKeyboardController.cpp:58-61
        /// and 183-190; RGBController_MintakaKeyboard.cpp:268), so each light's
        /// address is that value. A key past the end of the list, whose
        /// color OpenRGB would take from past the list's end, is left out.</summary>
        internal static List<LightInfo> Lights()
        {
            var values = KeyboardLayouts.Recipe("mintaka_offset_values").DefaultValues;
            var keys = KeyboardLedNames.FromLayout(Layout());
            var lights = new List<LightInfo>(keys.Count);
            for (int i = 0; i < keys.Count && i < values.Length; i++)
                lights.Add(keys[i] with { Address = (int)values[i] });
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_MintakaKeyboard.cpp")
            {
                Product = $"mintaka:{VendorId:X4}:{productId:X4}",
            },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            HeldByWindows = true,
            ReplacesStoredEffects = true,
            Begin = (_, _) => new Session(),
        };

        /// <summary>A report: ID 0, then 64 data bytes.</summary>
        internal static byte[] Report(ReadOnlySpan<byte> data)
        {
            var report = new byte[PacketSize];
            data.Slice(0, Math.Min(data.Length, DataLength)).CopyTo(report.AsSpan(1));
            return report;
        }

        internal static byte[] Command(byte command, byte at8 = 0)
        {
            var data = new byte[DataLength];
            data[0] = Header;
            data[1] = command;
            data[8] = at8;
            return Report(data);
        }

        /// <summary>The effects with no color of their own: Random colors,
        /// Spectrum cycle and Off, which alone lack
        /// MODE_FLAG_HAS_MODE_SPECIFIC_COLOR
        /// (RGBController_MintakaKeyboard.cpp:100-202).</summary>
        private static bool TakesAColor(byte value) => value is not (0x06 or 0x08 or 0x80);

        /// <summary>One effect's 16 bytes: its value, white where it takes a
        /// color, brightness 15, the check code AA 55 (SetMode,
        /// MintakaKeyboardController.cpp:113-153). OpenRGB's default color is
        /// black, which the keyboard would show as no light. Every default
        /// speed and direction is 0, and no effect starts in random
        /// colors.</summary>
        internal static void Effect(Span<byte> page, byte value)
        {
            page[0] = value;
            if (TakesAColor(value))
            {
                page[1] = 0xFF;
                page[2] = 0xFF;
                page[3] = 0xFF;
            }
            page[9] = Brightness;
            page[14] = 0xAA;
            page[15] = 0x55;
        }

        /// <summary>The 9 color pages: 0x80 ahead of each color, and each
        /// light's red, green and blue at four times its place
        /// (lines 175-196).</summary>
        internal static byte[] ColorBuffer(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var buffer = new byte[ColorBufSize];
            for (int i = 0; i < ColorBufSize; i += 4) buffer[i] = 0x80;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int offset = lights[i].Address * 4;
                if (offset < 0 || offset + 3 >= ColorBufSize) continue;
                buffer[offset + 1] = (byte)(colors[i] >> 16);
                buffer[offset + 2] = (byte)(colors[i] >> 8);
                buffer[offset + 3] = (byte)colors[i];
            }
            return buffer;
        }

        /// <summary>A picture's exchange in order: each entry a report, or
        /// null for a feature read.</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var steps = new List<byte[]>
            {
                Command(CustomizationOn), null,
                Command(WriteEffectArea, EffectPages), null,
            };
            // The Custom mode is active, and the Off effect shares its page
            // (lines 140-152), so that page is the last one sent.
            var selected = new byte[EffectPageLength];
            var effects = EffectValues;
            for (int i = 0; i < 5; i++)
            {
                var data = new byte[DataLength];
                for (int j = 0; j < 4; j++)
                {
                    byte value = effects[j + i * 4];
                    var page = data.AsSpan(j * EffectPageLength, EffectPageLength);
                    Effect(page, value);
                    if (value == 0x80) page.CopyTo(selected);
                }
                steps.Add(Report(data));
            }
            for (int i = 0; i < 3; i++) steps.Add(Report(Array.Empty<byte>()));
            var buffer = ColorBuffer(lights, colors);
            for (int p = 0; p < 9; p++) steps.Add(Report(buffer.AsSpan(p * DataLength, DataLength)));
            steps.Add(Report(selected));
            steps.Add(Command(CommunicationEnd));
            steps.Add(null);
            steps.Add(Command(EffectStart));
            return steps;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var step in Picture(lights, colors))
                {
                    // OpenRGB reads after the three commands that answer and
                    // does nothing with what comes back (Read, lines 276-286).
                    if (step == null) io.GetFeature(0x00, new byte[PacketSize]);
                    else if (!io.SetFeature(step)) return HidLightingPaint.Failed;
                    wait(GapMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
