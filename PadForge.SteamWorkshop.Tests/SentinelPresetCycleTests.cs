using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v20 pins: CHANGE_PRESET sentinel ids are
    /// commands, not preset references. 32766 (change to next action set)
    /// and 32765 (change to previous action set) lower to one Cycle
    /// activator through every action set in authored order, with Base
    /// riding the ring's include-Base stop and previous walking the ring
    /// in reverse. Grounding: the community .vdf grammar guide (Steam
    /// file 932405100) documents both sentinel forms, and corpus fixture
    /// 3353604014 carries the next form once. Genuinely dangling numeric
    /// ids keep the MissingPreset note.</summary>
    public class SentinelPresetCycleTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Sentinel\"\n";

        private static string Group(int id, string mode, string body = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{body}\t}}\n";

        private static string Inputs(params (string Name, string Binding)[] members)
        {
            var sb = new System.Text.StringBuilder("\t\t\"inputs\"\n\t\t{\n");
            foreach (var (name, binding) in members)
            {
                sb.Append($"\t\t\t\"{name}\"\n\t\t\t{{\n");
                sb.Append("\t\t\t\t\"activators\"\n\t\t\t\t{\n");
                sb.Append("\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n");
                sb.Append("\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n");
                sb.Append($"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n");
                sb.Append("\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n");
            }
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

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

        private static string ThreeSetConfig(string sentinelBinding) => Head
            + Group(1, "four_buttons", Inputs(
                ("button_a", sentinelBinding),
                ("button_b", "key_press E")))
            + Group(2, "four_buttons", Inputs(("button_b", "key_press F")))
            + Group(3, "four_buttons", Inputs(("button_b", "key_press G")))
            + Preset(0, "Default", (1, "button_diamond active"))
            + Preset(1, "AltOne", (2, "button_diamond active"))
            + Preset(2, "AltTwo", (3, "button_diamond active"))
            + "}\n";

        // ─── Sentinel next: the authored-order set ring ─────────────────

        [Fact]
        public void ChangePresetNext_LowersToSetCycle_InAuthoredOrder()
        {
            var p = Translate(ThreeSetConfig("controller_action CHANGE_PRESET 32766 1 1"));

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Cycle", act.Mode);
            Assert.Equal("Layer_42_1|Layer_42_2", act.CycleLayers);
            Assert.True(act.CycleIncludeBase);
            Assert.Equal("Layer_42_1", act.LayerMask);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.False(act.InheritUnmapped); // action sets replace
            Assert.Equal("Default / AltOne / AltTwo", act.LayerName);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MissingPreset);
        }

        [Fact]
        public void ChangePresetPrevious_WalksTheRingInReverse()
        {
            var p = Translate(ThreeSetConfig("controller_action CHANGE_PRESET 32765 1 1"));

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Cycle", act.Mode);
            Assert.Equal("Layer_42_2|Layer_42_1", act.CycleLayers);
            Assert.True(act.CycleIncludeBase);
            Assert.Equal("Layer_42_2", act.LayerMask);
            Assert.Equal("AltTwo / AltOne / Default", act.LayerName);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MissingPreset);
        }

        /// <summary>Every set in the ring is reachable through the cycle,
        /// so no set may draw the PresetHasNoActivator note even though
        /// only the first stop is the activator's own mask.</summary>
        [Fact]
        public void ChangePresetNext_RingCoveredSets_DrawNoActivatorlessNote()
        {
            var p = Translate(ThreeSetConfig("controller_action CHANGE_PRESET 32766 1 1"));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.PresetHasNoActivator);
        }

        // ─── Sentinel next on a single-set config ───────────────────────

        /// <summary>A single-set config still gets its activator: the ring
        /// is Base alone (empty queue plus the include-Base stop), the
        /// runtime never steps an empty queue, and that matches Steam's
        /// next-set press when there is only one set to land on.</summary>
        [Fact]
        public void ChangePresetNext_SingleSet_EmitsBaseOnlyRing()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    ("button_a", "controller_action CHANGE_PRESET 32766 1 1"),
                    ("button_b", "key_press E")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Cycle", act.Mode);
            Assert.Equal("", act.CycleLayers);
            Assert.True(act.CycleIncludeBase);
            Assert.Equal("", act.LayerMask);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MissingPreset);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ShiftLayerEmitted
                && e.Emitted == "Cycle -> Base");
        }

        // ─── Genuine danglers keep the note ─────────────────────────────

        [Fact]
        public void ChangePreset_DanglingNumericIndex_KeepsMissingPresetNote()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    ("button_a", "controller_action CHANGE_PRESET 9 1 1"),
                    ("button_b", "key_press E")))
                + Group(2, "four_buttons", Inputs(("button_b", "key_press F")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "AltOne", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.KbmMappingSet.ShiftActivators);
            Assert.Empty(p.XboxMappingSet.ShiftActivators);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MissingPreset
                && e.ReasonArgs.Contains("9"));
        }
    }
}
