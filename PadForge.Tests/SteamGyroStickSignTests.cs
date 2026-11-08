using System;
using System.Linq;
using PadForge.Engine.Data;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Which way a Steam gyro-to-stick group moves the virtual
    /// stick, read through the engine. sc-controller's gyro joystick action
    /// (scc/actions.py GyroAction.gyro, the editor's "gyro(ABS_RY, ABS_RX)"
    /// output in glade/ae/gyro_action.glade) writes ABS_RY = pitch * -10 and
    /// ABS_RX = yaw * -10. Its Steam Controller pitch and yaw are SDL's
    /// (scc/drivers/sc_dongle.py reads sGyroX and sGyroZ, which SDL's
    /// SDL_hidapi_steam.c reports as pitch and yaw unchanged), and Linux
    /// ABS_RY is down positive, so the nose up pushes the stick up and the
    /// nose left pushes it left.</summary>
    [Collection("SettingsManagerStatics")]
    public class SteamGyroStickSignTests
    {
        private static TranslatedProfile Translate(string vdf)
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(vdf));
            return new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = 42,
                PreferredLanguage = "english",
            });
        }

        private static string Vdf(string mode, params (string Key, string Value)[] settings)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"GyroSign\"\n");
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
            sb.Append("\t\t\"group_source_bindings\"\n\t\t{\n\t\t\t\"1\"\t\"gyro active\"\n\t\t}\n\t}\n}\n");
            return sb.ToString();
        }

        /// <summary>The right stick the translated rows write for a gyro
        /// rate, every gyro leg of each row summed, in XInput's sign.</summary>
        private static (int X, int Y) Stick(TranslatedProfile p, float pitch = 0, float yaw = 0, float roll = 0)
        {
            int Sum(string target)
            {
                var row = p.XboxMappingSet.Rows.Single(r => r.Target == target && (r.LayerMask ?? "Base") == "Base");
                return row.Sources
                    .Where(s => (s.Descriptor ?? "").StartsWith("Gyro ", StringComparison.Ordinal))
                    .Sum(s => (int)GyroStickSignProbe.AtRate(st => GyroStickSignProbe.Written(st, s, target), pitch: pitch, yaw: yaw, roll: roll));
            }
            return (Sum("RightThumbAxisX"), Sum("RightThumbAxisY"));
        }

        /// <summary>The screen counts the KbmMouseX row moves the cursor by
        /// for a gyro rate, every gyro source summed, right positive.</summary>
        private static float CursorX(TranslatedProfile p, float yaw = 0, float roll = 0)
        {
            var row = p.KbmMappingSet.Rows.Single(r => r.Target == "KbmMouseX" && (r.LayerMask ?? "Base") == "Base");
            return row.Sources
                .Where(s => (s.Descriptor ?? "").StartsWith("Gyro ", StringComparison.Ordinal))
                .Sum(s => GyroStickSignProbe.AtRate(st => GyroStickSignProbe.ScreenCounts(st, s, forX: true), yaw: yaw, roll: roll));
        }

        // ─── The gyro's turning axis (Steam client: gyro_axis, setting 64) ───
        //
        // steamclient64.dll pairs setting 64 with "gyro_axis", 125 with
        // "gyro_yaw_scale", 126 with "gyro_roll_scale" and 152 with
        // "gyro_to_2d_conversion_style". steamui/sp.js offers setting 64 as
        // GyroAxisAbsMouse, GyroAxisJoystickCamera and GyroAxisMouseJoystick
        // with the choices Gyro_Yaw 0, Gyro_Roll 1, Gyro_YawAndRoll 2, and
        // setting 152 (GyroTo2DConversionStyle, gyro_to_mouse and
        // gyro_to_joystick) with LocalSpaceYaw 0, LocalSpaceRoll 1,
        // LocalSpaceCombineYawAndRoll 2, LocalSpaceAdvanced 3, PlayerSpace 4,
        // WorldSpace 5, LaserPointer 6. sc-controller reads gyro_axis 1 as
        // MouseAction(ROLL), whose cursor X is roll * -speed with SDL's roll,
        // so a roll to the right moves the cursor right.

        [Theory]
        [InlineData("absolute_mouse")]
        [InlineData("joystick_camera")]
        public void GyroAxisRoll_TurnsTheCursorWithARollToTheRight(string mode)
        {
            var p = Translate(Vdf(mode, ("gyro_axis", "1")));
            float right = CursorX(p, roll: -1f);
            Assert.True(right > 1f, $"a roll to the right should move the cursor right, moved {right}");
            Assert.Equal(0f, CursorX(p, yaw: 1f));
        }

        [Fact]
        public void GyroAxisAbsent_KeepsTheYaw()
        {
            var p = Translate(Vdf("absolute_mouse"));
            Assert.True(CursorX(p, yaw: 1f) < -1f);
            Assert.Equal(0f, CursorX(p, roll: -1f));
        }

        [Fact]
        public void GyroAxisYawAndRoll_AddsBoth_AndSaysTheContributionIsNotRead()
        {
            var p = Translate(Vdf("absolute_mouse", ("gyro_axis", "2")));
            Assert.True(CursorX(p, roll: -1f) > 1f, "a roll to the right should move the cursor right");
            Assert.True(CursorX(p, yaw: -1f) > 1f, "the nose right should move the cursor right");
            Assert.Contains(p.Report.Entries, e => e.Status == TranslationStatus.Partial
                && e.ReasonKey == TranslationReasons.GyroRollContribution);
        }

        [Fact]
        public void GyroAxisRoll_OnAMouseJoystick_PushesTheStickRight()
        {
            var p = Translate(Vdf("mouse_joystick", ("gyro_axis", "1")));
            var (right, _) = Stick(p, roll: -1f);
            Assert.True(right > 1000, $"a roll to the right should push the stick right, wrote {right}");
            Assert.Equal(0, Stick(p, yaw: 1f).X);
        }

        [Theory]
        [InlineData("gyro_to_mouse")]
        [InlineData("gyro_to_joystick")]
        public void ConversionStyleRoll_TurnsWithARollToTheRight(string mode)
        {
            var p = Translate(Vdf(mode, ("gyro_to_2d_conversion_style", "1")));
            if (mode == "gyro_to_mouse")
                Assert.True(CursorX(p, roll: -1f) > 1f, "a roll to the right should move the cursor right");
            else
                Assert.True(Stick(p, roll: -1f).X > 1000, "a roll to the right should push the stick right");
        }

        [Fact]
        public void ConversionStyleYawPlusRoll_AddsBoth()
        {
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", "2")));
            Assert.True(CursorX(p, roll: -1f) > 1f);
            Assert.True(CursorX(p, yaw: -1f) > 1f);
            Assert.Contains(p.Report.Entries, e => e.Status == TranslationStatus.Partial
                && e.ReasonKey == TranslationReasons.GyroRollContribution);
        }

        [Theory]
        [InlineData("3", TranslationReasons.GyroLocalSpaceAdvanced)]
        [InlineData("6", TranslationReasons.GyroLaserPointer)]
        public void ConversionStyleWithNoCounterpart_ReadsYaw_AndSaysSo(string style, string reason)
        {
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", style)));
            Assert.True(CursorX(p, yaw: 1f) < -1f, "the nose left should move the cursor left");
            Assert.Contains(p.Report.Entries, e => e.Status == TranslationStatus.Partial && e.ReasonKey == reason);
            Assert.Equal("", p.GyroSpace);
        }

        [Theory]
        [InlineData("4", "Player")]
        [InlineData("5", "World")]
        [InlineData("0", "")]
        public void ConversionStyleSpace_StampsTheGyroSpace(string style, string space)
        {
            // Steam: "Player Space uses Yaw + Roll around the gravity axis
            // for horizontal output, and Local Pitch for vertical output",
            // PadForge's Player (GamepadMotion.hpp CalculatePlayerSpaceGyro).
            // "World Space uses all rotation around the gravity axis for
            // horizontal output, and World Pitch for vertical output",
            // PadForge's World (CalculateWorldSpaceGyro).
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", style)));
            Assert.Equal(space, p.GyroSpace);
            Assert.DoesNotContain(p.Report.Entries, e => e.Status == TranslationStatus.Partial);
            var set = PadForge.Services.WorkshopProfileMaterializer.Materialize(p).SlotMappingSets;
            Assert.Contains(set, s => s != null && s.WorkshopGyroSpace == space && s.Rows.Count > 0);
        }

        [Theory]
        [InlineData("mouse_joystick")]
        [InlineData("gyro_to_joystick")]
        public void TheNoseUpPushesTheStickUp_AndTheNoseLeftPushesItLeft(string mode)
        {
            var p = Translate(Vdf(mode));
            var (_, up) = Stick(p, pitch: 1f);
            Assert.True(up > 1000, $"the nose up should push the stick up, wrote {up}");
            var (left, _) = Stick(p, yaw: 1f);
            Assert.True(left < -1000, $"the nose left should push the stick left, wrote {left}");
        }

        [Fact]
        public void InvertY_PushesTheStickDownForTheNoseUp()
        {
            var p = Translate(Vdf("gyro_to_joystick", ("invert_y", "1")));
            var (_, up) = Stick(p, pitch: 1f);
            Assert.True(up < -1000, $"invert_y: the nose up should push the stick down, wrote {up}");
        }

        /// <summary>gyro_to_joystick_deflection holds the controller as the
        /// stick itself. Valve's 10/25/23 client notes call the mode "most
        /// appropriate for simulating Steering and Flight Yokes", and
        /// JoyShockMapper's motion stick, the same tilt-to-stick read, writes
        /// calY = -grav.z (main.cpp), so tilting back, the nose up, is the
        /// stick back (README: "MUP, MDOWN, MLEFT, MRIGHT: Motion stick
        /// tilted forward, back, left or right"). The right edge down is the
        /// stick right.</summary>
        [Fact]
        public void Deflection_TheNoseUpPullsTheStickBack_AndTheRightEdgeDownPushesRight()
        {
            var p = Translate(Vdf("gyro_to_joystick_deflection"));
            MappingSource Lean(string target) => p.XboxMappingSet.Rows
                .Single(r => r.Target == target && (r.LayerMask ?? "Base") == "Base")
                .Sources.Single(s => (s.Descriptor ?? "").StartsWith("Gyro Lean", StringComparison.Ordinal));
            float s30 = MathF.Sin(30 * MathF.PI / 180f), c30 = MathF.Cos(30 * MathF.PI / 180f);
            const float G = 9.81f;
            // The accelerometer reads up: flat is +Y, the nose up leans it to
            // -Z, the right edge down to -X.
            short back = AtTilt(s => GyroStickSignProbe.Written(s, Lean("RightThumbAxisY"), "RightThumbAxisY"), (0f, G * c30, -G * s30));
            Assert.True(back < -10000, $"the nose up should pull the stick back, wrote {back}");
            short right = AtTilt(s => GyroStickSignProbe.Written(s, Lean("RightThumbAxisX"), "RightThumbAxisX"), (-G * s30, G * c30, 0f));
            Assert.True(right > 10000, $"the right edge down should push the stick right, wrote {right}");
        }

        /// <summary>A lean read with the resting grip flat and the
        /// accelerometer then reading <paramref name="tilted"/>.</summary>
        private static T AtTilt<T>(Func<PadForge.Engine.CustomInputState, T> read, (float x, float y, float z) tilted)
        {
            var oldGravity = PadForge.Engine.Common.Mapping.SourceCoercion.GravityProvider;
            try
            {
                PadForge.Engine.Common.Mapping.SourceCoercion.ResetGyroLeanNeutral();
                var gravity = (0f, 9.81f, 0f);
                PadForge.Engine.Common.Mapping.SourceCoercion.GravityProvider = _ => gravity;
                GyroStickSignProbe.AtRate(read);
                gravity = tilted;
                return GyroStickSignProbe.AtRate(read);
            }
            finally
            {
                PadForge.Engine.Common.Mapping.SourceCoercion.ResetGyroLeanNeutral();
                PadForge.Engine.Common.Mapping.SourceCoercion.GravityProvider = oldGravity;
            }
        }

        /// <summary>Rotation turns the gyro's output the way it turns a
        /// trackpad's. The trackpad legs realize R(θ) on the pad's right and
        /// down frame (X = cos x - sin y, Y = sin x + cos y), so a finger up
        /// rotated 30 degrees writes the stick at (sin, cos) of its length,
        /// up and to the right. The nose up must land in the same place, and
        /// the nose left at (-cos, sin).</summary>
        [Fact]
        public void Rotation_TurnsTheGyroOutputLikeTheTrackpads()
        {
            var p = Translate(Vdf("gyro_to_joystick", ("rotation", "30")));
            var (ux, uy) = Stick(p, pitch: 1f);
            Assert.True(ux > 0 && uy > 0, $"the nose up should land up and right, wrote ({ux}, {uy})");
            Assert.InRange(ux / (double)uy, 0.52, 0.63);   // tan 30 = 0.577

            var (lx, ly) = Stick(p, yaw: 1f);
            Assert.True(lx < 0 && ly > 0, $"the nose left should land left and up, wrote ({lx}, {ly})");
            Assert.InRange(ly / (double)-lx, 0.52, 0.63);
        }
    }
}
