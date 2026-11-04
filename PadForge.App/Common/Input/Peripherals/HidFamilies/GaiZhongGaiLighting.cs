using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of a GaiZhongGai board: its number in OpenRGB's
    /// light order, its name and its matrix cell
    /// (RGBController_GaiZhongGai.cpp:21-233).</summary>
    internal readonly record struct GaiZhongGaiLed(int Number, string Name, byte Row, byte Col);

    /// <summary>
    /// The GaiZhongGai open-hardware boards, lit as OpenRGB's
    /// GaiZongGaiKeyboardController lights them (#557): the 68+4 PRO and 42
    /// PRO keyboards, the 17 PRO, 20 PRO and 17+4+Touch PRO keypads, the
    /// Dial, the LightBoard and the green and blue RGB hubs, each on
    /// interface 3 (GaiZhongGaiControllerDetect.cpp:35-43). OpenRGB opens
    /// every collection of that interface and writes 65-byte output reports,
    /// report ID 0, so the collection lit is the one whose output report is
    /// 65 bytes.
    ///
    /// <para>The colors. Each light's green, red and blue, in that order
    /// (RGBController_GaiZhongGai.cpp:694-709), in the board's reports
    /// (GaiZhongGaiController.cpp:195-285): the 68+4 PRO in four reports
    /// 0x10 to 0x13 of lights 0-20, 21-41, 42-62 and 63-67, 2 ms apart, the
    /// 42 PRO in 0x10 and 0x11, the 17 PRO in one report 0x10 of lights
    /// 68-84, the 20 PRO and 17+4+Touch PRO of lights 68-87, and the Dial
    /// of lights 85-88. OpenRGB's Dial report also carries 51 bytes past
    /// its last light from an unset buffer, which a session sends as 0. The
    /// LightBoard and the hubs take every light in order, 20 to a report
    /// numbered from 0, the report after the last whole one included.</para>
    ///
    /// <para>The lights. The keyboards and keypads have the lights their
    /// matrix holds: OpenRGB gives the keypads and the Dial the 68+4's
    /// first 68 key names as well, which no report carries, and they are
    /// left out. The Dial's four ring lights and the keypads' strip lights,
    /// "RGB Strip" 1 to 3, are zones. The LightBoard reads how its panels
    /// connect from its memory, command 0x85 three times, and lays out a
    /// light for every other point around each panel, a hexagon, a square
    /// or a triangle (GaiZhongGaiController.cpp:40-65,
    /// RGBController_GaiZhongGai.cpp:238-358). The hubs read each channel's
    /// light count, command 0x87, two bytes per channel high first
    /// (GaiZhongGaiController.cpp:70-98), four channels on the green hub and
    /// eight on the blue (RGBController_GaiZhongGai.cpp:477-490), and each
    /// channel is a zone of its own lights. PadForge does not change a
    /// hub's counts: OpenRGB writes them only when its user resizes a
    /// zone (lines 676-692).</para>
    ///
    /// <para>The hand-back. The colors are stored nowhere ("@save :x:",
    /// lines 360-382). OpenRGB's controller sends 0xFF when it closes,
    /// "Restore built-in light effect" (GaiZhongGaiController.cpp:149-160),
    /// and so do the hand-back and the crash path.</para>
    ///
    /// <para>No program is named: these boards have no vendor program
    /// beyond their open firmware.</para>
    /// </summary>
    internal sealed class GaiZhongGaiLighting : IHidLightingFamily
    {
        public static readonly GaiZhongGaiLighting Instance = new();

        internal const string FamilyName = "GaiZongGaiKeyboardController";

        /// <summary>The product IDs (GaiZhongGaiController.h:21-38).</summary>
        internal const ushort Pro68 = 0x4700;
        internal const ushort Pro42 = 0x4701;
        internal const ushort PadTouch = 0x4770;
        internal const ushort Pad17 = 0x4771;
        internal const ushort Pad20 = 0x4772;
        internal const ushort LightBoard = 0x4710;
        internal const ushort HubGreen = 0x4711;
        internal const ushort HubBlue = 0x4712;
        internal const ushort Dial = 0x4720;

        /// <summary>The report length, the report ID included.</summary>
        internal const int ReportLength = 65;

        /// <summary>The wait between the keyboards' reports
        /// (GaiZhongGaiController.cpp:211-221 and 233).</summary>
        internal const int ReportWaitMs = 2;

        /// <summary>How long a session waits for an answer to a read.
        /// OpenRGB's hid_read waits without bound, so the bound is
        /// PadForge's.</summary>
        internal const int ReadTimeoutMs = 1000;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it.</summary>
        internal const int RepaintMs = 33;

        /// <summary>The release to the built-in effect (line 156).</summary>
        internal static readonly byte[] Restore = MakeRestore();

        private static byte[] MakeRestore()
        {
            var report = new byte[ReportLength];
            report[1] = 0xFF;
            return report;
        }

        public string Name => "GaiZhongGai";

        private readonly Dictionary<ushort, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.OutputReportLength != ReportLength) return null;
            var registrations = OpenRgbRegistrations.Match(FamilyName, collection);
            if (registrations.Count == 0) return null;
            lock (_models)
            {
                if (_models.TryGetValue(collection.ProductId, out var model)) return model;
                ushort pid = collection.ProductId;
                string product = $"gaizhonggai:{collection.VendorId:X4}:{pid:X4}";
                model = new HidLightingModel
                {
                    Family = this,
                    Name = registrations[0].Name,
                    Map = new LightMap(Lights(pid), "OpenRGB RGBController_GaiZhongGai.cpp") { Product = product },
                    RepaintMs = RepaintMs,
                    Begin = (io, wait) => Session.Begin(io, pid, product),
                };
                _models[pid] = model;
                return model;
            }
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        internal static GaiZhongGaiLed[] Table(ushort pid) => pid switch
        {
            Pro68 => GaiZhongGaiLayouts.Pro68,
            Pro42 => GaiZhongGaiLayouts.Pro42,
            Dial => GaiZhongGaiLayouts.Dial,
            Pad17 => GaiZhongGaiLayouts.Pad17,
            Pad20 => GaiZhongGaiLayouts.Pad20,
            PadTouch => GaiZhongGaiLayouts.PadTouch,
            _ => null,
        };

        /// <summary>A keyboard's, keypad's or the Dial's lights, each
        /// addressed by its number. A strip light is a zone, numbered by its
        /// strip number.</summary>
        internal static IReadOnlyList<LightInfo> Lights(ushort pid)
        {
            var table = Table(pid);
            if (table == null) return Array.Empty<LightInfo>();
            var lights = new List<LightInfo>(table.Length);
            foreach (var led in table)
            {
                var light = led.Name.StartsWith("RGB Strip ", StringComparison.Ordinal)
                    && int.TryParse(led.Name.AsSpan(10), out int strip)
                    ? new LightInfo(led.Number, LightKind.Zone, 0, LightKeySource.None) { Zone = strip }
                    : SinowealthLighting.Named(led.Number, led.Name);
                lights.Add(light with { X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true });
            }
            return lights;
        }

        /// <summary>
        /// The LightBoard's light positions from its panel record, OpenRGB's
        /// LightBoard_init and board_led_xy_self_call
        /// (RGBController_GaiZhongGai.cpp:238-358): from byte 0, each byte a
        /// panel, its top three bits 1 for a hexagon, else bits 3 and 4 set
        /// to 1 for a square, else bit 2 alone for a triangle, and 7 to
        /// stop. A light sits at every other point around it and, at the others,
        /// the next panel where the byte's bit for that side is set. Byte
        /// 120 is the scale in hundredths, 121 the turn, 122 and 123 the
        /// offsets, 124 the width. The lights are numbered in the order they
        /// are laid out. The positions are computed in single precision as
        /// OpenRGB's are, and may round apart from its by one cell.
        /// </summary>
        internal static List<(float X, float Y)> LightBoardPositions(byte[] flash)
        {
            var points = new List<(float, float)>();
            float pi = MathF.Acos(-1.0f);
            float angle = ((21 - flash[121]) % 12) * pi / 6;
            int offset = 0;
            Panel(flash, ref offset, points, 0, 0, 0, angle, pi);
            return points;
        }

        private static void Panel(byte[] p, ref int offset, List<(float, float)> points, float x, float y,
            float distance, float angle, float pi)
        {
            if (offset == 120 || points.Count > 1024) return;
            byte temp = p[offset];
            float ledDistance = 0, boardDistance = 0, step = 0;
            int range = 0;
            switch (temp >> 5)
            {
                case 0x07:
                    return;
                case 0x01:
                    ledDistance = 3.0f; boardDistance = 4.33f; range = 12; step = pi / 6.0f;
                    break;
                case 0x00:
                    switch (temp >> 3)
                    {
                        case 0x01:
                            ledDistance = 3.54f; boardDistance = 5.0f; range = 8; step = pi / 4.0f;
                            break;
                        case 0x00:
                            if (temp >> 2 == 1)
                            {
                                ledDistance = 2.8f; boardDistance = 2.9f; range = 6; step = pi / 3.0f;
                            }
                            break;
                    }
                    break;
            }
            float nx = x + MathF.Cos(angle) * (distance + boardDistance);
            float ny = y + MathF.Sin(angle) * (distance + boardDistance);
            float na = angle > pi ? angle - pi : angle + pi;
            for (int i = 1; i < range; i++)
            {
                na -= step;
                if ((i & 1) != 0)
                {
                    byte px = (byte)((short)((nx + MathF.Cos(na) * ledDistance) * p[120] * 0.01f + 0.5f) - (sbyte)p[122]);
                    byte py = (byte)((short)(((ny + MathF.Sin(na) * ledDistance) * p[120] * 0.01f + 0.5f) / -1) - (sbyte)p[123]);
                    points.Add((px, py));
                }
                else if ((temp & (1 << (i / 2 - 1))) != 0)
                {
                    offset++;
                    Panel(p, ref offset, points, nx, ny, boardDistance, na, pi);
                }
            }
        }

        /// <summary>The LightBoard's lights at their places, one zone of
        /// lights in its order.</summary>
        internal static IReadOnlyList<LightInfo> LightBoardLights(byte[] flash)
        {
            var points = LightBoardPositions(flash);
            var lights = new List<LightInfo>(points.Count);
            for (int i = 0; i < points.Count; i++)
                lights.Add(new LightInfo(i, LightKind.Zone, 0, LightKeySource.None)
                {
                    Zone = 1, X = points[i].X, Y = points[i].Y, HasPosition = true,
                });
            return lights;
        }

        /// <summary>A hub's lights: each channel's count in order, the
        /// lights of channel n zone n + 1.</summary>
        internal static IReadOnlyList<LightInfo> HubLights(ushort pid, byte[] lengths)
        {
            int channels = pid == HubBlue ? 8 : 4;
            var lights = new List<LightInfo>();
            for (int ch = 0; ch < channels; ch++)
            {
                int count = lengths[ch * 2] << 8 | lengths[ch * 2 + 1];
                for (int i = 0; i < count && lights.Count < 32 * 20; i++)
                    lights.Add(new LightInfo(lights.Count, LightKind.Zone, 0, LightKeySource.None) { Zone = ch + 1 });
            }
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>Each light's green, red and blue by its number, for
        /// numbers 0 to <paramref name="length"/> - 1.</summary>
        internal static byte[] ColorData(int length, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var data = new byte[length * 3];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = lights[n].Address;
                if ((uint)at >= (uint)length) continue;
                int rgb = colors[n];
                data[at * 3] = (byte)(rgb >> 8);
                data[at * 3 + 1] = (byte)(rgb >> 16);
                data[at * 3 + 2] = (byte)rgb;
            }
            return data;
        }

        private static byte[] Report(byte id, byte[] data, int from, int count)
        {
            var report = new byte[ReportLength];
            report[1] = id;
            Array.Copy(data, from, report, 2, Math.Max(0, Math.Min(count, data.Length - from)));
            return report;
        }

        /// <summary>A keyboard's, keypad's or the Dial's reports, and
        /// whether each is followed by the 2 ms wait (SendColors,
        /// GaiZhongGaiController.cpp:205-257).</summary>
        internal static List<(byte[] Report, bool Wait)> BoardFrames(ushort pid, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            var data = ColorData(89, lights, colors);
            var frames = new List<(byte[], bool)>();
            switch (pid)
            {
                case Pro68:
                    frames.Add((Report(0x10, data, 0 * 3, 63), true));
                    frames.Add((Report(0x11, data, 21 * 3, 63), true));
                    frames.Add((Report(0x12, data, 42 * 3, 63), true));
                    frames.Add((Report(0x13, data, 63 * 3, 15), false));
                    break;
                case Pro42:
                    frames.Add((Report(0x10, data, 0 * 3, 63), true));
                    frames.Add((Report(0x11, data, 21 * 3, 63), false));
                    break;
                case PadTouch:
                case Pad20:
                    frames.Add((Report(0x10, data, 68 * 3, 60), false));
                    break;
                case Pad17:
                    frames.Add((Report(0x10, data, 68 * 3, 51), false));
                    break;
                case Dial:
                    frames.Add((Report(0x10, data, 85 * 3, 63), false));
                    break;
            }
            return frames;
        }

        /// <summary>The LightBoard's and the hubs' reports: 60 bytes each,
        /// numbered from 0, up to report size / 60 and at most 32
        /// (lines 259-283).</summary>
        internal static List<byte[]> StreamFrames(int lightCount, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var data = ColorData(lightCount, lights, colors);
            var frames = new List<byte[]>();
            for (int i = 0; i < 32; i++)
            {
                if (i > data.Length / 60) break;
                frames.Add(Report((byte)i, data, i * 60, 60));
            }
            return frames;
        }

        /// <summary>A read command and its answer's data, the bytes after the
        /// report ID, or null for none (lines 42-85).</summary>
        internal static byte[] Read(IHidReportChannel io, byte[] command)
        {
            if (!io.WriteOutput(command)) return null;
            var buffer = new byte[Math.Max(ReportLength, io.InputReportLength)];
            int read = io.ReadInput(buffer, ReadTimeoutMs);
            if (read <= 1) return null;
            var data = new byte[ReportLength - 1];
            Array.Copy(buffer, 1, data, 0, Math.Min(data.Length, read - 1));
            return data;
        }

        /// <summary>The LightBoard's panel record: 0x85 with an offset and a
        /// length, three times, the data from byte 3 of each answer
        /// (lines 40-65).</summary>
        internal static byte[] ReadLightBoard(IHidReportChannel io)
        {
            var flash = new byte[128];
            foreach (var (at, length) in new[] { (0, 60), (60, 60), (120, 8) })
            {
                var command = new byte[ReportLength];
                command[1] = 0x85;
                command[2] = (byte)at;
                command[3] = (byte)length;
                var answer = Read(io, command);
                if (answer == null) return null;
                Array.Copy(answer, 3, flash, at, length);
            }
            return flash;
        }

        /// <summary>A hub's channel counts: 0x87, the data from byte 2 of the
        /// answer, 16 bytes (lines 70-85).</summary>
        internal static byte[] ReadHubLengths(IHidReportChannel io)
        {
            var command = new byte[ReportLength];
            command[1] = 0x87;
            var answer = Read(io, command);
            if (answer == null) return null;
            var lengths = new byte[16];
            Array.Copy(answer, 2, lengths, 0, 16);
            return lengths;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly ushort _pid;
            private readonly byte[][] _crash = { Restore };

            private Session(ushort pid, LightMap map)
            {
                _pid = pid;
                Map = map;
            }

            /// <summary>The LightBoard's and the hubs' lights, read from them,
            /// or null for a board whose lights are fixed.</summary>
            public override LightMap Map { get; }

            public override bool Feature => false;

            public override byte[][] CrashFrames => _crash;

            public static HidLightingSession Begin(IHidReportChannel io, ushort pid, string product)
            {
                switch (pid)
                {
                    case LightBoard:
                    {
                        var flash = ReadLightBoard(io);
                        if (flash == null) return null;
                        return new Session(pid, new LightMap(LightBoardLights(flash), "GaiZhongGai LightBoard, from the board") { Product = product });
                    }
                    case HubGreen:
                    case HubBlue:
                    {
                        var lengths = ReadHubLengths(io);
                        if (lengths == null) return null;
                        return new Session(pid, new LightMap(HubLights(pid, lengths), "GaiZhongGai RGB hub, from the hub") { Product = product });
                    }
                    default:
                        return new Session(pid, null);
                }
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (Map != null)
                {
                    foreach (var frame in StreamFrames(Map.Lights.Count, lights, colors))
                        if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                    return HidLightingPaint.Sent;
                }
                foreach (var (report, pause) in BoardFrames(_pid, lights, colors))
                {
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    if (pause) wait(ReportWaitMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => io.WriteOutput(Restore);
        }
    }
}
