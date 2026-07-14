using System.Linq;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v6 (Wave 4b, #9 B-15) contracts.
    /// Verified semantics first: Steam's trackpad absolute_mouse ("As
    /// Mouse") moves the cursor RELATIVELY (trackball/friction settings
    /// vocabulary, the Steam Input API's delta delivery, sc-controller's
    /// importer, Valve's mobile-touch template naming), so the relative
    /// rows are faithful and the old AbsoluteMouseApproximated Partial is
    /// retired. The 1:1 pad-to-screen construct is mouse_region
    /// ("touching a particular place on the pad will always put the
    /// cursor in the same place on the screen", Steamworks Input Source
    /// Modes doc): trackpad-hosted regions now emit Clean absolute
    /// "Touchpad {p} Pointer X/Y" rows with the region geometry on the
    /// per-source window params. Corpus coverage rides the goldens
    /// (2795727040's scale-10 minimap regions above all); these tests pin
    /// the per-branch contracts.</summary>
    public class WaveFourBTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 47)
        {
            var config = Model.SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string HeadPs4 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Edge\"\n\t\"controller_type\"\t\"controller_ps4\"\n";
        private const string HeadDeck = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Edge\"\n\t\"controller_type\"\t\"controller_neptune\"\n";

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

        private static string EmptyInputs => "\t\t\"inputs\"\n\t\t{\n\t\t}\n";

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

        // ─── absolute_mouse: the retired false alarm ────────────────────

        [Fact]
        public void AbsoluteMouse_OnTrackpad_IsCleanRelative_NoPartial()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", EmptyInputs)
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            // The relative rows stay exactly as wave 3 shaped them.
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Touchpad 0 Finger 0 X Right", Assert.Single(x.Sources).Descriptor);
            // Verified semantics: no positioning-approximation Partial.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AbsoluteMouseApproximated);
            Assert.Equal(0, p.Report.PartialCount);
        }

        [Fact]
        public void AbsoluteMouse_OnGyro_IsCleanRelative_NoPartial()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", EmptyInputs)
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Gyro Yaw", Assert.Single(x.Sources).Descriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AbsoluteMouseApproximated);
        }

        // ─── mouse_region on trackpads: the real absolute pointer ───────

        [Fact]
        public void MouseRegion_Ps4RightTrackpad_EmitsCleanPointerRowsOnTheRightHalf()
        {
            string vdf = HeadPs4
                + Group(1, "mouse_region", EmptyInputs)
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            var sx = Assert.Single(x.Sources);
            Assert.Equal("Touchpad 0 Pointer X Right", sx.Descriptor);
            Assert.Equal(0.5, sx.ParamPointerCenter, 6);
            Assert.Equal(1.0, sx.ParamPointerExtent, 6);

            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.Equal("Touchpad 0 Pointer Y Right", Assert.Single(y.Sources).Descriptor);

            // Faithful now: no macro, no approximation Partial.
            Assert.Empty(p.Macros);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionApproximated);
            Assert.Equal(0, p.Report.PartialCount);
        }

        [Fact]
        public void MouseRegion_Geometry_RidesThePointerWindowParams()
        {
            // 2795727040's shape: a 10%-scale minimap region near the
            // bottom-left (position_x 9, position_y 10; Steam's position_y
            // is bottom-origin per sc-controller's importer), with per-axis
            // sensitivity scales.
            string vdf = HeadDeck
                + Group(1, "mouse_region", EmptyInputs + Settings(
                    ("scale", "10"), ("position_x", "9"), ("position_y", "10"),
                    ("sensitivity_horiz_scale", "110"), ("sensitivity_vert_scale", "70")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            var sx = Assert.Single(x.Sources);
            Assert.Equal("Touchpad 1 Pointer X", sx.Descriptor); // Deck right pad = pad 1, whole
            Assert.Equal(0.09, sx.ParamPointerCenter, 6);
            Assert.Equal(0.10 * 1.10, sx.ParamPointerExtent, 6);

            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            var sy = Assert.Single(y.Sources);
            Assert.Equal(1.0 - 0.10, sy.ParamPointerCenter, 6); // bottom-origin flip
            Assert.Equal(0.10 * 0.70, sy.ParamPointerExtent, 6);

            // The sensitivity scales are CONSUMED as extent, so the
            // curve-drop note must not name them here.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
        }

        [Fact]
        public void MouseRegion_TeleportAndEdgeKeys_GetTheNamedPartial()
        {
            string vdf = HeadPs4
                + Group(1, "mouse_region", EmptyInputs + Settings(
                    ("teleport_stop", "1"), ("edge_binding_radius", "32767")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionTuningDropped);
            Assert.Contains("teleport_stop", entry.ReasonArgs[0]);
            Assert.Contains("edge_binding_radius", entry.ReasonArgs[0]);
        }

        [Fact]
        public void MouseRegion_ZeroValuedTeleportKeys_StayQuiet()
        {
            // The corpus writes explicit zeros (2220285578 teleport_stop 0);
            // zero = off = exactly the pointer's behavior, so no note.
            string vdf = HeadPs4
                + Group(1, "mouse_region", EmptyInputs + Settings(
                    ("teleport_stop", "0"), ("teleport_start", "0")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionTuningDropped);
        }

        [Fact]
        public void MouseRegion_OnStick_KeepsTheClampApproximation()
        {
            // A stick's deflection is not an absolute position; the wave-2A
            // path stays: trigger-hosted regions get the clamp macro, and a
            // stick host (no press surface) keeps the named skip.
            string vdf = HeadPs4
                + Group(1, "mouse_region", EmptyInputs)
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.NoDeviceFreeTrigger);
        }

        [Fact]
        public void MouseRegion_OnLayer_KeepsTheHostingLayer()
        {
            // Workshop configs host regions on action-set layers; the rows
            // must land on the layer so Step 3's layer-aware pointer walk
            // engages them only while the layer is active.
            string vdf = HeadPs4
                + Group(1, "four_buttons", EmptyInputs)
                + Group(2, "mouse_region", EmptyInputs)
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Preset_1000001", (2, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.NotEqual("Base", x.LayerMask);
        }
    }
}
