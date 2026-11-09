using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Where a Steam group's pair goes, and how a gyro tilt group
    /// reads, as the Steam client does it (steamclient64.dll). The stick
    /// classes send output_joystick 0 to the left stick's axes and 1 to the
    /// right's, and joystick_move and joystick_camera send 2 to the mouse.
    /// A file that leaves the key out gets the mode's default:
    /// joystick_move and joystick_camera start on the input's own side, the
    /// gyro included on the left, mouse_joystick and gyro_to_joystick on
    /// the right stick, and gyro_to_joystick_deflection on the left. The
    /// configurator's choice lists (steamui/sp.js) and Valve's templates
    /// agree.</summary>
    public class OutputJoystickTranslationTests
    {
        private static TranslatedProfile Translate(string vdf, long fileId = 42)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = fileId });
        }

        private const string Head = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Out\"\n";

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

        // A config with no binding at all translates to nothing, so each
        // test config also carries an A button on the face buttons.
        private const string FaceA =
            "\t\t\"inputs\"\n\t\t{\n\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button A\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n";

        private static TranslatedProfile One(string mode, string source, params (string Key, string Value)[] settings)
            => Translate(Head
                + Group(1, mode, settings.Length > 0 ? Settings(settings) : "")
                + Group(2, "four_buttons", FaceA)
                + Preset(0, "Default", (1, source + " active"), (2, "button_diamond active"))
                + "}\n");

        private static string[] Targets(TranslatedProfile p)
            => p.XboxMappingSet.Rows.Where(r => r.Sources.Count > 0).Select(r => r.Target)
                .Concat(p.KbmMappingSet.Rows.Where(r => r.Sources.Count > 0).Select(r => r.Target))
                .OrderBy(t => t, System.StringComparer.Ordinal).ToArray();

        // ─── joystick_move ──────────────────────────────────────────────

        [Theory]
        [InlineData("joystick", null, "LeftThumbAxisX", "Gamepad LeftStickX")]
        [InlineData("right_joystick", null, "RightThumbAxisX", "Gamepad RightStickX")]
        [InlineData("joystick", "0", "LeftThumbAxisX", "Gamepad LeftStickX")]
        [InlineData("joystick", "1", "RightThumbAxisX", "Gamepad LeftStickX")]
        [InlineData("right_joystick", "0", "LeftThumbAxisX", "Gamepad RightStickX")]
        [InlineData("right_joystick", "1", "RightThumbAxisX", "Gamepad RightStickX")]
        public void JoystickMove_SendsTheStickWhereSteamDoes(string source, string output,
            string target, string descriptor)
        {
            var p = output == null
                ? One("joystick_move", source)
                : One("joystick_move", source, ("output_joystick", output));
            var row = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == target);
            Assert.Equal(descriptor, Assert.Single(row.Sources).Descriptor);
            string other = target.StartsWith("Left") ? "RightThumbAxisX" : "LeftThumbAxisX";
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == other && r.Sources.Count > 0);
        }

        [Theory]
        [InlineData("joystick")]
        [InlineData("right_joystick")]
        public void JoystickMove_Output2_MovesTheMouse_AsJoystickMouseDoes(string source)
        {
            var asMove = One("joystick_move", source, ("output_joystick", "2"));
            var asMouse = One("joystick_mouse", source, ("output_joystick", "2"));
            Assert.Contains(asMove.KbmMappingSet.Rows, r => r.Target == "KbmMouseX" && r.Sources.Count > 0);
            Assert.DoesNotContain(asMove.XboxMappingSet.Rows, r => r.Target.Contains("ThumbAxis") && r.Sources.Count > 0);
            Assert.Equal(Targets(asMouse), Targets(asMove));
        }

        // The client routes a stick class's pair only for 0, 1 and 2, so any
        // other value moves nothing, and the import names the value.
        [Theory]
        [InlineData("joystick_move", "3")]
        [InlineData("joystick_move", "-1")]
        [InlineData("joystick_camera", "3")]
        [InlineData("mouse_joystick", "2")]
        public void AnUnroutedOutput_MapsNothing_AndNamesTheValue(string mode, string output)
        {
            var p = One(mode, "right_joystick", ("output_joystick", output));
            Assert.Equal(new[] { "ButtonA" }, Targets(p));
            var line = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.OutputJoystickUnrouted);
            Assert.Equal(output, Assert.Single(line.ReasonArgs));
        }

        [Theory]
        [InlineData("right_trackpad", null, "RightThumbAxisX")]
        [InlineData("left_trackpad", null, "LeftThumbAxisX")]
        [InlineData("right_trackpad", "0", "LeftThumbAxisX")]
        [InlineData("left_trackpad", "1", "RightThumbAxisX")]
        public void JoystickMove_OnATrackpad_LandsOnTheSideSteamPicks(string source, string output, string target)
        {
            var p = output == null
                ? One("joystick_move", source)
                : One("joystick_move", source, ("output_joystick", output));
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == target && r.Sources.Count > 0);
            string other = target.StartsWith("Left") ? "RightThumbAxisX" : "LeftThumbAxisX";
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == other && r.Sources.Count > 0);
        }

        [Fact]
        public void JoystickMove_OnATrackpad_Output2_MovesTheMouse()
        {
            var p = One("joystick_move", "right_trackpad", ("output_joystick", "2"));
            Assert.Contains(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX" && r.Sources.Count > 0);
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target.Contains("ThumbAxis") && r.Sources.Count > 0);
        }

        [Fact]
        public void JoystickMove_DeadZoneShape_LandsOnTheStickTheGroupFeeds()
        {
            var p = One("joystick_move", "right_joystick", ("output_joystick", "0"), ("deadzone_shape", "1"));
            Assert.Equal("2", p.LeftStickDeadZoneShape);
            Assert.True(string.IsNullOrEmpty(p.RightStickDeadZoneShape));
        }

        // ─── joystick_move on the gyro: the tilt read ───────────────────

        [Fact]
        public void JoystickMove_OnTheGyro_ReadsTheTiltPair_WithSteamsAngles()
        {
            var p = One("joystick_move", "gyro");
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            var y = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisY");
            var sx = x.Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            var sy = y.Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            Assert.Equal("Gyro Tilt X", sx.Descriptor);
            Assert.Equal("Gyro Tilt Y", sy.Descriptor);
            // Off a stick, the client's outer radius starts at 16000, full
            // deflection 2.8125 + 16000 / 32767 * 87.1875 degrees out, and
            // the inner radius at 0.
            double outer = 2.8125 + 16000.0 / 32767.0 * 87.1875;
            Assert.Equal(outer, sx.ParamTiltRangeDeg, 3);
            Assert.Equal(outer, sy.ParamTiltRangeDeg, 3);
            Assert.Equal(0.0, sx.ParamTiltInnerDz, 3);
            Assert.Equal(0.0, sx.ParamRangeOuter, 3);
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroRollFromHorizon);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("45", Assert.Single(pitch.ReasonArgs));
        }

        [Fact]
        public void JoystickMove_OnTheGyro_ReadsTheFilesRadiiAndNeutral()
        {
            var p = One("joystick_move", "gyro", ("output_joystick", "1"),
                ("deadzone_inner_radius", "3641"), ("deadzone_outer_radius", "32767"),
                ("gyro_neutral", "7373"));
            var sx = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX").Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            Assert.Equal("Gyro Tilt X", sx.Descriptor);
            Assert.Equal(90.0, sx.ParamTiltRangeDeg, 3);
            Assert.Equal(3641.0 / 32767.0 * 90.0, sx.ParamTiltInnerDz, 3);
            Assert.Equal(50, sx.DeadZone);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("20", Assert.Single(pitch.ReasonArgs));
        }

        [Fact]
        public void JoystickMouse_OnTheGyro_MovesTheMouseByTheTilt()
        {
            // The joystick class reads the gyro as a stick position for every
            // output, the mouse included.
            var p = One("joystick_mouse", "gyro");
            var x = Assert.Single(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX");
            Assert.Contains(x.Sources, s => s.Descriptor == "Gyro Tilt X");
            Assert.DoesNotContain(x.Sources, s => s.Descriptor == "Gyro Yaw");
        }

        // ─── joystick_camera ────────────────────────────────────────────

        [Fact]
        public void JoystickCamera_WithNoKey_DrivesTheMatchedStick_NotTheMouse()
        {
            var p = One("joystick_camera", "right_trackpad");
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX" && r.Sources.Count > 0);
            Assert.DoesNotContain(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX" && r.Sources.Count > 0);
            var asMouseJoystick = One("mouse_joystick", "right_trackpad");
            Assert.Equal(Targets(asMouseJoystick), Targets(p));
        }

        [Fact]
        public void JoystickCamera_Output0_OnTheRightStick_DrivesTheLeftStick()
        {
            var p = One("joystick_camera", "right_joystick", ("output_joystick", "0"));
            var x = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.Equal("Gamepad RightStickX", Assert.Single(x.Sources).Descriptor);
        }

        [Fact]
        public void JoystickCamera_Output2_MovesTheMouse()
        {
            var p = One("joystick_camera", "right_trackpad", ("output_joystick", "2"));
            Assert.Contains(p.KbmMappingSet.Rows, r => r.Target == "KbmMouseX" && r.Sources.Count > 0);
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target.Contains("ThumbAxis") && r.Sources.Count > 0);
        }

        // ─── mouse_joystick and gyro_to_joystick: 0 left, 1 right ───────

        [Theory]
        [InlineData("mouse_joystick", null, "Right")]
        [InlineData("mouse_joystick", "1", "Right")]
        [InlineData("mouse_joystick", "0", "Left")]
        [InlineData("gyro_to_joystick", null, "Right")]
        [InlineData("gyro_to_joystick", "1", "Right")]
        [InlineData("gyro_to_joystick", "0", "Left")]
        public void GyroJoystickModes_ReadTheConfiguratorsList(string mode, string output, string side)
        {
            var p = output == null ? One(mode, "gyro") : One(mode, "gyro", ("output_joystick", output));
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == side + "ThumbAxisX" && r.Sources.Count > 0);
            string other = side == "Left" ? "RightThumbAxisX" : "LeftThumbAxisX";
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == other && r.Sources.Count > 0);
        }

        // ─── gyro_to_joystick_deflection: the tilt pair ─────────────────

        [Fact]
        public void Deflection_WithNoKeys_IsTheTiltPairOnTheLeftStick_AtSteamsAngles()
        {
            var p = One("gyro_to_joystick_deflection", "gyro");
            var sx = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX").Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            var sy = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisY").Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            Assert.Equal("Gyro Tilt X", sx.Descriptor);
            Assert.Equal("Gyro Tilt Y", sy.Descriptor);
            Assert.Equal(45.0, sx.ParamTiltRangeDeg, 3);
            Assert.Equal(2.0, sx.ParamTiltInnerDz, 3);
            Assert.Equal(45.0, sy.ParamTiltRangeDeg, 3);
            Assert.Equal(2.0, sy.ParamTiltInnerDz, 3);
            Assert.Equal(0.0, sx.ParamCurveExponent, 3);
            Assert.Equal(0.0, sx.ParamAntiDeadzone, 3);
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX" && r.Sources.Count > 0);
        }

        [Fact]
        public void Deflection_Output1_GoesRight()
        {
            var p = One("gyro_to_joystick_deflection", "gyro", ("output_joystick", "1"));
            Assert.Contains(p.XboxMappingSet.Rows, r => r.Target == "RightThumbAxisX" && r.Sources.Count > 0);
            Assert.DoesNotContain(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX" && r.Sources.Count > 0);
        }

        [Fact]
        public void Deflection_ReadsTheFilesAngles_CurveAndMinimumOutput()
        {
            var p = One("gyro_to_joystick_deflection", "gyro",
                ("gyro_deflection_to_joystick_min", "3"), ("gyro_deflection_to_joystick_max", "25"),
                ("gyro_to_joystick_power_curve", "180"), ("gyro_to_joystick_out_min", "150"),
                ("gyro_to_joystick_enable_angle_catchup", "0"), ("acceleration_axis_style", "0"));
            var sx = Assert.Single(p.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX").Sources.Single(s => s.Descriptor.StartsWith("Gyro Tilt"));
            Assert.Equal(25.0, sx.ParamTiltRangeDeg, 3);
            Assert.Equal(3.0, sx.ParamTiltInnerDz, 3);
            Assert.Equal(1.8, sx.ParamCurveExponent, 3);
            Assert.Equal(0.15, sx.ParamAntiDeadzone, 3);
            // Per axis, no drag, full output at 100%: nothing differs.
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
        }

        [Fact]
        public void Deflection_Defaults_NameTheDragAndTheCircularResponse()
        {
            var p = One("gyro_to_joystick_deflection", "gyro");
            var curve = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.Contains("gyro_to_joystick_enable_angle_catchup", string.Join(",", curve.ReasonArgs));
            var radial = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.DeadZoneRadialResidual);
            Assert.Contains("gyro_deflection_to_joystick_max", string.Join(",", radial.ReasonArgs));
        }

        [Fact]
        public void Deflection_MaximumOutputBelow100_IsNamed()
        {
            var p = One("gyro_to_joystick_deflection", "gyro", ("gyro_to_joystick_out_max", "800"),
                ("gyro_to_joystick_enable_angle_catchup", "0"), ("acceleration_axis_style", "0"));
            var curve = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.Equal("gyro_to_joystick_out_max", string.Join(",", curve.ReasonArgs));
        }

        [Fact]
        public void Deflection_WithNoGyroButton_IsMeasuredFromTheHorizon_AtTheWorldTiltOffset()
        {
            // Explicit mask 0: no gyro button, so the gyro is always on and
            // the client centers roll on the horizon and pitch at the World
            // Tilt Offset, 15 degrees on a standalone controller.
            var p = One("gyro_to_joystick_deflection", "gyro", ("gyro_ratchet_button_mask", "0"));
            Assert.Contains(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroRollFromHorizon);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("15", Assert.Single(pitch.ReasonArgs));
            Assert.False(p.GyroRecenterOnEngage);
        }

        [Fact]
        public void Deflection_WithAGyroButton_RecentersAtEachPress_AndNamesNoHorizon()
        {
            // A gyro button with Use Relative on (the default): the client
            // takes the pose at each activation. The import re-centers the
            // gyro each time it engages.
            var p = One("gyro_to_joystick_deflection", "gyro", ("gyro_ratchet_button_mask", "4"));
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroRollFromHorizon);
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.True(p.GyroRecenterOnEngage);
        }

        [Fact]
        public void Deflection_UseRelativePitchOff_CentersPitchAtTheNeutralAngle()
        {
            var p = One("gyro_to_joystick_deflection", "gyro", ("gyro_ratchet_button_mask", "4"),
                ("gyro_to_joystick_deflection_use_gravity", "0"),
                ("gyro_to_joystick_deflection_neutral_angle", "0"));
            Assert.DoesNotContain(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroRollFromHorizon);
            var pitch = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroPitchFromHorizon);
            Assert.Equal("0", Assert.Single(pitch.ReasonArgs));
        }

        [Fact]
        public void TiltRows_StopWhileTheGyroIsOff_WhenTheGroupHasGyroButtons()
        {
            // No key: the client's default gyro button, the right trackpad
            // touch, turns the gyro on, and the stick centers while it is
            // off. An explicit mask of 0 is no button, the gyro always on.
            var gated = One("gyro_to_joystick_deflection", "gyro");
            Assert.Contains(Assert.Single(gated.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX").Sources,
                s => s.Descriptor == "Gyro Engaged");
            var always = One("gyro_to_joystick_deflection", "gyro", ("gyro_ratchet_button_mask", "0"));
            Assert.DoesNotContain(Assert.Single(always.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX").Sources,
                s => s.Descriptor == "Gyro Engaged");
            var move = One("joystick_move", "gyro");
            Assert.Contains(Assert.Single(move.XboxMappingSet.Rows, r => r.Target == "LeftThumbAxisX").Sources,
                s => s.Descriptor == "Gyro Engaged");
        }

        [Fact]
        public void Deflection_MaximumPast90_IsNamed()
        {
            var p = One("gyro_to_joystick_deflection", "gyro", ("gyro_deflection_to_joystick_max", "113"));
            var cap = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.GyroTiltRangeCapped);
            Assert.Equal("113", Assert.Single(cap.ReasonArgs));
        }
    }
}
