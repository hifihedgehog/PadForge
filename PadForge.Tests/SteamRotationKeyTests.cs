using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Which key a Steam group's rotation comes from, and which way
    /// it turns. The client registers setting 45, "rotation_2", in the
    /// absolute mouse class, the flick stick, the 3DOF to 2D gyro class, the
    /// joystick move class and the two camera-angle classes, and every
    /// Rotation control in steamui/sp.js writes it. The older setting 128,
    /// "rotation", is registered only by the absolute mouse class and the
    /// flick stick, which move it into rotation_2 while rotation_2 sits at its
    /// default. joystick_move's help text: "pushing the physical stick North
    /// results in an output of East, when set to 90".</summary>
    [Collection("SettingsManagerStatics")]
    public class SteamRotationKeyTests
    {
        private static TranslatedProfile Translate(string controllerType, string mode, string source,
            params (string Key, string Value)[] settings)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Rotation\"\n");
            if (controllerType.Length > 0)
                sb.Append($"\t\"controller_type\"\t\"{controllerType}\"\n");
            sb.Append($"\t\"group\"\n\t{{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"{mode}\"\n");
            if (settings.Length > 0)
            {
                sb.Append("\t\t\"settings\"\n\t\t{\n");
                foreach (var (k, v) in settings)
                    sb.Append($"\t\t\t\"{k}\"\t\"{v}\"\n");
                sb.Append("\t\t}\n");
            }
            sb.Append("\t}\n");
            sb.Append("\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n");
            sb.Append($"\t\t\"group_source_bindings\"\n\t\t{{\n\t\t\t\"1\"\t\"{source} active\"\n\t\t}}\n\t}}\n}}\n");
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(sb.ToString()));
            return new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 42, PreferredLanguage = "english" });
        }

        /// <summary>The angle a rotated mouse row realizes, from its X row's
        /// two legs in the order the emitter adds them: the pad X leg carries
        /// cos, the pad Y leg -sin.</summary>
        private static double MouseRotationDeg(TranslatedProfile p)
        {
            var row = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX");
            if (row.Sources.Count == 1) return 0;
            Assert.Equal(2, row.Sources.Count);
            var xLeg = row.Sources[0];
            var yLeg = row.Sources[1];
            double cos = xLeg.Sensitivity * (xLeg.Invert ? -1 : 1);
            double minusSin = yLeg.Sensitivity * (yLeg.Invert ? -1 : 1);
            return Math.Atan2(-minusSin, cos) * 180.0 / Math.PI;
        }

        [Fact]
        public void RotationTwo_TurnsTheAbsoluteMouse()
        {
            var p = Translate("controller_ps4", "absolute_mouse", "right_trackpad", ("rotation_2", "30"));
            Assert.InRange(MouseRotationDeg(p), 29.9, 30.1);
        }

        [Fact]
        public void TheOldRotation_MovesIntoRotationTwo_OnlyWhereTheClientRegistersIt()
        {
            Assert.InRange(MouseRotationDeg(Translate("controller_ps4", "absolute_mouse", "right_trackpad",
                ("rotation", "20"))), 19.9, 20.1);
            // rotation_2 wins while it is not its default.
            Assert.InRange(MouseRotationDeg(Translate("controller_ps4", "absolute_mouse", "right_trackpad",
                ("rotation", "20"), ("rotation_2", "10"))), 9.9, 10.1);
            // The gyro class never registers setting 128.
            var gyro = Translate("controller_ps4", "gyro_to_mouse", "gyro", ("rotation", "30"));
            Assert.Single(gyro.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX").Sources);
        }

        [Fact]
        public void OnAHandheld_TheOldRotationMovesInTimesOneAndAHalf()
        {
            var p = Translate("controller_neptune", "absolute_mouse", "right_trackpad", ("rotation", "20"));
            Assert.InRange(MouseRotationDeg(p), 29.9, 30.1);
        }

        [Theory]
        [InlineData("right_trackpad", 15.0)]
        [InlineData("left_trackpad", -15.0)]
        public void SteamControllerTrackpads_DefaultToFifteenDegrees(string source, double degrees)
        {
            // On top of the 15 degrees the driver turns each pad by, in the
            // client and in SDL (SDL_hidapi_steam.c RotatePad).
            var p = Translate("controller_steamcontroller_gordon", "absolute_mouse", source);
            Assert.InRange(MouseRotationDeg(p), degrees - 0.1, degrees + 0.1);
            var set = Translate("controller_steamcontroller_gordon", "absolute_mouse", source, ("rotation_2", "0"));
            Assert.Equal(0.0, MouseRotationDeg(set));
        }

        [Fact]
        public void JoystickCamera_HasNoRotation()
        {
            var p = Translate("controller_ps4", "joystick_camera", "right_joystick", ("rotation_2", "30"), ("rotation", "30"));
            Assert.Single(p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX").Sources);
        }

        [Fact]
        public void JoystickMove_TurnsNorthToEastAtNinety()
        {
            var p = Translate("controller_ps4", "joystick_move", "joystick", ("rotation_2", "90"));
            short Sum(string target)
            {
                var row = p.XboxMappingSet.Rows.Single(r => r.Target == target && (r.LayerMask ?? "Base") == "Base");
                var state = new CustomInputState();
                for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
                state.Axis[1] = 0;   // the physical stick pushed North
                return (short)Math.Clamp(row.Sources.Sum(s => (int)GyroStickSignProbe.Written(state, s, target)),
                    short.MinValue, short.MaxValue);
            }
            Assert.True(Sum("LeftThumbAxisX") > 30000, "North should come out East");
            Assert.InRange(Sum("LeftThumbAxisY"), -500, 500);
        }

        [Fact]
        public void MouseRegion_NamesItsRotation()
        {
            var p = Translate("controller_ps4", "mouse_region", "right_trackpad", ("rotation_2", "15"));
            var note = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.MouseModeTuningDropped);
            Assert.Contains("rotation_2", Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void FlickStick_ReadsRotationTwo()
        {
            var p = Translate("controller_ps4", "flickstick", "right_joystick", ("rotation_2", "-5"), ("rotation", "9"));
            var src = p.KbmMappingSet.Rows.Concat(p.XboxMappingSet?.Rows ?? Enumerable.Empty<MappingRow>())
                .SelectMany(r => r.Sources)
                .Single(s => (s.Descriptor ?? "").StartsWith("Flick Stick", StringComparison.Ordinal));
            Assert.Equal(-5, src.ParamFlickRotationOffsetDeg, 6);
        }
    }
}
