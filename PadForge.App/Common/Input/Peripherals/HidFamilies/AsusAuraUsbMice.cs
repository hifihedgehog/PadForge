using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ASUS Aura mice (#557): the ROG Gladius, Chakram, Spatha X, Pugio,
    /// Strix Impact and Keris lines and the TUF M3 and M5 of OpenRGB's
    /// AsusAuraMouseController, and the first ROG mice, the Strix Evolve and
    /// the Spatha, of its AsusAuraMouseGen1Controller.
    ///
    /// <para>Mice. A mouse's zones, mode list and bounds come from
    /// aura_mouse_devices by product ID (<see cref="AsusAuraTables.Mice"/>),
    /// one light per zone, named by aura_mouse_zone_names
    /// (RGBController_AsusAuraMouse.cpp:14-21 and 193-216). A product ID the
    /// table lacks gets no zones, as std::map's operator[] gives it
    /// (line 48). A mouse with a direct mode of its own takes 51 29 frames,
    /// five LEDs to a frame from an offset, each zone's color on the LEDs
    /// aura_mouse_led_maps gives it (AsusAuraMouseController.cpp:221-264).
    /// Any other mouse is lit per zone through its static mode, as OpenRGB
    /// and ASUS light it (RGBController_AsusAuraMouse.cpp:56-89): 51 28 with
    /// the zone, the static mode's place in the mode list, the brightness at
    /// its maximum and the color, the input drained before it and its answer
    /// awaited for 10 ms (AsusAuraMouseController.cpp:141-219 and
    /// RGBController_AsusAuraMouse.cpp:238-251). The Gladius II Origin PNK LTD
    /// takes the long form of that frame (lines 173-194).</para>
    ///
    /// <para>First ROG mice. Feature report 0C. The Strix Evolve takes its
    /// Direct mode and full brightness at the keys 19 and 1A and its color at
    /// 1C to 1E, each by SendUpdate (RGBController_AsusROGStrixEvolve.cpp:109-130,
    /// AsusAuraMouseGen1Controller.cpp:101-125), which waits 15 ms after each.
    /// The Spatha takes all three colors in one feature report 10
    /// (lines 150-169). ResetToSavedLighting, 0C C4 and zeros (lines 171-181),
    /// returns both to the lighting stored in the mouse, and is their
    /// hand-back.</para>
    ///
    /// <para>Lights: the wheel is a zone, the logo a logo light, the
    /// underglow, the Spatha's side and the Strix Evolve's light are edge
    /// lights, and the Spatha X dock is another light.</para>
    /// </summary>
    internal sealed partial class AsusAuraUsbLighting
    {
        /// <summary>The mouse zones (AsusAuraMouseDevices.h:64-71).</summary>
        internal const byte MouseZoneLogo = 0, MouseZoneScroll = 1, MouseZoneUnderglow = 2, MouseZoneAll = 3,
            MouseZoneDock = 4;

        /// <summary>AURA_MOUSE_MODE_STATIC (AsusAuraMouseDevices.h:75).</summary>
        internal const byte MouseModeStatic = 0;

        /// <summary>The Gladius II Origin PNK LTD (AsusAuraMouseDevices.h:20).</summary>
        internal const int GladiusIIOriginPnk = 0x18CD;

        /// <summary>The LEDs of one direct frame (AsusAuraMouseController.cpp:241-246).</summary>
        internal const int MouseLedsPerFrame = 5;

        /// <summary>How long SendUpdate waits for its answer
        /// (AsusAuraMouseController.cpp:218).</summary>
        internal const int MouseAwaitMs = 10;

        /// <summary>The Gen 1 mice (AsusAuraMouseGen1ControllerDetect.cpp:24-26).</summary>
        internal const int StrixEvolvePid = 0x185B, SpathaWired = 0x181C, SpathaWireless = 0x1824;

        /// <summary>The Gen 1 feature report's length (AsusAuraMouseGen1Controller.cpp:107).</summary>
        internal const int Gen1Length = 9;

        /// <summary>The wait after each Gen 1 SendUpdate
        /// (AsusAuraMouseGen1Controller.cpp:123).</summary>
        internal const int Gen1SettleMs = 15;

        /// <summary>ASUS_ROG_STRIX_EVOLVE_MODE_DIRECT and its full brightness
        /// (RGBController_AsusROGStrixEvolve.h:19-28).</summary>
        internal const byte StrixEvolveDirect = 0x01, StrixEvolveBrightness = 0xFF;

        internal static LightInfo MouseLight(byte zone, int wheelNumber) => zone switch
        {
            MouseZoneLogo => Logo(zone),
            MouseZoneScroll => Zone(zone, wheelNumber),
            MouseZoneUnderglow => Edge(zone),
            _ => Other(zone),
        };

        /// <summary>A mouse's lights: one per zone, in its zone order, each
        /// at its zone number.</summary>
        internal static LightMap MouseMap(int productId)
        {
            var mouse = AsusAuraTables.Mouse(productId);
            var zones = mouse?.Zones ?? Array.Empty<byte>();
            var lights = new LightInfo[zones.Length];
            for (int i = 0; i < zones.Length; i++) lights[i] = MouseLight(zones[i], 1);
            return new LightMap(lights, "OpenRGB AsusAuraMouseDevices.h") { Product = ProductKey(productId) };
        }

        private HidLightingModel Mouse(string name, int productId)
        {
            var mouse = AsusAuraTables.Mouse(productId);
            return Model(name, productId, MouseMap(productId),
                (io, wait) => io == null ? null : new MouseSession(productId, mouse));
        }

        /// <summary>The place of the static mode in a mouse's mode list,
        /// the value OpenRGB gives its Static mode (RGBController_AsusAuraMouse.cpp:56-89).
        /// -1 where the list has none.</summary>
        internal static int StaticModeOf(AsusMouseDevice mouse)
            => mouse == null ? -1 : Array.IndexOf(mouse.Modes, MouseModeStatic);

        /// <summary>SendUpdate's frame (AsusAuraMouseController.cpp:141-219)
        /// for a zone in a mode with a color, direction 0, no random color,
        /// speed 0 and a brightness, as DeviceUpdateSingleLED sends it
        /// (RGBController_AsusAuraMouse.cpp:249).</summary>
        internal static byte[] MouseUpdateFrame(int productId, byte zone, byte mode, int rgb, byte brightness)
        {
            var frame = Frame(PacketSize, 0x00, 0x51, 0x28, zone, 0x00, mode, brightness);
            if (productId == GladiusIIOriginPnk)
                Put(frame, 0x08, rgb);
            else
                Put(frame, 0x07, rgb);
            return frame;
        }

        /// <summary>SendDirect's frames (AsusAuraMouseController.cpp:221-264):
        /// each zone's color, by its place among the mouse's zones, on the
        /// LEDs the map lists for that place, then the LEDs five to a frame
        /// with their offset.</summary>
        internal static List<byte[]> MouseDirectFrames(int productId, IReadOnlyList<int> zoneColors)
        {
            var map = AsusAuraTables.MouseLedMap(productId);
            int amount = map?.LedCount ?? 0;
            var leds = new int[amount];
            for (int zone = 0; zone < (zoneColors?.Count ?? 0); zone++)
                foreach (byte led in map?.LedsOf(zone) ?? Array.Empty<byte>())
                    if (led < amount) leds[led] = zoneColors[zone];
            var frames = new List<byte[]>();
            for (int led = 0; led < amount; led += MouseLedsPerFrame)
            {
                int count = Math.Min(MouseLedsPerFrame, amount - led);
                var frame = Frame(PacketSize, 0x00, 0x51, 0x29, (byte)count, 0x00, (byte)led);
                for (int c = 0; c < count; c++) Put(frame, 0x06 + c * 3, leds[led + c]);
                frames.Add(frame);
            }
            return frames;
        }

        internal sealed class MouseSession : HidLightingSession
        {
            private readonly int _productId;
            private readonly AsusMouseDevice _mouse;
            private byte[] _scratch;

            public MouseSession(int productId, AsusMouseDevice mouse)
            {
                _productId = productId;
                _mouse = mouse;
            }

            public override bool Feature => false;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (_mouse == null) return HidLightingPaint.Sent;
                if (_mouse.Direct)
                {
                    foreach (var frame in MouseDirectFrames(_productId, colors))
                        if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                    return HidLightingPaint.Sent;
                }
                int mode = StaticModeOf(_mouse);
                if (mode < 0) return HidLightingPaint.Sent;
                _scratch ??= new byte[Math.Max(PacketSize, io.InputReportLength)];
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    Clear(io, _scratch);
                    if (!io.WriteOutput(MouseUpdateFrame(_productId, (byte)lights[i].Address, (byte)mode, colors[i],
                            _mouse.BrightnessMax)))
                        return HidLightingPaint.Failed;
                    Await(io, _scratch, MouseAwaitMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  The first ROG mice
        // ─────────────────────────────────────────────

        /// <summary>SendUpdate (AsusAuraMouseGen1Controller.cpp:101-125).</summary>
        internal static byte[] Gen1UpdateFrame(byte key, byte value)
            => Frame(Gen1Length, 0x0C, 0xC4, 0x0F, 0x00, key, value);

        /// <summary>ResetToSavedLighting (AsusAuraMouseGen1Controller.cpp:171-181).</summary>
        internal static byte[] Gen1ResetFrame() => Frame(Gen1Length, 0x0C, 0xC4);

        /// <summary>SendDirectSpatha (AsusAuraMouseGen1Controller.cpp:150-169).</summary>
        internal static byte[] SpathaDirectFrame(IReadOnlyList<int> colors)
        {
            var frame = Frame(33, 0x10, 0x00, 0xF0, 0x00);
            for (int i = 0; i < 3; i++) Put(frame, 4 + i * 3, i < (colors?.Count ?? 0) ? colors[i] : 0);
            return frame;
        }

        private HidLightingModel StrixEvolve(string name, int productId)
            => Model(name, productId,
                new LightMap(new[] { Edge(0) }, "OpenRGB RGBController_AsusROGStrixEvolve.cpp") { Product = ProductKey(productId) },
                (io, wait) => BeginGen1(io, wait, spatha: false));

        /// <summary>The Spatha's zones, Side, Scroll Wheel and Logo
        /// (RGBController_AsusROGSpatha.cpp:127).</summary>
        private HidLightingModel Spatha(string name, int productId)
            => Model(name, productId,
                new LightMap(new[] { Edge(0), Zone(1, 1), Logo(2) }, "OpenRGB RGBController_AsusROGSpatha.cpp")
                {
                    Product = ProductKey(productId),
                },
                (io, wait) => BeginGen1(io, wait, spatha: true));

        /// <summary>One SendUpdate: the feature report, a read of its answer,
        /// and 15 ms.</summary>
        internal static bool Gen1Update(IHidReportChannel io, Action<int> wait, byte key, byte value)
        {
            if (!io.SetFeature(Gen1UpdateFrame(key, value))) return false;
            var answer = new byte[Math.Max(Gen1Length, io.FeatureReportLength)];
            io.GetFeature(0x0C, answer);
            wait(Gen1SettleMs);
            return true;
        }

        /// <summary>The Strix Evolve's Direct mode at full brightness
        /// (RGBController_AsusROGStrixEvolve.cpp:126-130 with the mode's
        /// default brightness, lines 37-45). The Spatha's Direct mode sends
        /// nothing (RGBController_AsusROGSpatha.cpp:192-197).</summary>
        private static HidLightingSession BeginGen1(IHidReportChannel io, Action<int> wait, bool spatha)
        {
            if (io == null) return null;
            if (!spatha && (!Gen1Update(io, wait, 0x19, StrixEvolveDirect)
                            || !Gen1Update(io, wait, 0x1A, StrixEvolveBrightness)))
                return null;
            return new Gen1Session(spatha);
        }

        internal sealed class Gen1Session : HidLightingSession
        {
            private readonly bool _spatha;
            private readonly byte[][] _crash = { Gen1ResetFrame() };

            public Gen1Session(bool spatha) => _spatha = spatha;

            public override bool Feature => true;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (_spatha)
                    return io.SetFeature(SpathaDirectFrame(colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                int rgb = colors[0];
                return Gen1Update(io, wait, 0x1C, (byte)(rgb >> 16)) && Gen1Update(io, wait, 0x1D, (byte)(rgb >> 8))
                       && Gen1Update(io, wait, 0x1E, (byte)rgb)
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => io.SetFeature(Gen1ResetFrame());
        }
    }
}
