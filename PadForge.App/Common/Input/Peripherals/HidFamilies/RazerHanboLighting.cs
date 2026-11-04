using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Razer Hanbo Chroma liquid cooler (0F35), lit as OpenRGB's
    /// RazerHanboController lights it (#557), on the collection of usage
    /// page FF00, usage 1, on interface 0 (RazerControllerDetect.cpp:483,
    /// read into <see cref="RazerHidTable"/>). openrazer has no driver for
    /// it.
    ///
    /// <para>The wire. The cooler declares no report ID, and every report
    /// is 65 bytes written with hid_write: byte 0 the ID 00 hidapi needs on
    /// Windows, byte 1 the command and 63 arguments (RazerHanboController.h:29-45,
    /// RazerHanboController.cpp:204-216 and 259-262). Each command but a
    /// color write is followed by one input report read with a 2 ms timeout
    /// (cpp:254-257). OpenRGB does not look at what it reads, and neither
    /// does PadForge.</para>
    ///
    /// <para>The direct mode. OpenRGB enters it as soon as it finds the
    /// cooler, and never again while its Direct mode stays chosen
    /// (cpp:34-35, RGBController_RazerHanbo.cpp:61 and 158-177): 82 01, 80
    /// 01 00 01, 80 01 01 01, 82 01, each with its read (SetDirectMode,
    /// cpp:73-105). A session sends them before its first picture. Each
    /// picture is SetZoneLeds for the pump and the three fans in turn
    /// (RGBController_RazerHanbo.cpp:135-151): command 32 for the pump and 40
    /// for a fan, then 01 07 00 00 00 00 and the zone number, then green,
    /// red and blue for each of the zone's LEDs, and a 2 ms wait after it,
    /// since a color write gets no answer (cpp:133-174). The lights are the
    /// razer_device zones: a pump of 16 and three fans of 18
    /// (RazerDevices.cpp:9759-9830). OpenRGB never sends the Direct mode's
    /// brightness (DeviceUpdateMode sends nothing for it), nor does
    /// PadForge.</para>
    ///
    /// <para>The hand-back is OpenRGB's Spectrum Cycle, the one effect of the
    /// cooler's own it drives: 82 01, 80 01, 80 01 01, each with its read
    /// (SetModeSpectrumCycle, cpp:112-131). No reference reads the cooler's
    /// own effect back, so a cooler whose own lighting was another effect
    /// shows the spectrum cycle after it. OpenRGB marks the controller as
    /// saving nothing on the cooler (@save :robot:,
    /// RGBController_RazerHanbo.cpp:19). The crash path sends the three
    /// reports with no reads.</para>
    ///
    /// <para>The cooler is Razer's, so the linker's Razer row reaches it and
    /// closes its path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).</para>
    /// </summary>
    internal sealed class RazerHanboLighting : IHidLightingFamily
    {
        public static readonly RazerHanboLighting Instance = new();

        internal const string Detector = "DetectRazerHanboControllers";

        /// <summary>The report's length with the dummy ID byte
        /// (RazerHanboController.h:41-45).</summary>
        internal const int ReportLength = 65;

        /// <summary>UsbReceive's timeout (RazerHanboController.cpp:254-257)
        /// and the wait after a color write (line 173).</summary>
        internal const int ReadMs = 2;
        internal const int ColorWaitMs = 2;

        /// <summary>The commands (RazerHanboController.cpp:75, 86, 135 and 141).</summary>
        internal const byte CommandMode = 0x82;
        internal const byte CommandEffect = 0x80;
        internal const byte CommandPump = 0x32;
        internal const byte CommandFan = 0x40;

        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer Hanbo";

        private readonly Dictionary<string, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null) return null;
            lock (_models)
            {
                if (!_models.TryGetValue(registration.Name, out var model))
                    _models[registration.Name] = model = Model(registration.Name, collection.ProductId);
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId)
        {
            var device = RazerHidTable.Device(productId);
            int[] zones = device == null ? Array.Empty<int>() : Array.ConvertAll(device.Zones, z => z.Count);
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = RazerHidTable.MapOf(productId),
                RepaintMs = RepaintMs,
                Begin = (io, wait) => io == null ? null : new Session(zones),
            };
        }

        /// <summary>razer_hanbo_create_report: byte 0 the dummy ID, byte 1
        /// the command, the arguments from byte 2 (cpp:204-216).</summary>
        internal static byte[] Report(byte command, ReadOnlySpan<byte> arguments)
        {
            var report = new byte[ReportLength];
            report[1] = command;
            arguments.CopyTo(report.AsSpan(2));
            return report;
        }

        /// <summary>SetDirectMode's four reports (cpp:73-105).</summary>
        internal static byte[][] DirectMode() => new[]
        {
            Report(CommandMode, stackalloc byte[] { 0x01 }),
            Report(CommandEffect, stackalloc byte[] { 0x01, 0x00, 0x01 }),
            Report(CommandEffect, stackalloc byte[] { 0x01, 0x01, 0x01 }),
            Report(CommandMode, stackalloc byte[] { 0x01 }),
        };

        /// <summary>SetModeSpectrumCycle's three reports (cpp:112-131).</summary>
        internal static byte[][] SpectrumCycle() => new[]
        {
            Report(CommandMode, stackalloc byte[] { 0x01 }),
            Report(CommandEffect, stackalloc byte[] { 0x01 }),
            Report(CommandEffect, stackalloc byte[] { 0x01, 0x01 }),
        };

        /// <summary>SetZoneLeds for one zone: 32 for the pump, 40 for a fan,
        /// the payload "0107000000000" and the zone number as hex pairs, then
        /// green, red and blue for each LED (cpp:133-174).</summary>
        internal static byte[] ZoneReport(int zone, ReadOnlySpan<int> colors)
        {
            var arguments = new byte[7 + 3 * colors.Length];
            arguments[0] = 0x01;
            arguments[1] = 0x07;
            arguments[6] = (byte)zone;
            for (int i = 0; i < colors.Length; i++)
            {
                arguments[7 + 3 * i] = (byte)(colors[i] >> 8);
                arguments[8 + 3 * i] = (byte)(colors[i] >> 16);
                arguments[9 + 3 * i] = (byte)colors[i];
            }
            return Report(zone > 0 ? CommandFan : CommandPump, arguments);
        }

        /// <summary>Writes each report and reads one input report after it
        /// with a 2 ms timeout, as UsbSend and UsbReceive pair up. False when
        /// a write failed.</summary>
        internal static bool Exchange(IHidReportChannel io, IEnumerable<byte[]> reports)
        {
            var answer = new byte[ReportLength];
            foreach (var report in reports)
            {
                if (!io.WriteOutput(report)) return false;
                io.ReadInput(answer, ReadMs);
            }
            return true;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly int[] _zones;
            private bool _direct;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(int[] zones) => _zones = zones;

            public override bool Feature => false;

            /// <summary>The spectrum cycle, once the direct mode may have gone
            /// out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct)
                {
                    _crash = SpectrumCycle();
                    if (!Exchange(io, DirectMode())) return HidLightingPaint.Failed;
                    _direct = true;
                }
                int at = 0;
                for (int zone = 0; zone < _zones.Length; zone++)
                {
                    var zoneColors = new int[_zones[zone]];
                    for (int i = 0; i < zoneColors.Length; i++, at++)
                        zoneColors[i] = colors != null && at < colors.Count ? colors[at] : 0;
                    if (!io.WriteOutput(ZoneReport(zone, zoneColors))) return HidLightingPaint.Failed;
                    wait?.Invoke(ColorWaitMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (!Exchange(io, _crash)) return false;
                _direct = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
