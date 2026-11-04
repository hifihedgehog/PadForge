using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The OKS Optical Axis RGB keyboard (#557), lit key by key as OpenRGB's
    /// OKSController lights it: 1C4F:EE88 on interface 1
    /// (OKSKeyboardControllerDetect.cpp:35), 65-byte output reports with
    /// report ID 4 carrying OKS's "kb2" packets: 5C, the data length, the
    /// command and a checksum, then the data (OKSKeyboardController.h:32-54).
    /// OpenRGB registers the interface without a usage, so the collection
    /// taken is the one whose output report carries a packet.
    ///
    /// <para>The board. OpenRGB lays out an 87-key board when the serial
    /// string starts with "B" and the full 6 by 21 board otherwise
    /// (RGBController_OKSKeyboard.cpp:166-218). The full board's keys are
    /// OpenRGB's led_names, each at its place in the 6 by 21 grid (lines
    /// 54-69 and 223-252). The 87-key board's are its led_kb87_names in a 6
    /// by 17 grid, each LED's color at the same row and column of the 6 by
    /// 21 grid the frames carry (lines 26-32, 49-52, 71-90 and 237-247). A
    /// session reads the serial string before it writes anything, and an
    /// 87-key board's session publishes its map
    /// (<see cref="HidLightingSession.Map"/>).</para>
    ///
    /// <para>The model of the device. A session sends OpenRGB's opening
    /// packet, the write-RGB command 0x15 with brightness 0, mode 0, speed 2
    /// and direction 1 (SendInitialize, OKSKeyboardController.cpp:130-135),
    /// then the Direct mode the same way with brightness 2, the mode's
    /// default (RGBController_OKSKeyboard.cpp:121-133 and 264-280), each
    /// followed by a 2 ms wait (Send, lines 102-128). A picture is nine
    /// write-LED packets, 0x17, each carrying 14 places as index, red,
    /// green and blue, then FF FF, and a 5 ms wait after the nine
    /// (SendColors and kb2M_wled, lines 59-93 and 177-184). The checksum is
    /// 0x35 plus the head, the length, the command and the last data byte
    /// (kb2_ComputeChecksum, lines 137-147). OpenRGB's Direct mode does not
    /// save (its flags, RGBController_OKSKeyboard.cpp:124).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the keyboard's own modes, so the keyboard keeps
    /// PadForge's last picture until its own controls change it. No source
    /// names a program that lights it.</para>
    /// </summary>
    internal sealed class OksKeyboardLighting : IHidLightingFamily
    {
        public static readonly OksKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "OKSController";

        /// <summary>OKS_VID (OKSKeyboardController.h:22).</summary>
        internal const ushort VendorId = 0x1C4F;

        internal const int PacketSize = 65;
        internal const byte ReportId = 0x04;

        /// <summary>KB2_PACK_HEAD and KB2_HEAD_SIZE (OKSKeyboardController.h:32-33).</summary>
        internal const byte Head = 0x5C;
        internal const int HeadSize = 4;

        /// <summary>KB2_CMD_WRGB and KB2_CMD_WLED (lines 48-54).</summary>
        internal const byte WriteRgb = 0x15;
        internal const byte WriteLed = 0x17;

        /// <summary>The Direct mode's brightness, speed and direction
        /// (RGBController_OKSKeyboard.cpp:127-132; OKS_SPEED_NORMAL 2;
        /// MODE_DIRECTION_RIGHT 1, RGBControllerInterface.h).</summary>
        internal const byte DirectBrightness = 2;
        internal const byte Speed = 2;
        internal const byte Direction = 1;

        /// <summary>The places of the 6 by 21 grid.</summary>
        internal const int Places = 6 * 21;

        internal const int SendMs = 2;
        internal const int ColorsMs = 5;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "OKS";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0 || collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        internal static List<LightInfo> Lights() => KeyboardLedNames.FromNames(KeyboardLedNames.OksNames, i => i, Places);

        /// <summary>zone_sizes_kb87 (RGBController_OKSKeyboard.cpp:49-52) and
        /// its grid's width (line 182).</summary>
        internal const int Kb87Leds = 102;
        internal const int Kb87Width = 17;

        /// <summary>The 87-key board's LEDs, each at the place DeviceUpdateLEDs
        /// gives its color: its row times 21 plus its column (lines 237-247).</summary>
        internal static List<LightInfo> Kb87Lights()
            => KeyboardLedNames.FromNames(KeyboardLedNames.OksKb87Names, i => i / Kb87Width * 21 + i % Kb87Width, Kb87Leds);

        /// <summary>Whether a serial string names the 87-key board: its first
        /// character is 'B' (lines 178-184).</summary>
        internal static bool IsKb87(string serial) => !string.IsNullOrEmpty(serial) && serial[0] == 'B';

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_OKSKeyboard.cpp")
            {
                Product = $"oks:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Begin(io, wait, productId),
        };

        /// <summary>A kb2 packet: head, length, command, checksum, data
        /// (kb2M_init and kb2_ComputeChecksum, lines 137-165).</summary>
        internal static byte[] Packet(byte command, ReadOnlySpan<byte> data)
        {
            var packet = new byte[HeadSize + data.Length];
            packet[0] = Head;
            packet[1] = (byte)data.Length;
            packet[2] = command;
            byte checksum = (byte)(0x35 + Head + data.Length + command);
            if (data.Length > 0) checksum += data[^1];
            packet[3] = checksum;
            data.CopyTo(packet.AsSpan(HeadSize));
            return packet;
        }

        /// <summary>kb2M_wrgb (lines 166-176): the brightness as 32 bits,
        /// little-endian, the mode, the speed, the direction, FF FF.</summary>
        internal static byte[] WriteRgbPacket(byte brightness, byte mode, byte speed, byte direction)
            => Packet(WriteRgb, new byte[] { brightness, 0, 0, 0, mode, speed, direction, 0xFF, 0xFF });

        /// <summary>A packet in reports of report ID 4 and 64 bytes each
        /// (Send, lines 102-128).</summary>
        internal static List<byte[]> Reports(byte[] packet)
        {
            var reports = new List<byte[]>();
            for (int at = 0; at < packet.Length; at += 64)
            {
                var report = new byte[PacketSize];
                report[0] = ReportId;
                packet.AsSpan(at, Math.Min(64, packet.Length - at)).CopyTo(report.AsSpan(1));
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>The picture's nine reports (SendColors, lines 59-93, with
        /// the grid DeviceUpdateLEDs fills, RGBController_OKSKeyboard.cpp:223-252).
        /// Bytes 63 and 64 of each report, which OpenRGB leaves as whatever
        /// its packet buffer held, are 0 here.</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var grid = KeyboardLedNames.ByAddress(lights, colors, Places);
            var reports = new List<byte[]>(9);
            for (int first = 0; first < Places; first += 14)
            {
                var data = new byte[14 * 4 + 2];
                for (int n = 0; n < 14; n++)
                {
                    int pos = first + n;
                    data[n * 4] = (byte)pos;
                    data[n * 4 + 1] = (byte)(grid[pos] >> 16);
                    data[n * 4 + 2] = (byte)(grid[pos] >> 8);
                    data[n * 4 + 3] = (byte)grid[pos];
                }
                data[56] = 0xFF;
                data[57] = 0xFF;
                var report = new byte[PacketSize];
                report[0] = ReportId;
                Packet(WriteLed, data).CopyTo(report.AsSpan(1));
                reports.Add(report);
            }
            return reports;
        }

        private static bool Send(IHidReportChannel io, Action<int> wait, byte[] packet)
        {
            foreach (var report in Reports(packet))
            {
                if (!io.WriteOutput(report)) return false;
                wait(SendMs);
            }
            return true;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, ushort productId)
        {
            if (io == null) return null;
            bool kb87 = IsKb87(io.SerialNumber);
            if (!Send(io, wait, WriteRgbPacket(0, 0, Speed, Direction))
                || !Send(io, wait, WriteRgbPacket(DirectBrightness, 0, Speed, Direction)))
                return null;
            return new Session(kb87
                ? new LightMap(Kb87Lights(), "OpenRGB RGBController_OKSKeyboard.cpp") { Product = $"oks:{VendorId:X4}:{productId:X4}:kb87" }
                : null);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(LightMap map) => _map = map;

            /// <summary>The 87-key board's map, or null for the model's full
            /// board.</summary>
            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Nothing undoes the Direct mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                wait(ColorsMs);
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
