using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The SayoDevice E1 knob (#557), lit as OpenRGB's SayoDeviceController
    /// lights it: 8089:0007 on usage page 0xFF11, usage 2
    /// (SayoDeviceControllerDetect.cpp:16-17 and 37), one underglow light
    /// (SetupZones, RGBController_SayoDevice.cpp:106-131).
    ///
    /// <para>The model of the device. A picture is OpenRGB's Direct mode:
    /// the lighting command 0x11 with the static mode at speed 3 and the
    /// light's color, in one 64-byte output report whose first byte, 0x21,
    /// is the report ID: 21 12, a checksum of 0x1221 plus the command's
    /// bytes taken as little-endian words, then the command (SetMode and
    /// SendPacket, SayoDeviceController.cpp:40-65 and 79-128;
    /// DeviceUpdateLEDs, RGBController_SayoDevice.cpp:133-140; SAYO_MODE_PACK,
    /// SayoDeviceController.h:66-69). Each report is followed by reading
    /// away every input report the device has waiting, as OpenRGB does
    /// (lines 115-127). The command sets the lighting without saving it:
    /// OpenRGB saves to the device's flash only through a separate report
    /// on a manual save (Save, lines 67-77; MODE_FLAG_MANUAL_SAVE,
    /// RGBController_SayoDevice.cpp:40 and 160-163), which PadForge never
    /// sends.</para>
    ///
    /// <para>Hand-back. No source reads the lighting the knob ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// knob keeps PadForge's last color until the lighting it saved is
    /// loaded again or another program sets one. No source names a program
    /// that lights it.</para>
    /// </summary>
    internal sealed class SayoDeviceLighting : IHidLightingFamily
    {
        public static readonly SayoDeviceLighting Instance = new();

        internal const string OpenRgbFamily = "SayoDeviceController";
        internal const ushort VendorId = 0x8089;

        internal const int PacketSize = 64;

        /// <summary>SAYO_CMD_LIGHTING_SET (SayoDeviceController.h:80).</summary>
        internal const byte LightingSet = 0x11;

        /// <summary>The Direct mode's byte: speed 3 less the mode's speed of
        /// 0, the static colors and the static mode
        /// (RGBController_SayoDevice.cpp:135-137; SayoDeviceController.h:24, 46-49, 60 and 66-69).</summary>
        internal const byte DirectModeByte = (3 & 0x3) << 6 | (0 & 0x3) << 4 | 0 & 0xF;

        /// <summary>How many waiting input reports a flush reads at most.
        /// OpenRGB reads until none is left (lines 121-126), and this bound
        /// keeps a device that never stops sending from holding the worker.</summary>
        internal const int FlushReads = 64;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "SayoDevice";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } },
                "OpenRGB RGBController_SayoDevice.cpp")
            {
                Product = $"sayo:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Begin = (_, _) => new Session(),
        };

        /// <summary>SendPacket's report for a command (lines 79-111).</summary>
        internal static byte[] Packet(ReadOnlySpan<byte> command)
        {
            var report = new byte[PacketSize];
            report[0] = 0x21;
            report[1] = 0x12;
            ushort checksum = 0x1221;
            for (int i = 0; i < command.Length; i += 2)
            {
                ushort word = command[i];
                if (i + 1 < command.Length) word |= (ushort)(command[i + 1] << 8);
                checksum += word;
            }
            report[2] = (byte)checksum;
            report[3] = (byte)(checksum >> 8);
            command.CopyTo(report.AsSpan(4));
            return report;
        }

        /// <summary>SetMode's command for the Direct mode and a color
        /// (lines 55-63).</summary>
        internal static byte[] DirectCommand(int color) => new byte[]
        {
            0x1C, 0x00, LightingSet, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x15, 0x00, 0x28, 0x00, 0x26, 0x00, 0x4C, 0x00,
            0x26, 0x00, 0x00, 0x00, DirectModeByte, 0x00, 0x80, 0x80,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color,
        };

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                int color = KeyboardLedNames.ByAddress(lights, colors, 1)[0];
                if (!io.WriteOutput(Packet(DirectCommand(color)))) return HidLightingPaint.Failed;
                var buffer = new byte[Math.Max(PacketSize, io.InputReportLength)];
                for (int reads = 0; reads < FlushReads && io.ReadInput(buffer, 0) > 0; reads++) { }
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
