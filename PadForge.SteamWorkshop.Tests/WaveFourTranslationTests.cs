using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v5 (Wave 4a, #225) edge tests: the flickstick
    /// joystick mode. Happy-path corpus coverage is pinned by the golden
    /// fixture 2374887917 (right_joystick flickstick with Dots Per 360,
    /// an inactive twin, a modeshift, and an action-layer preset); these
    /// tests pin the branches the fixture misses (left stick, trackpad
    /// host, the dropped tuning-key vocabulary, layer-hosted rows).</summary>
    public class WaveFourTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
            });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Edge\"\n";

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
             + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
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

        // ─── B-16 flickstick ────────────────────────────────────────────

        [Fact]
        public void Flickstick_RightJoystick_EmitsFlickSource_WithDotsPer360()
        {
            // "sensitivity" is Steam's shared Dots Per 360 (client l10n:
            // "Flick Stick ° to Mouse Pixels (Dots Per 360°)"; corpus
            // 2374887917 carries 2788).
            string vdf = Head
                + Group(1, "flickstick", Settings(("sensitivity", "2788")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmMouseX", x.Target);
            Assert.Equal("Base", x.LayerMask);
            var src = Assert.Single(x.Sources);
            Assert.Equal("Flick Stick Right", src.Descriptor);
            Assert.Equal(2788, src.ParamFlickCountsPer360, 3);
            Assert.True(p.NeedsKbmSlot);
            Assert.All(p.Report.Entries, e => Assert.Equal(TranslationStatus.Clean, e.Status));
        }

        [Fact]
        public void Flickstick_LeftJoystick_NoSettings_KeepsJsmDefaultDots()
        {
            // Valve's shipped flickstick templates carry no settings; the
            // default stays the JSM-derived 14400 (REAL_WORLD_CALIBRATION
            // 40 x 360).
            string vdf = Head
                + Group(1, "flickstick", "")
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var x = Assert.Single(p.KbmMappingSet.Rows);
            var src = Assert.Single(x.Sources);
            Assert.Equal("Flick Stick Left", src.Descriptor);
            Assert.Equal(14400, src.ParamFlickCountsPer360, 3);
        }

        [Fact]
        public void Flickstick_ClickMember_TranslatesAsStickClick()
        {
            // Valve's flickstick templates and the corpus bind the stick
            // click inside the flickstick group; the member walk must keep
            // translating it.
            string vdf = Head
                + Group(1, "flickstick", Inputs(Inp("click", "xinput_button JOYSTICK_RIGHT")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX"
                && r.Sources.Any(s => s.Descriptor == "Flick Stick Right"));
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "RightThumbButton"
                && r.Sources.Any(s => s.Descriptor == "Gamepad RightStick"));
        }

        [Fact]
        public void Flickstick_OnTrackpad_SkipsNamed_MembersStillTranslate()
        {
            // The gordon-era corpus hosts flickstick on trackpads
            // (2228940979 binds it to the right pad); PadForge's flick
            // stick reads a physical stick only.
            string vdf = Head
                + Group(1, "flickstick", Inputs(Inp("click", "key_press E")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.KbmMappingSet.Rows, r =>
                r.Sources.Any(s => (s.Descriptor ?? "").StartsWith("Flick Stick")));
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.FlickStickSurfaceNotSupported
                && e.Status == TranslationStatus.Skipped);
            // The click member still lands (pad click -> key E).
            var key = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", key.Target); // E
            Assert.Equal("Touchpad 1 Click", Assert.Single(key.Sources).Descriptor);
        }

        [Fact]
        public void Flickstick_UngroundedTuningKeys_ReportOneNamedPartial()
        {
            // The wild tuning vocabulary (2779652507 / 2228940979):
            // edge_binding_radius, mouse_smoothing, rotation,
            // transition_time. Mapping them onto the JSM knobs would be a
            // semantics guess, so they ride one named Partial per group.
            string vdf = Head
                + Group(1, "flickstick", Settings(
                    ("edge_binding_radius", "19566"),
                    ("sensitivity", "2603"),
                    ("rotation", "-1"),
                    ("mouse_smoothing", "6"),
                    ("transition_time", "106")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal(2603, src.ParamFlickCountsPer360, 3);

            var partial = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.FlickStickTuningDropped);
            Assert.Equal(TranslationStatus.Partial, partial.Status);
            string keys = Assert.Single(partial.ReasonArgs);
            Assert.Contains("edge_binding_radius", keys);
            Assert.Contains("mouse_smoothing", keys);
            Assert.Contains("rotation", keys);
            Assert.Contains("transition_time", keys);
            Assert.DoesNotContain("sensitivity", keys);
        }

        [Fact]
        public void Flickstick_OnActionLayerPreset_LandsOnLayerRow()
        {
            // #225's headline: flick stick hosted on a non-Base layer. A
            // second preset's rows carry the deterministic layer mask, so
            // the engine's layer engage/disengage machinery hosts the flick
            // source.
            string vdf = Head
                + Group(1, "joystick_move", "")
                + Group(2, "flickstick", Settings(("sensitivity", "2788")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + Preset(1, "Preset_1000001", (2, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var flickRow = p.KbmMappingSet.Rows.Single(r =>
                r.Sources.Any(s => s.Descriptor == "Flick Stick Right"));
            Assert.Equal("Layer_42_1", flickRow.LayerMask);
            Assert.Equal("KbmMouseX", flickRow.Target);
        }
    }
}
