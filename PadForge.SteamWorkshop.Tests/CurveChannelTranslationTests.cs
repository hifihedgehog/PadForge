using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v11: the per-source response-curve / outer-range
    /// channel. Stick-hosted joystick groups stamp Steam's curve cluster
    /// onto both member rows of the emitted axis pair
    /// (ParamCurveExponent / ParamRangeOuter / Sensitivity), and the
    /// ResponseCurveNotSupported note names only genuinely dropped keys.
    /// Unit grounding lives on ConfigTranslator.CurveRangeChannel: the
    /// curve_exponent PRESET SELECTOR (steamui localization
    /// ControllerBinding_CurveExponent_*), the x100 Custom slider (Valve's
    /// CSGO configs: 195 = 1.95), and deadzone_outer_radius on the
    /// deadzone_inner_radius 0..32767 scale (Valve basicui 28000..32767).</summary>
    public class CurveChannelTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
            });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Curve\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Inputs(params string[] members)
            => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Inp(string name, string binding)
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n"
             + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";

        private static string Preset(int id, string name, params (int GroupId, string Binding)[] entries)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"{name}\"\n");
            sb.Append("\t\t\"group_source_bindings\"\n\t\t{\n");
            foreach (var e in entries)
                sb.Append($"\t\t\t\"{e.GroupId}\"\t\"{e.Binding}\"\n");
            sb.Append("\t\t}\n\t}\n");
            return sb.ToString();
        }

        // ─── joystick_move, matched side ───────────────────────────────

        [Fact]
        public void JoystickMove_MatchedSide_StampsBothMemberRows_NoNote()
        {
            // The xinput click binding puts the Xbox side in play, so the
            // matched analog pair materializes as explicit rows carrying
            // the group's cluster: curve_exponent 4 = Steam Extra Wide
            // (exponent 2.5), deadzone_outer_radius 28800 / 32767.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "xinput_button JOYSTICK_RIGHT"))
                    + Settings(("deadzone_outer_radius", "28800"), ("curve_exponent", "4")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            foreach (var target in new[] { "LeftThumbAxisX", "LeftThumbAxisY" })
            {
                var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == target);
                var src = row.Sources.Single(s => s.Descriptor.StartsWith("Gamepad LeftStick"));
                Assert.Equal(2.5, src.ParamCurveExponent);
                Assert.Equal(28800 / 32767.0, src.ParamRangeOuter, 6);
                Assert.Equal(1.0, src.Sensitivity);
            }
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        // ─── joystick_move, crossed output ─────────────────────────────

        [Fact]
        public void JoystickMove_CrossedOutput_StampsBothRows()
        {
            // output_joystick 2 crosses the left stick onto the right
            // thumb pair; the stamps ride the crossed rows the same way.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"), ("curve_exponent", "1")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            foreach (var target in new[] { "RightThumbAxisX", "RightThumbAxisY" })
            {
                var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == target);
                var src = Assert.Single(row.Sources);
                // Steam Aggressive reaches 100% output faster: exponent 0.5.
                Assert.Equal(0.5, src.ParamCurveExponent);
                Assert.Equal(0.0, src.ParamRangeOuter);
            }
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        // ─── Preset selector semantics ─────────────────────────────────

        [Theory]
        [InlineData("1", 0.5)] // Aggressive: "gets to 100% output faster"
        [InlineData("2", 1.5)] // Relaxed: "slightly more slow range"
        [InlineData("3", 2.0)] // Wide: "much slower than default"
        [InlineData("4", 2.5)] // Extra Wide: "100% at the extremes"
        public void CurveExponent_PresetInt_MapsBySteamSemantics(string preset, double expected)
        {
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"), ("curve_exponent", preset)))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(expected, Assert.Single(row.Sources).ParamCurveExponent);
        }

        [Fact]
        public void CurveExponent_LinearZero_LeavesTheChannelOff()
        {
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"), ("curve_exponent", "0")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(0.0, Assert.Single(row.Sources).ParamCurveExponent);
        }

        [Fact]
        public void CustomCurve_SliderIsStoredTimes100()
        {
            // Wild grounding: gw2-steam-controller stores curve_exponent 5
            // (the Custom selector) beside custom_curve_exponent 60; Valve's
            // CSGO ps4 gyro config stores 195. Both only read sanely / 100.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"),
                        ("curve_exponent", "5"), ("custom_curve_exponent", "195")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(1.95, Assert.Single(row.Sources).ParamCurveExponent, 6);
        }

        [Fact]
        public void CustomCurve_StaleSliderLosesToNamedPreset()
        {
            // A preset 1..4 beside a custom value is the configurator's
            // stale slider; Steam applies the named preset, so we do too.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"),
                        ("curve_exponent", "4"), ("custom_curve_exponent", "50")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(2.5, Assert.Single(row.Sources).ParamCurveExponent);
        }

        // ─── Outer radius scale ────────────────────────────────────────

        [Fact]
        public void OuterRadius_FullScale_ReadsAsIdentityAndStaysQuiet()
        {
            // 32767 = full deflection = the identity map (Valve basicui
            // carries it verbatim); consumed with no stamp and no note.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"), ("deadzone_outer_radius", "32767")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(0.0, Assert.Single(row.Sources).ParamRangeOuter);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        // ─── Per-axis sensitivity scales ───────────────────────────────

        [Fact]
        public void SensitivityScales_FoldIntoTheMatchingAxisRow()
        {
            // Percent semantics (the shipped configurator ids are
            // #Unit_Percent, same clamp(1,400)/100 read as mouse_region):
            // horiz lands on the X row, vert on the Y row.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "2"),
                        ("sensitivity_horiz_scale", "110"), ("sensitivity_vert_scale", "70")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal(1.10, Assert.Single(x.Sources).Sensitivity, 6);
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisY");
            Assert.Equal(0.70, Assert.Single(y.Sources).Sensitivity, 6);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        // ─── joystick_mouse host ───────────────────────────────────────

        [Fact]
        public void JoystickMouse_StickHost_StampsTheMouseRows()
        {
            // The corpus's marquee case (3451446931 group 9): a stick-hosted
            // joystick_mouse with curve_exponent 4 + deadzone_outer_radius.
            // Steam defines the Stick Response Curve for this mode
            // (ControllerBinding_CurveExponent_joystick_mouse), and the KbM
            // mouse rows read the stick through the same bipolar seam. The
            // per-axis scales multiply INTO the mode's sensitivity ratio
            // (sensitivity 80 = the stick-mouse 1.0x baseline).
            string vdf = Head
                + Group(1, "joystick_mouse",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("sensitivity", "80"),
                        ("curve_exponent", "4"), ("deadzone_outer_radius", "28800"),
                        ("sensitivity_horiz_scale", "110")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            var sx = Assert.Single(x.Sources);
            Assert.Equal("Gamepad LeftStickX", sx.Descriptor);
            Assert.Equal(2.5, sx.ParamCurveExponent);
            Assert.Equal(28800 / 32767.0, sx.ParamRangeOuter, 6);
            Assert.Equal(1.10, sx.Sensitivity, 6);

            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            var sy = Assert.Single(y.Sources);
            Assert.Equal(2.5, sy.ParamCurveExponent);
            Assert.Equal(1.0, sy.Sensitivity); // no vert scale in the group

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        // ─── Hosts without the channel ─────────────────────────────────

        [Fact]
        public void TrackpadHost_StampsTheGestureStickSources()
        {
            // v18: the gesture Stick lane applies the per-source shaping
            // in the engine now, so trackpad-as-stick stamps like a stick
            // host and nothing is named.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("deadzone_outer_radius", "28800"), ("curve_exponent", "4")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.All(p.XboxMappingSet.Rows.SelectMany(r => r.Sources), s =>
            {
                Assert.Equal(2.5, s.ParamCurveExponent); // preset 4 = ExtraWide
                Assert.Equal(28800 / 32767.0, s.ParamRangeOuter, 6);
            });
        }

        [Fact]
        public void DeadzoneShape_StampsThePairChannel_OnThumbPairGroups()
        {
            // v18: deadzone_shape on a thumb-pair output consumes into the
            // slot-level DeadZoneShape stamp (Steam Circle = the engine's
            // ScaledRadial "2"), leaving no named curve keys.
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("output_joystick", "1"),
                        ("deadzone_outer_radius", "31999"), ("deadzone_shape", "1")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.Equal("2", p.LeftStickDeadZoneShape); // output_joystick 1 = left pair
            Assert.Equal("", p.RightStickDeadZoneShape);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.Equal(31999 / 32767.0, Assert.Single(row.Sources).ParamRangeOuter, 6);
        }

        [Fact]
        public void CurveCluster_OnTriggerGroup_StampsThePullRow()
        {
            // v18: the unipolar trigger tail shapes now, so a trigger
            // group's curve cluster rides the pull (matched analog side).
            string vdf = Head
                + Group(1, "trigger",
                    Inputs(Inp("click", "xinput_button TRIGGER_LEFT"))
                    + Settings(("deadzone_outer_radius", "28800"), ("curve_exponent", "1"),
                        ("anti_deadzone", "3277")))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftTrigger");
            var src = row.Sources.First(s => !s.HalfAxis); // the analog pull leg
            Assert.Equal(0.5, src.ParamCurveExponent); // preset 1 = Aggressive
            Assert.Equal(28800 / 32767.0, src.ParamRangeOuter, 6);
            Assert.Equal(3277 / 32767.0, src.ParamAntiDeadzone, 6);
        }
    }
}
