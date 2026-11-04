using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Elgato Stream Deck MK.2 (#557), lit as OpenRGB's
    /// ElgatoStreamDeckController lights it: 0FD9:0080, on its interface 0
    /// (ElgatoStreamDeckControllerDetect.cpp:16-17, 23 and 39). Each of its
    /// 15 keys is a light, in OpenRGB's 3 by 5 matrix, key 1 at the top
    /// left (RGBController_ElgatoStreamDeck.cpp:51-73).
    ///
    /// <para>The frames. The keys are screens, so OpenRGB draws each key's
    /// color as a 72 by 72 image, encodes it as a JPEG at quality 95 with
    /// stb_image_write (CreateButtonImage, lines 75-99, ported in
    /// <see cref="StreamDeckJpeg"/>), and sends every key's image in its
    /// own 1024-byte output report: 02 07, the key's index, 01 for the
    /// last page, the image's length in two bytes, low first, page 0 in
    /// two bytes, and the image (SendFullFrame and SendButtonImage,
    /// ElgatoStreamDeckController.cpp:49-79). python-elgato-streamdeck
    /// writes the same header for the MK.2 (StreamDeckOriginalV2.py:22-33
    /// and 114-142), and an image of one color fits one page. A picture
    /// sends all 15 keys, as DeviceUpdateLEDs does (lines 101-109). The
    /// keys keep nothing ("@save :x:", line 19).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes and names no
    /// way back to the keys' own images, so the keys show PadForge's last
    /// colors until the Stream Deck app draws them again, which it does
    /// when it starts. The model names that app, so PadForge lets go of
    /// the device while it runs.</para>
    /// </summary>
    internal sealed class StreamDeckLighting : IHidLightingFamily
    {
        public static readonly StreamDeckLighting Instance = new();

        internal const string OpenRgbFamily = "StreamDeckController";

        /// <summary>The report and image sizes (ElgatoStreamDeckController.cpp:62-63,
        /// RGBController_ElgatoStreamDeck.cpp:77-78 and 96).</summary>
        internal const int PacketSize = 1024;
        internal const int HeaderSize = 8;
        internal const int KeyCount = 15;
        internal const int Columns = 5;
        internal const int ImageSize = 72;
        internal const int Quality = 95;

        /// <summary>A picture is 15 reports of 1024 bytes and no reference
        /// bounds the rate, so the bound is PadForge's.</summary>
        internal const int RepaintMs = 100;

        public string Name => "Elgato Stream Deck";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            // The detector lights interface 0 only (line 23).
            if (OpenRgbRegistrations.InterfaceOf(collection, OpenRgbRegistrations.BusOf(collection)) != 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            var r = registrations[0];
            return _models.Get(r.Name, () => Model(r.Name, (ushort)r.VendorId, (ushort)r.ProductId));
        }

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[KeyCount];
            for (int i = 0; i < KeyCount; i++)
                lights[i] = new LightInfo(i, LightKind.Other, 0, LightKeySource.None)
                {
                    X = i % Columns, Y = i / Columns, Width = 1, Height = 1, HasPosition = true,
                };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_ElgatoStreamDeck.cpp") { Product = $"elgato:{vendorId:X4}:{productId:X4}" },
            Programs = new[] { VendorPrograms.ElgatoStreamDeck },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new ReportsSession(false, KeyCount, Frames),
        };

        /// <summary>CreateButtonImage: a key's color as OpenRGB's JPEG.</summary>
        internal static byte[] Image(int rgb)
        {
            var pixels = new byte[ImageSize * ImageSize * 3];
            for (int i = 0; i < ImageSize * ImageSize; i++)
            {
                pixels[i * 3] = LightReports.Red(rgb);
                pixels[i * 3 + 1] = LightReports.Green(rgb);
                pixels[i * 3 + 2] = LightReports.Blue(rgb);
            }
            return StreamDeckJpeg.Write(ImageSize, ImageSize, pixels, Quality);
        }

        /// <summary>SendButtonImage's report for one key.</summary>
        internal static byte[] KeyReport(int key, int rgb)
        {
            var jpeg = Image(rgb);
            var report = new byte[PacketSize];
            report[0] = 0x02;
            report[1] = 0x07;
            report[2] = (byte)key;
            report[3] = 0x01;
            report[4] = (byte)jpeg.Length;
            report[5] = (byte)(jpeg.Length >> 8);
            Array.Copy(jpeg, 0, report, HeaderSize, Math.Min(jpeg.Length, PacketSize - HeaderSize));
            return report;
        }

        /// <summary>SendFullFrame's reports, every key in order.</summary>
        internal static IReadOnlyList<byte[]> Frames(int[] colors)
        {
            var frames = new byte[KeyCount][];
            for (int key = 0; key < KeyCount; key++) frames[key] = KeyReport(key, key < colors.Length ? colors[key] : 0);
            return frames;
        }
    }
}
