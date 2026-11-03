using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Fnatic Streak, miniStreak and Streak65 keyboards (#557), lit as
    /// OpenRGB's FnaticStreakController lights them: 2F0E:0101, 0102 and
    /// 0105 on interface 1 (FnaticStreakControllerDetect.cpp:25-29 and
    /// 64-66). OpenRGB registers the interface without a usage, so the
    /// collection taken is the one whose output report carries a frame.
    ///
    /// <para>The Streak and the miniStreak. Their lights are OpenRGB's
    /// led_names_full, 124 LEDs, and led_names_mini, 106, each at its LED
    /// index (RGBController_FnaticStreak.cpp:69-195, 201-309 and 501-533).
    /// A picture is the Direct mode's request 0F 03 followed by the colors,
    /// red, green and blue at three times each index, in 65-byte output
    /// reports: report ID 0, the command, the request's total length and the
    /// report's offset into it, each as three little-endian bytes, then up
    /// to 57 bytes of the request (SendRequest and SendRGBToDevice,
    /// FnaticStreakController.cpp:128-184 and 231-268; SetLEDsDirect at the
    /// Direct mode's brightness of 100, which scales nothing, lines 186-229
    /// and RGBController_FnaticStreak.cpp:337-347 and 535-546).</para>
    ///
    /// <para>The Streak65. OpenRGB lays it out with the Streak's 124 LEDs
    /// but sends it one color, the first LED's, in bytes 25 to 27 of a
    /// 64-byte output report with report ID 0x0F, 0F 15, 0F 03 in bytes 7
    /// and 8 and FF in bytes 9 to 24 (SendRGBToDevice, lines 233-254). So
    /// its map here is that one light.</para>
    ///
    /// <para>Holding and handing back. The keyboard goes back to its
    /// profile's effect unless it hears from the host, so OpenRGB sends the
    /// request 07 whenever no picture has gone out for 500 ms, checking
    /// every 100 ms (KeepaliveThread, RGBController_FnaticStreak.cpp:480-487
    /// and 683-699; SendKeepalive, FnaticStreakController.cpp:110-119). A
    /// session does the same on its recheck. Nothing a picture sends is
    /// saved: OpenRGB saves only after an effect request, 0x05
    /// (lines 172-183). The hand-back is the keyboard's own: once the
    /// keep-alives stop, it returns to its profile's effect, so a session
    /// sends nothing to hand it back. No source names a program that lights
    /// these keyboards.</para>
    /// </summary>
    internal sealed class FnaticStreakLighting : IHidLightingFamily
    {
        public static readonly FnaticStreakLighting Instance = new();

        internal const string OpenRgbFamily = "FnaticStreakController";

        internal const ushort VendorId = 0x2F0E;
        internal const ushort StreakPid = 0x0101;
        internal const ushort MiniStreakPid = 0x0102;
        internal const ushort Streak65Pid = 0x0105;

        internal const int PacketSize = 65;
        internal const int Streak65Size = 64;

        /// <summary>The data a report carries after its 8-byte header
        /// (FnaticStreakController.cpp:139-166).</summary>
        internal const int Payload = 57;

        /// <summary>The LED counts (GetLEDCount, lines 78-88).</summary>
        internal const int FullLeds = 124;
        internal const int MiniLeds = 106;

        /// <summary>The keep-alive's idle time and the period OpenRGB checks
        /// it on (RGBController_FnaticStreak.cpp:693 and 698).</summary>
        internal const int IdleMs = 500;
        internal const int CheckMs = 100;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Fnatic";

        private readonly Func<long> _ticks;
        private readonly KeyboardLedNames.ModelCache _models = new();

        public FnaticStreakLighting() : this(null) { }

        /// <summary>Test seam: the clock the keep-alive is timed by.</summary>
        internal FnaticStreakLighting(Func<long> ticks) => _ticks = ticks ?? (() => Environment.TickCount64);

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            ushort pid = collection.ProductId;
            bool one = pid == Streak65Pid;
            if (collection.OutputReportLength < (one ? Streak65Size : PacketSize)) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, pid));
        }

        /// <summary>A board's lights: the LEDs of its name list at their
        /// indexes, or the Streak65's one color.</summary>
        internal static List<LightInfo> Lights(ushort pid)
        {
            if (pid == Streak65Pid)
                return new List<LightInfo> { new(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };
            return pid == MiniStreakPid
                ? KeyboardLedNames.FromNames(KeyboardLedNames.FnaticMiniNames, i => i, MiniLeds)
                : KeyboardLedNames.FromNames(KeyboardLedNames.FnaticFullNames, i => i, FullLeds);
        }

        private HidLightingModel Model(string name, ushort pid) => new()
        {
            Family = this,
            Name = name,
            Map = new LightMap(Lights(pid), "OpenRGB RGBController_FnaticStreak.cpp")
            {
                Product = $"fnatic:{VendorId:X4}:{pid:X4}",
            },
            RepaintMs = RepaintMs,
            RecheckMs = CheckMs,
            Begin = (_, _) => new Session(pid, _ticks),
        };

        /// <summary>SendRequest's reports for a command's prefix and data
        /// (FnaticStreakController.cpp:128-170).</summary>
        internal static List<byte[]> Request(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data)
        {
            int total = prefix.Length + data.Length;
            var request = new byte[total];
            prefix.CopyTo(request);
            data.CopyTo(request.AsSpan(prefix.Length));
            byte command = request[0];
            var reports = new List<byte[]>();
            for (int offset = 0; offset < total; offset += Payload)
            {
                var report = new byte[PacketSize];
                report[1] = command;
                report[2] = (byte)total;
                report[3] = (byte)(total >> 8);
                report[4] = (byte)(total >> 16);
                report[5] = (byte)offset;
                report[6] = (byte)(offset >> 8);
                report[7] = (byte)(offset >> 16);
                request.AsSpan(offset, Math.Min(Payload, total - offset)).CopyTo(report.AsSpan(8));
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>The keep-alive request, 07 (lines 110-119).</summary>
        internal static byte[] KeepAlive() => Request(new byte[] { 0x07 }, ReadOnlySpan<byte>.Empty)[0];

        /// <summary>A Streak or miniStreak picture: 0F 03 and the colors.</summary>
        internal static List<byte[]> Picture(int leds, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byIndex = KeyboardLedNames.ByAddress(lights, colors, leds);
            var data = new byte[leds * 3];
            for (int i = 0; i < leds; i++)
            {
                data[i * 3] = (byte)(byIndex[i] >> 16);
                data[i * 3 + 1] = (byte)(byIndex[i] >> 8);
                data[i * 3 + 2] = (byte)byIndex[i];
            }
            return Request(new byte[] { 0x0F, 0x03 }, data);
        }

        /// <summary>The Streak65's report for one color (lines 233-254).</summary>
        internal static byte[] Streak65Picture(int color)
        {
            var report = new byte[Streak65Size];
            report[0] = 0x0F;
            report[1] = 0x15;
            report[7] = 0x0F;
            report[8] = 0x03;
            for (int i = 9; i < 25; i++) report[i] = 0xFF;
            report[25] = (byte)(color >> 16);
            report[26] = (byte)(color >> 8);
            report[27] = (byte)color;
            return report;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly ushort _pid;
            private readonly Func<long> _ticks;
            private long _sentAt;

            public Session(ushort pid, Func<long> ticks)
            {
                _pid = pid;
                _ticks = ticks;
                _sentAt = ticks();
            }

            public override bool Feature => false;

            /// <summary>Nothing to send: the keyboard returns to its profile
            /// on its own (see the class comment).</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (_pid == Streak65Pid)
                {
                    int color = KeyboardLedNames.ByAddress(lights, colors, 1)[0];
                    if (!io.WriteOutput(Streak65Picture(color))) return HidLightingPaint.Failed;
                }
                else
                {
                    foreach (var report in Picture(_pid == MiniStreakPid ? MiniLeds : FullLeds, lights, colors))
                        if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                }
                _sentAt = _ticks();
                return HidLightingPaint.Sent;
            }

            /// <summary>The keep-alive, once no picture has gone out for
            /// <see cref="IdleMs"/>.</summary>
            public override bool Recheck(IHidReportChannel io, Action<int> wait)
            {
                if (_ticks() - _sentAt <= IdleMs) return true;
                return io != null && io.WriteOutput(KeepAlive());
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
