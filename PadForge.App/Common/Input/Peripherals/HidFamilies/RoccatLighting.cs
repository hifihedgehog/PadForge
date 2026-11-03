using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Roccat's mice, mousepads, headset and Horde Aimo keyboard, each lit
    /// through the one HID collection OpenRGB's RoccatController detector
    /// registration names for it (RoccatControllerDetect.cpp:390-434), as
    /// OpenRGB lights it (#557). The Vulcan, Pyro and Magma keyboards, which
    /// take two collections, are <see cref="RoccatVulcanLighting"/>.
    ///
    /// <para>What each write does to the device, model by model.</para>
    ///
    /// <para>Burst Core, Burst Pro and Kone Pro. The control report
    /// 0E 06 01 01 00 FF puts the mouse under the host's control, and
    /// 0E 06 01 00 00 FF gives it back to the effect it stores
    /// (SwitchControl, RoccatBurstController.cpp:53-70 and
    /// RoccatKoneProController.cpp:53-70, which OpenRGB sends with false
    /// before it lets a stored mode play, line 153 of the first). The
    /// colors go out as report 0D 0B and are not stored (SendDirect, lines
    /// 72-89 of each). Hand-back: the control report with 00. SignalRGB
    /// sends 0E 06 00 00 00 FF there (Roccat_Burst_Core_Mouse.js:190-195,
    /// Roccat_Kone_Pro_Mouse.js:247-252), and OpenRGB's own bytes are kept.</para>
    ///
    /// <para>Kone XP. OpenRGB's Direct mode writes the profile's stored mode
    /// before it turns the host's control on (DeviceUpdateMode,
    /// RGBController_RoccatKoneXP.cpp:256-276). Eruption drives the same
    /// mouse with the control report alone and no profile write
    /// (roccat_kone_xp.rs:425-466, the report at lines 235-247), so that is
    /// the path here: EnableDirect and the ready wait
    /// (RoccatKoneXPController.cpp:153-170 and 292-307), then OpenRGB's
    /// report 0D 7A for the colors (lines 259-290). Hand-back: the control
    /// report off and the ready wait.</para>
    ///
    /// <para>Kone Aimo and Kone Aimo 16K. OpenRGB reads report 0 and sends
    /// the same control report on, then sends every color in one report
    /// 0D 2E and reads three bytes back after it, "swarm does it"
    /// (RoccatKoneAimoController.cpp:60-112). OpenRGB has no hand-back for
    /// them. SignalRGB's Kone plugin, for this product, sends
    /// 0E 06 00 00 00 FF to give it back (Roccat_Kone_Mouse.js:3 and
    /// 199-204), and the hand-back is that.</para>
    ///
    /// <para>Burst Pro Air, Kone Pro Air, Elo 7.1 and Horde Aimo. OpenRGB
    /// sends their colors with no step that gives the device back:
    /// the Burst Pro Air takes its Direct mode values once and then the
    /// colors (RoccatBurstProAirController.cpp:51-94), the Kone Pro Air
    /// its direct report (RoccatKoneProAirController.cpp:51-54 and 96-119),
    /// the Elo its start sequence and then one color
    /// (RoccatEloController.cpp:54-97), the Horde Aimo its first report and
    /// then six colors (RoccatHordeAimoController.cpp:30-96). SignalRGB
    /// paints a color of its own as it stops (Roccat_Elo_7.1 USB_Headset.js:51-54,
    /// Roccat_Kone_Pro_Air_Mouse.js:204-206,
    /// Roccat_Burst_Pro_Air_Mouse_Wired.js:355-357). No reference gives
    /// these back, so nothing goes out at the hand-back, and the device
    /// keeps the last colors PadForge sent until Swarm or a replug sets it
    /// again.</para>
    ///
    /// <para>Kova. Its only color write is its profile: OpenRGB reads the
    /// profile report 06, sets the custom-color flags, the mode and the
    /// two colors, and writes it back with its sum
    /// (RoccatKovaController.cpp:60-109 and 133-147). Eruption writes the
    /// same report for its Kova 2016, this product
    /// (roccat_kova_2016.rs, hwdevices/mod.rs:131). Every picture is a
    /// write into the mouse's memory, so a changed picture goes out once it
    /// has held for <see cref="StoredHoldMs"/> and no sooner than
    /// <see cref="StoredRepaintMs"/> after the one before, and the profile
    /// as the session first read it is saved in the settings file before
    /// the first write (<see cref="KeyboardProfileBackups"/>), as the
    /// Lenovo family saves its keyboard's profile. The hand-back writes it
    /// back and lets the backup go, and a run that ends without one leaves
    /// the backup for the next run to put back.</para>
    ///
    /// <para>Sense Aimo. OpenRGB's Direct mode writes the pad's mode report
    /// with mode 0x0B and the colors, and then sends the colors as report
    /// 03 (RGBController_RoccatSenseAimo.cpp:162-190,
    /// RoccatSenseAimoController.cpp:101-151). The mode report is the pad's
    /// stored setting, which OpenRGB reads back to show the mode the pad is
    /// in (GetMode, lines 69-99), so the session reads it, saves it the way
    /// the Kova's profile is saved, and its hand-back writes it back.
    /// SignalRGB lights the pad through report 01 instead
    /// (Roccat_AIMO_Mousepad.js:43-55), which no other reference sends.</para>
    ///
    /// <para>ROCCAT Swarm lights all of them, and while it runs the family
    /// leaves them to it (<see cref="VendorPrograms.RoccatSwarm"/>).</para>
    /// </summary>
    internal sealed class RoccatLighting : IHidLightingFamily
    {
        public static readonly RoccatLighting Instance = new();

        internal const string FamilyName = "RoccatController";

        /// <summary>The shortest time between two pictures of a device that
        /// does not store them. No reference bounds the rate, so the bound
        /// is PadForge's.</summary>
        internal const int RepaintMs = 50;

        /// <summary>For a device whose every picture is stored: how long a
        /// changed picture holds before it is written, and the shortest time
        /// between two writes, the Lenovo family's bounds
        /// (<see cref="LenovoLegionLighting.HoldMs"/>,
        /// <see cref="LenovoLegionLighting.RepaintMs"/>).</summary>
        internal const int StoredHoldMs = LenovoLegionLighting.HoldMs;
        internal const int StoredRepaintMs = LenovoLegionLighting.RepaintMs;

        /// <summary>How often a picture that waits for its backup to reach
        /// the settings file looks again.</summary>
        internal const int SavingMs = LenovoLegionLighting.SavingMs;

        /// <summary>How many times one run tries to put back what an earlier
        /// run left, before it leaves that to the next run.</summary>
        internal const int RepairTries = LenovoLegionLighting.RepairTries;

        public string Name => "Roccat";

        private readonly Dictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            foreach (var registration in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                if (registration.Detector == RoccatVulcanLighting.Detector) return null;
                lock (_gate)
                {
                    if (!_models.TryGetValue(registration.Name, out var model))
                    {
                        model = ModelOf(registration);
                        if (model == null) return null;
                        _models[registration.Name] = model;
                    }
                    return model;
                }
            }
            return null;
        }

        /// <summary>The model a registration names, by its detector
        /// function, or null for one this family does not light.</summary>
        internal HidLightingModel ModelOf(OpenRgbRegistration registration)
        {
            string product = $"roccat:{registration.VendorId:X4}:{registration.ProductId:X4}";
            HidLightingModel Direct(IReadOnlyList<LightInfo> lights, string source, Func<Protocol> protocol) => new()
            {
                Family = this,
                Name = registration.Name,
                Map = new LightMap(lights, source) { Product = product },
                Programs = VendorPrograms.Roccat,
                RepaintMs = RepaintMs,
                Begin = (io, wait) => new DirectSession(protocol(), wait),
            };
            switch (registration.Detector)
            {
                case "DetectRoccatBurstCoreControllers":
                    return Direct(new[] { Zone(0, 1) }, "OpenRGB RGBController_RoccatBurst.cpp:120-144", () => Burst(1));
                case "DetectRoccatBurstProControllers":
                    return Direct(new[] { Zone(0, 1), Logo(1) }, "OpenRGB RGBController_RoccatBurst.cpp:120-144", () => Burst(2));
                case "DetectRoccatKoneProControllers":
                    return Direct(new[] { Zone(0, 1), Zone(1, 2) }, "OpenRGB RGBController_RoccatKonePro.cpp:122-150", KonePro);
                case "DetectRoccatKoneXPControllers":
                    return Direct(KoneXpLights(), "OpenRGB RGBController_RoccatKoneXP.cpp:177-232", KoneXp);
                case "DetectRoccatMouseControllers":
                    return Direct(KoneAimoLights(), "OpenRGB RGBController_RoccatKoneAimo.cpp:58-157", KoneAimo);
                case "DetectRoccatBurstProAirControllers":
                    return Direct(new[] { Zone(0, 1), Logo(1), Zone(2, 2), Zone(3, 3) },
                        "OpenRGB RGBController_RoccatBurstProAir.cpp:95-121", BurstProAir);
                case "DetectRoccatKoneProAirControllers":
                    return Direct(new[] { Zone(0, 1), Zone(1, 2) }, "OpenRGB RGBController_RoccatKoneProAir.cpp:129-153", KoneProAir);
                case "DetectRoccatEloControllers":
                    return Direct(new[] { Zone(0, 1) }, "OpenRGB RGBController_RoccatElo.cpp:54-69", Elo);
                case "DetectRoccatHordeAimoKeyboardControllers":
                {
                    var lights = new LightInfo[HordeLeds];
                    for (int i = 0; i < lights.Length; i++) lights[i] = Zone(i, i + 1);
                    return Direct(lights, "OpenRGB RGBController_RoccatHordeAimo.cpp:53-71", Horde);
                }
                case "DetectRoccatKovaControllers":
                    return new HidLightingModel
                    {
                        Family = this,
                        Name = registration.Name,
                        Map = new LightMap(new[] { Zone(0, 1), Edge(1) }, "OpenRGB RGBController_RoccatKova.cpp:91-112") { Product = product },
                        Programs = VendorPrograms.Roccat,
                        HoldMs = StoredHoldMs,
                        RepaintMs = StoredRepaintMs,
                        Begin = (io, wait) => BeginKova(io, product),
                        Owed = () => Owed(product),
                        Repair = (io, wait) => Repair(io, product),
                    };
                case "DetectRoccatSenseAimoControllers":
                    return new HidLightingModel
                    {
                        Family = this,
                        Name = registration.Name,
                        Map = new LightMap(new[] { Edge(0), Edge(1) }, "OpenRGB RGBController_RoccatSenseAimo.cpp:141-160") { Product = product },
                        Programs = VendorPrograms.Roccat,
                        RepaintMs = RepaintMs,
                        Begin = (io, wait) => BeginSenseAimo(io, product),
                        Owed = () => Owed(product),
                        Repair = (io, wait) => Repair(io, product),
                    };
                default:
                    return null;
            }
        }

        private static LightInfo Zone(int address, int zone) => new(address, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };
        private static LightInfo Logo(int address) => new(address, LightKind.Logo, 0, LightKeySource.None);
        private static LightInfo Edge(int address) => new(address, LightKind.Edge, 0, LightKeySource.None);

        /// <summary>The Kone XP's 20 LEDs in OpenRGB's order: nine of the
        /// left strip, nine of the right, the wheel and the DPI button
        /// (RGBController_RoccatKoneXP.cpp:177-232).</summary>
        internal static LightInfo[] KoneXpLights()
        {
            var lights = new LightInfo[KoneXpLeds];
            for (int i = 0; i < 18; i++) lights[i] = Edge(i);
            lights[18] = Zone(18, 1);
            lights[19] = Zone(19, 2);
            return lights;
        }

        /// <summary>The Kone Aimo's 11 LEDs: the wheel, four of the left
        /// strip, four of the right, the lower left and the lower right, at
        /// the channels of RoccatKoneAimoController.h:30-37.</summary>
        internal static LightInfo[] KoneAimoLights()
        {
            var lights = new LightInfo[KoneAimoLeds];
            lights[0] = Zone(0, 1);
            for (int i = 1; i < KoneAimoLeds; i++) lights[i] = Edge(i);
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        private static byte R(int rgb) => (byte)(rgb >> 16);
        private static byte G(int rgb) => (byte)(rgb >> 8);
        private static byte B(int rgb) => (byte)rgb;
        private static int At(IReadOnlyList<int> colors, int i) => colors != null && i < colors.Count ? colors[i] : 0;

        /// <summary>The control report of the Burst, Kone Pro, Kone XP and
        /// Kone Aimo: on puts the device under the host's control
        /// (RoccatBurstController.cpp:58-70).</summary>
        internal static byte[] Control(bool on) => new byte[] { 0x0E, 0x06, 0x01, (byte)(on ? 0x01 : 0x00), 0x00, 0xFF };

        /// <summary>The report that gives a Kone Aimo back, SignalRGB's
        /// (Roccat_Kone_Mouse.js:202).</summary>
        internal static readonly byte[] KoneAimoRelease = { 0x0E, 0x06, 0x00, 0x00, 0x00, 0xFF };

        /// <summary>SendDirect of the Burst and the Kone Pro: 0D 0B, then
        /// red, green and blue per LED, 11 bytes (RoccatBurstController.h:183-188,
        /// RoccatBurstController.cpp:72-89, RoccatKoneProController.h:17-23).</summary>
        internal static byte[] SmallDirect(IReadOnlyList<int> colors, int leds)
        {
            var buf = new byte[11];
            buf[0] = 0x0D;
            buf[1] = 0x0B;
            for (int i = 0; i < leds; i++)
            {
                buf[0x02 + 3 * i] = R(At(colors, i));
                buf[0x03 + 3 * i] = G(At(colors, i));
                buf[0x04 + 3 * i] = B(At(colors, i));
            }
            return buf;
        }

        internal const int KoneXpLeds = 20;

        /// <summary>SendDirect of the Kone XP: 0D 7A, then per LED red,
        /// green, blue and FF FF FF, 122 bytes
        /// (RoccatKoneXPController.cpp:259-290).</summary>
        internal static byte[] KoneXpDirect(IReadOnlyList<int> colors)
        {
            var buf = new byte[0x7A];
            buf[0] = 0x0D;
            buf[1] = 0x7A;
            for (int i = 0; i < KoneXpLeds && i < (colors?.Count ?? 0); i++)
            {
                buf[0x02 + i * 6] = R(colors[i]);
                buf[0x03 + i * 6] = G(colors[i]);
                buf[0x04 + i * 6] = B(colors[i]);
                buf[0x05 + i * 6] = 0xFF;
                buf[0x06 + i * 6] = 0xFF;
                buf[0x07 + i * 6] = 0xFF;
            }
            return buf;
        }

        /// <summary>WaitUntilReady of the Kone XP (RoccatKoneXPController.cpp:292-307):
        /// report 04, four bytes, until byte 1 is 1, 100 reads at most, 200 ms
        /// apart. False when a read failed.</summary>
        internal static bool KoneXpReady(IHidReportChannel io, Action<int> wait)
        {
            var buf = new byte[4];
            buf[0] = 0x04;
            for (int i = 0; buf[1] != 1 && i < 100; i++)
            {
                if (i != 0) wait(200);
                if (!io.GetFeature(0x04, buf)) return false;
            }
            return true;
        }

        internal const int KoneAimoLeds = 11;

        /// <summary>The Kone Aimo's color report: 0D 2E, then red, green,
        /// blue and a fourth byte left 0 per LED, 46 bytes
        /// (RoccatKoneAimoController.cpp:25-27 and 85-107). OpenRGB reads
        /// three bytes of report 0D into the same buffer after each send
        /// (line 111), which can leave a byte of the answer in byte 1 of the
        /// next report. The report is built fresh each time, with 2E
        /// there.</summary>
        internal static byte[] KoneAimoDirect(IReadOnlyList<int> colors)
        {
            var buf = new byte[46];
            buf[0] = 0x0D;
            buf[1] = 0x2E;
            for (int i = 0; i < KoneAimoLeds; i++)
            {
                int at = 0x02 + i * 4;
                buf[at + 0] = R(At(colors, i));
                buf[at + 1] = G(At(colors, i));
                buf[at + 2] = B(At(colors, i));
            }
            return buf;
        }

        internal const int BurstProAirLeds = 4;

        /// <summary>The Burst Pro Air's mode report, SetModeValues
        /// (RoccatBurstProAirController.cpp:75-94), for its Direct mode:
        /// mode 0x01, speed 0, brightness FF (RGBController_RoccatBurstProAir.cpp:36-44,
        /// the speed a mode with none keeps, RGBControllerInterface.h:325-342).
        /// 30 bytes.</summary>
        internal static byte[] BurstProAirMode()
            => new byte[30] { 0x06, 0x01, 0x4C, 0x06, 0x06, 0x01, 0x00, 0xFF, 0x0F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>The Burst Pro Air's colors, SetColors (lines 51-73):
        /// 06 01 4C 06 14, then per LED its number from 1, FF and its color.</summary>
        internal static byte[] BurstProAirColors(IReadOnlyList<int> colors)
        {
            var buf = new byte[30];
            buf[0] = 0x06;
            buf[1] = 0x01;
            buf[2] = 0x4C;
            buf[3] = 0x06;
            buf[4] = 0x14;
            for (int i = 0; i < BurstProAirLeds; i++)
            {
                buf[5 + 5 * i] = (byte)(i + 1);
                buf[6 + 5 * i] = 0xFF;
                buf[7 + 5 * i] = R(At(colors, i));
                buf[8 + 5 * i] = G(At(colors, i));
                buf[9 + 5 * i] = B(At(colors, i));
            }
            return buf;
        }

        /// <summary>The Kone Pro Air's direct report, SendRGB with direct
        /// (RoccatKoneProAirController.cpp:51-54 and 96-119): output report
        /// 0, 10 10 0B 00, the Direct mode 09, the two LEDs' brightness and
        /// the brightness at their maximum 64, the speed at its maximum 0B,
        /// then the two colors (RoccatKoneProAirController.h:17-41). 16 bytes.</summary>
        internal static byte[] KoneProAirDirect(IReadOnlyList<int> colors)
        {
            var buf = new byte[16];
            buf[1] = 0x10;
            buf[2] = 0x10;
            buf[3] = 0x0B;
            buf[4] = 0x00;
            buf[5] = 0x09;
            buf[6] = 0x64;
            buf[7] = 0x64;
            buf[8] = 0x64;
            buf[9] = 0x0B;
            for (int i = 0; i < 2; i++)
            {
                buf[10 + 3 * i] = R(At(colors, i));
                buf[11 + 3 * i] = G(At(colors, i));
                buf[12 + 3 * i] = B(At(colors, i));
            }
            return buf;
        }

        /// <summary>An Elo report: output report FF, 16 bytes
        /// (RoccatEloController.h:18-20).</summary>
        internal static byte[] EloReport(byte command, byte b3 = 0, int rgb = 0)
        {
            var buf = new byte[16];
            buf[0] = 0xFF;
            buf[1] = command;
            buf[3] = b3;
            if (command == 0x04)
            {
                buf[4] = R(rgb);
                buf[5] = G(rgb);
                buf[6] = B(rgb);
            }
            return buf;
        }

        /// <summary>The Elo's start, SendInit (RoccatEloController.cpp:54-82):
        /// FF 01, FF 02, FF 03 with byte 3 set, the color black, FF 01.</summary>
        internal static byte[][] EloInit() => new[]
        {
            EloReport(0x01), EloReport(0x02), EloReport(0x03, b3: 0x01), EloReport(0x04, rgb: 0), EloReport(0x01),
        };

        internal const int HordeLeds = 6;

        /// <summary>The Horde Aimo's first report, InitialPacket
        /// (RoccatHordeAimoController.cpp:30-41): 13 08 01, eight bytes.</summary>
        internal static byte[] HordeInit() => new byte[] { 0x13, 0x08, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>The Horde Aimo's colors, SetColors (lines 66-92): report
        /// 18, its length 24, FF FF, six colors and a 16-bit sum of the
        /// bytes before it, low byte first.</summary>
        internal static byte[] HordeColors(IReadOnlyList<int> colors)
        {
            var buf = new byte[24];
            buf[0] = 0x18;
            buf[1] = 24;
            buf[2] = 0xFF;
            buf[3] = 0xFF;
            for (int i = 0; i < HordeLeds; i++)
            {
                buf[0x04 + i * 3] = R(At(colors, i));
                buf[0x05 + i * 3] = G(At(colors, i));
                buf[0x06 + i * 3] = B(At(colors, i));
            }
            int crc = 0;
            for (int i = 0; i < 22; i++) crc += buf[i];
            buf[22] = (byte)crc;
            buf[23] = (byte)(crc >> 8);
            return buf;
        }

        // ─────────────────────────────────────────────
        //  The sessions of the devices that store nothing
        // ─────────────────────────────────────────────

        /// <summary>What a device that stores none of its pictures takes:
        /// whether its reports are feature reports, the step that puts it
        /// under the host's control before its first picture, its picture,
        /// a read after each picture, and the step and frames that give it
        /// back.</summary>
        internal sealed class Protocol
        {
            public bool Feature { get; init; } = true;
            public Func<IHidReportChannel, Action<int>, bool> Start { get; init; }
            public Func<IReadOnlyList<int>, byte[]> Frame { get; init; }
            public Action<IHidReportChannel> After { get; init; }
            public Func<IHidReportChannel, Action<int>, bool> Release { get; init; }
            public byte[][] ReleaseFrames { get; init; } = Array.Empty<byte[]>();
        }

        private static Protocol Burst(int leds) => new()
        {
            Start = (io, _) => io.SetFeature(Control(true)),
            Frame = c => SmallDirect(c, leds),
            Release = (io, _) => io.SetFeature(Control(false)),
            ReleaseFrames = new[] { Control(false) },
        };

        private static Protocol KonePro() => Burst(2);

        private static Protocol KoneXp() => new()
        {
            Start = (io, wait) => io.SetFeature(Control(true)) && KoneXpReady(io, wait),
            Frame = KoneXpDirect,
            Release = (io, wait) => io.SetFeature(Control(false)) && KoneXpReady(io, wait),
            ReleaseFrames = new[] { Control(false) },
        };

        private static Protocol KoneAimo() => new()
        {
            // OpenRGB's read of report 0 and of report 0D: it ignores what
            // they answer and whether they answer (lines 67 and 111).
            Start = (io, _) =>
            {
                try { io.GetFeature(0x00, new byte[3]); } catch { }
                return io.SetFeature(Control(true));
            },
            Frame = KoneAimoDirect,
            After = io =>
            {
                var answer = new byte[3];
                answer[0] = 0x0D;
                try { io.GetFeature(0x0D, answer); } catch { }
            },
            Release = (io, _) => io.SetFeature(KoneAimoRelease),
            ReleaseFrames = new[] { KoneAimoRelease },
        };

        private static Protocol BurstProAir() => new()
        {
            Start = (io, _) => io.SetFeature(BurstProAirMode()),
            Frame = BurstProAirColors,
        };

        private static Protocol KoneProAir() => new()
        {
            Feature = false,
            Frame = KoneProAirDirect,
        };

        private static Protocol Elo() => new()
        {
            Feature = false,
            Start = (io, _) =>
            {
                foreach (var report in EloInit())
                    if (!io.WriteOutput(report)) return false;
                return true;
            },
            Frame = c => EloReport(0x04, rgb: At(c, 0)),
        };

        /// <summary>The Horde Aimo. OpenRGB reads a feature report after each
        /// picture into a buffer whose first byte, the report ID, it never
        /// sets (RoccatHordeAimoController.cpp:94-95), so which report it
        /// reads is not defined, and the session reads none.</summary>
        private static Protocol Horde() => new()
        {
            Start = (io, _) => io.SetFeature(HordeInit()),
            Frame = HordeColors,
        };

        internal sealed class DirectSession : HidLightingSession
        {
            private readonly Protocol _protocol;
            private readonly Action<int> _wait;
            private bool _started;
            private byte[][] _crash = Array.Empty<byte[]>();

            public DirectSession(Protocol protocol, Action<int> wait)
            {
                _protocol = protocol;
                _wait = wait;
            }

            public override bool Feature => _protocol.Feature;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                wait ??= _wait;
                if (!_started)
                {
                    // Marked first: a write that reports a failure may still
                    // have landed.
                    _crash = _protocol.ReleaseFrames;
                    if (_protocol.Start != null && !_protocol.Start(io, wait)) return HidLightingPaint.Failed;
                    _started = true;
                }
                var frame = _protocol.Frame(colors);
                bool sent = _protocol.Feature ? io.SetFeature(frame) : io.WriteOutput(frame);
                if (!sent) return HidLightingPaint.Failed;
                _protocol.After?.Invoke(io);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_started && _crash.Length == 0) return true;
                if (_protocol.Release != null && !_protocol.Release(io, wait ?? _wait)) return false;
                _started = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }

        // ─────────────────────────────────────────────
        //  The devices whose pictures are stored
        // ─────────────────────────────────────────────

        /// <summary>The Kova's reports (RoccatKovaController.h:17-62).</summary>
        internal const byte KovaInitReport = 0x04;
        internal const byte KovaProfileReport = 0x06;
        internal const int KovaProfileLength = 28;
        internal const byte KovaVersionReport = 0x09;
        internal const int KovaVersionLength = 8;
        internal const int KovaFlags = 14;
        internal const int KovaColorFlow = 15;
        internal const int KovaMode = 16;
        internal const int KovaSpeed = 17;
        internal const int KovaPipe = 18;
        internal const int KovaWheel = 22;
        internal const int KovaChecksum = 26;
        internal const byte KovaCustomColors = 0b0011_0000;
        internal const byte KovaLightsOn = 0b0000_0011;
        internal const byte KovaStatic = 0x01;

        /// <summary>SendInitialPacket (RoccatKovaController.cpp:111-118): 04 00 80.</summary>
        internal static byte[] KovaInit() => new byte[] { KovaInitReport, 0x00, 0x80 };

        /// <summary>The profile report SetColor writes for OpenRGB's Static
        /// mode (RoccatKovaController.cpp:60-109, RGBController_RoccatKova.cpp:129-141):
        /// the profile as read, with the report ID and length, the custom-color
        /// and lights-on flags set, the color flow off, mode 0x01 and speed 0
        /// (Static has none), the stripe at 18 and the wheel at 22, each a
        /// zero and red, green, blue, and the 16-bit sum of the 26 bytes
        /// before, low byte first.</summary>
        internal static byte[] KovaProfile(byte[] read, int wheel, int stripe)
        {
            var buf = new byte[KovaProfileLength];
            Array.Copy(read, buf, Math.Min(read.Length, KovaProfileLength));
            buf[0] = KovaProfileReport;
            buf[1] = KovaProfileLength;
            buf[KovaFlags] |= KovaCustomColors;
            buf[KovaFlags] |= KovaLightsOn;
            buf[KovaWheel] = 0x00;
            buf[KovaWheel + 1] = R(wheel);
            buf[KovaWheel + 2] = G(wheel);
            buf[KovaWheel + 3] = B(wheel);
            buf[KovaPipe] = 0x00;
            buf[KovaPipe + 1] = R(stripe);
            buf[KovaPipe + 2] = G(stripe);
            buf[KovaPipe + 3] = B(stripe);
            buf[KovaColorFlow] = 0;
            buf[KovaMode] = KovaStatic;
            buf[KovaSpeed] = 0;
            ushort sum = 0;
            for (int i = 0; i < KovaProfileLength - 2; i++) sum += buf[i];
            buf[KovaChecksum] = (byte)(sum & 0xFF);
            buf[KovaChecksum + 1] = (byte)(sum >> 8);
            return buf;
        }

        /// <summary>The Sense Aimo's mode report, its length and its Direct
        /// mode (RoccatSenseAimoController.cpp:69-128,
        /// RoccatSenseAimoController.h:19-24).</summary>
        internal const byte SenseModeReport = 0x02;
        internal const int SenseModeLength = 19;
        internal const byte SenseDirect = 0x0B;

        /// <summary>SetMode for the Direct mode (lines 101-128): 02, the
        /// profile, then per LED the mode, speed 0 and brightness 0 (the
        /// Direct mode has neither, RGBController_RoccatSenseAimo.cpp:37-42),
        /// a zero, the color and FF, and a zero for Swarm's theme.</summary>
        internal static byte[] SenseAimoMode(byte profile, IReadOnlyList<int> colors)
        {
            var buf = new byte[SenseModeLength];
            buf[0] = SenseModeReport;
            buf[1] = profile;
            for (int i = 0; i < 2; i++)
            {
                buf[0x02 + i * 8] = SenseDirect;
                buf[0x03 + i * 8] = 0;
                buf[0x04 + i * 8] = 0;
                buf[0x05 + i * 8] = 0x00;
                buf[0x06 + i * 8] = R(At(colors, i));
                buf[0x07 + i * 8] = G(At(colors, i));
                buf[0x08 + i * 8] = B(At(colors, i));
                buf[0x09 + i * 8] = 0xFF;
            }
            buf[0x12] = 0x00;
            return buf;
        }

        /// <summary>SendDirect of the Sense Aimo (lines 130-151): report 03,
        /// then per LED red, green, blue and FF.</summary>
        internal static byte[] SenseAimoDirect(IReadOnlyList<int> colors)
        {
            var buf = new byte[9];
            buf[0] = 0x03;
            for (int i = 0; i < 2; i++)
            {
                buf[0x01 + i * 4] = R(At(colors, i));
                buf[0x02 + i * 4] = G(At(colors, i));
                buf[0x03 + i * 4] = B(At(colors, i));
                buf[0x04 + i * 4] = 0xFF;
            }
            return buf;
        }

        private static bool Owed(string product)
            => KeyboardProfileBackups.Get(product) != null && KeyboardProfileBackups.Failures(product) < RepairTries;

        /// <summary>Writes back what an earlier run saved, with no claim on
        /// the device. True when nothing is owed any more.</summary>
        private static bool Repair(IHidReportChannel io, string product)
        {
            if (KeyboardProfileBackups.Get(product) is not { } backup) return true;
            if (io.SetFeature(backup.Contents))
            {
                KeyboardProfileBackups.Drop(product);
                return true;
            }
            KeyboardProfileBackups.NoteFailure(product);
            return false;
        }

        /// <summary>The report a session puts back: what an earlier run
        /// saved and never put back, else what the device holds now.</summary>
        private static byte[] Original(string product, byte[] read)
            => KeyboardProfileBackups.Get(product) is { } held && held.Contents.Length == read.Length ? held.Contents : read;

        private static HidLightingSession BeginKova(IHidReportChannel io, string product)
        {
            // OpenRGB's constructor: the init report, then the version
            // (RoccatKovaController.cpp:17-25 and 111-131).
            if (!io.SetFeature(KovaInit())) return null;
            var version = new byte[KovaVersionLength];
            version[0] = KovaVersionReport;
            io.GetFeature(KovaVersionReport, version);
            var read = ReadReport(io, KovaProfileReport, KovaProfileLength);
            if (read == null) return null;
            return new StoredSession(product, 0, Original(product, read), stored: true,
                (channel, original, colors) =>
                {
                    var now = ReadReport(channel, KovaProfileReport, KovaProfileLength);
                    return now != null && channel.SetFeature(KovaProfile(now, At(colors, 0), At(colors, 1)));
                });
        }

        private static HidLightingSession BeginSenseAimo(IHidReportChannel io, string product)
        {
            var read = ReadReport(io, SenseModeReport, SenseModeLength);
            if (read == null) return null;
            var original = Original(product, read);
            byte profile = original[1];
            bool moded = false;
            return new StoredSession(product, profile, original, stored: false,
                (channel, _, colors) =>
                {
                    if (!moded)
                    {
                        if (!channel.SetFeature(SenseAimoMode(profile, colors))) return false;
                        moded = true;
                    }
                    return channel.SetFeature(SenseAimoDirect(colors));
                });
        }

        /// <summary>A feature report read whole, its ID in byte 0, or
        /// null.</summary>
        internal static byte[] ReadReport(IHidReportChannel io, byte id, int length)
        {
            var buf = new byte[length];
            buf[0] = id;
            if (!io.GetFeature(id, buf)) return null;
            buf[0] = id;
            return buf;
        }

        /// <summary>A session on a device whose stored setting a picture
        /// changes. The setting as it stood is saved before the first write
        /// and written back at the hand-back.</summary>
        internal sealed class StoredSession : HidLightingSession
        {
            private readonly string _product;
            private readonly byte _profile;
            private readonly byte[] _original;
            private readonly Func<IHidReportChannel, byte[], IReadOnlyList<int>, bool> _paint;
            private bool _ours;
            private byte[][] _crash = Array.Empty<byte[]>();

            public StoredSession(string product, byte profile, byte[] original, bool stored,
                Func<IHidReportChannel, byte[], IReadOnlyList<int>, bool> paint)
            {
                _product = product;
                _profile = profile;
                _original = original;
                Stored = stored;
                _paint = paint;
            }

            /// <summary>Every picture is stored, not only the first.</summary>
            public bool Stored { get; }

            /// <summary>What the device held before PadForge wrote it.</summary>
            public byte[] Original => _original;

            public override bool Feature => true;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // The backup reaches the settings file before the first
                // write it guards.
                if (KeyboardProfileBackups.Get(_product) is not { } held || held.Profile != _profile
                    || !held.Contents.AsSpan().SequenceEqual(_original))
                    KeyboardProfileBackups.Take(_product, _profile, _original);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                // Marked first: a write that reports a failure may still
                // have landed.
                _ours = true;
                _crash = new[] { _original };
                return _paint(io, _original, colors) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_ours && !io.SetFeature(_original)) return false;
                _ours = false;
                _crash = Array.Empty<byte[]>();
                if (KeyboardProfileBackups.Get(_product) is { } held && held.Contents.AsSpan().SequenceEqual(_original))
                    KeyboardProfileBackups.Drop(_product);
                return true;
            }
        }
    }
}
