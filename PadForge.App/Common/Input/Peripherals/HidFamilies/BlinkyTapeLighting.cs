using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Blinkinlabs' BlinkyTape LED strip (#557), lit over its USB serial
    /// port as OpenRGB's BlinkyTapeController lights it.
    ///
    /// <para>The look is OpenRGB's detector: every present port of a
    /// 1D50:605E device (BlinkyTapeControllerDetect.cpp:20-21 and 37,
    /// find_usb_serial_port_win.cpp:54-104), opened at 115200 baud
    /// (BlinkyTapeController.cpp:36-47).</para>
    ///
    /// <para>The lights. OpenRGB gives the strip one zone of 0 to 512 LEDs
    /// whose length the user sets, starting at 0 (RGBController_BlinkyTape.cpp:52-121),
    /// and the firmware drives up to 512 (MAX_LEDS, BlinkyTape_Arduino
    /// examples/ProductionSketch/BlinkyTape.h:7). So the model has one zone,
    /// and its color goes to all 512, which a strip of any length shows, as
    /// the Cooler Master Small ARGB family does for its header.</para>
    ///
    /// <para>The frames. A picture is red, green and blue of each LED, each
    /// held to 254, and the end byte 0xFF (SetLEDs, BlinkyTapeController.cpp:64-105).
    /// The firmware takes 0xFF as the end of a frame and the bytes between
    /// as colors (serialLoop, BlinkyTape_Arduino
    /// examples/ProductionSketch/SerialLoop.cpp:9-43). OpenRGB's Direct mode
    /// saves nothing ("@save :x:", RGBController_BlinkyTape.cpp:18).</para>
    ///
    /// <para>Hand-back. None is possible. Once a byte comes in, the
    /// firmware leaves its pattern for its serial loop and never returns
    /// from it (ProductionSketch.ino:183-190, SerialLoop.cpp:16-42), and
    /// OpenRGB sends nothing when it closes. The strip shows PadForge's last
    /// picture until it is unplugged. No source names a Blinkinlabs program
    /// that holds the port, so the model names none.</para>
    /// </summary>
    internal sealed class BlinkyTapeLighting : IOwnLookFamily
    {
        internal const ushort VendorId = 0x1D50;
        internal const ushort ProductId = 0x605E;
        internal const int Baud = 115200;

        /// <summary>The longest strip OpenRGB and the firmware take
        /// (RGBController_BlinkyTape.cpp:75, BlinkyTape.h:7).</summary>
        internal const int MaxLeds = 512;

        /// <summary>The highest channel value, since 0xFF ends a frame
        /// (BlinkyTapeController.cpp:96-98).</summary>
        internal const byte MaxValue = 254;
        internal const byte End = 0xFF;

        /// <summary>A picture is 1,537 bytes and no reference bounds the
        /// rate, so the bound is PadForge's, the one the Dygma Raise keeps
        /// for its serial line.</summary>
        internal const int RepaintMs = 100;

        private readonly Func<IReadOnlyList<ComPort>> _ports;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, ISerialLineChannel> _open;

        public BlinkyTapeLighting()
            : this(null, null, null) { }

        /// <summary>Test seam: the port list, the container lookup and the
        /// opener.</summary>
        internal BlinkyTapeLighting(Func<IReadOnlyList<ComPort>> ports, Func<string, Guid> containerOf,
            Func<string, ISerialLineChannel> open)
        {
            _ports = ports ?? SerialControllers.ListComPorts;
            _containerOf = containerOf ?? OwnLightingChannels.ContainerOfInstance;
            _open = open ?? (path => OwnLightingChannels.OpenSerial(path, Baud));
            Model = new HidLightingModel
            {
                Family = this,
                Name = "BlinkyTape",
                Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                    "OpenRGB RGBController_BlinkyTape.cpp") { Product = $"blinkinlabs:{VendorId:X4}:{ProductId:X4}" },
                RepaintMs = RepaintMs,
                Open = key => _open(key),
                BeginOwn = (channel, _) => channel is ISerialLineChannel port
                    ? new SerialFramesSession(port, 1, colors => Frame(colors[0]))
                    : null,
                // The session names no crash frames, so the crash path never
                // writes: see the class comment.
                WriteOnce = (_, _, _) => false,
            };
        }

        public static readonly BlinkyTapeLighting Instance = new();

        public string Name => "BlinkyTape";

        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        public IReadOnlyList<OwnDevice> Look()
        {
            IReadOnlyList<ComPort> ports;
            try { ports = _ports(); }
            catch { return null; }
            return ports == null ? null : LightReports.SerialPorts(ports, _containerOf, VendorId, ProductId, _ => Model);
        }

        /// <summary>SetLEDs' bytes with one color on every LED.</summary>
        internal static byte[] Frame(int rgb)
        {
            var frame = new byte[MaxLeds * 3 + 1];
            byte r = Math.Min(LightReports.Red(rgb), MaxValue);
            byte g = Math.Min(LightReports.Green(rgb), MaxValue);
            byte b = Math.Min(LightReports.Blue(rgb), MaxValue);
            for (int i = 0; i < MaxLeds; i++)
            {
                frame[i * 3] = r;
                frame[i * 3 + 1] = g;
                frame[i * 3 + 2] = b;
            }
            frame[^1] = End;
            return frame;
        }
    }
}
