using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Thermaltake Poseidon Z RGB keyboard (#557), lit key by key as
    /// OpenRGB's ThermaltakePoseidonZRGBController lights it: 264A:3006 on
    /// interface 1, usage page 0xFF01 (ThermaltakePoseidonZRGBControllerDetect.cpp:17-18
    /// and 38), 264-byte feature reports with report ID 7. Its 104 keys are
    /// OpenRGB's led_names in order (RGBController_ThermaltakePoseidonZRGB.cpp:41-147),
    /// each addressed by its place in that order, and each place has its
    /// byte in the color reports, keys[] (ThermaltakePoseidonZRGBController.cpp:18-26).
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode:
    /// two feature reports, 07 0E 01 01 with each key's red at its byte and
    /// its green 128 bytes on, then, 5 ms later, 07 0E 01 02 with its blue at
    /// its byte (SetLEDsDirect, lines 75-125; POSEIDONZ_PACKET_ID_SET_DIRECT,
    /// POSEIDONZ_PROFILE_P1 and POSEIDONZ_DIRECT_RED_GREEN and _BLUE,
    /// ThermaltakePoseidonZRGBController.h:24-66). The Direct mode does not
    /// save (its flags, RGBController_ThermaltakePoseidonZRGB.cpp:171-176).
    /// When OpenRGB's Direct mode is chosen it also sends the effect report
    /// that makes the first profile's effect static (SetMode and SendControl,
    /// ThermaltakePoseidonZRGBController.cpp:56-73 and 218-253), which edits
    /// that profile's stored effect, so PadForge sends the direct reports
    /// alone, as OpenRGB does for a Direct picture while no mode has been
    /// chosen (DeviceUpdateLEDs, RGBController_ThermaltakePoseidonZRGB.cpp:250-258).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back from the direct colors, so the keyboard shows PadForge's
    /// last picture until its own controls change it. No source names a
    /// program that lights it.</para>
    /// </summary>
    internal sealed class PoseidonZRgbLighting : IHidLightingFamily
    {
        public static readonly PoseidonZRgbLighting Instance = new();

        internal const string OpenRgbFamily = "ThermaltakePoseidonZRGBController";
        internal const ushort VendorId = 0x264A;

        internal const int PacketSize = 264;
        internal const byte ReportId = 0x07;
        internal const byte SetDirect = 0x0E;
        internal const byte Profile1 = 0x01;
        internal const byte RedGreen = 0x01;
        internal const byte Blue = 0x02;

        /// <summary>The wait between the two reports (line 122).</summary>
        internal const int GapMs = 5;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Thermaltake Poseidon Z";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        internal static List<LightInfo> Lights() => KeyboardLedNames.FromNames(KeyboardLedNames.PoseidonNames, i => i);

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_ThermaltakePoseidonZRGB.cpp")
            {
                Product = $"thermaltake:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>The picture's two reports (lines 75-125).</summary>
        internal static (byte[] RedGreen, byte[] Blue) Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var rg = new byte[PacketSize];
            var b = new byte[PacketSize];
            rg[0] = b[0] = ReportId;
            rg[1] = b[1] = SetDirect;
            rg[2] = b[2] = Profile1;
            rg[3] = RedGreen;
            b[3] = Blue;
            var keys = KeyboardLedNames.PoseidonKeys;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int place = lights[i].Address;
                if ((uint)place >= (uint)keys.Length) continue;
                int at = keys[place];
                rg[at] = (byte)(colors[i] >> 16);
                rg[at + 128] = (byte)(colors[i] >> 8);
                b[at] = (byte)colors[i];
            }
            return (rg, b);
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
                var (rg, blue) = Picture(lights, colors);
                if (!io.SetFeature(rg)) return HidLightingPaint.Failed;
                wait(GapMs);
                return io.SetFeature(blue) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
