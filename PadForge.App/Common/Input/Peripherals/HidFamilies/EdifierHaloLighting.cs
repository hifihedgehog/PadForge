using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Edifier Halo Pixelbar speaker (#557), lit as OpenRGB's
    /// EdifierHaloController lights it: 2D99:A106 on interface 4, usage
    /// page 0xFF14, usage 1 (EdifierHaloControllerDetect.cpp:23-27 and 56).
    /// OpenRGB makes two controllers of the one device
    /// (lines 38-44), and here they are the model's two lights: zone 1, the
    /// light effects, and zone 2, the color of the pixel display's text
    /// (RGBController_EdifierHaloPixelbarAmbient.cpp:124-139,
    /// RGBController_EdifierHaloPixelbarScreen.cpp:89-104).
    ///
    /// <para>The frames. Every command is one 64-byte output report: ID
    /// 0x2E, then AA EC, the command, the payload's length in two bytes,
    /// the payload, and a checksum, the low byte of the sum of every byte
    /// from AA to the payload's last (SendEdifierPacket,
    /// EdifierHaloController.cpp:168-205). The light effects take the power
    /// command, 6B with 00 00 00 00 01 FF FF, and then the Static effect,
    /// 6B with 13, the effect 03, the color, the brightness 0x3C and the
    /// speed 01 (SetAmbientLightPower and SetAmbientLight, lines 83-120,
    /// as DeviceUpdateLEDs sends them, RGBController_EdifierHaloPixelbarAmbient.cpp:141-160):
    /// OpenRGB starts the controller in Static (line 115), whose brightness
    /// is 3, sent as 0x3C, and which has no speed, so the mode's speed of 0
    /// goes out as 1 (the same file's lines 65-73, EdifierHaloController.cpp:101-107,
    /// and a mode's speed of 0, RGBControllerInterface.h:339). The display's text takes
    /// EF with 03 and the color (SetPixelScreenColor, lines 122-132, as
    /// RGBController_EdifierHaloPixelbarScreen.cpp:106-128 sends it). Only a
    /// light whose color changed goes out.</para>
    ///
    /// <para>What the writes do. OpenRGB marks every mode of both
    /// controllers as saved by the device ("@save :robot:" and
    /// MODE_FLAG_AUTOMATIC_SAVE, RGBController_EdifierHaloPixelbarAmbient.cpp:18
    /// and 68), so a picture is held 150 ms and pictures go out no sooner
    /// than 500 ms apart, PadForge's spacing for writes that land in a
    /// device's memory (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Hand-back. No reference reads the speaker's effect or display
    /// color back, and OpenRGB sends nothing when it closes, so the speaker
    /// keeps PadForge's last colors, in its Static effect, until something
    /// else sets others. No source names a process of Edifier's software,
    /// so the model names no program.</para>
    /// </summary>
    internal sealed class EdifierHaloLighting : IHidLightingFamily
    {
        public static readonly EdifierHaloLighting Instance = new();

        internal const string OpenRgbFamily = "EdifierHaloController";

        /// <summary>packet[64] (line 175).</summary>
        internal const int PacketSize = 64;
        internal const byte ReportId = 0x2E;

        internal const byte AmbientCommand = 0x6B;
        internal const byte ScreenCommand = 0xEF;

        /// <summary>EDIFIER_HALO_AMBIENT_MODE_STATIC (EdifierHaloController.h:22).</summary>
        internal const byte StaticEffect = 0x03;

        /// <summary>The raw brightness of brightness 3, the Static mode's
        /// (EdifierHaloController.cpp:101-104).</summary>
        internal const byte Brightness = 0x3C;

        /// <summary>The speed a mode with none sends (lines 106-107).</summary>
        internal const byte Speed = 0x01;

        public string Name => "Edifier";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
        };

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_EdifierHaloPixelbarAmbient.cpp, RGBController_EdifierHaloPixelbarScreen.cpp")
            {
                Product = $"edifier:{vendorId:X4}:{productId:X4}",
            },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>SendEdifierPacket: the report around a command and its
        /// payload.</summary>
        internal static byte[] Packet(byte command, params byte[] payload)
        {
            var packet = new byte[PacketSize];
            packet[0] = ReportId;
            packet[1] = 0xAA;
            packet[2] = 0xEC;
            packet[3] = command;
            packet[4] = (byte)(payload.Length >> 8);
            packet[5] = (byte)payload.Length;
            Array.Copy(payload, 0, packet, 6, payload.Length);
            int sum = 0;
            for (int i = 1; i < 6 + payload.Length; i++) sum += packet[i];
            packet[6 + payload.Length] = (byte)sum;
            return packet;
        }

        /// <summary>SetAmbientLightPower(true).</summary>
        internal static byte[] PowerOn() => Packet(AmbientCommand, 0x00, 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF);

        /// <summary>SetAmbientLight in the Static effect.</summary>
        internal static byte[] Ambient(int rgb) => Packet(AmbientCommand, 0x13, StaticEffect,
            LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb), Brightness, Speed);

        /// <summary>SetPixelScreenColor.</summary>
        internal static byte[] Screen(int rgb) => Packet(ScreenCommand, 0x03,
            LightReports.Red(rgb), LightReports.Green(rgb), LightReports.Blue(rgb));

        internal sealed class Session : HidLightingSession
        {
            private int? _ambient;
            private int? _screen;

            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var byAddress = KeyboardLedNames.ByAddress(lights, colors, 2);
                if (_ambient != byAddress[0])
                {
                    if (!io.WriteOutput(PowerOn()) || !io.WriteOutput(Ambient(byAddress[0]))) return Fail();
                    _ambient = byAddress[0];
                }
                if (_screen != byAddress[1])
                {
                    if (!io.WriteOutput(Screen(byAddress[1]))) return Fail();
                    _screen = byAddress[1];
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>A failed write may still have landed, so the next
            /// picture sends both lights again.</summary>
            private HidLightingPaint Fail()
            {
                _ambient = null;
                _screen = null;
                return HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
