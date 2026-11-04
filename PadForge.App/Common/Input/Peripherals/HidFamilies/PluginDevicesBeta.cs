using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>A board of a plugin that keeps many in one object: what the
    /// board calls itself, its name, and its table.</summary>
    internal sealed record PluginBoard(string Model, string Name, Func<PluginKeyTable> Table);

    /// <summary>A keyboard flashed with the SignalRGB protocol. A plugin that
    /// tells its layouts apart by the third byte of the firmware's unique
    /// identifier lists them by that byte, null for any other.</summary>
    internal sealed record PluginQmkBoard(string Name, string Plugin, ushort Vendor, ushort[] Products, Func<PluginKeyTable> Table,
        (byte? Unique, Func<PluginKeyTable> Table)[] Variants = null);

    /// <summary>What a device of Corsair's wireless family is.</summary>
    internal enum PluginCorsairKind { Keyboard, Mouse, Headset }

    /// <summary>A device of Corsair's wireless family, as a plugin lists it. The
    /// spacing is how far a color's green sits from its red, 0 for triplets.</summary>
    internal sealed record PluginCorsairDevice(ushort Product, string Name, PluginCorsairKind Kind, int Interface, int Spacing, Func<PluginKeyTable> Table);

    internal static partial class PluginCorsairTables
    {
        private static string[] Names(int count, params (int At, string Name)[] named) => PluginKeyNames.Sparse(count, named);
    }

    internal static partial class PluginQmkTables
    {
        private static string[] Names(int count, params (int At, string Name)[] named) => PluginKeyNames.Sparse(count, named);
    }

    internal static partial class PluginLibraryTables
    {
        private static string[] Names(int count, params (int At, string Name)[] named) => PluginKeyNames.Sparse(count, named);
    }
}

namespace PadForge.Common.Input.Peripherals
{
    using PadForge.Common.Input.Peripherals.HidFamilies;

    /// <summary>What a device told a session about itself: the lights and
    /// frames of the board it turned out to be.</summary>
    internal sealed record PluginVariant(IReadOnlyList<LightInfo> Lights, int Slots, Func<int[], IEnumerable<PluginStep>> Picture = null);

    internal sealed partial class PluginDeviceLighting
    {
        /// <summary>The bound on a read that asks a board what it is. The plugins
        /// wait on their reads with no bound of their own.</summary>
        internal const int AskMs = 100;

        private static IEnumerable<PluginDevice> BetaDevices()
            => TurtleBeach().Concat(RoccatVulcanIIMiniAir()).Concat(BloodyA70x()).Concat(AlienwareAw558()).Concat(RogSpotlight())
                .Concat(HyperXHaste2()).Concat(Level20Desk()).Concat(CoolerMasterCk72x()).Concat(OemWireless())
                .Concat(Royuan4002()).Concat(Sonix2()).Concat(EVisionBoards()).Concat(ByTech()).Concat(Nuvoton()).Concat(Kbm())
                .Concat(Xiaohua()).Concat(Nuvoton2()).Concat(Sinowealth2()).Concat(AsusKeyboards()).Concat(AsusMice())
                .Concat(AlienwareMonitorsGen1()).Concat(AsusMonitors()).Concat(ThermaltakeDasher()).Concat(SteelSeriesLaptops())
                .Concat(ApexProMiniGen3()).Concat(AsusOmni());

        /// <summary>The lights and LED count of a table.</summary>
        internal static PluginVariant VariantOf(PluginKeyTable table, Func<int[], IEnumerable<PluginStep>> picture = null)
            => new(PluginKeyNames.Lights(table), table.Indexes.Max() + 1, picture);

        // ── Turtle Beach's Roccat-made devices (Turtle Beach/*.js) ──

        private static IEnumerable<PluginDevice> TurtleBeach()
        {
            yield return new PluginDevice
            {
                Name = "Turtle Beach Kone XP Air Dock",
                VendorId = RoccatVulcanLighting.TurtleBeachVendorId,
                ProductId = 0x5019,
                Plugin = "Turtle Beach/Turtle_Beach_Kone_XP_Air_Dock.js",
                Programs = VendorPrograms.TurtleBeach,
                Interface = 0,
                Usage = 0x0001,
                FeatureLength = 17,
                Lights = Edges(4),
                Slots = 4,
                Start = KoneDockStart(),
                Picture = KoneDockPicture,
            };
            foreach (var (name, pid, plugin, id) in new (string, ushort, string, byte)[]
            {
                ("Turtle Beach Kone XP Air - Wired", 0x5015, "Turtle_Beach_Kone_XP_Air_Wired_Mouse.js", 0x00),
                ("Turtle Beach Kone XP Air", 0x5017, "Turtle_Beach_Kone_XP_Air_Mouse.js", 0x01),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = RoccatVulcanLighting.TurtleBeachVendorId,
                    ProductId = pid,
                    Plugin = "Turtle Beach/" + plugin,
                    Programs = VendorPrograms.TurtleBeach,
                    Interface = 2,
                    Usage = 0xFF00,
                    FeatureLength = 30,
                    Lights = new[] { Wheel(0), Edge(1), Edge(2), Edge(3), Edge(4) },
                    Slots = 5,
                    Start = KoneXpStart(id),
                    Picture = colors => KoneXpPicture(id, colors),
                };
        }

        // ── Roccat and Turtle Beach Vulcan II Mini Air ──

        private static IEnumerable<PluginDevice> RoccatVulcanIIMiniAir()
        {
            foreach (var (name, vendor, pid, programs) in new (string, ushort, ushort, IReadOnlyList<VendorProgram>)[]
            {
                ("Roccat Vulcan II Mini Air", RoccatVulcanLighting.RoccatVendorId, 0x6978, VendorPrograms.Roccat),
                ("Roccat Vulcan II Mini Air", RoccatVulcanLighting.RoccatVendorId, 0x3AA9, VendorPrograms.Roccat),
                ("Turtle Beach Vulcan II Mini Air", RoccatVulcanLighting.TurtleBeachVendorId, 0x501E, VendorPrograms.TurtleBeach),
                ("Turtle Beach Vulcan II Mini Air", RoccatVulcanLighting.TurtleBeachVendorId, 0x5021, VendorPrograms.TurtleBeach),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = vendor,
                    ProductId = pid,
                    Plugin = PluginKeyTables.VulcanIIMiniAir.Plugin,
                    Programs = programs,
                    Interface = 1,
                    UsagePage = 0x00FF,
                    Usage = 0x0001,
                    OutputLength = 65,
                    Lights = PluginKeyNames.Lights(PluginKeyTables.VulcanIIMiniAir),
                    Slots = VulcanMiniAirSlots,
                    Picture = VulcanMiniAirPicture,
                };
        }

        /// <summary>Five reports of zone 1 and two of zone 2, fifteen LEDs each.</summary>
        internal const int VulcanMiniAirSlots = 7 * 15;

        internal static IEnumerable<PluginStep> VulcanMiniAirPicture(int[] colors)
        {
            int led = 0;
            for (byte zone = 1; zone <= 2; zone++)
                for (byte packet = 0; packet < (zone == 2 ? 2 : 5); packet++)
                {
                    var frame = Frame(65, 0x00, 0x07, 0x31, 0x33, zone, packet);
                    for (int i = 0; i < 15; i++, led++) PutRgb(frame, 6 + i * 3, colors[led]);
                    yield return PluginStep.Output(frame);
                    yield return PluginStep.Wait(5);
                }
        }

        // ── A4Tech Bloody A70x (A4Tech/A4Tech_Controller.js) ──

        /// <summary>The plugin sends OpenRGB's Bloody mouse reports to eight LEDs,
        /// on a collection of its own. The release is the same family's.</summary>
        private static IEnumerable<PluginDevice> BloodyA70x()
        {
            foreach (ushort pid in new ushort[] { 0x1CCA, 0x11A1, 0x7452 })
                yield return new PluginDevice
                {
                    Name = "A4Tech Bloody A70x",
                    VendorId = 0x09DA,
                    ProductId = pid,
                    Plugin = "A4Tech/A4Tech_Controller.js",
                    Interface = 2,
                    UsagePage = 0xFF32,
                    Usage = 0x04C0,
                    FeatureLength = A4TechLighting.PacketSize,
                    // The wheel at LED 0, then the buttons, the sides and the bottom.
                    Lights = new[] { Wheel(0), Edge(1), Edge(2), Edge(3), Edge(4), Edge(5), Edge(6), Edge(7) },
                    Slots = 8,
                    Start = A4TechLighting.ControlFrames(0x01).Select(PluginStep.Feature).ToArray(),
                    Picture = colors =>
                    {
                        var frame = Frame(A4TechLighting.PacketSize, 0x07, 0x03, 0x06, 0x02);
                        for (int i = 0; i < 8; i++) PutRgb(frame, 8 + i * 3, colors[i]);
                        return new[] { PluginStep.Feature(frame) };
                    },
                    HandBack = A4TechLighting.ControlFrames(0x00).Select(PluginStep.Feature).ToArray(),
                };
        }

        // ── Alienware AW558 (Alienware/Alienware_AW558_Mouse.js) ──

        private static IEnumerable<PluginDevice> AlienwareAw558()
        {
            yield return new PluginDevice
            {
                Name = "Alienware AW558",
                VendorId = 0x04CA,
                ProductId = 0x00A5,
                Plugin = "Alienware/Alienware_AW558_Mouse.js",
                Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                Interface = 1,
                UsagePage = 0x000C,
                Usage = 0x0001,
                OutputLength = 64,
                Lights = new[] { Logo(2), Edge(1), Edge(0) },
                Slots = 3,
                // The plugin's first byte here is the report ID its color frames
                // leave at 00, and it ignores what the write returns.
                Start = new[] { PluginStep.Output(Frame(64, 0x0D, 0x5A, 0x21, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA5)) },
                Picture = colors =>
                {
                    var frame = Frame(64, 0x00, 0x0D, 0x5A, 0x03, 0x00);
                    for (int i = 0; i < 3; i++) PutRgb(frame, 5 + i * 3, colors[i]);
                    frame[14] = 0xA5;
                    return new[] { PluginStep.Output(frame), PluginStep.Wait(1) };
                },
            };
        }

        // ── ASUS ROG Spotlight (Asus/ASUS_ROG_Spotlight.js) ──

        private static IEnumerable<PluginDevice> RogSpotlight()
        {
            yield return new PluginDevice
            {
                Name = "ASUS ROG Spotlight",
                VendorId = 0x0B05,
                ProductId = 0x1881,
                Plugin = "Asus/ASUS_ROG_Spotlight.js",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 0,
                UsagePage = 0x00FF,
                Usage = 0x0001,
                OutputLength = 65,
                Lights = Edges(1),
                Slots = 1,
                Picture = colors =>
                {
                    var first = Frame(65, 0xEC, 0x3B, 0x00, 0xFF);
                    PutRgb(first, 4, colors[0]);
                    var second = Frame(65, 0xEC, 0x40, 0x00, 0xEE, 0x01);
                    PutRgb(second, 5, colors[0]);
                    new byte[] { 0x00, 0x00, 0x84, 0xF3, 0x12, 0x03, 0x70, 0x12, 0x3B, 0x75, 0x47, 0xA1 }.CopyTo(second, 8);
                    return new[] { PluginStep.Output(first), PluginStep.Output(second), PluginStep.Wait(1) };
                },
            };
        }

        // ── HyperX Pulsefire Haste 2 (HyperX/HyperX_Pulsefire_Haste_2_Mouse.js) ──

        private static IEnumerable<PluginDevice> HyperXHaste2()
        {
            foreach (var (name, pid, page, usage) in new (string, ushort, int, int)[]
            {
                ("HyperX Pulsefire Haste 2", 0x0B97, 0xFF00, 0x0001),
                ("HyperX Pulsefire Haste 2", 0x0D97, 0xFF00, 0x0001),
                ("HyperX Pulsefire Haste 2", 0x0F98, 0xFF00, 0x0001),
                ("HyperX Pulsefire Haste 2 Mini", 0x0DA0, 0xFF90, 0xFF00),
                ("HyperX Pulsefire Haste 2 Mini", 0x0BA0, 0xFF90, 0xFF00),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x03F0,
                    ProductId = pid,
                    Plugin = "HyperX/HyperX_Pulsefire_Haste_2_Mouse.js",
                    Programs = new[] { VendorPrograms.Ngenuity },
                    Interface = 2,
                    UsagePage = page,
                    Usage = usage,
                    OutputLength = 65,
                    Lights = new[] { Wheel(0) },
                    Slots = 1,
                    Picture = colors =>
                    {
                        var frame = Frame(65, 0x44, 0x02, 0x00, 0x00);
                        PutRgb(frame, 4, colors[0]);
                        return new[] { PluginStep.Output(Frame(65, 0x44, 0x01, 0x01)), PluginStep.Output(frame) };
                    },
                };
        }

        // ── Thermaltake Level 20 desk (Thermal Take/ThermalTake_Level20_Desk.js) ──

        internal const int Level20ReportLength = 839;

        private static IEnumerable<PluginDevice> Level20Desk()
        {
            yield return new PluginDevice
            {
                Name = "Thermaltake Level 20 Desk",
                VendorId = 0x264A,
                ProductId = 0x07D1,
                Plugin = PluginKeyTables.Level20Desk.Plugin,
                Programs = new[] { VendorPrograms.ThermaltakeItake },
                Interface = 0,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                OutputLength = Level20ReportLength,
                Lights = PluginKeyNames.Lights(PluginKeyTables.Level20Desk),
                Slots = PluginKeyTables.Level20Desk.Indexes.Max() + 1,
                Start = new[] { PluginStep.Output(Frame(Level20ReportLength, 0x00, 0xFE, 0x33)) },
                Picture = Level20Picture,
            };
        }

        /// <summary>Green, red, blue, as the plugin sends them.</summary>
        internal static IEnumerable<PluginStep> Level20Picture(int[] colors)
        {
            var frame = Frame(Level20ReportLength, 0x00, 0x32, 0x52, 0x04, 0x24);
            for (int led = 0; led < colors.Length; led++)
            {
                frame[5 + led * 3] = G(colors[led]);
                frame[6 + led * 3] = R(colors[led]);
                frame[7 + led * 3] = B(colors[led]);
            }
            yield return PluginStep.Output(frame);
        }

        // ── Cooler Master CK720 and CK721 (CoolerMaster/*.js) ──

        private static IEnumerable<PluginDevice> CoolerMasterCk72x()
        {
            foreach (var (name, pid, table) in new (string, ushort, PluginKeyTable)[]
            {
                ("Cooler Master CK720", 0x016B, PluginKeyTables.Ck720),
                ("Cooler Master CK721", 0x016D, PluginKeyTables.Ck721),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = CoolerMasterLighting.VendorId,
                    ProductId = pid,
                    Plugin = table.Plugin,
                    Programs = new[] { VendorPrograms.MasterPlus },
                    Interface = 1,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 65,
                    Lights = PluginKeyNames.Lights(table),
                    Slots = table.Indexes.Max() + 1,
                    Start = new[]
                    {
                        PluginStep.Output(Frame(65, 0x00, 0x80, 0x01, 0x01, 0x02, 0x02)),
                        PluginStep.Output(Frame(65, 0x00, 0x01, 0x81, 0xC8, 0x00, 0x01, 0x00, 0x12)),
                    },
                    Picture = Ck72xPicture,
                };
        }

        internal static IEnumerable<PluginStep> Ck72xPicture(int[] colors)
        {
            for (int led = 0; led < colors.Length; led += 18)
            {
                int count = Math.Min(18, colors.Length - led);
                var frame = Frame(65, 0x00, 0x56, 0x42, 0x00, 0x00, 0x02, (byte)count, (byte)led, 0x00);
                for (int i = 0; i < count; i++) PutRgb(frame, 9 + i * 3, colors[led + i]);
                yield return PluginStep.Output(frame);
            }
        }

        // ── OEM wireless keyboard (OEM/OEM_Keyboard_Wireless_Controller.js) ──

        private static IEnumerable<PluginDevice> OemWireless()
        {
            yield return new PluginDevice
            {
                Name = "OEM Wireless Keyboard",
                VendorId = 0x3554,
                ProductId = 0xFA09,
                Plugin = "OEM/OEM_Keyboard_Wireless_Controller.js",
                Interface = 1,
                UsagePage = 0xFF02,
                Usage = 0x0002,
                OutputLength = 20,
                // The plugin says the firmware takes one color for the board.
                Lights = Zones(1),
                Slots = 1,
                Picture = colors =>
                {
                    var frame = Frame(20, 0x13, 0x88, 0x01, 0x00, 0x23);
                    PutRgb(frame, 5, colors[0]);
                    int sum = 0;
                    for (int i = 0; i < 19; i++) sum += frame[i];
                    frame[19] = (byte)sum;
                    return new[] { PluginStep.Output(frame), PluginStep.Wait(1) };
                },
            };
        }

        // ── Royuan 0x4002 (Royuan/Royuan_Keyboard_Controller.js) ──

        private static IEnumerable<PluginDevice> Royuan4002()
        {
            yield return new PluginDevice
            {
                Name = "Royuan keyboard",
                VendorId = 0x3151,
                ProductId = 0x4002,
                Plugin = "Royuan/Royuan_Keyboard_Controller.js",
                Interface = 0,
                UsagePage = 0x0001,
                Usage = 0x0006,
                FeatureLength = 65,
                // The plugin says the firmware takes one color in direct mode.
                Lights = Zones(1),
                Slots = 1,
                HeldByWindows = true,
                Start = new[] { PluginStep.Feature(Frame(65, 0x00, 0x07, 0x15, 0x04, 0x04, 0x07, 0x00, 0x00, 0x00, 0xD4)) },
                Picture = colors =>
                {
                    var frame = Frame(65, 0x00, 0x0E);
                    PutRgb(frame, 2, colors[0]);
                    int crc = 255;
                    for (int i = 1; i < 5; i++) crc -= frame[i];
                    frame[8] = (byte)crc;
                    return new[] { PluginStep.Feature(frame) };
                },
            };
        }

        // ── Alienware's first monitors (Alienware/Alienware_Monitor_Gen1_Controller.js) ──

        private static IEnumerable<PluginDevice> AlienwareMonitorsGen1()
        {
            foreach (var (name, pid) in new (string, ushort)[]
            {
                ("Alienware AW3418DW", 0x274A),
                ("Alienware AW3418HW", 0x274B),
                ("Alienware AW2518H", 0x274C),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x0424,
                    ProductId = pid,
                    Plugin = "Alienware/Alienware_Monitor_Gen1_Controller.js",
                    Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                    Interface = 0,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 65,
                    // The back, the stand, the bottom and the switch, each its LED's bit.
                    Lights = new[] { Edge(0), Edge(1), Edge(2), Status(3) },
                    Slots = 4,
                    RepaintMs = AlienwareMonitorRepaintMs,
                    Start = new[] { PluginStep.Output(FilledFrame(0x95, 0x00, 0x00, 0x00)) },
                    Picture = AlienwareGen1Picture,
                };
        }

        /// <summary>A 65-byte frame of FF, the plugin's own filler, its report ID
        /// byte included, with the bytes given from byte 1.</summary>
        private static byte[] FilledFrame(params byte[] bytes)
        {
            var frame = new byte[65];
            Array.Fill(frame, (byte)0xFF);
            bytes.CopyTo(frame, 1);
            return frame;
        }

        internal static IEnumerable<PluginStep> AlienwareGen1Picture(int[] colors)
        {
            for (int i = 0; i < 4; i++)
                yield return PluginStep.Output(FilledFrame(0x92, 0x48, 0x05, 0x00, 0x04, (byte)(1 << i), R(colors[i]), G(colors[i]), B(colors[i])));
        }

        // ── ASUS monitors (Asus/ASUS_Monitor_Controller.js) ──

        private static IEnumerable<PluginDevice> AsusMonitors()
        {
            foreach (var (name, pid, lights) in new (string, ushort, LightInfo[])[]
            {
                ("ASUS PG27UQ", 0x186E, new[] { Edge(0), Edge(1), Edge(2) }),
                ("ASUS PG248QP", 0x1AC5, new[] { Logo(0) }),
                ("ASUS PG32UQX", 0x1941, new[] { Edge(0), Edge(1), Edge(2), Edge(3), Logo(4) }),
                ("ASUS PG35VQ", 0x18D4, new[] { Edge(0), Edge(1), Edge(2), Edge(3), Logo(4) }),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x0B05,
                    ProductId = pid,
                    Plugin = "Asus/ASUS_Monitor_Controller.js",
                    Programs = new[] { VendorPrograms.ArmouryCrate },
                    Interface = 0,
                    FeatureLength = 8,
                    Lights = lights,
                    Slots = lights.Length,
                    Picture = AsusMonitorPicture,
                };
        }

        /// <summary>Three opening commands, then each LED's red, blue and green,
        /// each its own report, in that order.</summary>
        internal static IEnumerable<PluginStep> AsusMonitorPicture(int[] colors)
        {
            static PluginStep Command(byte a, byte b) => PluginStep.Feature(Frame(8, 0x03, 0x02, 0xA1, 0x80, a, b));
            yield return Command(0x20, 0x01);
            yield return Command(0x30, 0x01);
            yield return Command(0xA0, 0x01);
            for (int i = 0; i < colors.Length; i++)
            {
                yield return Command((byte)(i * 3), R(colors[i]));
                yield return Command((byte)(i * 3 + 1), B(colors[i]));
                yield return Command((byte)(i * 3 + 2), G(colors[i]));
            }
        }

        // ── Thermaltake Dasher (Thermal Take/Thermaltake_Dasher_RGB_Mousepad.js) ──

        /// <summary>The plugin, on a branch beta has not taken up, keeps adding
        /// to one array in its loop, so each of its reports carries the first
        /// light's color. Each here carries its own light's, the report's form.</summary>
        private static IEnumerable<PluginDevice> ThermaltakeDasher()
        {
            yield return new PluginDevice
            {
                Name = "Thermaltake Dasher",
                VendorId = 0x264A,
                ProductId = 0x9008,
                Plugin = "Thermal Take/Thermaltake_Dasher_RGB_Mousepad.js",
                Interface = 1,
                UsagePage = 0x0001,
                Usage = 0x0006,
                FeatureLength = 10,
                HeldByWindows = true,
                Lights = Edges(4),
                Slots = 4,
                Picture = colors =>
                {
                    var steps = new List<PluginStep>();
                    for (int i = 0; i < 4; i++)
                    {
                        var frame = Frame(10, 0x0C, 0xC4, 0x03, 0x01, (byte)i);
                        PutRgb(frame, 5, colors[i]);
                        steps.Add(PluginStep.Feature(frame));
                    }
                    steps.Add(PluginStep.Wait(1));
                    return steps;
                },
            };
        }

        // ── MSI laptops' SteelSeries keyboards (Steelseries/Steelseries_Keyboard_Laptop_Controller.js) ──

        private static IEnumerable<PluginDevice> SteelSeriesLaptops()
        {
            foreach (var (name, pid, number, model) in new (string, ushort, int, string)[]
            {
                ("MSI GE68 HX 13V", 0x113A, 1, "4410"),
                ("MSI GE66 Raider", 0x113C, 0, "4412"),
            })
            {
                var board = BoardNamed(PluginLibraryTables.SteelSeriesLaptop, model);
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x1038,
                    ProductId = pid,
                    Plugin = "Steelseries/Steelseries_Keyboard_Laptop_Controller.js",
                    Programs = new[] { VendorPrograms.SteelSeriesGG },
                    Interface = number,
                    UsagePage = 0xFFC0,
                    Usage = 0x0001,
                    FeatureLength = 525,
                    // The report ends at LED 173, and the plugin's frame stops
                    // with it, so the higher numbers it lists never go out.
                    BuildLights = () => PluginKeyNames.Lights(board.Table()).Where(l => l.Address <= SteelSeriesLaptopLastLed).ToArray(),
                    Slots = 256,
                    Start = new[] { PluginStep.Feature(Frame(525, 0x00, 0x4B)) },
                    Picture = colors => SteelSeriesLaptopPicture(board.Table().Indexes, colors),
                };
            }
        }

        internal const int SteelSeriesLaptopLastLed = (525 - 5) / 3 - 1;

        /// <summary>Each LED's color at three times its number. The fourth byte is
        /// the plugin's LED count over four.</summary>
        internal static IEnumerable<PluginStep> SteelSeriesLaptopPicture(int[] leds, int[] colors)
        {
            var frame = Frame(525, 0x00, 0x0E, 0x00, (byte)(leds.Length / 4), 0x00);
            foreach (int led in leds)
                if (5 + led * 3 + 2 < frame.Length) PutRgb(frame, 5 + led * 3, colors[led]);
            yield return PluginStep.Feature(frame);
            yield return PluginStep.Wait(1);
            yield return PluginStep.Feature(Frame(525, 0x00, 0x0D, 0x00, 0x02));
        }

        // ── Apex Pro Mini Gen 3 (Steelseries/Steelseries_Keyboard_Controller.js) ──

        private static IEnumerable<PluginDevice> ApexProMiniGen3()
        {
            var board = BoardNamed(PluginLibraryTables.SteelSeriesKeyboard, "5704");
            yield return new PluginDevice
            {
                Name = "SteelSeries Apex Pro Mini Gen 3",
                VendorId = 0x1038,
                ProductId = 0x1648,
                Plugin = "Steelseries/Steelseries_Keyboard_Controller.js",
                Programs = new[] { VendorPrograms.SteelSeriesGG },
                Interface = 1,
                UsagePage = 0xFFC0,
                Usage = 0x0001,
                FeatureLength = 642,
                BuildLights = () => PluginKeyNames.Lights(board.Table()),
                Slots = 256,
                Picture = colors =>
                {
                    var leds = board.Table().Indexes;
                    var frame = Frame(642, 0x00, 0x40, (byte)leds.Length);
                    for (int i = 0; i < leds.Length; i++)
                    {
                        frame[3 + i * 4] = (byte)leds[i];
                        PutRgb(frame, 4 + i * 4, colors[leds[i]]);
                    }
                    return new[] { PluginStep.Feature(frame) };
                },
            };
        }

        // ── ASUS Omni dongle (Asus/Omni Controller/*.js) ──

        /// <summary>One dongle holds a keyboard or a mouse, each on its own
        /// collection, so each row asks if its kind is there. The plugin tries
        /// four times, 50 ms apart.</summary>
        private static IEnumerable<PluginDevice> AsusOmni()
        {
            yield return new PluginDevice
            {
                Name = "ASUS Omni keyboard",
                VendorId = 0x0B05,
                ProductId = 0x1ACE,
                Plugin = "Asus/Omni Controller/ASUS_Omni_Device.js",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 2,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                OutputLength = 64,
                BuildLights = FirstBoardLights(PluginLibraryTables.AsusOmniKeyboard),
                Slots = 256,
                Answers = (io, wait) => OmniConnected(io, wait, Frame(64, 0x02, 0x12, 0x03)),
                AskVariant = (io, _, wait) =>
                {
                    var board = BoardNamed(PluginLibraryTables.AsusOmniKeyboard, OmniModel(io, wait, Frame(64, 0x02, 0x12, 0x14, 0x02)));
                    if (board == null) return null;
                    var table = board.Table();
                    return VariantOf(table, colors => OmniKeyboardPicture(table.Indexes, colors));
                },
            };
            yield return new PluginDevice
            {
                Name = "ASUS Omni mouse",
                VendorId = 0x0B05,
                ProductId = 0x1ACE,
                Plugin = "Asus/Omni Controller/ASUS_Omni_Device.js",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 2,
                UsagePage = 0xFF01,
                Usage = 0x0001,
                OutputLength = 64,
                Lights = new[] { Wheel(0) },
                Slots = 1,
                Answers = (io, wait) => OmniConnected(io, wait, Frame(64, 0x03, 0x12, 0x00, 0x02)),
                Start = new[] { PluginStep.Output(Frame(65, 0x03, 0x51, 0x28, 0x03, 0x00, 0x02, 0x64, 0x02)) },
                AskVariant = (io, _, wait) => OmniMice.TryGetValue(OmniModel(io, wait, Frame(64, 0x03, 0x12, 0x12, 0x01)) ?? "", out var lights)
                    ? new PluginVariant(lights, lights.Length, OmniMousePicture)
                    : null,
            };
        }

        /// <summary>The mice the Omni plugin's library names, by the serial they
        /// report, each light at its place in the list.</summary>
        private static readonly Dictionary<string, LightInfo[]> OmniMice = new()
        {
            ["R3MPGDD21800"] = new[] { Wheel(0) },
            ["SAMPGDD00EAH"] = new[] { Logo(0), Wheel(1), Edge(2) },
            ["R9MPGDD81805"] = new[] { Wheel(0) },
            ["E503FF3290C8"] = new[] { Logo(0), Wheel(1) },
        };

        internal static bool OmniConnected(IHidReportChannel io, Action<int> wait, byte[] ask)
        {
            var answer = new byte[Math.Max(io.InputReportLength, 64)];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (!io.WriteOutput(ask)) return false;
                wait?.Invoke(10);
                if (io.ReadInput(answer, AskMs) > 0 && answer[5] == 1) return true;
                wait?.Invoke(50);
            }
            return false;
        }

        /// <summary>The serial number at bytes 5 to 16 of the answer.</summary>
        internal static string OmniModel(IHidReportChannel io, Action<int> wait, byte[] ask)
        {
            if (!io.WriteOutput(ask)) return null;
            wait?.Invoke(10);
            var answer = new byte[Math.Max(io.InputReportLength, 64)];
            if (io.ReadInput(answer, AskMs) <= 0) return null;
            return Encoding.ASCII.GetString(answer, 5, 12).Replace("\0", "").Trim();
        }

        /// <summary>Fourteen LEDs to a report, each its number and color. The count
        /// is the LEDs in this report, where the plugged-in form counts what is left.</summary>
        internal static IEnumerable<PluginStep> OmniKeyboardPicture(int[] leds, int[] colors)
        {
            for (int at = 0; at < leds.Length; at += 14)
            {
                int count = Math.Min(14, leds.Length - at);
                var frame = Frame(65, 0x02, 0xC0, 0x81, (byte)count, 0x00);
                for (int i = 0; i < count; i++)
                {
                    frame[5 + i * 4] = (byte)leds[at + i];
                    PutRgb(frame, 6 + i * 4, colors[leds[at + i]]);
                }
                yield return PluginStep.Output(frame);
            }
        }

        internal static IEnumerable<PluginStep> OmniMousePicture(int[] colors)
        {
            var frame = Frame(64, 0x03, 0x51, 0x29, 0xFF, 0x00, 0x00);
            for (int i = 0; i < colors.Length; i++) PutRgb(frame, 6 + i * 3, colors[i]);
            yield return PluginStep.Output(frame);
        }

        // ── The boards told apart by a name they report ──

        /// <summary>The board a plugin's table names, or null: the plugins light
        /// nothing for a name they do not know.</summary>
        private static PluginBoard BoardNamed(IReadOnlyList<PluginBoard> boards, string model)
            => boards.FirstOrDefault(b => string.Equals(b.Model, model, StringComparison.Ordinal));

        /// <summary>A collection's lights before its board has said what it is.</summary>
        private static Func<IReadOnlyList<LightInfo>> FirstBoardLights(IReadOnlyList<PluginBoard> boards)
            => () => PluginKeyNames.Lights(boards[0].Table());

        // ── SONIX, second plugin (Sonix/SONIX_Keyboard_Controller_2.js) ──

        private static IEnumerable<PluginDevice> Sonix2()
        {
            foreach (ushort pid in new ushort[] { 0x8006, 0x8009, 0x800A, 0x80B1, 0x8801 })
                yield return new PluginDevice
                {
                    Name = "SONIX keyboard",
                    VendorId = 0x0C45,
                    ProductId = pid,
                    Plugin = "Sonix/SONIX_Keyboard_Controller_2.js",
                    Interface = 3,
                    UsagePage = 0xFF13,
                    Usage = 0x0001,
                    FeatureLength = 65,
                    BuildLights = FirstBoardLights(PluginLibraryTables.Sonix2),
                    Slots = 256,
                    AskVariant = (io, _, _) =>
                    {
                        var board = BoardNamed(PluginLibraryTables.Sonix2, io.ProductString);
                        return board == null ? null : VariantOf(board.Table(), Sonix2Picture);
                    },
                };
        }

        /// <summary>Each LED's number and color, sixty-four bytes to a report,
        /// between the plugin's opening and closing reports.</summary>
        internal static IEnumerable<PluginStep> Sonix2Picture(int[] colors)
        {
            yield return PluginStep.Feature(Frame(65, 0x00, 0x04, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08));
            yield return PluginStep.ReadFeature(0x00);
            var data = new byte[colors.Length * 4];
            for (int led = 0; led < colors.Length; led++)
            {
                data[led * 4] = (byte)led;
                PutRgb(data, led * 4 + 1, colors[led]);
            }
            for (int at = 0; at < data.Length; at += 64)
            {
                var frame = new byte[65];
                Array.Copy(data, at, frame, 1, Math.Min(64, data.Length - at));
                yield return PluginStep.Feature(frame);
                yield return PluginStep.Wait(2);
            }
            yield return PluginStep.Feature(new byte[65]);
            yield return PluginStep.Feature(Frame(65, 0x00, 0x04, 0x02));
            yield return PluginStep.ReadFeature(0x00);
        }

        // ── EVision, by product string (EVision/EVision_Keyboard_Controller.js) ──

        private static IEnumerable<PluginDevice> EVisionBoards()
        {
            foreach (ushort pid in new ushort[] { 0x5055, 0x5088 })
                yield return new PluginDevice
                {
                    Name = "EVision keyboard",
                    VendorId = 0x320F,
                    ProductId = pid,
                    Plugin = "EVision/EVision_Keyboard_Controller.js",
                    UsagePage = 0xFF1C,
                    Usage = 0x0092,
                    OutputLength = 64,
                    BuildLights = FirstBoardLights(PluginLibraryTables.EVision),
                    Slots = 256,
                    AskVariant = (io, _, _) =>
                    {
                        var board = BoardNamed(PluginLibraryTables.EVision, io.ProductString);
                        return board == null ? null : VariantOf(board.Table(), EVisionPicture);
                    },
                };
        }

        /// <summary>Fifty-six bytes of color to a report, under the checksum the
        /// plugin computes: the bytes' sum plus an amount that changes at the sixth
        /// report (calculateChecksum, EVision_Keyboard_Controller.js:165-174).</summary>
        internal static IEnumerable<PluginStep> EVisionPicture(int[] colors)
        {
            const int Size = 56;
            var data = new byte[colors.Length * 3];
            for (int led = 0; led < colors.Length; led++) PutRgb(data, led * 3, colors[led]);
            for (int index = 0; index * Size < data.Length; index++)
            {
                var frame = new byte[64];
                int count = Math.Min(Size, data.Length - index * Size);
                Array.Copy(data, index * Size, frame, 8, count);
                int sum = 0;
                for (int i = 0; i < count; i++) sum += frame[8 + i];
                int checksum = sum + (index >= 5 ? (index - 5) * Size + 99 : index * Size + 74);
                int sent = index * Size;
                frame[0] = 0x04;
                frame[1] = (byte)checksum;
                frame[2] = (byte)(checksum >> 8);
                frame[3] = 0x12;
                frame[4] = Size;
                frame[5] = (byte)sent;
                frame[6] = (byte)(sent >> 8);
                yield return PluginStep.Output(frame);
                yield return PluginStep.Wait(1);
            }
        }

        // ── BY Tech (BYTech/BYTech_Keyboard_Controller.js) ──

        private static IEnumerable<PluginDevice> ByTech()
        {
            yield return new PluginDevice
            {
                Name = "BY Tech keyboard",
                VendorId = 0x372E,
                ProductId = 0x103E,
                Plugin = "BYTech/BYTech_Keyboard_Controller.js",
                Interface = 2,
                UsagePage = 0xFF60,
                Usage = 0x0061,
                OutputLength = 64,
                BuildLights = FirstBoardLights(PluginLibraryTables.ByTech),
                Slots = 256,
                AskVariant = (io, _, _) =>
                {
                    var board = BoardNamed(PluginLibraryTables.ByTech, io.ProductString);
                    if (board == null) return null;
                    var table = board.Table();
                    return VariantOf(table, colors => ByTechPicture(table.Indexes, colors));
                },
            };
        }

        /// <summary>The firmware takes colors with the LEDs that show them. The
        /// plugin sends at most forty colors, merging close ones once there are
        /// more (compressColors), and drops what is left.</summary>
        internal static IEnumerable<PluginStep> ByTechPicture(int[] leds, int[] colors)
        {
            const int Most = 40, Close = 15, Size = 56;
            var groups = new List<(int Color, List<byte> Leds)>();
            foreach (int led in leds)
            {
                int at = groups.FindIndex(g => g.Color == colors[led]);
                if (at < 0)
                {
                    groups.Add((colors[led], new List<byte>()));
                    at = groups.Count - 1;
                }
                groups[at].Leds.Add((byte)led);
            }
            if (groups.Count > Most)
            {
                var merged = new List<(int Color, List<byte> Leds)>();
                var used = new bool[groups.Count];
                for (int i = 0; i < groups.Count && merged.Count < Most; i++)
                {
                    if (used[i]) continue;
                    var main = (groups[i].Color, new List<byte>(groups[i].Leds));
                    for (int j = i + 1; j < groups.Count; j++)
                    {
                        if (used[j]) continue;
                        int distance = Math.Abs(R(groups[i].Color) - R(groups[j].Color)) + Math.Abs(G(groups[i].Color) - G(groups[j].Color))
                            + Math.Abs(B(groups[i].Color) - B(groups[j].Color));
                        if (distance > Close) continue;
                        main.Item2.AddRange(groups[j].Leds);
                        used[j] = true;
                    }
                    merged.Add(main);
                    used[i] = true;
                }
                groups = merged;
            }
            var data = new List<byte>();
            foreach (var (color, members) in groups)
            {
                data.Add(R(color));
                data.Add(G(color));
                data.Add(B(color));
                data.Add((byte)members.Count);
                data.AddRange(members);
            }
            int total = (data.Count + Size - 1) / Size;
            for (int index = 0; index < total; index++)
            {
                int count = Math.Min(Size, data.Count - index * Size);
                var frame = Frame(64, 0x09, 0x08, 0x01, 0x00, (byte)total, (byte)index, (byte)count);
                data.CopyTo(index * Size, frame, 7, count);
                int sum = 0;
                for (int i = 0; i < 7 + count; i++) sum += frame[i];
                frame[63] = (byte)(0xFF - (sum & 0xFF));
                yield return PluginStep.Output(frame);
                yield return PluginStep.Wait(1);
            }
        }

        // ── The firmware behind Nuvoton's, KBM!'s and Xiaohua's plugins ──

        /// <summary>The firmware's own lighting mode goes first. The plugins'
        /// report ID differs, and Xiaohua's leaves the first report out.</summary>
        private static PluginStep[] NuvotonStart(byte report, bool first = true)
        {
            var steps = new List<PluginStep>();
            if (first) steps.Add(PluginStep.Output(Frame(64, report, 0x07, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x04, 0x03, 0xFF)));
            steps.Add(PluginStep.Output(Frame(64, report, 0x17, 0x00, 0x00, 0x00, 0x01, 0x01)));
            steps.Add(PluginStep.Output(Frame(64, report, 0x08, 0x00, 0x00, 0x00, 0x0D, 0x02, 0x03, 0x03, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01)));
            return steps.ToArray();
        }

        /// <summary>The fifth comma-separated field of the firmware's answer
        /// (fetchFirmwareData).</summary>
        internal static string NuvotonModel(IHidReportChannel io, byte report)
        {
            if (!io.WriteOutput(Frame(64, report, 0x0D))) return null;
            var answer = new byte[Math.Max(io.InputReportLength, 64)];
            if (io.ReadInput(answer, AskMs) <= 0) return null;
            var fields = Encoding.ASCII.GetString(answer).Split(',');
            return fields.Length > 4 ? fields[4] : null;
        }

        /// <summary>The LEDs each zone takes, from the highest LED the table
        /// names in it: an LED's number is its zone times 256 plus its LED.</summary>
        internal static int[] NuvotonZones(PluginKeyTable table)
        {
            int zones = table.Indexes.Max() / 256 + 1;
            var leds = new int[zones];
            foreach (int index in table.Indexes) leds[index / 256] = Math.Max(leds[index / 256], index % 256 + 1);
            return leds;
        }

        /// <summary>Eighteen LEDs to a report, zone after zone, then what the
        /// plugin closes a picture with.</summary>
        internal static IEnumerable<PluginStep> NuvotonPicture(byte report, int[] zones, int[] colors, bool wait, IEnumerable<PluginStep> after)
        {
            for (int zone = 0; zone < zones.Length; zone++)
            {
                int packet = 0;
                for (int led = 0; led < zones[zone]; led += 18, packet++)
                {
                    int count = Math.Min(18, zones[zone] - led);
                    var frame = Frame(65, report, 0x0F, (byte)zone, 0x00, (byte)packet, (byte)(count * 3));
                    for (int i = 0; i < count; i++) PutRgb(frame, 6 + i * 3, colors[zone * 256 + led + i]);
                    yield return PluginStep.Output(frame);
                    if (wait) yield return PluginStep.Wait(1);
                }
            }
            foreach (var step in after) yield return step;
        }

        /// <summary>A board with one zone still takes a blank second one.</summary>
        private static IEnumerable<PluginStep> BlankSecondZone(byte report, int zones)
        {
            if (zones != 1) yield break;
            yield return PluginStep.Output(Frame(65, report, 0x0F, 0x01, 0x00, 0x00, 0x36));
            yield return PluginStep.Output(Frame(65, report, 0x0F, 0x01, 0x00, 0x01, 0x2D));
        }

        private static IEnumerable<PluginDevice> Nuvoton()
        {
            yield return new PluginDevice
            {
                Name = "Nuvoton keyboard",
                VendorId = 0x0416,
                ProductId = 0x7372,
                Plugin = "Nuvoton/Nuvoton_Keyboard_Controller.js",
                Interface = 2,
                UsagePage = 0xFF1B,
                Usage = 0x0091,
                OutputLength = 64,
                BuildLights = FirstBoardLights(PluginLibraryTables.Nuvoton),
                Slots = 512,
                Start = NuvotonStart(0x01),
                AskVariant = (io, _, _) =>
                {
                    var board = BoardNamed(PluginLibraryTables.Nuvoton, NuvotonModel(io, 0x01));
                    if (board == null) return null;
                    var table = board.Table();
                    var zones = NuvotonZones(table);
                    return new PluginVariant(PluginKeyNames.Lights(table), zones.Length * 256, colors =>
                        NuvotonPicture(0x01, zones, colors, true, NuvotonClose(zones.Length, board.Name)));
                },
            };
        }

        private static IEnumerable<PluginStep> NuvotonClose(int zones, string name)
        {
            foreach (var step in BlankSecondZone(0x01, zones)) yield return step;
            if (zones != 1) yield break;
            // The plugin's own note: only the AK820 Max needs this.
            if (name == "Ajazz AK820 MAX") yield return PluginStep.Output(Frame(65, 0x01, 0x0F, 0x00, 0x00, 0x07, 0x12));
            yield return PluginStep.Wait(1);
        }

        private static IEnumerable<PluginDevice> Kbm()
        {
            foreach (var (name, pid, model) in new (string, ushort, string)[]
            {
                ("KBM! TG700 Preto", 0xE987, "MK69_JLRGB"),
                ("KBM! TG600 Branco", 0xE989, "JLMK60V2RGB"),
            })
            {
                var board = BoardNamed(PluginLibraryTables.Kbm, model);
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x4A4C,
                    ProductId = pid,
                    Plugin = "KBM Gaming/KBM_Keyboard_Controller.js",
                    Programs = new[] { VendorPrograms.KbmTeclado },
                    Interface = 2,
                    UsagePage = 0xFF1B,
                    Usage = 0x0091,
                    OutputLength = 64,
                    BuildLights = () => PluginKeyNames.Lights(board.Table()),
                    Slots = 512,
                    Start = NuvotonStart(0x01),
                    Picture = colors =>
                    {
                        var zones = NuvotonZones(board.Table());
                        return NuvotonPicture(0x01, zones, colors, false, new[] { PluginStep.Output(Frame(65, 0x01, 0x0F, 0x01, 0x00, 0x02, 0x1B)) });
                    },
                };
            }
        }

        private static IEnumerable<PluginDevice> Xiaohua()
        {
            yield return new PluginDevice
            {
                Name = "Xiaohua keyboard",
                VendorId = 0x2E88,
                ProductId = 0xB30A,
                Plugin = "Xiaohua/Xiaohua_Keyboard_Controller.js",
                Interface = 0,
                UsagePage = 0xFF1B,
                Usage = 0x0091,
                OutputLength = 64,
                BuildLights = FirstBoardLights(PluginLibraryTables.Xiaohua),
                Slots = 512,
                Start = NuvotonStart(0x06, first: false),
                AskVariant = (io, _, _) =>
                {
                    var board = BoardNamed(PluginLibraryTables.Xiaohua, NuvotonModel(io, 0x06));
                    if (board == null) return null;
                    var table = board.Table();
                    var zones = NuvotonZones(table);
                    return new PluginVariant(PluginKeyNames.Lights(table), zones.Length * 256, colors =>
                        NuvotonPicture(0x06, zones, colors, false, BlankSecondZone(0x06, zones.Length)));
                },
            };
        }

        // ── Nuvoton, second plugin (Nuvoton/Nuvoton_Keyboard2_Controller.js) ──

        private static IEnumerable<PluginDevice> Nuvoton2()
        {
            yield return new PluginDevice
            {
                Name = "Nuvoton keyboard",
                VendorId = 0x0416,
                ProductId = 0xA0F8,
                Plugin = "Nuvoton/Nuvoton_Keyboard2_Controller.js",
                Interface = 1,
                UsagePage = 0x0001,
                Usage = 0x0000,
                OutputLength = 32,
                BuildLights = FirstBoardLights(PluginLibraryTables.Nuvoton2),
                Slots = 256,
                AskVariant = (io, _, _) =>
                {
                    if (!io.WriteOutput(Frame(32, 0x06, 0xBE, 0x02))) return null;
                    var answer = new byte[Math.Max(io.InputReportLength, 32)];
                    if (io.ReadInput(answer, AskMs) <= 0) return null;
                    string model = Encoding.ASCII.GetString(answer, 4, 16).Replace("\0", "");
                    var board = BoardNamed(PluginLibraryTables.Nuvoton2, model);
                    if (board == null) return null;
                    var table = board.Table();
                    return VariantOf(table, colors => Nuvoton2Picture(table.Indexes, colors));
                },
            };
        }

        /// <summary>Five LEDs at a time, each five named first, then their
        /// colors, between the plugin's two fixed reports.</summary>
        internal static IEnumerable<PluginStep> Nuvoton2Picture(int[] leds, int[] colors)
        {
            for (int at = 0; at < leds.Length; at += 5)
            {
                int count = Math.Min(5, leds.Length - at);
                var frame = Frame(32, 0x06, 0xBE, 0x19, 0x00, 0x01, 0x01, 0x0E);
                for (int i = 0; i < count; i++)
                {
                    frame[7 + i] = (byte)leds[at + i];
                    PutRgb(frame, 12 + i * 4, colors[leds[at + i]]);
                }
                yield return PluginStep.Output(Frame(32, 0x06, 0xBE, 0x15, 0x00, 0x01, 0x01, 0x0E, 0x05, 0x06, 0x00, 0x00, 0x00, 0xFF));
                yield return PluginStep.Output(frame);
                yield return PluginStep.Wait(1);
                yield return PluginStep.Output(Frame(32, 0x06, 0xBE, 0x15, 0x00, 0x02, 0x01, 0x01, 0x05, 0x09, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x14, 0xFF, 0x00, 0x00, 0x01));
            }
        }

        // ── Sinowealth, second plugin (Sinowealth/Sinowealth_Keyboard_Controller2.js) ──

        internal const int Sinowealth2Length = 382;

        private static IEnumerable<PluginDevice> Sinowealth2()
        {
            yield return new PluginDevice
            {
                Name = "Sinowealth keyboard",
                VendorId = 0x258A,
                ProductId = 0x0049,
                Plugin = "Sinowealth/Sinowealth_Keyboard_Controller2.js",
                Interface = 1,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                FeatureLength = Sinowealth2Length,
                BuildLights = FirstBoardLights(PluginLibraryTables.Sinowealth2),
                Slots = 256,
                // The model comes from the collection beside the colors' one.
                Siblings = new[] { new HidSibling(1, 0xFF00) { FeatureBelow = Sinowealth2Length } },
                PictureWith = _ => null,
                AskVariant = (_, siblings, wait) =>
                {
                    var query = siblings[0];
                    if (!query.SetFeature(Frame(6, 0x05, 0x05, 0x81))) return null;
                    wait?.Invoke(1);
                    var answer = new byte[Math.Max(query.FeatureReportLength, 8)];
                    if (!query.GetFeature(0x05, answer)) return null;
                    wait?.Invoke(1);
                    var board = BoardNamed(PluginLibraryTables.Sinowealth2, (answer[6] * 255 + answer[7]).ToString());
                    return board == null ? null : VariantOf(board.Table(), Sinowealth2Picture);
                },
            };
        }

        internal static IEnumerable<PluginStep> Sinowealth2Picture(int[] colors)
        {
            var frame = Frame(Sinowealth2Length, 0x08, 0x0A, 0x7A, 0x01);
            for (int led = 0; led < colors.Length && 6 + led * 3 < Sinowealth2Length; led++) PutRgb(frame, 4 + led * 3, colors[led]);
            yield return PluginStep.Feature(frame);
            yield return PluginStep.Wait(1);
        }

        // ── ASUS keyboards OpenRGB has no row for (Asus/ASUS_Keyboard.js) ──

        private static IEnumerable<PluginDevice> AsusKeyboards()
        {
            foreach (var (name, pid, model) in new (string, ushort, string)[]
            {
                ("ASUS ROG Strix Scope", 0x18F6, "ROG Strix Scope"),
                ("ASUS ROG Strix Scope RX", 0x1A55, "ROG Strix Scope RX"),
                ("ASUS ROG Falchion Ace", 0x1A64, "ROG Falchion"),
                ("ASUS ROG Falchion RX", 0x1B04, "ROG Falchion RX"),
                ("ASUS ROG Falchion Ace HFX", 0x1B7E, "ROG Falchion Ace HFX"),
            })
            {
                var board = BoardNamed(PluginLibraryTables.AsusKeyboard, model);
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x0B05,
                    ProductId = pid,
                    Plugin = "Asus/ASUS_Keyboard.js",
                    Programs = new[] { VendorPrograms.ArmouryCrate },
                    Interface = 1,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 65,
                    BuildLights = () => PluginKeyNames.Lights(board.Table()),
                    Slots = 256,
                    Picture = colors => AsusKeyboardPicture(board.Table().Indexes, colors),
                    HandBack = new[] { PluginStep.Output(ClaymoreOwnEffect) },
                };
            }
        }

        /// <summary>Fifteen LEDs to a report, each its number and its color. The
        /// count in a report is the LEDs still to come.</summary>
        internal static IEnumerable<PluginStep> AsusKeyboardPicture(int[] leds, int[] colors)
        {
            for (int at = 0; at < leds.Length; at += 15)
            {
                var frame = Frame(65, 0x00, 0xC0, 0x81, (byte)(leds.Length - at), 0x00);
                for (int i = 0; i < 15 && at + i < leds.Length; i++)
                {
                    frame[5 + i * 4] = (byte)leds[at + i];
                    PutRgb(frame, 6 + i * 4, colors[leds[at + i]]);
                }
                yield return PluginStep.Output(frame);
            }
        }

        // ── ASUS mice OpenRGB has no row for (Asus/Asus_Mouse.js) ──

        private static IEnumerable<PluginDevice> AsusMice()
        {
            foreach (var (name, pid, wheel) in new (string, ushort, bool)[]
            {
                ("ASUS ROG Harpe Ace AIM LAB Edition", 0x1A92, true),
                ("ASUS ROG Harpe Ace AIM LAB Edition", 0x1A94, true),
                ("ASUS ROG Gladius III Aimpoint", 0x1B0C, false),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x0B05,
                    ProductId = pid,
                    Plugin = "Asus/Asus_Mouse.js",
                    Programs = new[] { VendorPrograms.ArmouryCrate },
                    Interface = 0,
                    UsagePage = 0xFF01,
                    Usage = 0x0001,
                    OutputLength = 65,
                    Lights = wheel ? new[] { Wheel(0) } : new[] { Logo(0) },
                    Slots = 1,
                    Start = new[] { PluginStep.Output(Frame(65, 0x00, 0x51, 0x28, 0x03, 0x00, 0x02, 0x64, 0x02)) },
                    Picture = colors =>
                    {
                        var frame = Frame(65, 0x00, 0x51, 0x29, 0xFF, 0x00, 0x00);
                        PutRgb(frame, 6, colors[0]);
                        return new[] { PluginStep.Output(frame) };
                    },
                };
            yield return new PluginDevice
            {
                Name = "ASUS ROG Keris II Origin",
                VendorId = 0x0B05,
                ProductId = 0x1C0C,
                Plugin = "Asus/Asus_Mouse.js",
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Interface = 0,
                UsagePage = 0xFF01,
                Usage = 0x0001,
                OutputLength = 65,
                Lights = new[] { Logo(0), Wheel(1), Edge(2) },
                Slots = 3,
                Picture = colors => Enumerable.Range(0, 3).SelectMany(led => new[]
                {
                    PluginStep.Output(Frame(65, 0x00, 0x51, 0x28, (byte)led, 0x00, 0x00, 0x64, R(colors[led]), G(colors[led]), B(colors[led]))),
                    PluginStep.Wait(1),
                }),
            };
        }
    }
}
