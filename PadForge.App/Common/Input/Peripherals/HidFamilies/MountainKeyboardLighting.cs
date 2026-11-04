using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Mountain Everest and Everest 60 keyboards (#557), lit light by
    /// light as OpenRGB's MountainKeyboardController lights them.
    ///
    /// <para>The Everest (3282:0001, interface 3, usage page 0xFF00, usage 1,
    /// MountainKeyboardControllerDetect.cpp:69) takes 65-byte output reports
    /// with no report ID. Its lights are OpenRGB's four zones in order: 87
    /// main keys, 17 number pad keys, 32 edge lights round the keyboard and
    /// 14 round the number pad (RGBController_MountainKeyboard.cpp:341-388
    /// and 717-752). A key's address is its color index, led_names'
    /// numbering (lines 391-518), and an edge light's is
    /// <see cref="EdgeBase"/> plus its edge index. OpenRGB's fifth zone, the
    /// wheel, is left out: its color reaches the keyboard only when OpenRGB
    /// saves a mode (DeviceSaveMode, lines 1060-1064), which writes the
    /// keyboard's memory.</para>
    ///
    /// <para>The Everest's model. A session selects the keyboard's custom
    /// mode, 14 00 with 01 06 in bytes 5 and 6, and waits 200 ms, as
    /// OpenRGB does when its Direct or Custom mode is chosen (SelectMode,
    /// MountainKeyboardController.cpp:55-66, both modes MOUNTAIN_KEYBOARD_IDX_CUSTOM,
    /// RGBController_MountainKeyboard.h:27, sent from DeviceUpdateMode,
    /// lines 1009-1020) and as SignalRGB's plugin does when it starts
    /// (signal-plugins Mountain Everest Max.js:74-82). A picture is OpenRGB's
    /// Custom mode: a start report, the key colors in seven reports and three
    /// confirm reports, then a start report, the edge colors in three reports
    /// and three confirm reports, at brightness 100, the Custom mode's
    /// (SendDirectColorCmd and SendDirectColorEdgeCmd with quick_mode false,
    /// MountainKeyboardController.cpp:353-428 and 478-580; lines 560-568 and
    /// 814-818 of the RGBController). The mode saves nothing by itself:
    /// OpenRGB marks it "can manually be saved" (MODE_FLAG_MANUAL_SAVE,
    /// RGBControllerInterface.h:72) and saves only through a separate save
    /// report (SaveData, lines 68-78), which PadForge never sends. OpenRGB's
    /// Direct mode sends the same color reports without the start and
    /// confirm reports, at brightness 0, and OpenRGB marks the Everest's
    /// direct support problematic (@direct :rotating_light:, line 525), so
    /// the Custom mode's full sequence is the one used here.</para>
    ///
    /// <para>Hand-back. No source reads the mode the keyboard ran before or
    /// returns it to that mode: OpenRGB reads no lighting state ("TODO:
    /// Figure out how to read RGB state", line 326) and sends nothing when it
    /// closes, and SignalRGB's plugin sends a last color when it shuts down
    /// (Mountain Everest Max.js:88-96). So the keyboard stays in its custom
    /// mode with PadForge's last picture until Mountain's Base Camp or the
    /// keyboard's own controls change it. No source names Base Camp's
    /// processes, so the model names no program.</para>
    ///
    /// <para>The Everest 60 (3282:0005 and 0006, interface 2, usage page
    /// 0xFFFF, usage 1, MountainKeyboardControllerDetect.cpp:70-71) takes
    /// 65-byte feature reports, each read back with a feature read. A
    /// session sends the reset report 03 46 23 EA, as OpenRGB's controller
    /// does when it opens (Mountain60KeyboardController.cpp:20-38), and
    /// selects mode 7, the custom mode OpenRGB's Direct mode is
    /// (SelectMode, lines 114-131; RGBController_Mountain60Keyboard.cpp:193-201),
    /// as SignalRGB's plugin does (Mountain Everest 60.js:62-66). OpenRGB
    /// follows that with its mode details and a save report
    /// (SendModeDetails, lines 84-112, which reads one color past an empty
    /// list for the Direct mode), and PadForge sends neither: the save
    /// writes the keyboard's memory and SignalRGB's plugin streams without
    /// it. A picture is a start report at brightness 100, the lights in
    /// quads of index, red, green and blue, 14 to a report, and a finish
    /// report (SendDirect, lines 151-245). The keyboard needs the report 08
    /// 46 23 EA every 500 ms to keep showing the picture (UpdateMountain,
    /// RGBController_Mountain60Keyboard.cpp:338-343 and 434-442,
    /// MOUNTAIN60_KEEP_LIVE_PERIOD, RGBController_Mountain60Keyboard.h:24),
    /// whatever else goes out between, which the worker sends on that
    /// period as the session's keep-alive (<see cref="HidLightingModel.KeepAliveMs"/>,
    /// with no idle time, since a picture does not move OpenRGB's sleep).
    /// Its lights are OpenRGB's 60%
    /// layout with the number pad module and the edges
    /// (mountain60_keyboard_overlay_no_numpad, lines 46-170), each at its
    /// LED value. Hand-back: the same as the Everest's, nothing a source
    /// names, so the keyboard keeps the last picture.</para>
    /// </summary>
    internal sealed class MountainKeyboardLighting : IHidLightingFamily
    {
        public static readonly MountainKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "MountainKeyboardController";

        /// <summary>MOUNTAIN_VID (MountainKeyboardController.h:20).</summary>
        internal const ushort VendorId = 0x3282;
        internal const ushort EverestPid = 0x0001;

        /// <summary>MOUNTAIN_KEYBOARD_USB_BUFFER_SIZE and
        /// MOUNTAIN60_KEYBOARD_USB_BUFFER_SIZE, 65 bytes with the report ID
        /// (MountainKeyboardController.h:33, Mountain60KeyboardController.h:21).</summary>
        internal const int PacketSize = 65;

        /// <summary>The address of the first edge light: past the 126 colors
        /// of MOUNTAIN_KEYBOARD_MAX_TRANSFER_COLORS (MountainKeyboardController.h:27).</summary>
        internal const int EdgeBase = 126;
        internal const int KeyColors = 126;
        internal const int EdgeColors = 46;

        /// <summary>The Custom mode's brightness, MOUNTAIN_KEYBOARD_BRIGHTNESS_MAX
        /// (RGBController_MountainKeyboard.h:19 and RGBController_MountainKeyboard.cpp:565).</summary>
        internal const byte EverestBrightness = 100;

        /// <summary>MOUNTAIN_KEYBOARD_USB_MAX_DIRECT_PAYLOAD_SIZE, 65 less the
        /// 8-byte header (MountainKeyboardController.h:34-36).</summary>
        internal const int EverestPayload = 57;

        /// <summary>MOUNTAIN60_KEYBOARD_USB_MAX_DIRECT_PAYLOAD_SIZE, 65 less the
        /// 9-byte header (Mountain60KeyboardController.h:22-25).</summary>
        internal const int Everest60Payload = 56;

        /// <summary>The Everest 60's Direct brightness, its maximum of 4
        /// (RGBController_Mountain60Keyboard.h:19 and .cpp:198), sent as
        /// brightness times 25 (Mountain60KeyboardController.cpp:163).</summary>
        internal const byte Everest60Brightness = 4 * 25;

        /// <summary>The Everest 60's keep-alive period (RGBController_Mountain60Keyboard.h:24).</summary>
        internal const int KeepAliveMs = 500;

        internal const int SelectModeMs = 200;
        internal const int CommandMs = 10;
        internal const int PacketMs = 5;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Mountain";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.ProductId == EverestPid)
            {
                if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
                return _models.Get("everest", () => EverestModel(registrations[0].Name));
            }
            if (collection.FeatureReportLength != 0 && collection.FeatureReportLength < PacketSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get("60:" + pid, () => Everest60Model(registrations[0].Name, pid));
        }

        // ─────────────────────────────────────────────
        //  The Everest
        // ─────────────────────────────────────────────

        /// <summary>The Everest's lights in OpenRGB's order: the main keys
        /// and the number pad keys at their color indexes, then the two edge
        /// zones at <see cref="EdgeBase"/> plus their edge indexes
        /// (RGBController_MountainKeyboard.cpp:717-752).</summary>
        internal static List<LightInfo> EverestLights()
        {
            var lights = new List<LightInfo>(150);
            foreach (int index in KeyboardLedNames.MountainKeyLut)
                if (KeyboardLedNames.Named(index, KeyboardLedNames.MountainNames[index]) is { } key) lights.Add(key);
            foreach (int index in KeyboardLedNames.MountainNumpadLut)
                if (KeyboardLedNames.Named(index, KeyboardLedNames.MountainNames[index]) is { } key) lights.Add(key);
            foreach (int index in KeyboardLedNames.MountainEdgeLut)
                lights.Add(new LightInfo(EdgeBase + index, LightKind.Edge, 0, LightKeySource.None));
            foreach (int index in KeyboardLedNames.MountainNumpadEdgeLut)
                lights.Add(new LightInfo(EdgeBase + index, LightKind.Edge, 0, LightKeySource.None));
            return lights;
        }

        private static HidLightingModel EverestModel(string name) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(EverestLights(), "OpenRGB RGBController_MountainKeyboard.cpp")
            {
                Product = $"mountain:{VendorId:X4}:{EverestPid:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => BeginEverest(io, wait),
        };

        private static byte[] EverestReport() => new byte[PacketSize];

        /// <summary>SelectMode (MountainKeyboardController.cpp:55-66).</summary>
        internal static byte[] SelectMode(byte mode)
        {
            var report = EverestReport();
            report[1] = 0x14;
            report[2] = 0x00;
            report[5] = 0x01;
            report[6] = mode;
            return report;
        }

        /// <summary>SendColorStartPacketCmd: 0xFF throughout, then the header
        /// (MountainKeyboardController.cpp:353-366).</summary>
        internal static byte[] StartReport(byte brightness)
        {
            var report = EverestReport();
            Array.Fill(report, (byte)0xFF);
            report[0] = 0x00;
            report[1] = 0x14;
            report[2] = 0x2C;
            report[3] = 0x0A;
            report[4] = 0x00;
            report[6] = brightness;
            return report;
        }

        /// <summary>SendColorPacketCmd and SendColorEdgePacketCmd
        /// (MountainKeyboardController.cpp:368-412): the header, then the
        /// data from byte 8, zeros after it.</summary>
        internal static byte[] ColorReport(bool edge, byte packet, byte brightness, ReadOnlySpan<byte> data)
        {
            var report = EverestReport();
            report[1] = 0x14;
            if (edge)
            {
                report[2] = 0x2D;
                report[3] = 0x0A;
                report[6] = 0xFF;
            }
            else
            {
                report[2] = 0x2C;
                report[4] = 0x01;
                report[6] = brightness;
            }
            report[5] = packet;
            data.CopyTo(report.AsSpan(8));
            return report;
        }

        /// <summary>SendColorPacketFinishCmd's report for one index
        /// (MountainKeyboardController.cpp:414-428).</summary>
        internal static byte[] ConfirmReport(byte index)
        {
            var report = EverestReport();
            report[1] = 0x14;
            report[2] = 0xA0;
            report[3] = index;
            return report;
        }

        /// <summary>The Everest's color data: the keys' 378 bytes and the
        /// edges' 138, red, green and blue at three times each index
        /// (RGBController_MountainKeyboard.cpp:763-800).</summary>
        internal static (byte[] Keys, byte[] Edges) EverestData(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var keys = new byte[KeyColors * 3];
            var edges = new byte[EdgeColors * 3];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int address = lights[i].Address;
                byte[] data = keys;
                if (address >= EdgeBase)
                {
                    data = edges;
                    address -= EdgeBase;
                }
                if (address < 0 || address * 3 + 2 >= data.Length) continue;
                data[address * 3] = (byte)(colors[i] >> 16);
                data[address * 3 + 1] = (byte)(colors[i] >> 8);
                data[address * 3 + 2] = (byte)colors[i];
            }
            return (keys, edges);
        }

        /// <summary>Every report of a picture, each with the wait OpenRGB
        /// takes after it.</summary>
        internal static List<(byte[] Report, int WaitMs)> EverestPicture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var (keys, edges) = EverestData(lights, colors);
            var reports = new List<(byte[], int)>();
            foreach (bool edge in new[] { false, true })
            {
                byte[] data = edge ? edges : keys;
                reports.Add((StartReport(EverestBrightness), CommandMs));
                byte packet = 0;
                for (int at = 0; at < data.Length; at += EverestPayload, packet++)
                    reports.Add((ColorReport(edge, packet, EverestBrightness,
                        data.AsSpan(at, Math.Min(EverestPayload, data.Length - at))), PacketMs));
                for (byte index = 0; index < 3; index++)
                    reports.Add((ConfirmReport(index), CommandMs));
            }
            return reports;
        }

        private static HidLightingSession BeginEverest(IHidReportChannel io, Action<int> wait)
        {
            if (io == null || !io.WriteOutput(SelectMode(0x06))) return null;
            wait(SelectModeMs);
            return new EverestSession();
        }

        internal sealed class EverestSession : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes the custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var (report, ms) in EverestPicture(lights, colors))
                {
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    wait(ms);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  The Everest 60
        // ─────────────────────────────────────────────

        /// <summary>The Everest 60's layout, OpenRGB's mountain60_layout in
        /// ANSI with its edit list (RGBController_Mountain60Keyboard.cpp:359-360).</summary>
        internal static KeyboardLayoutManager Everest60Layout()
            => KeyboardLayouts.Build("mountain60_layout", KeyboardLayout.AnsiQwerty);

        /// <summary>The Everest 60's lights at their LED values, the edges
        /// ("Edge 1" to "Edge 44" and "Numpad Edge 1" to "Numpad Edge 22")
        /// as edge lights.</summary>
        internal static List<LightInfo> Everest60Lights()
            => KeyboardLedNames.FromLayout(Everest60Layout(), name => name.Contains("Edge", StringComparison.Ordinal));

        private static HidLightingModel Everest60Model(string name, ushort pid) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Everest60Lights(), "OpenRGB RGBController_Mountain60Keyboard.cpp")
            {
                Product = $"mountain:{VendorId:X4}:{pid:X4}",
            },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            Begin = (io, wait) => BeginEverest60(io, wait),
        };

        /// <summary>A report of the Everest 60: the command, 46 23 EA
        /// (Mountain60KeyboardController.cpp:26-37 and on).</summary>
        internal static byte[] Report60(byte command)
        {
            var report = new byte[PacketSize];
            report[1] = command;
            report[2] = 0x46;
            report[3] = 0x23;
            report[4] = 0xEA;
            return report;
        }

        /// <summary>MOUNTAIN60_KEYBOARD_RESET_CMD (Mountain60KeyboardController.h:29).</summary>
        internal static byte[] Reset60() => Report60(0x03);

        /// <summary>UpdateData, MOUNTAIN60_KEYBOARD_CHECK_NUMPAD (line 30), the
        /// keep-alive (Mountain60KeyboardController.cpp:68-82).</summary>
        internal static byte[] KeepAlive60() => Report60(0x08);

        /// <summary>SelectMode (Mountain60KeyboardController.cpp:114-131).</summary>
        internal static byte[] SelectMode60(byte mode)
        {
            var report = Report60(0x16);
            report[5] = 0x01;
            report[9] = mode;
            return report;
        }

        /// <summary>SendDirectStartPacketCmd (lines 151-168).</summary>
        internal static byte[] Start60(byte brightness)
        {
            var report = Report60(0x34);
            report[5] = brightness;
            report[6] = 0xC0;
            return report;
        }

        /// <summary>SendDirectPacketCmd (lines 170-196): the stream flag, the
        /// data from byte 9, 0xFF after it.</summary>
        internal static byte[] Packet60(byte flag, ReadOnlySpan<byte> data)
        {
            var report = Report60(0x35);
            report[5] = flag;
            data.CopyTo(report.AsSpan(9));
            report.AsSpan(9 + data.Length).Fill(0xFF);
            return report;
        }

        /// <summary>SendDirectPacketFinishCmd (lines 198-212).</summary>
        internal static byte[] Finish60() => Report60(0x36);

        /// <summary>The picture's reports (SendDirect, lines 214-245, with the
        /// data DeviceUpdateLEDs builds, RGBController_Mountain60Keyboard.cpp:384-403):
        /// each light's value, red, green and blue, in map order. Full
        /// reports carry flag 0x0E, a short last one 0x0A.</summary>
        internal static List<byte[]> Everest60Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            var data = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                data[i * 4] = (byte)lights[i].Address;
                data[i * 4 + 1] = (byte)(colors[i] >> 16);
                data[i * 4 + 2] = (byte)(colors[i] >> 8);
                data[i * 4 + 3] = (byte)colors[i];
            }
            var reports = new List<byte[]> { Start60(Everest60Brightness) };
            int at = 0;
            while (at < data.Length)
            {
                int left = data.Length - at;
                if (left >= Everest60Payload)
                {
                    reports.Add(Packet60(0x0E, data.AsSpan(at, Everest60Payload)));
                    at += Everest60Payload;
                }
                else
                {
                    reports.Add(Packet60(0x0A, data.AsSpan(at, left)));
                    at = data.Length;
                }
            }
            reports.Add(Finish60());
            return reports;
        }

        /// <summary>A feature report and the feature read OpenRGB makes after
        /// each. OpenRGB ignores the read, and so does this.</summary>
        internal static bool Exchange60(IHidReportChannel io, byte[] report)
        {
            if (!io.SetFeature(report)) return false;
            io.GetFeature(0x00, new byte[PacketSize]);
            return true;
        }

        private static HidLightingSession BeginEverest60(IHidReportChannel io, Action<int> wait)
        {
            if (io == null || !Exchange60(io, Reset60()) || !Exchange60(io, SelectMode60(0x07))) return null;
            return new Everest60Session();
        }

        internal sealed class Everest60Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing undoes the custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Everest60Picture(lights, colors))
                    if (!Exchange60(io, report)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>The keep-alive, report 08 read back, never the
            /// picture again.</summary>
            public override bool KeepAlive(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io != null && Exchange60(io, KeepAlive60());

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
