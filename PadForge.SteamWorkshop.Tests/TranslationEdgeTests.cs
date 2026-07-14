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

        // ─── Explicit identity rows ─────────────────────────────────────

        [Fact]
        public void PureAutomapPreset_EmitsExplicitIdentityRows()
        {
            // Imported sets are authoritative (the legacy automap never adds
            // to them), so automap-identical bindings must materialize as
            // real rows instead of the former zero-row passthrough.
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
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Equal(new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY" },
                p.XboxMappingSet.Rows.Select(r => r.Target).ToArray());
            foreach (var row in p.XboxMappingSet.Rows)
            {
                var src = Assert.Single(row.Sources);
                Assert.Equal("Gamepad " + row.Target, src.Descriptor);
                Assert.True(string.IsNullOrEmpty(src.DeviceGuid));
            }
            Assert.Equal(4, p.Report.Entries.Count);
            Assert.All(p.Report.Entries, e =>
            {
                Assert.Equal(TranslationStatus.Clean, e.Status);
                Assert.Equal(TranslationReasons.RowEmitted, e.ReasonKey);
            });
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.DefaultAutomapPassthrough);
        }

        [Fact]
        public void PureIdentityConfig_StillNeedsXboxSlot()
        {
            string vdf = Head
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "xinput_button A")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.True(p.NeedsXboxSlot);
            Assert.False(p.NeedsKbmSlot);
        }

        [Fact]
        public void DivergentTargetAbsorbsIdentityBinding()
        {
            // button_a -> B (divergent), button_b -> B (identity): the B row
            // must carry BOTH sources, not a duplicate ButtonB row.
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
            // ButtonA's automap default no longer fires at all on an
            // authoritative imported set, so the old "automap also active"
            // warning would be false and must not appear.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.AutomapAlsoActive);
        }

        // ─── Nintendo-labeled diamond (Switch family) ───────────────────

        private static string HeadWithType(string controllerType)
            => "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Edge\"\n"
             + $"\t\"controller_type\"\t\"{controllerType}\"\n";

        [Theory]
        [InlineData("controller_switch_pro")]
        [InlineData("controller_switch2_pro")]
        [InlineData("controller_switch_joycon_left")]
        [InlineData("controller_switch_joycon_right")]
        [InlineData("controller_switch_joycon_pair")]
        public void SwitchFamily_DiamondResolvesByNintendoLabel(string type)
        {
            // Switch configs name the diamond by label: button_a is the
            // A-labeled cap on the EAST, which is the positional ButtonB.
            string vdf = HeadWithType(type)
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "key_press E")
                    + Inp("button_x", "key_press Q")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var e = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey45"); // E
            var q = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey51"); // Q
            Assert.Equal("Gamepad ButtonB", Assert.Single(e.Sources).Descriptor);
            Assert.Equal("Gamepad ButtonY", Assert.Single(q.Sources).Descriptor);
        }

        [Theory]
        [InlineData("controller_xbox360")]
        [InlineData("controller_xboxone")]
        [InlineData("controller_ps4")]
        [InlineData("controller_ps5")]
        [InlineData("controller_neptune")]
        [InlineData("controller_steamcontroller_gordon")]
        [InlineData("")]
        public void NonSwitchTypes_DiamondStaysPositional(string type)
        {
            string vdf = (type.Length == 0 ? Head : HeadWithType(type))
                + Group(1, "four_buttons", SimpleInput("button_a", "key_press E"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("Gamepad ButtonA", Assert.Single(row.Sources).Descriptor);
        }

        [Fact]
        public void SwitchConfig_LabelCrossBinding_IsPositionalIdentity()
        {
            // The corpus norm (sonic campaign, 3354224367): a Switch config
            // that binds label-A to xinput B IS the positional passthrough.
            // Identity detection must run on the post-swap source, so the
            // whole crossed diamond materializes as clean identity rows.
            string vdf = HeadWithType("controller_switch_pro")
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "xinput_button B")
                    + Inp("button_b", "xinput_button A")
                    + Inp("button_x", "xinput_button Y")
                    + Inp("button_y", "xinput_button X")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY" },
                p.XboxMappingSet.Rows.Select(r => r.Target).ToArray());
            foreach (var row in p.XboxMappingSet.Rows)
                Assert.Equal("Gamepad " + row.Target, Assert.Single(row.Sources).Descriptor);
            Assert.All(p.Report.Entries, e =>
            {
                Assert.Equal(TranslationStatus.Clean, e.Status);
                Assert.Equal(TranslationReasons.RowEmitted, e.ReasonKey);
            });
        }

        [Fact]
        public void SwitchConfig_LabelIdentityBinding_IsCrossedRow()
        {
            // The converse: label-A to xinput A means the EAST cap emits A,
            // a real crossed row (ButtonA <- Gamepad ButtonB), never an
            // identity.
            string vdf = HeadWithType("controller_switch_pro")
                + Group(1, "four_buttons", SimpleInput("button_a", "xinput_button A"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonA", row.Target);
            Assert.Equal("Gamepad ButtonB", Assert.Single(row.Sources).Descriptor);
        }

        [Fact]
        public void SwitchConfig_NonDiamondInputs_Unaffected()
        {
            string vdf = HeadWithType("controller_switch_pro")
                + Group(1, "dpad", SimpleInput("dpad_north", "key_press E"))
                + Group(2, "switches",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_escape", "key_press Q")
                    + Inp("left_bumper", "key_press R")
                    + "\t\t}\n")
                + Preset(0, "Default", (1, "dpad active"), (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad DPadUp",
                p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey45").Sources.Single().Descriptor);
            Assert.Equal("Gamepad ButtonStart",
                p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey51").Sources.Single().Descriptor);
            Assert.Equal("Gamepad LeftShoulder",
                p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey52").Sources.Single().Descriptor);
        }

        [Fact]
        public void SwitchConfig_ActivatorReference_UsesPostSwapSource()
        {
            // Activator references must ride the post-swap source too:
            // label-A holds the layer from the EAST cap (Gamepad ButtonB).
            string vdf = HeadWithType("controller_switch_pro")
                + Group(1, "four_buttons",
                    "\t\t\"inputs\"\n\t\t{\n"
                    + Inp("button_a", "controller_action HOLD_LAYER 2")
                    + "\t\t}\n")
                + Group(2, "four_buttons", SimpleInput("button_b", "key_press E"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Gamepad ButtonB", act.Descriptor);
            // And the layer's own row reads label-B as positional south.
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("Gamepad ButtonA", Assert.Single(row.Sources).Descriptor);
        }

        [Fact]
        public void SwitchConfig_MacroTriggerBit_UsesPostSwapSource()
        {
            // Release-activator key taps become macros triggered by the
            // Xbox combined-output bit. Label-B is the positional SOUTH
            // cap, so the trigger mask must be the Xbox A bit.
            string vdf = HeadWithType("controller_switch_pro")
                + Group(1, "four_buttons", SimpleInput("button_b", "key_press E", "Release"))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var macro = Assert.Single(p.Macros);
            Assert.Equal(PadForge.Engine.Gamepad.A, macro.TriggerXboxButtons);
        }

        // ─── Matched-side implicit analog outputs ───────────────────────

        [Fact]
        public void MatchedStick_EmitsExplicitAxisPassthroughRows()
        {
            // joystick_move with no output_joystick redirect: Steam passes
            // the stick through implicitly, so the authoritative set gets
            // the explicit axis pair.
            string vdf = Head
                + Group(1, "joystick_move", SimpleInput("click", "xinput_button JOYSTICK_LEFT"))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "LeftThumbAxisX");
            var y = p.XboxMappingSet.Rows.Single(r => r.Target == "LeftThumbAxisY");
            Assert.Equal("Gamepad LeftStickX", Assert.Single(x.Sources).Descriptor);
            Assert.Equal("Gamepad LeftStickY", Assert.Single(y.Sources).Descriptor);
            Assert.False(x.Sources[0].HalfAxis);
            Assert.True(string.IsNullOrEmpty(x.Sources[0].DeviceGuid));
            // The click identity still lands as its own row.
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbButton");
        }

        [Fact]
        public void MatchedTrigger_AnalogSourcePrimary_ClickIdentityAbsorbed()
        {
            string vdf = Head
                + Group(1, "trigger", SimpleInput("click", "xinput_button TRIGGER_LEFT"))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("LeftTrigger", row.Target);
            // Direct full-axis passthrough is primary; the click identity
            // absorbs behind it instead of standing alone as the only
            // upper-half source.
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("Gamepad LeftTrigger", row.Sources[0].Descriptor);
            Assert.False(row.Sources[0].HalfAxis);
            Assert.Equal("Gamepad LeftTrigger", row.Sources[1].Descriptor);
            Assert.True(row.Sources[1].HalfAxis);
            // Axis default combine (max-abs), not Sum: the pull must stay a
            // clean analog read with the click leg riding on top.
            Assert.True(string.IsNullOrEmpty(row.CombineMode));
        }

        [Fact]
        public void DivergentStick_MouseMode_GetsNoMatchedAxisRows()
        {
            // joystick_mouse re-natures the stick to mouse output: no
            // implicit stick passthrough exists, so none may be synthesized.
            string vdf = Head
                + Group(1, "joystick_mouse", SimpleInput("click", "xinput_button JOYSTICK_LEFT"))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisY");
            Assert.Contains(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
        }

        [Fact]
        public void PureKbmConfig_MatchedStickEmitsNothing_XboxStaysEmpty()
        {
            // No xinput binding anywhere: the Xbox side is not in play, so
            // the matched stick must not sprout Xbox rows (a pure keyboard
            // config imports without a phantom Xbox pad).
            string vdf = Head
                + Group(1, "joystick_move", SimpleInput("click", "key_press E"))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.False(p.NeedsXboxSlot);
            Assert.Contains(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
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
        public void MousePositionOnPaddle_RidesDeviceFreeDescriptorTrigger()
        {
            // Wave 3: paddle-hosted cursor warps trigger on the paddle's
            // own descriptor (empty-guid InputDevice entry) instead of the
            // old NoDeviceFreeTrigger skip.
            string vdf = Head
                + Group(1, "switches", SimpleInput("button_back_left", "controller_action MOUSE_POSITION 100 100 1"))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MoveMouseToScreenPosition, m.Action);
            Assert.Equal("Gamepad Paddle2", Assert.Single(m.TriggerInputDescriptors));
            Assert.Equal(0, (int)m.TriggerXboxButtons);
            Assert.Equal("", m.TriggerAxisTarget);
            Assert.False(m.ConsumeTrigger);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
            Assert.Equal(TranslationReasons.MacroEmitted, entry.ReasonKey);
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
