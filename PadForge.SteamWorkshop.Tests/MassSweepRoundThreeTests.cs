using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Translator v26 pins: wild-corpus round three. The
    /// gravity-lean channel (gyro-hosted dpad wedges, the deflection
    /// joystick mode, lean-hosted menus), the capsense reads (stick tops,
    /// grips, enum bits 44-47), the mobile-touch and chord-without-partner
    /// approved classes, the physical-dpad edge / click any-direction
    /// read, the trackpad edge ring, the touch-surface flick stick, the
    /// stick mouse_region engage, the button-pair hotbar grid, and the
    /// gated-wedge / pulse-gesture activator hosts.</summary>
    public class MassSweepRoundThreeTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V26\"\n";
        private const string HeadPs5 = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"V26\"\n\t\"controller_type\"\t\"controller_ps5\"\n";

        private static string Group(int id, string mode, string inputsAndSettings = "")
            => $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n{inputsAndSettings}\t}}\n";

        private static string Inputs(params string[] members)
            => "\t\t\"inputs\"\n\t\t{\n" + string.Concat(members) + "\t\t}\n";

        private static string Settings(params (string Key, string Value)[] kvs)
        {
            var sb = new System.Text.StringBuilder("\t\t\"settings\"\n\t\t{\n");
            foreach (var (k, v) in kvs)
                sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
            sb.Append("\t\t}\n");
            return sb.ToString();
        }

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

        // ─── Gyro-hosted dpad: lean wedges ──────────────────────────────

        [Fact]
        public void GyroDpad_LowersOntoLeanWedges_WithTheStickTable()
        {
            // Wild witness 707592150: gyro dpad members bind keys. The
            // wedge table mirrors the stick-as-dpad table exactly: north =
            // Lean Y negative half (tilt the top edge away = push the
            // "stick" forward), south = positive (nose up = pull back),
            // east / west = Lean X halves.
            string vdf = Head
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "key_press W"),
                    Inp("dpad_south", "key_press S"),
                    Inp("dpad_east", "key_press D"),
                    Inp("dpad_west", "key_press A")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);

            var leanSources = p.KbmMappingSet.Rows
                .SelectMany(r => r.Sources)
                .Where(s => (s.Descriptor ?? "").StartsWith("Gyro Lean"))
                .ToList();
            Assert.Equal(4, leanSources.Count);
            Assert.All(leanSources, s => Assert.True(s.HalfAxis));
            // Unauthored deadzone: the 22.5-degree tilt default.
            Assert.All(leanSources, s => Assert.Equal(25, s.DeadZone));
            var shapes = leanSources
                .Select(s => (s.Descriptor, s.Invert))
                .OrderBy(t => t.Descriptor).ThenBy(t => t.Invert)
                .ToArray();
            Assert.Equal(new[]
            {
                ("Gyro Lean X", false), // east
                ("Gyro Lean X", true),  // west
                ("Gyro Lean Y", false), // south (nose up = pull back)
                ("Gyro Lean Y", true),  // north (tilt away = push forward)
            }, shapes);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void GyroDpad_AuthoredDeadzone_OverridesTheTiltDefault()
        {
            // 707592150-style authored deadzone 14010 = 43% = a 38-degree
            // wedge.
            string vdf = Head
                + Group(1, "dpad",
                    Inputs(Inp("dpad_east", "key_press E"))
                    + Settings(("deadzone", "14010")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal(43, src.DeadZone);
        }

        [Fact]
        public void GyroFourButtons_FoldOntoLeanSeats()
        {
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "key_press 1")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal("Gyro Lean Y", src.Descriptor); // A = south seat
            Assert.True(src.HalfAxis);
            Assert.False(src.Invert);
        }

        // ─── gyro_to_joystick / _deflection ─────────────────────────────

        [Fact]
        public void GyroToJoystick_EmitsTheRatePair_OntoTheThumbAxes()
        {
            string vdf = Head
                + Group(1, "gyro_to_joystick", Inputs())
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisY");
            Assert.Equal("Gyro Yaw", Assert.Single(x.Sources).Descriptor);
            Assert.Equal("Gyro Pitch", Assert.Single(y.Sources).Descriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        [Fact]
        public void GyroToJoystickDeflection_EmitsTheLeanPair()
        {
            string vdf = Head
                + Group(1, "gyro_to_joystick_deflection",
                    Inputs() + Settings(("output_joystick", "1")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisY");
            var sx = Assert.Single(x.Sources);
            var sy = Assert.Single(y.Sources);
            Assert.Equal("Gyro Lean X", sx.Descriptor);
            Assert.Equal("Gyro Lean Y", sy.Descriptor);
            // Full deflection at 45 degrees of tilt (the JSM motion-stick
            // envelope): Sensitivity 2.0 on the 90-degree lean scale.
            Assert.Equal(2.0, sx.Sensitivity, 3);
            Assert.Equal(2.0, sy.Sensitivity, 3);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownGroupMode);
        }

        // ─── Capsense ───────────────────────────────────────────────────

        [Fact]
        public void GripCapsense_SwitchMembers_ReadTheGripTouches()
        {
            // Wild witness 3722524382: button_leftauxcapsense hosts layer
            // verbs; here a plain key binding pins the read.
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("button_leftauxcapsense", "key_press E"),
                    Inp("button_rightauxcapsense", "key_press R")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.KbmMappingSet.Rows, r =>
                r.Sources.Any(s => s.Descriptor == "Gamepad LeftGripTouch"));
            Assert.Contains(p.KbmMappingSet.Rows, r =>
                r.Sources.Any(s => s.Descriptor == "Gamepad RightGripTouch"));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void StickTouch_InAJoystickMouseGroup_ReadsTheStickTopCapsense()
        {
            // Wild witness 3705891700: right_joystick joystick_mouse touch
            // member.
            string vdf = Head
                + Group(1, "joystick_mouse", Inputs(Inp("touch", "mouse_button MIDDLE")))
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.KbmMappingSet.Rows, r =>
                r.Sources.Any(s => s.Descriptor == "Gamepad RightStickTouch"));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void GyroButton_CapsenseBit_GroundsOnTheCapsenseRead()
        {
            // gyro_button 46 = CapSenseLeftStick in the shipped enum.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Inputs() + Settings(("gyro_button", "46")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad LeftStickTouch", p.GyroEngageDescriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.GyroButtonMaskDropped);
        }

        [Fact]
        public void Chord_CapsenseButton_GatesOnTheGripTouch()
        {
            // chord_button 44 = CapSenseLeftAux (the left grip).
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "key_press R", "chord",
                        ActSettings(("chord_button", "44")))))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal("Gamepad LeftShoulder",
                Assert.Single(p.KbmMappingSet.Rows).Sources[0].Descriptor);
            GateAssert.Gated(p.KbmMappingSet, "Gamepad LeftShoulder", "Gamepad LeftGripTouch");
        }

        // ─── Physical dpad edge / click / scroll ────────────────────────

        [Fact]
        public void DpadEdgeAndClick_ReadTheAnyDirectionPov()
        {
            // A pressed dpad IS at the edge and IS clicked.
            string vdf = Head
                + Group(1, "dpad", Inputs(
                    Inp("edge", "key_press LEFT_SHIFT"),
                    Inp("click", "key_press SPACE")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Equal(2, p.KbmMappingSet.Rows.Count(r =>
                r.Sources.Any(s => s.Descriptor == "POV 0 Any")));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void DpadScrollwheel_FoldsOntoTheRotationSideDirections()
        {
            // Wild witness 2805114063: a hand-authored scrollwheel on the
            // physical dpad.
            string vdf = Head
                + Group(1, "scrollwheel", Inputs(
                    Inp("scroll_clockwise", "key_press W")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows);
            Assert.Equal("POV 0 Right", Assert.Single(row.Sources).Descriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        // ─── Trackpad edge ring ─────────────────────────────────────────

        [Fact]
        public void TrackpadEdge_SinglePadHalf_RidesTheWindowedRing()
        {
            string vdf = HeadPs5
                + Group(1, "dpad",
                    Inputs(Inp("edge", "key_press E"))
                    + Settings(("edge_binding_radius", "24999"), ("edge_binding_invert", "1")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            var row = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey45");
            var src = Assert.Single(row.Sources);
            Assert.Equal("Touchpad 0 Finger 0 Ring Right", src.Descriptor);
            Assert.True(src.Invert);
            Assert.Equal(76, src.DeadZone); // 24999/32767
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.MouseRegionTuningDropped);
        }

        // ─── Stick mouse_region engage ──────────────────────────────────

        [Fact]
        public void StickMouseRegion_NonIdentityGeometry_EmitsTheEngagedClamp()
        {
            // Wild witness 1216423479: joystick-hosted regions with scale.
            string vdf = Head
                + Group(1, "mouse_region", Inputs() + Settings(("scale", "22")))
                + Preset(0, "Default", (1, "joystick active"))
                + "}\n";
            var p = Translate(vdf);
            var clamp = Assert.Single(p.Macros,
                m => m.Action == TranslatedMacroAction.MouseLimitRegion);
            Assert.Contains("Gamepad LeftStickRing", clamp.TriggerInputDescriptors);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.NoDeviceFreeTrigger);
        }

        [Fact]
        public void MemberlessIdentityRegion_OnAButtonHost_IsAProvableNoOp()
        {
            // Wild witness 2837961678: empty-inputs mouse_region parked on
            // the face diamond with no geometry keys. Scale 100 centered =
            // the whole screen; clamping to the whole screen changes
            // nothing, so the group lowers silently.
            string vdf = Head
                + Group(1, "mouse_region", Inputs() + Settings(("output_joystick", "3")))
                + Preset(0, "Default", (1, "button_diamond active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Empty(p.Macros);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.NoDeviceFreeTrigger);
        }

        // ─── Hotbar on button-pair hosts ────────────────────────────────

        [Fact]
        public void Hotbar_OnTheDpad_BuildsAButtonPairGrid()
        {
            // Wild witnesses 3256894758 / 3371006087 (dpad) and
            // 2793649185 (diamond).
            string vdf = Head
                + Group(1, "hotbar", Inputs(
                    Inp("touch_menu_button_0", "key_press 1"),
                    Inp("touch_menu_button_1", "key_press 2")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var menu = Assert.Single(p.Menus);
            Assert.Equal(PadForge.Engine.Menus.MenuKind.Grid, menu.Kind);
            Assert.Equal("Gamepad DPad", menu.HostDescriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.MenuSurfaceNotSupported);
        }

        // ─── Gated-wedge and pulse-gesture activator hosts ──────────────

        [Fact]
        public void WedgeFullPress_ChangePreset_LowersWithTheContactGate()
        {
            // Wild witness 2890096314: dpad_south/Full_Press CHANGE_PRESET
            // on a single-pad trackpad dpad group. The wedge is the anchor
            // D-pad gesture gated on its half's contact; the activator
            // carries both legs (Kind=Chord: gesture bool + gate).
            string vdf = HeadPs5
                + Group(1, "dpad", Inputs(
                    Inp("dpad_south", "controller_action CHANGE_PRESET 2 0 0")))
                + Group(2, "switches", Inputs(Inp("button_menu", "key_press E")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + Preset(1, "Alt", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorInputNotSupported);
            var act = p.XboxMappingSet.ShiftActivators
                .Concat(p.KbmMappingSet.ShiftActivators)
                .First(a => a.Descriptor == "Touchpad 0 DPadDown");
            Assert.Equal("Chord", act.Kind);
            // requires_click defaults ON for dpad groups, so the gate is
            // the half-windowed click (click + half in one composed read).
            Assert.Equal("Touchpad 0 Click Left", act.ChordSecondDescriptor);
        }

        [Fact]
        public void DoubleTapGesture_ChangePreset_LowersAsAPulseHost()
        {
            // Wild witness 2890096314: doubletap/Full_Press CHANGE_PRESET
            // on a mouse_joystick group. The tap gesture is a one-shot
            // pulse, so it hosts the press-edge jump (the flick-host rule).
            string vdf = Head
                + Group(1, "mouse_joystick", Inputs(
                    Inp("doubletap", "controller_action CHANGE_PRESET 2 0 0")))
                + Group(2, "switches", Inputs(Inp("button_menu", "key_press E")))
                + Preset(0, "Default", (1, "right_trackpad active"))
                + Preset(1, "Alt", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorInputNotSupported);
            Assert.Contains(p.XboxMappingSet.ShiftActivators
                .Concat(p.KbmMappingSet.ShiftActivators),
                a => a.Descriptor == "Touchpad 1 DoubleTap");
        }

        // ─── Chord double gate (Gate2) ──────────────────────────────────

        [Fact]
        public void Chord_OnAGatedWedge_RidesTheSecondCompanion()
        {
            // Wild witness 3290233831: single-pad trackpad D-pad wedges
            // chorded with the right bumper. The half-contact gate keeps
            // the primary slot; the partner rides Gate2.
            string vdf = HeadPs5
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "mouse_wheel SCROLL_UP", "chord",
                        ActSettings(("chord_button", "2")))))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownActivatorType);
            // requires_click defaults ON for dpad groups, so the direction
            // carries TWO gates: the half-windowed click and the chord
            // partner. Both are real sources, ANDed in order.
            GateAssert.Gated(p.KbmMappingSet, "Touchpad 0 DPadUp",
                "Touchpad 0 Click Left", "Gamepad RightShoulder");
        }

        // ─── Analog activator ───────────────────────────────────────────

        [Fact]
        public void AnalogActivator_LowersAsTheLiveMagnitudePress()
        {
            // Wild witness 3667416983: "analog" activators on the bumpers
            // driving xinput shoulders. "Analog Activator produces an
            // analog output" (shipped string): the normal walk's held-state
            // row IS that contract on a digital host.
            string vdf = Head
                + Group(1, "switches", Inputs(
                    Inp("left_bumper", "xinput_button SHOULDER_LEFT", "analog")))
                + Preset(0, "Default", (1, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "LeftShoulder");
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownActivatorType);
        }

        // ─── Mobile class boundaries ────────────────────────────────────

        [Fact]
        public void MobileTokens_AreExactlyTheFive()
        {
            Assert.True(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro5"));
            Assert.True(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro6"));
            Assert.True(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro7"));
            Assert.True(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro1finger"));
            Assert.True(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro2finger"));
            Assert.False(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_macro4"));
            Assert.False(PhysicalSlotResolver.IsMobileTouchOnlyToken("button_capture"));
        }
    }
}
