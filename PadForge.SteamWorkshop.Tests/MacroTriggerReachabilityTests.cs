using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// A macro triggering on the Xbox slot's combined output only fires if some
    /// emitted row actually drives that bit. The macro-backed key forms
    /// (autofire, on-release) emit no row for their own source, so a button
    /// whose ONLY binding is one of them produced a macro that could never
    /// fire: the import looked complete and the feature was dead. Authoritative
    /// imported sets block the legacy automap from filling the gap either.
    ///
    /// <para>Audit 2026-07-14 (found by Codex, which executed the translator on
    /// this exact shape and got XboxRows=0). The rescue retargets such a
    /// trigger onto the hosting input's own descriptor, and must NOT disturb a
    /// trigger whose bit genuinely is fed.</para>
    /// </summary>
    public class MacroTriggerReachabilityTests
    {
        private static TranslatedProfile Translate(string vdf)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 77 });
        }

        /// <summary>button_a carries `binding` under `activator`; when
        /// <paramref name="bAlsoBound"/>, button_b additionally carries a plain
        /// identity xinput binding (its own row, a DIFFERENT bit).</summary>
        private static string Vdf(string binding, string activator, string actSettings = "",
            bool bAlsoBound = false)
        {
            string bInput = bAlsoBound
                ? "\t\t\t\"button_b\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
                  + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                  + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button B\"\n"
                  + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
                : "";

            return "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
                + "\t\"title\"\t\"MacroReach\"\n\t\"controller_type\"\t\"controller_xbox360\"\n"
                + "\t\"group\"\n\t{\n\t\t\"id\"\t\"10\"\n\t\t\"mode\"\t\"four_buttons\"\n"
                + "\t\t\"inputs\"\n\t\t{\n"
                + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
                + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
                + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
                + "\t\t\t\t\t\t}\n"
                + actSettings
                + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
                + bInput
                + "\t\t}\n\t}\n"
                + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Preset_1000000\"\n"
                + "\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"10\"\t\"button_diamond active\"\n\t\t}\n\t}\n"
                + "}\n";
        }

        private static string HoldRepeats() =>
            "\t\t\t\t\t\t\"settings\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"hold_repeats\"\t\"1\"\n\t\t\t\t\t\t}\n";

        [Fact]
        public void AutofireKey_OnAZeroRowSet_KeepsTheCombinedOutputTrigger()
        {
            // The macro-only shape: button_a's autofire is the config's ONLY
            // binding, so the Xbox set has zero rows. An empty set does not
            // replace the legacy mapping at runtime, so the slot's automap
            // still drives the A bit and the combined-output trigger fires.
            // The rescue must NOT touch this: retargeting here would break the
            // documented macro-only passthrough shape.
            var t = Translate(Vdf("key_press W", "Full_Press", HoldRepeats()));

            Assert.Empty(t.XboxMappingSet.Rows);
            var macro = Assert.Single(t.Profile_Macros());
            Assert.NotEqual(0, macro.TriggerXboxButtons);
            Assert.Empty(macro.TriggerInputDescriptors);
        }

        [Fact]
        public void AutofireKey_WhoseButtonDoesHaveARow_KeepsTheCombinedOutputTrigger()
        {
            // Same-window positive control. button_a keeps its identity row via
            // an explicit xinput_button A binding, so the A bit IS fed and the
            // cheaper consume-capable combined-output trigger must survive.
            string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
                + "\t\"title\"\t\"MacroReach\"\n\t\"controller_type\"\t\"controller_xbox360\"\n"
                + "\t\"group\"\n\t{\n\t\t\"id\"\t\"10\"\n\t\t\"mode\"\t\"four_buttons\"\n"
                + "\t\t\"inputs\"\n\t\t{\n"
                + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
                + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                + "\t\t\t\t\t\t\t\"binding\"\t\"key_press W\"\n"
                + "\t\t\t\t\t\t}\n" + HoldRepeats() + "\t\t\t\t\t}\n"
                + "\t\t\t\t\t\"Start_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button A\"\n"
                + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
                + "\t\t}\n\t}\n"
                + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Preset_1000000\"\n"
                + "\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"10\"\t\"button_diamond active\"\n\t\t}\n\t}\n"
                + "}\n";

            var t = Translate(vdf);
            var macro = t.Profile_Macros().FirstOrDefault(m => m.Name.StartsWith("Autofire"));
            Assert.NotNull(macro);
            Assert.NotEqual(0, macro.TriggerXboxButtons);
            Assert.Empty(macro.TriggerInputDescriptors);
        }

        [Fact]
        public void AutofireKey_OnANonEmptySetWithNoRowForItsOwnBit_IsRetargetedToTheInput()
        {
            // THE BUG, and Codex's exact reported shape: A carries key_press W
            // with hold_repeats, B carries an identity xinput_button B. B's row
            // makes the set non-empty, which suppresses the legacy passthrough,
            // but it feeds the B bit. Nothing feeds A, so A's macro could never
            // fire. The bit-level check is what distinguishes this from the
            // zero-row case above: a naive "are there any rows?" test gets both
            // wrong, in opposite directions.
            var t = Translate(Vdf("key_press W", "Full_Press", HoldRepeats(), bAlsoBound: true));

            Assert.Contains(t.XboxMappingSet.Rows, r => r.Target == "ButtonB");
            Assert.DoesNotContain(t.XboxMappingSet.Rows, r => r.Target == "ButtonA");

            var macro = Assert.Single(t.Profile_Macros());
            Assert.Equal(0, macro.TriggerXboxButtons);
            Assert.Contains("Gamepad ButtonA", macro.TriggerInputDescriptors);
            // A descriptor trigger reads the physical input: no bits to consume.
            Assert.False(macro.ConsumeTrigger);
        }

        // ── The analog-trigger shape of the same trigger ──
        //
        // FillMacroTrigger emits TWO combined-output shapes: a button bitmask
        // and, when the host is an analog trigger, TriggerXboxButtons=0 plus a
        // TriggerAxisTarget. It stashes a fallback descriptor for both. The
        // first cut of the rescue skipped every TriggerXboxButtons==0 macro,
        // which is exactly the axis shape, so an autofire hosted on a trigger
        // pull was never rescued. Same defect, same fix, other half of the
        // family.

        /// <summary>left_trigger carries `binding` inside a SWITCHES group, so
        /// it is bound as a switch and the group emits no implicit trigger-pull
        /// passthrough (only a `trigger`-mode group's MatchedAnalogs do that,
        /// and that implicit row would feed the axis and make the trigger
        /// reachable). button_a carries a plain identity binding, so the Xbox
        /// set is non-empty and the legacy passthrough is suppressed.</summary>
        private static string SwitchesTriggerVdf(string binding, string actSettings)
            => "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
             + "\t\"title\"\t\"MacroReachAxis\"\n\t\"controller_type\"\t\"controller_xbox360\"\n"
             + "\t\"group\"\n\t{\n\t\t\"id\"\t\"20\"\n\t\t\"mode\"\t\"switches\"\n"
             + "\t\t\"inputs\"\n\t\t{\n"
             + "\t\t\t\"left_trigger\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
             + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n" + actSettings + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
             + "\t\t}\n\t}\n"
             + "\t\"group\"\n\t{\n\t\t\"id\"\t\"21\"\n\t\t\"mode\"\t\"four_buttons\"\n"
             + "\t\t\"inputs\"\n\t\t{\n"
             + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
             + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
             + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button A\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
             + "\t\t}\n\t}\n"
             + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Preset_1000000\"\n"
             + "\t\t\"group_source_bindings\"\n\t\t{\n"
             + "\t\t\t\"20\"\t\"switch active\"\n"
             + "\t\t\t\"21\"\t\"button_diamond active\"\n"
             + "\t\t}\n\t}\n"
             + "}\n";

        [Fact]
        public void AutofireKey_OnATriggerWithNoRowForItsOwnAxis_IsRetargetedToTheInput()
        {
            var t = Translate(SwitchesTriggerVdf("key_press W", HoldRepeats()));

            var macro = t.Profile_Macros().FirstOrDefault(m => m.Name.StartsWith("Autofire"));
            Assert.NotNull(macro);

            // Precondition for the defect: the trigger took the AXIS-shaped
            // combined-output trigger (bitmask 0 + an axis target), and no row
            // feeds that axis. Skip rather than assert a false pass if the
            // translator ever stops producing this shape here.
            Assert.NotEmpty(t.XboxMappingSet.Rows);
            Assert.DoesNotContain(t.XboxMappingSet.Rows, r => r.Target == "LeftTrigger");

            // The fix: rescued onto its own physical input. Before it, the
            // finalize pass skipped every TriggerXboxButtons==0 macro, which is
            // exactly this shape, and the autofire was dead on import.
            Assert.Equal("", macro.TriggerAxisTarget);
            Assert.NotEmpty(macro.TriggerInputDescriptors);
            Assert.False(macro.ConsumeTrigger);
        }

        [Fact]
        public void AutofireKey_OnATriggerWhoseAxisIsFed_KeepsTheCombinedOutputTrigger()
        {
            // Same-window positive control for the axis shape, built the same
            // way the button one above is: give the SAME input a second
            // activator carrying its identity binding, so LeftTrigger genuinely
            // IS fed and the cheaper consume-capable combined-output trigger
            // must survive. This is what the rescue must not disturb, and it is
            // what distinguishes the fix from "retarget every axis macro".
            string vdf = SwitchesTriggerVdf("key_press W", HoldRepeats())
                .Replace(
                    "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n\t\"group\"\n\t{\n\t\t\"id\"\t\"21\"",
                    "\t\t\t\t\t}\n"
                    + "\t\t\t\t\t\"Start_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                    + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button trigger_left\"\n"
                    + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n"
                    + "\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n\t\"group\"\n\t{\n\t\t\"id\"\t\"21\"");

            var t = Translate(vdf);
            Assert.Contains(t.XboxMappingSet.Rows, r => r.Target == "LeftTrigger");

            var macro = t.Profile_Macros().FirstOrDefault(m => m.Name.StartsWith("Autofire"));
            Assert.NotNull(macro);
            Assert.Equal("LeftTrigger", macro.TriggerAxisTarget);
            Assert.Empty(macro.TriggerInputDescriptors);
        }
    }

    internal static class TranslatedProfileMacroExt
    {
        /// <summary>The run's macros. Named to keep the tests readable when the
        /// profile shape shifts.</summary>
        public static System.Collections.Generic.List<TranslatedMacro> Profile_Macros(this TranslatedProfile p)
            => p.Macros;
    }
}
