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
                e => e.ReasonKey == "Workshop_Tr_ScrollWheelModeNotSupported");
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
                e.ReasonKey == "Workshop_Tr_ScrollWheelModeNotSupported");
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
        public void DelayStart_OnRowBinding_ReroutesOntoTheHoldPair()
        {
            // v18: rows have no delay channel, so a delayed press reroutes
            // onto the HoldKey pair carrying the Delay step.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E",
                        activatorSettings: ActSettings(("delay_start", "120")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var hold = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.HoldKey);
            Assert.Equal(120, hold.DelayStartMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ActivatorDelayDropped);
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
            // v17: no note. "Approximated as a PrintScreen key tap" is
            // exactly what a user expects the action to do.
            Assert.Empty(p.Report.Entries);
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
            // v17: silent, the SCREENSHOT ruling.
            Assert.Empty(p.Report.Entries);
        }

        [Fact]
        public void SystemKey1_ReleaseActivator_BecomesPrintScreenTap()
        {
            // The corpus shape: every system_key_1 occurrence (fixtures and
            // Valve's shipped controller_base configs alike) rides
            // button_capture Release, authors restoring the Capture
            // button's native screenshot behavior. v20 lowers it exactly
            // like SCREENSHOT, honoring the hosting activator.
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_capture", "controller_action system_key_1",
                        activator: "Release")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal(0x2C, m.VirtualKey); // VK_SNAPSHOT
            Assert.Equal("OnRelease", m.TriggerMode);
            // Silent, the SCREENSHOT ruling. The SteamSystemAction note no
            // longer fires for system_key_1.
            Assert.Empty(p.Report.Entries);
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
        }

        [Fact]
        public void DoublePress_ButtonHost_BuildsHoldKeyMacro_OnTheDoublePressTrigger()
        {
            // v17: the button-hosted Double_Press lowers to a HoldKey macro
            // on the engine's DoublePress trigger (Valve's "if held on the
            // second press, it will remain pressed"), window at the 442 ms
            // template-grounded default when unauthored.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E", activator: "Double_Press")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldKey, m.Action);
            Assert.Equal("DoublePress", m.TriggerMode);
            Assert.Equal(442, m.TriggerDoublePressMs);
            Assert.Equal(0x45, m.VirtualKey); // VK_E
        }

        [Fact]
        public void DoublePress_AuthoredWindow_AndVcTarget_CarryThrough()
        {
            // The activator's double_tap_time (the serializer's own token,
            // authored 442 in Valve's basicui templates) rides the emitted
            // macro; an xinput button takes the HoldVcButton shape.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button Y", activator: "Double_Press",
                        activatorSettings: ActSettings(("double_tap_time", "300")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldVcButton, m.Action);
            Assert.Equal("DoublePress", m.TriggerMode);
            Assert.Equal(300, m.TriggerDoublePressMs);
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

        // ─── v16: the degenerate whole-pad Outer Ring ───────────────────

        private static string GroupSettings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

        [Fact]
        public void MouseRegion_WholePadEdge_ResolvesToTheTouchRead()
        {
            // Inverted ring at the 32767 ceiling = anywhere on the pad
            // (the corpus 3456927474 shape): the edge member IS the touch
            // read, so its mouse_delta binding lands as a nudge macro and
            // the two consumed geometry keys leave the tuning note.
            string vdf = Head
                + Group(1, "mouse_region",
                    Inputs(Inp("edge", "controller_action mouse_delta 100 0"))
                    + GroupSettings(
                        ("edge_binding_radius", "32767"),
                        ("edge_binding_invert", "1")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var nudge = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseNudge, nudge.Action);
            Assert.Equal(100, nudge.DeltaX);
            Assert.Equal(0, nudge.DeltaY);
            Assert.Equal("Touchpad 1 Finger 0 Down", Assert.Single(nudge.TriggerInputDescriptors));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionTuningDropped);
        }

        [Fact]
        public void MouseRegion_PartialRingEdge_BuildsOnTheTouchRead_NamingTheGeometry()
        {
            // v17: a real ring (radius below the ceiling) approximates onto
            // the touch read, so the binding fires on any touch. The
            // dropped ring geometry stays named in the region tuning note.
            string vdf = Head
                + Group(1, "mouse_region",
                    Inputs(Inp("edge", "key_press E"))
                    + GroupSettings(
                        ("edge_binding_radius", "20000"),
                        ("edge_binding_invert", "1")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            Assert.Equal("Touchpad 1 Finger 0 Down", Assert.Single(row.Sources).Descriptor);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionTuningDropped
                && e.ReasonArgs.Single().Contains("edge_binding_radius"));
        }

        [Fact]
        public void StickHostedEdge_BuildsOnTheRingRead()
        {
            // v17: the stick edge lowers onto the deflection-magnitude ring
            // family. The inverted full-radius zone of corpus 3456927474
            // ("middle mouse while the stick is deflected at all") becomes
            // an inner ring at 100 percent. The engine's rest floor keeps
            // the centered stick silent.
            string vdf = Head
                + Group(1, "joystick_mouse",
                    Inputs(Inp("edge", "mouse_button MIDDLE"))
                    + GroupSettings(
                        ("edge_binding_radius", "32767"),
                        ("edge_binding_invert", "1")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);
            // The joystick_mouse group also emits its own mouse rows; the
            // edge member's row is the ring-sourced one.
            var row = Assert.Single(p.KbmMappingSet.Rows,
                r => r.Sources.Any(s => s.Descriptor == "Gamepad RightStickRing"));
            var src = Assert.Single(row.Sources);
            Assert.True(src.Invert);
            Assert.Equal(100, src.DeadZone);
        }

        [Fact]
        public void StickHostedEdge_PartialInnerRing_CarriesTheRadiusPercent()
        {
            // The 789818086 walk-modifier shape: dpad on the left stick,
            // RCtrl while deflected but inside 74.6 percent of full
            // deflection (24432/32767). The ring source carries the radius
            // as its DeadZone percent.
            string vdf = Head
                + Group(1, "dpad",
                    Inputs(Inp("edge", "key_press RIGHT_CONTROL"))
                    + GroupSettings(
                        ("edge_binding_radius", "24432"),
                        ("edge_binding_invert", "1")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKeyA3", row.Target); // VK 0xA3, Right Ctrl
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftStickRing", src.Descriptor);
            Assert.True(src.Invert);
            Assert.Equal(75, src.DeadZone);
        }

        [Fact]
        public void StickHostedEdge_OuterRingDefaultRadius_UsesTheSerializerDefault()
        {
            // No authored radius: the untouched-slider default the
            // serializer writes across the corpus and Valve's templates
            // (24995..24999) grounds the 76 percent outer ring.
            string vdf = Head
                + Group(1, "dpad", Inputs(Inp("edge", "key_press E")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftStickRing", src.Descriptor);
            Assert.False(src.Invert);
            Assert.Equal(76, src.DeadZone);
        }
    }
}
