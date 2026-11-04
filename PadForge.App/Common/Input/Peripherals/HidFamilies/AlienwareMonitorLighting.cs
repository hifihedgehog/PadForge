using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's AlienwareMonitorController family (#557): the Alienware
    /// AW3423DWF (187C:100E) and AW3225QF (187C:1013) monitors. Both detectors
    /// register the vendor and product ID alone
    /// (AlienwareAW3423DWFControllerDetect.cpp:47,
    /// AlienwareMonitorControllerDetect.cpp:47), so every collection of the
    /// monitor answers, and OpenRGB opens a controller on each. Only a
    /// collection with output reports as long as the family's frames carries
    /// them (hid_write of 193 and 65 bytes, AlienwareAW3423DWFController.cpp:27
    /// and AlienwareMonitorController.cpp:83), so PadForge lists that one, as
    /// <see cref="AsusAuraUsbLighting.Carries"/> does for a registration
    /// that names no usage page.
    ///
    /// <para>The lights. Three, each OpenRGB's zone of one LED with the LED's
    /// value its address: the logo (01), the number on the back (02) and the
    /// power button (08) (SetupZones, RGBController_AlienwareAW3423DWF.cpp:54-96
    /// and RGBController_AlienwareMonitor.cpp:54-96). Dell's user guide names
    /// the same three: the Alienware logo, the number graphic and the power
    /// button (AW3423DWF User's Guide, "AlienFX Lighting"). PadForge's kinds:
    /// the logo a logo light, the power button's light a status light, the
    /// number an other light.</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode, its only mode. Three equal
    /// colors go out as one write to the target 0B, "ALL". Three different
    /// ones as one write per light in the order above
    /// (DeviceUpdateLEDs, RGBController_AlienwareAW3423DWF.cpp:98-119 and
    /// RGBController_AlienwareMonitor.cpp:98-119).</para>
    ///
    /// <para>The AW3225QF. A session starts with OpenRGB's Initialize: three
    /// 65-byte output reports, report ID 0, each followed by 50 ms
    /// (AlienwareMonitorController.cpp:91-146). A color is one output report,
    /// 92 37 0A 00 51 87 D0 04, the light, red, green, blue, 64 and a
    /// checksum, 110 XOR bytes 5 to 13, the rest FF, followed by 50 ms
    /// (SendColor and fillInChecksum, lines 47-89).</para>
    ///
    /// <para>The AW3423DWF. Every color is a login and then the color, each
    /// a 193-byte output report, report ID 0: 40 E1 01, a read of input
    /// report 0, 40 E1 02 with a key made from the read at byte 64 of the
    /// data, then 40 C6 with the color at byte 68 of the data, followed by
    /// 200 ms (SendColor, PerformLogin, GetReportResponse and GenerateKey,
    /// AlienwareAW3423DWFController.cpp:22-158). The read is
    /// hid_get_input_report (<see cref="IHidInputReportReader"/>), which
    /// the shared channel makes as HidD_GetInputReport. A channel that
    /// cannot make it gets no session, and nothing is written.</para>
    ///
    /// <para>What the writes store. OpenRGB marks both controllers as ones
    /// that save nothing ("@save :x:", RGBController_AlienwareAW3423DWF.cpp:18
    /// and RGBController_AlienwareMonitor.cpp:18), and neither has a save
    /// command. No reference names what the AW3225QF's three opening reports
    /// set.</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the monitor's
    /// lighting or returns it to the effect set in its own AlienFX menu. The
    /// monitor shows PadForge's last colors until that menu or Alienware
    /// Command Center sets others. Alienware Command Center lights the
    /// monitors (Dell knowledge base 000141237: AlienFX is integrated into
    /// it), and while it runs they are left to it
    /// (<see cref="VendorPrograms.AlienwareCommandCenter"/>).</para>
    /// </summary>
    internal sealed class AlienwareMonitorLighting : IHidLightingFamily
    {
        public static readonly AlienwareMonitorLighting Instance = new();

        internal const string OpenRgbFamily = "AlienwareMonitorController";

        /// <summary>ALIENWARE_VID and the two product IDs
        /// (AlienwareAW3423DWFControllerDetect.cpp:20-25,
        /// AlienwareMonitorControllerDetect.cpp:20-25).</summary>
        internal const ushort VendorId = 0x187C;
        internal const ushort Aw3423Dwf = 0x100E;
        internal const ushort Aw3225Qf = 0x1013;

        /// <summary>The AW3225QF's report with its ID (packet[65],
        /// AlienwareMonitorController.cpp:61).</summary>
        internal const int QfReportSize = 65;

        /// <summary>The AW3423DWF's report with its ID: 192 data bytes behind
        /// report ID 0 (SendControlPacket, AlienwareAW3423DWFController.cpp:22-28,
        /// the packets at lines 43, 53 and 69), and the input report read
        /// for the key (GetReportResponse, lines 30-39).</summary>
        internal const int DwfReportSize = 193;

        /// <summary>The bytes of the input report the key is made from,
        /// buffer bytes 1 to 17 (line 38).</summary>
        internal const int DwfResponseLength = 17;

        /// <summary>The waits after each AW3225QF report and after each
        /// AW3423DWF color (AlienwareMonitorController.cpp:85-88 and 105-145,
        /// AlienwareAW3423DWFController.cpp:90).</summary>
        internal const int QfWaitMs = 50;
        internal const int DwfWaitMs = 200;

        /// <summary>The target that colors every light at once (0x0B,
        /// RGBController_AlienwareAW3423DWF.cpp:100-110).</summary>
        internal const byte AllTarget = 0x0B;

        /// <summary>The lights' values: logo, number, power button
        /// (RGBController_AlienwareAW3423DWF.cpp:66, 79 and 92).</summary>
        internal static readonly byte[] LedValues = { 0x01, 0x02, 0x08 };

        /// <summary>The shortest time between two pictures. OpenRGB's Direct
        /// mode saves nothing and no reference bounds the rate, so the bound
        /// is PadForge's (<see cref="AsusTufLaptopLighting.RepaintMs"/>). The
        /// waits each picture takes set the pace before it.</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Alienware Monitor";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            bool dwf = registration.Detector == "DetectAlienwareAW3423DWFControllers";
            if (collection.OutputReportLength < (dwf ? DwfReportSize : QfReportSize)) return null;
            return _models.Get(registration.Name, () => Model(registration.Name, collection.ProductId, dwf));
        }

        /// <summary>The logo, the number and the power button, each at its
        /// LED value.</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(LedValues[0], LightKind.Logo, 0, LightKeySource.None),
            new LightInfo(LedValues[1], LightKind.Other, 0, LightKeySource.None),
            new LightInfo(LedValues[2], LightKind.Status, 0, LightKeySource.None),
        };

        private static HidLightingModel Model(string name, ushort productId, bool dwf) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), dwf ? "OpenRGB RGBController_AlienwareAW3423DWF.cpp" : "OpenRGB RGBController_AlienwareMonitor.cpp")
            {
                Product = $"alienware:{VendorId:X4}:{productId:X4}",
            },
            Programs = new[] { VendorPrograms.AlienwareCommandCenter },
            RepaintMs = RepaintMs,
            Begin = dwf ? BeginDwf : BeginQf,
        };

        // ─────────────────────────────────────────────
        //  The picture
        // ─────────────────────────────────────────────

        /// <summary>The writes of one picture, target and color: one to
        /// <see cref="AllTarget"/> when the three colors are equal, else one
        /// per light in the order of <see cref="LedValues"/>.</summary>
        internal static List<(byte Led, int Rgb)> Writes(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byLed = new int[LedValues.Length];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int place = Array.IndexOf(LedValues, (byte)lights[i].Address);
                if (place >= 0 && lights[i].Address <= 0xFF) byLed[place] = colors[i] & 0xFFFFFF;
            }
            var writes = new List<(byte, int)>(LedValues.Length);
            if (byLed[0] == byLed[1] && byLed[1] == byLed[2])
            {
                writes.Add((AllTarget, byLed[0]));
                return writes;
            }
            for (int i = 0; i < LedValues.Length; i++) writes.Add((LedValues[i], byLed[i]));
            return writes;
        }

        // ─────────────────────────────────────────────
        //  AW3225QF
        // ─────────────────────────────────────────────

        /// <summary>A 65-byte report filled with <paramref name="fill"/>,
        /// report ID 0, then the bytes given from byte 1.</summary>
        private static byte[] QfReport(byte fill, params byte[] bytes)
        {
            var report = new byte[QfReportSize];
            Array.Fill(report, fill);
            report[0] = 0x00;
            bytes.CopyTo(report, 1);
            return report;
        }

        /// <summary>Initialize's three reports (lines 91-146): 95 00 00 00 in
        /// FF, 92 37 08 00 51 85 01 FE 03 00 06 40 in FF, and 93 37 12 in
        /// zeros.</summary>
        internal static byte[][] QfInitFrames() => new[]
        {
            QfReport(0xFF, 0x95, 0x00, 0x00, 0x00),
            QfReport(0xFF, 0x92, 0x37, 0x08, 0x00, 0x51, 0x85, 0x01, 0xFE, 0x03, 0x00, 0x06, 0x40),
            QfReport(0x00, 0x93, 0x37, 0x12),
        };

        /// <summary>SendColor's report (lines 59-83), its checksum by
        /// fillInChecksum (lines 47-57).</summary>
        internal static byte[] QfColorFrame(byte led, int rgb)
        {
            var report = QfReport(0xFF, 0x92, 0x37, 0x0A, 0x00, 0x51, 0x87, 0xD0, 0x04,
                led, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x64);
            byte checksum = 110;
            for (int index = 5; index <= 13; index++) checksum ^= report[index];
            report[14] = checksum;
            return report;
        }

        private static HidLightingSession BeginQf(IHidReportChannel io, Action<int> wait)
        {
            if (io == null) return null;
            // The controller's constructor sends Initialize once
            // (AlienwareMonitorController.cpp:18-25).
            foreach (var frame in QfInitFrames())
            {
                if (!io.WriteOutput(frame)) return null;
                wait(QfWaitMs);
            }
            return new QfSession();
        }

        internal sealed class QfSession : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var (led, rgb) in Writes(lights, colors))
                {
                    if (!io.WriteOutput(QfColorFrame(led, rgb))) return HidLightingPaint.Failed;
                    wait(QfWaitMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  AW3423DWF
        // ─────────────────────────────────────────────

        /// <summary>A 193-byte report, report ID 0, its data from byte 1 as
        /// SendControlPacket copies it (lines 22-28): the data bytes given at
        /// their data offsets, zero elsewhere.</summary>
        private static byte[] DwfReport(params (int At, byte Value)[] data)
        {
            var report = new byte[DwfReportSize];
            foreach (var (at, value) in data) report[1 + at] = value;
            return report;
        }

        /// <summary>The login's first report, 40 E1 01 (lines 43-47).</summary>
        internal static byte[] DwfLoginStart() => DwfReport((0, 0x40), (1, 0xE1), (2, 0x01));

        /// <summary>The login's second report, 40 E1 02 and the key at data
        /// byte 64 (lines 53-59).</summary>
        internal static byte[] DwfLoginKey(byte[] key)
        {
            var report = DwfReport((0, 0x40), (1, 0xE1), (2, 0x02));
            Array.Copy(key, 0, report, 1 + 64, Math.Min(key.Length, 8));
            return report;
        }

        /// <summary>SendColor's report (lines 62-88): 40 C6, 0A at 6, 6E at 8,
        /// 82 at 10, 51 87 D0 04 at 64, the light, red, green, blue, 64 and
        /// the light's second ID at 68, the second ID F6 for 01, F5 for 02,
        /// FF for 08 and FC for any other (lines 64-66).</summary>
        internal static byte[] DwfColorFrame(byte led, int rgb)
        {
            byte second = led == 0x01 ? (byte)0xF6 : led == 0x02 ? (byte)0xF5 : led == 0x08 ? (byte)0xFF : (byte)0xFC;
            return DwfReport((0, 0x40), (1, 0xC6), (6, 0x0A), (8, 0x6E), (10, 0x82),
                (64, 0x51), (65, 0x87), (66, 0xD0), (67, 0x04),
                (68, led), (69, (byte)(rgb >> 16)), (70, (byte)(rgb >> 8)), (71, (byte)rgb), (72, 0x64), (73, second));
        }

        /// <summary>The OEM key GenerateKey folds into its synthetic key
        /// (lines 98-100).</summary>
        private static readonly byte[] OemKey = { 0xF5, 0x3F, 0xC1, 0x39, 0x44, 0x3A, 0x31, 0x79, 0x0D, 0xB1, 0x82, 0x76 };

        /// <summary>GenerateKey (lines 93-158): the eight-byte key the login
        /// sends, from the 17 bytes of the input report after its ID.</summary>
        internal static byte[] GenerateKey(byte[] response)
        {
            var key = new byte[8];
            int sk = 0, ok = 0;
            while (ok < OemKey.Length && sk < 8)
            {
                int run = (OemKey[ok] & 1) + ((OemKey[ok] & 0x10) >> 4);
                for (int i = 0; i < run && sk < 8; i++)
                {
                    key[sk] = (byte)(OemKey[ok + 1] ^ OemKey[ok]);
                    ok++;
                    sk++;
                }
                ok++;
            }

            int v15 = response[15] | (response[0] << 8);
            bool odd = (System.Numerics.BitOperations.PopCount((uint)v15) & 1) != 0;
            byte[] mix;
            if (odd)
            {
                mix = new byte[8];
                Array.Copy(response, 0, mix, 0, 8);
                int index = response[14] & 0x07;
                mix[index] ^= response[index + 8];
            }
            else
            {
                mix = new byte[8];
                Array.Copy(response, 8, mix, 0, 8);
                int index = response[6] & 0x07;
                mix[index] ^= response[index];
            }
            for (int i = 0; i < 8; i++) key[i] ^= mix[i];
            return key;
        }

        private static HidLightingSession BeginDwf(IHidReportChannel io, Action<int> wait)
            => io is IHidInputReportReader ? new DwfSession() : null;

        internal sealed class DwfSession : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io is not IHidInputReportReader reader) return HidLightingPaint.Failed;
                foreach (var (led, rgb) in Writes(lights, colors))
                {
                    if (!io.WriteOutput(DwfLoginStart())) return HidLightingPaint.Failed;
                    var buffer = new byte[DwfReportSize];
                    if (!reader.GetInputReport(0x00, buffer)) return HidLightingPaint.Failed;
                    var response = new byte[DwfResponseLength];
                    Array.Copy(buffer, 1, response, 0, DwfResponseLength);
                    if (!io.WriteOutput(DwfLoginKey(GenerateKey(response)))) return HidLightingPaint.Failed;
                    if (!io.WriteOutput(DwfColorFrame(led, rgb))) return HidLightingPaint.Failed;
                    wait(DwfWaitMs);
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
