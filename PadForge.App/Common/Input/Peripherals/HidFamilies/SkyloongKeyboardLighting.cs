using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Skyloong GK104 Pro keyboard (#557), lit key by key as OpenRGB's
    /// SkyloongController lights it: 1EA7:0907 on interface 1
    /// (SkyloongControllerDetect.cpp:20-45), 65-byte output reports with
    /// report ID 0, each carrying a CRC-16/CCITT-FALSE of all 65 bytes in
    /// bytes 7 and 8 (SendCommand and Crc16CcittFalse,
    /// SkyloongGK104ProController.cpp:51-65 and 118-140). OpenRGB registers
    /// the interface without a usage, so the collection taken is the one
    /// whose output report carries a frame. Its keys are OpenRGB's
    /// keyboard_offset_values with its edits, each at its LED value
    /// (RGBController_SkyloongGK104Pro.cpp:21-40 and 84-131).
    ///
    /// <para>The model of the device. A session pings the keyboard, puts it
    /// online and pings it again, as OpenRGB's controller does when it opens
    /// (0C, 0B 05, 0C, lines 24-33). A picture is the LE define area in
    /// 56-byte pieces, each "set" (1A 01) with its address and length, the
    /// color of each LED at four times its value as red, green, blue and
    /// brightness 127, then "save" (1A 02) (SendColorPacket and SetLEDefine,
    /// lines 67-116; BRIGHTNESS_MAX, RGBController_SkyloongGK104Pro.h:18).
    /// OpenRGB sends that for its Direct mode, whose updates it does not mark
    /// as saving, at every picture (lines 63-72 and 133-136), so PadForge
    /// does the same at its repaint interval. The hand-back takes the
    /// keyboard offline again, 0B 04, as OpenRGB's controller does when it
    /// closes (lines 35-39), and the crash path sends the same report. No
    /// source names a program that lights it.</para>
    /// </summary>
    internal sealed class SkyloongKeyboardLighting : IHidLightingFamily
    {
        public static readonly SkyloongKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "SkyloongController";
        internal const ushort VendorId = 0x1EA7;

        /// <summary>PACKET_SIZE, TOTAL_LED_BYTES and LED_BYTES_IN_CHUNK
        /// (SkyloongGK104ProController.h:19-22).</summary>
        internal const int PacketSize = 65;
        internal const int TotalLedBytes = 528;
        internal const int Chunk = 56;

        /// <summary>The commands (SkyloongGK104ProController.cpp:17-22) and
        /// their sub-commands (SkyloongGK104ProController.h:24-30).</summary>
        internal const byte Ping = 0x0C;
        internal const byte Mode = 0x0B;
        internal const byte LeDefine = 0x1A;
        internal const byte Offline = 0x04;
        internal const byte Online = 0x05;
        internal const byte Set = 0x01;
        internal const byte Save = 0x02;

        /// <summary>The Direct mode's brightness, BRIGHTNESS_MAX
        /// (RGBController_SkyloongGK104Pro.h:18, .cpp:70).</summary>
        internal const byte Brightness = 127;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Skyloong";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0 || collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        internal static KeyboardLayoutManager Layout() => KeyboardLayouts.Build("skyloong_gk104_pro", KeyboardLayout.AnsiQwerty);

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(KeyboardLedNames.FromLayout(Layout()), "OpenRGB RGBController_SkyloongGK104Pro.cpp")
            {
                Product = $"skyloong:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (io, _) => Begin(io),
        };

        /// <summary>Crc16CcittFalse (SkyloongGK104ProController.cpp:118-140).</summary>
        internal static ushort Crc(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in bytes)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
            return crc;
        }

        private static byte[] Sealed(byte[] report)
        {
            ushort crc = Crc(report);
            report[7] = (byte)crc;
            report[8] = (byte)(crc >> 8);
            return report;
        }

        /// <summary>SendCommand (lines 51-65).</summary>
        internal static byte[] Command(byte command, byte sub)
        {
            var report = new byte[PacketSize];
            report[1] = command;
            report[2] = sub;
            return Sealed(report);
        }

        /// <summary>SetLEDefine (lines 95-116): the address in bytes 3 to 5,
        /// the length in byte 6, the data from byte 9.</summary>
        internal static byte[] Define(int address, ReadOnlySpan<byte> data)
        {
            var report = new byte[PacketSize];
            report[1] = LeDefine;
            report[2] = Set;
            int header = address + (data.Length << 24);
            report[3] = (byte)header;
            report[4] = (byte)(header >> 8);
            report[5] = (byte)(header >> 16);
            report[6] = (byte)(header >> 24);
            data.CopyTo(report.AsSpan(9));
            return Sealed(report);
        }

        /// <summary>A picture's reports (SendColorPacket, lines 67-93).</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var area = new byte[TotalLedBytes];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int index = lights[i].Address * 4;
                if (index < 0 || index + 3 >= TotalLedBytes) continue;
                area[index] = (byte)(colors[i] >> 16);
                area[index + 1] = (byte)(colors[i] >> 8);
                area[index + 2] = (byte)colors[i];
                area[index + 3] = Brightness;
            }
            var reports = new List<byte[]>();
            for (int n = 0; n < TotalLedBytes; n += Chunk)
                reports.Add(Define(n, area.AsSpan(n, Math.Min(Chunk, TotalLedBytes - n))));
            reports.Add(Command(LeDefine, Save));
            return reports;
        }

        private static HidLightingSession Begin(IHidReportChannel io)
        {
            if (io == null || !io.WriteOutput(Command(Ping, 0)) || !io.WriteOutput(Command(Mode, Online))
                || !io.WriteOutput(Command(Ping, 0)))
                return null;
            return new Session();
        }

        internal sealed class Session : HidLightingSession
        {
            private byte[][] _crash = { Command(Mode, Offline) };

            public override bool Feature => false;

            /// <summary>The offline report, from the moment the keyboard went online.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (io == null || !io.WriteOutput(_crash[0])) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
