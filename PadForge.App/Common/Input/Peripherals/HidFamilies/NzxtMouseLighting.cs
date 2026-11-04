using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The NZXT Lift mouse (#557), lit as OpenRGB's NZXTMouseController
    /// lights it: 1E71:2100 on interface 0, usage page 0xFFCA, usage 1
    /// (NZXTMouseControllerDetect.cpp:20-41). Its six lights are OpenRGB's
    /// two zones of three, Left LED 0 to 2 and Right LED 0 to 2, in that
    /// order (RGBController_NZXTMouse.cpp:54-93).
    ///
    /// <para>The model of the device. OpenRGB's controller asks the mouse
    /// for its firmware version when it is made: the output report 43 81 00
    /// 01, 64 bytes with report ID 0x43, then input reports read until one
    /// starts 43 86 (SendFirmwareRequest, NZXTMouseController.cpp:16-26 and
    /// 114-144). A session asks the same way when it begins, and reads at
    /// most <see cref="AnswerReads"/> reports of <see cref="AnswerMs"/>
    /// each where OpenRGB waits with no bound. A mouse that does not answer
    /// gets no session, as OpenRGB makes no controller for it. A picture is
    /// OpenRGB's Direct mode, one output report of 64 bytes: 43 AE 00 10 02
    /// 3F, 06 at 24, then red, green and blue at 25, 29, 33, 37, 41 and 45
    /// for Left LED 2, Left LED 1, Left LED 0, Right LED 0, Right LED 1 and
    /// Right LED 2 (SetLEDs, lines 61-112). OpenRGB marks nothing as saving
    /// ("@save :x:", RGBController_NZXTMouse.cpp:18). SignalRGB's plugin
    /// opens the same interface and report ID (NZXT_Lift_Mouse.js:132-140
    /// and 197-199).</para>
    ///
    /// <para>Hand-back. No source reads the colors the mouse showed before,
    /// and OpenRGB sends nothing when it closes, so the mouse keeps
    /// PadForge's last picture. NZXT lights the mouse through NZXT CAM
    /// (<see cref="VendorPrograms.NzxtCam"/>), and PadForge leaves the mouse
    /// to it while it runs.</para>
    /// </summary>
    internal sealed class NzxtMouseLighting : IHidLightingFamily
    {
        public static readonly NzxtMouseLighting Instance = new();

        internal const string OpenRgbFamily = "NZXTMouseController";
        internal const ushort VendorId = 0x1E71;

        internal const int ReportSize = 64;
        internal const byte ReportId = 0x43;

        /// <summary>How many input reports a session reads for the firmware
        /// answer, and how long it waits for each. PadForge's bound: OpenRGB
        /// reads until the answer comes.</summary>
        internal const int AnswerReads = 8;
        internal const int AnswerMs = 250;

        /// <summary>Each light's red in the report, by its index in
        /// OpenRGB's color list (SetLEDs, NZXTMouseController.cpp:82-106).</summary>
        internal static readonly int[] RedAt = { 33, 29, 25, 37, 41, 45 };

        public string Name => "NZXT mice";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < ReportSize) return null;
            ushort pid = collection.ProductId;
            return _models.Get(registrations[0].Name + pid, () => Model(registrations[0].Name, pid));
        }

        /// <summary>Left LED 0 to 2 in zone 1, Right LED 0 to 2 in zone 2,
        /// each at its index in OpenRGB's color list.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new LightInfo[6];
            for (int i = 0; i < 6; i++) lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i / 3 + 1 };
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_NZXTMouse.cpp") { Product = $"nzxtmouse:{VendorId:X4}:{productId:X4}" },
            RepaintMs = AocMouseLighting.RepaintMs,
            Programs = VendorPrograms.Nzxt,
            Begin = (io, _) => io != null && AskFirmware(io) ? new Session() : null,
        };

        /// <summary>SendFirmwareRequest's request (lines 116-130).</summary>
        internal static byte[] FirmwareRequest()
        {
            var report = new byte[ReportSize];
            report[0x00] = ReportId;
            report[0x01] = 0x81;
            report[0x03] = 0x01;
            return report;
        }

        /// <summary>The request, then input reports until one starts 43 86
        /// (lines 132-138), within the bound. True when it came.</summary>
        internal static bool AskFirmware(IHidReportChannel io)
        {
            if (!io.WriteOutput(FirmwareRequest())) return false;
            var buffer = new byte[Math.Max(ReportSize, io.InputReportLength)];
            for (int i = 0; i < AnswerReads; i++)
            {
                int read = io.ReadInput(buffer, AnswerMs);
                if (read < 0) return false;
                if (read >= 2 && buffer[0] == ReportId && buffer[1] == 0x86) return true;
            }
            return false;
        }

        /// <summary>SetLEDs (lines 61-112), each light's color by its
        /// address.</summary>
        internal static byte[] Frame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byAddress = KeyboardLedNames.ByAddress(lights, colors, RedAt.Length);
            var report = new byte[ReportSize];
            report[0x00] = ReportId;
            report[0x01] = 0xAE;
            report[0x03] = 0x10;
            report[0x04] = 0x02;
            report[0x05] = 0x3F;
            report[24] = 0x06;
            for (int i = 0; i < RedAt.Length; i++)
            {
                report[RedAt[i]] = (byte)(byAddress[i] >> 16);
                report[RedAt[i] + 1] = (byte)(byAddress[i] >> 8);
                report[RedAt[i] + 2] = (byte)byAddress[i];
            }
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io.WriteOutput(Frame(lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
