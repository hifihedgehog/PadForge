using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v22 pins plus the v23 engage-enum pins.
    /// gyro_ratchet_button_mask lowers bit-by-bit (Steam's own
    /// k_eGamepadButtonBitMask enum, read out of the shipped configurator
    /// JS) onto the slot-level ratchet clutch lane, naming only genuinely
    /// ungrounded bits; since v23 gyro_button indexes the same enum and
    /// lowers onto the engage stamp; the activator delay family closes
    /// whole (layer release linger, autofire release linger, wheel-row
    /// reroute, press-leg tap extension, unobservable-edge proofs); and
    /// group-level haptic intensity rides every member activation through
    /// the EmitHapticPulse fallback.</summary>
    public class RatchetDelayHapticTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V22\"\n";
        private const string HeadPs5 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V22\"\n\t\"controller_type\"\t\"controller_ps5\"\n";

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

        // ─── gyro_ratchet_button_mask: grounded bits build ──────────────

        [Fact]
        public void RatchetMask_UpperLeftPaddle_StampsPaddle4()
        {
            // Fixture 3456927474's live shape: 1<<41 = ButtonBackGripLeftUpper.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask", "2199023255552")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "Gamepad Paddle4" }, p.GyroRatchetDescriptors);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void RatchetMask_RightPadTouch_MultiPad_StampsPadOneTouch()
        {
            // Fixture 3725174032's live shape: 1<<20 = CapSenseRightTouchPad.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask", "1048576")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "Touchpad 1 Finger 0 Down" }, p.GyroRatchetDescriptors);
        }

        [Fact]
        public void RatchetMask_RightPadTouch_SinglePad_StampsRightHalfSpot()
        {
            string vdf = HeadPs5
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask", "1048576")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "Touchpad 0 Finger 0 Down Right" }, p.GyroRatchetDescriptors);
        }

        [Fact]
        public void RatchetMask_UngroundedBits_KeepTheNoteWithResidualMask()
        {
            // 1<<41 grounds (Paddle4); 1<<37 (ButtonMacro5, the Steam Link
            // on-screen button past SDL's misc space) has no read.
            ulong mask = (1UL << 41) | (1UL << 37);
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask",
                    mask.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[] { "Gamepad Paddle4" }, p.GyroRatchetDescriptors);
            var e = Assert.Single(p.Report.Entries, x =>
                x.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
            Assert.Equal(new[] { "gyro_ratchet_button_mask", (1UL << 37).ToString() }, e.ReasonArgs);
        }

        [Fact]
        public void RatchetMask_CapSenseBits_GroundOnTheCapsenseReads()
        {
            // v26: bits 44-47 = CapSenseLeftAux / RightAux / LeftStick /
            // RightStick in the shipped configurator's
            // k_eGamepadButtonBitMask, landing on the fork's
            // SDL_GetGamepadCapSense channels. Wild witnesses 3726651949
            // (bits 44+45) and 3724212306 (bit 47).
            ulong mask = (1UL << 44) | (1UL << 45) | (1UL << 46) | (1UL << 47);
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask",
                    mask.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(new[]
            {
                "Gamepad LeftGripTouch", "Gamepad LeftStickTouch",
                "Gamepad RightGripTouch", "Gamepad RightStickTouch",
            }, p.GyroRatchetDescriptors.OrderBy(d => d, System.StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void RatchetMask_DualSenseButtonSet_GroundsWhole()
        {
            // Fixture 3353604014's authored mask: triggers + face + D-pad +
            // View/Options + pad clicks + stick clicks, bits
            // {0,1,4..12,14,17,18,22,26}. Single-pad host folds both pad
            // clicks onto the one physical click.
            string vdf = HeadPs5
                + Group(1, "gyro_to_mouse", Settings(("gyro_ratchet_button_mask", "71720947")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
            Assert.Equal(new[]
            {
                "Gamepad ButtonA", "Gamepad ButtonB", "Gamepad ButtonBack",
                "Gamepad ButtonStart", "Gamepad ButtonX", "Gamepad ButtonY",
                "Gamepad DPadDown", "Gamepad DPadLeft", "Gamepad DPadRight",
                "Gamepad DPadUp", "Gamepad LeftStick", "Gamepad LeftTrigger",
                "Gamepad RightStick", "Gamepad RightTrigger", "Touchpad 0 Click",
            }, p.GyroRatchetDescriptors);
        }

        [Fact]
        public void RatchetMask_ComposesWithGyroButtonEngage()
        {
            // The ratchet is its own AND-NOT lane, so an authored engage
            // button and a ratchet mask stamp side by side (the reason the
            // SetGyroEngaged / engage-invert lowerings were rejected: the
            // engage read ORs its sources, and the invert flag owns the
            // single engage descriptor).
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(
                    ("gyro_button", "0"),
                    ("gyro_ratchet_button_mask", "2199023255552")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Touchpad 1 Finger 0 Down", p.GyroEngageDescriptor);
            Assert.Equal(new[] { "Gamepad Paddle4" }, p.GyroRatchetDescriptors);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void GyroButton_Index9_LowersTheDPadRightEngage()
        {
            // v23: gyro_button=N indexes the same k_eGamepadButtonBitMask
            // enum the ratchet mask grounds against (the configurator
            // renders both settings through the one GyroButtonPicker
            // glyph map). Index 9 = DPadRight, the exact setting the v22
            // arm dropped.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_button", "9")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad DPadRight", p.GyroEngageDescriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void GyroButton_OutOfEnumIndex_StillKeepsTheNote()
        {
            // Index 21 is an enum hole (no k_eGamepadButtonBitMask member,
            // see RatchetBitDescriptor), so the engage arm may not guess:
            // the named note survives for genuinely out-of-enum indices.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Settings(("gyro_button", "21")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("", p.GyroEngageDescriptor);
            var e = Assert.Single(p.Report.Entries, x =>
                x.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
            Assert.Equal(new[] { "gyro_button", "21" }, e.ReasonArgs);
            Assert.Empty(p.GyroRatchetDescriptors);
        }

        // ─── layer delay_end: Hold-mode release linger ──────────────────

        [Fact]
        public void ModeShiftDelays_RideDebounceAndReleaseLinger()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2",
                        activatorSettings: ActSettings(("delay_start", "80"), ("delay_end", "350")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal(80, act.DelayMs);
            Assert.Equal(350, act.ReleaseDelayMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        [Fact]
        public void ToggledModeShift_DelayEnd_ConsumesSilently()
        {
            // A Toggle-mode carrier deactivates on a press, never on the
            // release, so Steam's own release+delay_end edge changes
            // nothing: no linger stamp, no note.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mode_shift button_diamond 2",
                        activatorSettings: ActSettings(("toggle", "1"), ("delay_end", "350")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press Q")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "button_diamond active modeshift"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Toggle", act.Mode);
            Assert.Equal(0, act.ReleaseDelayMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        // ─── autofire delay_end: the pulse-stop release linger ──────────

        [Fact]
        public void AutofireDelays_DelayStepPlusReleaseLinger()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "key_press E",
                        activatorSettings: ActSettings(
                            ("hold_repeats", "1"), ("repeat_rate", "150"),
                            ("delay_start", "90"), ("delay_end", "400")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros, x => x.Action == TranslatedMacroAction.RepeatKeyWhileHeld);
            Assert.Equal(90, m.DelayStartMs);
            Assert.Equal(400, m.DelayEndMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        // ─── wheel-row delays: reroute onto the wheel turbo ─────────────

        [Fact]
        public void WheelRowDelays_RerouteOntoTheWheelTurbo()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mouse_wheel SCROLL_UP",
                        activatorSettings: ActSettings(("delay_start", "120"), ("delay_end", "250")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var m = Assert.Single(p.Macros, x => x.Action == TranslatedMacroAction.RepeatWheelWhileHeld);
            // delay_start composes into the HoldForMs threshold (a Delay
            // step would re-run inside every detent iteration, v19 T1);
            // delay_end rides the release linger.
            Assert.Equal("HoldForMs", m.TriggerMode);
            Assert.Equal(120, m.TriggerHoldMs);
            Assert.Equal(250, m.DelayEndMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        [Fact]
        public void WheelRow_WithoutDelays_StaysARow()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "mouse_wheel SCROLL_UP")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmScroll");
            Assert.Empty(p.Macros);
        }

        // ─── press-leg one-shot delay_end ───────────────────────────────

        [Fact]
        public void PressLegCursorWarp_DelayEnd_ConsumesSilently()
        {
            // Fixture 3456927474's live shape (menu MOUSE_POSITION chains):
            // the deactivation edge of an edge-fired command emits nothing
            // in Steam either, so the shifted edge is unobservable.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "controller_action MOUSE_POSITION 27741 30558 1",
                        activatorSettings: ActSettings(("delay_start", "50"), ("delay_end", "300")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros, x => x.Action == TranslatedMacroAction.MoveMouseToScreenPosition);
            Assert.Equal(50, m.DelayStartMs);
            Assert.Equal(0, m.DelayEndMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        [Fact]
        public void PressLegTap_DelayEnd_GrowsTheAssert()
        {
            // A one-shot swipe tap deactivates late by delay_end: the
            // assert grows to the authored length.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_north", "key_press E",
                        activatorSettings: ActSettings(("delay_end", "220")))))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros, x => x.Action == TranslatedMacroAction.KeyTap);
            Assert.Equal(220, m.TapDurationMs);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_ActivatorDelayDropped");
        }

        // ─── group-level haptics ride the member activations ────────────

        [Fact]
        public void GroupHapticIntensity_PulsesEveryMemberActivator()
        {
            string vdf = Head
                + Group(1, "four_buttons",
                    Inputs(
                        Inp("button_a", "key_press E"),
                        Inp("button_b", "key_press Q", activatorSettings: ActSettings(("haptic_intensity", "1"))),
                        Inp("button_x", "key_press R", activatorSettings: ActSettings(("haptic_intensity", "0"))))
                    + Settings(("haptic_intensity", "2")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var pulses = p.Macros.Where(m => m.Action == TranslatedMacroAction.RumblePulse).ToList();
            // A takes the group's level (66), B's own 1 wins (33), X's
            // explicit 0 stays off.
            Assert.Equal(2, pulses.Count);
            Assert.Equal(new[] { 66, 33 }, pulses.Select(m => m.RumbleStrengthPercent).ToArray());
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == "Workshop_Tr_HapticIntensityDropped");
        }

        [Fact]
        public void GroupHapticOverrideZero_SilencesThePlainKey()
        {
            string vdf = Head
                + Group(1, "four_buttons",
                    Inputs(Inp("button_a", "key_press E"))
                    + Settings(("haptic_intensity", "2"), ("haptic_intensity_override", "0")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Macros, m => m.Action == TranslatedMacroAction.RumblePulse);
        }

        [Fact]
        public void GroupHapticLevel_DoesNotLeakIntoTheNextGroup()
        {
            string vdf = Head
                + Group(1, "four_buttons",
                    Inputs(Inp("button_a", "key_press E"))
                    + Settings(("haptic_intensity", "3")))
                + Group(2, "dpad", Inputs(Inp("dpad_north", "key_press W")))
                + Preset(0, "Default", (1, "button_diamond active"), (2, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var pulse = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.RumblePulse);
            Assert.Equal(100, pulse.RumbleStrengthPercent);
        }
    }
}
