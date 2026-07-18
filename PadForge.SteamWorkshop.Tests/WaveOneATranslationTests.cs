using System.Linq;
using PadForge.Engine;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v2 (Wave 1a) edge tests: single_button,
    /// gyro_to_mouse, digital-trigger switches members, #token titles,
    /// inner deadzone, the named-skip vocabulary, Long_Press layer carries,
    /// and set_led. Happy-path corpus coverage is pinned by the golden
    /// fixtures (1451857916, 2494749393, 1172518660, 3725174032,
    /// 3353604014); these tests pin the branches the corpus misses.</summary>
    public class WaveOneATranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42,
            string preferredLanguage = "english")
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
                PreferredLanguage = preferredLanguage,
            });
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

        // ─── B-3 single_button ──────────────────────────────────────────

        [Fact]
        public void SingleButton_TrackpadClickAndTouch_BothTranslate()
        {
            string vdf = Head
                + Group(1, "single_button", Inputs(
                    Inp("click", "xinput_button SELECT"),
                    Inp("touch", "key_press E")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var back = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonBack", back.Target);
            Assert.Equal("Touchpad 0 Click", Assert.Single(back.Sources).Descriptor);

            var key = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", key.Target); // E
            Assert.Equal("Touchpad 0 Finger 0 Down", Assert.Single(key.Sources).Descriptor);

            Assert.All(p.Report.Entries, e => Assert.Equal(TranslationStatus.Clean, e.Status));
        }

        [Fact]
        public void SingleButton_UnresolvableMember_SkipsWithReason()
        {
            // "touch" has no source on a stick; the member reports instead
            // of silently vanishing (the old behavior skipped the whole
            // group as UnknownGroupMode).
            string vdf = Head
                + Group(1, "single_button", Inputs(Inp("touch", "key_press E")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.UnknownPhysicalInput, entry.ReasonKey);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        // ─── B-4 gyro_to_mouse ──────────────────────────────────────────

        [Fact]
        public void GyroToMouse_EmitsGyroMouseAxes_WithNaturalSensitivity()
        {
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_natural_sensitivity", "75")))
                + Group(2, "four_buttons", Inputs(Inp("button_a", "key_press E")))
                + Preset(0, "Default", (1, "gyro active"), (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var x = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX");
            var y = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseY");
            Assert.Equal("Gyro Yaw", Assert.Single(x.Sources).Descriptor);
            Assert.Equal("Gyro Pitch", Assert.Single(y.Sources).Descriptor);
            // gyro_natural_sensitivity stores percent of natural 1:1.
            Assert.Equal(0.75, x.Sources[0].GyroSensitivity, 3);
            Assert.Equal(0.75, y.Sources[0].GyroSensitivity, 3);
        }

        [Fact]
        public void GyroToMouse_NoSettings_DefaultsToUnitSensitivity()
        {
            // Valve's shipped gyro templates carry no settings at all.
            string vdf = Head
                + Group(1, "gyro_to_mouse", "")
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var x = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX");
            Assert.Equal(1.0, Assert.Single(x.Sources).GyroSensitivity, 3);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        [Fact]
        public void GyroToMouse_RatchetMaskZero_NoDropNote()
        {
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask", "0")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        // ─── B-5 digital trigger members on switches ────────────────────

        [Fact]
        public void SwitchesTrigger_MouseButton_EmitsFullPullRow()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("left_trigger", "mouse_button LEFT")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmMBtn0", row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftTrigger", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(75, src.DeadZone); // end-of-travel click
        }

        [Fact]
        public void SwitchesTrigger_MatchingXInput_IsIdentityRow()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("right_trigger", "xinput_button TRIGGER_RIGHT")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("RightTrigger", row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad RightTrigger", src.Descriptor);
            Assert.True(src.HalfAxis);
        }

        [Fact]
        public void SwitchesTrigger_ModeShiftCarrier_EmitsHoldActivator()
        {
            // 770509247's shape: a left_trigger switch member carrying the
            // right-pad mode shift.
            string vdf = Head
                + Group(1, "switches", Inputs(Inp("left_trigger", "mode_shift right_trackpad 2")))
                + Group(2, "dpad", Inputs(Inp("dpad_north", "key_press Q")))
                + Preset(0, "Default", (1, "switch active"), (2, "right_trackpad active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Gamepad LeftTrigger", act.Descriptor);
            Assert.Equal("Hold", act.Mode);
            // Button kind: the button-like read thresholds the pull.
            Assert.Equal("Button", act.Kind);
        }

        [Fact]
        public void SwitchesTriggerIdentity_AbsorbsBehindMatchedAnalogPull()
        {
            // A trigger group's matched analog passthrough plus a switches
            // left_trigger identity: one row, analog source first, click
            // leg behind it, max-abs combine (never Sum).
            string vdf = Head
                + Group(1, "trigger", Inputs(Inp("click", "xinput_button TRIGGER_LEFT")))
                + Group(2, "switches", Inputs(Inp("left_trigger", "xinput_button TRIGGER_LEFT")))
                + Preset(0, "Default", (1, "left_trigger active"), (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("LeftTrigger", row.Target);
            Assert.Equal(3, row.Sources.Count);
            Assert.False(row.Sources[0].HalfAxis); // the analog pull leads
            Assert.All(row.Sources.Skip(1), s => Assert.True(s.HalfAxis));
            Assert.True(string.IsNullOrEmpty(row.CombineMode));
        }

        // ─── B-6 #token titles ──────────────────────────────────────────

        private static string TokenHead(string title)
            => $"\"controller_mappings\"\n{{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"{title}\"\n";

        private static string Localization(params (string Lang, string Key, string Value)[] entries)
        {
            var sb = new System.Text.StringBuilder("\t\"localization\"\n\t{\n");
            foreach (var byLang in entries.GroupBy(e => e.Lang))
            {
                sb.Append($"\t\t\"{byLang.Key}\"\n\t\t{{\n");
                foreach (var e in byLang)
                    sb.Append($"\t\t\t\"{e.Key}\"\t\"{e.Value}\"\n");
                sb.Append("\t\t}\n");
            }
            sb.Append("\t}\n");
            return sb.ToString();
        }

        [Fact]
        public void TokenTitle_ResolvesFromEnglishLocalization()
        {
            string vdf = TokenHead("#Title_TF2Default")
                + Localization(("english", "Title_TF2Default", "Team Fortress 2 Defaults"))
                + "}\n";
            Assert.Equal("Team Fortress 2 Defaults", Translate(vdf).Name);
        }

        [Fact]
        public void TokenTitle_PrefersRequestedLanguage()
        {
            string vdf = TokenHead("#T")
                + Localization(("english", "T", "English Name"), ("german", "T", "Deutscher Name"))
                + "}\n";
            Assert.Equal("Deutscher Name", Translate(vdf, preferredLanguage: "german").Name);
            Assert.Equal("English Name", Translate(vdf).Name);
        }

        [Fact]
        public void TokenTitle_FallsBackToAnyLanguage()
        {
            string vdf = TokenHead("#T")
                + Localization(("french", "T", "Nom Français"))
                + "}\n";
            Assert.Equal("Nom Français", Translate(vdf).Name);
        }

        [Fact]
        public void TokenTitle_UnresolvedLibraryToken_UsesFallback()
        {
            // 770509247's shape: a Steam-library token that no config-local
            // language defines.
            string vdf = TokenHead("#Library_ControllerSaveDefaultTitle")
                + Localization(("english", "Unrelated", "x"))
                + "}\n";
            Assert.Equal("Steam Workshop Config", Translate(vdf).Name);
        }

        // ─── B-14a inner deadzone ───────────────────────────────────────

        [Fact]
        public void InnerDeadzone_StickAsDpad_SetsWedgeDeadZonePercent()
        {
            string vdf = Head
                + Group(1, "dpad",
                    Inputs(Inp("dpad_north", "key_press Q"))
                    + Settings(("deadzone_inner_radius", "1600")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal("Gamepad LeftStickY", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(5, src.DeadZone); // 1600 / 32767 = 4.88% -> 5
        }

        [Fact]
        public void InnerDeadzone_TriggerClickThresholdStaysExplicit()
        {
            // The click's 75 encodes the reachable-range end-of-travel
            // point; the group deadzone must not clobber it.
            string vdf = Head
                + Group(1, "trigger",
                    Inputs(Inp("click", "key_press Q"))
                    + Settings(("deadzone_inner_radius", "32767")))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);
            var row = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmKey51");
            Assert.Equal(75, Assert.Single(row.Sources).DeadZone);
        }

        [Fact]
        public void InnerDeadzone_Zero_KeepsEngineDefault()
        {
            string vdf = Head
                + Group(1, "dpad",
                    Inputs(Inp("dpad_north", "key_press Q"))
                    + Settings(("deadzone_inner_radius", "0")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal(50, src.DeadZone); // MappingSource default
        }

        [Fact]
        public void InnerDeadzone_MatchedStickPassthrough_CarriesPercent()
        {
            string vdf = Head
                + Group(1, "joystick_move",
                    Inputs(Inp("click", "xinput_button JOYSTICK_LEFT"))
                    + Settings(("deadzone_inner_radius", "4800")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "LeftThumbAxisX");
            Assert.Equal(15, Assert.Single(x.Sources).DeadZone); // 4800/32767 = 14.6% -> 15
        }

        // ─── Named-skip batch ───────────────────────────────────────────

        [Fact]
        public void TwoDScroll_GyroHost_TapsOnTheSignedRateHalf()
        {
            // v15: a gyro flick east reads the yaw rate's LOWER half
            // (SDL frame: positive yaw = nose left, so east = negative)
            // through a half-stamped descriptor trigger.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_east", "key_press F9")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal("Gyro Yaw", Assert.Single(m.TriggerInputDescriptors));
            Assert.True(m.TriggerDescriptorHalfAxis);
            Assert.True(m.TriggerDescriptorInvert);
            // Unset on purpose: the engine's own 30 deg/s gyro-as-button
            // rate threshold gates the flick.
            Assert.Equal(0, m.TriggerDescriptorDeadZonePercent);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        [Fact]
        public void TwoDScroll_ButtonHost_SkipsPerMemberNotPerGroup()
        {
            // A hand-hacked config can host 2dscroll on a surface with no
            // dpad_* member sources. The member walk (v15) names each
            // unresolvable input instead of skipping the group whole.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_east", "key_press F9")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Skipped, entry.Status);
            Assert.Equal(TranslationReasons.UnknownPhysicalInput, entry.ReasonKey);
            Assert.Equal(new[] { "ButtonDiamond", "dpad_east" }, entry.ReasonArgs);
        }

        [Fact]
        public void TwoDScroll_DpadHost_TapsOncePerPress()
        {
            // v15: a physical-dpad-hosted swipe member is a button read,
            // and the one-shot walk taps once per press edge.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_north", "key_press F5")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, m.Action);
            Assert.Equal("OnPress", m.TriggerMode);
            // Button host: the combined-output trigger shape (a zero-row
            // Xbox set keeps the automap passthrough feeding the bit, the
            // FinalizeMacroTriggers contract).
            Assert.Equal(Gamepad.DPAD_UP, m.TriggerXboxButtons);
            Assert.Equal("Gamepad DPadUp", m.TriggerFallbackDescriptor);
            Assert.False(m.TriggerDescriptorHalfAxis);
        }

        [Fact]
        public void HapticIntensity_EmitsRumblePulsePerActivator()
        {
            // v10 G1: activator-level haptics become RumblePulse macros,
            // level-scaled. A stored 0 is off and stays silent. The lowering
            // is clean and silent since v13: Steam Input treats rumble and
            // haptics interchangeably, so no Partial note fires.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E", activatorSettings: ActSettings(("haptic_intensity", "2"))),
                    Inp("button_b", "key_press Q", activatorSettings: ActSettings(("haptic_intensity", "1"))),
                    Inp("button_x", "key_press R", activatorSettings: ActSettings(("haptic_intensity", "0")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var pulses = p.Macros.Where(m => m.Action == TranslatedMacroAction.RumblePulse).ToList();
            Assert.Equal(2, pulses.Count);
            Assert.Equal(new[] { 66, 33 }, pulses.Select(m => m.RumbleStrengthPercent).ToArray());
            Assert.All(pulses, m => Assert.False(m.ConsumeTrigger));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.Status == TranslationStatus.Partial);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_HapticIntensityDropped");
        }

        [Fact]
        public void CurveSettings_NameOnlyTheChannelLessKeys()
        {
            // v18 consumes the exponent / range cluster on every analog
            // host (the trigger pull included), so only deadzone_shape on
            // a mouse-output host and the defensive output_curve remain
            // named.
            string vdf = Head
                + Group(1, "joystick_mouse",
                    Inputs(Inp("click", "key_press E"))
                    + Settings(("deadzone_outer_radius", "28800"), ("curve_exponent", "4"),
                        ("deadzone_shape", "1"), ("output_curve", "2")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.Equal("deadzone_shape, output_curve", Assert.Single(entry.ReasonArgs));
        }

        [Fact]
        public void ActivatorDelays_NamedPartial_OnlyWhenNonZero()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E",
                        activatorSettings: ActSettings(("delay_start", "50"), ("delay_end", "200"))),
                    Inp("button_b", "key_press Q",
                        activatorSettings: ActSettings(("delay_start", "0")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            // v18: the delayed key press reroutes onto the HoldKey pair
            // (down after delay_start, up delay_end after release); the
            // dormant delay_start 0 sibling keeps its plain row.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
            var hold = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.HoldKey);
            Assert.Equal(50, hold.DelayStartMs);
            Assert.Equal(200, hold.DelayEndMs);
            // The undelayed sibling (Q) keeps its plain row.
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("Gamepad ButtonB", Assert.Single(row.Sources).Descriptor);
        }

        [Fact]
        public void Interruptable_ZeroIsNativeBehavior_Silent()
        {
            // v18: stored interruptable 0 matches PadForge's never-cancel
            // evaluation exactly (sibling activators on one input all
            // fire), so nothing is reported for either value.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E", activatorSettings: ActSettings(("interruptable", "0"))),
                    Inp("button_b", "key_press Q", activatorSettings: ActSettings(("interruptable", "1")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(2, p.KbmMappingSet.Rows.Count);
            Assert.All(p.Report.Entries, e => Assert.Equal(TranslationStatus.Clean, e.Status));
        }

        [Theory]
        [InlineData("CHANGE_PLAYER_NUMBER", TranslationReasons.PlayerNumberActionNotSupported)]
        [InlineData("TOGGLE_LIZARD_MODE", TranslationReasons.LizardModeActionNotSupported)]
        [InlineData("Change_Player_Number", TranslationReasons.PlayerNumberActionNotSupported)]
        [InlineData("Toggle_Lizard_Mode", TranslationReasons.LizardModeActionNotSupported)]
        public void SteamOnlyControllerActions_GetNamedSkips(string action, string reason)
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", $"controller_action {action}")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Skipped, entry.Status);
            Assert.Equal(reason, entry.ReasonKey);
        }

        // ─── B-8a Long_Press layer carries ──────────────────────────────

        [Fact]
        public void LongPress_ModeShift_EmitsActivatorWithDelay()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "224")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal(224, act.DelayMs);
            Assert.Equal("Gamepad ButtonA", act.Descriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LongPressNotSupported);
        }

        [Fact]
        public void LongPress_HoldLayer_DefaultsTo500ms()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action HOLD_LAYER 2", activator: "Long_Press")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal(500, act.DelayMs); // Steam's UI default
        }

        [Fact]
        public void LongPress_AddLayer_TogglesWithDelay()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action ADD_LAYER 2", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "800")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode);
            Assert.Equal(800, act.DelayMs);
        }

        [Fact]
        public void LongPress_KeyBinding_BecomesHoldKeyPairMacro()
        {
            // v10 G10: a Long_Press key rides the HoldForMs HoldKey macro,
            // which the materializer lowers to a press-until-release +
            // OnRelease KeyRelease pair (Steam's exact semantics), retiring
            // the wave-2A tap-at-threshold approximation.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press F", activator: "Long_Press",
                        activatorSettings: ActSettings(("long_press_time", "224")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldKey, m.Action);
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(224, m.TriggerHoldMs);
            Assert.Equal(PadForge.Engine.Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal(0x46, m.VirtualKey); // VK_F
            Assert.False(m.ConsumeTrigger); // the OnRelease twin reads the same trigger
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.LongPressKeyTap);
        }

        [Fact]
        public void FullPressActivators_CarryNoDelay()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "mode_shift button_diamond 2")))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(0, Assert.Single(p.KbmMappingSet.ShiftActivators).DelayMs);
        }

        // ─── B-7 set_led ────────────────────────────────────────────────

        [Fact]
        public void SetLed_VintageSaturationScale_NormalizedToPercent()
        {
            // 1451857916's era: saturation 0-255.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action set_led 0 255 0 100 255 1")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.SetLightbarColor, m.Action);
            Assert.Equal(PadForge.Engine.Gamepad.A, m.TriggerXboxButtons);
            Assert.Equal((0, 255, 0), (m.LedR, m.LedG, m.LedB));
            Assert.Equal(100, m.LedBrightnessPercent);
            Assert.Equal(100, m.LedSaturationPercent); // 255 -> 100%
            Assert.Equal(1, m.LedSetting);
            Assert.False(m.ConsumeTrigger);
        }

        [Fact]
        public void SetLed_CurrentSaturationScale_PassesThrough()
        {
            // 3353604014's era: saturation 0-100.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action set_led 255 30 0 42 96 1")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var m = Assert.Single(Translate(vdf).Macros);
            Assert.Equal(42, m.LedBrightnessPercent);
            Assert.Equal(96, m.LedSaturationPercent);
        }

        [Fact]
        public void SetLed_SettingZero_MacroWithoutPartialNote()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action set_led 0 255 255 100 255 0")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(0, Assert.Single(p.Macros).LedSetting);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.Status == TranslationStatus.Partial);
        }

        [Fact]
        public void SetLed_SettingTwo_MacroWithoutApproximationNote()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action set_led 255 0 0 100 100 2")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(2, Assert.Single(p.Macros).LedSetting);
            // v17: no note. Clearing the override IS restoring the
            // default lighting, so the macro reports Clean.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.Status == TranslationStatus.Partial);
        }

        [Fact]
        public void SetLed_TouchpadHosted_RidesWedgeDescriptorTrigger()
        {
            // Wave 3: the trackpad-wedge-hosted set_led triggers on the
            // wedge gesture descriptor (empty-guid InputDevice entry).
            // The wedge's joystick-output feature self-arms at apply
            // since v14, so the macro reports Clean.
            string vdf = Head
                + Group(1, "dpad", Inputs(Inp("dpad_north", "controller_action set_led 255 0 0 100 255 1")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.SetLightbarColor, m.Action);
            // requires_click is absent on the dpad group, which defaults to
            // require: the wedge's click gate rides the trigger as its AND
            // companion.
            Assert.Equal(new[] { "Touchpad 0 DPadUp", "Touchpad 0 Click" },
                m.TriggerInputDescriptors.ToArray());
            Assert.Equal(0, (int)m.TriggerXboxButtons);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MacroEmitted
                && e.Status == TranslationStatus.Clean);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_TrackpadFeatureRequired");
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.NoDeviceFreeTrigger);
        }

        [Fact]
        public void SetLed_TriggerClickHost_UsesAxisTrigger()
        {
            string vdf = Head
                + Group(1, "trigger", Inputs(Inp("click", "controller_action set_led 13 255 0 100 100 1")))
                + Preset(0, "Default", (1, "left_trigger active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(0, (int)m.TriggerXboxButtons);
            Assert.Equal("LeftTrigger", m.TriggerAxisTarget);
            Assert.Equal(75, m.TriggerAxisThresholdPercent);
        }

        [Theory]
        [InlineData("set_led 255 0 0 100 100")]      // arity
        [InlineData("set_led 255 0 0 100 100 7")]    // unknown setting
        [InlineData("set_led x 0 0 100 100 1")]      // junk channel
        public void SetLed_Malformed_SkippedAsUnsupported(string param)
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", $"controller_action {param}")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.UnsupportedControllerAction, entry.ReasonKey);
        }

        // ─── Translator version ─────────────────────────────────────────

        [Fact]
        public void TranslatorVersion_IsTwentyFour_AndRidesTheSummary()
        {
            Assert.Equal(24, TranslationReport.CurrentTranslatorVersion);
            var p = Translate(Head + "}\n");
            Assert.Equal(24, p.Report.TranslatorVersion);
            Assert.StartsWith("v24 ", p.Report.ToSummaryString());
        }
    }
}
