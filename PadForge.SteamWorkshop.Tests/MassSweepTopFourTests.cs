using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v24 pins: the mass-sweep top four.
    /// button_macro0..4 resolve onto the raw MISC2..MISC6 reads
    /// ("Button 17..21", Steam macro N = SDL misc N+2); the chord
    /// activator lowers to gated bindings (chord_button indexes
    /// k_eGamepadButtonBitMask, the gyro_button value space, and the
    /// partner rides the AND-gate leg end to end); Long_Press impossible
    /// bindings count into their binding's OWN class (game actions feed
    /// the per-preset aggregate, controller_action verbs route through
    /// the canonical walk); and "@"-prefixed menu icons (the
    /// configurator's app-provided namespace) degrade silently to text
    /// labels.</summary>
    public class MassSweepTopFourTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V24\"\n";
        private const string HeadPs5 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V24\"\n\t\"controller_type\"\t\"controller_ps5\"\n";

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

        // ─── button_macro0..4: the raw MISC2..MISC6 reads ───────────────

        [Fact]
        public void ButtonMacro_FirstAndLast_ResolveToRawMiscButtons()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_macro0", "xinput_button A"),
                    Inp("button_macro4", "xinput_button B")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Button 17",
                p.XboxMappingSet.Rows.Single(r => r.Target == "ButtonA").Sources.Single().Descriptor);
            Assert.Equal("Button 21",
                p.XboxMappingSet.Rows.Single(r => r.Target == "ButtonB").Sources.Single().Descriptor);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void ButtonMacro_BeyondSdlMiscSpace_GetsTheMobileTouchClass()
        {
            // v26: macro5..7 and the mobile finger taps exceed SDL's misc
            // space (MISC2..MISC6 hold macro0..4 only) and exist only on
            // the Steam Link touch overlay, so they get the PRECISE
            // mobile-only class, never the generic unknown-input net.
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_macro5", "xinput_button A"),
                    Inp("button_macro2finger", "xinput_button B")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.XboxMappingSet.Rows);
            Assert.Equal(2, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.MobileTouchSurfaceOnly));
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        // ─── chord: gated bindings ──────────────────────────────────────

        [Fact]
        public void Chord_KeyPress_EmitsRowGatedOnThePartner()
        {
            // chord_button 4 = k_eGamepadButtonBitMask ButtonNorth.
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "key_press R", "chord",
                        ActSettings(("chord_button", "4")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("Gamepad LeftShoulder", row.Sources[0].Descriptor);
            GateAssert.Gated(p.KbmMappingSet, "Gamepad LeftShoulder", "Gamepad ButtonY");
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownActivatorType);
        }

        [Fact]
        public void Chord_XinputIdentity_IsNotFoldedAway()
        {
            // A gated read is not the automap's plain read: chord A-on-A
            // must emit an explicit gated row, never the identity fold.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button A", "chord",
                        ActSettings(("chord_button", "3")))))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "ButtonA");
            Assert.Equal("Gamepad ButtonA", row.Sources[0].Descriptor);
            GateAssert.Gated(p.XboxMappingSet, "Gamepad ButtonA", "Gamepad LeftShoulder");
        }

        [Fact]
        public void Chord_MacroShapedBinding_CarriesBothTriggerLegs()
        {
            // SCREENSHOT lowers to a silent KeyTap macro on a descriptor
            // trigger; the chord partner must ride as a second gate entry
            // (the combined-output identity is stripped: the output bit
            // fires partner-or-not).
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("right_bumper", "controller_action SCREENSHOT", "chord",
                        ActSettings(("chord_button", "1")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var macro = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.KeyTap, macro.Action);
            Assert.Equal(0, (int)macro.TriggerXboxButtons);
            Assert.Equal(new[] { "Gamepad RightShoulder", "Gamepad LeftTrigger" },
                macro.TriggerInputDescriptors);
        }

        [Fact]
        public void Chord_LayerVerb_LowersToKindChordActivator()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_escape", "controller_action HOLD_LAYER 2", "chord",
                        ActSettings(("chord_button", "13")))))
                + Group(2, "four_buttons", Inputs(Inp("button_b", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + Preset(1, "Alt", (2, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            var act = Assert.Single(p.KbmMappingSet.ShiftActivators);
            Assert.Equal("Chord", act.Kind);
            Assert.Equal("Gamepad ButtonStart", act.Descriptor);
            Assert.Equal("Gamepad ButtonGuide", act.ChordSecondDescriptor);
        }

        [Fact]
        public void Chord_SinglePadHalfClickHost_FoldsIntoTheWindowedClick()
        {
            // The half click's own touch-spot gate folds into the v18
            // windowed click read, freeing the gate slot for the partner.
            string vdf = HeadPs5
                + Group(1, "absolute_mouse", Inputs(
                    Inp("click", "key_press R", "chord",
                        ActSettings(("chord_button", "4")))))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            // The absolute_mouse mode emits its own pointer rows; find the
            // chord row by its folded source.
            GateAssert.Gated(p.KbmMappingSet, "Touchpad 0 Click Right", "Gamepad ButtonY");
        }

        [Fact]
        public void Chord_ZeroButton_IsTheUnsetSentinel_GetsTheChordWithoutPartnerClass()
        {
            // 0 in the shared value space is the gyro_button none/default
            // sentinel; no corpus chord authors it and the serializer
            // omits defaults, so 0 = an unset picker, not an RT chord.
            // v26: that arm is the PRECISE config-error class (not even
            // Steam can fire a chord with no partner).
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "key_press R", "chord",
                        ActSettings(("chord_button", "0")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.ChordWithoutPartner);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownActivatorType);
        }

        [Fact]
        public void Chord_OutOfEnumButton_KeepsTheNamedSkip()
        {
            // 21 is an enum hole (no k_eGamepadButtonBitMask member).
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "key_press R", "chord",
                        ActSettings(("chord_button", "21")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.UnknownActivatorType);
            Assert.Equal("chord", entry.ReasonArgs[0]);
        }

        // ─── Long_Press: impossible bindings keep their own class ───────

        [Fact]
        public void LongPress_GameAction_FeedsThePresetAggregate()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "game_action GameControls lookatweapon", "Long_Press")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LongPressNotSupported);
            var agg = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GameActionsNotSupported);
            Assert.Equal("1", agg.ReasonArgs[0]);
        }

        [Fact]
        public void LongPress_GameActionAnalog_FeedsTheSameAggregate()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "game_action_analog Set_InGame SteamInput_R2", "Long_Press")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LongPressNotSupported);
            Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.GameActionsNotSupported);
        }

        [Fact]
        public void LongPress_SteamClientVerb_KeepsItsOwnClass()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "controller_action gr_toggle", "Long_Press")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.LongPressNotSupported);
            var entry = Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.SteamSystemAction);
            Assert.Equal("gr_toggle", entry.ReasonArgs[0]); // args carry the authored spelling
            // Nothing lowered, so no haptic pulse may tick for it.
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void LongPress_ShowKeyboard_FiresAtTheHoldThreshold()
        {
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "controller_action SHOW_KEYBOARD", "Long_Press",
                        ActSettings(("long_press_time", "350")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var macro = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ShowOnScreenKeyboard, macro.Action);
            Assert.Equal("HoldForMs", macro.TriggerMode);
            Assert.Equal(350, macro.TriggerHoldMs);
        }

        // ─── "@" menu icons: silent label fallback ──────────────────────

        private static string MenuCell(string name, string binding)
            => Inp(name, binding);

        [Fact]
        public void MenuIcon_AppProvided_DegradesSilentlyToTheLabel()
        {
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    MenuCell("touch_menu_button_0",
                        "key_press LEFT_SHIFT, Fist, @gesture_fist.png, #232323 #E4E4E4")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuIconUnresolved);
            var menu = Assert.Single(p.Menus);
            var item = Assert.Single(menu.Items);
            Assert.Equal("", item.Icon);
            Assert.Equal("Fist", item.Label);
        }

        [Fact]
        public void MenuIcon_MalformedReference_StillNamed()
        {
            string vdf = Head
                + Group(1, "touch_menu", Inputs(
                    MenuCell("touch_menu_button_0",
                        "key_press LEFT_SHIFT, Fist, sub\\\\dir\\\\evil.png, #232323 #E4E4E4")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Single(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MenuIconUnresolved);
        }
    }
}
