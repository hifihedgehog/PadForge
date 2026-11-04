using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The table of <see cref="PluginDeviceLighting"/>: each device's
    /// collection, lights and frames, from its plugin under
    /// signal-plugins/Plugins.</summary>
    internal sealed partial class PluginDeviceLighting
    {
        private static IEnumerable<PluginDevice> BuildDevices()
            => Evga().Concat(Patriot()).Concat(Speedlink()).Concat(ThermaltakeArgent()).Concat(CreativeKatana())
                .Concat(AlienwareDevices()).Concat(AsusDevices()).Concat(Fnatic()).Concat(HyperX()).Concat(RazerDevices())
                .Concat(LogitechG19()).Concat(MountainMakalu()).Concat(CoolerMasterMat());

        // ── EVGA X15 and X17 mice (X-15_Mouse.js, X-17_Mouse.js) ──

        private static IEnumerable<PluginDevice> Evga()
        {
            foreach (var (name, pid, plugin) in new[]
            {
                ("EVGA X15 Mouse", (ushort)0x2415, "EVGA/X-15_Mouse.js"),
                ("EVGA X17 Mouse", (ushort)0x240D, "EVGA/X-17_Mouse.js"),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x3842,
                    ProductId = pid,
                    Plugin = plugin,
                    Interface = 1,
                    UsagePage = 0x0008,
                    Usage = 0x004B,
                    FeatureLength = 40,
                    Lights = new[] { Zone(0, 1), Zone(1, 2), Wheel(2), Logo(3) },
                    Slots = 4,
                    Picture = EvgaPicture,
                };
        }

        /// <summary>A report per zone of the mouse's firmware, each with all
        /// four colors, an answer read after each, then an apply per zone.</summary>
        internal static IEnumerable<PluginStep> EvgaPicture(int[] colors)
        {
            for (byte zone = 1; zone <= 2; zone++)
            {
                var frame = Frame(40, 0x07, 0xEA, zone, 0x02);
                for (int i = 0; i < 4; i++)
                {
                    frame[4 + i * 4] = 0xFF;
                    PutRgb(frame, 5 + i * 4, colors[i]);
                }
                frame[20] = frame[21] = frame[22] = frame[23] = 0x01;
                yield return PluginStep.Feature(frame);
                yield return PluginStep.ReadFeature(0x07);
            }
            for (byte zone = 1; zone <= 2; zone++)
                yield return PluginStep.Feature(Frame(40, 0x07, 0xEA, zone, 0x01, 0x01, 0x01, 0x01, 0x01));
        }

        // ── Patriot Viper V380 headset (V380_Headset.js) ──

        private static IEnumerable<PluginDevice> Patriot()
        {
            yield return new PluginDevice
            {
                Name = "Patriot Viper V380 Headset",
                VendorId = 0x260D,
                ProductId = 0x2002,
                Plugin = "Patriot/V380_Headset.js",
                Interface = 3,
                Usage = 0x0001,
                OutputLength = 16,
                Lights = Edges(1),
                Slots = 1,
                Start = new[]
                {
                    PluginStep.Output(Frame(16, 0xFF, 0x02, 0x00, 0x00, 0x01, 0x00, 0x01)),
                    PluginStep.Output(Frame(16, 0xFF, 0x03, 0x01, 0x01, 0x00, 0x0A, 0x00)),
                    PluginStep.Output(Frame(16, 0xFF, 0x04, 0x00, 0x00, 0xFF, 0x00, 0xFF)),
                    PluginStep.Output(Frame(16, 0xFF, 0x01)),
                },
                Picture = colors => new[]
                {
                    PluginStep.Output(Frame(16, 0xFF, 0x03, 0x01, 0x01, 0x00, 0x0A, 0x00)),
                    PluginStep.Output(Frame(16, 0xFF, 0x04, 0x00, 0x00, G(colors[0]), B(colors[0]), R(colors[0]))),
                },
            };
        }

        // ── Speedlink ORIOS mouse, mousepad and keyboard (SpeedLink/*.js) ──

        private static readonly VendorProgram s_orios = new("ORIOS", "ORIOS");

        private const int SonixPage = 0xFF1C;
        private const int SonixUsage = 0x0092;

        private static IEnumerable<PluginDevice> Speedlink()
        {
            yield return new PluginDevice
            {
                Name = "Speedlink ORIOS RGB Gaming Mouse",
                VendorId = 0x0C45,
                ProductId = 0x5F01,
                Plugin = "SpeedLink/Speedlink_ORIOS_RGB_Gaming_Mouse.js",
                Interface = 1,
                UsagePage = SonixPage,
                Usage = SonixUsage,
                OutputLength = 64,
                Lights = Zones(6),
                Slots = 6,
                Programs = new[] { s_orios },
                Picture = colors => SonixPicture(Frame(64, 0x04, 0x00, 0x00, 0x12, 0x38), colors),
            };
            yield return new PluginDevice
            {
                Name = "Speedlink ORIOS RGB Gaming Mousepad",
                VendorId = 0x0C45,
                ProductId = 0x763D,
                Plugin = "SpeedLink/Speedlink_ORIOS_RGB_Gaming_Mousepad.js",
                Interface = 1,
                UsagePage = SonixPage,
                Usage = SonixUsage,
                OutputLength = 64,
                Lights = Edges(10),
                Slots = 10,
                Programs = new[] { s_orios },
                Picture = colors => SonixPicture(Frame(64, 0x04, 0x00, 0x00, 0x12, 0x21), colors),
            };
            yield return new PluginDevice
            {
                Name = "Speedlink ORIOS RGB Opto-Mech KB",
                VendorId = 0x0C45,
                ProductId = 0x763E,
                Plugin = PluginKeyTables.SpeedlinkOriosKb.Plugin,
                Interface = 1,
                UsagePage = SonixPage,
                Usage = SonixUsage,
                OutputLength = 64,
                Lights = PluginKeyNames.Lights(PluginKeyTables.SpeedlinkOriosKb, places: s_qwertz),
                Slots = OriosKeyboardSlots,
                Programs = new[] { s_orios },
                Picture = OriosKeyboardPicture,
            };
        }

        /// <summary>The Speedlink keyboard's German legends by place: its Y and
        /// Z trade places with a US board's.</summary>
        private static readonly Dictionary<string, ushort> s_qwertz = new() { ["Y"] = 0x1D, ["Z"] = 0x1C };

        /// <summary>The start report, the colors and the commit of the
        /// Speedlink mouse and mousepad. The plugin writes each color's red at
        /// 8 + 3i, its blue at 10 + 3i and its green at 12 + 3i.</summary>
        internal static IEnumerable<PluginStep> SonixPicture(byte[] frame, int[] colors)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                frame[8 + i * 3] = R(colors[i]);
                frame[10 + i * 3] = B(colors[i]);
                frame[12 + i * 3] = G(colors[i]);
            }
            yield return PluginStep.Output(Frame(64, 0x04, 0x01, 0x00, 0x01));
            yield return PluginStep.Output(frame);
            yield return PluginStep.Output(Frame(64, 0x04, 0x02, 0x00, 0x02));
        }

        private const int OriosKeyboardSlots = 392 / 3;

        /// <summary>Seven reports of 56 color bytes, each with the block's
        /// place in its three header bytes.</summary>
        internal static IEnumerable<PluginStep> OriosKeyboardPicture(int[] colors)
        {
            byte[] four = { 0x38, 0x38, 0x38, 0x38, 0x38, 0x38, 0x2A };
            byte[] five = { 0x00, 0x38, 0x70, 0xA8, 0xE0, 0x18, 0x50 };
            byte[] six = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01 };
            var data = new byte[392];
            for (int i = 0; i < colors.Length; i++) PutRgb(data, i * 3, colors[i]);
            for (int row = 0; row <= 6; row++)
            {
                var frame = Frame(64, 0x04, 0x00, 0x00, 0x12, four[row], five[row], six[row], 0x00);
                Array.Copy(data, row * 56, frame, 8, 56);
                yield return PluginStep.Output(frame);
            }
        }

        // ── Thermaltake Argent bungee, stands and mat (Thermal Take/*.js) ──

        /// <summary>The wire IDs of the E1 stand's 25 lights.</summary>
        private static readonly byte[] s_e1Ids =
        {
            0x00, 0x08, 0x10, 0x18, 0x01, 0x09, 0x11, 0x19, 0x21, 0x02, 0x0A, 0x12, 0x1A, 0x20, 0x22, 0x03, 0x0B, 0x13, 0x1B, 0x23, 0x04, 0x0C, 0x14, 0x1C, 0x24,
        };

        private static IEnumerable<PluginDevice> ThermaltakeArgent()
        {
            var start = new[]
            {
                PluginStep.Output(Frame(64, 0x41, 0x03)),
                PluginStep.Output(Frame(64, 0x12, 0x22)),
            };
            var sequence = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
            yield return Argent("ThermalTake Argent Mouse Bungee", 0x8020, "Thermal Take/ThermalTake_Argent_Mouse_Bungee.js", start,
                Edges(10).Append(Logo(11)).ToArray(), 13, colors => ArgentPicture(colors, Enumerable.Range(0, 13).Select(i => (byte)i).ToArray(), 13));
            yield return Argent("ThermalTake Argent E1 Headphone Stand", 0x8002, "Thermal Take/ThermalTake_E1_Headphone_Stand.js", start,
                Edges(25).Select(l => l.Address == 13 ? Logo(13) : l).ToArray(), 25, colors => ArgentPicture(colors, s_e1Ids, 15));
            yield return Argent("ThermalTake Argent MP1 Mousepad", 0x9011, "Thermal Take/Thermaltake_Argent_MP1_Mousepad.js", start,
                Edges(20), 20, colors => ArgentPicture(colors, sequence, 15));
            yield return Argent("ThermalTake Argent HS1 Headphone Stand", 0x8022, "Thermal Take/Thermaltake_HS1_Headphone_Stand.js", start,
                Edges(20).Select(l => l.Address is 8 or 11 ? Logo(l.Address) : l).ToArray(), 20, colors => ArgentPicture(colors, sequence, 15));
        }

        private static PluginDevice Argent(string name, ushort pid, string plugin, PluginStep[] start,
            IReadOnlyList<LightInfo> lights, int slots, Func<int[], IEnumerable<PluginStep>> picture) => new()
        {
            Name = name,
            VendorId = 0x264A,
            ProductId = pid,
            Plugin = plugin,
            Interface = 1,
            OutputLength = 65,
            Lights = lights,
            Slots = slots,
            Start = start,
            Picture = picture,
        };

        /// <summary>Reports of up to <paramref name="perReport"/> lights, each
        /// light its wire ID and color, as the plugins' sendColors sends them
        /// (bungee: all 13, the stands and the mat: 15 and the rest).</summary>
        internal static IEnumerable<PluginStep> ArgentPicture(int[] colors, byte[] ids, int perReport)
        {
            for (int start = 0; start < colors.Length; start += perReport)
            {
                int count = Math.Min(perReport, colors.Length - start);
                var frame = Frame(65, 0x00, 0xC0, 0x01, (byte)count, 0x00);
                for (int i = 0; i < count; i++)
                {
                    frame[5 + i * 4] = ids[start + i];
                    PutRgb(frame, 6 + i * 4, colors[start + i]);
                }
                yield return PluginStep.Output(frame);
                if (perReport == 15) yield return PluginStep.Wait(1);
            }
        }

        // ── Creative Sound BlasterX Katana (Creative_Sound_BlasterX_Katana_Soundbar.js) ──

        private static IEnumerable<PluginDevice> CreativeKatana()
        {
            yield return new PluginDevice
            {
                Name = "Sound BlasterX Katana",
                VendorId = 0x041E,
                ProductId = 0x3247,
                Plugin = "Creative Labs/Creative_Sound_BlasterX_Katana_Soundbar.js",
                Interface = 4,
                OutputLength = 65,
                Lights = Edges(7),
                Slots = 7,
                Picture = colors =>
                {
                    var frame = Frame(65, 0x00, 0x5A, 0x3A, 0x21, 0x0A, 0x00, 0x03, 0x01, 0x02);
                    for (int i = 0; i < 7; i++)
                    {
                        frame[9 + i * 4] = 0xFF;
                        frame[10 + i * 4] = B(colors[i]);
                        frame[11 + i * 4] = G(colors[i]);
                        frame[12 + i * 4] = R(colors[i]);
                    }
                    return new[] { PluginStep.Output(frame) };
                },
            };
        }

        // ── Alienware 15 R3, AW3423DW and AW3821DW (Alienware/*.js) ──

        /// <summary>The Alienware 15 R3's zone bits, by the plugin's light.</summary>
        private static readonly int[] s_alienware15Zones =
        {
            0x000001, 0x000002, 0x000004, 0x000008, 0x000020, 0x000040, 0x000080, 0x000100, 0x000400, 0x000800, 0x001000, 0x002000, 0x004000,
        };

        private static IEnumerable<PluginDevice> AlienwareDevices()
        {
            yield return new PluginDevice
            {
                Name = "Alienware 15 R3",
                VendorId = 0x187C,
                ProductId = 0x0530,
                Plugin = "Alienware/Alienware_15_R3_Laptop.js",
                Interface = 0,
                OutputLength = 12,
                // Keyboard zones 4 to 1, the two logos, the trackpad, the power
                // button, the four bars and the macro keys.
                Lights = new[]
                {
                    Zone(0, 4), Zone(1, 3), Zone(2, 2), Zone(3, 1), Logo(4), Logo(5), Edge(6), Status(7), Edge(8), Edge(9), Edge(10), Edge(11), Edge(12),
                },
                Slots = 13,
                Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                Picture = Alienware15Picture,
            };
            foreach (var (name, pid, plugin) in new[]
            {
                ("Alienware AW3423DW", (ushort)0x100B, "Alienware/Alienware_AW3423DW.js"),
                ("Alienware AW3821DW", (ushort)0x1008, "Alienware/Alienware_AW3821DW.js"),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x187C,
                    ProductId = pid,
                    Plugin = plugin,
                    Interface = 0,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 65,
                    // The middle light, the big back light, the back logo and
                    // the switch.
                    Lights = new[] { Edge(0), Edge(1), Logo(2), Status(3) },
                    Slots = 4,
                    RepaintMs = AlienwareMonitorRepaintMs,
                    Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                    Start = new[]
                    {
                        PluginStep.Output(Frame(65, 0x00, 0x92, 0x37, 0x05, 0x00, 0x51, 0x82, 0xD0, 0xF4, 0x99)),
                    },
                    Picture = AlienwareMonitorPicture,
                };
        }

        /// <summary>Monitors take a report per light, and OpenRGB waits 50 ms
        /// after each on the AW3225QF (AlienwareMonitorController.cpp:47-89),
        /// so a picture of four lights stays this far apart.</summary>
        internal const int AlienwareMonitorRepaintMs = 200;

        internal static IEnumerable<PluginStep> Alienware15Picture(int[] colors)
        {
            yield return PluginStep.Output(Frame(12, 0x02, 0x07, 0x04));
            yield return PluginStep.Wait(1);
            for (int i = 0; i < s_alienware15Zones.Length; i++)
            {
                int zone = s_alienware15Zones[i];
                yield return PluginStep.Output(Frame(12, 0x02, 0x03, (byte)(i + 1), (byte)(zone >> 16), (byte)(zone >> 8), (byte)zone,
                    R(colors[i]), G(colors[i]), B(colors[i])));
                yield return PluginStep.Output(Frame(12, 0x02, 0x04));
            }
            yield return PluginStep.Output(Frame(12, 0x02, 0x05));
        }

        /// <summary>A report per light, the rest of it FF. The plugin leaves
        /// byte 0 at FF too, where OpenRGB writes report ID 0 to the same
        /// firmware family (AlienwareMonitorController.cpp:47-89). The monitor
        /// has no numbered reports, so PadForge sends 0.</summary>
        internal static IEnumerable<PluginStep> AlienwareMonitorPicture(int[] colors)
        {
            byte[] ids = { 1, 2, 4, 8 };
            for (int i = 0; i < ids.Length; i++)
            {
                var frame = new byte[65];
                Array.Fill(frame, (byte)0xFF);
                frame[0] = 0x00;
                new byte[] { 0x92, 0x37, 0x0A, 0x00, 0x51, 0x87, 0xD0, 0x04, ids[i], R(colors[i]), G(colors[i]), B(colors[i]), 0x23, 0xB8 }
                    .CopyTo(frame, 1);
                yield return PluginStep.Output(frame);
            }
        }

        // ── ASUS PG43UQ, ROG Keris and ROG Claymore II (Asus/*.js) ──

        private static IEnumerable<PluginDevice> AsusDevices()
        {
            yield return new PluginDevice
            {
                Name = "ASUS PG43UQ",
                VendorId = 0x0B05,
                ProductId = 0x1931,
                Plugin = "Asus/ASUS_PG43UQ_Monitor.js",
                Interface = 0,
                FeatureLength = 8,
                Lights = new[] { Logo(0) },
                Slots = 1,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Picture = colors => new[]
                {
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0x20, 0x01)),
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0x30, 0x01)),
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0xA0, 0x01)),
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0x00, R(colors[0]))),
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0x01, B(colors[0]))),
                    PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, 0x02, G(colors[0]))),
                },
            };
            yield return new PluginDevice
            {
                Name = "ASUS ROG Keris",
                VendorId = 0x0B05,
                ProductId = 0x1A59,
                Plugin = "Asus/ASUS_ROG_Keris_Mouse.js",
                Interface = 0,
                OutputLength = 65,
                Lights = new[] { Logo(0), Wheel(1) },
                Slots = 2,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Picture = colors => Enumerable.Range(0, 2).Select(zone => PluginStep.Output(Frame(65,
                    0x00, 0x51, 0x28, (byte)zone, 0x00, 0x00, 0x04, R(colors[zone]), G(colors[zone]), B(colors[zone])))),
            };
            yield return new PluginDevice
            {
                Name = "ASUS ROG Claymore II",
                VendorId = 0x0B05,
                ProductId = 0x196B,
                Plugin = PluginKeyTables.ClaymoreIiRight.Plugin,
                Interface = 1,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                OutputLength = 65,
                Lights = PluginKeyNames.Lights(PluginKeyTables.ClaymoreIiRight),
                Slots = ClaymoreSlots,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Siblings = new[] { new HidSibling(2, 0xFFC0) },
                PictureWith = ClaymorePictureWith,
                HandBack = new[] { PluginStep.Output(ClaymoreOwnEffect) },
            };
        }

        /// <summary>The LED numbers of the Claymore II run to 181.</summary>
        private const int ClaymoreSlots = 182;

        /// <summary>The effect the plugin's Shutdown sets. It then saves it
        /// with 50 55, which would overwrite the effect the user stored, so
        /// PadForge leaves that out and the stored effect comes back at the
        /// next power cycle.</summary>
        private static readonly byte[] ClaymoreOwnEffect = Frame(65,
            0x00, 0x51, 0x2C, 0x04, 0x00, 0x48, 0x64, 0x00, 0x00, 0x02, 0x07, 0x0E, 0xF5, 0x00, 0xFF, 0x1D, 0x00, 0x06, 0xFF,
            0x2B, 0x00, 0xFA, 0xFF, 0x39, 0x01, 0xFF, 0x00, 0x48, 0xFF, 0xF6, 0x00, 0x56, 0xFF, 0x78, 0x07, 0x64, 0xFF, 0x00, 0x0D);

        /// <summary>The number pad comes off and goes on either side, and every
        /// LED number moves with it. The keyboard reports the side on its
        /// interface 2 as 03 81 04 00 and 1 for the right or 0 for the left, and
        /// the plugin starts from the right. A light's address stays its LED
        /// number with the pad on the right, and the frames take the other
        /// side's number for the same key, so the card does not change.</summary>
        internal static Func<int[], IEnumerable<PluginStep>> ClaymorePictureWith(IReadOnlyList<IHidReportChannel> siblings)
        {
            bool left = false;
            var buffer = new byte[65];
            return colors =>
            {
                var plate = siblings.Count > 0 ? siblings[0] : null;
                while (plate != null && plate.ReadInput(buffer, 0) > 0)
                    if (buffer[0] == 0x03 && buffer[1] == 0x81 && buffer[2] == 0x04 && buffer[3] == 0x00)
                    {
                        if (buffer[4] == 0x01) left = false;
                        else if (buffer[4] == 0x00) left = true;
                    }
                return ClaymorePicture(colors, left);
            };
        }

        /// <summary>The LED number with the pad on the left, by the key's LED
        /// number with the pad on the right. The two ISO keys are in the right
        /// table alone, and stay out of the left one.</summary>
        private static readonly Dictionary<int, int> s_claymoreLeft = PluginKeyTables.ClaymoreIiRight.Names
            .Select((name, i) => (name, i))
            .Where(k => Array.IndexOf(PluginKeyTables.ClaymoreIiLeft.Names, k.name) >= 0)
            .ToDictionary(k => PluginKeyTables.ClaymoreIiRight.Indexes[k.i],
                k => PluginKeyTables.ClaymoreIiLeft.Indexes[Array.IndexOf(PluginKeyTables.ClaymoreIiLeft.Names, k.name)]);

        /// <summary>Reports of fifteen lights, each light its LED number and
        /// color, 120 lights in all, the entries past the keyboard's filled
        /// with FF as the plugin leaves them. The fourth byte of a report counts
        /// down from 90 by 0F.</summary>
        internal static IEnumerable<PluginStep> ClaymorePicture(int[] colors, bool left = false)
        {
            var indexes = PluginKeyTables.ClaymoreIiRight.Indexes;
            var data = new byte[120 * 4];
            Array.Fill(data, (byte)0xFF);
            int at = 0;
            foreach (int index in indexes)
            {
                int number = index;
                if (left && !s_claymoreLeft.TryGetValue(index, out number)) continue;
                data[at] = (byte)number;
                PutRgb(data, at + 1, colors[index]);
                at += 4;
            }
            for (int report = 0; report < 8; report++)
            {
                var frame = Frame(65, 0x00, 0xC0, 0x81, (byte)(0x90 - 0x0F * report), 0x00);
                Array.Copy(data, report * 60, frame, 5, 60);
                yield return PluginStep.Output(frame);
            }
        }

        // ── Fnatic miniStreak Speed keyboard (Fnatic_MiniSTREAK_Speed_Keyboard.js) ──

        private static IEnumerable<PluginDevice> Fnatic()
        {
            yield return new PluginDevice
            {
                Name = "Fnatic MiniSTREAK Speed",
                VendorId = 0x2F0E,
                ProductId = 0x0103,
                Plugin = PluginKeyTables.FnaticMiniStreak.Plugin,
                Interface = 1,
                OutputLength = 65,
                // The home row's ISO key is named "ISO /" and the one beside
                // the left shift "ISO #".
                Lights = PluginKeyNames.Lights(PluginKeyTables.FnaticMiniStreak, places: new Dictionary<string, ushort>
                {
                    ["ISO /"] = 0x32,
                    ["ISO #"] = 0x64,
                }),
                Slots = 144,
                Picture = FnaticPicture,
            };
        }

        /// <summary>106 LEDs' worth of colors, nineteen to a report, each
        /// report with the running byte count. The first gives two bytes to
        /// 0F 03. A LED no key names stays white, as in the plugin.</summary>
        internal static IEnumerable<PluginStep> FnaticPicture(int[] colors)
        {
            var data = new byte[144 * 3];
            Array.Fill(data, (byte)0xFF);
            foreach (int index in PluginKeyTables.FnaticMiniStreak.Indexes)
                PutRgb(data, index * 3, colors[index]);
            int at = 0, sent = 0, left = 106;
            while (left > 0)
            {
                int leds = Math.Min(19, left);
                var frame = Frame(65, 0x00, 0x0F, 0x40, 0x01, 0x00, (byte)sent, (byte)(sent >> 8), 0x00);
                int start = 8, bytes = leds * 3;
                if (sent == 0)
                {
                    frame[8] = 0x0F;
                    frame[9] = 0x03;
                    start = 10;
                    bytes -= 2;
                }
                Array.Copy(data, at, frame, start, bytes);
                at += bytes;
                left -= leds;
                sent += leds * 3;
                yield return PluginStep.Output(frame);
            }
        }

        // ── HyperX Pulsefire FPS Pro and Haste Wireless (HyperX/*.js) ──

        private static IEnumerable<PluginDevice> HyperX()
        {
            yield return new PluginDevice
            {
                Name = "HyperX PulseFire FPS Pro",
                VendorId = 0x0951,
                ProductId = 0x1729,
                Plugin = "HyperX/HyperX_Pulsefire_FPS_Pro.js",
                Interface = 2,
                OutputLength = 65,
                Lights = new[] { Wheel(0), Logo(1) },
                Slots = 2,
                Programs = new[] { VendorPrograms.Ngenuity },
                Picture = colors =>
                {
                    var frame = Frame(65, 0x00, 0xD8, 0x00, 0x00, 0x06);
                    PutRgb(frame, 5, colors[0]);
                    PutRgb(frame, 8, colors[1]);
                    frame[11] = 0x64;
                    return new[] { PluginStep.Output(frame), PluginStep.Wait(1) };
                },
            };
            foreach (ushort pid in new ushort[] { 0x048E, 0x028E })
                yield return new PluginDevice
                {
                    Name = "HyperX Pulsefire Haste Wireless",
                    VendorId = 0x03F0,
                    ProductId = pid,
                    Plugin = "HyperX/HyperX_PusleFire_Haste_Wireless_Mouse.js",
                    Interface = 3,
                    FeatureLength = 65,
                    Lights = new[] { Wheel(0) },
                    Slots = 1,
                    Programs = new[] { VendorPrograms.Ngenuity },
                    Start = HasteStart().ToArray(),
                    Picture = HastePicture,
                };
        }

        private static IEnumerable<PluginStep> HasteExchange(byte[] frame)
        {
            yield return PluginStep.Feature(frame);
            yield return PluginStep.ReadFeature(0x00);
        }

        /// <summary>The plugin's Initialize, its mouse settings left out.</summary>
        private static IEnumerable<PluginStep> HasteStart()
        {
            byte[] Report(byte third, params (int At, byte Value)[] rest)
            {
                var frame = Frame(65, 0x00, 0x04, third);
                foreach (var (at, value) in rest) frame[at] = value;
                return frame;
            }
            var seal = new[] { (63, (byte)0xAA), (64, (byte)0x55) };
            var empty = new byte[65];
            var steps = new List<PluginStep>();
            steps.AddRange(HasteExchange(Report(0x05, (9, 0x01))));
            steps.AddRange(HasteExchange(Report(0x66, (9, 0x01))));
            steps.AddRange(HasteExchange(Report(0x67, (9, 0x01))));
            steps.AddRange(HasteExchange(Report(0x02, seal)));
            steps.AddRange(HasteExchange(Report(0x77, (13, 0x01))));
            steps.AddRange(HasteExchange(Frame(65, (1, 0x03), (63, 0xAA), (64, 0x55))));
            steps.AddRange(HasteExchange(Report(0x02, seal)));
            steps.AddRange(HasteExchange(Report(0x51, (9, 0x01))));
            steps.Add(PluginStep.Feature(empty));
            steps.AddRange(HasteExchange(Frame(65, (63, 0xAA), (64, 0x55))));
            steps.AddRange(HasteExchange(Report(0x02, seal)));
            steps.AddRange(HasteExchange(Report(0x65, (9, 0x07))));
            for (int i = 0; i < 6; i++) steps.AddRange(HasteExchange(empty));
            steps.Add(PluginStep.Feature(empty));
            steps.AddRange(HasteExchange(Report(0x02, seal)));
            return steps;
        }

        internal static IEnumerable<PluginStep> HastePicture(int[] colors)
        {
            var color = Frame(65, 0x00, 0x81);
            PutRgb(color, 2, colors[0]);
            color[9] = 0x02;
            return HasteExchange(Frame(65, (1, 0x04), (2, 0xF2), (9, 0x02)))
                .Concat(HasteExchange(color))
                .Concat(HasteExchange(new byte[65]));
        }

        // ── Razer Nari headsets and Raptor 27 monitor (Razer/*.js) ──

        private static IEnumerable<PluginDevice> RazerDevices()
        {
            foreach (var (name, pid, plugin) in new[]
            {
                ("Razer Nari Ultimate Edition", (ushort)0x051A, "Razer/Razer_Nari_Ultimate_Headset.js"),
                ("Razer Nari Essential", (ushort)0x051F, "Razer/Razer_Nari_Essential_Headset_Wired.js"),
                ("Razer Nari Essential", (ushort)0x051E, "Razer/Razer_Nari_Essential_Headset_Wireless.js"),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x1532,
                    ProductId = pid,
                    Plugin = plugin,
                    Interface = 5,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    FeatureLength = 64,
                    Lights = Edges(1),
                    Slots = 1,
                    RepaintMs = NariWaitMs,
                    Picture = colors =>
                    {
                        var frame = Frame(64, 0xFF, 0x0A, 0x00, 0xFF, 0x04, 0x12, 0xF1, 0x05, 0x72);
                        PutRgb(frame, 9, colors[0]);
                        return new[] { PluginStep.Feature(frame) };
                    },
                };
            yield return new PluginDevice
            {
                Name = "Razer Raptor 27 Inch",
                VendorId = 0x1532,
                ProductId = 0x0F12,
                Plugin = "Razer/Razer_Raptor_27_Inch_Monitor.js",
                Interface = 0,
                UsagePage = 0x000C,
                Usage = 0x0001,
                FeatureLength = 91,
                Lights = Edges(12),
                Slots = 12,
                Picture = RaptorPicture,
            };
        }

        /// <summary>The plugin waits 60 ms after each report.</summary>
        internal const int NariWaitMs = 60;

        /// <summary>The XOR of bytes 3 to 88 of a 91-byte Razer report.</summary>
        internal static byte RazerCrc(byte[] report)
        {
            byte crc = 0;
            for (int i = 3; i < 89; i++) crc ^= report[i];
            return crc;
        }

        internal static IEnumerable<PluginStep> RaptorPicture(int[] colors)
        {
            var frame = Frame(91, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x29, 0x0F, 0x03, 0x00, 0x00, 0x00, 0x00, 0x0B);
            for (int i = 0; i < 12; i++) PutRgb(frame, 14 + i * 3, colors[i]);
            frame[89] = RazerCrc(frame);
            yield return PluginStep.Feature(frame);
        }

        // ── Logitech G19 (Logitech_G19_Keyboard.js) ──

        private static IEnumerable<PluginDevice> LogitechG19()
        {
            yield return new PluginDevice
            {
                Name = "Logitech G19",
                VendorId = 0x046D,
                ProductId = 0xC229,
                Plugin = "Logitech/Logitech_G19_Keyboard.js",
                Interface = 1,
                Usage = 0x0000,
                FeatureLength = 4,
                Lights = Zones(1),
                Slots = 1,
                Picture = colors => new[] { PluginStep.Feature(new byte[] { 0x07, R(colors[0]), G(colors[0]), B(colors[0]) }) },
            };
        }

        // ── Mountain Makalu 67 (Mountain_Makalu_67_Mouse.js) ──

        private static IEnumerable<PluginDevice> MountainMakalu()
        {
            yield return new PluginDevice
            {
                Name = "Mountain Makalu 67",
                VendorId = 0x3282,
                ProductId = 0x0003,
                Plugin = "Mountain/Mountain_Makalu_67_Mouse.js",
                Interface = 1,
                UsagePage = 0xFF01,
                Usage = 0x0002,
                FeatureLength = 64,
                Lights = Edges(8),
                Slots = 8,
                Start = new[] { PluginStep.Feature(Frame(64, (0, 0xA1), (1, 0x3C), (16, 0x01))) },
                Picture = colors =>
                {
                    var frame = Frame(64, (0, 0xA1), (1, 0x3C), (16, 0x0F), (41, 0x64));
                    for (int i = 0; i < 8; i++) PutRgb(frame, 17 + i * 3, colors[i]);
                    return new[] { PluginStep.Feature(frame) };
                },
            };
        }

        // ── Cooler Master MP860 (CoolerMaster_MP860_Mat.js) ──

        /// <summary>The MP860's 19 lights by their place in the color data.</summary>
        internal static readonly int[] Mp860Places = { 23, 24, 25, 27, 0, 22, 1, 21, 2, 20, 3, 15, 8, 14, 13, 12, 11, 10, 9 };

        private static IEnumerable<PluginDevice> CoolerMasterMat()
        {
            yield return new PluginDevice
            {
                Name = "Cooler Master MP860 Mouse Pad",
                VendorId = CoolerMasterLighting.VendorId,
                ProductId = 0x006D,
                Plugin = "CoolerMaster/CoolerMaster_MP860_Mat.js",
                Interface = 1,
                OutputLength = 65,
                Lights = Mp860Places.Select(Edge).ToArray(),
                Slots = 30,
                Programs = new[] { VendorPrograms.MasterPlus },
                Picture = Mp860Picture,
            };
        }

        /// <summary>41 80, three reports of twelve colors numbered 0, 2 and 4,
        /// and 51 28 00 00 B0.</summary>
        internal static IEnumerable<PluginStep> Mp860Picture(int[] colors)
        {
            yield return PluginStep.Output(Frame(65, 0x00, 0x41, 0x80));
            int left = 30, at = 0;
            for (byte number = 0; number < 6; number += 2)
            {
                int leds = Math.Min(12, left);
                var frame = Frame(65, 0x00, 0x51, 0xA8, number, 0x00);
                for (int i = 0; i < leds; i++) PutRgb(frame, 5 + i * 3, colors[at + i]);
                at += leds;
                left -= leds;
                yield return PluginStep.Output(frame);
            }
            yield return PluginStep.Output(Frame(65, 0x00, 0x51, 0x28, 0x00, 0x00, 0xB0));
        }
    }
}
