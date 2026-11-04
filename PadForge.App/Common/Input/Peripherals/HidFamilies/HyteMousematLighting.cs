using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The HYTE CNVS mousepad (#557), lit over its USB serial port as
    /// OpenRGB's HYTEMousematController lights it on Windows.
    ///
    /// <para>The look is OpenRGB's two detectors: every present port of a
    /// 3402:0B00 or 3402:0B01 device, the pad's two hardware versions
    /// (HYTEMousematControllerDetect_Windows_MacOS.cpp:18-21 and 35-36),
    /// opened at 2,000,000 baud, which a CDC ACM port ignores
    /// (HYTEMousematController_Windows_MacOS.cpp:20-24).</para>
    ///
    /// <para>The lights are the pad's 50 edge LEDs in OpenRGB's order
    /// (RGBController_HYTEMousemat.cpp:61-84).</para>
    ///
    /// <para>The frames. OpenRGB's Direct mode first turns the firmware's
    /// animation off, FF DC 05 00 (FirmwareAnimationControl(false),
    /// HYTEMousematController_Windows_MacOS.cpp:42-63, from DeviceUpdateMode,
    /// RGBController_HYTEMousemat.cpp:101-107),
    /// which the session sends once before its first picture. A picture is
    /// the 157-byte streaming command, FF EE 02 01 00 32 00 and then green,
    /// red and blue of each LED, each at 72 percent of its value in whole
    /// numbers (StreamingCommand, HYTEMousematController_Windows_MacOS.cpp:65-100).
    /// The Direct mode saves nothing ("@save :x:", RGBController_HYTEMousemat.cpp:18).</para>
    ///
    /// <para>Hand-back. The session turns the firmware's animation back on,
    /// FF DC 05 01, the command OpenRGB's Rainbow Wave mode would send
    /// (HYTE_CNVS_MODE_RAINBOW, RGBController_HYTEMousemat.cpp:108-111),
    /// and that is also its crash frame. OpenRGB leaves that mode out
    /// because the pad does not seem to return to its animation once the
    /// streaming command has been used (lines 42-49), so the pad may keep
    /// PadForge's last picture until HYTE Nexus, which the model names, or a
    /// replug sets its lighting again.</para>
    /// </summary>
    internal sealed class HyteMousematLighting : IOwnLookFamily
    {
        internal const ushort VendorId = 0x3402;
        internal const ushort Version1ProductId = 0x0B00;
        internal const ushort Version2ProductId = 0x0B01;

        /// <summary>The baud OpenRGB opens the port at (line 24).</summary>
        internal const int Baud = 2000000;

        internal const int LedCount = 50;
        internal const int StreamLength = 157;

        /// <summary>max_brightness, a percentage (line 68).</summary>
        internal const int MaxBrightness = 72;

        /// <summary>No reference bounds the rate, so the bound is PadForge's,
        /// the one the other families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        internal static readonly byte[] AnimationOff = { 0xFF, 0xDC, 0x05, 0x00 };
        internal static readonly byte[] AnimationOn = { 0xFF, 0xDC, 0x05, 0x01 };

        private readonly Func<IReadOnlyList<ComPort>> _ports;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, ISerialLineChannel> _open;
        private readonly Func<string, byte[], int, bool> _writeOnce;

        public HyteMousematLighting()
            : this(null, null, null, null) { }

        /// <summary>Test seam: the port list, the container lookup, the
        /// opener and the crash path's one write.</summary>
        internal HyteMousematLighting(Func<IReadOnlyList<ComPort>> ports, Func<string, Guid> containerOf,
            Func<string, ISerialLineChannel> open, Func<string, byte[], int, bool> writeOnce)
        {
            _ports = ports ?? SerialControllers.ListComPorts;
            _containerOf = containerOf ?? OwnLightingChannels.ContainerOfInstance;
            _open = open ?? (path => OwnLightingChannels.OpenSerial(path, Baud));
            _writeOnce = writeOnce ?? ((path, bytes, timeoutMs) => OwnLightingChannels.SerialWriteOnce(path, Baud, bytes, timeoutMs));
            Model = new HidLightingModel
            {
                Family = this,
                Name = "HYTE CNVS",
                Map = new LightMap(Lights(), "OpenRGB RGBController_HYTEMousemat.cpp") { Product = $"hyte:{VendorId:X4}:cnvs" },
                Programs = new[] { VendorPrograms.HyteNexus },
                RepaintMs = RepaintMs,
                Open = key => _open(key),
                BeginOwn = (channel, _) => channel is ISerialLineChannel port
                    ? new SerialFramesSession(port, LedCount, Stream, opening: AnimationOff, handBack: AnimationOn)
                    : null,
                WriteOnce = (key, frame, timeoutMs) => _writeOnce(key, frame, timeoutMs),
            };
        }

        public static readonly HyteMousematLighting Instance = new();

        public string Name => "HYTE CNVS";

        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[LedCount];
            for (int i = 0; i < LedCount; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return lights;
        }

        public IReadOnlyList<OwnDevice> Look()
        {
            IReadOnlyList<ComPort> ports;
            try { ports = _ports(); }
            catch { return null; }
            if (ports == null) return null;
            var found = LightReports.SerialPorts(ports, _containerOf, VendorId, Version1ProductId, _ => Model);
            found.AddRange(LightReports.SerialPorts(ports, _containerOf, VendorId, Version2ProductId, _ => Model));
            return found;
        }

        /// <summary>StreamingCommand's bytes for the colors by LED.</summary>
        internal static byte[] Stream(int[] colors)
        {
            var stream = LightReports.Report(StreamLength, 0xFF, 0xEE, 0x02, 0x01, 0x00, 0x32, 0x00);
            for (int i = 0; i < LedCount; i++)
            {
                int rgb = colors != null && i < colors.Length ? colors[i] : 0;
                stream[7 + i * 3] = (byte)(MaxBrightness * LightReports.Green(rgb) / 100);
                stream[8 + i * 3] = (byte)(MaxBrightness * LightReports.Red(rgb) / 100);
                stream[9 + i * 3] = (byte)(MaxBrightness * LightReports.Blue(rgb) / 100);
            }
            return stream;
        }
    }
}
