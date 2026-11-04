using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's ViewSonicController family (#557): the ViewSonic XG270QG
    /// (0416:5020, interface 0, usage page 0xFF00, usage 1), the XG271QG
    /// (0543:A004, interface 0, usage page 1) and the XG270QC (0543:A002,
    /// interface 0, usage page 1, usage 0) (ViewSonicControllerDetect.cpp:19-23
    /// and 61-63).
    ///
    /// <para>The lights. Two, each OpenRGB's zone of one LED: "Base", the
    /// LEDs facing down, and "Rear", the LEDs facing back (SetupZones,
    /// RGBController_XG270QG.cpp:115-144 and RGBController_XG270QC.cpp:94-123,
    /// the facing VS_XG270QG_Controller.cpp:77-90), zones 1 and 2
    /// here.</para>
    ///
    /// <para>A picture. OpenRGB has no direct mode for these monitors
    /// ("@direct :x:"). Its per-LED mode is Custom on the XG270QG and XG271QG
    /// and Static on the XG270QC, mode 01 in both zones with each zone's
    /// color (DeviceUpdateMode, RGBController_XG270QG.cpp:161-187 and
    /// RGBController_XG270QC.cpp:140-167). The report is feature report 02:
    /// for each zone the mode, red, green, blue, 00 0A, then 00 and 01 at
    /// bytes 14 and 15, on the XG270QG and XG271QG the first 32 bytes of the
    /// controller's 64 (SendModeComplete, VS_XG270QG_Controller.cpp:57-94),
    /// on the XG270QC 167 bytes (VS_XG270QC_Controller.cpp:70-105). Both
    /// detectors open the collection the registration names, and a
    /// collection whose feature reports are shorter than the frame does not
    /// carry it, so PadForge lists only one that does.</para>
    ///
    /// <para>What the writes store. OpenRGB marks every mode of these
    /// monitors, the per-LED one included, as one the monitor saves
    /// (MODE_FLAG_AUTOMATIC_SAVE, RGBController_XG270QG.cpp:43-51 and
    /// RGBController_XG270QC.cpp:41-48), so each picture is written to the
    /// monitor's memory. A changed picture waits
    /// <see cref="LenovoLegionLighting.HoldMs"/> before it goes out and
    /// pictures go out at most every <see cref="LenovoLegionLighting.RepaintMs"/>,
    /// the bounds PadForge keeps for a keyboard that stores every
    /// write.</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the mode the
    /// monitor had, and none returns it. The monitor keeps PadForge's last
    /// colors in the Custom mode, across power cycles, until its own menu or
    /// ViewSonic's software sets another. No source names that software's
    /// process, so none is listed.</para>
    /// </summary>
    internal sealed class ViewSonicMonitorLighting : IHidLightingFamily
    {
        public static readonly ViewSonicMonitorLighting Instance = new();

        internal const string OpenRgbFamily = "ViewSonicController";

        /// <summary>The report ID, the header byte both controllers start
        /// with (VS_XG270QG_Controller.cpp:65, VS_XG270QC_Controller.cpp:79).</summary>
        internal const byte ReportId = 0x02;

        /// <summary>The lengths the two controllers send
        /// (VS_XG270QG_Controller.cpp:93, VS_XG270QC_Controller.cpp:104).</summary>
        internal const int QgLength = 0x20;
        internal const int QcLength = 167;

        /// <summary>VS_MODE_STATIC, the Custom and Static modes' value
        /// (VS_XG270QG_Controller.h:26, VS_XG270QC_Controller.h:24).</summary>
        internal const byte StaticMode = 0x01;

        /// <summary>The hold and the bound between two pictures of a device
        /// that stores every write (<see cref="LenovoLegionLighting.HoldMs"/>,
        /// <see cref="LenovoLegionLighting.RepaintMs"/>).</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        public string Name => "ViewSonic Monitor";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            bool qc = registration.Detector == "DetectViewSonicQC";
            if (collection.FeatureReportLength < (qc ? QcLength : QgLength)) return null;
            return _models.Get(registration.Name, () => Model(registration.Name, collection.VendorId, collection.ProductId, qc));
        }

        /// <summary>Base, then Rear, zones 1 and 2 at the LED values 0 and
        /// 1.</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
        };

        private static HidLightingModel Model(string name, ushort vendorId, ushort productId, bool qc) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), qc ? "OpenRGB RGBController_XG270QC.cpp" : "OpenRGB RGBController_XG270QG.cpp")
            {
                Product = $"viewsonic:{vendorId:X4}:{productId:X4}",
            },
            HoldMs = HoldMs,
            RepaintMs = RepaintMs,
            Begin = (io, _) => io == null ? null : new Session(qc),
        };

        /// <summary>SendModeComplete's report for the static mode with the
        /// base and rear colors.</summary>
        internal static byte[] Frame(bool qc, int baseRgb, int rearRgb)
        {
            // The XG270QG's array is 64 bytes, of which 32 go out. The
            // XG270QC's is 167.
            var data = new byte[qc ? QcLength : 64];
            data[0x00] = ReportId;
            data[0x01] = StaticMode;
            data[0x02] = (byte)(baseRgb >> 16);
            data[0x03] = (byte)(baseRgb >> 8);
            data[0x04] = (byte)baseRgb;
            data[0x05] = 0x00;
            data[0x06] = 0x0A;
            data[0x07] = 0x00;
            data[0x08] = StaticMode;
            data[0x09] = (byte)(rearRgb >> 16);
            data[0x0A] = (byte)(rearRgb >> 8);
            data[0x0B] = (byte)rearRgb;
            data[0x0C] = 0x00;
            data[0x0D] = 0x0A;
            data[0x0E] = 0x00;
            data[0x0F] = 0x01;
            if (qc) return data;
            var sent = new byte[QgLength];
            Array.Copy(data, sent, QgLength);
            return sent;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _qc;

            public Session(bool qc) => _qc = qc;

            public override bool Feature => true;

            /// <summary>Nothing undoes a picture: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var byLed = KeyboardLedNames.ByAddress(lights, colors, 2);
                return io.SetFeature(Frame(_qc, byLed[0], byLed[1])) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
