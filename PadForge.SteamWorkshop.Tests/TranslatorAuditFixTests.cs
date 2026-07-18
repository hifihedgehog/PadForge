using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v9 (audit 2026-07-16) contracts the corpus
    /// goldens don't pin: unmerged CHANGE_PRESET lowering (the runtime's
    /// Custom mode latches LayerMask and ignores JumpToLayer),
    /// mouse_joystick as stick output (sc-controller's importer lowers the
    /// mode to ABS_RX/ABS_RY), stick-hosted diamond cells, the multi-pad
    /// center_trackpad skip, clamp-macro mouse_region dropped keys, and
    /// single-pad half-click chord activators (#9 B-1).</summary>
    public class TranslatorAuditFixTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Audit\"\n";
        private const string HeadPs4 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Audit\"\n\t\"controller_type\"\t\"controller_ps4\"\n";
        private const string HeadSwitch = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Audit\"\n\t\"controller_type\"\t\"controller_switch_pro\"\n";

        private static string Group(int id, string mode, string body = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{body}\t}}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

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

        // ─── F2: unmerged CHANGE_PRESET lowering ────────────────────────

        [Fact]
        public void UnmergedChangePreset_LatchesTargetLayer()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(("button_a", "controller_action CHANGE_PRESET 2 1 1")))
                + Group(2, "four_buttons", Inputs(("button_b", "key_press E")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Custom", act.Mode);
            Assert.Equal("Layer_42_1", act.LayerMask);
            Assert.Equal("", act.JumpToLayer); // runtime latches LayerMask, dialog shape
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.False(act.InheritUnmapped); // action sets replace
        }

        [Fact]
        public void UnmergedChangePresetToBase_CyclesHostingLayerWithBase()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(("button_b", "key_press E")))
                + Group(2, "four_buttons", Inputs(
                    ("button_a", "controller_action CHANGE_PRESET 1 1 1"),
                    ("button_b", "key_press F")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Cycle", act.Mode);
            Assert.Equal("Layer_42_1", act.CycleLayers);
            Assert.True(act.CycleIncludeBase);
            Assert.Equal("", act.JumpToLayer);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
        }

        [Fact]
        public void UnmergedChangePreset_NeverEmitsJumpMask()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(("button_a", "controller_action CHANGE_PRESET 2 1 1")))
                + Group(2, "four_buttons", Inputs(("button_b", "key_press E")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.KbmMappingSet.ShiftActivators, a => a.LayerMask.StartsWith("Jump_"));
            Assert.DoesNotContain(p.XboxMappingSet.ShiftActivators, a => a.LayerMask.StartsWith("Jump_"));
        }

        // ─── F3: mouse_joystick outputs a stick, not the cursor ─────────

        [Fact]
        public void GyroMouseJoystick_EmitsRightStickRows()
        {
            string vdf = Head
                + Group(1, "mouse_joystick")
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Equal(2, p.XboxMappingSet.Rows.Count);
            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisX");
            var y = p.XboxMappingSet.Rows.Single(r => r.Target == "RightThumbAxisY");
            Assert.Equal("Gyro Yaw", Assert.Single(x.Sources).Descriptor);
            Assert.Equal("Gyro Pitch", Assert.Single(y.Sources).Descriptor);
        }

        [Fact]
        public void MouseJoystick_OutputJoystick1_RedirectsToLeftStick()
        {
            string vdf = Head
                + Group(1, "mouse_joystick", Settings(("output_joystick", "1")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisY");
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target.StartsWith("RightThumb"));
        }

        // ─── F6: stick-hosted diamond cells ─────────────────────────────

        [Fact]
        public void JoystickFourButtons_ResolvePositionallyOntoWedges()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(("button_a", "key_press E")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftStickY", src.Descriptor); // A = south = Y upper half
            Assert.True(src.HalfAxis);
            Assert.False(src.Invert);
        }

        [Fact]
        public void JoystickFourButtons_NintendoLabelsFoldOntoPosition()
        {
            string vdf = HeadSwitch
                + Group(1, "four_buttons", Inputs(("button_a", "key_press E")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.KbmMappingSet.Rows);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftStickX", src.Descriptor); // A label sits east on Switch
            Assert.True(src.HalfAxis);
            Assert.False(src.Invert);
        }

        // ─── F7 (re-adjudicated in v25): center_trackpad reads pad 0
        // whole on EVERY type. The token means "the single central pad"
        // (25 of 30 wild authors are controller_ps4); no SDL device
        // registers a third pad, and the non-PS authors are
        // type-converted leftovers whose sections should drive whichever
        // pad-bearing device the user maps, not skip. ──────────────────

        [Fact]
        public void CenterTrackpadOnMultiPadConfig_ReadsPadZeroWhole()
        {
            string vdf = Head // typeless = multi-pad family
                + Group(1, "dpad", Inputs(("dpad_north", "key_press E")))
                + Preset(0, "Default", (1, "center_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.NotEmpty(p.KbmMappingSet.Rows);
            var src = p.KbmMappingSet.Rows.SelectMany(r => r.Sources).First();
            Assert.StartsWith("Touchpad 0 ", src.Descriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void CenterTrackpadOnSinglePadConfig_StillTranslates()
        {
            string vdf = HeadPs4 // single pad: center_trackpad = pad 0 whole
                + Group(1, "dpad", Inputs(("dpad_north", "key_press E")))
                + Preset(0, "Default", (1, "center_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.NotEmpty(p.KbmMappingSet.Rows);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        // ─── F8: clamp-macro mouse_region names its dropped keys ────────

        [Fact]
        public void TriggerHostedMouseRegion_NamesDroppedEdgeKeys()
        {
            string vdf = Head
                + Group(1, "mouse_region", Settings(
                    ("teleport_start", "1"),
                    ("edge_binding_radius", "25000")))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Contains(p.Macros, m => m.Action == TranslatedMacroAction.MouseLimitRegion);
            var entry = p.Report.Entries.Single(
                e => e.ReasonKey == TranslationReasons.MouseRegionTuningDropped);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
            Assert.Equal("teleport_start, edge_binding_radius", Assert.Single(entry.ReasonArgs));
        }

        // ─── F9: single-pad half click drives a chord activator ─────────

        [Fact]
        public void HalfPadClickModeShift_EmitsChordActivator()
        {
            string vdf = HeadPs4
                + Group(1, "switches", Inputs(("left_click", "mode_shift joystick 2")))
                + Group(2, "dpad", Inputs(("dpad_north", "xinput_button A")))
                + Preset(0, "Default", (1, "switch active"), (2, "joystick active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.XboxMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal("Chord", act.Kind);
            Assert.Equal("Touchpad 0 Click", act.Descriptor);
            Assert.Equal("Touchpad 0 TouchLeft", act.ChordSecondDescriptor);
            Assert.Equal("Layer_42_0_MS_joystick_2", act.LayerMask);
            Assert.True(act.InheritUnmapped);

            // The chord's touch-spot leg self-arms at apply (v14): the
            // activator's descriptors reference the spot, so no feature
            // note remains.
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == "Workshop_Tr_TrackpadFeatureRequired");
        }

        [Fact]
        public void WholeTouchModeShift_BuildsHeldSpotActivator()
        {
            // v23 (the lockdown re-audit): a bare half touch spot is a
            // held-state bool (the recognizer adds the spot key at contact
            // and removes it at lift), read by the activator's Button kind
            // through the same evaluator a chord leg uses, and the
            // touch-spots feature self-arms off act.Descriptor. So the
            // rejection was a translator gate, not an engine limit, and
            // the mode shift lowers (golden 2374887917's hold_layer twins
            // pin the layer-verb shape).
            string vdf = HeadPs4
                + Group(1, "single_button", Inputs(("touch", "mode_shift joystick 2")))
                + Group(2, "dpad", Inputs(("dpad_north", "xinput_button A")))
                + Preset(0, "Default", (1, "left_trackpad active"), (2, "joystick active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.XboxMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal("Button", act.Kind);
            Assert.Equal("Touchpad 0 TouchLeft", act.Descriptor);
            Assert.Equal("Layer_42_0_MS_joystick_2", act.LayerMask);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorInputNotSupported);
        }
    }
}
