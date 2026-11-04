using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Skydimo SK0902 headset stand (#557), lit as OpenRGB's
    /// SkydimoHIDController lights it: 1A86:E316 on interface 0, usage page
    /// 0xFF00, usage 1 (SkydimoHIDControllerDetect.cpp:21-25 and 50). Its
    /// 49 lights are a 7 by 7 matrix, each at the column and row OpenRGB
    /// gives it (skydimo_hid_led_positions, RGBController_SkydimoHID.cpp:25-34
    /// and 64-99).
    ///
    /// <para>The model of the device. OpenRGB has only a Direct mode for it,
    /// which saves nothing ("@save :x:", line 18), and sends nothing ahead
    /// of it. A picture is the 49 colors as green, red and blue, 147 bytes,
    /// in 64-byte output reports with report ID 1, each carrying the first
    /// light's number in byte 1, a zero, up to 60 bytes of the colors from
    /// byte 3, and in byte 63 a CRC-8 with polynomial 0x07 and start 0 over
    /// bytes 0 to 62. Then comes the frame's end, ID 1, FF FF, the light count
    /// 49 and 0x1E in byte 60 (SetLEDs and SendPacket,
    /// SkydimoHIDController.cpp:17-39 and 107-195, the constants at
    /// SkydimoHIDController.h:20-42).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the stand's own lighting, so the stand shows PadForge's
    /// last picture until Skydimo's app sets another or it is unplugged.
    /// The app's executable is SkyDimo.exe (the file lists Advanced
    /// Uninstaller PRO gives for Skydimo's Windows installs, under
    /// C:\Program Files (x86)\Skydimo), which the model names.</para>
    /// </summary>
    internal sealed class SkydimoHidLighting : IHidLightingFamily
    {
        public static readonly SkydimoHidLighting Instance = new();

        internal const string OpenRgbFamily = "SkydimoHIDController";

        /// <summary>SKYDIMO_HID_* (SkydimoHIDController.h:20-42).</summary>
        internal const int LedCount = 49;
        internal const int MatrixSize = 7;
        internal const int PacketSize = 64;
        internal const int DataSize = 60;
        internal const int DataOffset = 3;
        internal const byte ReportId = 0x01;
        internal const byte CrcPolynomial = 0x07;
        internal const int EndMarkerIndex = 60;
        internal const byte EndMarker = 0x1E;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        /// <summary>skydimo_hid_led_positions: column, row of each light
        /// (RGBController_SkydimoHID.cpp:25-34).</summary>
        internal static readonly (int Col, int Row)[] Positions =
        {
            (0, 6), (0, 5), (0, 4), (0, 3), (0, 2), (0, 1), (0, 0),
            (1, 0), (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6),
            (2, 6), (2, 5), (2, 4), (2, 3), (2, 2), (2, 1), (2, 0),
            (3, 0), (3, 1), (3, 2), (3, 3), (3, 4), (3, 5), (3, 6),
            (4, 6), (4, 5), (4, 4), (4, 3), (4, 2), (4, 1), (4, 0),
            (5, 0), (5, 1), (5, 2), (5, 3), (5, 4), (5, 5), (5, 6),
            (6, 6), (6, 5), (6, 4), (6, 3), (6, 2), (6, 1), (6, 0),
        };

        public string Name => "Skydimo";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[LedCount];
            for (int i = 0; i < LedCount; i++)
                lights[i] = new LightInfo(i, LightKind.Other, 0, LightKeySource.None)
                {
                    X = Positions[i].Col, Y = Positions[i].Row, Width = 1, Height = 1, HasPosition = true,
                };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_SkydimoHID.cpp") { Product = $"skydimo:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.Skydimo },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(false, LedCount, Frames),
        };

        /// <summary>CalculateCRC8 (SkydimoHIDController.cpp:17-39).</summary>
        internal static byte Crc8(byte[] data, int length)
        {
            byte crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ CrcPolynomial) : (byte)(crc << 1);
            }
            return crc;
        }

        /// <summary>SetLEDs' reports for the colors by light.</summary>
        internal static IReadOnlyList<byte[]> Frames(int[] colors)
        {
            var data = new byte[LedCount * 3];
            for (int i = 0; i < LedCount && i < colors.Length; i++)
            {
                data[i * 3] = LightReports.Green(colors[i]);
                data[i * 3 + 1] = LightReports.Red(colors[i]);
                data[i * 3 + 2] = LightReports.Blue(colors[i]);
            }
            var frames = new List<byte[]>(4);
            for (int offset = 0; offset < data.Length; offset += DataSize)
            {
                var packet = new byte[PacketSize];
                packet[0] = ReportId;
                packet[1] = (byte)(offset / 3);
                Array.Copy(data, offset, packet, DataOffset, Math.Min(DataSize, data.Length - offset));
                packet[PacketSize - 1] = Crc8(packet, PacketSize - 1);
                frames.Add(packet);
            }
            var end = new byte[PacketSize];
            end[0] = ReportId;
            end[1] = 0xFF;
            end[2] = 0xFF;
            end[3] = LedCount;
            end[EndMarkerIndex] = EndMarker;
            frames.Add(end);
            return frames;
        }
    }
}
