using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>A row of asusctl's aura_support.ron: the name a board's name holds,
    /// the product it is limited to, or empty for any, its layout, whether it is
    /// per-key, and whether it has the four keyboard zones.</summary>
    internal sealed record AsusLaptopBoard(string Name, string Product, string Layout, bool PerKey, bool Zones);

    /// <summary>The PC's product name, as G-Helper's model checks read it, and its
    /// mainboard name, as asusctl and OpenRGB read it.</summary>
    internal sealed record AsusLaptopMachine(string Product, string Board)
    {
        internal static readonly AsusLaptopMachine Unknown = new(null, null);

        private bool Has(params string[] names)
        {
            string text = Product + " " + Board;
            foreach (string name in names)
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>isACPI, IsTUF and IsVivoZenPro (Aura.cs:105, AppConfig.cs:403-405
        /// and 423-425): these keyboards take their color through the ATK ACPI
        /// device (<see cref="AsusTufLaptopLighting"/>).</summary>
        public bool ThroughAcpi => Has("TUF", "TX Gaming", "TX Air", "Vivobook", "Zenbook", "ProArt", "EXPERTBOOK", " V16", "ASUSLaptop");

        /// <summary>IsStrix (AppConfig.cs:515-518).</summary>
        public bool Strix => Has("Strix", "Scar", "G703G");

        /// <summary>IsS17 (AppConfig.cs:595-598).</summary>
        public bool S17 => Has("S17");

        /// <summary>IsZ13 (AppConfig.cs:575-578).</summary>
        public bool Z13 => Has("Z13");

        /// <summary>IsNoDirectRGB and IsSlash (AppConfig.cs:480-488 and 555-558).</summary>
        public bool NoDirectColor => Has("GA503", "G533Q", "GU502", "GA403", "GU605", "GA605", "GA405", "GU405", "GU606", "GX651");

        /// <summary>IsWhite with NoAura (AppConfig.cs:455-468), whose GA401I the GA401
        /// covers, and IsARCNM (lines 398-401).</summary>
        public bool WhiteOnly => Has("GA401", "FX517Z", "FX516P", "X13", "GZ301VIC", "FA617N", "FA617X", "GA502IU", "HN7306", "H760", "H740", "M6500X");
    }

    /// <summary>
    /// The keyboards of ASUS laptops OpenRGB's Aura Core family does not describe
    /// (#557): G-Helper lights all of them through report 5D, and asusctl lists the
    /// keys of its per-key laptops.
    ///
    /// <para>Which collection. G-Helper takes the collection of a product in its list
    /// whose report descriptor declares feature report 5D (AsusHid.cs:40-88). A
    /// program has no descriptor here, so the family asks the same thing of the
    /// collection, as OpenRGB's IdentifyDevice does, with a read
    /// (AsusAuraCoreController.cpp:316). OpenRGB's rows name the Aura collections on
    /// page FF31, and this family asks only on a vendor page.</para>
    ///
    /// <para>What kind of keyboard. G-Helper sends B9, the ID string and
    /// 05 20 31 00 20, reads report 5D, and takes the answer only when it echoes the
    /// first bytes (AuraProbe, AsusHid.cs:254-296). Byte 9 is the kind: 02 four zones,
    /// 03 per-key, 04 or 00 one zone (Aura.cs:70-76 and 356-363). Byte 12 is the layout, 2 for
    /// ISO (Aura.cs:326, and OpenRGB's AsusAuraCoreLaptopController.h:100). Byte 13 says
    /// which lights are past the keys: 02 a lightbar, 80 a rear strip (Aura.cs:333-340
    /// and 370-376). Where the firmware gives no answer or 00, asusctl's list decides.
    /// A keyboard white only takes a level, and G-Helper's list and the firmware's bit
    /// 10 in byte 14 tell which (Aura.cs:380).</para>
    ///
    /// <para>What goes out. BC with 01, or 00 where G-Helper's IsOldStrix or IsS17 hold,
    /// opens direct mode (Aura.cs:672-677 and 824-829), and nothing written this way
    /// is stored: asusctl writes its per-key frames with no apply after
    /// (asusd src/aura_laptop/mod.rs:179-205). A per-key keyboard opens with BC 00, as
    /// OpenRGB's laptops and asusctl's get_init_msg do. Brightness goes to 3 with
    /// BA C5 C4, as OpenRGB's Direct mode sets it, so a keyboard turned down still
    /// shows its colors. One zone is BC 01 01 with the color at byte 9 (Aura.cs:832-842),
    /// asusctl's capture of Armoury Crate (advanced.rs, new_zoned). Four zones are
    /// BC 00 01 04 with zones 0 to 3 and the lightbar at 6 to 11 from byte 9
    /// (Aura.cs:624-632 and 726-741). Per-key is the frames of OpenRGB's SetLedsDirect,
    /// which G-Helper sends too (Aura.cs:697-712), then the rear strip of 39 LEDs,
    /// BC 00 01 05 with the frame's first LED plus one (Aura.cs:752-766). The ROG Flow
    /// Z13 first takes C0 03 01 (Aura.cs:388-389).</para>
    ///
    /// <para>G-Helper's laptops with no direct color and the white ones take B3 with
    /// zone 0 and speed EB, then B5 (Aura.cs:291-308 and 811-815), which also stores
    /// nothing, since "Changes won't persist unless apply is set"
    /// (asusd src/aura_laptop/mod.rs:118-125). A white keyboard's level goes in the red
    /// byte, and G-Helper zeroes the other two. PadForge sends the brightest channel
    /// there, so a color with no red still lights the keyboard. The Z13's rear light is
    /// G-Helper's REAR_LIGHT_PIDS, the same B3 and B5 with B4, which stores
    /// (Aura.cs:863), so it keeps the stored-write bounds of
    /// <see cref="LenovoLegionLighting"/>.</para>
    ///
    /// <para>G-Helper lights a laptop with a LampArray interface through that interface
    /// (AsusLampArray.cs:33-46, Aura.cs:659-663 and 803-807), and OpenRGB's laptops light
    /// through BC, so the row goes through both, each shown where the firmware lets it
    /// (<see cref="HidLightingModel.LiveThroughLampArray"/>).</para>
    ///
    /// <para>Hand-back. Nothing goes out, as with OpenRGB's laptops. No reference
    /// reads the keyboard's own effect back, so the keyboard keeps PadForge's last
    /// picture until Armoury Crate, G-Helper or a restart sets it again.</para>
    /// </summary>
    internal static class AsusLaptopAura
    {
        internal const ushort VendorId = 0x0B05;
        internal const ushort FirstVendorPage = 0xFF00;
        internal const byte Report = 0x5D;
        internal const int Length = 64;

        /// <summary>MAIN_AURA_PIDS (AsusHid.cs:13), less the Ally's two, which OpenRGB's
        /// USB family lights, and the three OpenRGB's Aura Core rows take first.</summary>
        internal static readonly ushort[] Products =
        {
            0x1A30, 0x19B6, 0x1822, 0x1837, 0x184A, 0x183D, 0x8502, 0x1807, 0x17E0, 0x1B6E, 0x1B2C, 0x8854, 0x1CE7, 0x1CE6,
            0x1BF2, 0x1BF3, 0x1CD7, 0x1CD8, 0x4B42,
        };

        /// <summary>REAR_LIGHT_PIDS (AsusHid.cs:14). asusctl lists it as a keyboard too
        /// (G533QS in aura_support.ron), which it is on any laptop but the Z13
        /// (HasRearLight, AppConfig.cs:584-587).</summary>
        internal const ushort RearLightProduct = 0x18C6;

        internal const byte KindZones = 0x02, KindPerKey = 0x03, KindOneZone = 0x04;

        private const byte FeatureLightbar = 0x02, FeatureRearStrip = 0x80, FeatureRedOnly = 0x10;

        internal const int ZonesLightbarFirst = 6, ZonesLightbarLights = 6;

        /// <summary>The LEDs a per-key picture numbers: 167 keys and the eleven past
        /// them (Aura.cs:655-656).</summary>
        internal const int PerKeyLeds = 178;

        internal const int RearStripLeds = 39, RearStripPerFrame = 16;

        /// <summary>A rear strip's LEDs are numbered from here in the map. They have no
        /// number in the picture of the keys.</summary>
        internal const int RearStripFirstAddress = PerKeyLeds;

        /// <summary>The repaint interval of the laptops with no direct color, G-Helper's
        /// aura_refresh for anything but a Strix (Aura.cs:916).</summary>
        internal const int StaticRepaintMs = 300;

        private const byte StaticSpeed = 0xEB;

        internal enum Kind
        {
            /// <summary>One light, BC 01 01.</summary>
            Whole,
            /// <summary>Four zones and a lightbar, BC 00 01 04.</summary>
            Zones,
            /// <summary>Key by key.</summary>
            PerKey,
            /// <summary>One light through B3 and B5.</summary>
            Static,
        }

        /// <summary>What the firmware answers about its keyboard.</summary>
        internal readonly record struct Answer(byte Type, byte Layout, byte Features1, byte Features2);

        /// <summary>Whether the family lights a collection of a product, before its
        /// machine and its answer are known.</summary>
        internal static bool Takes(HidCollectionInfo collection)
            => collection != null && collection.VendorId == VendorId && collection.UsagePage >= FirstVendorPage
               && collection.FeatureReportLength > 0
               && (collection.ProductId == RearLightProduct || Array.IndexOf(Products, collection.ProductId) >= 0);

        /// <summary>Whether a collection has the Aura report: a read, which writes
        /// nothing and passes on a handle with no access right.</summary>
        internal static bool AnswersAura(IHidReportChannel io)
            => io != null && io.GetFeature(Report, new byte[Math.Max(Length, io.FeatureReportLength)]);

        /// <summary>asusctl's match_device: from the end of the list, the first name the
        /// board's name holds, whose product is this one if it names one.</summary>
        internal static AsusLaptopBoard BoardOf(string board, int productId)
        {
            if (string.IsNullOrEmpty(board)) return null;
            string product = productId.ToString("x4");
            for (int i = AsusLaptopTables.Boards.Length - 1; i >= 0; i--)
            {
                var row = AsusLaptopTables.Boards[i];
                if (!board.Contains(row.Name, StringComparison.Ordinal)) continue;
                if (row.Product.Length > 0 && !string.Equals(row.Product, product, StringComparison.OrdinalIgnoreCase)) continue;
                return row;
            }
            return null;
        }

        internal static byte[] Frame(params byte[] head)
        {
            var frame = new byte[Length];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>AuraProbe's writes (AsusHid.cs:262-269).</summary>
        internal static List<byte[]> Query()
        {
            var id = new byte[Length];
            id[0] = Report;
            System.Text.Encoding.ASCII.GetBytes("ASUS Tech.Inc.").CopyTo(id, 1);
            return new List<byte[]> { Frame(Report, 0xB9), id, Frame(Report, 0x05, 0x20, 0x31, 0x00, 0x20) };
        }

        /// <summary>A frame as a feature report, and as an output report when that fails.
        /// G-Helper sends the keyboard's direct frames as feature reports
        /// (SetFeatureAura, AsusHid.cs:187-213).</summary>
        internal static bool Send(IHidReportChannel io, byte[] frame)
            => io.SetFeature(frame) || (io.OutputReportLength > 0 && io.WriteOutput(frame));

        /// <summary>A frame as an output report, and as a feature report where the collection
        /// has none or the write fails. G-Helper writes the Z13's opening and every frame it
        /// stores with stream.Write (Aura.cs:388-389 and 863, AsusHid.cs:160-173).</summary>
        internal static bool SendOutputFirst(IHidReportChannel io, byte[] frame)
            => (io.OutputReportLength > 0 && io.WriteOutput(frame)) || io.SetFeature(frame);

        /// <summary>The firmware's answer, or null where it does not echo the query.
        /// G-Helper writes these as output reports and OpenRGB's query goes as a
        /// feature report (AsusAuraCoreLaptopController.cpp:99-139), so a feature report
        /// that fails is sent the other way.</summary>
        internal static Answer? Ask(IHidReportChannel io)
        {
            foreach (var frame in Query())
                if (!Send(io, frame)) return null;
            var answer = new byte[Math.Max(Length, io.FeatureReportLength)];
            if (!io.GetFeature(Report, answer)) return null;
            if (answer[0] != Report || answer[1] != 0x05 || answer[2] != 0x20 || answer[3] != 0x31) return null;
            return new Answer(answer[9], answer[12], answer[13], answer[14]);
        }

        /// <summary>The kind of a keyboard from its answer, its machine and asusctl's
        /// row. The firmware's word comes first, and a keyboard white only or with no
        /// direct color takes B3 unless the firmware or asusctl calls it per-key or
        /// zoned.</summary>
        internal static Kind KindOf(Answer? answer, AsusLaptopMachine machine, AsusLaptopBoard row)
        {
            if (answer?.Type == KindPerKey) return Kind.PerKey;
            if (answer?.Type == KindZones) return Kind.Zones;
            if (row?.PerKey == true) return Kind.PerKey;
            if (WhiteOnly(answer, machine) || machine.NoDirectColor) return Kind.Static;
            if ((answer == null || answer.Value.Type == 0) && row?.Zones == true) return Kind.Zones;
            return Kind.Whole;
        }

        private static bool WhiteOnly(Answer? answer, AsusLaptopMachine machine)
            => machine.WhiteOnly || (answer is { Type: not 0 } a && (a.Features2 & FeatureRedOnly) != 0);

        /// <summary>Whether a collection of OpenRGB's Aura Core rows, which answers 5D, is
        /// better lit this way than by its Static mode per zone: the firmware or asusctl
        /// says what the keyboard is. A keyboard that says nothing stays with OpenRGB.</summary>
        internal static bool Says(Answer? answer, AsusLaptopBoard row)
            => answer?.Type is KindZones or KindPerKey or KindOneZone || row?.PerKey == true;

        /// <summary>IsOldStrix (Aura.cs:372).</summary>
        private static bool OldStrix(Answer? answer, AsusLaptopMachine machine)
            => answer is { Features1: 0 } && machine.Strix;

        /// <summary>The byte after BC that opens direct mode.</summary>
        internal static byte OpeningByte(Kind kind, Answer? answer, AsusLaptopMachine machine) => kind switch
        {
            Kind.PerKey => 0x00,
            Kind.Zones => (byte)(OldStrix(answer, machine) || machine.S17 ? 0x00 : 0x01),
            _ => 0x01,
        };

        /// <summary>The frames that open a keyboard of a kind.</summary>
        internal static List<byte[]> Opening(Kind kind, Answer? answer, AsusLaptopMachine machine)
        {
            var frames = new List<byte[]>();
            if (machine.Z13) frames.Add(Frame(Report, 0xC0, 0x03, 0x01));
            if (kind == Kind.Static) return frames;
            var open = Frame(Report, AsusAuraCoreLighting.Direct, OpeningByte(kind, answer, machine));
            frames.Add(open);
            frames.Add(Frame(Report, AsusAuraCoreLighting.Brightness, 0xC5, 0xC4, AsusAuraCoreLighting.LaptopBrightness));
            frames.Add((byte[])open.Clone());
            return frames;
        }

        private static string ProductKey(int productId, string detail) => $"asus:{VendorId:X4}:{productId:X4}:{detail}";

        internal static LightMap WholeMap(int productId, string detail = "laptop-whole")
            => new(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } }, "G-Helper app/USB/Aura.cs")
            {
                Product = ProductKey(productId, detail),
            };

        internal static LightMap ZonesMap(int productId, bool lightbar)
        {
            var lights = new List<LightInfo>();
            for (int i = 0; i < AsusAuraCoreLighting.KeyboardZones; i++)
                lights.Add(new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 });
            if (lightbar)
                for (int i = 0; i < ZonesLightbarLights; i++)
                    lights.Add(new LightInfo(ZonesLightbarFirst + i, LightKind.Edge, 0, LightKeySource.None));
            return new LightMap(lights, "G-Helper app/USB/Aura.cs") { Product = ProductKey(productId, lightbar ? "laptop-zones-bar" : "laptop-zones") };
        }

        /// <summary>The lights of a per-key keyboard. On ISO, LED 96 is the key beside
        /// Enter and LED 106 the key beside Left Shift (G-Helper's packetMap names them #
        /// and ISO\, Aura.cs:565-567). On ANSI they sit under Enter and Left Shift.</summary>
        internal static LightMap PerKeyMap(int productId, PluginKeyTable table, bool iso, bool rearStrip)
        {
            var lights = PluginKeyNames.Lights(table).ToList();
            if (iso)
            {
                for (int i = 0; i < lights.Count; i++)
                {
                    if (lights[i].Address == 96) lights[i] = lights[i] with { Kind = LightKind.Key, Usage = 0x32, KeySource = LightKeySource.Generated };
                    else if (lights[i].Address == 106) lights[i] = lights[i] with { Kind = LightKind.Key, Usage = 0x64, KeySource = LightKeySource.Generated };
                }
            }
            if (rearStrip)
                for (int i = 0; i < RearStripLeds; i++)
                    lights.Add(new LightInfo(RearStripFirstAddress + i, LightKind.Edge, 0, LightKeySource.None));
            string detail = $"laptop-{table.Plugin}{(iso ? ":iso" : ":ansi")}{(rearStrip ? ":rear" : "")}";
            return new LightMap(lights, table.Plugin) { Product = ProductKey(productId, detail) };
        }

        /// <summary>The table of a per-key keyboard: its row's layout, or every LED
        /// G-Helper names, of which a keyboard without some simply does not show them.</summary>
        internal static PluginKeyTable PerKeyTable(AsusLaptopBoard row)
            => (row == null ? null : AsusLaptopTables.Layout(row.Layout)) ?? AsusLaptopTables.Default();

        /// <summary>What a model shows before a session has asked the firmware.</summary>
        internal static LightMap FirstMap(int productId, AsusLaptopMachine machine)
        {
            var row = BoardOf(machine.Board, productId);
            return row?.PerKey == true ? PerKeyMap(productId, PerKeyTable(row), false, false) : WholeMap(productId);
        }

        /// <summary>One light through BC 01 01.</summary>
        internal static byte[] WholeFrame(int rgb)
        {
            var frame = Frame(Report, AsusAuraCoreLighting.Direct, 0x01, 0x01);
            AsusAuraUsbLighting.Put(frame, 9, rgb);
            return frame;
        }

        /// <summary>The zones and the lightbar, each at its address from byte 9.</summary>
        internal static byte[] ZonesFrame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = Frame(Report, AsusAuraCoreLighting.Direct, 0x00, 0x01, 0x04);
            int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                int at = lights[i].Address;
                if (at >= 0 && at < ZonesLightbarFirst + ZonesLightbarLights) AsusAuraUsbLighting.Put(frame, 9 + at * 3, colors[i]);
            }
            return frame;
        }

        /// <summary>AuraMessage for the Static mode (Aura.cs:291-308), then B5. Byte 9 is
        /// FF for black. A white keyboard's level goes in the red byte.</summary>
        internal static List<byte[]> StaticFrames(int rgb, bool white, bool stored)
        {
            if (white)
                rgb = Math.Max(Math.Max((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF), rgb & 0xFF) << 16;
            var message = Frame(Report, 0xB3, 0x00, 0x00);
            AsusAuraUsbLighting.Put(message, 4, rgb);
            message[7] = StaticSpeed;
            message[8] = 0x00;
            message[9] = (byte)((rgb & 0xFFFFFF) == 0 ? 0xFF : 0x00);
            AsusAuraUsbLighting.Put(message, 10, rgb);
            var frames = new List<byte[]> { message, Frame(Report, AsusAuraCoreLighting.Set) };
            if (stored) frames.Add(Frame(Report, AsusAuraCoreLighting.Apply));
            return frames;
        }

        /// <summary>The rear strip's frames (Aura.cs:752-766).</summary>
        internal static List<byte[]> RearStripFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var strip = new int[RearStripLeds];
            int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                int at = lights[i].Address - RearStripFirstAddress;
                if (at >= 0 && at < RearStripLeds) strip[at] = colors[i];
            }
            var frames = new List<byte[]>();
            for (int i = 0; i < RearStripLeds; i += RearStripPerFrame)
            {
                int leds = Math.Min(RearStripPerFrame, RearStripLeds - i);
                var frame = Frame(Report, AsusAuraCoreLighting.Direct, 0x00, 0x01, 0x05, 0x00, (byte)(i + 1), (byte)leds);
                for (int k = 0; k < leds; k++) AsusAuraUsbLighting.Put(frame, 9 + k * 3, strip[i + k]);
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>The frames of a picture.</summary>
        internal static List<byte[]> Frames(Kind kind, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors,
            bool white, bool stored, bool rearStrip)
        {
            int first = colors != null && colors.Count > 0 ? colors[0] : 0;
            switch (kind)
            {
                case Kind.Whole:
                    return new List<byte[]> { WholeFrame(first) };
                case Kind.Zones:
                    return new List<byte[]> { ZonesFrame(lights, colors) };
                case Kind.Static:
                    return StaticFrames(first, white, stored);
                default:
                    var frames = AsusAuraCoreLighting.LaptopFrames(lights, colors, PerKeyLeds);
                    if (rearStrip) frames.AddRange(RearStripFrames(lights, colors));
                    return frames;
            }
        }

        /// <summary>Opens the keyboard of a collection: asks the firmware, then
        /// <see cref="Open"/>. The Z13's rear light has one light and no kind to ask.</summary>
        internal static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, int productId, AsusLaptopMachine machine)
        {
            if (io == null) return null;
            if (productId == RearLightProduct && machine.Z13)
                return new Session(WholeMap(productId, "laptop-rear"), Kind.Static, machine.WhiteOnly, true, false);
            return Open(io, wait, productId, machine, Ask(io));
        }

        /// <summary>The session of a keyboard whose answer is known. Null when a write
        /// failed.</summary>
        internal static HidLightingSession Open(IHidReportChannel io, Action<int> wait, int productId, AsusLaptopMachine machine, Answer? answer)
        {
            var row = BoardOf(machine.Board, productId);
            var kind = KindOf(answer, machine, row);
            byte features = answer?.Features1 ?? 0;
            bool rearStrip = kind == Kind.PerKey && (features & FeatureRearStrip) != 0;
            LightMap map = kind switch
            {
                Kind.PerKey => PerKeyMap(productId, PerKeyTable(row), answer?.Layout == AsusAuraCoreLighting.LaptopIso, rearStrip),
                Kind.Zones => ZonesMap(productId, (features & FeatureLightbar) != 0 || OldStrix(answer, machine)),
                _ => WholeMap(productId),
            };
            var opening = Opening(kind, answer, machine);
            for (int i = 0; i < opening.Count; i++)
                if (!(machine.Z13 && i == 0 ? SendOutputFirst(io, opening[i]) : Send(io, opening[i]))) return null;
            if (kind != Kind.Static) wait?.Invoke(50);
            return new Session(map, kind, WhiteOnly(answer, machine), false, rearStrip);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;
            private readonly Kind _kind;
            private readonly bool _white, _stored, _rearStrip;

            public Session(LightMap map, Kind kind, bool white, bool stored, bool rearStrip)
            {
                _map = map;
                _kind = kind;
                _white = white;
                _stored = stored;
                _rearStrip = rearStrip;
            }

            public override bool Feature => true;

            public override LightMap Map => _map;

            public override int? HoldMs => _stored ? LenovoLegionLighting.HoldMs : 0;

            public override int? RepaintMs => _stored ? LenovoLegionLighting.RepaintMs
                : _kind == Kind.Static ? StaticRepaintMs : HidppBackend.PaintMs;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Frames(_kind, lights, colors, _white, _stored, _rearStrip))
                    if (!(_stored ? SendOutputFirst(io, frame) : Send(io, frame))) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
