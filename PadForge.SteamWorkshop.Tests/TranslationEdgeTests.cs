using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Hand-built configs for the translator's failure paths. The
    /// happy paths are pinned by the golden fixtures.</summary>
    public class TranslationEdgeTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Edge\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string SimpleInput(string name, string binding, string activator = "Full_Press")
            => "\t\t\"inputs\"\n\t\t{\n"
             + $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n";

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

        // ─── Empty config ───────────────────────────────────────────────

        [Fact]
        public void EmptyConfig_ProducesValidEmptyProfile()
        {
            var p = Translate(Head + "}\n");
            Assert.Equal("Edge", p.Name);
            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Empty(p.XboxMappingSet.ShiftActivators);
            Assert.Empty(p.KbmMappingSet.ShiftActivators);
            Assert.Empty(p.Macros);
            Assert.Equal(0, p.Report.XboxRowCount);
            Assert.Equal(0, p.Report.KbmRowCount);
            Assert.Empty(p.Report.Entries);
        }

        // ─── Reference cycle ────────────────────────────────────────────

        [Fact]
        public void ReferenceCycle_ReportsErrorWithoutCrashing()
        {
            string vdf = Head
                + Group(1, "reference", "\t\t\"settings\"\n\t\t{\n\t\t\t\"referenced_mode\"\t\"2\"\n\t\t}\n")
                + Group(2, "reference", "\t\t\"settings\"\n\t\t{\n\t\t\t\"referenced_mode\"\t\"1\"\n\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.Empty(p.KbmMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Error, entry.Status);
            Assert.Equal(TranslationReasons.ReferenceCycle, entry.ReasonKey);
        }

        [Fact]
        public void ReferenceToSelf_ReportsError()
        {
            string vdf = Head
                + Group(1, "reference", "\t\t\"settings\"\n\t\t{\n\t\t\t\"referenced_mode\"\t\"1\"\n\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.ReferenceCycle, entry.ReasonKey);
        }

        [Fact]
        public void ReferenceChain_ResolvesWithinDepthCap()
        {
            // 1 -> 2 -> 3, where 3 is a real four_buttons group.
            string vdf = Head
                + Group(1, "reference", "\t\t\"settings\"\n\t\t{\n\t\t\t\"referenced_mode\"\t\"2\"\n\t\t}\n")
                + Group(2, "reference", "\t\t\"settings\"\n\t\t{\n\t\t\t\"referenced_mode\"\t\"3\"\n\t\t}\n")
                + Group(3, "four_buttons", SimpleInput("button_a", "key_press E"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target); // E = VK 0x45
            Assert.Equal("Gamepad ButtonA", Assert.Single(row.Sources).Descriptor);
        }

        // ─── Unknown vocabulary ─────────────────────────────────────────

        [Fact]
        public void UnknownBindingType_SkippedWithReason()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "warp_drive ENGAGE"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Empty(p.XboxMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Skipped, entry.Status);
            Assert.Equal(TranslationReasons.UnknownBindingType, entry.ReasonKey);
            Assert.Equal("warp_drive", Assert.Single(entry.ReasonArgs));
        }

        [Fact]
        public void UnknownKeyName_SkippedWithReason()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press FROBNICATE"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Skipped, entry.Status);
            Assert.Equal(TranslationReasons.UnknownKey, entry.ReasonKey);
            Assert.Equal("FROBNICATE", Assert.Single(entry.ReasonArgs));
        }

        [Fact]
        public void KnownButUnsupportedKey_SkippedWithReason()
        {
            // F13 resolves to a VK but the KbM output engine has no channel.
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press F13"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.UnsupportedKey, entry.ReasonKey);
        }

        [Fact]
        public void UnknownXInputButton_SkippedWithReason()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "xinput_button MEGABUTTON"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.UnknownXInputButton, entry.ReasonKey);
        }

        // ─── mode_shift to a missing group ──────────────────────────────

        [Fact]
        public void ModeShiftToMissingGroup_ReportsPartialAndNoActivator()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "mode_shift button_diamond 99"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.XboxMappingSet.ShiftActivators);
            Assert.Empty(p.KbmMappingSet.ShiftActivators);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
            Assert.Equal(TranslationReasons.MissingModeShiftGroup, entry.ReasonKey);
            Assert.Equal(new[] { "button_diamond", "99" }, entry.ReasonArgs);
        }

        [Fact]
        public void ModeShift_EmitsHoldActivatorAndLayerRows()
        {
            string inputs = SimpleInput("button_a", "mode_shift button_diamond 2");
            string vdf = Head
                + Group(1, "four_buttons", inputs)
                + Group(2, "four_buttons", SimpleInput("button_b", "key_press Q"))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            string layer = "Layer_42_0_MS_button_diamond_2";
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal(layer, row.LayerMask);
            Assert.Equal("KbmKey51", row.Target); // Q
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal(layer, act.LayerMask);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.True(act.InheritUnmapped);
            // The Xbox set has no rows on the layer, so no activator lands there.
            Assert.Empty(p.XboxMappingSet.ShiftActivators);
        }

        // ─── Automap passthrough recognition ────────────────────────────

        [Fact]
        public void PureAutomapPreset_EmitsZeroRowsAndOnePassthroughEntry()
        {
            string vdf = Head
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "xinput_button A")
                    + Inp("button_b", "xinput_button B")
                    + Inp("button_x", "xinput_button X")
                    + Inp("button_y", "xinput_button Y")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.Empty(p.KbmMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
            Assert.Equal(TranslationReasons.DefaultAutomapPassthrough, entry.ReasonKey);
            Assert.Equal("4", Assert.Single(entry.ReasonArgs));
        }

        [Fact]
        public void DivergentTargetAbsorbsIdentityBinding()
        {
            // button_a -> B (divergent), button_b -> B (identity): the B row
            // must carry BOTH sources, because a row suppresses the automap
            // fallback for its target.
            string vdf = Head
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "xinput_button B")
                    + Inp("button_b", "xinput_button B")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonB", row.Target);
            Assert.Equal(new[] { "Gamepad ButtonA", "Gamepad ButtonB" },
                row.Sources.Select(s => s.Descriptor).ToArray());
            // The remap leaves ButtonA's automap unclaimed: reported.
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AutomapAlsoActive
                && e.ReasonArgs.SequenceEqual(new[] { "Gamepad ButtonA", "ButtonA" }));
        }

        // ─── Macros ─────────────────────────────────────────────────────

        [Fact]
        public void HoldRepeatsKeyPress_BecomesAutofireMacro()
        {
            string inputs =
                "\t\t\"inputs\"\n\t\t{\n"
                + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
                + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
                + "\t\t\t\t\t\t\t\"binding\"\t\"key_press W\"\n"
                + "\t\t\t\t\t\t}\n"
                + "\t\t\t\t\t\t\"settings\"\n\t\t\t\t\t\t{\n"
                + "\t\t\t\t\t\t\t\"hold_repeats\"\t\"1\"\n"
                + "\t\t\t\t\t\t\t\"repeat_rate\"\t\"99\"\n"
                + "\t\t\t\t\t\t}\n"
                + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n";
            string vdf = Head
                + Group(1, "four_buttons", inputs)
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.KbmMappingSet.Rows); // macro replaces the row
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatKeyWhileHeld, m.Action);
            Assert.Equal("WhileHeld", m.TriggerMode);
            Assert.Equal(PadForge.Engine.Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(0x57, m.VirtualKey); // W
            Assert.Equal(99, m.IntervalMs);
            Assert.True(m.ConsumeTrigger);
        }

        [Fact]
        public void MousePosition_BecomesCursorWarpMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "controller_action MOUSE_POSITION 32768 16384 1"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MoveMouseToScreenPosition, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal(PadForge.Engine.Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(32768, m.NormalizedX);
            Assert.Equal(16384, m.NormalizedY);
            Assert.False(m.ConsumeTrigger);
        }

        [Fact]
        public void MousePositionOnPaddle_SkippedNoDeviceFreeTrigger()
        {
            string vdf = Head
                + Group(1, "switches", SimpleInput("button_back_left", "controller_action MOUSE_POSITION 100 100 1"))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.NoDeviceFreeTrigger, entry.ReasonKey);
        }

        [Fact]
        public void ReleaseKeyBinding_BecomesKeyTapMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press E", activator: "release"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal(0x45, m.VirtualKey);
        }

        // ─── Preset filtering and determinism plumbing ──────────────────

        [Fact]
        public void IncludedPresetIds_FiltersPresets()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press E"))
                + Group(2, "four_buttons", SimpleInput("button_b", "key_press Q"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Second", (2, "button_diamond active"))
                + "}\n";
            var all = Translate(vdf);
            Assert.Equal(2, all.KbmMappingSet.Rows.Count);
            Assert.Contains(all.KbmMappingSet.Rows, r => r.LayerMask == "Layer_42_1");

            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            var onlyBase = new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = 42,
                IncludedPresetIds = new System.Collections.Generic.HashSet<int> { 0 },
            });
            var row = Assert.Single(onlyBase.KbmMappingSet.Rows);
            Assert.Equal("Base", row.LayerMask);
        }

        [Fact]
        public void SecondPresetWithoutActivator_ReportsPartial()
        {
            string vdf = Head
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press E"))
                + Group(2, "four_buttons", SimpleInput("button_b", "key_press Q"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Second", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.PresetHasNoActivator
                && e.ReasonArgs.Contains("Second"));
        }

        private static string Inp(string name, string binding)
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";
    }
}
