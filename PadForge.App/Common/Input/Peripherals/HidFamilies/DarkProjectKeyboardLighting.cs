using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Dark Project KD3B V2 keyboard (#557), lit key by key as OpenRGB's
    /// DarkProject controller lights it: 195D:2061 on interface 2, usage page
    /// 0xFFC2, usage 4 (DarkProjectControllerDetect.cpp:43). Its 87 keys are
    /// OpenRGB's led_names in order (RGBController_DarkProjectKeyboard.cpp:25-119
    /// and 161-196), each addressed by its place in that order.
    ///
    /// <para>The model of the device. OpenRGB has only a Direct mode for it
    /// and sends nothing ahead of it. A picture is two 256-byte output
    /// reports with report ID 8: 08 07 00 00 00 with each key's red at 5
    /// plus its place in packet_map and its green at 107 plus that place,
    /// then 08 07 00 01 00 with its blue at 5 plus the place (SetLedsDirect,
    /// DarkProjectKeyboardController.cpp:16-46 and 83-105; DARKPROJECTKEYBOARD_RED_BLUE_BYTE
    /// and _GREEN_BYTE, DarkProjectKeyboardController.h:29-35). The Direct
    /// mode does not save (its flags, RGBController_DarkProjectKeyboard.cpp:147).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the keyboard's own modes, so the keyboard keeps
    /// PadForge's last picture until its own controls change it. No source
    /// names a program that lights it.</para>
    /// </summary>
    internal sealed class DarkProjectKeyboardLighting : IHidLightingFamily
    {
        public static readonly DarkProjectKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "DarkProject";
        internal const ushort VendorId = 0x195D;

        /// <summary>DARKPROJECTKEYBOARD_PACKET_SIZE (DarkProjectKeyboardController.h:21).</summary>
        internal const int PacketSize = 256;
        internal const int RedBlueByte = 5;
        internal const int GreenByte = 107;

        /// <summary>DARKPROKECTKEYBOARD_TKL_KEYCOUNT, the lights OpenRGB
        /// makes (line 22; RGBController_DarkProjectKeyboard.cpp:169-171).</summary>
        internal const int KeyCount = 87;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Dark Project";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        internal static List<LightInfo> Lights() => KeyboardLedNames.FromNames(KeyboardLedNames.DarkProjectNames, i => i, KeyCount);

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_DarkProjectKeyboard.cpp")
            {
                Product = $"darkproject:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>The picture's two reports (SetLedsDirect, lines 83-105).</summary>
        internal static (byte[] RedGreen, byte[] Blue) Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var rg = new byte[PacketSize];
            var ba = new byte[PacketSize];
            rg[0] = ba[0] = 0x08;
            rg[1] = ba[1] = 0x07;
            ba[3] = 0x01;
            var map = KeyboardLedNames.DarkProjectPacketMap;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int place = lights[i].Address;
                if ((uint)place >= (uint)KeyCount) continue;
                int offset = map[place];
                rg[RedBlueByte + offset] = (byte)(colors[i] >> 16);
                rg[GreenByte + offset] = (byte)(colors[i] >> 8);
                ba[RedBlueByte + offset] = (byte)colors[i];
            }
            return (rg, ba);
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var (rg, blue) = Picture(lights, colors);
                return io.WriteOutput(rg) && io.WriteOutput(blue) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
