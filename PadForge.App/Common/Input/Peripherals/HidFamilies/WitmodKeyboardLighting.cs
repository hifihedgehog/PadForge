using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Witmod keyboards (#557), the GK8110 and GK8120 boards resold as
    /// the Dark Project KD87A and KD104A and the Red Square Keyrox TKL
    /// Classic, lit key by key as OpenRGB's WitmodKeyboardController lights
    /// them: 0416:C345 on interface 2 (WitmodKeyboardControllerDetect.cpp:22-43),
    /// 64-byte output reports with report ID 1 (WitmodKeyboardController.h:19).
    /// OpenRGB registers the interface without a usage, so the collection
    /// taken is the one whose output report carries a frame.
    ///
    /// <para>The two sizes share their IDs. A session asks the keyboard
    /// which it is, 01 0D, and reads up to ten input reports for 150 ms each
    /// for the answer 01 0D and a string such as "2M252,01,KB,JC,GK8120SKRGB,V1.04.04":
    /// "GK8120" is the full-size board, anything else the TKL
    /// (ReadDeviceInfo, WitmodKeyboardController.cpp:80-166; WitmodKeyboardController.h:21-35).
    /// The keys are OpenRGB's gk8110_offset_values or gk8120_offset_values,
    /// each at its LED value, the offset of its green byte in the color
    /// data (RGBController_WitmodKeyboard.cpp:39-82 and 233-280). The model
    /// publishes the TKL's, and a session that hears the full-size board
    /// publishes that one (<see cref="HidLightingSession.Map"/>).</para>
    ///
    /// <para>The model of the device. The session then selects OpenRGB's
    /// Direct mode, 01 07 with the custom mode 0x0A in byte 6 and brightness
    /// 4 in byte 7, as OpenRGB does when that mode is chosen (SetMode,
    /// WitmodKeyboardController.cpp:202-245; the Direct mode, RGBController_WitmodKeyboard.cpp:106-110
    /// and 173-218; DeviceUpdateMode, lines 297-300), and waits 10 ms. A
    /// picture is the color data in eight reports, 01 0F, the report's
    /// number in byte 4 and its length in byte 5, then a 10 ms wait
    /// (SetLEDsData, lines 247-283). OpenRGB does not mark its Direct mode
    /// as one that saves (its flags, RGBController_WitmodKeyboard.cpp:109
    /// and 178), so PadForge sends a picture at its repaint interval.</para>
    ///
    /// <para>Hand-back. No source reads the mode the keyboard ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// keyboard stays in its custom mode with PadForge's last picture until
    /// another program or its own controls change it. No source names a
    /// program that lights it.</para>
    /// </summary>
    internal sealed class WitmodKeyboardLighting : IHidLightingFamily
    {
        public static readonly WitmodKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "WitmodKeyboardController";
        internal const ushort VendorId = 0x0416;

        /// <summary>WITMOD_PACKET_DATA_LENGTH, the report ID included (WitmodKeyboardController.h:19).</summary>
        internal const int PacketSize = 64;
        internal const byte ReportId = 0x01;

        /// <summary>WITMOD_INFO_COMMAND, WITMOD_INFO_RETRIES and
        /// WITMOD_INFO_TIMEOUT (WitmodKeyboardController.h:31-33).</summary>
        internal const byte InfoCommand = 0x0D;
        internal const int InfoReads = 10;
        internal const int InfoMs = 150;

        /// <summary>WITMOD_CUSTOM_MODE_VALUE and WITMOD_BRIGHTNESS_MAX (lines 51 and 65).</summary>
        internal const byte CustomMode = 0x0A;
        internal const byte Brightness = 0x04;

        /// <summary>The wait after the mode and after a picture
        /// (WitmodKeyboardController.cpp:243-244 and 281-282).</summary>
        internal const int GapMs = 10;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Witmod";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0 || collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The map of one size: the GK8110's or the GK8120's
        /// layout in ANSI (RGBController_WitmodKeyboard.cpp:241-251).</summary>
        internal static LightMap Map(bool full, ushort productId)
        {
            var layout = KeyboardLayouts.Build(full ? "gk8120_offset_values" : "gk8110_offset_values", KeyboardLayout.AnsiQwerty);
            return new LightMap(KeyboardLedNames.FromLayout(layout), "OpenRGB RGBController_WitmodKeyboard.cpp")
            {
                Product = $"witmod:{VendorId:X4}:{productId:X4}:{(full ? "gk8120" : "gk8110")}",
            };
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = Map(false, productId),
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Begin(io, wait, productId),
        };

        internal static byte[] Report(byte command)
        {
            var report = new byte[PacketSize];
            report[0] = ReportId;
            report[1] = command;
            return report;
        }

        /// <summary>SetMode for the Direct mode (WitmodKeyboardController.cpp:208-242):
        /// no colors, direction left (0), random off.</summary>
        internal static byte[] DirectMode()
        {
            var report = Report(0x07);
            report[6] = CustomMode;
            report[7] = Brightness;
            return report;
        }

        /// <summary>Whether an answer's string names the full-size board,
        /// the test ReadDeviceInfo makes (lines 110-160). Null when the
        /// report is not the answer or holds no string.</summary>
        internal static bool? IsFullSize(byte[] answer, int length)
        {
            if (length < 2 || answer[0] != ReportId || answer[1] != InfoCommand) return null;
            int at = 2;
            int end = Math.Min(length, PacketSize);
            // The padding ahead of the string, zeros and spaces alike, is
            // stepped over (lines 119-132).
            while (at < end && answer[at] <= (byte)' ') at++;
            if (at >= end) return null;
            int stop = at;
            while (stop < end && answer[stop] != 0) stop++;
            string info = Encoding.ASCII.GetString(answer, at, stop - at);
            return info.Contains("GK8120", StringComparison.Ordinal);
        }

        /// <summary>The picture's eight reports (SetLEDsData, lines 253-279).</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var data = new byte[PacketSize * 8];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int offset = lights[i].Address;
                if (offset < 1 || offset + 1 >= data.Length) continue;
                data[offset - 1] = (byte)(colors[i] >> 16);
                data[offset] = (byte)(colors[i] >> 8);
                data[offset + 1] = (byte)colors[i];
            }
            var reports = new List<byte[]>(8);
            for (int i = 0; i < 8; i++)
            {
                var report = Report(0x0F);
                report[4] = (byte)i;
                report[5] = i == 7 ? (byte)0x12 : (byte)0x36;
                Array.Copy(data, PacketSize * i + 6, report, 6, PacketSize - 6);
                reports.Add(report);
            }
            return reports;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, ushort productId)
        {
            if (io == null || !io.WriteOutput(Report(InfoCommand))) return null;
            bool full = false;
            var answer = new byte[Math.Max(PacketSize, io.InputReportLength)];
            for (int read = 0; read < InfoReads; read++)
            {
                Array.Clear(answer);
                int length = io.ReadInput(answer, InfoMs);
                if (length <= 0) continue;
                if (IsFullSize(answer, length) is bool size)
                {
                    full = size;
                    break;
                }
            }
            if (!io.WriteOutput(DirectMode())) return null;
            wait(GapMs);
            return new Session(full ? Map(true, productId) : null);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(LightMap map) => _map = map;

            /// <summary>The GK8120's map when the keyboard said it is one.</summary>
            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Nothing undoes the custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                wait(GapMs);
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
