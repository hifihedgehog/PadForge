using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The MSI Mystic Light keyboards OpenRGB's MSIKeyboardController lights
    /// (#557): the MS-1565 keyboard with its four zones, and the MS-1562,
    /// MS-1563 and MS-1564 keyboards OpenRGB lights as one light (the GL66
    /// and its kin, RGBController_MSIMysticLight64.cpp:16-23). The
    /// collections are OpenRGB's registrations
    /// (MSIKeyboard1565ControllerDetect.cpp:34,
    /// MSIMysticLigh64ControllerDetect.cpp:37-39), each with 64-byte feature
    /// reports.
    ///
    /// <para>The colors go out as OpenRGB's Static mode sends them, the one
    /// mode of either controller that holds a color still. Neither controller
    /// has a mode that streams. OpenRGB marks both as saving every write on
    /// their own ("@save :robot:", RGBController_MSIMysticLightKB.cpp:18 and
    /// RGBController_MSIMysticLight64.cpp:20, the legend's "Feature is
    /// automatic and can not be turned off"), so every picture lands in the
    /// keyboard's memory. A changed picture goes out once it has held for
    /// <see cref="HoldMs"/>, and no sooner than <see cref="RepaintMs"/> after
    /// the one before, as the Lenovo family writes its stored pictures
    /// (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back. Nothing goes out. No reference reads the mode or the
    /// colors back: both controllers only send (MSIKeyboard1565Controller.cpp:34-65,
    /// MSIMysticLight64Controller.cpp:31-58). The keyboard keeps PadForge's
    /// last picture until MSI's program or the user sets another.</para>
    ///
    /// <para>MSI's program. Mystic Light lights these keyboards, and while
    /// its service runs the keyboard is left to it
    /// (<see cref="VendorPrograms.MsiMysticLight"/>).</para>
    /// </summary>
    internal sealed class MsiKeyboardLighting : IHidLightingFamily
    {
        public static readonly MsiKeyboardLighting Instance = new();

        /// <summary>The OpenRGB family the registrations come from.</summary>
        internal const string OpenRgbFamily = "MSIKeyboardController";

        /// <summary>The detector of the MS-1565 keyboard
        /// (MSIKeyboard1565ControllerDetect.cpp:16).</summary>
        internal const string Detector1565 = "DetectMSIKeyboardController";

        /// <summary>The detector of the 64-byte keyboards
        /// (MSIMysticLigh64ControllerDetect.cpp:19).</summary>
        internal const string Detector64 = "DetectMSIMysticLight64Controllers";

        /// <summary>The length of every frame, the report ID included: the
        /// 64-byte buffers both controllers send
        /// (MSIKeyboard1565Controller.cpp:44, MSIKeyboard1565Controller.h:34-50,
        /// MSIMysticLight64Controller.h:63-73).</summary>
        internal const int PacketSize = 64;

        /// <summary>The report ID of every frame
        /// (MSIKeyboard1565Controller.cpp:45, MSIKeyboard1565Controller.h:37,
        /// MSIMysticLight64Controller.h:65).</summary>
        internal const byte ReportId = 0x02;

        /// <summary>How long a changed picture holds before it goes out, and
        /// the shortest time between two pictures, the bounds the Lenovo
        /// family keeps for a keyboard that stores every write
        /// (<see cref="LenovoLegionLighting.HoldMs"/>,
        /// <see cref="LenovoLegionLighting.RepaintMs"/>). No reference bounds
        /// these keyboards' writes, so the bounds are PadForge's.</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "MSI Keyboard";

        private static readonly Lazy<HidLightingModel> s_1565 = new(() => Model(
            "MSI Keyboard MS_1565", 0x1601, Lights1565(), (_, _) => new Session1565()));

        private static readonly Dictionary<int, HidLightingModel> s_64 = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < PacketSize) return null;
            foreach (var registration in OpenRgbRegistrations.Match(OpenRgbFamily, collection))
            {
                if (registration.Detector == Detector1565) return s_1565.Value;
                if (registration.Detector == Detector64)
                {
                    lock (s_64)
                    {
                        if (!s_64.TryGetValue(collection.ProductId, out var model))
                            s_64[collection.ProductId] = model = Model(registration.Name, collection.ProductId,
                                Lights64(), (_, _) => new Session64());
                        return model;
                    }
                }
            }
            return null;
        }

        private static HidLightingModel Model(string name, ushort productId, IReadOnlyList<LightInfo> lights,
            Func<IHidReportChannel, Action<int>, HidLightingSession> begin)
            => new()
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(lights, "OpenRGB MSIKeyboardController") { Product = $"msi:1462:{productId:X4}" },
                Programs = new[] { VendorPrograms.MsiMysticLight },
                HoldMs = HoldMs,
                RepaintMs = RepaintMs,
                Begin = begin,
            };

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The MS-1565's four zones, MS_1565_ZONE_1 to 4, each a
        /// light whose address is its zone (MSIKeyboard1565Controller.h:67-74).
        /// OpenRGB sets them through its "Static zone 1" to "zone 4" modes
        /// (RGBController_MSIMysticLightKB.cpp:181-193).</summary>
        internal static IReadOnlyList<LightInfo> Lights1565()
        {
            var lights = new LightInfo[4];
            for (int zone = 1; zone <= 4; zone++)
                lights[zone - 1] = new LightInfo(zone, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };
            return lights;
        }

        /// <summary>The 64-byte keyboards' one light, "MSI LED" in the zone
        /// "MSI Zone" (RGBController_MSIMysticLight64.cpp:46-61).</summary>
        internal static IReadOnlyList<LightInfo> Lights64()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        // ─────────────────────────────────────────────
        //  The MS-1565 frames
        // ─────────────────────────────────────────────

        /// <summary>The zone bits of MS_1565_ZONE_1 to 4 and of the whole
        /// device (MSIKeyboard1565Controller.cpp:14-21).</summary>
        internal static byte ZoneBits(int zone) => zone switch
        {
            1 => 1,
            2 => 2,
            3 => 4,
            4 => 8,
            _ => 15,
        };

        /// <summary>The whole device, MS_1565_ZONE_DEVICE
        /// (MSIKeyboard1565Controller.h:73).</summary>
        internal const int DeviceZone = 5;

        /// <summary>MS_1565_STEADY (MSIKeyboard1565Controller.h:55).</summary>
        internal const byte Steady = 0x01;

        /// <summary>The frame that picks the zones the next frame sets
        /// (SetMode, MSIKeyboard1565Controller.cpp:44-48).</summary>
        internal static byte[] ZoneSelect(int zone)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = 0x01;
            frame[2] = ZoneBits(zone);
            return frame;
        }

        /// <summary>FeaturePacket_MS1565 for the Static mode of one color
        /// (MSIKeyboard1565Controller.h:35-50, MSIKeyboard1565Controller.cpp:50-63),
        /// as DeviceUpdateLEDs fills it (RGBController_MSIMysticLightKB.cpp:55-92):
        /// the speed is 1200 less 9 times the mode's speed, which the Static
        /// mode leaves at 0 (RGBControllerInterface.h:339), low byte first,
        /// the direction 0, and two key frames, the color at time 0 and again
        /// at time 100, at the brightness of 100 SetupMode gives every mode
        /// (line 170).</summary>
        internal static byte[] SteadyFrame(int rgb)
        {
            const int speed = 1200;
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = 0x02;
            frame[2] = Steady;
            frame[3] = speed & 0xFF;
            frame[4] = speed >> 8;
            frame[7] = 0x0F;
            frame[8] = 0x01;
            frame[9] = 0x00;
            WriteKeyFrame(frame, 10, 0, rgb);
            WriteKeyFrame(frame, 14, 100, rgb);
            return frame;
        }

        private static void WriteKeyFrame(byte[] frame, int at, byte time, int rgb)
        {
            frame[at] = time;
            frame[at + 1] = (byte)(rgb >> 16);
            frame[at + 2] = (byte)(rgb >> 8);
            frame[at + 3] = (byte)rgb;
        }

        /// <summary>The frames of a picture: the whole device in one color
        /// when every zone has the same one, as OpenRGB's "Static" mode sets
        /// it, else each zone in turn, as its "Static zone" modes do.</summary>
        internal static List<byte[]> Frames1565(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            if (count == 0) return frames;
            bool same = true;
            for (int n = 1; n < count; n++)
                if ((colors[n] & 0xFFFFFF) != (colors[0] & 0xFFFFFF)) same = false;
            if (same && count == 4)
            {
                frames.Add(ZoneSelect(DeviceZone));
                frames.Add(SteadyFrame(colors[0]));
                return frames;
            }
            for (int n = 0; n < count; n++)
            {
                int zone = lights[n].Address;
                if (zone < 1 || zone > 4) continue;
                frames.Add(ZoneSelect(zone));
                frames.Add(SteadyFrame(colors[n]));
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  The 64-byte frames
        // ─────────────────────────────────────────────

        /// <summary>MSI_64_STEADY and MSI_64_BRIGHTNESS_LEVEL_100
        /// (MSIMysticLight64Controller.h:21-45).</summary>
        internal const byte Steady64 = 0x01;
        internal const byte Brightness100 = 10;

        /// <summary>The colors SetupMode puts in every mode with its own
        /// colors, the Static mode included, after which the user's color
        /// takes the first place (RGBController_MSIMysticLight64.cpp:149-155).
        /// RGBColor is 0x00BBGGRR (RGBControllerInterface.h), so these are
        /// red, orange, yellow, green, blue, purple and magenta as
        /// 0xRRGGBB.</summary>
        internal static readonly int[] ModeColors = { 0xFF0000, 0xFF5000, 0xFFFF00, 0x00FF00, 0x0000FF, 0x9600FF, 0xFF00FF };

        /// <summary>FeaturePacket_64 for the Static mode
        /// (MSIMysticLight64Controller.h:63-73, MSIMysticLight64Controller.cpp:31-58),
        /// as DeviceUpdateLEDs fills it (RGBController_MSIMysticLight64.cpp:68-90):
        /// the speed the Static mode leaves at 0 (it has no speed,
        /// RGBControllerInterface.h:339), the brightness of 100, and the
        /// mode's seven colors, the first one the light's.</summary>
        internal static byte[] SteadyFrame64(int rgb)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = 0x00;
            frame[2] = Steady64;
            frame[3] = 0x00;
            frame[4] = Brightness100;
            frame[5] = (byte)ModeColors.Length;
            for (int i = 0; i < ModeColors.Length; i++)
            {
                int color = i == 0 ? rgb : ModeColors[i];
                frame[6 + 3 * i] = (byte)(color >> 16);
                frame[7 + 3 * i] = (byte)(color >> 8);
                frame[8 + 3 * i] = (byte)color;
            }
            return frame;
        }

        // ─────────────────────────────────────────────
        //  The sessions
        // ─────────────────────────────────────────────

        internal sealed class Session1565 : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Frames1565(lights, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        internal sealed class Session64 : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                return io.SetFeature(SteadyFrame64(colors[0])) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
