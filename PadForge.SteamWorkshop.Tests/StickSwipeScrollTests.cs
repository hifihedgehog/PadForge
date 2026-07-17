using System.Linq;
using PadForge.Engine;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v12 contracts: stick-hosted 2dscroll lowers each
    /// dpad_* member's one-shot-able bindings onto tap macros triggered on
    /// the member's own wedge read (one fire per deflection entry), the
    /// descriptor trigger carries the wedge's half-axis shape end to end,
    /// and stick-hosted scrollwheel feeds KbmScroll from the stick's Y
    /// deflection drag in the trackpad G4 shape.</summary>
    public class StickSwipeScrollTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Stick\"\n";

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

        // ─── 2dscroll on a stick: one-shot wedge taps ───────────────────

        [Fact]
        public void TwoDScroll_StickHost_KeyBindings_BecomeOneShotWedgeTaps()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_north", "key_press F5"),
                    Inp("dpad_east", "key_press F9")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            // Flicks are macros, not rows: a row would fire for the whole
            // deflection instead of once per entry.
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.Equal(2, p.Macros.Count);

            var north = Assert.Single(p.Macros, m => m.VirtualKey == 0x74); // F5
            Assert.Equal(TranslatedMacroAction.KeyTap, north.Action);
            Assert.Equal("OnPress", north.TriggerMode);
            Assert.Equal("Gamepad LeftStickY", Assert.Single(north.TriggerInputDescriptors));
            Assert.True(north.TriggerDescriptorHalfAxis);
            Assert.True(north.TriggerDescriptorInvert); // north = Y lower half
            Assert.False(north.ConsumeTrigger);

            var east = Assert.Single(p.Macros, m => m.VirtualKey == 0x78); // F9
            Assert.Equal("Gamepad LeftStickX", Assert.Single(east.TriggerInputDescriptors));
            Assert.True(east.TriggerDescriptorHalfAxis);
            Assert.False(east.TriggerDescriptorInvert); // east = X upper half

            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.FlickBindingNotOneShot
                || e.ReasonKey == TranslationReasons.FlickAxisTargetNotSupported);
            Assert.Equal(2, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.MacroEmitted
                && e.Status == TranslationStatus.Clean));
        }

        [Fact]
        public void TwoDScroll_RightStick_GroupDeadzone_RidesTheWedgeTrigger()
        {
            string vdf = Head
                + Group(1, "2dscroll",
                    Inputs(Inp("dpad_south", "xinput_button A"))
                    + Settings(("deadzone_inner_radius", "9830")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var tap = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.VcButtonTap, tap.Action);
            Assert.Equal("OnPress", tap.TriggerMode);
            Assert.Equal(Gamepad.A, tap.TargetXboxButtons);
            Assert.Equal("Gamepad RightStickY", Assert.Single(tap.TriggerInputDescriptors));
            Assert.True(tap.TriggerDescriptorHalfAxis);
            Assert.False(tap.TriggerDescriptorInvert); // south = Y upper half
            Assert.Equal(30, tap.TriggerDescriptorDeadZonePercent); // 9830/32767
            // The tap writes a VC button, so the Xbox slot is demanded.
            Assert.True(p.NeedsXboxSlot);
        }

        [Fact]
        public void TwoDScroll_StickHost_MouseButton_TapsOnFlick()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_west", "mouse_button LEFT")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var tap = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseButtonTap, tap.Action);
            Assert.Equal("OnPress", tap.TriggerMode);
            Assert.Equal(0, tap.MouseButtonIndex);
            Assert.Equal("Gamepad LeftStickX", Assert.Single(tap.TriggerInputDescriptors));
            Assert.True(tap.TriggerDescriptorHalfAxis);
            Assert.True(tap.TriggerDescriptorInvert); // west = X lower half
        }

        [Fact]
        public void TwoDScroll_StickHost_SetLed_FiresOncePerFlick()
        {
            // One-shot controller_action verbs ride the wedge trigger too.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_north", "controller_action set_led 255 0 0 43 100 1")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var led = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.SetLightbarColor, led.Action);
            Assert.Equal("OnPress", led.TriggerMode);
            Assert.Equal("Gamepad LeftStickY", Assert.Single(led.TriggerInputDescriptors));
            Assert.True(led.TriggerDescriptorHalfAxis);
            Assert.True(led.TriggerDescriptorInvert);
        }

        [Fact]
        public void TwoDScroll_StickHost_HapticIntensity_PulsesOnTheWedge()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_east", "key_press F9",
                        activatorSettings: ActSettings(("haptic_intensity", "1")))))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var pulse = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.RumblePulse);
            Assert.Equal(33, pulse.RumbleStrengthPercent);
            Assert.Equal("Gamepad LeftStickX", Assert.Single(pulse.TriggerInputDescriptors));
            Assert.True(pulse.TriggerDescriptorHalfAxis);
            Assert.False(pulse.TriggerDescriptorInvert);
        }

        [Fact]
        public void TwoDScroll_StickHost_ModeShift_KeepsNamedSkip()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_west", "mode_shift joystick 5")))
                + Group(5, "dpad", Inputs(Inp("dpad_north", "key_press Q")))
                + Preset(0, "Default",
                    (1, "joystick active"), (5, "joystick active modeshift"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.Macros);
            Assert.Empty(p.XboxMappingSet.ShiftActivators);
            Assert.Empty(p.KbmMappingSet.ShiftActivators);
            // A mode shift needs held state, which a one-shot flick has
            // no carrier for: the per-arm reason names that (v14).
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.FlickBindingNotOneShot
                && e.Status == TranslationStatus.Skipped);
        }

        [Fact]
        public void TwoDScroll_StickHost_TriggerAxisTarget_KeepsNamedSkip()
        {
            // No discrete trigger-pull tap primitive exists (AxisSet is a
            // one-frame write), same gate as the release-activator path.
            // The per-arm reason names the axis-natured target (v14).
            string vdf = Head
                + Group(1, "2dscroll", Inputs(Inp("dpad_south", "xinput_button TRIGGER_LEFT")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.Macros);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.FlickAxisTargetNotSupported
                && e.Status == TranslationStatus.Skipped);
        }

        [Fact]
        public void TwoDScroll_StickHost_ClickMember_TranslatesAsNormalMember()
        {
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_north", "key_press F5"),
                    Inp("click", "xinput_button JOYSTICK_LEFT")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("LeftThumbButton", row.Target);
            Assert.Equal("Gamepad LeftStick", Assert.Single(row.Sources).Descriptor);
        }

        [Fact]
        public void TwoDScroll_TrackpadHost_ClickMember_TranslatesAsNormalMember()
        {
            // The v10 swipe walk skipped non-dpad members whole; the click
            // command is a plain pad click and translates since v12.
            string vdf = Head
                + Group(1, "2dscroll", Inputs(
                    Inp("dpad_north", "key_press F5"),
                    Inp("click", "mouse_button LEFT")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var click = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMBtn0");
            Assert.Equal("Touchpad 1 Click", Assert.Single(click.Sources).Descriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        // ─── scrollwheel on a stick: deflection drag ────────────────────

        [Fact]
        public void ScrollWheel_StickHost_LowersOntoDeflectionDrag()
        {
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_DOWN"),
                    Inp("scroll_counterclockwise", "mouse_wheel SCROLL_UP"),
                    Inp("click", "mouse_button MIDDLE")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            // clockwise + SCROLL_DOWN: deflect down scrolls down, and the
            // symmetric counterclockwise twin folds into the same source.
            var scroll = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmScroll");
            var drag = Assert.Single(scroll.Sources);
            Assert.Equal("Gamepad LeftStickY", drag.Descriptor);
            Assert.False(drag.Invert);

            var click = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMBtn2");
            Assert.Equal("Gamepad LeftStick", Assert.Single(click.Sources).Descriptor);

            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelApproximated
                && e.Status == TranslationStatus.Partial);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelModeNotSupported);
        }

        [Fact]
        public void ScrollWheel_RightStick_Reversed_InvertsAndCarriesDeadzone()
        {
            string vdf = Head
                + Group(1, "scrollwheel",
                    Inputs(Inp("scroll_clockwise", "mouse_wheel SCROLL_UP"))
                    + Settings(("deadzone_inner_radius", "6553")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var scroll = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmScroll");
            var drag = Assert.Single(scroll.Sources);
            Assert.Equal("Gamepad RightStickY", drag.Descriptor);
            Assert.True(drag.Invert);
            Assert.Equal(20, drag.DeadZone); // 6553/32767, rest jitter gate
        }

        [Fact]
        public void ScrollWheel_StickHost_KeyOnDetent_KeepsNamedSkip()
        {
            // A key on a wheel detent has no continuous channel on a
            // deflection drag either, so it keeps the named skip.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(Inp("scroll_clockwise", "key_press A")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelModeNotSupported
                && e.Status == TranslationStatus.Skipped);
        }
    }
}
