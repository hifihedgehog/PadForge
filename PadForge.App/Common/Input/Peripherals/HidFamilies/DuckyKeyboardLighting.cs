using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Ducky's One 2 RGB and Shine 7 keyboards, lit as OpenRGB's
    /// DuckyKeyboardController lights them (#557), key by key, with the keys
    /// of its led_names table (RGBController_DuckyKeyboard.cpp:55-189), the
    /// full table for the Shine 7 and One 2 RGB and its first 108 for the
    /// One 2 RGB TKL (zone_sizes and zone_sizes_tkl, lines 45-53 and
    /// 236-272).
    ///
    /// <para>A session takes the lighting over with 41 01, as OpenRGB's
    /// controller does when it opens (SendInitialize,
    /// DuckyKeyboardController.cpp:81-102) and Aurora's Ducky device does
    /// (DuckyTakeover, Aurora DuckyRGBMappings.cs:16). Each picture is a
    /// start frame (56 81), eight frames of colors (56 83) and the apply,
    /// 51 28 00 00 FF, each followed by OpenRGB's 2 ms wait (SendColors,
    /// lines 59-79 and 104-215). None is a save. OpenRGB's color buffer is 465
    /// bytes, and the bytes past the keyboard's colors are whatever the stack
    /// held (RGBController_DuckyKeyboard.cpp:279-291). Here they are 0.</para>
    ///
    /// <para>Hand-back: 41 00, Aurora's DuckyRelease
    /// (DuckyRGBMappings.cs:17), which its Shutdown sends to hand the LEDs
    /// back to the keyboard (DuckyDevice.cs:67-80). OpenRGB sends nothing
    /// when it closes. No source names a Ducky program that lights these
    /// keyboards, so none is in the vendor-program table.</para>
    /// </summary>
    internal sealed class DuckyKeyboardLighting : IHidLightingFamily
    {
        public static readonly DuckyKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "DuckyKeyboardController";

        /// <summary>DUCKY_VID and the two PIDs (DuckyKeyboardController.h:21-27).</summary>
        internal const ushort VendorId = 0x04D9;
        internal const ushort OneTwoTkl = 0x0356;

        /// <summary>The size of OpenRGB's color buffer, 155 colors
        /// (RGBController_DuckyKeyboard.cpp:281).</summary>
        internal const int ColorBytes = 155 * 3;

        /// <summary>The wait after every frame (DuckyKeyboardController.cpp:101
        /// and the frames after it).</summary>
        internal const int GapMs = 2;

        /// <summary>The shortest time between two pictures. No reference bounds
        /// it, so the bound is PadForge's, as the Cooler Master family's.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Ducky";

        private HidLightingModel _full, _tkl;

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            bool tkl = collection.ProductId == OneTwoTkl;
            if (tkl) return _tkl ??= Model(registration.Name, collection.ProductId, 108);
            return _full ??= Model(registration.Name, collection.ProductId, 132);
        }

        internal static HidLightingModel Model(string name, ushort productId, int count)
        {
            var names = new string[count];
            Array.Copy(OpenRgbLedNames.DuckyKeyboard, names, count);
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(HyperXKeyboardLighting.Named(names), "OpenRGB RGBController_DuckyKeyboard.cpp")
                {
                    Product = $"ducky:{VendorId:X4}:{productId:X4}",
                },
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Begin(io, wait),
            };
        }

        private static byte[] Frame(params (int At, byte Value)[] bytes)
        {
            var frame = new byte[65];
            foreach (var (at, value) in bytes) frame[at] = value;
            return frame;
        }

        /// <summary>41 01 and 41 00, Aurora's DuckyTakeover and DuckyRelease
        /// (DuckyRGBMappings.cs:16-17), the first OpenRGB's SendInitialize.</summary>
        internal static byte[] Control(bool takeover) => Frame((1, 0x41), (2, takeover ? (byte)0x01 : (byte)0x00));

        /// <summary>A picture's frames (DuckyKeyboardController.cpp:59-215).</summary>
        internal static List<byte[]> PictureFrames(int[] colors)
        {
            var data = new byte[ColorBytes];
            for (int i = 0; i < colors.Length && i * 3 + 2 < data.Length; i++)
            {
                data[i * 3] = (byte)(colors[i] >> 16);
                data[i * 3 + 1] = (byte)(colors[i] >> 8);
                data[i * 3 + 2] = (byte)colors[i];
            }
            var frames = new List<byte[]>
            {
                Frame((1, 0x56), (2, 0x81), (5, 0x01), (9, 0x08), (0x0D, 0xAA), (0x0E, 0xAA), (0x0F, 0xAA), (0x10, 0xAA)),
            };
            int at = 0;
            for (int id = 0; id < 8; id++)
            {
                var frame = Frame((1, 0x56), (2, 0x83), (3, (byte)id));
                int start = 0x05, room = 65 - 0x05;
                if (id == 0)
                {
                    frame[0x05] = 0x01;
                    frame[0x09] = 0x80;
                    frame[0x0A] = 0x01;
                    frame[0x0C] = 0xC1;
                    frame[0x11] = 0xFF;
                    frame[0x12] = 0xFF;
                    frame[0x13] = 0xFF;
                    frame[0x14] = 0xFF;
                    start = 0x19;
                    room = 65 - 0x19;
                }
                int count = Math.Min(room, data.Length - at);
                Array.Copy(data, at, frame, start, count);
                at += count;
                frames.Add(frame);
            }
            frames.Add(Frame((1, 0x51), (2, 0x28), (5, 0xFF)));
            return frames;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
        {
            if (io == null || !io.WriteOutput(Control(true))) return null;
            wait(GapMs);
            return new Session();
        }

        internal sealed class Session : HidLightingSession
        {
            private byte[][] _crash = { Control(false) };

            public override bool Feature => false;

            /// <summary>The release, from the takeover on.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var frame in PictureFrames(CoolerMasterLighting.ByAddress(lights, colors, lights.Count)))
                {
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                    wait(GapMs);
                }
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
