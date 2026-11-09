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

        private static string TiltPair(int deadzone)
            => "Gyro Tilt " + System.Math.Round(deadzone / 32767.0 * 90.0, 4)
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + " 90";

        [Fact]
        public void GyroDpad_ReadsSteamsWedges_OnTheTiltPastTheDeadZonesAngle()
        {
            // Wild witness 707592150: gyro dpad members bind keys. Steam's
            // dpad on the gyro (steamclient64.dll 0x1388b2b90) reads the
            // tilt past deadzone / 32767 * 90 degrees (27.5 by default) out to
            // 90 and engages and picks the direction as on a stick. With no
            // gyro button key the Steam Controller's right pad touch turns
            // the gyro on, so the reads rest while it is off.
            string vdf = Head
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "key_press W"),
                    Inp("dpad_south", "key_press S"),
                    Inp("dpad_east", "key_press D"),
                    Inp("dpad_west", "key_press A")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            string pair = TiltPair(10000);
            var reads = p.KbmMappingSet.Rows.SelectMany(r => r.Sources).Select(s => s.Descriptor).OrderBy(d => d).ToArray();
            Assert.Equal(new[] { 0.0, 90.0, 180.0, 270.0 }
                .Select(b => SteamPadExpect.Wedge(b, PadForge.Engine.Common.Mapping.BoundedGate.Engaged, pair))
                .OrderBy(d => d).ToArray(), reads);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroRollFromHorizon);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("45", Assert.Single(pitch.ReasonArgs));
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownPhysicalInput);
        }

        [Fact]
        public void GyroDpad_AuthoredDeadzone_SetsTheTiltAngleAndTheEngage()
        {
            // 707592150-style authored deadzone 14010: the tilt reads past
            // 38.5 degrees, and the dpad engages past 14010 plus 1.25 percent.
            string vdf = Head
                + Group(1, "dpad",
                    Inputs(Inp("dpad_east", "key_press E"))
                    + Settings(("deadzone", "14010"), ("gyro_ratchet_button_mask", "0")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var src = Assert.Single(Assert.Single(p.KbmMappingSet.Rows).Sources);
            Assert.Equal(SteamPadExpect.Wedge(0, PadForge.Engine.Common.Mapping.BoundedGate.None, TiltPair(14010), deadzone: 14010),
                src.Descriptor);
        }

        [Fact]
        public void GyroFourButtons_PressesPast22AndAHalfDegrees_TheNorthCellOnTheDpadsSouthTilt()
        {
            // Steam's four_buttons on the gyro (0x1388b6b99) presses a cell
            // past a fixed 22.5 degrees whatever the dead zone, its north cell
            // (Y) for the tilt the gyro dpad calls south and its south cell
            // (A) for the dpad's north.
            string vdf = Head
                + Group(1, "four_buttons", Inputs(Inp("button_a", "key_press 1"), Inp("button_y", "key_press 2"))
                    + Settings(("deadzone", "14010")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var a = Assert.Single(Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey31").Sources);
            Assert.Equal("Gyro Lean Y", a.Descriptor);
            Assert.True(a.HalfAxis);
            Assert.True(a.Invert);
            Assert.Equal(25, a.DeadZone);
            var y = Assert.Single(Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmKey32").Sources);
            Assert.False(y.Invert);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("45", Assert.Single(pitch.ReasonArgs));
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
        public void GyroToJoystickDeflection_EmitsTheTiltPair()
        {
            // output_joystick 1 is the right stick (the configurator's
            // LeftJoystick 0, RightJoystick 1).
            string vdf = Head
                + Group(1, "gyro_to_joystick_deflection",
                    Inputs() + Settings(("output_joystick", "1")))
                + Preset(0, "Default", (1, "gyro active"))
                + "}\n";
            var p = Translate(vdf);
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX");
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisY");
            var sx = x.Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            var sy = y.Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            Assert.Equal("Gyro Tilt X", sx.Descriptor);
            Assert.Equal("Gyro Tilt Y", sy.Descriptor);
            // Full deflection at the client's default maximum of 45
            // degrees, nothing inside its default minimum of 2.
            Assert.Equal(45.0, sx.ParamTiltRangeDeg, 3);
            Assert.Equal(2.0, sx.ParamTiltInnerDz, 3);
            Assert.Equal(45.0, sy.ParamTiltRangeDeg, 3);
            Assert.Equal(2.0, sy.ParamTiltInnerDz, 3);
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
            // gyro_button 21 = GyroLStickTouch in the configurator's legacy
            // list, which the client converts to mask bit 46, the left
            // stick's capacitive touch.
            string vdf = Head
                + Group(1, "gyro_to_mouse", Inputs() + Settings(("gyro_button", "21")))
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
            Assert.Equal(SteamPadExpect.Edge("Touchpad 0 Finger 0 Ring Right", 24999, inverted: true), src.Descriptor);
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
        public void Hotbar_OnTheDpad_BuildsAHotbar()
        {
            // Wild witnesses 3256894758 / 3371006087 (dpad) and
            // 2793649185 (diamond). SteamHotbarImportTests pins the rest.
            string vdf = Head
                + Group(1, "hotbar", Inputs(
                    Inp("touch_menu_button_0", "key_press 1"),
                    Inp("touch_menu_button_1", "key_press 2")))
                + Preset(0, "Default", (1, "dpad active"))
                + "}\n";
            var p = Translate(vdf);
            var menu = Assert.Single(p.Menus);
            Assert.Equal(PadForge.Engine.Menus.MenuKind.Hotbar, menu.Kind);
            Assert.Equal("Gamepad DPad", menu.HostDescriptor);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.MenuSurfaceNotSupported);
        }

        // ─── Gated-wedge and pulse-gesture activator hosts ──────────────

        [Fact]
        public void WedgeFullPress_ChangePreset_LowersWithTheContactGate()
        {
            // Wild witness 2890096314: dpad_south/Full_Press CHANGE_PRESET
            // on a single-pad trackpad dpad group. The activator reads Steam's
            // dpad direction on the half, the click (on by default for a
            // trackpad dpad) inside the read.
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
                .Single(a => a.Descriptor == SteamPadExpect.Wedge(270, PadForge.Engine.Common.Mapping.BoundedGate.Click, "Touchpad 0 Finger 0 Ring Left"));
            Assert.NotEqual("Chord", act.Kind);
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
            // Wild witness 3290233831: single-pad trackpad D-pad
            // directions chorded with the right bumper. The direction's read
            // carries its click, and the bumper ANDs in as the gate.
            string vdf = HeadPs5
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "mouse_wheel SCROLL_UP", "chord",
                        ActSettings(("chord_button", "2")))))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.UnknownActivatorType);
            GateAssert.Gated(p.KbmMappingSet, SteamPadExpect.Wedge(90, PadForge.Engine.Common.Mapping.BoundedGate.Click, "Touchpad 0 Finger 0 Ring Left"),
                "Gamepad RightShoulder");
        }

        [Fact]
        public void ALayerVerbOnAGatedChordKeepsThePartnerToo()
        {
            // The same wild shape as the row case above, with a layer verb
            // instead of a scroll. The activator ANDs the direction's read,
            // which carries its click, with the bumper.
            string vdf = HeadPs5
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "controller_action HOLD_LAYER 2 0 0", "chord",
                        ActSettings(("chord_button", "2")))))
                + Group(2, "switches", Inputs(Inp("button_menu", "key_press E")))
                + Preset(0, "Default", (1, "left_trackpad active"))
                + Preset(1, "Alt", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);
            Assert.DoesNotContain(p.Report.Entries,
                e => e.ReasonKey == TranslationReasons.ActivatorInputNotSupported);

            var act = p.XboxMappingSet.ShiftActivators
                .Concat(p.KbmMappingSet.ShiftActivators)
                .Single(a => a.Descriptor == SteamPadExpect.Wedge(90, PadForge.Engine.Common.Mapping.BoundedGate.Click, "Touchpad 0 Finger 0 Ring Left"));
            Assert.Equal("Chord", act.Kind);
            Assert.Equal("Gamepad RightShoulder", act.ChordSecondDescriptor);
        }

        [Fact]
        public void BothHalvesKeepTheirOwnChordPartner()
        {
            // Two directions on one physical pad, one per half, each chorded
            // with a different shoulder. Each half reads its own direction
            // and keeps its own partner.
            string vdf = HeadPs5
                + Group(1, "dpad", Inputs(
                    Inp("dpad_north", "controller_action HOLD_LAYER 2 0 0", "chord",
                        ActSettings(("chord_button", "2")))))
                + Group(3, "dpad", Inputs(
                    Inp("dpad_north", "controller_action HOLD_LAYER 2 0 0", "chord",
                        ActSettings(("chord_button", "3")))))
                + Group(2, "switches", Inputs(Inp("button_menu", "key_press E")))
                + Preset(0, "Default", (1, "left_trackpad active"), (3, "right_trackpad active"))
                + Preset(1, "Alt", (2, "switch active"))
                + "}\n";
            var p = Translate(vdf);

            var acts = p.XboxMappingSet.ShiftActivators.Concat(p.KbmMappingSet.ShiftActivators).ToList();
            var left = Assert.Single(acts, a => a.Descriptor == SteamPadExpect.Wedge(90,
                PadForge.Engine.Common.Mapping.BoundedGate.Click, "Touchpad 0 Finger 0 Ring Left"));
            var right = Assert.Single(acts, a => a.Descriptor == SteamPadExpect.Wedge(90,
                PadForge.Engine.Common.Mapping.BoundedGate.Click, "Touchpad 0 Finger 0 Ring Right"));
            Assert.Equal("Gamepad RightShoulder", left.ChordSecondDescriptor);
            Assert.Equal("Gamepad LeftShoulder", right.ChordSecondDescriptor);
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
