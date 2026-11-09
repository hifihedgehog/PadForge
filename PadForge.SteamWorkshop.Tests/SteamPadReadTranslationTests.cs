using System.Linq;
using PadForge.Engine.Common.Mapping;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Steam's dpad and four_buttons on a stick, a trackpad or the
    /// gyro import as the reads steamclient64.dll's modes take: the dpad's
    /// layouts (0 four-way, 1 and 2 eight-way by overlap_region, 3 the cross
    /// gate), its dead zone, the click each mode asks for, and the
    /// four_buttons cell geometry.</summary>
    public class SteamPadReadTranslationTests
    {
        private static TranslatedProfile Translate(string vdf)
            => new ConfigTranslator().Translate(SteamInputConfig.FromVdf(VdfParser.Parse(vdf)),
                new TranslationOptions { FileId = 42 });

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Pad\"\n";

        private static string Group(int id, string mode, string body)
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{body}\t}}\n";

        private static string Inputs(params string[] members) => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Inp(string name, string binding)
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n\t\t\t\t\t\t}}\n\t\t\t\t\t}}\n\t\t\t\t}}\n\t\t\t}}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs) sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            return sb.Append("\t\t}\n").ToString();
        }

        private static string Preset(string source)
            => "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n\t\t\"group_source_bindings\"\n\t\t{\n"
             + $"\t\t\t\"1\"\t\"{source} active\"\n\t\t}}\n\t}}\n";

        private static TranslatedProfile North(string mode, string member, string source, params (string, string)[] settings)
            => Translate(Head + Group(1, mode, Inputs(Inp(member, "key_press W")) + Settings(settings)) + Preset(source) + "}\n");

        private static string Read(TranslatedProfile p) => Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources).Descriptor;

        [Fact]
        public void Dpad_Layout0_IsFourWay()
        {
            var p = North("dpad", "dpad_north", "joystick", ("layout", "0"));
            Assert.Equal(SteamPadExpect.Wedge(90, BoundedGate.None, "Gamepad LeftStickRing", layout: 0), Read(p));
            Assert.Equal(BoundedReads.Wedge(90, 45, 1.8, SteamPadExpect.Press(), SteamPadExpect.Release(),
                BoundedGate.None, "Gamepad LeftStickRing"), Read(p));
        }

        [Fact]
        public void Dpad_OverlapRegion_WidensEachDirection()
        {
            // overlap_region 2000, the least Steam takes: 90 + 180000 / 2000
            // = 180 degrees wide, so each direction covers a half turn.
            var p = North("dpad", "dpad_north", "right_joystick", ("overlap_region", "2000"));
            Assert.Equal(BoundedReads.Wedge(90, 90, 3.6, SteamPadExpect.Press(), SteamPadExpect.Release(),
                BoundedGate.None, "Gamepad RightStickRing"), Read(p));
        }

        [Fact]
        public void Dpad_Layout2_ReadsEightWay_AndNamesThePulse()
        {
            var p = North("dpad", "dpad_north", "joystick", ("layout", "2"));
            Assert.Equal(SteamPadExpect.Wedge(90, BoundedGate.None, "Gamepad LeftStickRing"), Read(p));
            Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.DpadPulseHeld);
        }

        [Fact]
        public void Dpad_Layout1_NamesNoPulse()
        {
            var p = North("dpad", "dpad_north", "joystick");
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.DpadPulseHeld);
        }

        [Fact]
        public void Dpad_Layout3_IsTheCrossGate_EachComponentPast20Percent()
        {
            var p = North("dpad", "dpad_north", "joystick", ("layout", "3"));
            Assert.Equal(BoundedReads.Hold(20, 0, SteamPadExpect.Press(), SteamPadExpect.Release(), 1, -1,
                BoundedGate.None, "Gamepad LeftStickRing"), Read(p));
        }

        [Fact]
        public void Dpad_RequiresClickOn_OnAStick_NeedsTheStickClick()
        {
            var p = North("dpad", "dpad_north", "joystick", ("requires_click", "1"));
            Assert.Equal(SteamPadExpect.Wedge(90, BoundedGate.Click, "Gamepad LeftStickRing"), Read(p));
        }

        [Fact]
        public void Dpad_ADeadZoneUnderTheHoldBack_EngagesAtTwiceIt()
        {
            // Steam pulls a threshold below its hold-back up to the hold-back:
            // deadzone 100 engages past 2.5 percent and holds down to zero.
            var p = North("dpad", "dpad_north", "joystick", ("deadzone", "100"));
            Assert.Equal(BoundedReads.Wedge(90, 67.5, 2.7, 2.5, 0, BoundedGate.None, "Gamepad LeftStickRing"), Read(p));
        }

        [Fact]
        public void DpadEdge_TakesNoClick_EvenWhereTheDirectionsDo()
        {
            // The edge's trigger is stepped apart from the click test
            // (steamclient64.dll 0x1388b319e), so a trackpad dpad's edge
            // fires on the finger's place alone.
            var p = North("dpad", "edge", "left_trackpad");
            Assert.Equal(SteamPadExpect.Edge("Touchpad 0 Finger 0 Ring"), Read(p));
        }

        [Fact]
        public void DpadEdge_OnTheGyro_ReadsTheTilt_AndRestsWhileTheGyroIsOff()
        {
            var p = North("dpad", "edge", "gyro", ("edge_binding_radius", "20000"));
            string pair = "Gyro Tilt " + System.Math.Round(10000 / 32767.0 * 90.0, 4)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + " 90";
            Assert.Equal(SteamPadExpect.Edge(pair, 20000, gate: BoundedGate.Engaged), Read(p));
        }

        [Fact]
        public void FourButtons_OnAStick_NeedTheClick_UnlessTheFileSaysNot()
        {
            var clicked = North("four_buttons", "button_y", "joystick");
            Assert.Equal(SteamPadExpect.Disc(0, -SteamPadExpect.Dist(), BoundedGate.Click, "Gamepad LeftStickRing"),
                Read(clicked));
            var free = North("four_buttons", "button_y", "joystick", ("requires_click", "0"));
            Assert.Equal(SteamPadExpect.Disc(0, -SteamPadExpect.Dist(), BoundedGate.None, "Gamepad LeftStickRing"),
                Read(free));
        }

        [Fact]
        public void FourButtons_NintendoLabels_NameTheCellsByLabel()
        {
            // A Nintendo-labeled file's button_a is the east cell, its A.
            string vdf = Head.Replace("\"title\"\t\"Pad\"\n", "\"title\"\t\"Pad\"\n\t\"controller_type\"\t\"controller_switch_pro\"\n")
                + Group(1, "four_buttons", Inputs(Inp("button_a", "key_press W")) + Settings(("requires_click", "0")))
                + Preset("joystick") + "}\n";
            Assert.Equal(SteamPadExpect.Disc(SteamPadExpect.Dist(), 0, BoundedGate.None, "Gamepad LeftStickRing"),
                Read(Translate(vdf)));
        }

        [Fact]
        public void FourButtons_ReadTheFilesCellSizeAndDistance()
        {
            // Wild witness: button_size 30286 and button_dist 28190 on a left
            // trackpad.
            var p = North("four_buttons", "button_b", "left_trackpad",
                ("button_size", "30286"), ("button_dist", "28190"));
            Assert.Equal(SteamPadExpect.Disc(SteamPadExpect.Dist(28190), 0, BoundedGate.Click,
                "Touchpad 0 Finger 0 Ring", size: 30286), Read(p));
        }

        [Fact]
        public void FourButtons_ACellAtTheRangesEdge_KeepsItsHoldBackInsideTheRange()
        {
            // button_size 32767 puts the release edge past the range, so
            // Steam pulls the threshold in by the hold-back.
            var p = North("four_buttons", "button_x", "center_trackpad",
                ("button_size", "32767"), ("button_dist", "1"));
            var read = BoundedReads.Parse(Read(p));
            Assert.Equal(BoundedReadKind.Disc, read.Kind);
            Assert.Equal(100, read.ReleaseRadius, 3);
            Assert.Equal(96, read.PressRadius, 3);
        }
    }
}
