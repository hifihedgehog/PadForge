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
                .Concat(LogitechG19()).Concat(MountainMakalu()).Concat(CoolerMasterMat()).Concat(CorsairHeadsets())
                .Concat(CorsairDarkCore()).Concat(CorsairNexus()).Concat(Roccat()).Concat(RoyalKludge())
                .Concat(NzxtFunction()).Concat(Glorious()).Concat(SteelSeriesKeyboards()).Concat(StripControllers())
                .Concat(QmkBoards()).Concat(Macropad()).Concat(RamaM65B()).Concat(BetaDevices()).Concat(HasteWirelessNewFirmware()).Concat(OtherProjectDevices());

        // ── Corsair headsets (Corsair/Modern_Corsair/Virtuoso_HeadSets, Corsair/Void_HeadSets) ──

        private static readonly VendorProgram[] s_icue = { VendorPrograms.CorsairIcue };

        /// <summary>The Virtuoso plugins' device byte: 09 through the headset's
        /// dongle, 08 on its cable. The plain Virtuoso's wired plugin switches
        /// to 08 for its start and leaves 09 in its colors and hand-back, where
        /// the SE's wired plugin uses 08 throughout, and PadForge sends the
        /// SE's.</summary>
        private static IEnumerable<PluginDevice> CorsairHeadsets()
        {
            const string Headsets = "Corsair/Modern_Corsair/Corsair_Headset_Controller.js";
            foreach (var (name, pid, wireless) in new (string, ushort, bool)[]
            {
                ("Corsair Virtuoso Wireless", 0x0A40, true),
                ("Corsair Virtuoso", 0x0A41, false),
                ("Corsair Virtuoso Wireless", 0x0A42, true),
                ("Corsair Virtuoso", 0x0A43, false),
                ("Corsair Virtuoso Wireless", 0x0A44, true),
                ("Corsair Virtuoso Wireless", 0x0A4B, true),
                ("Corsair Virtuoso Wireless", 0x0A4C, true),
                ("Corsair Virtuoso", 0x0A5A, false),
                ("Corsair Virtuoso Wireless", 0x0A5C, true),
                ("Corsair Virtuoso SE", 0x0A3D, false),
                ("Corsair Virtuoso SE Wireless", 0x0A3E, true),
                ("Corsair Virtuoso XT", 0x0A62, false),
                ("Corsair Virtuoso XT Wireless", 0x0A64, true),
                ("Corsair HS80 RGB", 0x0A69, false),
                ("Corsair HS80 RGB Wireless", 0x0A6B, true),
                ("Corsair HS80 RGB White", 0x0A71, false),
                ("Corsair HS80 RGB White Wireless", 0x0A73, true),
                // On master only, and not on beta's list.
                ("Corsair Virtuoso Gaming SE", 0xA346, true),
            })
            {
                // The device byte: 09 through the dongle, 08 on the cable.
                byte device = wireless ? (byte)0x09 : (byte)0x08;
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = CorsairLights.VendorId,
                    ProductId = pid,
                    Plugin = Headsets,
                    // Interface 3 or 4 by the model: the plugin tries the ones it lists.
                    UsagePage = 0xFF42,
                    Usage = 0x0001,
                    OutputLength = 64,
                    // The logo, the power light and the microphone.
                    Lights = Zones(3),
                    Slots = 3,
                    Programs = s_icue,
                    // A wireless headset drops the host's colors when it sleeps. The
                    // plugin watches for that on its reports, and a keep-alive takes
                    // it again with no second collection.
                    KeepAliveMs = wireless ? 5000 : 0,
                    Start = new[]
                    {
                        PluginStep.Output(Frame(64, 0x02, device, 0x01, 0x03, 0x00, 0x02)),
                        PluginStep.Wait(100),
                        PluginStep.Output(Frame(64, 0x02, device, 0x0D, 0x00, 0x01)),
                        PluginStep.Wait(100),
                        PluginStep.Output(Frame(64, 0x02, device, 0x01, 0x02, 0x00, 0xE8, 0x03)),
                    },
                    Picture = colors => VirtuosoPicture(device, colors),
                    HandBack = new[] { PluginStep.Output(Frame(64, 0x02, device, 0x01, 0x03, 0x00, 0x01)) },
                };
            }
            yield return new PluginDevice
            {
                Name = "Corsair HS80 RGB Wired Gaming Headset",
                VendorId = CorsairLights.VendorId,
                ProductId = 0x0A88,
                Plugin = "Corsair/Modern_Corsair/Corsair_HS80_RGB_Wired_Gaming_Headset.js",
                Interface = 3,
                UsagePage = 0xFFC5,
                Usage = 0x0001,
                OutputLength = 16,
                Lights = new[] { Logo(0) },
                Slots = 1,
                Programs = s_icue,
                Picture = colors => new[]
                {
                    PluginStep.Output(Frame(16, 0xCB, 0x03, 0x1C, R(colors[0]), 0x16, G(colors[0]), 0x17, B(colors[0]))),
                    PluginStep.Wait(1),
                },
            };
            foreach (var (name, pid, plugin) in new (string, ushort, string)[]
            {
                ("Corsair Void Pro Headset", 0x0A14, "Corsair_Void_Pro_HeadSet_0x0A14.js"),
                ("Corsair Void Pro Headset", 0x0A17, "Corsair_Void_Pro_HeadSet_0x0A17.js"),
                ("Corsair Void Pro Headset", 0x0A19, "Corsair_Void_Pro_HeadSet_0x0A19.js"),
                ("Corsair Void Pro Headset", 0x0A1A, "Corsair_Void_Pro_HeadSet_0x0A1A.js"),
                ("Corsair Void Pro Headset", 0x0A1D, "Corsair_Void_Pro_HeadSet_0x0A1D.js"),
                ("Corsair Void Pro Headset", 0x1B23, "Corsair_Void_Pro_HeadSet_0x1B23.js"),
                ("Corsair Void Elite Headset", 0x0A56, "Corsair_Void_Elite_HeadSet.js"),
                ("Corsair Void Elite Headset", 0x0A52, "Corsair_Void_Elite_HeadSet_0x0A52.js"),
                ("Corsair Void Elite Wireless Headset", 0x0A51, "Corsair_Void_Elite_HeadSet_Wireless_0x0A51.js"),
                ("Corsair Void Elite Wireless Headset", 0x0A55, "Corsair_Void_Elite_HeadSet_Wireless_0x0A55.js"),
                ("Corsair Void Elite Wireless Headset", 0x0A75, "Corsair_Void_Elite_HeadSet_Wireless_0x0A75.js"),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = CorsairLights.VendorId,
                    ProductId = pid,
                    Plugin = "Corsair/Void_HeadSets/" + plugin,
                    Interface = 3,
                    // This model's plugin takes page FF00 as well.
                    UsagePage = pid == 0x0A14 ? OpenRgbRegistration.Any : 0xFFC5,
                    Usage = 0x0001,
                    OutputLength = 20,
                    // The left and the right can.
                    Lights = Zones(2),
                    Slots = 2,
                    Programs = s_icue,
                    RepaintMs = VoidWaitMs,
                    Start = new[] { PluginStep.Output(Frame(20, 0xC8, 0x01)) },
                    Picture = colors => new[]
                    {
                        PluginStep.Output(Frame(20, 0xCB, 0x06, 0x1C, R(colors[0]), 0x16, G(colors[0]), 0x17, B(colors[0]),
                            0x1D, R(colors[1]), 0x18, G(colors[1]), 0x19, B(colors[1]))),
                    },
                    HandBack = new[] { PluginStep.Output(Frame(20, 0xC8, 0x00)) },
                };
        }

        /// <summary>The plugin waits 30 ms after each Void report.</summary>
        internal const int VoidWaitMs = 30;

        /// <summary>The reds, then the greens, then the blues.</summary>
        internal static IEnumerable<PluginStep> VirtuosoPicture(byte device, int[] colors)
        {
            var frame = Frame(64, 0x02, device, 0x06, 0x00, 0x09);
            for (int i = 0; i < 3; i++)
            {
                frame[8 + i] = R(colors[i]);
                frame[11 + i] = G(colors[i]);
                frame[14 + i] = B(colors[i]);
            }
            yield return PluginStep.Output(frame);
        }

        // ── Corsair Dark Core and Dark Core SE (Legacy_Corsair_Mouse.js) ──

        private static IEnumerable<PluginDevice> CorsairDarkCore()
        {
            foreach (var (name, pid) in new (string, ushort)[]
            {
                ("Corsair Dark Core", 0x1B35), ("Corsair Dark Core", 0x1B64), ("Corsair Dark Core SE", 0x1B51),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = CorsairLights.VendorId,
                    ProductId = pid,
                    Plugin = "Corsair/Legacy_Corsair/Legacy_Corsair_Mouse.js",
                    Interface = 1,
                    UsagePage = 0xFFC2,
                    Usage = 0x0004,
                    OutputLength = 65,
                    // The logo, the sniper button's light and the front.
                    Lights = new[] { Logo(0), Status(1), Zone(2, 1) },
                    Slots = 3,
                    Programs = s_icue,
                    // A wireless mouse loses software mode when it sleeps. The
                    // plugin sets it again when the mouse says it woke, and a
                    // keep-alive does the same without a second collection.
                    KeepAliveMs = 5000,
                    Start = new[]
                    {
                        PluginStep.Output(Frame(65, 0x00, 0x07, 0x05, 0x02, 0x00, 0x01)),
                        PluginStep.Output(Frame(65, 0x00, 0x07, 0x04, 0x02)),
                    },
                    Picture = DarkCorePicture,
                    HandBack = new[]
                    {
                        PluginStep.Output(Frame(65, 0x00, 0x07, 0x05, 0x01, 0x00, 0x01)),
                        PluginStep.Output(Frame(65, 0x00, 0x07, 0x04, 0x01)),
                    },
                };
        }

        /// <summary>A report per zone, 5 ms apart: setDarkCoreLighting.</summary>
        internal static IEnumerable<PluginStep> DarkCorePicture(int[] colors)
        {
            byte[] zones = { 0x01, 0x02, 0x04 };
            byte[] last = { 0x05, 0x04, 0x03 };
            for (int i = 0; i < 3; i++)
            {
                yield return PluginStep.Output(Frame(65, 0x00, 0x07, 0xAA, 0x00, 0x00, zones[i], 0x07, 0x00, 0x00, 0x64,
                    R(colors[i]), G(colors[i]), B(colors[i]), 0x00, 0x00, 0x00, last[i]));
                yield return PluginStep.Wait(5);
            }
        }

        // ── Corsair iCUE Nexus screen (Corsair_Nexus_Screen.js) ──

        private static IEnumerable<PluginDevice> CorsairNexus()
        {
            static IEnumerable<PluginStep> Ask(params byte[] head)
            {
                yield return PluginStep.Feature(Frame(32, head));
                yield return PluginStep.ReadFeature(0x03);
            }
            yield return new PluginDevice
            {
                Name = "Corsair Nexus",
                VendorId = CorsairLights.VendorId,
                ProductId = 0x1B8E,
                Plugin = "Corsair/Corsair_Nexus_Screen.js",
                Interface = 0,
                OutputLength = 1024,
                FeatureLength = 32,
                Lights = Zones(1),
                Slots = 1,
                Programs = s_icue,
                RepaintMs = NexusRepaintMs,
                Start = Ask(0x03, 0x1D, 0x01, 0x00)
                    .Concat(Ask(0x03, 0x19))
                    .Concat(Ask(0x03, 0x20, 0x00, 0x19, 0x79, 0xE7, 0x32, 0x2E, 0x30, 0x2E, 0x30, 0x2E, 0x33))
                    .Concat(Ask(0x03, 0x0B, 0x40, 0x01, 0x79, 0xE7, 0x32, 0x2E, 0x30, 0x2E, 0x30, 0x2E, 0x33))
                    .ToArray(),
                Picture = NexusPicture,
                HandBack = new[] { NexusOwnScreen(0x0D, 0x01), NexusOwnScreen(0x01, 0x64) },
            };
        }

        /// <summary>A whole screen is 121 reports of 1,024 bytes, so pictures
        /// stay this far apart. No source bounds it, so the bound is PadForge's.</summary>
        internal const int NexusRepaintMs = 100;

        private static PluginStep NexusOwnScreen(byte first, byte second)
        {
            var frame = Frame(32, 0x03, first, second, 0x01, 0x78, 0x00, 0xC0, 0x03);
            for (int i = 8; i < 32; i += 4)
            {
                frame[i] = frame[i + 1] = frame[i + 2] = 0x2F;
                frame[i + 3] = 0xFF;
            }
            return PluginStep.Feature(frame);
        }

        /// <summary>The screen's 640 by 48 pixels as blue, green, red and FF.
        /// The plugin draws every other pixel from the canvas. PadForge has one
        /// color for the screen and draws them all.</summary>
        internal static IEnumerable<PluginStep> NexusPicture(int[] colors)
        {
            for (int row = 0; row < 121; row++)
            {
                var frame = Frame(1024, 0x02, 0x05, 0x40, (byte)(row == 120 ? 1 : 0), (byte)row, 0x00, 0xF8, 0x03);
                for (int at = 8; at < 1024; at += 4)
                {
                    frame[at] = B(colors[0]);
                    frame[at + 1] = G(colors[0]);
                    frame[at + 2] = R(colors[0]);
                    frame[at + 3] = 0xFF;
                }
                yield return PluginStep.Output(frame);
            }
        }

        // ── Roccat mice, dock, headsets and microphone (Roccat/*.js) ──

        private static readonly VendorProgram[] s_swarm = { VendorPrograms.RoccatSwarm };

        private static PluginDevice RoccatRow(string name, ushort pid, string plugin) => new()
        {
            Name = name,
            VendorId = 0x1E7D,
            ProductId = pid,
            Plugin = "Roccat/" + plugin,
            Programs = s_swarm,
        };

        private static PluginStep[] Steps(int waitMs, int length, params byte[][] heads)
        {
            var steps = new List<PluginStep>();
            foreach (var head in heads)
            {
                steps.Add(PluginStep.Feature(Frame(length, head)));
                if (waitMs > 0) steps.Add(PluginStep.Wait(waitMs));
            }
            return steps.ToArray();
        }

        private static IEnumerable<PluginDevice> Roccat()
        {
            yield return new PluginDevice
            {
                Name = "Roccat Kone XP Air Dock",
                VendorId = 0x1E7D,
                ProductId = 0x2CB8,
                Plugin = "Roccat/Roccat_Kone_XP_Air_Dock.js",
                Programs = s_swarm,
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
                ("Roccat Kone XP Air - Wired", 0x2CB2, "Roccat_Kone_XP_Air_Mouse_Wired.js", 0x00),
                ("Roccat Kone XP Air", 0x2CB6, "Roccat_Kone_XP_Air.js", 0x01),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x1E7D,
                    ProductId = pid,
                    Plugin = "Roccat/" + plugin,
                    Programs = s_swarm,
                    Interface = 2,
                    Usage = 0xFF00,
                    FeatureLength = 30,
                    // The wheel and four lights on the shell.
                    Lights = new[] { Wheel(0), Edge(1), Edge(2), Edge(3), Edge(4) },
                    Slots = 5,
                    Start = KoneXpStart(id),
                    Picture = colors => KoneXpPicture(id, colors),
                };
            yield return new PluginDevice
            {
                Name = "Roccat Burst Pro Air - Wired",
                VendorId = 0x1E7D,
                ProductId = 0x2CAB,
                Plugin = "Roccat/Roccat_Burst_Pro_Air_Mouse_Wired.js",
                Programs = s_swarm,
                Interface = 2,
                Usage = 0xFF00,
                FeatureLength = 30,
                // The wheel, the two buttons and the back.
                Lights = new[] { Wheel(0), Edge(1), Edge(2), Logo(3) },
                Slots = 4,
                Start = BurstStart().Concat(BurstStart()).ToArray(),
                Picture = colors =>
                {
                    var frame = Frame(30, 0x06, 0x00, 0x4D, 0x06, 0x0C);
                    for (int i = 0; i < 4; i++) PutRgb(frame, 5 + i * 3, colors[i]);
                    return new[] { PluginStep.Feature(frame), PluginStep.Wait(1) };
                },
            };
            yield return new PluginDevice
            {
                Name = "Roccat Torch",
                VendorId = 0x1E7D,
                ProductId = 0x3A56,
                Plugin = "Roccat/Roccat_Torch_Microphone.js",
                Programs = s_swarm,
                Interface = 3,
                OutputLength = 6,
                Lights = Edges(1),
                Slots = 1,
                Start = new[]
                {
                    PluginStep.Output(Frame(6, 0x00, 0x80, 0x80)), PluginStep.Wait(100),
                    PluginStep.Output(Frame(6, 0x00, 0x80, 0x02)), PluginStep.Wait(100),
                    PluginStep.Output(Frame(6, 0x00, 0x80, 0x02)), PluginStep.Wait(100),
                    PluginStep.Output(new byte[6]), PluginStep.Wait(100),
                },
                Picture = TorchPicture,
            };
            foreach (var (name, pid, plugin, lights) in new (string, ushort, string, LightInfo[])[]
            {
                ("Roccat Kain 100", 0x2D00, "Roccat_Kain_100_Mouse.js", new[] { Wheel(0) }),
                ("Roccat Kain 120", 0x2D5C, "Roccat_Kain_120_Mouse.js", new[] { Wheel(0), Logo(1) }),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x1E7D,
                    ProductId = pid,
                    Plugin = "Roccat/" + plugin,
                    Programs = s_swarm,
                    Interface = 3,
                    FeatureLength = 11,
                    Lights = lights,
                    Slots = lights.Length,
                    Start = new[] { PluginStep.Feature(Frame(6, 0x0E, 0x06, 0x01, 0x01, 0x00, 0xFF)) },
                    Picture = colors => RoccatZones(11, 0x0D, 0x0B, colors),
                    HandBack = new[] { PluginStep.Feature(Frame(6, 0x0E, 0x06, 0x00, 0x00, 0x00, 0xFF)) },
                };
            yield return new PluginDevice
            {
                Name = "Roccat Kova Aimo",
                VendorId = 0x1E7D,
                ProductId = 0x2CF1,
                Plugin = "Roccat/Roccat_Kova_Aimo_Mouse.js",
                Programs = s_swarm,
                Interface = 0,
                Usage = 0x0000,
                FeatureLength = 8,
                // The plugin lists the wheel at place 1 and the back at place 0.
                Lights = new[] { Wheel(1), Logo(0) },
                Slots = 2,
                Start = new[] { PluginStep.Feature(Frame(6, 0x0E, 0x06, 0x01, 0x01, 0x00, 0xFF)) },
                Picture = colors => RoccatZones(8, 0x0A, 0x08, colors),
                HandBack = new[] { PluginStep.Feature(Frame(6, 0x0E, 0x06, 0x00, 0x00, 0x00, 0xFF)) },
            };
            yield return new PluginDevice
            {
                Name = "Roccat Kain 200",
                VendorId = 0x1E7D,
                ProductId = 0x2D5F,
                Plugin = "Roccat/Roccat_Kain_200_Mouse_Wired.js",
                Programs = s_swarm,
                Interface = 2,
                UsagePage = 0xFF01,
                Usage = 0x0001,
                FeatureLength = 22,
                Lights = new[] { Wheel(0), Logo(1) },
                Slots = 2,
                Picture = colors =>
                {
                    var frame = KainFrame(colors);
                    byte crc = 0;
                    for (int i = 0; i < 10; i++) crc ^= frame[i];
                    frame[10] = crc;
                    return new[] { PluginStep.Feature(frame) };
                },
            };
            yield return new PluginDevice
            {
                Name = "Roccat Kain 200",
                VendorId = 0x1E7D,
                ProductId = 0x2D60,
                Plugin = "Roccat/Roccat_Kain_200_Mouse_Wireless.js",
                Programs = s_swarm,
                FeatureLength = 22,
                Lights = new[] { Wheel(0), Logo(1) },
                Slots = 2,
                Picture = colors =>
                {
                    var frame = KainFrame(colors);
                    frame[10] = (byte)Math.Abs(frame[4] - frame[5] + frame[6] - frame[7] + frame[8] - frame[9] - 0x32);
                    return new[] { PluginStep.Feature(frame) };
                },
            };
            yield return new PluginDevice
            {
                Name = "Roccat Elo 7.1 Air",
                VendorId = 0x1E7D,
                ProductId = 0x3A37,
                Plugin = "Roccat/Roccat_Elo_7.1_Air_Headset.js",
                Programs = s_swarm,
                Interface = 0,
                OutputLength = 64,
                Lights = Edges(1),
                Slots = 1,
                // The headset drops the host's colors when it goes off and
                // comes back. The plugin watches for that on its reports, and a
                // keep-alive takes it again with no watching.
                KeepAliveMs = 5000,
                Start = new[]
                {
                    PluginStep.Output(Frame(64, 0x00, 0xFF, 0x02)), PluginStep.Wait(10),
                    PluginStep.Output(Frame(64, 0x00, 0xFF, 0x03, 0x00, 0x01, 0x00, 0x01)), PluginStep.Wait(10),
                    PluginStep.Output(Frame(64, 0x00, 0xFF, 0x04)), PluginStep.Wait(10),
                    PluginStep.Output(Frame(64, 0x00, 0xFF, 0x01)), PluginStep.Wait(10),
                    PluginStep.ReadInput(),
                },
                Picture = colors => HeadsetColor(0xFF, 0x04, colors[0]),
            };
            yield return new PluginDevice
            {
                Name = "Roccat Syn Pro Air",
                VendorId = 0x1E7D,
                ProductId = 0x3A3B,
                Plugin = "Roccat/Roccat_Syn_Pro_Air_Headset.js",
                Programs = s_swarm,
                Interface = 0,
                OutputLength = 64,
                // The front and the rear.
                Lights = Edges(2),
                Slots = 2,
                KeepAliveMs = 5000,
                Start = new[]
                {
                    // sendPacketString reads its index as hexadecimal, so the plugin's bytes past
                    // the tenth land from byte 16.
                    PluginStep.Output(Frame(64, 0x00, 0xFE, 0x01, 0x00, 0x00, 0x00, 0x09, 0x08, 0x02, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF)),
                    PluginStep.Output(Frame(64, 0x00, 0xFE, 0x02, 0x00, 0x00, 0x01)),
                    // The plugin's keep-alive report (GetBatteryStatus).
                    PluginStep.Output(Frame(64, 0x00, 0xFE, 0x04)),
                },
                Picture = SynProAirPicture,
            };
        }

        /// <summary>The Kone XP Air dock's Initialize, Roccat's and Turtle Beach's.</summary>
        private static PluginStep[] KoneDockStart() => new[]
        {
            PluginStep.Feature(new byte[]
            {
                        0x02, 0x00, 0x09, 0x12, 0xFA, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x09, 0x12, 0xFA, 0x00, 0xFF, 0x7D, 0x00, 0xFF, 0x09, 0x12,
                        0xFA, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x09, 0x12, 0xFA, 0x00, 0x7D, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x01, 0xFF, 0x00,
            }),
            PluginStep.Feature(Frame(5, 0x01, 0xFF, 0xFF)),
        };

        internal static IEnumerable<PluginStep> KoneDockPicture(int[] colors)
        {
            var frame = Frame(17, 0x03);
            for (int i = 0; i < 4; i++)
            {
                PutRgb(frame, 1 + i * 4, colors[i]);
                frame[4 + i * 4] = 0xFF;
            }
            yield return PluginStep.Feature(frame);
        }

        /// <summary>The Kone XP Air's Initialize. The wireless plugin's third
        /// byte says 01 for the mouse and 00 for the receiver, its own note.</summary>
        private static PluginStep[] KoneXpStart(byte id) => Steps(30, 30,
            new byte[] { 0x06, id, 0x13, 0x07, 0x02 },
            new byte[] { 0x06, 0x00, 0x00, 0x04 },
            new byte[] { 0x06, 0x00, 0x00, 0x05 },
            new byte[] { 0x06, id, 0x35, 0x07 },
            new byte[] { 0x06, id, 0x00, 0x04 },
            new byte[] { 0x06, id, 0x00, 0x05 },
            new byte[] { 0x06, id, 0x4E, 0x06, 0x04, 0x01, 0x01, 0x01, 0xFF },
            new byte[] { 0x06, id, 0x44, 0x07 },
            new byte[] { 0x06, id, 0x49, 0x06, 0x01, 0x04 },
            new byte[] { 0x06, id, 0x44, 0x07 });

        /// <summary>The plugin for the wired Kone XP Air has fixed bytes before
        /// the colors where the wireless one has zeros.</summary>
        internal static IEnumerable<PluginStep> KoneXpPicture(byte id, int[] colors)
        {
            var frame = id == 0x00
                ? Frame(30, 0x06, 0x00, 0x4D, 0x06, 0x15, 0x6D, 0x70, 0x61)
                : Frame(30, 0x06, 0x01, 0x4D, 0x06, 0x15);
            for (int i = 0; i < 5; i++) PutRgb(frame, 8 + i * 3, colors[i]);
            yield return PluginStep.Feature(frame);
            yield return PluginStep.Wait(1);
        }

        private static PluginStep[] BurstStart() => Steps(70, 30,
            new byte[] { 0x06, 0x00, 0x35, 0x07 },
            new byte[] { 0x06, 0x00, 0x43, 0x07, 0x0C, 0x11, 0x0D, 0x00, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A },
            new byte[] { 0x06, 0x00, 0x4E, 0x06, 0x04, 0x01, 0x01, 0x01, 0xFF },
            new byte[] { 0x06, 0x00, 0x00, 0x04 },
            new byte[] { 0x06, 0x00, 0x00, 0x05 });

        /// <summary>The color packed as the plugin packs it, then an empty
        /// report 7 ms later.</summary>
        internal static IEnumerable<PluginStep> TorchPicture(int[] colors)
        {
            int r = R(colors[0]), g = G(colors[0]), b = B(colors[0]);
            yield return PluginStep.Output(Frame(6, 0x00, 0x80, (byte)(r >> 3), (byte)(r >> 6 | g >> 2), (byte)(g >> 7 | b >> 2)));
            yield return PluginStep.Wait(7);
            yield return PluginStep.Output(new byte[6]);
        }

        internal static IEnumerable<PluginStep> RoccatZones(int length, byte first, byte second, int[] colors)
        {
            var frame = Frame(length, first, second);
            for (int i = 0; i < colors.Length; i++) PutRgb(frame, 2 + i * 3, colors[i]);
            yield return PluginStep.Feature(frame);
            yield return PluginStep.Wait(1);
        }

        private static byte[] KainFrame(int[] colors)
        {
            var frame = Frame(22, 0x08, 0x09, 0x33, 0x00);
            PutRgb(frame, 4, colors[0]);
            PutRgb(frame, 7, colors[1]);
            return frame;
        }

        internal static IEnumerable<PluginStep> SynProAirPicture(int[] colors)
        {
            var frame = Frame(65, 0x00, 0xFE, 0x03);
            PutRgb(frame, 5, colors[0]);
            PutRgb(frame, 8, colors[1]);
            yield return PluginStep.Output(frame);
        }

        private static IEnumerable<PluginStep> HeadsetColor(byte first, byte second, int color)
        {
            var frame = Frame(65, 0x00, first, second);
            PutRgb(frame, 5, color);
            yield return PluginStep.Output(frame);
        }

        // ── Royal Kludge RK61 and RK84 (Royal Kludge/*.js) ──

        private static IEnumerable<PluginDevice> RoyalKludge()
        {
            foreach (ushort pid in new ushort[] { 0x0055, 0x007A, 0x0060, 0x004A, 0x00EC, 0x00E1, 0x00F8, 0x00C7, 0x0065 })
                yield return RoyalKludgeRow("Royal Kludge RK61", pid, PluginKeyTables.Rk61, colors => RoyalKludgePicture(colors, greenFirst: true, waitAfter: true));
            foreach (ushort pid in new ushort[] { 0x005D, 0x0059, 0x00C8, 0x010D, 0x00F4, 0x00F3 })
                yield return RoyalKludgeRow("Royal Kludge RK84", pid, PluginKeyTables.Rk84, colors => RoyalKludgePicture(colors, greenFirst: false, waitAfter: false));
        }

        private static PluginDevice RoyalKludgeRow(string name, ushort pid, PluginKeyTable table, Func<int[], IEnumerable<PluginStep>> picture) => new()
        {
            Name = name,
            VendorId = 0x258A,
            ProductId = pid,
            Plugin = table.Plugin,
            Interface = 1,
            UsagePage = 0xFF00,
            Usage = 0x0001,
            FeatureLength = 65,
            Lights = PluginKeyNames.Lights(table),
            Slots = RoyalKludgeBytes,
            Picture = picture,
        };

        /// <summary>The bytes of a picture. A key's LED index in these plugins
        /// is the byte of the color data its color starts at.</summary>
        internal const int RoyalKludgeBytes = 425;

        /// <summary>Five reports, then two empty ones the plugins send for zones
        /// 6 and 7. The RK61 takes its colors green first and waits after a
        /// report, the RK84 red first and waits before one.</summary>
        internal static IEnumerable<PluginStep> RoyalKludgePicture(int[] colors, bool greenFirst, bool waitAfter)
        {
            var data = new byte[RoyalKludgeBytes];
            for (int at = 0; at + 2 < RoyalKludgeBytes; at++)
            {
                int color = colors[at];
                if (color == 0) continue;
                data[at] = greenFirst ? G(color) : R(color);
                data[at + 1] = greenFirst ? R(color) : G(color);
                data[at + 2] = B(color);
            }
            var first = Frame(65, 0x0A, 0x07, 0x01, 0x06);
            Array.Copy(data, 0, first, 4, 61);
            yield return PluginStep.Feature(first);
            int from = 61;
            for (byte zone = 2; zone <= 5; zone++)
            {
                var frame = Frame(65, 0x0A, 0x07, zone);
                Array.Copy(data, from, frame, 3, 62);
                from += 62;
                if (!waitAfter) yield return PluginStep.Wait(1);
                yield return PluginStep.Feature(frame);
                if (waitAfter) yield return PluginStep.Wait(1);
            }
            for (byte zone = 6; zone <= 7; zone++)
            {
                if (!waitAfter) yield return PluginStep.Wait(1);
                yield return PluginStep.Feature(Frame(65, 0x0A, 0x07, zone));
            }
        }

        // ── NZXT Function keyboards (Nzxt/Peripheral Protocol/*.js) ──

        private static IEnumerable<PluginDevice> NzxtFunction()
        {
            const string Folder = "Nzxt/Peripheral Protocol/";
            // setISO and setMTKL (NZXT_Function_Keyboard.js:121-127).
            foreach (var (name, pid, iso, mini) in new (string, ushort, bool, bool)[]
            {
                ("NZXT Function", 0x2103, false, false),
                ("NZXT Function", 0x2106, false, false),
                ("NZXT Function MiniTKL", 0x2105, false, true),
                ("NZXT Function MiniTKL", 0x2108, true, true),
                ("NZXT Function TKL", 0x2107, true, false),
                ("NZXT Function TKL", 0x2104, false, false),
                ("NZXT Function 2", 0x2130, false, false),
                ("NZXT Function 2", 0x2131, true, false),
                ("NZXT Function 2", 0x2136, false, false),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x1E71,
                    ProductId = pid,
                    Plugin = Folder + "NZXT_Function_Keyboard.js",
                    Interface = 1,
                    OutputLength = 65,
                    Lights = Zones(10),
                    Slots = 10,
                    Programs = new[] { VendorPrograms.NzxtCam },
                    Start = new[]
                    {
                        PluginStep.Output(Frame(64, 0x43, 0x81, 0x00, 0x84)),
                        PluginStep.Output(Frame(64, 0x43, 0x81, 0x00, 0x86)),
                        PluginStep.Output(Frame(64, 0x43, 0x82, 0x00, 0x41, 0x64)),
                        PluginStep.Output(Frame(64, 0x43, 0x97, 0x00, 0x10, 0x01)),
                    },
                    Picture = colors => FunctionPicture(colors, iso, mini),
                    AskVariant = (io, _, _) =>
                    {
                        Func<int[], IEnumerable<PluginStep>> picture = colors => FunctionPicture(colors, iso, mini);
                        if (FunctionFirmwareIsNew(io)) picture = FunctionNewPicture;
                        return new PluginVariant(Zones(10), 10, picture);
                    },
                };
        }

        /// <summary>Whether the firmware's version, the third to fifth bytes of its
        /// second answer, is 1.3.71 or later, where the keyboard takes one report
        /// (Initialize and fetchFWVersion, NZXT_Function_Keyboard.js). A keyboard
        /// that does not answer keeps the older frames.</summary>
        internal static bool FunctionFirmwareIsNew(IHidReportChannel io)
        {
            if (!io.WriteOutput(Frame(64, 0x43, 0x81, 0x00, 0x01))) return false;
            var answer = new byte[Math.Max(io.InputReportLength, 64)];
            if (io.ReadInput(answer, AskMs) <= 0 || io.ReadInput(answer, AskMs) <= 0) return false;
            int version = answer[3] << 16 | answer[4] << 8 | answer[5];
            return version >= (1 << 16 | 3 << 8 | 71);
        }

        /// <summary>All ten zones in one report, with every key of every bank on,
        /// then the plugin's closing report.</summary>
        internal static IEnumerable<PluginStep> FunctionNewPicture(int[] colors)
        {
            var frame = new byte[64];
            new byte[] { 0x43, 0xBD, 0x01, 0x10, 0x02 }.CopyTo(frame, 0);
            Array.Fill(frame, (byte)0xFF, 5, 18);
            frame[23] = 0x00;
            frame[24] = 0x0A;
            for (int z = 0; z < 10; z++) PutRgb(frame, 25 + z * 4, colors[z]);
            yield return PluginStep.Output(frame);
            yield return PluginStep.Output(Frame(64, 0x43, 0x01));
        }

        /// <summary>Four reports that carry a keyboard's ten zones between fixed
        /// bytes, each zone's color 22 bytes on from the last.</summary>
        internal static IEnumerable<PluginStep> FunctionPicture(int[] colors, bool iso, bool mini)
        {
            var one = Frame(65, 0x43, 0xBD, 0x03, 0x10, 0x01, 0x01, 0x00, 0x03, 0x00, 0x03, 0x00, 0x03, 0x00,
                iso ? (byte)0x03 : (byte)0x01, 0x00, 0x03);
            foreach (var (at, value) in new (int, byte)[]
            {
                (0x1A, 0x01), (0x1B, 0x06), (29, 0x0C), (31, 0x0C), (33, 0x0C), (35, 0x0C), (37, 0x04), (48, 0x01),
                (49, 0x60), (51, 0xC0), (53, 0xC0), (55, 0xC0), (57, 0xC0), (59, 0x20),
            })
                one[at] = value;
            for (int z = 0; z < 2; z++) PutRgb(one, z * 22 + 23, colors[z]);
            yield return PluginStep.Output(one);

            var two = Frame(65, 0x43, 0x3D, 0x02);
            foreach (var (at, value) in new (int, byte)[]
            {
                (0x09, 0x01), (0x0A, 0x80), (0x0B, 0x01), (0x0D, 0x03), (0x0F, 0x03), (0x11, 0x03), (0x13, 0x03),
                (0x1F, 0x01), (0x2C, 0xC0), (0x2E, 0x46), (0x2F, 0x0E), (0x35, 0x01),
            })
                two[at] = value;
            for (int z = 0; z < 3; z++) PutRgb(two, z * 22 + 6, colors[z + 2]);
            yield return PluginStep.Output(two);

            var three = Frame(65, 0x43, 0x3D, 0x01, 0x00, 0x00, 0x32, 0x04, 0xB0, 0x11);
            foreach (var (at, value) in new (int, byte)[]
            {
                (0x0E, 0x01), (0x10, 0x40), (0x18, 0x40), (0x1A, 0x40), (0x1B, 0x08), (0x1C, 0x43), (0x1D, 0x08), (0x1E, 0x40),
                (0x24, 0x01), (0x26, mini ? (byte)0xF0 : (byte)0x30), (0x28, 0x40),
                (0x2A, mini ? (byte)0xE0 : (byte)0x60),
                (0x2C, iso ? (byte)0x70 : (byte)0x60), (0x2E, 0x20), (0x30, 0x34),
                (58, 0x01), (60, 0x0E), (62, 0x1C),
            })
                three[at] = value;
            for (int z = 0; z < 3; z++) PutRgb(three, z * 22 + 11, colors[z + 5]);
            yield return PluginStep.Output(three);

            var four = Frame(65, 0x43, 0x26, 0x00, 0x1C, 0x00, 0x0C, 0x00, 0x0C, 0x00, 0x0A);
            foreach (var (at, value) in new (int, byte)[] { (0x13, 0x01), (0x14, 0x18), (0x16, 0x30), (0x18, 0x30), (0x1A, 0x30), (0x1C, 0x30) })
                four[at] = value;
            for (int z = 0; z < 2; z++) PutRgb(four, z * 22 + 16, colors[z + 8]);
            yield return PluginStep.Output(four);
        }

        // ── Glorious Model O and D Wireless (Glorious/*.js) ──

        private static IEnumerable<PluginDevice> Glorious()
        {
            foreach (var (name, pid, plugin, wired) in new (string, ushort, string, bool)[]
            {
                ("Glorious Model O Wireless", 0x2024, "Glorious_Model_O_Wireless.js", false),
                ("Glorious Model O Wireless", 0x2026, "Glorious_Model_O_Wireless.js", false),
                ("Glorious Model D Wireless", 0x2025, "Glorious_Model_D_Wireless.js", false),
                ("Glorious Model O Wireless - Wired", 0x2013, "Glorious_Model_O_Wireless_-_Wired.js", true),
                ("Glorious Model D Wireless - Wired", 0x2014, "Glorious_Model_D_Wireless_-_Wired.js", true),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x258A,
                    ProductId = pid,
                    Plugin = "Glorious/" + plugin,
                    Interface = 2,
                    FeatureLength = 65,
                    Lights = Zones(1),
                    Slots = 1,
                    Programs = new[] { VendorPrograms.GloriousCore },
                    Picture = colors => GloriousPicture(colors, wired),
                    HandBack = wired ? Array.Empty<PluginStep>() : new[]
                    {
                        PluginStep.Feature(Frame(65, 0x00, 0x00, 0x00, 0x02, 0x05, 0x02, 0x00, 0x01, 0xFF, 0x01, 0x00, 0x09)),
                        PluginStep.Feature(Frame(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x01, 0xFF)),
                        PluginStep.Feature(Frame(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0xFF)),
                    },
                };
        }

        /// <summary>The color report. The wired plugins follow it with the two
        /// reports the wireless ones keep for their Shutdown.</summary>
        internal static IEnumerable<PluginStep> GloriousPicture(int[] colors, bool wired)
        {
            var frame = Frame(65, 0x00, 0x00, 0x00, 0x02, 0x08, 0x02, 0x00, 0x01, 0xFF, 0x04, 0x00, 0x09);
            PutRgb(frame, 12, colors[0]);
            yield return PluginStep.Feature(frame);
            if (!wired) yield break;
            yield return PluginStep.Feature(Frame(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x01, 0xFF));
            yield return PluginStep.Feature(Frame(65, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02, 0x02, 0x00, 0xFF));
        }

        // ── SteelSeries Apex M750 TKL and Apex Pro Mini (Steelseries/*.js) ──

        private static IEnumerable<PluginDevice> SteelSeriesKeyboards()
        {
            foreach (var (name, pid) in new (string, ushort)[] { ("SteelSeries Apex M750 TKL", 0x0617), ("SteelSeries Apex M750 TKL PUBG Edition", 0x0619) })
            yield return new PluginDevice
            {
                Name = name,
                VendorId = 0x1038,
                ProductId = pid,
                Plugin = PluginKeyTables.ApexM750Tkl.Plugin,
                Interface = 2,
                FeatureLength = 513,
                Lights = PluginKeyNames.Lights(PluginKeyTables.ApexM750Tkl),
                Slots = (513 - 9) / 3,
                Programs = new[] { VendorPrograms.SteelSeriesGG },
                Start = new[]
                {
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x81)),
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x04, 0x01)),
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x05, 0x01, 0x01, 0x01, 0x00, 0xEB, 0x0C, 0x0C)),
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x00, 0x00, 0x00, 0x01)),
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x04, 0x00, 0x2D, 0x01)),
                    PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x00, 0xFE, 0x00, 0x06)),
                },
                Picture = ApexM750Picture,
            };
            foreach (var (pid, number, command) in new (ushort, int, byte)[] { (0x1626, 3, 0x21), (0x161E, 1, 0x40) })
                yield return new PluginDevice
                {
                    Name = "SteelSeries Apex Pro Mini",
                    VendorId = 0x1038,
                    ProductId = pid,
                    Plugin = PluginKeyTables.ApexProMini.Plugin,
                    Interface = number,
                    FeatureLength = 642,
                    Lights = PluginKeyNames.Lights(PluginKeyTables.ApexProMini),
                    Slots = 256,
                    Programs = new[] { VendorPrograms.SteelSeriesGG },
                    Picture = colors => ApexProMiniPicture(colors, command),
                };
        }

        internal static IEnumerable<PluginStep> ApexM750Picture(int[] colors)
        {
            var frame = Frame(513, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x01, 0x03, 0x06, 0x12);
            foreach (int index in PluginKeyTables.ApexM750Tkl.Indexes) PutRgb(frame, index * 3 + 9, colors[index]);
            yield return PluginStep.Feature(frame);
            yield return PluginStep.Feature(Frame(513, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x04, 0x00, 0x2D, 0x01));
            yield return PluginStep.Wait(2);
        }

        internal static IEnumerable<PluginStep> ApexProMiniPicture(int[] colors, byte command)
        {
            var indexes = PluginKeyTables.ApexProMini.Indexes;
            var frame = Frame(642, 0x00, command, (byte)indexes.Length);
            for (int i = 0; i < indexes.Length; i++)
            {
                frame[3 + i * 4] = (byte)indexes[i];
                PutRgb(frame, 4 + i * 4, colors[indexes[i]]);
            }
            yield return PluginStep.Feature(frame);
        }

        // ── LED strip controllers: Razer, Vertagear and SRGBmods ──

        private static byte[] ChromaReport(params (int At, byte Value)[] bytes)
        {
            var report = Frame(91, bytes);
            report[89] = RazerCrc(report);
            return report;
        }

        private static IEnumerable<PluginDevice> StripControllers()
        {
            yield return new PluginDevice
            {
                Name = "Razer Chroma Light Strip Set",
                VendorId = 0x1532,
                ProductId = 0x0F2C,
                Plugin = "Razer/Razer_Chroma_Light_Strip_Set.js",
                Interface = 1,
                UsagePage = 0x000C,
                Usage = 0x0001,
                FeatureLength = 381,
                Slots = 3 * StripSpan,
                StripMaxLeds = new[] { 120, 120, 120 },
                StripDefaultLeds = 80,
                Siblings = new[] { new HidSibling(0, 0x000C) },
                PictureWith = _ => null,
                SiblingStart = new[]
                {
                    PluginStep.Feature(ChromaReport((2, 0x1F), (6, 0x01), (8, 0x37), (9, 0x01))),
                    PluginStep.Feature(ChromaReport((2, 0x9F), (6, 0x02), (8, 0x47), (9, 0x01), (10, 0x01))),
                    PluginStep.Feature(ChromaReport((2, 0x1F), (6, 0x02), (8, 0x84), (10, 0x01))),
                    PluginStep.Feature(ChromaReport((2, 0x1F), (6, 0x02), (8, 0x04), (9, 0x03), (10, 0x01))),
                },
                SiblingHandBack = new[] { PluginStep.Feature(ChromaReport((2, 0x1F), (6, 0x01), (8, 0x37))) },
                StripPicture = ChromaStripPicture,
            };
            yield return new PluginDevice
            {
                Name = "Vertagear RGB LED Upgrade Kits",
                VendorId = 0x1E71,
                ProductId = 0x2004,
                Plugin = "Nzxt/Vertagear_RGB_LED_Upgrade_Kits.js",
                Interface = 0,
                OutputLength = 64,
                Slots = 2 * StripSpan,
                StripMaxLeds = new[] { 40, 40 },
                StripDefaultLeds = 40,
                StripPicture = VertagearPicture,
            };
            yield return new PluginDevice
            {
                Name = "SRGBmods Pico LC",
                VendorId = 0x16D0,
                ProductId = 0x1123,
                Plugin = "SRGBmods.net/SRGBmods_Pico_LC.js",
                Interface = 2,
                OutputLength = 65,
                Slots = 2 * StripSpan,
                StripMaxLeds = new[] { 512, 512 },
                StripDefaultLeds = 90,
                Start = new[] { PluginStep.Output(Frame(65, (2, 0x01))) },
                StripPicture = PicoPicture,
            };
            yield return new PluginDevice
            {
                Name = "SRGBmods LED Controller v1",
                VendorId = 0x16D0,
                ProductId = 0x1205,
                Plugin = "SRGBmods.net/SRGBmods_LC_v1.js",
                Interface = 2,
                OutputLength = 65,
                Slots = StripSpan,
                StripMaxLeds = new[] { 800 },
                StripDefaultLeds = 512,
                StripPicture = (colors, leds) => SrgbModsPicture(colors, leds[0], 20, 65,
                    (current, total) => new byte[] { 0x00, current, total, 0x00, 0xAA }),
            };
            yield return new PluginDevice
            {
                Name = "SRGBmods LED Controller v2",
                VendorId = 0x16D0,
                ProductId = 0x1201,
                Plugin = "SRGBmods.net/SRGBmods_LC_v2.js",
                Interface = 0,
                OutputLength = 513,
                Slots = StripSpan,
                StripMaxLeds = new[] { 4056 },
                StripDefaultLeds = 1014,
                StripPicture = (colors, leds) => SrgbModsPicture(colors, leds[0], 169, 513,
                    (current, total) => new byte[] { 0x00, current, 0x00, total, 0x00, 0xAA }),
            };
        }

        /// <summary>A strip's colors in order, whether its count was set.</summary>
        private static int[] StripLeds(int[] byAddress, int strip, int leds)
            => leds >= 0 ? StripColors(byAddress, strip, leds, counted: true) : StripColors(byAddress, strip, -leds, counted: false);

        /// <summary>A report per channel with the count less one, 10 ms apart.
        /// The third channel's report starts 84 where the others start 04.</summary>
        internal static IEnumerable<PluginStep> ChromaStripPicture(int[] byAddress, IReadOnlyList<int> leds)
        {
            for (int c = 0; c < 3; c++)
            {
                var colors = StripLeds(byAddress, c, leds[c]);
                var frame = Frame(381, 0x00, (byte)(c == 2 ? 0x84 : 0x04), (byte)c, (byte)c, 0x00, (byte)(colors.Length - 1));
                for (int i = 0; i < colors.Length; i++) PutRgb(frame, 6 + i * 3, colors[i]);
                yield return PluginStep.Feature(frame);
                yield return PluginStep.Wait(10);
            }
        }

        /// <summary>For each kit, reports of twenty LEDs green first, each with
        /// its answer read, then the submit report.</summary>
        internal static IEnumerable<PluginStep> VertagearPicture(int[] byAddress, IReadOnlyList<int> leds)
        {
            for (int c = 0; c < 2; c++)
            {
                var colors = StripLeds(byAddress, c, leds[c]);
                byte channel = (byte)(1 << c);
                int sent = 0;
                for (byte number = 0; sent < colors.Length; number++)
                {
                    var frame = Frame(64, 0x22, (byte)(0x10 | number), channel, 0x00);
                    int count = Math.Min(20, colors.Length - sent);
                    for (int i = 0; i < count; i++)
                    {
                        frame[4 + i * 3] = G(colors[sent + i]);
                        frame[5 + i * 3] = R(colors[sent + i]);
                        frame[6 + i * 3] = B(colors[sent + i]);
                    }
                    sent += count;
                    yield return PluginStep.Output(frame);
                    yield return PluginStep.ReadInput();
                }
                yield return PluginStep.Output(Frame(64, 0x22, 0xA0, channel, 0x00, 0x01, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x80, 0x00, 0x32, 0x00, 0x00, 0x01));
                yield return PluginStep.ReadInput();
            }
        }

        internal static IEnumerable<PluginStep> PicoPicture(int[] byAddress, IReadOnlyList<int> leds)
        {
            for (int c = 0; c < 2; c++)
            {
                var colors = StripLeds(byAddress, c, leds[c]);
                int total = (colors.Length + 19) / 20;
                for (int n = 0; n < total; n++)
                {
                    var frame = Frame(65, 0x00, (byte)(n + 1), 0x00, (byte)total, (byte)(c + 1));
                    int count = Math.Min(20, colors.Length - n * 20);
                    for (int i = 0; i < count; i++) PutRgb(frame, 5 + i * 3, colors[n * 20 + i]);
                    yield return PluginStep.Output(frame);
                }
            }
            yield return PluginStep.Wait(1);
        }

        internal static IEnumerable<PluginStep> SrgbModsPicture(int[] byAddress, int leds, int perReport, int length, Func<byte, byte, byte[]> head)
        {
            var colors = StripLeds(byAddress, 0, leds);
            int total = (colors.Length + perReport - 1) / perReport;
            for (int n = 0; n < total; n++)
            {
                var start = head((byte)(n + 1), (byte)total);
                var frame = Frame(length, start);
                int count = Math.Min(perReport, colors.Length - n * perReport);
                for (int i = 0; i < count; i++) PutRgb(frame, start.Length + i * 3, colors[n * perReport + i]);
                yield return PluginStep.Output(frame);
            }
        }

        // ── Keyboards flashed with SignalRGB's QMK firmware (QMK/**/*.js) ──

        /// <summary>
        /// Boards whose owner flashed QMK with SignalRGB's protocol. Their stock
        /// firmware has the same IDs and no such protocol, so a session first asks
        /// for the firmware type (00 28), which only that firmware answers, with 01
        /// for mainline QMK or 02 for VIA (requestFirmwareType, Keychron_Q1_QMK_Keyboard.js).
        /// With no such answer the board stays dark.
        ///
        /// <para>A session turns the protocol's lighting on (00 25), and a picture
        /// goes out nine LEDs to a report (00 24, the first LED, the count, the
        /// colors). The hand-back turns it off (00 26), and the board's own effect
        /// returns. The GMMK Pro's plugin asks its user whether the board is ANSI
        /// or ISO and nothing on the wire says, so PadForge lists its ANSI keys.
        /// The GMMK 2 96%'s second ID, 0x505B, is an EVision board in OpenRGB, whose
        /// family lights it, and so stays there. The plugin for the Redragon K556
        /// names no IDs.</para>
        /// </summary>
        private static IEnumerable<PluginDevice> QmkBoards()
        {
            foreach (var board in PluginQmkTables.Boards)
                foreach (ushort product in board.Products)
                {
                    var table = new Lazy<PluginKeyTable>(board.Table);
                    yield return new PluginDevice
                    {
                        Name = board.Name,
                        VendorId = board.Vendor,
                        ProductId = product,
                        Plugin = board.Plugin,
                        Interface = 1,
                        OutputLength = QmkReportLength,
                        BuildLights = () => PluginKeyNames.Lights(table.Value),
                        // At most what one byte of a report can number.
                        Slots = 256,
                        Answers = QmkAnswers,
                        // Most of these ship with VIA on them, and take a color through it
                        // with no reflash.
                        Otherwise = (io, wait) => QmkRawHidLighting.Begin(io, wait, QmkLighting.Product(board.Vendor, product)),
                        AskVariant = (io, _, wait) => QmkVariant(board, table, io, wait),
                        Start = new[] { PluginStep.Output(Frame(32, 0x00, 0x25)), PluginStep.Wait(QmkCommandWaitMs) },
                        Picture = colors => QmkPicture(colors),
                        HandBack = new[] { PluginStep.Output(Frame(32, 0x00, 0x26)), PluginStep.Wait(QmkCommandWaitMs) },
                    };
                }
        }

        /// <summary>The board's table, and for a plugin that tells its layouts
        /// apart by the firmware, the one the third byte of its unique identifier
        /// names (returnUniqueIdentifier, GMMK_Pro_QMK_Keyboard.js and
        /// GMMK_3_QMK_Keyboard.js).</summary>
        internal static PluginVariant QmkVariant(PluginQmkBoard board, Lazy<PluginKeyTable> table, IHidReportChannel io, Action<int> wait)
        {
            if (board.Variants == null) return VariantOf(table.Value, QmkPicture);
            byte? unique = QmkUniqueByte(io, wait);
            foreach (var (byteValue, variant) in board.Variants)
                if (byteValue == unique) return VariantOf(variant(), QmkPicture);
            foreach (var (byteValue, variant) in board.Variants)
                if (byteValue == null) return VariantOf(variant(), QmkPicture);
            return VariantOf(table.Value, QmkPicture);
        }

        internal static byte? QmkUniqueByte(IHidReportChannel io, Action<int> wait)
        {
            if (!io.WriteOutput(Frame(32, 0x00, 0x23))) return null;
            wait?.Invoke(QmkCommandWaitMs);
            var answer = new byte[Math.Max(io.InputReportLength, 33)];
            for (int i = 0; i < 8; i++)
            {
                if (io.ReadInput(answer, 10) <= 0) return null;
                if (answer[1] == 0x23) return answer[4];
            }
            return null;
        }

        internal const int QmkReportLength = 33;
        internal const int QmkCommandWaitMs = 30;

        /// <summary>The firmware type's two answers.</summary>
        internal static bool QmkAnswers(IHidReportChannel io, Action<int> wait)
        {
            if (!io.WriteOutput(Frame(32, 0x00, 0x28))) return false;
            wait?.Invoke(QmkCommandWaitMs);
            var answer = new byte[Math.Max(io.InputReportLength, 33)];
            // The board may hold other reports ahead of the answer, as the
            // plugin's read loop allows for.
            for (int i = 0; i < 8; i++)
            {
                if (io.ReadInput(answer, 10) <= 0) return false;
                if (answer[1] == 0x28) return answer[2] is 0x01 or 0x02;
            }
            return false;
        }

        internal static IEnumerable<PluginStep> QmkPicture(int[] colors)
        {
            for (int led = 0; led < colors.Length; led += 9)
            {
                int count = Math.Min(9, colors.Length - led);
                var frame = Frame(QmkReportLength, 0x00, 0x24, (byte)led, (byte)count);
                for (int i = 0; i < count; i++) PutRgb(frame, 4 + i * 3, colors[led + i]);
                yield return PluginStep.Output(frame);
            }
        }

        // ── Mountain Macropad (Mountain_Macropad.js) ──

        private static IEnumerable<PluginDevice> Macropad()
        {
            var third = new byte[65];
            Array.Fill(third, (byte)0xFF);
            new byte[] { 0x14, 0x2C, 0x0A, 0x00, 0xFF, 0x64 }.CopyTo(third, 0);
            third[64] = 0x00;
            yield return new PluginDevice
            {
                Name = "Mountain Macropad",
                VendorId = 0x3282,
                ProductId = 0x0008,
                Plugin = "Mountain/Mountain_Macropad.js",
                Interface = 2,
                OutputLength = 65,
                Lights = Enumerable.Range(0, 12).Select(i =>
                    new LightInfo(i, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.MacroKey, LabelNumber = i + 1 }).ToArray(),
                Slots = 12,
                Start = new[]
                {
                    PluginStep.Output(Frame(65, 0x00, 0x11)),
                    PluginStep.Output(Frame(65, 0x00, 0x14, 0x00, 0x00, 0x00, 0x01, 0x06)),
                    PluginStep.Output(third),
                },
                Picture = colors =>
                {
                    var frame = Frame(65, 0x00, 0x14, 0x2C, 0x00, 0x01, 0x00, 0x4B, 0x00);
                    for (int i = 0; i < 12; i++) PutRgb(frame, 8 + i * 3, colors[i]);
                    return new[] { PluginStep.Output(frame) };
                },
            };
        }

        // ── Rama Works M65-B (Rama_M65B.js) ──

        /// <summary>The LEDs the plugin sends, nine to a report. It reads each
        /// LED's color at its place on a grid fifteen wide, and its list of key
        /// names is another board's, so no source names these keys and Map Key
        /// Lights asks about each.</summary>
        internal const int RamaLeds = 81;

        private static IEnumerable<PluginDevice> RamaM65B()
        {
            yield return new PluginDevice
            {
                Name = "Rama Works M65-B",
                VendorId = 0x5241,
                ProductId = 0x065B,
                Plugin = "Rama/Rama_M65B.js",
                UsagePage = 0xFF60,
                Usage = 0x0061,
                OutputLength = 64,
                Lights = Enumerable.Range(0, RamaLeds).Select(i => new LightInfo(i, LightKind.Key, 0, LightKeySource.None)
                {
                    X = i % 15, Y = i / 15, Width = 1, Height = 1, HasPosition = true,
                }).ToArray(),
                Slots = RamaLeds,
                Picture = RamaPicture,
            };
        }

        internal static IEnumerable<PluginStep> RamaPicture(int[] colors)
        {
            for (int offset = 0; offset < RamaLeds; offset += 9)
            {
                var frame = Frame(64, 0x00, 0x07, 0x41, (byte)offset);
                for (int i = 0; i < 9; i++) PutRgb(frame, 4 + i * 3, colors[offset + i]);
                yield return PluginStep.Output(frame);
            }
            yield return PluginStep.Output(Frame(64, 0x00, 0x07, 0x42));
        }

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
            // Each light's bit, by the plugin's Leds (Alienware_Monitor_Gen2_Controller.js:196-228).
            foreach (var (name, pid, bits, lights) in new[]
            {
                ("Alienware AW3423DW", (ushort)0x100B, new byte[] { 1, 2, 4, 8 }, new[] { Edge(0), Edge(1), Logo(2), Status(3) }),
                ("Alienware AW3821DW", (ushort)0x1008, new byte[] { 4, 2, 1, 8 }, new[] { Edge(0), Edge(1), Logo(2), Status(3) }),
                ("Alienware AW2725DF", (ushort)0x1014, new byte[] { 1, 2, 8 }, new[] { Logo(0), Edge(1), Status(2) }),
            })
                yield return new PluginDevice
                {
                    Name = name,
                    VendorId = 0x187C,
                    ProductId = pid,
                    Plugin = "Alienware/Alienware_Monitor_Gen2_Controller.js",
                    Interface = 0,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 65,
                    // The middle light, the big back light, the back logo and the
                    // switch. The AW2725DF has the logo, the number and the switch.
                    Lights = lights,
                    Slots = lights.Length,
                    RepaintMs = AlienwareMonitorRepaintMs,
                    Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                    Start = new[]
                    {
                        PluginStep.Output(Frame(65, 0x00, 0x92, 0x37, 0x05, 0x00, 0x51, 0x82, 0xD0, 0xF4, 0x99)),
                    },
                    Picture = colors => AlienwareMonitorPicture(colors, bits),
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
        /// has no numbered reports, so PadForge sends 0. Byte 14 is getCRC: 110, the bytes from 5
        /// to 13 in XOR.</summary>
        internal static IEnumerable<PluginStep> AlienwareMonitorPicture(int[] colors, byte[] bits)
        {
            for (int i = 0; i < bits.Length; i++)
            {
                var frame = new byte[65];
                Array.Fill(frame, (byte)0xFF);
                frame[0] = 0x00;
                new byte[] { 0x92, 0x37, 0x0A, 0x00, 0x51, 0x87, 0xD0, 0x04, bits[i], R(colors[i]), G(colors[i]), B(colors[i]), 0x23 }
                    .CopyTo(frame, 1);
                int check = 110;
                for (int at = 5; at <= 13; at++) check ^= frame[at];
                frame[14] = (byte)check;
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
                Plugin = "Asus/ASUS_Monitor_Controller.js",
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
                Plugin = "Asus/Asus_Mouse.js",
                Interface = 0,
                OutputLength = 65,
                Lights = new[] { Logo(0), Wheel(1) },
                Slots = 2,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Picture = colors => Enumerable.Range(0, 2).Select(zone => PluginStep.Output(Frame(65,
                    0x00, 0x51, 0x28, (byte)zone, 0x00, 0x00, 0x04, R(colors[zone]), G(colors[zone]), B(colors[zone])))),
            };
            foreach (ushort claymore in new ushort[] { 0x196B, 0x1934 })
            yield return new PluginDevice
            {
                Name = "ASUS ROG Claymore II",
                VendorId = 0x0B05,
                ProductId = claymore,
                Plugin = PluginKeyTables.ClaymoreIiRight.Plugin,
                Interface = 1,
                UsagePage = 0xFF00,
                Usage = 0x0001,
                OutputLength = 65,
                Lights = PluginKeyNames.Lights(PluginKeyTables.ClaymoreIiRight),
                Slots = ClaymoreSlots,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                Siblings = new[] { new HidSibling(2, 0xFFC0) },
                PictureWith = siblings => ClaymorePictureWith(siblings),
                AskVariant = (io, siblings, _) => new PluginVariant(PluginKeyNames.Lights(PluginKeyTables.ClaymoreIiRight), ClaymoreSlots,
                    ClaymorePictureWith(siblings, ClaymoreStartsLeft(io))),
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
        /// <summary>The pad's place as the keyboard reports it: byte 17 of its answer,
        /// FF with no pad and 01 for the right (ASUS_ROG_Claymore_II.js).</summary>
        internal static bool ClaymoreStartsLeft(IHidReportChannel io)
        {
            if (!io.WriteOutput(Frame(65, 0x00, 0x12))) return false;
            var answer = new byte[Math.Max(io.InputReportLength, 65)];
            return io.ReadInput(answer, AskMs) > 0 && answer[17] != 0xFF && answer[17] != 0x01;
        }

        internal static Func<int[], IEnumerable<PluginStep>> ClaymorePictureWith(IReadOnlyList<IHidReportChannel> siblings, bool startsLeft = false)
        {
            bool left = startsLeft;
            var buffer = new byte[65];
            return colors =>
            {
                var plate = siblings.Count > 0 ? siblings[0] : null;
                while (plate != null && plate.ReadInput(buffer, 0) > 0)
                    if (buffer[0] == 0x03 && buffer[1] == 0x81 && buffer[2] == 0x04 && buffer[3] == 0x00)
                    {
                        // FF: no pad on it, which lights as the right.
                        if (buffer[4] is 0x01 or 0xFF) left = false;
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
                    // The plugin as master kept it, for firmware before 4.1.0.6.
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

        /// <summary>Firmware 4.1.0.6 and later moved to interface 2 and these frames
        /// (HyperX_PulseFire_Haste_Wireless_Mouse.js on beta).</summary>
        private static IEnumerable<PluginDevice> HasteWirelessNewFirmware()
        {
            foreach (ushort pid in new ushort[] { 0x048E, 0x028E })
                yield return new PluginDevice
                {
                    Name = "HyperX Pulsefire Haste Wireless",
                    VendorId = 0x03F0,
                    ProductId = pid,
                    Plugin = "HyperX/HyperX_PulseFire_Haste_Wireless_Mouse.js",
                    Interface = 2,
                    UsagePage = 0xFF00,
                    Usage = 0x0001,
                    OutputLength = 64,
                    // Two LEDs under the wheel.
                    Lights = new[] { Wheel(0), Edge(1) },
                    Slots = 2,
                    Programs = new[] { VendorPrograms.Ngenuity },
                    Start = new[]
                    {
                        PluginStep.Output(Frame(64, 0x00, 0x46)),
                        PluginStep.Output(Frame(64, 0x00, 0x57, 0x12, 0x01)),
                        PluginStep.Output(Frame(64, 0x00, 0x51)),
                    },
                    Picture = colors =>
                    {
                        var frame = Frame(64, 0x00, 0xD2, 0x00, 0x00, 0x08);
                        PutRgb(frame, 5, colors[0]);
                        PutRgb(frame, 8, colors[1]);
                        frame[11] = 0x64;
                        return new[] { PluginStep.Output(frame) };
                    },
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
                Start = new[] { PluginStep.Feature(Frame(64, (0, 0xA1), (1, 0x3C), (16, 0x01), (41, 0x64))) },
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
