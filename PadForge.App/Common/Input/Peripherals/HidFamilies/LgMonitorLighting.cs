using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's LGMonitorController family (#557): the LG 27GN950-B
    /// (043E:9A8A) on interface 1, usage page 0xFF01, usage 1
    /// (LGMonitorControllerDetect.cpp:24-45). Its 48 LEDs ring the back of
    /// the monitor, one line, "Screen" (SetupZones,
    /// RGBController_LGMonitor.cpp:103-123), edge lights here.
    ///
    /// <para>The protocol. Every frame is a 65-byte output report, report ID
    /// 0 (LG_MONITOR_PACKET_SIZE, LGMonitorController.h:19), a command
    /// framed by "SC" (53 43) and "ED" (45 44) with an XOR checksum of the
    /// bytes between (crc, LGMonitorController.cpp:239-249). A picture is
    /// OpenRGB's Direct mode: the first one of a session turns the lighting
    /// on (CF 02 02 01) and selects the direct mode (CA 02 02 03 08), as
    /// OpenRGB's controller does once (SetDirect, TurnOn and EnableMode,
    /// lines 51-67 and 151-213), and each sends C1 02 91 00 with the 48
    /// colors, the checksum and "ED", 153 bytes split over three reports of
    /// 64 data bytes (lines 69-107). SignalRGB's plugin sends the same
    /// frames (signal-plugins LG_UltraGear_27GN950.js:51-57 and 96-127),
    /// mode 08 being LG's video sync mode there.</para>
    ///
    /// <para>Timing. OpenRGB's keep-alive sends the picture again once 500
    /// ms have passed since the last one while Direct is on
    /// (KeepaliveThread, RGBController_LGMonitor.cpp:146-157), so the model
    /// keeps the picture alive on that period
    /// (<see cref="HidLightingModel.KeepAliveMs"/>).</para>
    ///
    /// <para>What the writes store, and the hand-back. OpenRGB marks the
    /// modes it selects with the same CA command as ones the monitor saves
    /// (MODE_FLAG_AUTOMATIC_SAVE, lines 46-84), and Direct is selected with
    /// that command too, so the monitor may keep the direct mode after
    /// PadForge lets go. No reference reads the mode the monitor had, and
    /// none returns it, so nothing goes out at hand-back, and the monitor
    /// shows what its direct mode shows with no host feeding it until its
    /// own control or LG UltraGear Control Center selects another mode. No
    /// source names that program's process, so none is listed.</para>
    /// </summary>
    internal sealed class LgMonitorLighting : IHidLightingFamily
    {
        public static readonly LgMonitorLighting Instance = new();

        internal const string OpenRgbFamily = "LGMonitorController";

        /// <summary>LG_MONITOR_VID (LGMonitorControllerDetect.cpp:19).</summary>
        internal const ushort VendorId = 0x043E;

        /// <summary>LG_MONITOR_LEDS and LG_MONITOR_PACKET_SIZE
        /// (LGMonitorController.h:18-19).</summary>
        internal const int Leds = 48;
        internal const int PacketSize = 65;

        /// <summary>The command bytes (LGMonitorController.h:21-32).</summary>
        internal const byte Start1 = 0x53, Start2 = 0x43, End1 = 0x45, End2 = 0x44;
        internal const byte SetPowerState = 0xCF, SetMode = 0xCA, DirectCtl = 0xC1;
        internal const byte PowerOn = 0x01, ModeCtl = 0x03;

        /// <summary>LG_MONITOR_DIRECT_MODE_VALUE (LGMonitorController.h:37).</summary>
        internal const byte DirectMode = 0x08;

        /// <summary>How long after the last picture OpenRGB sends it again
        /// (RGBController_LGMonitor.cpp:150).</summary>
        internal const int KeepAliveMs = 500;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "LG Monitor";

        /// <summary>LG_27GN950_B_PID, and the 38GL950G, which SignalRGB's plugin
        /// lights with this family's frames and 48 colors
        /// (LG_UltraGear_38GL950G.js:60-65 and 76-94). OpenRGB does not list it.</summary>
        internal const ushort Ultragear27 = 0x9A8A;
        internal const ushort Ultragear38 = 0x9A57;

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            bool other = collection.ProductId == Ultragear38;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily,
                other && collection.UsagePage == 0xFF00 && OpenRgbRegistrations.InterfaceOf(collection, OpenRgbRegistrations.BusOf(collection)) == 0
                    ? collection with { ProductId = Ultragear27, InterfaceNumber = 1, UsagePage = 0xFF01 }
                    : collection);
            if (registrations.Count == 0) return null;
            string name = other ? "LG UltraGear 38GL950G" : registrations[0].Name;
            return _models.Get(name, () => Model(name, collection.ProductId));
        }

        /// <summary>The 48 LEDs in OpenRGB's order, each an edge light at its
        /// place.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[Leds];
            for (int i = 0; i < Leds; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_LGMonitor.cpp")
            {
                Product = $"lg:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            KeepAliveMs = KeepAliveMs,
            KeepAliveIdleMs = KeepAliveMs,
            Begin = (io, _) => io == null ? null : new Session(),
        };

        /// <summary>The XOR of <paramref name="data"/> from
        /// <paramref name="start"/> up to <paramref name="end"/>, not
        /// included (crc, lines 239-249).</summary>
        internal static byte Crc(byte[] data, int start, int end)
        {
            byte crc = 0;
            for (int i = start; i < end; i++) crc ^= data[i];
            return crc;
        }

        /// <summary>A two-byte command, as EnableMode and TurnOn build it
        /// (lines 157-213): SC, the command, 02 02, two value bytes, the
        /// checksum of bytes 1 to 7, ED.</summary>
        private static byte[] Command(byte command, byte value1, byte value2)
        {
            var buf = new byte[PacketSize];
            buf[1] = Start1;
            buf[2] = Start2;
            buf[3] = command;
            buf[4] = 0x02;
            buf[5] = 0x02;
            buf[6] = value1;
            buf[7] = value2;
            buf[8] = Crc(buf, 1, 8);
            buf[9] = End1;
            buf[10] = End2;
            return buf;
        }

        /// <summary>TurnOn(true) (lines 195-213): CF 02 02 01, byte 7 left
        /// zero.</summary>
        internal static byte[] TurnOnFrame() => Command(SetPowerState, PowerOn, 0x00);

        /// <summary>EnableMode(08) (lines 157-174).</summary>
        internal static byte[] DirectModeFrame() => Command(SetMode, ModeCtl, DirectMode);

        /// <summary>SetDirect's three reports for the 48 colors (lines
        /// 69-107).</summary>
        internal static byte[][] DirectFrames(IReadOnlyList<int> colors)
        {
            var data = new byte[192];
            int offset = 0;
            data[offset++] = Start1;
            data[offset++] = Start2;
            data[offset++] = DirectCtl;
            data[offset++] = 0x02;
            data[offset++] = 0x91;
            data[offset++] = 0x00;
            for (int i = 0; i < Leds; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                data[offset++] = (byte)(rgb >> 16);
                data[offset++] = (byte)(rgb >> 8);
                data[offset++] = (byte)rgb;
            }
            data[offset] = Crc(data, 0, offset);
            offset++;
            data[offset++] = End1;
            data[offset] = End2;
            var frames = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                frames[i] = new byte[PacketSize];
                Array.Copy(data, 64 * i, frames[i], 1, 64);
            }
            return frames;
        }

        internal sealed class Session : HidLightingSession
        {
            private bool _on;
            private bool _direct;

            public override bool Feature => false;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (!_on)
                {
                    if (!io.WriteOutput(TurnOnFrame())) return HidLightingPaint.Failed;
                    _on = true;
                }
                if (!_direct)
                {
                    if (!io.WriteOutput(DirectModeFrame())) return HidLightingPaint.Failed;
                    _direct = true;
                }
                foreach (var frame in DirectFrames(KeyboardLedNames.ByAddress(lights, colors, Leds)))
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
