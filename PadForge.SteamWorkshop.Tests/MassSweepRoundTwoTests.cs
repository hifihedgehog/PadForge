using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v25 pins: wild-corpus round two. The
    /// serializer-vocabulary switch members resolve (stick clicks, the
    /// Soft Pull thresholds, diamond / dpad members, pad touches, the
    /// lpad/rpad click pair), the mouse modes' doubletap member reads the
    /// tap gesture, seat folds close the dpad-hosted diamond and
    /// diamond-hosted dpad, always_on_action lowers onto the constant
    /// read with the macro layer gate, Double_Press layer verbs ride
    /// ShiftActivator.DoublePressMs, gyro_button_invert 2 is the Toggle
    /// arm, and the ratchet enum gains the single-pad center bits plus
    /// the v24 macro buttons.</summary>
    public class MassSweepRoundTwoTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V25\"\n";
        private const string HeadPs5 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V25\"\n\t\"controller_type\"\t\"controller_ps5\"\n";
        private const string HeadPs5Edge = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V25\"\n\t\"controller_type\"\t\"controller_ps5_edge\"\n";
        private const string HeadSwitchPro = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V25\"\n\t\"controller_type\"\t\"controller_switch_pro\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string Inputs(params string[] members)
            => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        private static string Inp(string name, string binding, string activator = "Full_Press",
            string activatorSettings = "")
            => $"\t\t\t\"{name}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n"
             + $"\t\t\t\t\t\"{activator}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{{\n"
             + $"\t\t\t\t\t\t\t\"binding\"\t\"{binding}\"\n"
             + "\t\t\t\t\t\t}\n"
             + activatorSettings
             + "\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n";

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

        private static void AssertNoUnknownInput(TranslatedProfile p)
            => Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);

        // ─── Serializer-vocabulary switch members ───────────────────────

        [Fact]
        public void StickClicks_AsSwitchMembers_Resolve()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_stick_click", "xinput_button A"),
                    Inp("right_stick_click", "xinput_button B")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad LeftStick",
                p.XboxMappingSet.Rows.Single(r => r.Target == "ButtonA").Sources.Single().Descriptor);
            Assert.Equal("Gamepad RightStick",
                p.XboxMappingSet.Rows.Single(r => r.Target == "ButtonB").Sources.Single().Descriptor);
            AssertNoUnknownInput(p);
        }

        [Fact]
        public void TriggerThresholds_AreTheSoftPullRead()
        {
            // "Soft Pull" (ControllerBinding_TriggerAnalogThresholdBinding
            // in the shipped strings): the trigger slot's edge shape.
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("left_trigger_threshold", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var src = p.KbmMappingSet.Rows.Single().Sources.Single();
            Assert.Equal("Gamepad LeftTrigger", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(15, src.DeadZone);
            AssertNoUnknownInput(p);
        }

        [Fact]
        public void TrackpadTouch_SwitchMembers_MultiPadAndSinglePad()
        {
            string body = Group(1, "switches", Inputs(Inp("right_trackpad_touch", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            // Multi-pad (typeless): the side's own contact bool.
            Assert.Equal("Touchpad 1 Finger 0 Down",
                Translate(Head + body).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
            // Single-pad (#9 B-1): the half's held-state touch spot.
            Assert.Equal("Touchpad 0 TouchRight",
                Translate(HeadPs5 + body).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
        }

        [Fact]
        public void PadClickTokenPair_ButtonLpadRpad_Resolve()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("button_lpad", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            Assert.Equal("Touchpad 0 Click",
                Translate(vdf).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
        }

        [Fact]
        public void DiamondMembers_InSwitchesGroups_ResolveWithLabelFold()
        {
            string body = Group(1, "switches", Inputs(Inp("button_y", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            // Positional family: Y = north.
            Assert.Equal("Gamepad ButtonY",
                Translate(Head + body).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
            // Nintendo labels sit crossed: the Y-labeled cap is WEST = X.
            Assert.Equal("Gamepad ButtonX",
                Translate(HeadSwitchPro + body).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
        }

        [Fact]
        public void DpadMembers_InSwitchesGroups_Resolve()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("dpad_east", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            Assert.Equal("Gamepad DPadRight",
                Translate(vdf).KbmMappingSet.Rows.Single().Sources.Single().Descriptor);
        }

        // ─── Seat folds ─────────────────────────────────────────────────

        [Fact]
        public void FourButtons_OnTheDpad_FoldOntoSeats()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press 1"), Inp("button_y", "key_press 2")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var descs = p.KbmMappingSet.Rows.SelectMany(r => r.Sources)
                .Select(s => s.Descriptor).OrderBy(d => d).ToArray();
            Assert.Equal(new[] { "Gamepad DPadDown", "Gamepad DPadUp" }, descs);
            AssertNoUnknownInput(p);
        }

        [Fact]
        public void DpadMode_OnTheDiamond_FoldsOntoSeatButtons()
        {
            string vdf = Head
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "key_press 1"), Inp("dpad_west", "key_press 2")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var descs = p.KbmMappingSet.Rows.SelectMany(r => r.Sources)
                .Select(s => s.Descriptor).OrderBy(d => d).ToArray();
            Assert.Equal(new[] { "Gamepad ButtonX", "Gamepad ButtonY" }, descs);
            AssertNoUnknownInput(p);
        }

        // ─── doubletap gesture member ───────────────────────────────────

        [Fact]
        public void DoubleTapMember_ReadsThePadTapGesture()
        {
            string vdf = Head
                + Group(1, "absolute_mouse", Inputs(Inp("doubletap", "key_press SPACE")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var src = p.KbmMappingSet.Rows
                .SelectMany(r => r.Sources)
                .Single(s => s.Descriptor.Contains("DoubleTap"));
            Assert.Equal("Touchpad 1 DoubleTap", src.Descriptor);
            AssertNoUnknownInput(p);
        }

        // ─── always_on_action ───────────────────────────────────────────

        [Fact]
        public void AlwaysOn_RowShapedBinding_RidesTheConstantRead()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("always_on_action", "xinput_button TRIGGER_LEFT")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = p.XboxMappingSet.Rows.Single(r => r.Target == "LeftTrigger");
            Assert.Equal("Always On", row.Sources.Single().Descriptor);
            Assert.Equal("Base", string.IsNullOrEmpty(row.LayerMask) ? "Base" : row.LayerMask);
            AssertNoUnknownInput(p);
        }

        [Fact]
        public void AlwaysOn_MacroShapedBinding_OnALayer_CarriesTheLayerGate()
        {
            // A set-scoped LED: the macro must fire at set ENTRY, so it
            // carries the hosting layer as its gate.
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("button_menu", "controller_action add_layer 2 0 0")))
                + Group(2, "switches", Inputs(Inp("always_on_action", "controller_action set_led 255 0 0 100 100 1")))
                + Preset(0, "Default", (1, "switch active"))
                + Preset(1, "Combat", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var led = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.SetLightbarColor);
            Assert.Equal("Layer_42_1", led.LayerMask);
            Assert.Contains("Always On", led.TriggerInputDescriptors);
        }

        [Fact]
        public void AlwaysOn_MacroOnlyLayer_KeepsItsActivator()
        {
            // A set whose ONLY content is an always-on one-shot must stay
            // reachable: layer-gated macros count as layer content for the
            // ShiftLayerEmpty check.
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("button_menu", "controller_action add_layer 2 0 0")))
                + Group(2, "switches", Inputs(Inp("always_on_action", "controller_action set_led 0 255 0 100 100 1")))
                + Preset(0, "Default", (1, "switch active"))
                + Preset(1, "LedOnly", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.XboxMappingSet.ShiftActivators, a => a.LayerMask == "Layer_42_1");
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ShiftLayerEmpty);
        }

        // ─── Double_Press layer verbs (ShiftActivator.DoublePressMs) ────

        [Fact]
        public void DoublePress_ChangePreset_LowersWithTheDoublePressGate()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_b", "controller_action CHANGE_PRESET 2 0 0", activator: "Double_Press")))
                + Group(2, "switches", Inputs(Inp("button_menu", "key_press E")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.XboxMappingSet.ShiftActivators
                .Concat(p.KbmMappingSet.ShiftActivators)
                .Where(a => a.DoublePressMs > 0).GroupBy(a => a.LayerMask).Select(g => g.First()));
            Assert.Equal(442, act.DoublePressMs); // Valve's shipped default window
            Assert.Equal("Gamepad ButtonB", act.Descriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorInputNotSupported);
        }

        [Fact]
        public void DoublePress_ModeShift_CarriesTheAuthoredWindow()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_menu", "mode_shift joystick 2", activator: "Double_Press",
                        activatorSettings: ActSettings(("double_tap_time", "300")))))
                + Group(2, "joystick_move", Inputs(Inp("click", "key_press E")))
                + Preset(0, "Default", (1, "switch active"), (2, "joystick active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = p.KbmMappingSet.ShiftActivators
                .Concat(p.XboxMappingSet.ShiftActivators)
                .First(a => a.DoublePressMs > 0);
            Assert.Equal(300, act.DoublePressMs);
            Assert.Equal("Hold", act.Mode);
        }

        // ─── gyro_button_invert 2 = Toggle ──────────────────────────────

        [Fact]
        public void GyroButtonInvertTwo_StampsTheToggleArm()
        {
            string vdf = Head
                + Group(1, "gyro_to_mouse",
                    Inputs()
                    + Settings(("gyro_button", "3"), ("gyro_button_invert", "2")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad LeftShoulder", p.GyroEngageDescriptor); // bit 3 = left bumper
            Assert.True(p.GyroEngageToggle);
            Assert.False(p.GyroEngageInvert);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        // ─── Ratchet enum: single-pad center bits + macro buttons ───────

        [Fact]
        public void RatchetCenterPadBit_GroundsOnSinglePadTypes()
        {
            // 2^27 = CapSenseCenterTouchPad; controller_ps5_edge is a
            // single-pad type (SDL_hidapi_ps5.c registers one touchpad).
            string vdf = HeadPs5Edge
                + Group(1, "gyro_to_mouse",
                    Inputs()
                    + Settings(("gyro_ratchet_button_mask", "134217728")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "Touchpad 0 Finger 0 Down" }, p.GyroRatchetDescriptors);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void GyroButton_MacroBit_GroundsOnTheV24MacroButtons()
        {
            // gyro_button 33 = ButtonMacro1 = SDL misc3 = raw Button 18.
            string vdf = Head
                + Group(1, "gyro_to_mouse",
                    Inputs()
                    + Settings(("gyro_button", "33")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Button 18", p.GyroEngageDescriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }
    }
}
