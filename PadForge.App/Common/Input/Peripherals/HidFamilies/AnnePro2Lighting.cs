using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of the Anne Pro 2 or the Red Square Keyrox TKL:
    /// OpenRGB's name for it, its place in the board's frame, and its matrix
    /// cell (RGBController_AnnePro2.cpp:19-111,
    /// RGBController_RedSquareKeyrox.cpp:31-148).</summary>
    internal readonly record struct AnneKeyroxLed(string Name, int Place, byte Row, byte Col);

    /// <summary>
    /// The Anne Pro 2, lit as OpenRGB's AnnePro2Controller lights it (#557),
    /// on interface 1 of its five product IDs (AnnePro2ControllerDetect.cpp:50-54).
    /// OpenRGB writes 64-byte output reports, report ID 0, to the collection
    /// it opens, so the collection lit is the interface's one whose output
    /// report holds them.
    ///
    /// <para>The frame. The 61 lights' red, green and blue in a buffer of 70
    /// places, a place left out after lights 40, 41, 52, 53 and 60 and two
    /// after 55 and 56 (RGBController_AnnePro2.cpp:188-217), sent as
    /// ObinsKit's multi-color message Annemone reverse engineered
    /// (AnnePro2Controller.cpp:43-122, Annemone index.js generateMultiColor):
    /// five reports of 00 7B 10 41, the message count and number in one byte
    /// (0x50 + n), the length (55, the last 10), 00 00 7D, 20 03 FF, 02, then
    /// 51 bytes of the buffer, 50 ms apart, since the keyboard drops
    /// commands that come faster. OpenRGB's left-out places and its last
    /// report's tail come from unset memory, which a session sends as
    /// 0.</para>
    ///
    /// <para>The colors are stored nowhere ("@save :x:",
    /// RGBController_AnnePro2.cpp:113-122) and neither OpenRGB nor Annemone
    /// sends anything to hand the keyboard back, so the hand-back sends
    /// nothing and the keyboard keeps the last picture until its own effect
    /// is chosen or it restarts.</para>
    ///
    /// <para>No program is named: no source names ObinsKit's processes.</para>
    /// </summary>
    internal sealed class AnnePro2Lighting : IHidLightingFamily
    {
        public static readonly AnnePro2Lighting Instance = new();

        internal const string FamilyName = "AnnePro2Controller";

        /// <summary>max_hid_length and the frame's places
        /// (AnnePro2Controller.cpp:56, RGBController_AnnePro2.cpp:16).</summary>
        internal const int ReportLength = 64;
        internal const int Places = 70;
        internal const int MessageLength = 51;

        /// <summary>The wait after each report (AnnePro2Controller.cpp:115-119).</summary>
        internal const int ReportWaitMs = 50;

        public string Name => "Anne Pro 2";

        private readonly Dictionary<(ushort, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.OutputReportLength < ReportLength) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            var key = (collection.VendorId, collection.ProductId);
            lock (_models)
            {
                if (_models.TryGetValue(key, out var model)) return model;
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registrations[0].Name,
                    Map = new LightMap(Lights(), "OpenRGB RGBController_AnnePro2.cpp")
                    {
                        Product = $"annepro2:{collection.VendorId:X4}:{collection.ProductId:X4}",
                    },
                    // Five reports 50 ms apart make a picture.
                    RepaintMs = ReportWaitMs,
                    Begin = (io, wait) => new Session(),
                };
                _models[key] = model;
                return model;
            }
        }

        /// <summary>The 61 lights, each addressed by its index.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var table = AnnePro2KeyroxLayouts.AnnePro2;
            var lights = new List<LightInfo>(table.Length);
            foreach (var led in table)
                lights.Add(SinowealthLighting.Named(led.Place, led.Name) with
                {
                    X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true,
                });
            return lights;
        }

        /// <summary>The frame place of each light (RGBController_AnnePro2.cpp:196-214).</summary>
        internal static int PlaceOf(int led)
        {
            int place = 0;
            for (int i = 0; i < led; i++)
            {
                if (i is 40 or 41 or 52 or 53 or 60) place++;
                else if (i is 55 or 56) place += 2;
                place++;
            }
            return place;
        }

        /// <summary>The 70-place frame of a picture.</summary>
        internal static byte[] Frame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = new byte[Places * 3];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int led = lights[n].Address;
                if (led < 0 || led >= AnnePro2KeyroxLayouts.AnnePro2.Length) continue;
                int at = PlaceOf(led) * 3;
                int rgb = colors[n];
                frame[at] = (byte)(rgb >> 16);
                frame[at + 1] = (byte)(rgb >> 8);
                frame[at + 2] = (byte)rgb;
            }
            return frame;
        }

        /// <summary>SendDirect's reports (AnnePro2Controller.cpp:43-122).</summary>
        internal static List<byte[]> Reports(byte[] frame)
        {
            int messages = frame.Length / MessageLength + 1;
            int last = frame.Length % MessageLength;
            if (last == 0) last = MessageLength;
            var reports = new List<byte[]>(messages);
            for (int p = 0; p < messages; p++)
            {
                var report = new byte[ReportLength];
                report[1] = 123;
                report[2] = 16;
                report[3] = 65;
                report[4] = (byte)((messages << 4) + p);
                report[5] = (byte)((p == messages - 1 ? last : MessageLength) + 4);
                report[8] = 125;
                report[9] = 32;
                report[10] = 3;
                report[11] = 255;
                report[12] = 2;
                for (int i = 0; i < MessageLength; i++)
                {
                    int at = p * MessageLength + i;
                    if (at < frame.Length) report[13 + i] = frame[at];
                }
                reports.Add(report);
            }
            return reports;
        }

        internal sealed class Session : HidLightingSession
        {
            public override bool Feature => false;

            /// <summary>Nothing: no reference hands the keyboard back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var report in Reports(Frame(lights, colors)))
                {
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    wait(ReportWaitMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
