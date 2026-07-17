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
        public void StickDirection_ReleaseToggleTurboLongPress_KeepNamedNotes()
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

            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ReleaseActivatorNotSupported);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ToggleDropped);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.RepeatDropped);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LongPressNotSupported);
            // The toggle and turbo variants still emit the momentary row
            // (button_b / button_x). Release and long-press emit nothing.
            Assert.Equal(2, p.XboxMappingSet.Rows.Count);
            Assert.Empty(p.Macros);
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
        public void MouseDelta_GetsItsNamedSkip()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "controller_action mouse_delta 100 0")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationReasons.MouseDeltaNotSupported, entry.ReasonKey);
            Assert.Equal(TranslationStatus.Skipped, entry.Status);
            Assert.Equal("mouse_delta 100 0", entry.ReasonArgs.Single());
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

        // ─── Scroll Wheel List members ──────────────────────────────────

        [Fact]
        public void ScrollWheelList_Members_GetNamedSkipsInsteadOfSilence()
        {
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "mouse_wheel SCROLL_DOWN"),
                    Inp("scroll_wheel_list_0", "key_press 1"),
                    Inp("scroll_wheel_list_1", "key_press 2")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);

            var listSkips = p.Report.Entries
                .Where(e => e.ReasonKey == TranslationReasons.ScrollWheelModeNotSupported)
                .ToList();
            Assert.Equal(2, listSkips.Count);
            Assert.Contains(listSkips, e => e.SourcePath.EndsWith("/scroll_wheel_list_0"));
            Assert.Contains(listSkips, e => e.SourcePath.EndsWith("/scroll_wheel_list_1"));
            // The wheel itself still lowers onto the drag row.
            Assert.Single(p.KbmMappingSet.Rows);
        }
    }
}
