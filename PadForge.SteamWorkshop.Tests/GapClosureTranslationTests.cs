using System.Linq;
using PadForge.Engine;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v10 (gap closure G1-G15) contracts the corpus
    /// goldens don't pin: activator haptics as rumble pulses, the
    /// button_capture raw source, swipe-lowered 2dscroll, drag-lowered
    /// scrollwheel, activator fire delays as macro Delay steps, release
    /// taps, the SCREENSHOT / SHOW_KEYBOARD approximations, the
    /// REMOVE_LAYER return cycle, the Long_Press upgrades, any-VK HoldKey
    /// macros, trackpad Double_Press, identity turbo, and hotbar menus.</summary>
    public class GapClosureTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Gap\"\n";

        private static string Group(int id, string mode, string body = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{body}\t}}\n";

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

        // ─── G1: activator haptics ──────────────────────────────────────

        [Fact]
        public void HapticPulse_LongPressActivator_FiresAtThreshold()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button B", activator: "Long_Press",
                        activatorSettings: ActSettings(
                            ("long_press_time", "300"), ("haptic_intensity", "3")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var pulse = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.RumblePulse);
            Assert.Equal("HoldForMs", pulse.TriggerMode);
            Assert.Equal(300, pulse.TriggerHoldMs);
            Assert.Equal(100, pulse.RumbleStrengthPercent);
        }

        // ─── G2: button_capture ─────────────────────────────────────────

        [Fact]
        public void ButtonCapture_ResolvesToRawButton11()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("button_capture", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target);
            Assert.Equal("Button 11", Assert.Single(row.Sources).Descriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        // ─── G3: 2dscroll on a trackpad ─────────────────────────────────

        [Fact]
        public void TwoDScroll_TrackpadHost_LowersOntoSwipes()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_west", "key_press F5"),
                    Inp("dpad_east", "key_press F9")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var f5 = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey74"); // F5
            var f9 = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey78"); // F9
            Assert.Equal("Touchpad 1 SwipeLeft", Assert.Single(f5.Sources).Descriptor);
            Assert.Equal("Touchpad 1 SwipeRight", Assert.Single(f9.Sources).Descriptor);
            // Swipe fires self-arm at apply (v14): the rows are Clean and
            // no feature note remains anywhere in the report.
            Assert.All(p.Report.Entries, e =>
                Assert.Equal(TranslationStatus.Clean, e.Status));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == "Workshop_Tr_TrackpadFeatureRequired");
        }

        // ─── G4: scrollwheel on a trackpad ──────────────────────────────

        [Fact]
        public void ScrollWheel_TrackpadHost_LowersOntoFingerDrag()
        {
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_DOWN"),
                    Inp("scroll_counterclockwise", "mouse_wheel SCROLL_UP"),
                    Inp("click", "mouse_button MIDDLE")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            // clockwise + SCROLL_DOWN: drag down scrolls down (no invert).
            // counterclockwise + SCROLL_UP names the SAME drag-to-wheel
            // map, so the twin folds into one source (two would Sum to
            // double rate).
            var scroll = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmScroll");
            var drag = Assert.Single(scroll.Sources);
            Assert.Equal("Touchpad 0 Finger 0 Y", drag.Descriptor);
            Assert.False(drag.Invert);

            var click = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMBtn2");
            Assert.Equal("Touchpad 0 Click", Assert.Single(click.Sources).Descriptor);

            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelApproximated
                && e.Status == TranslationStatus.Partial);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ScrollWheelModeNotSupported);
        }

        [Fact]
        public void ScrollWheel_ReversedDirections_InvertTheDrag()
        {
            // clockwise bound to SCROLL_UP (corpus 708227783's zoom shape):
            // wheel invert with no member flip = inverted read.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_UP")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var scroll = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmScroll");
            Assert.True(Assert.Single(scroll.Sources).Invert);
        }

        [Fact]
        public void ScrollWheel_KeyBindingOnScrollMember_TapsOnTheSwipeGesture()
        {
            // v15: a key on a wheel detent rides the one-shot swipe walk
            // (clockwise = SwipeDown), one tap per swipe.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "key_press KEYPAD_PLUS")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var tap = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, tap.Action);
            Assert.Equal("Touchpad 0 SwipeDown", Assert.Single(tap.TriggerInputDescriptors));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelModeNotSupported);
        }

        // ─── G5: activator fire delays ──────────────────────────────────

        [Fact]
        public void DelayEnd_OnReleaseKeyTap_BecomesDelayStep()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E", activator: "release",
                        activatorSettings: ActSettings(("delay_end", "250")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal(250, m.DelayEndMs);
            Assert.Equal(0, m.DelayStartMs);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorDelayDropped);
        }

        [Fact]
        public void DelayStart_OnRowBinding_KeepsDroppedNote()
        {
            // Rows have no delay channel; the note stays for them.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E",
                        activatorSettings: ActSettings(("delay_start", "120")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ActivatorDelayDropped);
            Assert.Equal("delay_start 120", Assert.Single(entry.ReasonArgs));
        }

        [Fact]
        public void Delays_OnLongPressKey_RideTheHoldPair()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press F", activator: "Long_Press",
                        activatorSettings: ActSettings(
                            ("long_press_time", "300"),
                            ("delay_start", "80"), ("delay_end", "160")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros, x => x.Action == TranslatedMacroAction.HoldKey);
            Assert.Equal(80, m.DelayStartMs);
            Assert.Equal(160, m.DelayEndMs);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorDelayDropped);
        }

        // ─── G6: release taps ───────────────────────────────────────────

        [Fact]
        public void ReleaseActivator_OnXInput_EmitsVcTapMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button B", activator: "release")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.VcButtonTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal(Gamepad.B, m.TargetXboxButtons);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
        }

        [Fact]
        public void ReleaseActivator_OnTriggerAxisTarget_TapsTheAxisHold()
        {
            // v15: one full-pull AxisHold tap on the release edge.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button TRIGGER_LEFT", activator: "release")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.VcAxisTap, m.Action);
            Assert.Equal("OnRelease", m.TriggerMode);
            Assert.Equal("LeftTrigger", m.TargetAxis);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
        }

        // ─── G7: SCREENSHOT / SHOW_KEYBOARD ─────────────────────────────

        [Fact]
        public void Screenshot_BecomesPrintScreenTap()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action SCREENSHOT")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal(0x2C, m.VirtualKey); // VK_SNAPSHOT
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
            Assert.Equal(TranslationReasons.ScreenshotApproximated, entry.ReasonKey);
        }

        [Fact]
        public void ShowKeyboard_BecomesOnScreenKeyboardLaunch()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action SHOW_KEYBOARD")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ShowOnScreenKeyboard, m.Action);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.ShowKeyboardApproximated, entry.ReasonKey);
        }

        [Fact]
        public void SystemKey1_KeepsSteamSystemActionSkip()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action SYSTEM_KEY_1")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.SteamSystemAction, entry.ReasonKey);
        }

        // ─── G8: REMOVE_LAYER ───────────────────────────────────────────

        [Fact]
        public void RemoveLayer_HostedInItsOwnLayer_BecomesReturnCycle()
        {
            // The corpus shape: a "back" binding inside the layer it
            // removes (remove_layer 2 = the second preset in id order).
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "key_press E")))
                + Group(2, "four_buttons", Inputs(
                    Inp("button_a", "controller_action remove_layer 2 1 1"),
                    Inp("button_b", "key_press F")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Cycle", act.Mode);
            Assert.Equal("Layer_42_1", act.LayerMask);
            Assert.Equal("Layer_42_1", act.CycleLayers);
            Assert.True(act.CycleIncludeBase);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RemoveLayerApproximated);
        }

        [Fact]
        public void RemoveLayer_TargetingAnotherLayer_StaysNoteOnly()
        {
            // remove_layer aimed at a layer the binding is NOT hosted in
            // has no return-cycle shape; the note-only Partial stays.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action remove_layer 2 1 1")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press F")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.ShiftActivators);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RemoveLayerApproximated);
        }

        // ─── G10: Long_Press legs ───────────────────────────────────────

        [Fact]
        public void LongPress_MouseButton_BecomesHoldMouseButtonPair()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mouse_button RIGHT", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "400")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldMouseButton, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(400, m.TriggerHoldMs);
            Assert.Equal(1, m.MouseButtonIndex); // RIGHT
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.LongPressNotSupported);
        }

        [Fact]
        public void LongPress_ChangePreset_CarriesDelayMs()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action CHANGE_PRESET 2 1 1", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "350")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press E")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Custom", act.Mode);
            Assert.Equal(350, act.DelayMs);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.LongPressNotSupported);
        }

        [Fact]
        public void LongPress_SetLed_FiresAtThreshold()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action set_led 255 0 0 100 100 1", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "600")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.SetLightbarColor, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(600, m.TriggerHoldMs);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.LongPressNotSupported);
        }

        // ─── G11: any-VK keys ───────────────────────────────────────────

        [Fact]
        public void UnsupportedKey_OnRelease_RidesTheKeyTapMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press NUM_LOCK", activator: "release")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal(0x90, m.VirtualKey); // VK_NUMLOCK
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnsupportedKey);
        }

        [Fact]
        public void UnsupportedKey_LongPress_RidesTheHoldKeyMacro()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press F14", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "300")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldKey, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(0x7D, m.VirtualKey); // VK_F14
        }

        // ─── G13: Double_Press on trackpads ─────────────────────────────

        [Fact]
        public void DoublePress_TrackpadHost_ReadsDoubleTapGesture()
        {
            string vdf = Head
                + Group(1, "single_button", Inputs(
                    Inp("click", "key_press E", activator: "Double_Press")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target);
            Assert.Equal("Touchpad 1 DoubleTap", Assert.Single(row.Sources).Descriptor);
            // The tap-gesture family self-arms at apply (v14): Clean row,
            // no feature note.
            Assert.All(p.Report.Entries, e =>
                Assert.Equal(TranslationStatus.Clean, e.Status));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.DoublePressNotSupported);
        }

        [Fact]
        public void DoublePress_ButtonHost_KeepsNamedSkip()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E", activator: "Double_Press")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.DoublePressNotSupported);
        }

        // ─── G15: hotbar + empty_binding ────────────────────────────────

        [Fact]
        public void Hotbar_LowersAsGridMenu()
        {
            string vdf = Head
                + Group(1, "hotbar", Inputs(
                    Inp("touch_menu_button_0", "key_press F5"),
                    Inp("touch_menu_button_1", "key_press F9")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var menu = Assert.Single(p.Menus);
            Assert.Equal(PadForge.Engine.Menus.MenuKind.Grid, menu.Kind);
            Assert.Equal(2, menu.Items.Count);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        [Fact]
        public void EmptyBinding_IsSilent()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action empty_binding")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Report.Entries);
        }
    }
}
