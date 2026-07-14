using System.Linq;
using PadForge.Engine;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v3 (Wave 2A) edge tests: Long_Press key/button
    /// macros on the HoldForMs trigger, xinput hold_repeats turbo, the
    /// activator toggle setting, camera_reset, and mouse_region. Corpus
    /// coverage rides the goldens (875948877 carries the Long_Press +
    /// hold_repeats xinput composite, 2774979654 the xinput toggle,
    /// 2795727040 / 3456927474 active mouse_region groups); these tests pin
    /// the branches the corpus misses, camera_reset above all (no public
    /// config carrying it was found in a 24-config sample, and Valve's own
    /// templates host it under Double_Press, which stays skipped).</summary>
    public class WaveTwoATranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
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

        // ─── Long_Press xinput: hold-until-release ──────────────────────

        [Fact]
        public void LongPress_XInput_EmitsHoldVcButtonMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button Y", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "300")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldVcButton, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(300, m.TriggerHoldMs);
            Assert.Equal(Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(Gamepad.Y, m.TargetXboxButtons);
            // Interruptable-pause approximation: the trigger's own identity
            // is consumed while the long press is active.
            Assert.True(m.ConsumeTrigger);
            Assert.True(p.NeedsXboxSlot);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
            Assert.Equal(TranslationReasons.MacroTriggerViaXboxOutput, entry.ReasonKey);
        }

        [Fact]
        public void LongPress_XInput_TriggerAxisTarget_StaysSkipped()
        {
            // No button-hold primitive reaches LT/RT pulls.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button TRIGGER_LEFT", activator: "Long_Press")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.LongPressNotSupported, entry.ReasonKey);
        }

        [Fact]
        public void LongPress_XInput_WithHoldRepeats_ComposesHoldThresholdTurbo()
        {
            // The 875948877 shape: trigger-click Long_Press + hold_repeats
            // xinput A at repeat_rate 10 ms.
            string vdf = Head
                + Group(1, "trigger", Inputs(
                    Inp("click", "xinput_button A", activator: "Long_Press",
                        activatorSettings: ActSettings(
                            ("hold_repeats", "1"), ("repeat_rate", "10")))))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatVcButtonWhileHeld, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(500, m.TriggerHoldMs); // Steam UI default
            Assert.Equal(Gamepad.A, m.TargetXboxButtons);
            Assert.Equal(10, m.IntervalMs);
            // Trigger-click host rides the axis trigger, not a button bit.
            Assert.Equal(0, (int)m.TriggerXboxButtons);
            Assert.Equal("LeftTrigger", m.TriggerAxisTarget);
            Assert.Equal(75, m.TriggerAxisThresholdPercent);
        }

        [Fact]
        public void LongPress_Key_WithHoldRepeats_AutofiresFromThreshold()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_b", "key_press SPACE", activator: "Long_Press",
                        activatorSettings: ActSettings(
                            ("long_press_time", "250"), ("hold_repeats", "1"), ("repeat_rate", "50")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatKeyWhileHeld, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(250, m.TriggerHoldMs);
            Assert.Equal(0x20, m.VirtualKey); // VK_SPACE
            Assert.Equal(50, m.IntervalMs);
        }

        [Fact]
        public void LongPress_TouchpadHosted_HasNoDeviceFreeTrigger()
        {
            string vdf = Head
                + Group(1, "single_button", Inputs(
                    Inp("touch", "xinput_button A", activator: "Long_Press")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.NoDeviceFreeTrigger, entry.ReasonKey);
        }

        // ─── hold_repeats turbo on Full_Press xinput ────────────────────

        [Fact]
        public void HoldRepeats_XInput_Divergent_EmitsTurboMacro_NoRow()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button B",
                        activatorSettings: ActSettings(("hold_repeats", "1"), ("repeat_rate", "125")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.XboxMappingSet.Rows); // the macro replaces the row
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatVcButtonWhileHeld, m.Action);
            Assert.Equal("WhileHeld", m.TriggerMode);
            Assert.Equal(0, m.TriggerHoldMs);
            Assert.Equal(Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(Gamepad.B, m.TargetXboxButtons);
            Assert.Equal(125, m.IntervalMs);
            Assert.False(m.ConsumeTrigger);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.MacroTriggerViaXboxOutput, entry.ReasonKey);
        }

        [Fact]
        public void HoldRepeats_XInput_Identity_KeepsRowAndRepeatDroppedNote()
        {
            // Identity turbo cannot pulse: the identity row that feeds the
            // combined-output trigger would hold the pulsed bit solid, so
            // the Wave-1 row + named note stays.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button A",
                        activatorSettings: ActSettings(("hold_repeats", "1"), ("repeat_rate", "99")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonA", row.Target);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.RepeatDropped);
        }

        [Fact]
        public void HoldRepeats_DormantDefault_StaysARow()
        {
            // repeat_rate alone (the 99 ms slider default, 113 corpus
            // occurrences) is turbo-off; nothing changes.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button B",
                        activatorSettings: ActSettings(("repeat_rate", "99")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonB", row.Target);
        }

        // ─── The activator toggle setting ───────────────────────────────

        [Fact]
        public void Toggle_XInput_KeepsRow_LatchFiresOnTargetBit()
        {
            // The 2774979654 shape: stick click latches B. Macro-only would
            // be a dead letter (the host input carries no other binding, so
            // nothing would feed a source-identity trigger); the momentary
            // row stays as the trigger's feed and the latch fires on the
            // TARGET bit's press edge.
            string vdf = Head
                + Group(1, "joystick_move", Inputs(
                    Inp("click", "xinput_button B",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "ButtonB");
            Assert.Equal("Gamepad LeftStick", row.Sources[0].Descriptor);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ToggleVcButton, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal(Gamepad.B, m.TriggerXboxButtons); // the target, fed by the row
            Assert.Equal(Gamepad.B, m.TargetXboxButtons);
            Assert.False(m.ConsumeTrigger);
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ToggleLatchEmitted);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
        }

        [Fact]
        public void Toggle_XInput_Identity_SameUnifiedStructure()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button A",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonA", row.Target);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ToggleVcButton, m.Action);
            Assert.Equal(Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(Gamepad.A, m.TargetXboxButtons);
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ToggleLatchEmitted);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
        }

        [Fact]
        public void Toggle_Key_MacroOnly_NoKbmRow()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_x", "key_press LEFT_SHIFT",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ToggleKey, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal(Gamepad.X, m.TriggerXboxButtons);
            Assert.Equal(0xA0, m.VirtualKey); // VK_LSHIFT
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ToggleLatchEmitted);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
        }

        [Fact]
        public void Toggle_TouchpadHosted_KeepsMomentaryRow_WithNamedDrop()
        {
            // No device-free trigger for a latch yet (the sibling wave's
            // device-free triggers will lift this): the Wave-1 momentary row
            // stays so the binding keeps working, and the toggle drop gets a
            // named Partial instead of the old silence.
            string vdf = Head
                + Group(1, "single_button", Inputs(
                    Inp("click", "key_press E",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target); // VK_E
            Assert.Equal("Touchpad 1 Click", row.Sources[0].Descriptor);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ToggleDropped);
        }

        [Fact]
        public void Toggle_MouseButton_KeepsRow_WithNamedDrop()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_y", "mouse_button LEFT",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Single(p.KbmMappingSet.Rows);
            Assert.Empty(p.Macros);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ToggleDropped);
        }

        [Fact]
        public void Toggle_ModeShift_LatchesTheShiftLayer()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode);
        }

        [Fact]
        public void Toggle_HoldLayer_BecomesToggleActivator()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action HOLD_LAYER 2",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode);
        }

        // ─── camera_reset ───────────────────────────────────────────────

        [Fact]
        public void CameraReset_EmitsGyroRecenterMacro_Partial()
        {
            // Valve's shipped templates: "camera_reset 180 66 90" (the
            // numeric args calibrate Steam's dots-per-360 camera surgery).
            string vdf = Head
                + Group(1, "joystick_move", Inputs(
                    Inp("click", "controller_action camera_reset 180 66 90")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.GyroRecenter, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal(Gamepad.RIGHT_THUMB, m.TriggerXboxButtons);
            Assert.False(m.ConsumeTrigger);
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.CameraResetApproximated);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
        }

        [Fact]
        public void CameraReset_UnderLongPress_RidesHoldThreshold()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action camera_reset 180 66 90",
                        activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "400")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.GyroRecenter, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(400, m.TriggerHoldMs);
        }

        [Fact]
        public void CameraReset_TouchpadHosted_SkipsNoDeviceFreeTrigger()
        {
            string vdf = Head
                + Group(1, "single_button", Inputs(
                    Inp("click", "controller_action camera_reset 180 66 90")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.NoDeviceFreeTrigger, entry.ReasonKey);
        }

        // ─── mouse_region ───────────────────────────────────────────────

        [Fact]
        public void MouseRegion_TrackpadHost_SkipsClampButKeepsMembers()
        {
            // Trackpad touch has no device-free trigger yet, so the clamp
            // macro is a named skip; the click member still translates.
            string vdf = Head
                + Group(1, "mouse_region",
                    Inputs(Inp("click", "mouse_button LEFT"))
                    + Settings(("scale", "10"), ("position_x", "9"), ("position_y", "10")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            Assert.Contains(p.Report.Entries, e =>
                e.Status == TranslationStatus.Skipped
                && e.ReasonKey == TranslationReasons.NoDeviceFreeTrigger);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmMBtn0", row.Target);
        }

        [Fact]
        public void MouseRegion_TriggerHost_EmitsWhileHeldClampMacro()
        {
            // A trigger-hosted region engages on the pull (the full-pull
            // click read is a device-free axis trigger).
            string vdf = Head
                + Group(1, "mouse_region",
                    "\t\t\"inputs\"\n\t\t{\n\t\t}\n"
                    + Settings(("scale", "40"), ("position_x", "25"), ("position_y", "75")))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseLimitRegion, m.Action);
            Assert.Equal("WhileHeld", m.TriggerMode);
            Assert.Equal("LeftTrigger", m.TriggerAxisTarget);
            Assert.Equal(75, m.TriggerAxisThresholdPercent);
            Assert.Equal(40, m.RegionScalePercent);
            Assert.Equal(25, m.RegionXPercent);
            Assert.Equal(75, m.RegionYPercent);
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.MouseRegionApproximated);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
            Assert.Equal(new[] { "40", "25", "75" }, entry.ReasonArgs.ToArray());
        }

        [Fact]
        public void MouseRegion_SensitivityScales_GetTheNamedCurveDrop()
        {
            string vdf = Head
                + Group(1, "mouse_region",
                    "\t\t\"inputs\"\n\t\t{\n\t\t}\n"
                    + Settings(("sensitivity_horiz_scale", "110"), ("sensitivity_vert_scale", "70")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported
                && e.ReasonArgs.Count == 1
                && e.ReasonArgs[0].Contains("sensitivity_horiz_scale"));
        }

        [Fact]
        public void MouseRegion_DefaultsCenterFullScreen()
        {
            string vdf = Head
                + Group(1, "mouse_region", "\t\t\"inputs\"\n\t\t{\n\t\t}\n")
                + Preset(0, "Default", (1, "right_trigger active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(100, m.RegionScalePercent);
            Assert.Equal(50, m.RegionXPercent);
            Assert.Equal(50, m.RegionYPercent);
            Assert.Equal("RightTrigger", m.TriggerAxisTarget);
        }
    }
}
