using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lighting controller of Dell G series laptops (187C:0550 and
    /// 187C:0551) that OpenRGB's AlienwareController lights (#557). The
    /// collections are OpenRGB's registrations
    /// (AlienwareControllerDetect.cpp:43-44) with feature reports of
    /// <see cref="ReportSize"/> bytes.
    ///
    /// <para>The lights are the controller's zones. A session asks the
    /// controller for its configuration first and reads the platform and
    /// the zone count from it, the count from a table for platforms that
    /// report a wrong one, and names and numbers the zones from OpenRGB's
    /// tables (the constructor, AlienwareController.cpp:95-194, and the
    /// tables at lines 30-66). Zones named "Light Bar" are edge lights. The
    /// model's map, until a session reads the controller, is the four zones
    /// of the G5 SE and the G15s.</para>
    ///
    /// <para>A picture goes out as OpenRGB's Static mode sends it: a new
    /// animation in the slot that is never saved (0xFFFF,
    /// ALIENWARE_COMMAND_USER_ANIM_KEYBOARD, AlienwareController.h:74), then
    /// for each zone its selection and a color action, then "finish and
    /// play" (UpdateMode, AlienwareController.cpp:782-907). The first
    /// picture of a session sets the zones' dim level to 0 first, as
    /// OpenRGB does once when it finds the controller (UpdateDim, lines
    /// 714-745, called from RGBController_Alienware.cpp:128). Each report
    /// is followed by a wait of 60 ms and a read of the controller's answer,
    /// which is checked as OpenRGB checks it (SendHIDReport, lines 68-93,
    /// and lines 288-523). SendHIDReport means to wait 1 s after "finish and
    /// play", but it looks for that step in byte 3, which holds the step's
    /// high byte, 0 (UserAnimation, lines 362-363), so OpenRGB waits 60 ms
    /// there too, and so does PadForge. A picture of four zones takes ten
    /// exchanges, 600 ms of waits, so the controller sets the pace.</para>
    ///
    /// <para>Hand-back. Nothing goes out. The animation slot PadForge plays
    /// is the one the controller does not save, and no reference returns
    /// the controller to its saved animation. The keyboard shows PadForge's
    /// last picture until Alienware Command Center sets another or the
    /// laptop restarts.</para>
    ///
    /// <para>Alienware Command Center lights these laptops, and while it
    /// runs the controller is left to it
    /// (<see cref="VendorPrograms.AlienwareCommandCenter"/>).</para>
    /// </summary>
    internal sealed partial class AlienwareLighting : IHidLightingFamily
    {
        public static readonly AlienwareLighting Instance = new();

        internal const string OpenRgbFamily = "AlienwareController";

        /// <summary>HIDAPI_ALIENWARE_REPORT_SIZE, the report with its ID
        /// (AlienwareController.h:22-23).</summary>
        internal const int ReportSize = 34;

        /// <summary>ALIENWARE_MAX_ZONES_PER_PACKET (AlienwareController.h:32).</summary>
        internal const int MaxZonesPerReport = 25;

        /// <summary>The commands (AlienwareController.h:33-45).</summary>
        internal const byte CommandReport = 0x20;
        internal const byte CommandUserAnimation = 0x21;
        internal const byte CommandSelectZones = 0x23;
        internal const byte CommandAddAction = 0x24;
        internal const byte CommandDim = 0x26;

        /// <summary>ALIENWARE_COMMAND_REPORT_CONFIG (AlienwareController.h:51).</summary>
        internal const byte ReportConfig = 0x02;

        /// <summary>The animation steps, new and finish-and-play
        /// (AlienwareController.h:61-63), and the slot that is not saved
        /// (line 74).</summary>
        internal const ushort AnimationNew = 0x0001;
        internal const ushort AnimationFinishPlay = 0x0003;
        internal const ushort UnsavedSlot = 0xFFFF;

        /// <summary>ALIENWARE_MODE_COLOR and ALIENWARE_TEMPO_MAX, and the
        /// 2000 ms duration UpdateMode gives the color action
        /// (AlienwareController.h:79 and 90, AlienwareController.cpp:817-819).</summary>
        internal const byte ModeColor = 0x00;
        internal const ushort TempoMax = 0x00FA;
        internal const ushort ColorDuration = 2000;

        /// <summary>The wait after a report (AlienwareController.cpp:83-92).</summary>
        internal const int ReportWaitMs = 60;

        /// <summary>The platform of the four zones the model's map holds
        /// until a session reads the controller: the G5 SE 5505
        /// (AlienwareController.cpp:32 and 46).</summary>
        internal const int DefaultPlatform = 0x0C01;

        public string Name => "Alienware";

        private readonly Dictionary<ushort, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < ReportSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            lock (_models)
            {
                if (!_models.TryGetValue(collection.ProductId, out var model))
                    _models[collection.ProductId] = model = new HidLightingModel
                    {
                        Family = this,
                        Name = registrations[0].Name,
                        Map = MapOf(DefaultPlatform, ZoneCount(DefaultPlatform, 0), collection.ProductId),
                        Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                        Begin = (io, wait) => Begin(io, wait, collection.ProductId),
                    };
                return model;
            }
        }

        // ─────────────────────────────────────────────
        //  The zones
        // ─────────────────────────────────────────────

        /// <summary>The zone count: the quirk table's for the platform, else
        /// the count the controller reported
        /// (AlienwareController.cpp:118).</summary>
        internal static int ZoneCount(int platform, int reported)
        {
            foreach (var (p, zones) in ZoneQuirks)
                if (p == platform) return zones;
            return reported;
        }

        /// <summary>The zones' names, "Unknown" for a platform the table does
        /// not hold (AlienwareController.cpp:135-152).</summary>
        internal static string[] ZoneNamesOf(int platform, int count)
        {
            foreach (var (p, names) in ZoneNames)
                if (p == platform) return names;
            var unknown = new string[count];
            Array.Fill(unknown, "Unknown");
            return unknown;
        }

        /// <summary>The zones' hardware IDs, from 0 for a platform the table
        /// does not hold (AlienwareController.cpp:157-167), and zone i's
        /// ID where the table runs short (lines 729, 757 and 802).</summary>
        internal static byte ZoneId(int platform, int zone)
        {
            foreach (var (p, ids) in ZoneIds)
                if (p == platform) return zone < ids.Length ? ids[zone] : (byte)zone;
            return (byte)zone;
        }

        /// <summary>One light per zone, its address the zone's hardware ID.
        /// A zone named "Light Bar" is an edge light, any other a zone of
        /// the keyboard, numbered from 1. A name the table runs short of is
        /// "Unknown", as OpenRGB reads past it at its own risk
        /// (RGBController_Alienware.cpp:143-149).</summary>
        internal static IReadOnlyList<LightInfo> Lights(int platform, int count)
        {
            var names = ZoneNamesOf(platform, count);
            var lights = new List<LightInfo>(count);
            int zone = 0;
            for (int i = 0; i < count; i++)
            {
                string name = i < names.Length ? names[i] : "Unknown";
                byte id = ZoneId(platform, i);
                lights.Add(name.StartsWith("Light Bar", StringComparison.Ordinal)
                    ? new LightInfo(id, LightKind.Edge, 0, LightKeySource.None)
                    : new LightInfo(id, LightKind.Zone, 0, LightKeySource.None) { Zone = ++zone });
            }
            return lights;
        }

        internal static LightMap MapOf(int platform, int count, ushort productId)
            => new(Lights(platform, count), "OpenRGB AlienwareController")
            {
                Product = $"alienware:187C:{productId:X4}:{platform:X4}",
            };

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        /// <summary>A report: the report ID 0, 03, the command, then the
        /// command's bytes (AlienwareController.cpp:258-264 and the like).</summary>
        private static byte[] Report(byte command, params byte[] data)
        {
            var report = new byte[ReportSize];
            report[1] = 0x03;
            report[2] = command;
            data.CopyTo(report, 3);
            return report;
        }

        /// <summary>The configuration request (Report, lines 249-272).</summary>
        internal static byte[] ConfigRequest() => Report(CommandReport, ReportConfig);

        /// <summary>A user animation step: the step, the slot and the
        /// duration, each high byte first (UserAnimation, lines 347-372).</summary>
        internal static byte[] Animation(ushort step, ushort slot)
            => Report(CommandUserAnimation, (byte)(step >> 8), (byte)step, (byte)(slot >> 8), (byte)slot, 0, 0);

        /// <summary>The selection of zones: 01, the count high byte first,
        /// the IDs (SelectZones, lines 402-460).</summary>
        internal static byte[] Select(IReadOnlyList<byte> zones, int offset, int count)
        {
            var report = Report(CommandSelectZones, 1, (byte)(count >> 8), (byte)count);
            for (int i = 0; i < count; i++) report[6 + i] = zones[offset + i];
            return report;
        }

        /// <summary>One color action: the mode, the duration and the tempo,
        /// each high byte first, and the color (ModeAction, lines 467-523).</summary>
        internal static byte[] ColorAction(int rgb)
            => Report(CommandAddAction, ModeColor, ColorDuration >> 8, ColorDuration & 0xFF, TempoMax >> 8, TempoMax & 0xFF,
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        /// <summary>The dim level of zones: the percent, the count high byte
        /// first, the IDs (Dim, lines 288-345).</summary>
        internal static byte[] Dim(byte percent, IReadOnlyList<byte> zones, int offset, int count)
        {
            var report = Report(CommandDim, percent, (byte)(count >> 8), (byte)count);
            for (int i = 0; i < count; i++) report[6 + i] = zones[offset + i];
            return report;
        }

        // ─────────────────────────────────────────────
        //  The exchanges
        // ─────────────────────────────────────────────

        /// <summary>Sends a report, waits as SendHIDReport waits and reads
        /// the answer (GetResponse, lines 231-247). Null when either
        /// failed.</summary>
        private static byte[] Exchange(IHidReportChannel io, Action<int> wait, byte[] report)
        {
            if (!io.SetFeature(report)) return null;
            wait(ReportWaitMs);
            var answer = new byte[ReportSize];
            return io.GetFeature(0, answer) ? answer : null;
        }

        /// <summary>The answer of a command whose failure is an answer equal
        /// to the request (lines 341, 456 and 522): byte 1 is 03 and the
        /// answer differs from the report.</summary>
        internal static bool Took(byte[] report, byte[] answer)
            => answer != null && answer[1] == 0x03 && !answer.AsSpan().SequenceEqual(report);

        /// <summary>A session on the platform the controller reports, or
        /// null when it gave no answer or no zones.</summary>
        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, ushort productId)
        {
            var answer = Exchange(io, wait, ConfigRequest());
            if (answer == null) return null;
            int platform = answer[4] << 8 | answer[5];
            int count = ZoneCount(platform, answer[6]);
            if (count == 0) return null;
            return new Session(MapOf(platform, count, productId));
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;
            private bool _dimmed;

            public Session(LightMap map) => _map = map;

            public override LightMap Map => _map;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                var ids = new byte[count];
                for (int n = 0; n < count; n++) ids[n] = (byte)lights[n].Address;
                if (!_dimmed)
                {
                    for (int offset = 0; offset < count; offset += MaxZonesPerReport)
                    {
                        var dim = Dim(0, ids, offset, Math.Min(MaxZonesPerReport, count - offset));
                        if (!Took(dim, Exchange(io, wait, dim))) return HidLightingPaint.Failed;
                    }
                    _dimmed = true;
                }
                var start = Exchange(io, wait, Animation(AnimationNew, UnsavedSlot));
                if (start == null || start[1] == 0) return HidLightingPaint.Failed;
                for (int n = 0; n < count; n++)
                {
                    var select = Select(ids, n, 1);
                    if (!Took(select, Exchange(io, wait, select))) return HidLightingPaint.Failed;
                    var action = ColorAction(colors[n]);
                    if (!Took(action, Exchange(io, wait, action))) return HidLightingPaint.Failed;
                }
                // Finish and play answers its result in byte 5 (line 394).
                var finish = Exchange(io, wait, Animation(AnimationFinishPlay, UnsavedSlot));
                return finish != null && finish[1] != 0 && finish[5] == 0 ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
