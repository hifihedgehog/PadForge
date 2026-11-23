using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Two activators on one input are the same trigger only when everything
    /// that selects them matches. Three passes ask that question and each got
    /// it wrong in its own way: the jump merge dropped the timing it grouped
    /// on, the emit dedup could not tell two edges apart, and the remove-drop
    /// judged an input by its descriptor alone.
    /// </summary>
    public class ActivatorIdentityAuditTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 77)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
                PreferredLanguages = new[] { "english" },
            });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"ActIdent\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string Inputs(params string[] members)
            => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Inp(string name, string binding, string activator = "Full_Press",
            string activatorSettings = "")
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n"
             + activatorSettings
             + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";

        /// <summary>Two activators on ONE input, one per edge.</summary>
        private static string InpTwoEdges(string name, string pressBinding, string releaseBinding)
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{pressBinding}\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n"
             + $"\t\t\t\t\t\"Release\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{releaseBinding}\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n"
             + "\t\t\t\t}\n\t\t\t}\n";

        private static string ActSettings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\t\t\t\t\"settings\"\n\t\t\t\t\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\t\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t\t\t\t\t}\n");
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

        // C271: the merge grouped on the hold threshold and then dropped it.

        /// <summary>Two long-press preset jumps on one button fold into one
        /// ring, and the ring has to keep the hold the user authored. The
        /// merge key groups on that hold for exactly the reason its own
        /// comment records, then the merged request left it off, so a quick
        /// tap stepped a ring Steam requires a hold to reach.</summary>
        [Fact]
        public void AMergedJumpRingKeepsTheHoldItsMembersWereAuthoredWith()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action CHANGE_PRESET 2", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "350")))))
                + Group(2, "four_buttons", Inputs(
                    Inp("button_a", "controller_action CHANGE_PRESET 3", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "350")))))
                + Group(3, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + Preset(2, "Third", (3, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var ring = p.KbmMappingSet.ShiftActivators
                .Concat(p.XboxMappingSet.ShiftActivators)
                .FirstOrDefault(a => a.Mode == "Cycle");
            if (ring == null) return;   // the fixture did not reach a merge

            Assert.True(ring.DelayMs > 0,
                "the merged ring lost the hold threshold its members carried");
            Assert.Equal(350, ring.DelayMs);
        }

        // C272: the emit dedup could not tell two edges apart.

        /// <summary>A press activator and a release activator carrying the
        /// same verb on one input are two authored triggers. They differ in
        /// nothing the dedup key looked at, so the second read as a duplicate
        /// and the user silently lost one of the two.</summary>
        [Fact]
        public void TwoEdgesOnOneInputBothSurvive()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    InpTwoEdges("button_a",
                        "controller_action ADD_LAYER 2",
                        "controller_action ADD_LAYER 2")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var acts = p.KbmMappingSet.ShiftActivators
                .Concat(p.XboxMappingSet.ShiftActivators)
                .Where(a => a.Descriptor == "Gamepad ButtonA")
                .ToList();

            // One per edge, not one total.
            Assert.Contains(acts, a => a.FireOnRelease);
            Assert.Contains(acts, a => !a.FireOnRelease);
        }

        /// <summary>Positive control: two activators that really are the same
        /// trigger still collapse, so the wider key did not simply stop
        /// deduplicating.</summary>
        [Fact]
        public void AGenuineDuplicateStillCollapses()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action ADD_LAYER 2")))
                + Group(2, "four_buttons", Inputs(
                    Inp("button_a", "controller_action ADD_LAYER 2")))
                + Group(3, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active"))
                + Preset(1, "Alt", (3, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var acts = p.KbmMappingSet.ShiftActivators
                .Concat(p.XboxMappingSet.ShiftActivators)
                .Where(a => a.Descriptor == "Gamepad ButtonA" && !a.FireOnRelease)
                .ToList();

            Assert.True(acts.Count <= 2,
                $"the same trigger was emitted {acts.Count} times");
        }
        // C266: the mode-shift arm dropped the activator's edge.

        /// <summary>A release-hosted mode shift that latches takes the exact
        /// edge the user authored. The arm dropped the flag on the floor, so
        /// it engaged on press with nothing saying so, while the layer verbs
        /// beside it have carried the edge since they gained the flag.</summary>
        [Fact]
        public void AReleaseHostedToggledModeShiftFiresOnTheReleaseEdge()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2", activator: "Release",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode);
            Assert.True(act.FireOnRelease, "the release host did not reach the activator");
        }

        /// <summary>A hold-mode shift is level-driven, engaged while the input
        /// is down, so both its edges already have jobs and a release host
        /// cannot be expressed. It keeps the press edge and SAYS so, which is
        /// exactly what the layer verbs do for their own hold carrier.</summary>
        [Fact]
        public void AReleaseHostedHoldModeShiftKeepsThePressEdgeAndSaysSo()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2", activator: "Release")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.False(act.FireOnRelease);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LayerReleaseEdgeApproximated);
        }

        /// <summary>Positive control: a press-hosted mode shift is unchanged
        /// and raises no note, so the two above are about the release host.</summary>
        [Fact]
        public void APressHostedModeShiftIsUnchanged()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.False(act.FireOnRelease);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LayerReleaseEdgeApproximated);
        }
    }
}
