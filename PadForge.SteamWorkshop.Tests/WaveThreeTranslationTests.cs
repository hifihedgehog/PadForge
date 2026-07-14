using System.Linq;
using PadForge.Engine;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v4 (Wave 3) edge tests: single-pad trackpad
    /// halves (B-1: ps4/ps5 left_/right_trackpad onto pad 0's
    /// region-windowed sources), the quadrant collapse for
    /// four_buttons-on-trackpad (B-19), per-row touchpad mouse sensitivity,
    /// and the device-free InputDevice macro triggers that replaced the
    /// NoDeviceFreeTrigger skips. Corpus coverage rides the goldens (the
    /// PS fixtures, 1150803559 above all); these tests pin the per-branch
    /// contracts.</summary>
    public class WaveThreeTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 43)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string HeadPs4 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Edge\"\n\t\"controller_type\"\t\"controller_ps4\"\n";
        private const string HeadDeck = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n"
            + "\t\"title\"\t\"Edge\"\n\t\"controller_type\"\t\"controller_neptune\"\n";

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

        // ─── B-1: single-pad token mapping ──────────────────────────────

        [Fact]
        public void SinglePadTypes_GroundedOnSdlTouchpadCounts()
        {
            // SDL registers ONE touchpad for ps4/ps5 (SDL_hidapi_ps4.c:732,
            // SDL_hidapi_ps5.c:846) and TWO for the multi-pad family
            // (SDL_hidapi_steam.c, SDL_hidapi_steamdeck.c,
            // SDL_hidapi_steam_triton.c).
            Assert.True(PhysicalSlotResolver.UsesSinglePadTrackpads("controller_ps4"));
            Assert.True(PhysicalSlotResolver.UsesSinglePadTrackpads("controller_ps5"));
            Assert.False(PhysicalSlotResolver.UsesSinglePadTrackpads("controller_neptune"));
            Assert.False(PhysicalSlotResolver.UsesSinglePadTrackpads("controller_steamcontroller_gordon"));
            Assert.False(PhysicalSlotResolver.UsesSinglePadTrackpads("controller_triton"));
            Assert.False(PhysicalSlotResolver.UsesSinglePadTrackpads(""));   // typeless = SC era
            Assert.False(PhysicalSlotResolver.UsesSinglePadTrackpads(null));
        }

        [Fact]
        public void Ps4_RightTrackpadMouse_RidesPadZeroRightHalf()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", "\t\t\"inputs\"\n\t\t{\n\t\t}\n")
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Touchpad 0 Finger 0 X Right", Assert.Single(x.Sources).Descriptor);
            var y = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseY");
            Assert.Equal("Touchpad 0 Finger 0 Y Right", Assert.Single(y.Sources).Descriptor);
        }

        [Fact]
        public void Ps4_CenterTrackpad_IsPadZeroWhole()
        {
            string vdf = HeadPs4
                + Group(1, "absolute_mouse", "\t\t\"inputs\"\n\t\t{\n\t\t}\n")
                + Preset(0, "Default", (1, "center_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Touchpad 0 Finger 0 X", Assert.Single(x.Sources).Descriptor);
        }

        [Fact]
        public void Deck_RightTrackpad_KeepsPadOne()
        {
            string vdf = HeadDeck
                + Group(1, "absolute_mouse", "\t\t\"inputs\"\n\t\t{\n\t\t}\n")
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal("Touchpad 1 Finger 0 X", Assert.Single(x.Sources).Descriptor);
        }

        [Fact]
        public void TouchpadMouseSensitivity_NowRidesTheRow_Clean()
        {
            // Trackpad mouse baseline is 50; sensitivity 100 = 2.0x on the
            // row's generic Sensitivity (B-13 made the finger reads honor
            // it), replacing the old TouchpadTuningNotPerRow drop.
            string vdf = HeadDeck
                + Group(1, "absolute_mouse",
                    "\t\t\"inputs\"\n\t\t{\n\t\t}\n" + Settings(("sensitivity", "100")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Equal(2.0, Assert.Single(x.Sources).Sensitivity, 3);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TouchpadTuningNotPerRow);
            // The row entries themselves are Clean (the remaining Partial
            // is absolute_mouse's own positioning approximation).
            Assert.All(p.Report.Entries.Where(e => e.ReasonKey == TranslationReasons.RowEmitted),
                e => Assert.Equal(TranslationStatus.Clean, e.Status));
        }

        [Fact]
        public void Ps4_HalfTouchMember_IsTheHalfTouchSpot()
        {
            string vdf = HeadPs4
                + Group(1, "single_button", Inputs(Inp("touch", "key_press E")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("KbmKey45", row.Target);
            Assert.Equal("Touchpad 0 TouchLeft", Assert.Single(row.Sources).Descriptor);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TrackpadFeatureRequired
                && e.ReasonArgs.Contains(PhysicalSlotResolver.FeatureTouchSpots));
        }

        [Fact]
        public void Ps4_HalfClickMember_GatesTheSingleClickOnTheHalfSpot()
        {
            string vdf = HeadPs4
                + Group(1, "single_button", Inputs(Inp("click", "key_press E")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("AND", row.CombineMode);
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("Touchpad 0 Click", row.Sources[0].Descriptor);
            Assert.Equal("Touchpad 0 TouchRight", row.Sources[1].Descriptor);
        }

        [Fact]
        public void Ps4_SwitchPadClicks_GateOnTheirHalves()
        {
            string vdf = HeadPs4
                + Group(1, "switches", Inputs(
                    Inp("left_click", "key_press Q"),
                    Inp("right_click", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var q = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey51");
            Assert.Equal("AND", q.CombineMode);
            Assert.Equal("Touchpad 0 Click", q.Sources[0].Descriptor);
            Assert.Equal("Touchpad 0 TouchLeft", q.Sources[1].Descriptor);
            var e = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            Assert.Equal("Touchpad 0 TouchRight", e.Sources[1].Descriptor);
        }

        [Fact]
        public void Deck_SwitchPadClicks_KeepPerPadClicks()
        {
            string vdf = HeadDeck
                + Group(1, "switches", Inputs(
                    Inp("left_click", "key_press Q"),
                    Inp("right_click", "key_press E")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var q = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey51");
            Assert.Equal("Touchpad 0 Click", Assert.Single(q.Sources).Descriptor);
            var e = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            Assert.Equal("Touchpad 1 Click", Assert.Single(e.Sources).Descriptor);
        }

        [Fact]
        public void Ps4_HalfJoystickMove_RidesWindowedAbsoluteReads()
        {
            string vdf = HeadPs4
                + Group(1, "joystick_move", "\t\t\"inputs\"\n\t\t{\n\t\t}\n"
                    + Settings(("output_joystick", "2")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            Assert.Equal("Touchpad 0 Finger 0 X Right", Assert.Single(x.Sources).Descriptor);
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisY");
            Assert.Equal("Touchpad 0 Finger 0 Y Right", Assert.Single(y.Sources).Descriptor);
            // Live by default: no gesture feature required.
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TrackpadFeatureRequired);
        }

        [Fact]
        public void Deck_TrackpadJoystickMove_KeepsGestureStickChannel()
        {
            string vdf = HeadDeck
                + Group(1, "joystick_move", "\t\t\"inputs\"\n\t\t{\n\t\t}\n")
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.Equal("Touchpad 0 StickX", Assert.Single(x.Sources).Descriptor);
        }

        // ─── B-19: four_buttons hosted on a touch surface ───────────────

        [Fact]
        public void Ps4_FourButtonsOnHalf_CollapsesOntoWindowedDown()
        {
            string vdf = HeadPs4
                + Group(1, "four_buttons", Inputs(
                    Inp("button_a", "xinput_button A"),
                    Inp("button_b", "xinput_button B")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var a = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "ButtonA");
            Assert.Equal("Touchpad 0 Finger 0 Down Right", Assert.Single(a.Sources).Descriptor);
            var b = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "ButtonB");
            Assert.Equal("Touchpad 0 Finger 0 Down Right", Assert.Single(b.Sources).Descriptor);
            // The honest quadrant-collapse Partial, once per cell binding.
            Assert.Equal(2, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.TouchQuadrantApproximated
                && e.Status == TranslationStatus.Partial));
        }

        [Fact]
        public void Deck_FourButtonsOnPad_CollapsesOntoWholePadDown()
        {
            string vdf = HeadDeck
                + Group(1, "four_buttons", Inputs(Inp("button_x", "xinput_button X")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "ButtonX");
            Assert.Equal("Touchpad 1 Finger 0 Down", Assert.Single(x.Sources).Descriptor);
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TouchQuadrantApproximated);
        }

        // ─── B-1: honest notes where the half window is dropped ─────────

        [Fact]
        public void Ps4_HalfDpadWedges_NoteTheWholePadApproximation_Once()
        {
            string vdf = HeadPs4
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "key_press W"),
                    Inp("dpad_south", "key_press S"))
                    + Settings(("requires_click", "0")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var w = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey57");
            Assert.Equal("Touchpad 0 DPadUp", Assert.Single(w.Sources).Descriptor);
            Assert.Equal(1, p.Report.Entries.Count(e =>
                e.ReasonKey == TranslationReasons.TrackpadHalfApproximated));
        }

        [Fact]
        public void Ps4_HalfDpadWithOnlySkippedBindings_GetsNoHalfNote()
        {
            // A group whose bindings all skip approximates nothing: the
            // half note stays out of the report.
            string vdf = HeadPs4
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "controller_action SCREENSHOT"))
                    + Settings(("requires_click", "0")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.KbmMappingSet.Rows);
            Assert.DoesNotContain(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TrackpadHalfApproximated);
        }

        // ─── Device-free macro triggers (part 1 conversions) ────────────

        [Fact]
        public void PaddleToggleKey_LatchesOnPaddleDescriptor()
        {
            string vdf = HeadPs4
                + Group(1, "switches", Inputs(
                    Inp("button_back_left", "key_press E",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ToggleKey, m.Action);
            Assert.Equal("Gamepad Paddle2", Assert.Single(m.TriggerInputDescriptors));
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ToggleLatchEmitted);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
        }

        [Fact]
        public void TouchpadTurbo_PulsesOnDescriptorTrigger()
        {
            string vdf = HeadDeck
                + Group(1, "single_button", Inputs(
                    Inp("touch", "xinput_button A",
                        activatorSettings: ActSettings(("hold_repeats", "1"), ("repeat_rate", "40")))))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatVcButtonWhileHeld, m.Action);
            Assert.Equal("WhileHeld", m.TriggerMode);
            Assert.Equal(40, m.IntervalMs);
            Assert.Equal(Gamepad.A, m.TargetXboxButtons);
            Assert.Equal("Touchpad 0 Finger 0 Down", Assert.Single(m.TriggerInputDescriptors));
            Assert.Empty(p.XboxMappingSet.Rows); // the macro replaces the row
            var entry = Assert.Single(p.Report.Entries);
            Assert.Equal(TranslationStatus.Clean, entry.Status);
            Assert.Equal(TranslationReasons.MacroEmitted, entry.ReasonKey);
        }

        [Fact]
        public void Ps4_HalfClickHostedLatch_CarriesClickAndSpotEntries()
        {
            // The AND pair rides the macro trigger too: pad click plus the
            // half's touch spot.
            string vdf = HeadPs4
                + Group(1, "single_button", Inputs(
                    Inp("click", "key_press E",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.ToggleKey, m.Action);
            Assert.Equal(new[] { "Touchpad 0 Click", "Touchpad 0 TouchLeft" },
                m.TriggerInputDescriptors.ToArray());
            // Feature-gated trigger: the latch entry is the honest Partial.
            var entry = Assert.Single(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ToggleLatchEmitted);
            Assert.Equal(TranslationStatus.Partial, entry.Status);
        }

        [Fact]
        public void RequiresClickWedge_MacroTrigger_CarriesTheClickGate()
        {
            // A set_led on a require-click D-pad wedge fires on wedge AND
            // click in Steam; the converted macro's entries AND the same
            // pair (requires_click absent = require, the dpad default).
            string vdf = HeadDeck
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "controller_action set_led 255 0 0 100 100 1")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.SetLightbarColor, m.Action);
            Assert.Equal(new[] { "Touchpad 0 DPadUp", "Touchpad 0 Click" },
                m.TriggerInputDescriptors.ToArray());
        }

        [Fact]
        public void Ps4_HalfMouseRegion_EngagesOnHalfSpot_WithFeatureNote()
        {
            string vdf = HeadPs4
                + Group(1, "mouse_region",
                    "\t\t\"inputs\"\n\t\t{\n\t\t}\n"
                    + Settings(("scale", "25"), ("position_x", "80"), ("position_y", "20")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.MouseLimitRegion, m.Action);
            Assert.Equal("Touchpad 0 TouchRight", Assert.Single(m.TriggerInputDescriptors));
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.TrackpadFeatureRequired
                && e.ReasonArgs.Contains(PhysicalSlotResolver.FeatureTouchSpots));
            Assert.Contains(p.Report.Entries, e =>
                e.ReasonKey == TranslationReasons.MouseRegionApproximated);
        }

        // ─── Slot demand with descriptor-triggered macros ────────────────

        [Fact]
        public void KeyLatchOnDescriptorTrigger_DemandsKbmSlotOnly()
        {
            string vdf = HeadPs4
                + Group(1, "switches", Inputs(
                    Inp("button_back_right", "key_press E",
                        activatorSettings: ActSettings(("toggle", "1")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Single(p.Macros);
            Assert.False(p.NeedsXboxSlot);
            Assert.True(p.NeedsKbmSlot);
        }

        [Fact]
        public void VcActionOnDescriptorTrigger_StillDemandsXboxSlot()
        {
            string vdf = HeadPs4
                + Group(1, "switches", Inputs(
                    Inp("button_back_right", "xinput_button A",
                        activatorSettings: ActSettings(("hold_repeats", "1"), ("repeat_rate", "50")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            var m = Assert.Single(p.Macros);
            Assert.Equal(TranslatedMacroAction.RepeatVcButtonWhileHeld, m.Action);
            Assert.NotEmpty(m.TriggerInputDescriptors);
            Assert.True(p.NeedsXboxSlot); // the action writes a VC button
        }
    }
}
