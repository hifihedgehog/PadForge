using System.Linq;
using System.Text;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v8 (audit finding 1g): the Steam group settings
    /// invert_x / invert_y flip the emitted mouse-axis source's Invert flag
    /// on every mode whose engine read honors it (stick / touchpad-finger /
    /// gyro mouse modes and the trackpad mouse_region pointer), so an
    /// imported trackpad tracks the config instead of running reversed under
    /// a Clean label. Reads that ignore Invert (flick stick's angle map) and
    /// invert_z (no third mouse-delta axis) get the named
    /// AxisInversionNotApplied Partial. rotation / friction / mouse_smoothing
    /// / trackball get MouseModeTuningDropped, and group-level
    /// haptic_intensity now feeds the per-config haptic aggregate.</summary>
    public class GroupAxisInversionTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 47)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private static TranslatedProfile TranslateFixture(long fileId)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(TestFixtures.Read(fileId)));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string HeadPs4 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Inv\"\n\t\"controller_type\"\t\"controller_ps4\"\n";

        private static string Group(int id, string mode, string settings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n"
             + "\t\t\"inputs\"\n\t\t{\n\t\t}\n" + settings + "\t}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Preset(int id, string binding, int groupId = 1)
            => $"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"P\"\n"
             + "\t\t\"group_source_bindings\"\n\t\t{\n"
             + $"\t\t\t\"{groupId}\"\t\"{binding}\"\n\t\t}}\n\t}}\n";

        // ─── The corpus proof (fail-before / pass-after) ────────────────

        /// <summary>Fixture 1129670518 carries invert_x "1" / invert_y "1" on
        /// its left_trackpad absolute_mouse group (group 7). Before the fix
        /// both trackpad mouse axes imported un-inverted under a Clean label
        /// (finding 1g-1); after it the emitted finger sources carry Invert.
        /// This assertion FAILS on HEAD and PASSES with the fix.</summary>
        [Fact]
        public void Fixture1129670518_AbsoluteMouseRows_CarryInvert()
        {
            var p = TranslateFixture(1129670518);
            var sources = p.KbmMappingSet.Rows.SelectMany(r => r.Sources).ToList();

            Assert.Contains(sources, s =>
                s.Descriptor == "Touchpad 0 Finger 0 X" && s.Invert);
            Assert.Contains(sources, s =>
                s.Descriptor == "Touchpad 0 Finger 0 Y" && s.Invert);
        }

        /// <summary>The inverted rows stay Clean: the flag is honored by the
        /// finger read, so the label is honest.</summary>
        [Fact]
        public void Fixture1129670518_InvertedTrackpadRows_StayClean()
        {
            var p = TranslateFixture(1129670518);
            // group 7's rows are the left_trackpad absolute_mouse RowEmitted
            // entries; none of them are Skipped/Partial for the inversion.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AxisInversionNotApplied);
        }

        // ─── Per-mode honoring (synthetic) ──────────────────────────────

        [Fact]
        public void StickMouse_InvertXY_SetsSourceInvert_Clean()
        {
            string vdf = HeadPs4
                + Group(1, "joystick_mouse", Settings(("invert_x", "1"), ("invert_y", "1")))
                + Preset(0, "joystick active")
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.True(Assert.Single(x.Sources).Invert);
            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.True(Assert.Single(y.Sources).Invert);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AxisInversionNotApplied);
        }

        [Fact]
        public void TrackpadAbsoluteMouse_InvertXOnly_FlipsOnlyX()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", Settings(("invert_x", "1")))
                + Preset(0, "right_trackpad active")
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.True(Assert.Single(x.Sources).Invert);
            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.False(Assert.Single(y.Sources).Invert);
        }

        [Fact]
        public void GyroToMouse_InvertXY_SetsSourceInvert()
        {
            string vdf = HeadPs4
                + Group(1, "gyro_to_mouse", Settings(("invert_x", "1"), ("invert_y", "1")))
                + Preset(0, "gyro active")
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Gyro Yaw", Assert.Single(x.Sources).Descriptor);
            Assert.True(x.Sources[0].Invert);
            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.True(Assert.Single(y.Sources).Invert);
        }

        [Fact]
        public void MouseRegionTrackpad_InvertXY_SetsPointerInvert_Clean()
        {
            string vdf = HeadPs4
                + Group(1, "mouse_region", Settings(("invert_x", "1"), ("invert_y", "1")))
                + Preset(0, "right_trackpad active")
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            var sx = Assert.Single(x.Sources);
            Assert.StartsWith("Touchpad 0 Pointer X", sx.Descriptor);
            Assert.True(sx.Invert);
            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.True(Assert.Single(y.Sources).Invert);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AxisInversionNotApplied);
        }

        // ─── Reads that DON'T honor Invert → named Partial ──────────────

        [Fact]
        public void FlickStick_Invert_ReportsAxisInversionNotApplied_NoSourceInvert()
        {
            string vdf = HeadPs4
                + Group(1, "flickstick", Settings(("invert_x", "1")))
                + Preset(0, "right_joystick active")
                + "}\n";
            var p = Translate(vdf);

            // The flick source rides KbmMouseX but the angle read ignores
            // Invert, so the flag is NOT set (that would be a dead flag) and
            // the drop is named instead.
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.False(Assert.Single(x.Sources).Invert);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AxisInversionNotApplied);
        }

        [Fact]
        public void InvertZ_OnMouseMode_ReportsAxisInversionNotApplied()
        {
            string vdf = HeadPs4
                + Group(1, "gyro_to_mouse", Settings(("invert_z", "1")))
                + Preset(0, "gyro active")
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AxisInversionNotApplied
                && e.ReasonArgs.Contains("invert_z"));
        }

        // ─── rotation / friction / smoothing / trackball named drop ─────

        [Fact]
        public void MouseModeTuning_OnAbsoluteMouse_BuildsTheFeelChannel()
        {
            // v18: rotation lowers to two-source Sum rows with the
            // trigonometric coefficients folded into Sensitivity, and
            // mouse_smoothing rides the per-source EMA. friction without
            // trackball 1 shapes nothing (it is the trackball decay knob),
            // so nothing is named.
            string vdf = HeadPs4
                + Group(1, "absolute_mouse",
                    Settings(("rotation", "-18"), ("friction", "1"), ("mouse_smoothing", "22")))
                + Preset(0, "right_trackpad active")
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, x =>
                x.ReasonKey == TranslationReasons.MouseModeTuningDropped);
            var xRow = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal(2, xRow.Sources.Count); // cos leg + sin leg
            Assert.Equal("Sum", xRow.CombineMode);
            Assert.All(xRow.Sources, s => Assert.Equal(0.22, s.ParamSmoothingAlpha, 6));
        }

        [Fact]
        public void RotationZero_IsNotNamed()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", Settings(("rotation", "0")))
                + Preset(0, "right_trackpad active")
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, x =>
                x.ReasonKey == TranslationReasons.MouseModeTuningDropped);
        }

        // ─── group-level haptic_intensity (v22: no aggregate note) ──────

        [Fact]
        public void GroupLevelHapticIntensity_OnMemberlessGroup_LowersSilently()
        {
            // v22: the per-config HapticIntensityDropped aggregate retired.
            // A member-less group has no activation to tick on, so nothing
            // emits and nothing is named (the continuous surface-motion
            // tick has no channel).
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", Settings(("haptic_intensity", "2")))
                + Preset(0, "right_trackpad active")
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.Report.Entries, x =>
                x.ReasonKey == "Workshop_Tr_HapticIntensityDropped");
            Assert.DoesNotContain(p.Macros, m =>
                m.Action == TranslatedMacroAction.RumblePulse);
        }
    }
}
