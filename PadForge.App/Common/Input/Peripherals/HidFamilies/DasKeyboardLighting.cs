using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Das Keyboard 4Q, 5Q and 5QS, lit as OpenRGB's
    /// DasKeyboardController lights them (#557), key by key through feature
    /// report 1.
    ///
    /// <para>The collections. OpenRGB registers the 4Q's collection on
    /// interface 1 with usage page 1 and usage 0x80, and every collection on
    /// interface 1 of the 5Q and 5QS (DasKeyboardControllerDetect.cpp:55-57).
    /// hidapi on Linux opens interface 1 as one device. Windows gives each
    /// of its top-level collections a path of its own, and feature report 1
    /// belongs to one of them, so on a 5Q or 5QS the family takes a
    /// collection of interface 1 only where it has feature reports of the
    /// 8 bytes the reads take (<see cref="ReportBytes"/>).</para>
    ///
    /// <para>The session. It opens the link as OpenRGB's SendInitialize
    /// does (DasKeyboardController.cpp:128-167): EA 02 B0 sent, the answer
    /// read, and if none came, both again in the 8-byte form older firmware
    /// takes (SendDataTraditional, lines 232-281). The answer is the
    /// firmware's version string, whose characters 3 and 4 tell a US board
    /// (16 or 63) from any other (GetLayoutString, lines 34-52), which picks
    /// the grid (RGBController_DasKeyboard.cpp:256-294). A picture sends
    /// each key whose color changed, EA 08 78 08, the key, mode 1 (Direct,
    /// RGBController_DasKeyboard.h:19) and the color, or for the Q button
    /// (LED 130) EA 06 78 06 and the color, then applies them with
    /// EA 03 78 0A and reads the answer (lines 296-335,
    /// DasKeyboardController.cpp:93-179). Every message goes out as report
    /// 1, its bytes and their XOR (SendDataModern, lines 193-230), three
    /// tries for each, with OpenRGB's 0.3 ms between them for a bug of
    /// firmware 21.27.0, which the session rounds up to 1 ms.</para>
    ///
    /// <para>What stays. OpenRGB marks the colors as not saved by the
    /// keyboard (@save :x:, RGBController_DasKeyboard.cpp:195-204) and has no
    /// step that gives the keyboard back, and node-lib, the other open
    /// driver, speaks an older protocol of the 5Q's with no such step either
    /// (node-lib src/internal/models/packets). Nothing goes out at the
    /// hand-back, and the keyboard shows the last colors PadForge sent until
    /// it is unplugged or the Das Keyboard Q software lights it.</para>
    /// </summary>
    internal sealed class DasKeyboardLighting : IHidLightingFamily
    {
        public static readonly DasKeyboardLighting Instance = new();

        internal const string FamilyName = "DasKeyboardController";

        internal const byte ReportId = 0x01;

        /// <summary>The feature report's bytes a read takes, the ID included
        /// (DasKeyboardController.cpp:285-298), and the most the modern
        /// form sends, a 9-byte message, its XOR and the ID (lines 98-108
        /// and 193-217).</summary>
        internal const int ReportBytes = 8;

        internal const int LedCount = 131;
        internal const int QButton = 130;
        internal const byte ModeDirect = 0x01;

        /// <summary>How many tries a message gets (err_cnt, lines 200 and 242).</summary>
        internal const int Tries = 3;

        /// <summary>The longest answer read, in 8-byte reports. OpenRGB reads
        /// until a report's first byte is 0 (lines 293-312). The bound is
        /// PadForge's.</summary>
        internal const int MaxAnswerReports = 64;

        /// <summary>The shortest time between two pictures. A picture is a
        /// report per changed key, and no reference bounds the rate, so the
        /// bound is PadForge's.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Das Keyboard";

        private readonly Dictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            foreach (var registration in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                if (registration.UsagePage == OpenRgbRegistration.Any && collection.FeatureReportLength < ReportBytes)
                    return null;
                lock (_gate)
                {
                    if (!_models.TryGetValue(registration.Name, out var model))
                        _models[registration.Name] = model = Model(registration);
                    return model;
                }
            }
            return null;
        }

        private HidLightingModel Model(OpenRgbRegistration registration)
        {
            string product = $"das:{registration.VendorId:X4}:{registration.ProductId:X4}";
            return new HidLightingModel
            {
                Family = this,
                Name = registration.Name,
                Map = MapOf(us: false, product),
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Begin(io, wait, product),
            };
        }

        /// <summary>The map of the US or the other grid. OpenRGB uses the
        /// other grid whenever the version does not say US
        /// (RGBController_DasKeyboard.cpp:272-279).</summary>
        internal static LightMap MapOf(bool us, string product)
        {
            var layout = us ? MatrixKeyboardLayouts.DasUs : MatrixKeyboardLayouts.DasEu;
            return new LightMap(MatrixKeyboardLayouts.Lights(layout), "OpenRGB " + layout.Source) { Product = product };
        }

        // ─────────────────────────────────────────────
        //  The messages
        // ─────────────────────────────────────────────

        /// <summary>SendDataModern's report: 01, the message, its XOR.</summary>
        internal static byte[] Modern(ReadOnlySpan<byte> message)
        {
            var report = new byte[message.Length + 2];
            report[0] = ReportId;
            byte sum = 0;
            for (int i = 0; i < message.Length; i++)
            {
                report[i + 1] = message[i];
                sum ^= message[i];
            }
            report[message.Length + 1] = sum;
            return report;
        }

        /// <summary>SendDataTraditional's reports: 01 and seven bytes each,
        /// the message, then its XOR, then zeros.</summary>
        internal static List<byte[]> Traditional(ReadOnlySpan<byte> message)
        {
            var reports = new List<byte[]>();
            byte sum = 0;
            for (int at = 0; at < message.Length + 1; at += 7)
            {
                var report = new byte[ReportBytes];
                report[0] = ReportId;
                for (int field = 1; field < 8; field++)
                {
                    int index = at + field - 1;
                    if (index < message.Length)
                    {
                        report[field] = message[index];
                        sum ^= message[index];
                    }
                    else if (index == message.Length)
                    {
                        report[field] = sum;
                    }
                }
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>SendColors (lines 93-125).</summary>
        internal static byte[] KeyMessage(int key, int rgb)
            => key < QButton
                ? new byte[] { 0xEA, 0x08, 0x78, 0x08, (byte)key, ModeDirect, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb }
                : new byte[] { 0xEA, 0x06, 0x78, 0x06, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb };

        internal static readonly byte[] Initialize = { 0xEA, 0x02, 0xB0 };
        internal static readonly byte[] Apply = { 0xEA, 0x03, 0x78, 0x0A };

        /// <summary>
        /// ReceiveData (lines 283-354): 8-byte reads of report 1 while the
        /// first byte of what comes back is not 0, their XOR 0, then the
        /// answer's length in byte 1 and the answer from byte 2. Answers the
        /// length as ReceiveData returns it: -1 when the XOR is not 0 or the
        /// length runs past what came, 0 when nothing came.
        /// </summary>
        internal static int Receive(IHidReportChannel io, out byte[] answer)
        {
            answer = Array.Empty<byte>();
            var received = new List<byte>();
            byte sum = 0;
            for (int i = 0; i < MaxAnswerReports; i++)
            {
                var buf = new byte[ReportBytes];
                buf[0] = ReportId;
                if (!io.GetFeature(ReportId, buf)) break;
                if (buf[0] == 0) break;
                foreach (byte b in buf)
                {
                    received.Add(b);
                    sum ^= b;
                }
            }
            if (sum != 0) return -1;
            if (received.Count <= 1) return 0;
            int size = received[1];
            if (size + 2 > received.Count) return -1;
            answer = new byte[Math.Max(0, size - 1)];
            for (int i = 0; i < answer.Length; i++) answer[i] = received[i + 2];
            return size;
        }

        /// <summary>A US board by its version string, GetLayoutString (lines
        /// 34-52): 17 characters or more, and "16" or "63" at 3.</summary>
        internal static bool IsUs(string version)
        {
            if (version == null || version.Length < 17) return false;
            string id = version.Substring(3, 2);
            return id is "16" or "63";
        }

        /// <summary>The version string in an answer to EA 02 B0: from byte 2
        /// up to the first 0 (line 165).</summary>
        internal static string VersionOf(byte[] answer)
        {
            if (answer == null || answer.Length <= 2) return "";
            int end = Array.IndexOf(answer, (byte)0, 2);
            return Encoding.ASCII.GetString(answer, 2, (end < 0 ? answer.Length : end) - 2);
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, string product)
        {
            var session = new Session(wait);
            // SendInitialize: the modern form, then the traditional one once.
            byte[] answer = Array.Empty<byte>();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!session.Send(io, Initialize)) return null;
                if (Receive(io, out answer) != 0) break;
                if (session.Traditional) break;
                session.Traditional = true;
            }
            // The version string starts at byte 2 of the answer and ends at
            // its first 0 (std::string from &usb_buf[2], line 165).
            string version = VersionOf(answer);
            session.SetMap(MapOf(IsUs(version), product), version);
            return session;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Action<int> _wait;
            private readonly int[] _shown = new int[LedCount];
            private LightMap _map;

            public Session(Action<int> wait)
            {
                _wait = wait;
                Array.Fill(_shown, -1);
            }

            /// <summary>The 8-byte form older firmware takes.</summary>
            public bool Traditional { get; set; }

            public string Version { get; private set; } = "";

            public override LightMap Map => _map;

            public void SetMap(LightMap map, string version)
            {
                _map = map;
                Version = version;
            }

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>One message in the form the keyboard took, each
            /// report tried three times, 1 ms after each.</summary>
            internal bool Send(IHidReportChannel io, byte[] message)
            {
                var reports = Traditional ? Traditional(message) : new List<byte[]> { Modern(message) };
                foreach (var report in reports)
                {
                    bool sent = false;
                    for (int t = 0; t < Tries && !sent; t++)
                    {
                        sent = io.SetFeature(report);
                        _wait?.Invoke(1);
                    }
                    if (!sent) return false;
                }
                return true;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                for (int i = 0; i < lights.Count && i < colors.Count; i++)
                {
                    int key = lights[i].Address;
                    if (key < 0 || key >= LedCount) continue;
                    int rgb = colors[i] & 0xFFFFFF;
                    if (_shown[key] == rgb) continue;
                    if (!Send(io, KeyMessage(key, rgb))) return HidLightingPaint.Failed;
                    _shown[key] = rgb;
                }
                if (!Send(io, Apply)) return HidLightingPaint.Failed;
                Receive(io, out _);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
