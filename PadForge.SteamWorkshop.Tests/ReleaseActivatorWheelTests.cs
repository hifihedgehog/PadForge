using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// A `release` activator bound to mouse_wheel: a row would scroll for the
    /// whole hold and stop on release, the inverse of what the config asked
    /// for (audit 2026-07-14, found by Codex, skipped then). Since v15 the
    /// leg emits one discrete MouseWheelTap detent on the release edge, the
    /// wheel sibling of the mouse_button / XInput tap macros.
    /// </summary>
    public class ReleaseActivatorWheelTests
    {
        private static TranslatedProfile Translate(string vdf)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 91 });
        }

        /// <summary>One four_buttons group whose button_a carries a single
        /// binding under the named activator.</summary>
        private static string Vdf(string binding, string activator) =>
            "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"ReleaseWheel\"\n\t\"controller_type\"\t\"controller_xbox360\"\n"
            + "\t\"group\"\n\t{\n\t\t\"id\"\t\"10\"\n\t\t\"mode\"\t\"four_buttons\"\n"
            + "\t\t\"inputs\"\n\t\t{\n"
            + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
            + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
            + "\t\t}\n\t}\n"
            + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Preset_1000000\"\n"
            + "\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"10\"\t\"button_diamond active\"\n\t\t}\n\t}\n"
            + "}\n";

        [Fact]
        public void ReleaseActivator_OnMouseWheel_EmitsOneDetentTap()
        {
            var t = Translate(Vdf("mouse_wheel SCROLL_UP", "release"));

            Assert.DoesNotContain(t.XboxMappingSet.Rows, r => r.Target.StartsWith("KbmScroll"));
            Assert.DoesNotContain(t.KbmMappingSet.Rows, r => r.Target.StartsWith("KbmScroll"));
            var m = Assert.Single(t.Macros);
            Assert.Equal(TranslatedMacroAction.MouseWheelTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal(1, m.WheelTicks);
            Assert.False(m.WheelHorizontal);
            Assert.DoesNotContain(t.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
        }

        [Fact]
        public void FullPressActivator_OnMouseWheel_StillTranslates()
        {
            // Same-window positive control: the skip must not swallow the
            // ordinary binding the wheel leg exists to emit.
            var t = Translate(Vdf("mouse_wheel SCROLL_UP", "Full_Press"));

            var rows = t.XboxMappingSet.Rows.Concat(t.KbmMappingSet.Rows).ToList();
            Assert.Contains(rows, r => r.Target == "KbmScroll");
            Assert.DoesNotContain(t.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
        }

        [Fact]
        public void ReleaseActivator_OnMouseButton_EmitsTapMacro()
        {
            // v10 G6: the mouse_button release leg emits one click on the
            // release edge instead of the old named skip.
            var t = Translate(Vdf("mouse_button LEFT", "release"));

            var m = Assert.Single(t.Macros);
            Assert.Equal(TranslatedMacroAction.MouseButtonTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal(0, m.MouseButtonIndex);
            Assert.DoesNotContain(t.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
        }
    }
}
