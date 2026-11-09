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
            => VdfFor("", mode, settings);

        /// <summary>A one-group config bound to the gyro, with the
        /// controller_type the client reads its defaults by.</summary>
        internal static string VdfFor(string controllerType, string mode, params (string Key, string Value)[] settings)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"GyroSign\"\n");
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
        private static float CursorX(TranslatedProfile p, float yaw = 0, float roll = 0, float pitch = 0)
            => Cursor(p, forX: true, pitch, yaw, roll);

        /// <summary>The screen counts the KbmMouseY row moves the cursor by,
        /// down positive.</summary>
        private static float CursorY(TranslatedProfile p, float pitch = 0, float yaw = 0, float roll = 0)
            => Cursor(p, forX: false, pitch, yaw, roll);

        private static float Cursor(TranslatedProfile p, bool forX, float pitch, float yaw, float roll)
        {
            string target = forX ? "KbmMouseX" : "KbmMouseY";
            var row = p.KbmMappingSet.Rows.SingleOrDefault(r => r.Target == target && (r.LayerMask ?? "Base") == "Base");
            if (row == null) return 0f;
            return row.Sources
                .Where(s => (s.Descriptor ?? "").StartsWith("Gyro ", StringComparison.Ordinal))
                .Sum(s => GyroStickSignProbe.AtRate(st => GyroStickSignProbe.ScreenCounts(st, s, forX),
                    pitch: pitch, yaw: yaw, roll: roll));
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
        public void GyroAxisYawAndRoll_AddsBoth_AndNamesSteamsBlend()
        {
            var p = Translate(Vdf("absolute_mouse", ("gyro_axis", "2")));
            Assert.True(CursorX(p, roll: -1f) > 1f, "a roll to the right should move the cursor right");
            Assert.True(CursorX(p, yaw: -1f) > 1f, "the nose right should move the cursor right");
        }

        // ─── The client's own rules for yaw, roll and their scales ───
        //
        // steamclient64.dll registers gyro_yaw_scale and gyro_roll_scale as
        // integers from -10000 to 10000 with a default of 10000 in both the
        // absolute mouse class and the 3DOF to 2D class, and divides by
        // 10000 where it reads them.

        [Fact]
        public void AbsoluteMouseYawAndRoll_TakesEachScale()
        {
            var p = Translate(Vdf("absolute_mouse", ("gyro_axis", "2"),
                ("gyro_yaw_scale", "5000"), ("gyro_roll_scale", "-10000")));
            float yawOnly = CursorX(Translate(Vdf("absolute_mouse")), yaw: -1f);
            Assert.InRange(CursorX(p, yaw: -1f) / yawOnly, 0.49, 0.51);
            Assert.True(CursorX(p, roll: -1f) < -1f, "a roll scale of -100 percent should turn the cursor left for a roll to the right");
        }

        [Fact]
        public void AbsoluteMouseOnADeck_StartsOnYawAndRoll()
        {
            // The client's absolute mouse class sets gyro_axis's default to 2
            // for its handheld types other than the Steam Controller (2026).
            var deck = Translate(VdfFor("controller_neptune", "absolute_mouse"));
            Assert.True(CursorX(deck, roll: -1f) > 1f, "a Deck config turns with roll by default");
            Assert.True(CursorX(deck, yaw: -1f) > 1f);
            var ps5 = Translate(VdfFor("controller_ps5", "absolute_mouse"));
            Assert.Equal(0f, CursorX(ps5, roll: -1f));
        }

        [Fact]
        public void JoystickCamera_OnTheGyro_ReadsTheTilt_WhateverTheAxis()
        {
            // The client's joystick classes read the gyro as a tilt
            // position (X the roll, Y the pitch) and never consult
            // gyro_axis there (steamclient64.dll), so the pair is the tilt
            // pair and a rate moves nothing.
            var p = Translate(Vdf("joystick_camera", ("gyro_axis", "2")));
            var x = p.XboxMappingSet.Rows.Single(r => r.Target == "LeftThumbAxisX");
            Assert.Contains(x.Sources, s => s.Descriptor == "Gyro Tilt X");
            Assert.Equal(0f, CursorX(p, roll: -1f));
            Assert.Equal(0f, CursorX(p, yaw: 1f));
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
        public void ConversionStyleYawPlusRoll_AddsBoth_Exactly()
        {
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", "2")));
            Assert.True(CursorX(p, roll: -1f) > 1f);
            Assert.True(CursorX(p, yaw: -1f) > 1f);
            Assert.InRange(CursorX(p, roll: -1f) / CursorX(p, yaw: -1f), 0.99, 1.01);
            Assert.DoesNotContain(p.Report.Entries, e => e.Status == TranslationStatus.Partial);
        }

        [Theory]
        [InlineData("5000", 0.5)]
        [InlineData("-10000", -1.0)]
        [InlineData("0", 0.0)]
        public void ConversionStyleYawPlusRoll_TakesTheRollShare(string rollScale, double share)
        {
            // The 3DOF to 2D class adds roll times gyro_roll_scale / 10000 to
            // yaw and never reads gyro_yaw_scale.
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", "2"),
                ("gyro_roll_scale", rollScale), ("gyro_yaw_scale", "2500")));
            float yaw = CursorX(p, yaw: -1f);
            Assert.InRange(CursorX(p, roll: -1f) / yaw, share - 0.01, share + 0.01);
            float yawOnly = CursorX(Translate(Vdf("gyro_to_mouse")), yaw: -1f);
            Assert.InRange(yaw / yawOnly, 0.99, 1.01);
        }

        [Theory]
        [InlineData("90", "0", 0.0, 1.0)]     // turned a quarter: roll alone, like style 1
        [InlineData("90", "10000", -1.0, 1.0)]
        [InlineData("30", "0", 0.866, 0.5)]
        [InlineData("0", "5000", 1.0, 0.5)]   // no offset: style 2
        public void ConversionStyleAdvanced_TurnsTheSampleByTheOffset(string offset, string rollScale,
            double yawShare, double rollShare)
        {
            // Style 3 turns the sample by -offset about the pitch axis, then
            // adds roll's share: yaw (cos a - s sin a) + roll (sin a + s cos a).
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", "3"),
                ("gyro_mouse_sample_angle_offset", offset), ("gyro_roll_scale", rollScale)));
            float unit = CursorX(Translate(Vdf("gyro_to_mouse")), yaw: -1f);
            Assert.InRange(CursorX(p, yaw: -1f) / unit, yawShare - 0.01, yawShare + 0.01);
            Assert.InRange(CursorX(p, roll: -1f) / unit, rollShare - 0.01, rollShare + 0.01);
            Assert.DoesNotContain(p.Report.Entries, e => e.Status == TranslationStatus.Partial);
        }

        [Fact]
        public void ConversionStyleLaserPointer_ReadsYaw_AndSaysSo()
        {
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_to_2d_conversion_style", "6")));
            Assert.True(CursorX(p, yaw: 1f) < -1f, "the nose left should move the cursor left");
            Assert.Contains(p.Report.Entries, e => e.Status == TranslationStatus.Partial
                && e.ReasonKey == TranslationReasons.GyroLaserPointer);
            Assert.Equal("", p.GyroSpace);
        }

        [Theory]
        [InlineData("controller_neptune", true)]
        [InlineData("controller_steamos_handheld", true)]
        [InlineData("controller_zotac_zone", true)]
        [InlineData("controller_triton", false)]
        [InlineData("controller_ps5", false)]
        public void ConversionStyleDefault_IsYawAndRollOnTheHandhelds(string controllerType, bool rolls)
        {
            // The client's gyro class sets both gyro_axis and the style to 2
            // on load for its handheld types, leaving the Steam Controller
            // (2026) out.
            var p = Translate(VdfFor(controllerType, "gyro_to_mouse"));
            Assert.True(CursorX(p, yaw: -1f) > 1f);
            if (rolls) Assert.True(CursorX(p, roll: -1f) > 1f, $"{controllerType} turns with roll by default");
            else Assert.Equal(0f, CursorX(p, roll: -1f));
        }

        [Theory]
        [InlineData("controller_ps5", "1", "0", true, false)]     // not the default: roll
        [InlineData("controller_neptune", "0", "0", false, true)] // not the Deck's default 2: yaw
        [InlineData("controller_neptune", "1", "10", true, true)] // an offset keeps the style, 2
        [InlineData("controller_ps5", "0", "0", false, true)]     // the default: style stays 0
        public void GyroAxisInAConversionGroup_ReplacesTheStyle(string controllerType, string axis,
            string offset, bool rolls, bool yaws)
        {
            var p = Translate(VdfFor(controllerType, "gyro_to_mouse", ("gyro_axis", axis),
                ("gyro_mouse_sample_angle_offset", offset)));
            Assert.Equal(rolls, CursorX(p, roll: -1f) > 1f);
            Assert.Equal(yaws, CursorX(p, yaw: -1f) > 1f);
        }

        [Theory]
        [InlineData(new string[0], 1.0)]
        [InlineData(new[] { "gyro_natural_sensitivity", "250" }, 1.0)]
        [InlineData(new[] { "gyro_natural_sensitivity", "100" }, 0.4)]
        [InlineData(new[] { "sensitivity", "150" }, 0.6)]   // an old key moves in as it is
        [InlineData(new[] { "sensitivity", "100" }, 1.0)]   // its default moves nothing
        public void NaturalSensitivity_DefaultsTo250(string[] setting, double ofDefault)
        {
            float at250 = CursorX(Translate(Vdf("gyro_to_mouse", ("gyro_natural_sensitivity", "250"))), yaw: -1f);
            var settings = setting.Length == 0 ? Array.Empty<(string, string)>() : new[] { (setting[0], setting[1]) };
            float moved = CursorX(Translate(Vdf("gyro_to_mouse", settings)), yaw: -1f);
            Assert.InRange(moved / at250, ofDefault - 0.01, ofDefault + 0.01);
            float natural100 = CursorX(Translate(Vdf("gyro_to_mouse", ("gyro_natural_sensitivity", "100"))), yaw: -1f);
            float oneX = CursorX(Translate(Vdf("absolute_mouse", ("sensitivity", "50"))), yaw: -1f);
            Assert.InRange(natural100 / oneX, 0.99, 1.01);   // 100 stays PadForge's 1.0x
        }

        [Fact]
        public void NaturalSensitivity_DoublesAnOldKeyOnTheSwitchFamily()
        {
            float pro = CursorX(Translate(VdfFor("controller_switch_pro", "gyro_to_mouse", ("sensitivity", "150"))), yaw: -1f);
            float ps5 = CursorX(Translate(VdfFor("controller_ps5", "gyro_to_mouse", ("sensitivity", "150"))), yaw: -1f);
            Assert.InRange(pro / ps5, 1.99, 2.01);
        }

        [Theory]
        [InlineData("400", 1.0, 1.0)]
        [InlineData("300", 1.0, 0.75)]
        [InlineData("0", 1.0, 0.0)]
        [InlineData("600", 0.5, 1.0)]
        [InlineData("800", 0.0, 1.0)]
        public void OutputMixer_ScalesTheHorizontalAndTheVertical(string ratio, double horizontal, double vertical)
        {
            // gyro_vertical_horizontal_ratio: 0 to 800, default 400. The
            // client multiplies X by 1 - clamp((v - 400) / 400) and Y by
            // clamp(v / 400).
            var plain = Translate(Vdf("gyro_to_mouse"));
            var p = Translate(Vdf("gyro_to_mouse", ("gyro_vertical_horizontal_ratio", ratio)));
            Assert.InRange(CursorX(p, yaw: -1f) / CursorX(plain, yaw: -1f), horizontal - 0.01, horizontal + 0.01);
            Assert.InRange(CursorY(p, pitch: -1f) / CursorY(plain, pitch: -1f), vertical - 0.01, vertical + 0.01);
        }

        [Fact]
        public void OutputMixer_TakesAnOldVerticalScale()
        {
            // sensitivity_vert_scale 150: s = 1.5 * 0.5625 = 0.84375, t = s - 1,
            // ratio = trunc(800 * (t + 1) / 2) = 337, so Y keeps 337 / 400.
            var plain = Translate(Vdf("gyro_to_mouse"));
            var p = Translate(Vdf("gyro_to_mouse", ("sensitivity_vert_scale", "150")));
            Assert.InRange(CursorY(p, pitch: -1f) / CursorY(plain, pitch: -1f), 0.8425 - 0.005, 0.8425 + 0.005);
            Assert.InRange(CursorX(p, yaw: -1f) / CursorX(plain, yaw: -1f), 0.99, 1.01);
        }

        [Fact]
        public void GyroClass_IgnoresTheStickCurveKeys_AndNamesItsSpeedSettings()
        {
            // The 3DOF to 2D class registers no curve_exponent, anti_deadzone,
            // deadzone_inner_radius or sensitivity_horiz_scale, so they leave
            // the rows alone. Its speed dead zone and precision speed have no
            // PadForge counterpart and are named.
            var p = Translate(Vdf("gyro_to_mouse", ("curve_exponent", "4"), ("anti_deadzone", "3277"),
                ("deadzone_inner_radius", "5000"), ("sensitivity_horiz_scale", "200"),
                ("gyro_speed_deadzone", "50"), ("gyro_precision_speed", "300")));
            Assert.All(p.KbmMappingSet.Rows.SelectMany(r => r.Sources), s =>
            {
                Assert.Equal(0, s.ParamCurveExponent);
                Assert.Equal(0, s.ParamAntiDeadzone);
                Assert.Equal(1.0, s.Sensitivity);
            });
            float plain = CursorX(Translate(Vdf("gyro_to_mouse")), yaw: -1f);
            Assert.InRange(CursorX(p, yaw: -1f) / plain, 0.99, 1.01);
            var note = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.MouseModeTuningDropped);
            Assert.Equal("gyro_speed_deadzone, gyro_precision_speed", Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void GyroStick_NamesItsDeflectionStage()
        {
            var p = Translate(Vdf("gyro_to_joystick", ("gyro_to_joystick_power_curve", "200"),
                ("gyro_to_joystick_out_max", "800")));
            var note = Assert.Single(p.Report.Entries, e => e.ReasonKey == TranslationReasons.ResponseCurveNotSupported);
            Assert.Equal("gyro_to_joystick_out_max, gyro_to_joystick_power_curve", Assert.Single(note.ReasonArgs));
        }

        [Fact]
        public void OutputMixer_ScalesTheGyroStickToo()
        {
            var plain = Translate(Vdf("gyro_to_joystick"));
            var p = Translate(Vdf("gyro_to_joystick", ("gyro_vertical_horizontal_ratio", "200")));
            Assert.InRange(Stick(p, pitch: 0.02f).Y / (double)Stick(plain, pitch: 0.02f).Y, 0.48, 0.52);
            Assert.InRange(Stick(p, yaw: 0.02f).X / (double)Stick(plain, yaw: 0.02f).X, 0.98, 1.02);
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
        /// stick itself. The client's deflection mode (steamclient64.dll
        /// 0x1388bd670) reads the tilt as -(gx, gy) / |(gx, gy)| times
        /// acos(gz) in degrees, from the gravity vector in the Steam
        /// Controller's frame: x right, y toward the nose, z up out of the
        /// face (SDL_hidapi_steam.c sends that pad's accelerometer as x, z,
        /// -y). The nose up is a negative Y, the right edge down a positive
        /// X. With no gyro button the client centers pitch at minus the World
        /// Tilt Offset (0x1388be6f0), a resting grip with the nose up, and it
        /// sends both axes times 32767 to a stick whose Y is up (that pad's
        /// stick Y is up, SDL_hidapi_steam.c inverts it). So the nose up
        /// pulls the stick back and the right edge down pushes it right, as
        /// JoyShockMapper's motion stick does (calY = -grav.z, main.cpp). The
        /// mode's stick starts on the left (the client sets its
        /// output_joystick default to 0), and the pair is the tilt pair at
        /// the client's default angles, full at 45 degrees.</summary>
        [Fact]
        public void Deflection_TheNoseUpPullsTheStickBack_AndTheRightEdgeDownPushesRight()
        {
            var p = Translate(Vdf("gyro_to_joystick_deflection"));
            MappingSource Tilt(string target) => p.XboxMappingSet.Rows
                .Single(r => r.Target == target && (r.LayerMask ?? "Base") == "Base")
                .Sources.Single(s => (s.Descriptor ?? "").StartsWith("Gyro Tilt", StringComparison.Ordinal));
            float s30 = MathF.Sin(30 * MathF.PI / 180f), c30 = MathF.Cos(30 * MathF.PI / 180f);
            const float G = 9.81f;
            // The accelerometer reads up: flat is +Y, the nose up leans it to
            // -Z, the right edge down to -X.
            short back = AtTilt(s => GyroStickSignProbe.Written(s, Tilt("LeftThumbAxisY"), "LeftThumbAxisY"), (0f, G * c30, -G * s30));
            Assert.True(back < -10000, $"the nose up should pull the stick back, wrote {back}");
            short right = AtTilt(s => GyroStickSignProbe.Written(s, Tilt("LeftThumbAxisX"), "LeftThumbAxisX"), (-G * s30, G * c30, 0f));
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
            var p = Translate(Vdf("gyro_to_joystick", ("rotation_2", "30")));
            var (ux, uy) = Stick(p, pitch: 1f);
            Assert.True(ux > 0 && uy > 0, $"the nose up should land up and right, wrote ({ux}, {uy})");
            Assert.InRange(ux / (double)uy, 0.52, 0.63);   // tan 30 = 0.577

            var (lx, ly) = Stick(p, yaw: 1f);
            Assert.True(lx < 0 && ly > 0, $"the nose left should land left and up, wrote ({lx}, {ly})");
            Assert.InRange(ly / (double)-lx, 0.52, 0.63);
        }
    }
}
