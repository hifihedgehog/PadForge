using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lighting controller of the Lenovo Legion Y740, the Legion 7 and
    /// 7S Gen 5 (Y750, Y750S) and the Legion 7 and 7S Gen 6 (Y760, Y760S),
    /// the ITE controller OpenRGB drives as LenovoUSBController (#557). The
    /// collection is the Spectrum keyboards' (usage page 0xFF89, usage
    /// 0x07, LenovoUSBControllerDetect.cpp:26-30 and 68-72), with
    /// 192-byte feature reports (LenovoUSBController.h:30).
    ///
    /// <para>The model of the device. OpenRGB puts the controller in its
    /// software mode when it opens it, instruction 0xB2, and every frame
    /// after that shows at once and is stored nowhere
    /// (LenovoUSBController.cpp:25 and 107-114, and "@save :x:" in
    /// RGBController_LenovoUSB.cpp:31). Instruction 0xB1 releases the
    /// controller to its own lighting, which OpenRGB sends when it lets the
    /// device go (RGBController_LenovoUSB.cpp:191-202,
    /// LenovoUSBController.cpp:116-125), and its note says software mode
    /// has not been seen to last across a restart. So a session sends
    /// 0xB2 when it starts, its pictures as they come, and 0xB1 at the
    /// hand-back and on the crash path. A run that ends without either
    /// leaves the keyboard on its last picture until the next restart, so
    /// a session records that in <see cref="KeyboardProfileBackups"/>
    /// before its first picture, and the next run sends 0xB1 for it.
    /// The information queries (0x01, 0x04, 0x07) only read.</para>
    ///
    /// <para>The frames. One feature report per zone and per 47 lights:
    /// 0x07, 0xA0 plus the zone, the light count, 0, and each light's number
    /// and red, green and blue (setZoneLeds, LenovoUSBController.cpp:48-79,
    /// LENOVO_MAX_LEDS_PER_PACKET in LenovoUSBController.h:31). A picture
    /// goes out zone by zone in OpenRGB's light order, a new run of reports
    /// at each change of zone (DeviceUpdateLEDs,
    /// RGBController_LenovoUSB.cpp:379-404).</para>
    ///
    /// <para>The board. OpenRGB reads which board the laptop has from an
    /// information query per model and picks its table by it: the Y740's
    /// 15 or 17 inch chassis and its ANSI or ISO board, the Y750's, the
    /// Y760's and the S models' ANSI, ISO or Japanese board
    /// (RGBController_LenovoUSB.cpp:89-184 and 204-322). A session reads
    /// the same and lights the board's own table (<see cref="Session.Map"/>).
    /// The worker publishes that map from then on, for as long as the
    /// keyboard stays. Until a session has read it, the published map is
    /// OpenRGB's default, the ANSI board and on the Y740 the 17 inch one
    /// (lines 87 and 226-227).</para>
    /// </summary>
    internal sealed class LenovoLegionUsbLighting : IHidLightingFamily
    {
        public static readonly LenovoLegionUsbLighting Instance = new();

        internal const ushort VendorId = 0x048D;
        internal const ushort UsagePage = 0xFF89;
        internal const ushort Usage = 0x07;

        /// <summary>LENOVO_HID_PACKET_SIZE, the report ID included
        /// (LenovoUSBController.h:30).</summary>
        internal const int PacketSize = 192;

        /// <summary>LENOVO_INSTRUCTION_START and LENOVO_ZONE_ID_0
        /// (LenovoUSBController.h:27-28).</summary>
        internal const byte ReportId = 0x07;
        internal const byte ZoneBase = 0xA0;

        /// <summary>LENOVO_MAX_LEDS_PER_PACKET (LenovoUSBController.h:31).</summary>
        internal const int MaxPerReport = 0x2F;

        /// <summary>The software and hardware mode instructions
        /// (LenovoUSBController.cpp:113 and 124).</summary>
        internal const byte SoftwareMode = 0xB2;
        internal const byte HardwareMode = 0xB1;

        /// <summary>The product IDs (LenovoDevices.h:29-33).</summary>
        internal const ushort LegionY740 = 0xC935;
        internal const ushort LegionY750 = 0xC956;
        internal const ushort LegionY750S = 0xC957;
        internal const ushort LegionY760 = 0xC968;
        internal const ushort LegionY760S = 0xC967;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, and a picture is up to eight reports, so the bound is
        /// PadForge's.</summary>
        internal const int RepaintMs = 33;

        /// <summary>How many times one run tries to hand back a keyboard an
        /// earlier run left in software mode.</summary>
        internal const int RepairTries = 3;

        /// <summary>How often a picture that waits for the owed hand-back to
        /// reach the settings file looks again.</summary>
        internal const int SavingMs = 100;

        /// <summary>What a session records in
        /// <see cref="KeyboardProfileBackups"/>, profile 0: the hand-back's
        /// first bytes, standing for "this keyboard is in software
        /// mode".</summary>
        internal static readonly byte[] SoftwareModeRecord = { ReportId, HardwareMode, 0x00 };

        public string Name => "Lenovo Legion (Y740 to Y760)";

        /// <summary>The boards OpenRGB tells apart.</summary>
        internal enum Board
        {
            Ansi,
            Iso,
            Japan,
            Ansi15,
            Iso15,
        }

        private static readonly Dictionary<ushort, Lazy<HidLightingModel>> s_models = new()
        {
            [LegionY740] = new(() => Model("Lenovo Legion Y740", LegionY740)),
            [LegionY750] = new(() => Model("Lenovo Legion 7 Gen 5", LegionY750)),
            [LegionY750S] = new(() => Model("Lenovo Legion 7S Gen 5", LegionY750S)),
            [LegionY760] = new(() => Model("Lenovo Legion 7 Gen 6", LegionY760)),
            [LegionY760S] = new(() => Model("Lenovo Legion 7S Gen 6", LegionY760S)),
        };

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            return s_models.TryGetValue(collection.ProductId, out var model) ? model.Value : null;
        }

        private static HidLightingModel Model(string name, ushort productId)
        {
            string product = $"lenovo:{VendorId:X4}:{productId:X4}";
            var lights = Lights(productId, Board.Ansi);
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(lights, "OpenRGB LenovoDevices.h") { Product = product },
                LitBesideWindows = true,
                Programs = VendorPrograms.LenovoLegionUsb,
                HoldMs = 0,
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Begin(io, wait, productId, product),
                Owed = _ => KeyboardProfileBackups.Get(product) != null
                             && KeyboardProfileBackups.Failures(product) < RepairTries,
                Repair = (io, _) => Repair(io, product),
            };
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The zones OpenRGB gives a model and board, in its order
        /// (SetupZones, RGBController_LenovoUSB.cpp:204-303).</summary>
        internal static IReadOnlyList<LightInfo> Lights(ushort productId, Board board)
        {
            var zones = new List<(LenovoZone, LightKind)>();
            switch (productId)
            {
                case LegionY740:
                    zones.Add(board switch
                    {
                        Board.Iso15 => (LenovoLights.LenovoLegionY74015KbdIso, LightKind.Other),
                        Board.Ansi15 => (LenovoLights.LenovoLegionY74015KbdAnsi, LightKind.Other),
                        Board.Iso => (LenovoLights.LenovoLegionY74017KbdIso, LightKind.Other),
                        _ => (LenovoLights.LenovoLegionY74017KbdAnsi, LightKind.Other),
                    });
                    zones.Add((LenovoLights.LenovoLegionY740Logo, LightKind.Logo));
                    zones.Add((LenovoLights.LenovoLegionY740Pwrbtn, LightKind.Other));
                    zones.Add((LenovoLights.LenovoLegionY740Vents, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY740Ports, LightKind.Edge));
                    break;
                case LegionY750:
                    zones.Add(Keyboard760(board));
                    zones.Add((LenovoLights.LenovoLegionY750Logo, LightKind.Logo));
                    zones.Add((LenovoLights.LenovoLegionY750Vents, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY750Neon, LightKind.Edge));
                    break;
                case LegionY750S:
                case LegionY760S:
                    zones.Add(Keyboard760(board));
                    break;
                case LegionY760:
                    zones.Add(Keyboard760(board));
                    zones.Add((LenovoLights.LenovoLegionY760Logo, LightKind.Logo));
                    zones.Add((LenovoLights.LenovoLegionY760VentLeft, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY760VentRight, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY760VentBackRight, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY760VentBackLeft, LightKind.Edge));
                    zones.Add((LenovoLights.LenovoLegionY760Neon, LightKind.Edge));
                    break;
            }
            return LenovoLights.Build(zones, Usages(productId, board));
        }

        private static (LenovoZone, LightKind) Keyboard760(Board board) => board switch
        {
            Board.Japan => (LenovoLights.LenovoLegionY760KbdJp, LightKind.Other),
            Board.Iso => (LenovoLights.LenovoLegionY760KbdIso, LightKind.Other),
            _ => (LenovoLights.LenovoLegionY760KbdAnsi, LightKind.Other),
        };

        /// <summary>The keyboard-zone ID of these boards (LenovoDevices.h:1413).</summary>
        private const int Kbd = 1 << 8;

        /// <summary>Where OpenRGB's name for a light names another key than
        /// the one it lights. The Y760 boards light the legends of a key
        /// apart from the key, and the tables name a legend light by its
        /// symbol: in the ANSI table the # over 3 is KEY_EN_POUND, the name
        /// of the ISO # key (LenovoDevices.h:191), so it is left unnamed (0)
        /// as the other legends are. The Japanese table names the legends #,
        /// = and ` by keys they do not sit on (lines 525, 533 and 572), and
        /// names the JIS ^, @, [, :, ] and ろ keys by names no usage table
        /// gives or by the ANSI keys of other places (lines 554, 573, 575,
        /// 598, 600 and 623): each takes the usage of its place on a JIS
        /// board (HID Usage Tables 1.6, section 10). Its 半角/全角 key's
        /// second light is "kanji" (line 542).</summary>
        private static IReadOnlyDictionary<int, ushort> Usages(ushort productId, Board board)
        {
            if (productId == LegionY740) return null;
            return board switch
            {
                Board.Ansi => new Dictionary<int, ushort> { [Kbd | 0x19] = 0 },
                Board.Japan => new Dictionary<int, ushort>
                {
                    [Kbd | 0x19] = 0,
                    [Kbd | 0x21] = 0,
                    [Kbd | 0x4C] = 0,
                    [Kbd | 0x2B] = 0x35,
                    [Kbd | 0x37] = 0x2E,
                    [Kbd | 0x61] = 0x2F,
                    [Kbd | 0x62] = 0x30,
                    [Kbd | 0x60] = 0x34,
                    [Kbd | 0x78] = 0x32,
                    [Kbd | 0x8C] = 0x87,
                },
                _ => null,
            };
        }

        /// <summary>The board an information answer names, OpenRGB's rule per
        /// model (RGBController_LenovoUSB.cpp:89-184): the answer's byte 4,
        /// ANSI when no answer came.</summary>
        internal static Board BoardOf(ushort productId, byte? info1, byte? info4, byte? info7)
        {
            switch (productId)
            {
                case LegionY740:
                {
                    bool fifteen = info1 is byte size && size <= 100;
                    bool iso = info4 is byte kind && kind >= 16 && kind <= 48;
                    return fifteen ? (iso ? Board.Iso15 : Board.Ansi15) : (iso ? Board.Iso : Board.Ansi);
                }
                case LegionY750:
                    return info4 switch { 41 => Board.Japan, >= 16 and <= 40 => Board.Iso, _ => Board.Ansi };
                case LegionY750S:
                    return info1 switch { 0x97 => Board.Japan, 0x91 => Board.Iso, _ => Board.Ansi };
                case LegionY760:
                    return info7 switch { 41 => Board.Japan, >= 16 and <= 40 => Board.Iso, _ => Board.Ansi };
                case LegionY760S:
                    return info1 switch { 0x97 => Board.Japan, 0x8F => Board.Iso, _ => Board.Ansi };
            }
            return Board.Ansi;
        }

        /// <summary>The information queries each model asks
        /// (RGBController_LenovoUSB.cpp:89-184).</summary>
        internal static byte[] InfoIds(ushort productId) => productId switch
        {
            LegionY740 => new byte[] { 0x01, 0x04 },
            LegionY750 => new byte[] { 0x04 },
            LegionY760 => new byte[] { 0x07 },
            _ => new byte[] { 0x01 },
        };

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>An instruction: 0x07 and its code, padded to the packet
        /// (sendBasicInstruction, LenovoUSBController.cpp:87-91).</summary>
        internal static byte[] Instruction(byte code)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = code;
            return frame;
        }

        /// <summary>The reports of a picture: per run of lights of one zone,
        /// in map order, one report per <see cref="MaxPerReport"/> lights
        /// (setZoneLeds and DeviceUpdateLEDs).</summary>
        internal static List<byte[]> ZoneFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            byte[] frame = null;
            int zone = -1;
            for (int n = 0; n < count; n++)
            {
                int z = lights[n].Address >> 8;
                if (frame == null || z != zone || frame[2] == MaxPerReport)
                {
                    frame = new byte[PacketSize];
                    frame[0] = ReportId;
                    frame[1] = (byte)(ZoneBase + z);
                    frames.Add(frame);
                    zone = z;
                }
                int at = frame[2] * 4 + 4;
                int color = colors[n];
                frame[at] = (byte)lights[n].Address;
                frame[at + 1] = (byte)(color >> 16);
                frame[at + 2] = (byte)(color >> 8);
                frame[at + 3] = (byte)color;
                frame[2]++;
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  The exchanges
        // ─────────────────────────────────────────────

        /// <summary>The byte 4 of the answer to information query
        /// <paramref name="id"/>, or null (getInformation,
        /// LenovoUSBController.cpp:93-105, read at byte 4 by
        /// RGBController_LenovoUSB.cpp:93-177).</summary>
        internal static byte? ReadInfo(IHidReportChannel io, byte id)
        {
            var answer = new byte[Math.Max(PacketSize, io.FeatureReportLength)];
            if (!io.SetFeature(Instruction(id)) || !io.GetFeature(ReportId, answer)) return null;
            return answer[0] == ReportId ? answer[4] : null;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, ushort productId, string product)
        {
            // A keyboard an earlier run left in software mode is the
            // session's to hand back now.
            if (!io.SetFeature(Instruction(SoftwareMode))) return null;
            byte? info1 = null, info4 = null, info7 = null;
            foreach (byte id in InfoIds(productId))
            {
                byte? value = ReadInfo(io, id);
                if (id == 0x01) info1 = value;
                else if (id == 0x04) info4 = value;
                else info7 = value;
            }
            var board = BoardOf(productId, info1, info4, info7);
            var lights = Lights(productId, board);
            return new Session(product, board, new LightMap(lights, $"OpenRGB LenovoDevices.h, {board}") { Product = product });
        }

        /// <summary>Hands back a keyboard an earlier run left in software
        /// mode. True when nothing is owed any more.</summary>
        private static bool Repair(IHidReportChannel io, string product)
        {
            if (KeyboardProfileBackups.Get(product) == null) return true;
            if (io.SetFeature(Instruction(HardwareMode)))
            {
                KeyboardProfileBackups.Drop(product);
                return true;
            }
            KeyboardProfileBackups.NoteFailure(product);
            return false;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly string _product;
            private readonly ISet<int> _addresses;
            private readonly byte[][] _crash = { Instruction(HardwareMode) };

            public Session(string product, Board board, LightMap map)
            {
                _product = product;
                Board = board;
                Map = map;
                _addresses = LenovoLights.Addresses(map.Lights);
            }

            /// <summary>The board the keyboard named.</summary>
            public Board Board { get; }

            /// <summary>The board's own lights
            /// (<see cref="HidLightingSession.Map"/>).</summary>
            public override LightMap Map { get; }

            public override bool Feature => true;

            /// <summary>The release to the keyboard's own lighting, from the
            /// session's start, since software mode is on from then.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (KeyboardProfileBackups.Get(_product) == null)
                    KeyboardProfileBackups.Take(_product, 0, SoftwareModeRecord);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                // Only the lights the board has: a light of the published
                // map another board numbers the same way still names the
                // same place.
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                var picked = new List<LightInfo>(count);
                var pickedColors = new List<int>(count);
                for (int n = 0; n < count; n++)
                {
                    if (!_addresses.Contains(lights[n].Address)) continue;
                    picked.Add(lights[n]);
                    pickedColors.Add(colors[n]);
                }
                foreach (var frame in ZoneFrames(picked, pickedColors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!io.SetFeature(Instruction(HardwareMode))) return false;
                KeyboardProfileBackups.Drop(_product);
                return true;
            }
        }
    }
}
