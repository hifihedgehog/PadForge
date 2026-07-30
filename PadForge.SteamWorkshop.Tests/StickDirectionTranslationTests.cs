using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v13 lowerings: the LSTICK_/RSTICK_ direction
    /// params as bipolar thumb-axis rows (row value convention SDL
    /// "+X right, +Y down", polarity on the output-side flip), the STEAM
    /// guide alias, the serializer key aliases and media row riding the
    /// SendInput macro channel, mouse_delta's named skip, the Steam-client
    /// verb families, and the Scroll Wheel List named skips.</summary>
    public class StickDirectionTranslationTests
    {
        private static TranslatedProfile Translate(string vdf)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 42 });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V13\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

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

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
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

        // ─── LSTICK_/RSTICK_ direction rows ─────────────────────────────

        [Theory]
        [InlineData("LSTICK_UP", "LeftThumbAxisY", true)]
        [InlineData("LSTICK_DOWN", "LeftThumbAxisY", false)]
        [InlineData("LSTICK_LEFT", "LeftThumbAxisX", true)]
        [InlineData("LSTICK_RIGHT", "LeftThumbAxisX", false)]
        [InlineData("RSTICK_UP", "RightThumbAxisY", true)]
        [InlineData("RSTICK_DOWN", "RightThumbAxisY", false)]
        [InlineData("RSTICK_LEFT", "RightThumbAxisX", true)]
        [InlineData("RSTICK_RIGHT", "RightThumbAxisX", false)]
        public void StickDirection_OnButtonHost_EmitsBipolarAxisRow(
            string token, string target, bool inverted)
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", $"xinput_button {token}")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal(target, row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad ButtonA", src.Descriptor);
            // Button host: no half-axis read, so the polarity rides Invert
            // (buttons map to plus/minus one, sign from Invert).
            Assert.False(src.HalfAxis);
            Assert.Equal(inverted, src.Invert);
            Assert.False(src.InvertOutput);
            Assert.True(p.NeedsXboxSlot);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownXInputButton);
        }

        [Fact]
        public void StickDirection_OnStickWedgeHost_KeepsHalfAxisAndRidesInvertOutput()
        {
            // A right-stick dpad_north wedge (lower Y half, Invert as the
            // half SELECTOR) bound to LSTICK_UP: the wedge read survives
            // and the up polarity lands on InvertOutput, so the half
            // selection is not destroyed (the engine's own predicate,
            // InvertConsumedByHalfAxisRead).
            string vdf = Head
                + Group(1, "dpad", Inputs(Inp("dpad_north", "xinput_button LSTICK_UP")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("LeftThumbAxisY", row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad RightStickY", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.True(src.Invert);        // still the north-wedge half selector
            Assert.True(src.InvertOutput);  // the up polarity
        }

        [Fact]
        public void StickDirection_DownOnWedge_NoOutputFlip()
        {
            string vdf = Head
                + Group(1, "dpad", Inputs(Inp("dpad_south", "xinput_button LSTICK_DOWN")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("LeftThumbAxisY", row.Target);
            var src = Assert.Single(row.Sources);
            Assert.Equal("Gamepad LeftStickY", src.Descriptor);
            Assert.True(src.HalfAxis);
            Assert.False(src.Invert);       // south wedge = upper half
            Assert.False(src.InvertOutput); // down = positive, no flip
        }

        [Fact]
        public void StickDirection_ReleaseToggleTurboLongPress_V15Closure()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button LSTICK_UP", activator: "release"),
                    Inp("button_b", "xinput_button LSTICK_UP",
                        activatorSettings: ActSettings(("toggle", "1"))),
                    Inp("button_x", "xinput_button RSTICK_UP",
                        activatorSettings: ActSettings(("hold_repeats", "1"))),
                    Inp("button_y", "xinput_button LSTICK_DOWN", activator: "Long_Press")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);

            // Release and Long_Press ride the AxisHold channel since v15.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported
                || e.ReasonKey == TranslationReasons.LongPressNotSupported);
            // v18: the toggle and turbo variants latch / pulse the axis
            // via macros instead of keeping momentary rows.
            Assert.Empty(p.XboxMappingSet.Rows);
            var latch = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.ToggleVcAxis);
            Assert.Equal("LeftThumbAxisY", latch.TargetAxis);
            Assert.True(latch.TargetAxisNegative); // LSTICK_UP = SDL-frame negative
            var turbo = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.RepeatVcAxisWhileHeld);
            Assert.Equal("RightThumbAxisY", turbo.TargetAxis);
            Assert.True(turbo.TargetAxisNegative); // RSTICK_UP
            var tap = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.VcAxisTap);
            Assert.Equal("OnRelease", tap.TriggerMode);
            Assert.Equal("LeftThumbAxisY", tap.TargetAxis);
            Assert.True(tap.TargetAxisNegative); // LSTICK_UP = SDL-frame negative
            var hold = Assert.Single(p.Macros, m => m.Action == TranslatedMacroAction.HoldVcAxis);
            Assert.Equal("HoldForMs", hold.TriggerMode);
            Assert.Equal("LeftThumbAxisY", hold.TargetAxis);
            Assert.False(hold.TargetAxisNegative); // LSTICK_DOWN = SDL-frame positive
        }

        [Fact]
        public void SteamToken_ResolvesAsGuideButton()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "xinput_button STEAM")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows);
            Assert.Equal("ButtonGuide", row.Target);
        }

        // ─── Serializer key aliases and the media row ───────────────────

        [Theory]
        [InlineData("ENTER", 0x0D)]
        [InlineData("ESC", 0x1B)]
        [InlineData("BACKSLASH", 0xDC)]
        [InlineData("BREAK", 0x13)]
        [InlineData("LEFT_WINDOWS", 0x5B)]
        [InlineData("RIGHT_WINDOWS", 0x5C)]
        [InlineData("MUTE", 0xAD)]
        [InlineData("VOLUME_DOWN", 0xAE)]
        [InlineData("VOLUME_UP", 0xAF)]
        [InlineData("NEXT_TRACK", 0xB0)]
        [InlineData("PREV_TRACK", 0xB1)]
        [InlineData("STOP", 0xB2)]
        [InlineData("PLAY", 0xB3)]
        public void SerializerKeyNames_Resolve(string name, int vk)
        {
            Assert.True(SteamInputVkTable.TryResolve(name, out byte resolved, out _));
            Assert.Equal(vk, resolved);
        }

        [Fact]
        public void MediaKey_PlainPress_RidesHoldKeyMacro()
        {
            // VK_MEDIA_PLAY_PAUSE is outside the KbM row engine's closed
            // set, so the plain press rides the SendInput HoldKey pair
            // (v10 G11 channel), not a dead row and not UnknownKey.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "key_press PLAY")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var macro = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.HoldKey, macro.Action);
            Assert.Equal(0xB3, macro.VirtualKey);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownKey);
        }

        // ─── controller_action census closures ──────────────────────────

        [Fact]
        public void MouseDelta_LowersToOneShotNudgeMacro()
        {
            // v16: "Move by Amount" builds. The authored dx/dy pixels ride
            // the MouseNudge macro on the hosting input's own descriptor
            // (no phantom Xbox slot for a cursor verb).
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action mouse_delta 100 0")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseNudge, m.Action);
            Assert.Equal(100, m.DeltaX);
            Assert.Equal(0, m.DeltaY);
            Assert.Equal("OnPress", m.TriggerMode);
            Assert.Equal("Gamepad ButtonA", Assert.Single(m.TriggerInputDescriptors));
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.MacroEmitted, entry.ReasonKey);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
        }

        [Fact]
        public void MouseDelta_NegativeDeltas_PassThroughSigned()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action mouse_delta -250 -40")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseNudge, m.Action);
            Assert.Equal(-250, m.DeltaX);
            Assert.Equal(-40, m.DeltaY);
        }

        [Fact]
        public void MouseDelta_Malformed_SkipsAsUnsupported()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action mouse_delta 100")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.UnsupportedControllerAction, entry.ReasonKey);
        }

        [Theory]
        [InlineData("SR_ENABLE")]
        [InlineData("GR_CLIP")]
        [InlineData("TS_N")]
        [InlineData("STEAMMUSIC_PLAYPAUSE")]
        [InlineData("BIGPICTURE_QUIT")]
        [InlineData("HOST_SUSPEND")]
        [InlineData("CHORD_HINT_DISPLAY")]
        [InlineData("BRIGHTNESS_UP")]
        [InlineData("CONTROLLER_POWEROFF")]
        [InlineData("QUIT_APPLICATION")]
        [InlineData("TOGGLE_MAGNIFIER")]
        [InlineData("SYSTEM_KEY_0")]
        public void SteamClientVerbs_GetTheNamedSystemActionSkip(string verb)
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", $"controller_action {verb}")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.SteamSystemAction, entry.ReasonKey);
            Assert.Equal(verb, entry.ReasonArgs.Single());
        }

        [Fact]
        public void ToggleLizard_SerializerSpelling_GetsTheLizardSkip()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action toggle_lizard")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.LizardModeActionNotSupported, entry.ReasonKey);
        }

        // ─── Scroll Wheel List: the v16 cycle lowering ──────────────────

        [Fact]
        public void ScrollWheelList_Trackpad_LowersToOneCycleMacro()
        {
            // v16: the ordered list becomes ONE CycleList macro on the
            // clockwise detent gesture. The cw/ccw wheel members keep
            // their drag row beside it.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_DOWN"),
                    Inp("scroll_wheel_list_0", "key_press 1"),
                    Inp("scroll_wheel_list_1", "key_press 2"),
                    Inp("scroll_wheel_list_2", "key_press 3")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var cycle = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.CycleList, cycle.Action);
            Assert.Equal("Touchpad 0 SwipeDown", Assert.Single(cycle.TriggerInputDescriptors));
            Assert.True(cycle.CycleWrap); // scroll_wrap absent = wrap
            Assert.Equal(3, cycle.CycleSteps.Count);
            Assert.All(cycle.CycleSteps, s => Assert.Equal(TranslatedCycleStepKind.KeyTap, s.Kind));
            Assert.Equal(new[] { 0x31, 0x32, 0x33 },
                cycle.CycleSteps.Select(s => s.VirtualKey).ToArray());
            Assert.Equal(new[] { 0, 1, 2 },
                cycle.CycleSteps.Select(s => s.ItemIndex).ToArray());

            // The wheel itself still lowers onto the drag row, one Clean
            // entry per list item names its step, and the geometry
            // Partial covers the group.
            Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal(3, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.MacroEmitted
                && e.SourcePath.Contains("scroll_wheel_list_")));
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ScrollWheelApproximated);
        }

        [Fact]
        public void ScrollWheelList_Stick_TriggersOnDragWedge_AndCarriesWrapOff()
        {
            string vdf = Head
                + Group(1, "scrollwheel",
                    Inputs(
                        Inp("scroll_wheel_list_0", "mouse_wheel SCROLL_UP"),
                        Inp("scroll_wheel_list_1", "mouse_wheel SCROLL_DOWN"))
                    + Settings(("scroll_wrap", "0"), ("deadzone_inner_radius", "6553")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);

            var cycle = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.CycleList, cycle.Action);
            Assert.False(cycle.CycleWrap);
            Assert.Equal("Gamepad LeftStickY", Assert.Single(cycle.TriggerInputDescriptors));
            Assert.True(cycle.TriggerDescriptorHalfAxis); // clockwise = deflect down
            Assert.False(cycle.TriggerDescriptorInvert);
            Assert.Equal(20, cycle.TriggerDescriptorDeadZonePercent);
            Assert.Equal(2, cycle.CycleSteps.Count);
            Assert.All(cycle.CycleSteps, s => Assert.Equal(TranslatedCycleStepKind.WheelTap, s.Kind));
            Assert.Equal(new[] { 1, -1 },
                cycle.CycleSteps.Select(s => s.WheelTicks).ToArray()); // UP then DOWN
        }

        [Fact]
        public void ScrollWheel_OutOfGrammarHost_FallsToTheSafetyNet()
        {
            // v16 retired-arm shape: Steam's own serializer never hosts
            // scrollwheel off a pad/stick (census guard in
            // TranslationGoldenTests), so a hand-edited diamond host is
            // out-of-grammar config and routes through the member walk's
            // named safety net instead of a dedicated skip arm.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_DOWN"),
                    Inp("scroll_wheel_list_0", "key_press 1")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Equal(2, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.UnknownPhysicalInput));
        }
    }
}
