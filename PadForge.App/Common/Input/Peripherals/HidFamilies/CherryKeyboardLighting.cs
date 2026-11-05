using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cherry's RGB keyboards, lit as OpenRGB's CherryKeyboardController
    /// lights them (#557): the collection on interface 1 and usage page
    /// 0xFF1C of each registered product (CherryKeyboardControllerDetect.cpp:41-68).
    ///
    /// <para>The keyboard takes no colors as they come. OpenRGB lights it in
    /// its Custom mode: the mode report once (DeviceUpdateMode,
    /// RGBController_CherryKeyboard.cpp:374-399), then the 126 LEDs' colors in
    /// seven reports of 54 bytes each (DeviceUpdateLEDs, lines 346-362, and
    /// SetKeyboardColors, CherryKeyboardController.cpp:51-81), each report
    /// answered by one the keyboard sends back (lines 190-229). The keyboard
    /// keeps both, the mode and the colors: OpenRGB marks the Custom mode as
    /// saved by the keyboard (MODE_FLAG_AUTOMATIC_SAVE, line 56), and its
    /// table marks saving as automatic and its Direct support as a problem
    /// (@save :robot:, @direct :rotating_light:, lines 31-40). So every
    /// picture is a write into the keyboard's memory, and a changed picture
    /// goes out once it has held for <see cref="LenovoLegionLighting.HoldMs"/>
    /// and no sooner than <see cref="LenovoLegionLighting.RepaintMs"/> after
    /// the one before.</para>
    ///
    /// <para>Hand-back. Nothing goes out. No reference reads the mode or the
    /// custom colors back: OpenRGB has no read, cherryrgb-rs reads only the
    /// key map and the key indexes (lib.rs fetch_device_state), and its
    /// 0x1B read, which OpenRGB names READ_CUSTOM_COLOR_DATA
    /// (CherryKeyboardController.h:57), answers one byte per key (lib.rs
    /// get_key_indexes and the captures in its tests). The keyboard keeps
    /// the Custom mode and the last colors PadForge sent until CHERRY
    /// Utility or the keyboard's own lighting keys change them.</para>
    ///
    /// <para>The sum. OpenRGB adds the bytes of each report as signed chars
    /// (ComputeChecksum, CherryKeyboardController.cpp:122-136), so a byte
    /// from 0x80 up lowers the sum's high byte. The reports CHERRY Utility
    /// sends add them unsigned: cherryrgb-rs checks its sum against 40
    /// captured reports, among them a Custom mode report whose color byte
    /// E0 makes the two sums differ (lib.rs calc_checksum and test_checksum,
    /// "04 52 01 06 09 00 00 55 00 08 03 00 00 00 E0 03 00"). PadForge sends
    /// the utility's sum.</para>
    ///
    /// <para>No source names the program CHERRY Utility runs as, so the
    /// family names none.</para>
    /// </summary>
    internal sealed class CherryKeyboardLighting : IHidLightingFamily
    {
        public static readonly CherryKeyboardLighting Instance = new();

        internal const string FamilyName = "CherryKeyboardController";

        /// <summary>The Custom mode (CherryKeyboardController.h:83) and its
        /// brightness, CHERRY_KB_BRIGHTNESS_HIGHEST (lines 95-99,
        /// RGBController_CherryKeyboard.cpp:53-61). The mode has no
        /// speed, so it keeps 0 (RGBControllerInterface.h:325-342).</summary>
        internal const byte ModeCustom = 0x08;
        internal const byte BrightnessHighest = 0x04;

        /// <summary>The commands (CherryKeyboardController.h:52-59).</summary>
        internal const byte SetParameter = 0x06;
        internal const byte WriteCustomColors = 0x0B;

        /// <summary>The colors' bytes per report, CHERRY_KB_MAX_PACKET_SIZE
        /// (CherryKeyboardController.h:19).</summary>
        internal const int ChunkBytes = 0x36;

        public string Name => "Cherry";

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
            var layout = MatrixKeyboardLayouts.Cherry;
            return new HidLightingModel
            {
                Family = this,
                Name = registration.Name,
                Map = new LightMap(MatrixKeyboardLayouts.Lights(layout), "OpenRGB " + layout.Source)
                {
                    Product = $"cherry:{registration.VendorId:X4}:{registration.ProductId:X4}",
                },
                HoldMs = LenovoLegionLighting.HoldMs,
                RepaintMs = LenovoLegionLighting.RepaintMs,
                Begin = (io, wait) => new SonixCustomSession(
                    SonixKeyboard.Parameter(SetParameter, new byte[] { 0x00, ModeCustom, BrightnessHighest, 0, 0, 0, 0, 0, 0 }, cherry: true),
                    WriteCustomColors, layout.Count),
            };
        }
    }

    /// <summary>
    /// The 64-byte output report of the Sonix keyboards that OpenRGB's
    /// Cherry and first EVision families light (#557): report 04, a 16-bit
    /// sum of bytes 3 to 63, the command, the data's length, its offset in
    /// two bytes, a byte the Cherry's parameter report sets to 55, and the
    /// data from byte 8 (CherryKeyboardController.cpp:122-260,
    /// EVisionKeyboardController.cpp:122-259). Each report is answered by
    /// one the keyboard sends back, which OpenRGB reads and drops.
    /// </summary>
    internal static class SonixKeyboard
    {
        internal const byte ReportId = 0x04;
        internal const int PacketSize = 64;

        /// <summary>How long a session waits for the keyboard's answer to a
        /// report. OpenRGB waits as long as it takes (hid_read); cherryrgb-rs
        /// waits 1000 ms (lib.rs TIMEOUT_MS). A session waits this long once.
        /// After an answer that did not come, it takes only what has already
        /// come until the keyboard answers again, since one thread serves every
        /// lit unit.</summary>
        internal const int AnswerMs = 1000;

        /// <summary>A report with its sum: the bytes 3 to 63 added as
        /// unsigned bytes, low byte first in bytes 1 and 2 (cherryrgb-rs
        /// lib.rs calc_checksum, EVisionV2KeyboardController.cpp:132-138).</summary>
        internal static byte[] Packet(byte command, int length, int offset, ReadOnlySpan<byte> data, byte b7 = 0)
        {
            var buf = new byte[PacketSize];
            buf[0] = ReportId;
            buf[3] = command;
            buf[4] = (byte)length;
            buf[5] = (byte)(offset & 0xFF);
            buf[6] = (byte)(offset >> 8);
            buf[7] = b7;
            data.CopyTo(buf.AsSpan(8));
            ushort sum = 0;
            for (int i = 3; i < PacketSize; i++) sum += buf[i];
            buf[1] = (byte)(sum & 0xFF);
            buf[2] = (byte)(sum >> 8);
            return buf;
        }

        /// <summary>SendKeyboardParameter with parameter 0, the mode: the
        /// Cherry's sets byte 7 to 55 (CherryKeyboardController.cpp:231-260),
        /// the EVision's does not (EVisionKeyboardController.cpp:231-259).</summary>
        internal static byte[] Parameter(byte command, byte[] data, bool cherry)
            => Packet(command, data.Length, 0, data, cherry ? (byte)0x55 : (byte)0x00);

        /// <summary>SetKeyboardColors: the colors cut into reports of
        /// <paramref name="chunk"/> bytes, each at its offset
        /// (CherryKeyboardController.cpp:51-81 and 190-229).</summary>
        internal static List<byte[]> ColorPackets(byte command, byte[] colors, int chunk = CherryKeyboardLighting.ChunkBytes)
        {
            var packets = new List<byte[]>();
            for (int offset = 0; offset < colors.Length; offset += chunk)
            {
                int size = Math.Min(chunk, colors.Length - offset);
                packets.Add(Packet(command, size, offset, colors.AsSpan(offset, size)));
            }
            return packets;
        }

        /// <summary>The 126 LEDs' colors, red, green and blue each, in LED
        /// order (RGBController_CherryKeyboard.cpp:346-362,
        /// RGBController_EVisionKeyboard.cpp:336-352).</summary>
        internal static byte[] ColorData(IReadOnlyList<int> colors, int count)
        {
            var data = new byte[count * 3];
            for (int i = 0; i < count && i < (colors?.Count ?? 0); i++)
            {
                data[3 * i + 0] = (byte)(colors[i] >> 16);
                data[3 * i + 1] = (byte)(colors[i] >> 8);
                data[3 * i + 2] = (byte)colors[i];
            }
            return data;
        }

        /// <summary>Writes a report and reads the keyboard's answer. False
        /// when the write or the read failed. An answer that does not come
        /// in time is not a failure: OpenRGB drops every answer unread.
        /// <paramref name="silent"/> says the last answer did not come, and
        /// then this read does not wait (<see cref="AnswerMs"/>).</summary>
        internal static bool Exchange(IHidReportChannel io, byte[] packet, ref bool silent)
        {
            if (!io.WriteOutput(packet)) return false;
            var answer = new byte[PacketSize + 1];
            int read = io.ReadInput(answer, silent ? 0 : AnswerMs);
            if (read < 0) return false;
            silent = read == 0;
            return true;
        }
    }

    /// <summary>A session on a Sonix keyboard lit in its Custom mode: the
    /// mode report before the first picture, then each picture's color
    /// reports. Nothing goes out at the hand-back (see
    /// <see cref="CherryKeyboardLighting"/>).</summary>
    internal sealed class SonixCustomSession : HidLightingSession
    {
        private readonly byte[] _mode;
        private readonly byte _colorCommand;
        private readonly int _count;
        private bool _moded;
        private bool _silent;

        public SonixCustomSession(byte[] mode, byte colorCommand, int count)
        {
            _mode = mode;
            _colorCommand = colorCommand;
            _count = count;
        }

        public override bool Feature => false;

        public override byte[][] CrashFrames => Array.Empty<byte[]>();

        public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            if (!_moded)
            {
                if (!SonixKeyboard.Exchange(io, _mode, ref _silent)) return HidLightingPaint.Failed;
                _moded = true;
            }
            foreach (var packet in SonixKeyboard.ColorPackets(_colorCommand, SonixKeyboard.ColorData(colors, _count)))
                if (!SonixKeyboard.Exchange(io, packet, ref _silent)) return HidLightingPaint.Failed;
            return HidLightingPaint.Sent;
        }

        public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
    }
}
