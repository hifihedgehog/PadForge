using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Turtle Beach Command Series KP7 keypad (#557), lit key by key as
    /// OpenRGB's TurtleBeachKP7Controller lights it: 10F5:503A on interface
    /// 4 (TurtleBeachKP7ControllerDetect.cpp:17-33, TurtleBeachKP7Controller.h:23-32).
    ///
    /// <para>The lights. OpenRGB lists 33 in three zones: the Num Lock and
    /// Shift Mode indicators and the Shift Mode key, the 28 keys of the
    /// keypad, and the two wrist rest lights
    /// (RGBController_TurtleBeachKP7.cpp:120-217). Each light's address is
    /// its slot in the keypad's color buffer, which turtle_beach_kp7_led_order
    /// gives for each light in that order (lines 28-48). A key OpenRGB names
    /// with a key name is that key's usage (two Number Pad + keys and two
    /// Number Pad Enter keys among them). The F13 to F15 row's Profile
    /// Switch key and the four Mode keys have no usage. The keypad's lights
    /// sit at their cells of OpenRGB's 7 by 5 matrix (lines 50-74), and the
    /// indicators and the wrist rest, which OpenRGB places in no matrix,
    /// have no place.</para>
    ///
    /// <para>The model of the device. A session sends the feature report
    /// 0E 00 01 00 00, "Without this write the device stays locked to its
    /// onboard preset effects" (EnterDirectMode,
    /// TurtleBeachKP7Controller.cpp:86-109, TurtleBeachKP7Controller.h:51-57).
    /// A picture is the colors as red, green and blue per slot, 99 bytes,
    /// in two output reports of 64 bytes with report ID 0xEF: EF 00 and the
    /// first 62 bytes, then EF 01 and the other 37, zero padded (SendFrame,
    /// TurtleBeachKP7Controller.cpp:133-187, TurtleBeachKP7Controller.h:34-49).
    /// "The device reverts to its onboard hue shift effect if it stops
    /// hearing from the host", so OpenRGB sends the last picture again every
    /// 50 ms (KeepAliveThreadFunction, lines 189-210, TurtleBeachKP7Controller.h:59-64),
    /// and the worker does the same as the session's keep-alive. Nothing is
    /// saved ("@save :x:", RGBController_TurtleBeachKP7.cpp:80).</para>
    ///
    /// <para>Hand-back. Stopping: with no picture the keypad returns to its
    /// onboard effect on its own, so the hand-back sends nothing. Turtle
    /// Beach lights the keypad through Swarm II
    /// (<see cref="VendorPrograms.TurtleBeachSwarmII"/>), and PadForge leaves
    /// it to Swarm II while it runs.</para>
    ///
    /// <para>The collection. OpenRGB opens every collection of interface 4.
    /// A collection that takes the 64-byte output report and the 5-byte
    /// feature report is the one the frames reach, so a collection with a
    /// shorter report of either kind is left out.</para>
    /// </summary>
    internal sealed class TurtleBeachKP7Lighting : IHidLightingFamily
    {
        public static readonly TurtleBeachKP7Lighting Instance = new();

        internal const string OpenRgbFamily = "TurtleBeachKP7Controller";
        internal const ushort VendorId = 0x10F5;

        /// <summary>TURTLE_BEACH_KP7_LED_COUNT, _REPORT_SIZE, _REPORT_ID,
        /// _PAGE0_LED_BYTES, _PAGE1_LED_BYTES, _ENTER_DIRECT_REPORT_ID and
        /// _KEEPALIVE_DELAY_MS (TurtleBeachKP7Controller.h:45-64).</summary>
        internal const int LedCount = 33;
        internal const int ReportSize = 64;
        internal const byte ReportId = 0xEF;
        internal const int Page0Bytes = 62;
        internal const int Page1Bytes = 37;
        internal const byte EnterDirectReportId = 14;
        internal const int EnterDirectSize = 5;
        internal const int KeepAliveMs = 50;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the keep-alive's
        /// interval.</summary>
        internal const int RepaintMs = KeepAliveMs;

        /// <summary>turtle_beach_kp7_led_order: each light's slot in the
        /// color buffer, in OpenRGB's light order
        /// (RGBController_TurtleBeachKP7.cpp:37-48).</summary>
        internal static readonly int[] LedOrder =
        {
            28, 29, 30,
            21, 0, 7, 14,
            8, 22, 15, 1, 11,
            23, 2, 9, 4, 18,
            10, 24, 16, 25, 5,
            17, 3, 13, 12, 19,
            27, 6, 20,
            26,
            31, 32,
        };

        /// <summary>OpenRGB's names in the same order: the indicator zone,
        /// the keypad zone and the wrist rest (lines 131-147 and 206-214;
        /// the key names, RGBControllerKeyNames.cpp:109-150).</summary>
        internal static readonly string[] LedNames =
        {
            "Num Lock Indicator", "Shift Mode Indicator", "Key: Shift Mode",
            "Key: F13", "Key: F14", "Key: F15", "Key: Profile Switch",
            "Key: Num Lock", "Key: Number Pad /", "Key: Number Pad *", "Key: Number Pad -", "Key: Mode 1",
            "Key: Number Pad 7", "Key: Number Pad 8", "Key: Number Pad 9", "Key: Number Pad +", "Key: Mode 2",
            "Key: Number Pad 4", "Key: Number Pad 5", "Key: Number Pad 6", "Key: Number Pad +", "Key: Mode 3",
            "Key: Number Pad 1", "Key: Number Pad 2", "Key: Number Pad 3", "Key: Number Pad Enter", "Key: Mode 4",
            "Key: Number Pad 0", "Key: Number Pad .", "Key: Number Pad Enter",
            "Key: Space",
            "Wrist Rest 1", "Wrist Rest 2",
        };

        internal const int IndicatorCount = 3;
        internal const int KeypadCount = 28;

        /// <summary>turtle_beach_kp7_matrix_map: the keypad light at each
        /// cell, by its index in the keypad zone, -1 for none
        /// (lines 62-74).</summary>
        internal static readonly int[,] Matrix =
        {
            { 0, 1, 2, 3, -1 },
            { 4, 5, 6, 7, 8 },
            { 9, 10, 11, 12, 13 },
            { 14, 15, 16, 17, 18 },
            { 19, 20, 21, 22, 23 },
            { 24, -1, 25, 26, -1 },
            { -1, -1, -1, 27, -1 },
        };

        public string Name => "Turtle Beach keypads";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < ReportSize || collection.FeatureReportLength < EnterDirectSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        /// <summary>The 33 lights in OpenRGB's order, each addressed by its
        /// slot.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(LedCount);
            for (int i = 0; i < LedCount; i++)
            {
                int slot = LedOrder[i];
                string name = LedNames[i];
                LightInfo light;
                if (i < 2)
                    light = new LightInfo(slot, LightKind.Status, 0, LightKeySource.None)
                    {
                        Label = i == 0 ? LightLabel.NumLock : LightLabel.None,
                    };
                else if (i >= IndicatorCount + KeypadCount)
                    light = new LightInfo(slot, LightKind.Edge, 0, LightKeySource.None);
                else
                    light = KeyboardLedNames.Named(slot, name).Value;
                int keypad = i - IndicatorCount;
                if (keypad >= 0 && keypad < KeypadCount)
                {
                    for (int row = 0; row < Matrix.GetLength(0); row++)
                        for (int col = 0; col < Matrix.GetLength(1); col++)
                            if (Matrix[row, col] == keypad)
                                light = light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true };
                }
                lights.Add(light);
            }
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_TurtleBeachKP7.cpp") { Product = $"kp7:{VendorId:X4}:{productId:X4}" },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            Programs = VendorPrograms.TurtleBeach,
            Begin = (io, _) => io != null && io.SetFeature(EnterDirect()) ? new Session() : null,
        };

        /// <summary>EnterDirectMode's report (TurtleBeachKP7Controller.cpp:86-109).</summary>
        internal static byte[] EnterDirect() => new byte[] { EnterDirectReportId, 0x00, 0x01, 0x00, 0x00 };

        /// <summary>A picture's two output reports, SendFrame (lines 133-187)
        /// with the colors placed by slot as DeviceUpdateLEDs places them
        /// (RGBController_TurtleBeachKP7.cpp:219-237).</summary>
        internal static byte[][] Frames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var bySlot = KeyboardLedNames.ByAddress(lights, colors, LedCount);
            var data = new byte[LedCount * 3];
            for (int slot = 0; slot < LedCount; slot++)
            {
                data[slot * 3] = (byte)(bySlot[slot] >> 16);
                data[slot * 3 + 1] = (byte)(bySlot[slot] >> 8);
                data[slot * 3 + 2] = (byte)bySlot[slot];
            }
            var page0 = new byte[ReportSize];
            page0[0] = ReportId;
            page0[1] = 0x00;
            Array.Copy(data, 0, page0, 2, Page0Bytes);
            var page1 = new byte[ReportSize];
            page1[0] = ReportId;
            page1[1] = 0x01;
            Array.Copy(data, Page0Bytes, page1, 2, Page1Bytes);
            return new[] { page0, page1 };
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing: the keypad returns to its onboard effect
            /// once the pictures stop.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Frames(lights, colors))
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
