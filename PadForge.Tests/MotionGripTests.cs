using System;
using System.IO;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Grip (#392, discussion #389): how the controller is held, per
    /// (device, slot). Pins the two rotation tables against SDL's own
    /// sideways Joy-Con swap and against each other, the calibrated read
    /// funnel (source-axis debias, then sign), the passthrough gyro in
    /// both toggle states, the gravity helper, persistence, and the
    /// sibling-set source contracts.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionGripTests
    {
        private const string Dev = "aaaa1111-2222-3333-4444-555566667777";

        [Fact]
        public void Tables_PointingIsIdentity_AndUnknownReadsAsPointing()
        {
            Assert.Equal((1f, 2f, 3f), SourceCoercion.RotateForGrip("Pointing", 1f, 2f, 3f));
            Assert.Equal((1f, 2f, 3f), SourceCoercion.RotateForGrip(null, 1f, 2f, 3f));
            Assert.Equal((1f, 2f, 3f), SourceCoercion.RotateForGrip("", 1f, 2f, 3f));
            Assert.Equal((1f, 2f, 3f), SourceCoercion.RotateForGrip("Banana", 1f, 2f, 3f));
        }

        /// <summary>SDL_hidapi_switch.c SendSensorUpdate, the mini-gamepad
        /// branch for a single left Joy-Con held sideways: data[2] = -data[0],
        /// data[0] = old data[2]. A sideways Wii Remote is the same hold,
        /// top edge to the left, so the table must match byte for byte.</summary>
        [Fact]
        public void Sideways_MatchesSdlsSidewaysLeftJoyConSwap()
        {
            var (x, y, z) = SourceCoercion.RotateForGrip("Sideways", 1f, 2f, 3f);
            Assert.Equal(3f, x);
            Assert.Equal(2f, y);
            Assert.Equal(-1f, z);
        }

        /// <summary>Upright: the remote's top edge points at the ceiling. The
        /// body reports gravity's reaction along -Z at rest (its pointing
        /// end is up); after the rotation that lands on +Y, where a flat pad
        /// reports it.</summary>
        [Fact]
        public void Upright_PutsRestingGravityOnPlusY()
        {
            var (x, y, z) = SourceCoercion.RotateForGrip("Upright", 0f, 0f, -9.8f);
            Assert.Equal(0f, x);
            Assert.Equal(9.8f, y);
            Assert.Equal(0f, z);
        }

        /// <summary>Wii Wheel: top edge left AND the face turned toward the
        /// player. The steering motion is a rotation about the face normal,
        /// body yaw, and must come out as roll, what a flat pad reports when
        /// rolled like a wheel. Bench-derived: with the face-up table this
        /// hold steered on yaw (owner, 2026-09-01).</summary>
        [Fact]
        public void WiiWheel_TurnsBodyYawIntoRoll_AndBodyPitchIntoYaw()
        {
            var steer = SourceCoercion.RotateForGrip("WiiWheel", 0f, 1f, 0f);
            Assert.Equal((0f, 0f, 1f), steer);
            var pitch = SourceCoercion.RotateForGrip("WiiWheel", 1f, 0f, 0f);
            Assert.Equal((0f, 1f, 0f), pitch);
            // Gravity at rest: the face toward the player puts the reaction
            // force on the body's +X (the right edge points up), and it must
            // land on +Y.
            Assert.Equal((0f, 9.8f, 0f), SourceCoercion.RotateForGrip("WiiWheel", 9.8f, 0f, 0f));
        }

        [Fact]
        public void GripAxis_AgreesWithRotateForGrip_OnEveryAxis()
        {
            foreach (string grip in new[] { "Pointing", "Sideways", "WiiWheel", "Upright" })
            {
                float[] v = { 1.5f, -2.5f, 3.5f };
                var r = SourceCoercion.RotateForGrip(grip, v[0], v[1], v[2]);
                float[] rotated = { r.x, r.y, r.z };
                for (int axis = 0; axis < 3; axis++)
                {
                    var (src, sign) = SourceCoercion.GripAxis(grip, axis);
                    Assert.Equal(rotated[axis], sign * v[src]);
                }
            }
        }

        /// <summary>Both rotations are proper (determinant +1), which is what
        /// lets one table serve a true vector (accel) and a pseudovector
        /// (gyro) alike. Checked as right-handedness of the rotated basis.</summary>
        [Fact]
        public void Tables_AreProperRotations()
        {
            foreach (string grip in new[] { "Sideways", "WiiWheel", "Upright" })
            {
                var ex = SourceCoercion.RotateForGrip(grip, 1f, 0f, 0f);
                var ey = SourceCoercion.RotateForGrip(grip, 0f, 1f, 0f);
                var ez = SourceCoercion.RotateForGrip(grip, 0f, 0f, 1f);
                // det = ex . (ey x ez)
                float cx = ey.y * ez.z - ey.z * ez.y;
                float cy = ey.z * ez.x - ey.x * ez.z;
                float cz = ey.x * ez.y - ey.y * ez.x;
                float det = ex.x * cx + ex.y * cy + ex.z * cz;
                Assert.Equal(1f, det);
            }
        }

        private static (float p, float y, float r) Passthrough(string grip, bool applyTuning,
            float pitch, float yaw, float roll, (float, float, float)? bias)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroBiasProvider = bias == null ? null : ((g, s) => bias.Value);
                SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning
                {
                    SensH = 1f, SensV = 1f, OutputCurve = "Linear", Space = "Local",
                    Grip = grip, ApplyToPassthrough = applyTuning,
                };
                var st = new CustomInputState();
                st.Gyro[0] = pitch; st.Gyro[1] = yaw; st.Gyro[2] = roll;
                SourceCoercion.GetPassthroughGyro(st, Dev, 0, out float p, out float y, out float r);
                return (p, y, r);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.GyroBiasProvider = oldBias;
            }
        }

        /// <summary>The funnel debiases the SOURCE axis, then applies the
        /// sign: with a sideways grip the output roll is minus the debiased
        /// source pitch, and the output pitch is the debiased source roll.</summary>
        [Fact]
        public void CalibratedRead_DebiasesTheSourceAxis_ThenSigns()
        {
            var got = Passthrough("Sideways", applyTuning: false, 1.0f, 2.0f, 3.0f, (0.1f, 0.2f, 0.3f));
            Assert.Equal(2.7f, got.p, 5);
            Assert.Equal(1.8f, got.y, 5);
            Assert.Equal(-0.9f, got.r, 5);
        }

        [Fact]
        public void Passthrough_RotatesInBothToggleStates()
        {
            var off = Passthrough("Sideways", applyTuning: false, 1.0f, 2.0f, 3.0f, null);
            var on = Passthrough("Sideways", applyTuning: true, 1.0f, 2.0f, 3.0f, null);
            Assert.Equal(3.0f, off.p, 5);
            Assert.Equal(-1.0f, off.r, 5);
            Assert.Equal(off.p, on.p, 5);
            Assert.Equal(off.y, on.y, 5);
            Assert.Equal(off.r, on.r, 5);
        }

        [Fact]
        public void Pointing_IsByteIdenticalToTheOldRead()
        {
            var got = Passthrough("Pointing", applyTuning: false, 1.0f, 2.0f, 3.0f, (0.1f, 0.2f, 0.3f));
            Assert.Equal(0.9f, got.p, 5);
            Assert.Equal(1.8f, got.y, 5);
            Assert.Equal(2.7f, got.r, 5);
        }

        [Fact]
        public void ReadGravity_RotatesTheBodySensorOnly_AndKeepsTheSentinel()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldGrav = SourceCoercion.GravityProvider;
            var oldGravAux = SourceCoercion.GravityProviderAux;
            try
            {
                SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = "Sideways" };
                SourceCoercion.GravityProvider = g => (1f, 2f, 3f);
                SourceCoercion.GravityProviderAux = g => (1f, 2f, 3f);
                Assert.Equal((3f, 2f, -1f), SourceCoercion.ReadGravity(Dev, 0, aux: false));
                Assert.Equal((1f, 2f, 3f), SourceCoercion.ReadGravity(Dev, 0, aux: true));
                SourceCoercion.GravityProvider = null;
                Assert.Equal((0f, 0f, -1f), SourceCoercion.ReadGravity(Dev, 0, aux: false));
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GravityProvider = oldGrav;
                SourceCoercion.GravityProviderAux = oldGravAux;
            }
        }

        /// <summary>The compass correction rides the yaw lane only while the
        /// grip still feeds that lane from the body's yaw axis: Sideways
        /// keeps it, Upright (yaw from roll) drops it.</summary>
        [Fact]
        public void CompassCorrection_FollowsTheYawAxis_NotTheYawLane()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldCorr = SourceCoercion.CompassYawCorrectionProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            try
            {
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.CompassYawCorrectionProvider = _ => 0.3f;
                var src = new MappingSource { Kind = "Direct", DeviceGuid = Dev, Descriptor = "Gyro Yaw" };
                var st = new CustomInputState();
                float Read(string grip)
                {
                    SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning
                    {
                        SensH = 1f, SensV = 1f, OutputCurve = "Linear", CompassYaw = true, Grip = grip,
                    };
                    return SourceEvaluator.EvaluateForBipolarAxisTarget(
                        st, src, 0, "LeftThumbAxisY", 0, null, 0.016, Dev);
                }
                Assert.NotEqual(0f, Read("Pointing"));
                Assert.NotEqual(0f, Read("Sideways"));
                Assert.Equal(0f, Read("WiiWheel"));
                Assert.Equal(0f, Read("Upright"));
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.CompassYawCorrectionProvider = oldCorr;
                SourceCoercion.AimEngageStateProvider = oldEngage;
                SourceCoercion.GyroBiasProvider = oldBias;
            }
        }

        /// <summary>The hat rides the same frame: with the top edge left,
        /// physical Right reads as Up, and so on around, Dolphin's sideways
        /// D-pad table. Diagonals follow the angle, centered passes through,
        /// and Pointing and Upright leave the hat alone.</summary>
        [Fact]
        public void GripPov_TurnsTheHatWithTheHold()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            try
            {
                foreach (string grip in new[] { "Sideways", "WiiWheel" })
                {
                    SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = grip };
                    Assert.Equal(0, SourceCoercion.GripPov(Dev, 0, 9000));       // physical Right -> Up
                    Assert.Equal(27000, SourceCoercion.GripPov(Dev, 0, 0));      // physical Up -> Left
                    Assert.Equal(9000, SourceCoercion.GripPov(Dev, 0, 18000));   // physical Down -> Right
                    Assert.Equal(18000, SourceCoercion.GripPov(Dev, 0, 27000));  // physical Left -> Down
                    Assert.Equal(31500, SourceCoercion.GripPov(Dev, 0, 4500));   // up-right -> up-left
                    Assert.Equal(-1, SourceCoercion.GripPov(Dev, 0, -1));
                }
                foreach (string grip in new[] { "Pointing", "Upright", null })
                {
                    SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = grip };
                    Assert.Equal(9000, SourceCoercion.GripPov(Dev, 0, 9000));
                }
            }
            finally { SourceCoercion.GyroTuningProvider = oldTuning; }
        }

        /// <summary>A "POV 0 Up" row fires from the physical Right press under
        /// a sideways grip, through the same evaluator the rows use.</summary>
        [Fact]
        public void PovRow_ReadsTheHeldFrame()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.AimEngageStateProvider = null;
                var src = new MappingSource { Kind = "Direct", DeviceGuid = Dev, Descriptor = "POV 0 Up" };
                var st = new CustomInputState();
                st.Povs[0] = 9000; // physical Right
                float Read(string grip)
                {
                    SourceCoercion.GyroTuningProvider = (g, s) => new SourceCoercion.GyroTuning { Grip = grip };
                    return SourceEvaluator.EvaluateForBipolarAxisTarget(st, src, 0, "LeftThumbAxisY", 0, null, 0.016, Dev);
                }
                Assert.Equal(0f, Read("Pointing"));
                Assert.NotEqual(0f, Read("Sideways"));
                Assert.NotEqual(0f, Read("WiiWheel"));
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        /// <summary>Every live hat read outside the evaluator goes through
        /// the same rotation: the legacy PadSetting mapper's four sites and
        /// the recorder, which must capture the held-frame direction.</summary>
        [Fact]
        public void HatReads_OutsideTheEvaluator_UseTheHeldFrame()
        {
            string s3 = RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.UpdateOutputStates.cs");
            Assert.Equal(4, CountOf(s3, "SourceCoercion.GripPov(deviceGuid, slotIndex, state.Povs[desc.Index])"));
            Assert.Contains("MapDPadFromPov(state, ps.DPad, ref gp, deviceGuid, slotIndex);", s3);
            Assert.DoesNotContain("IsPovDirectionActive(state.Povs[", s3);
            string rec = RepoText("PadForge.App", "Services", "RecorderService.cs");
            Assert.Contains("SourceCoercion.GripPov(", rec);
            Assert.Contains("dg.ToString(), _activePadIndex, current.Povs[i]", rec);
        }

        [Fact]
        public void PadSetting_DefaultsToPointing_AndRoundTripsXml()
        {
            var ps = new PadSetting();
            Assert.Equal("Pointing", ps.MotionGrip);
            ps.MotionGrip = "Upright";
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            var back = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("Upright", back.MotionGrip);
        }

        /// <summary>Every lane the per-axis invert commit (f3fb7b85) touched
        /// carries the field: load and save, both profile lanes, the dirty
        /// hook, the provider, the summary token, the XAML row, the reset
        /// commands, the Designer, and all ten locales.</summary>
        [Fact]
        public void SiblingSet_CarriesTheField()
        {
            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("padVm.MotionGrip = string.IsNullOrEmpty(ps.MotionGrip) ? \"Pointing\" : ps.MotionGrip;", ss);
            Assert.Contains("ps.MotionGrip = padVm.MotionGrip ?? \"Pointing\";", ss);

            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("padVm.MotionGrip = string.IsNullOrEmpty(ps.MotionGrip) ? \"Pointing\" : ps.MotionGrip;", svc);
            Assert.Contains("ps.MotionGrip = padVm.MotionGrip ?? \"Pointing\";", svc);
            Assert.Contains("Grip = string.IsNullOrEmpty(ps.MotionGrip) ? \"Pointing\" : ps.MotionGrip,", svc);
            Assert.Contains("AddToken(parts, \"GRIP \" + ps.MotionGrip);", svc);
            Assert.Equal(2, CountOf(svc, "SourceCoercion.ApplyMotionGrip(ud.InstanceGuidString, i, ref"));

            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("nameof(PadViewModel.MotionGrip)", mw);

            string im = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("SourceCoercion.ApplyMotionGrip(accelSrc.Ud.InstanceGuidString, padIndex, ref ax, ref ay, ref az);", im);

            string vm = RepoText("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Contains("public string MotionGrip", vm);
            Assert.Contains("MotionGripOptions", vm);
            Assert.Contains("ResetMotionGripCommand", vm);
            // The reset command restores the default (its lambda ends in a parenthesis).
            Assert.Contains("MotionGrip = \"Pointing\")", vm);

            string xaml = RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("SelectedValue=\"{Binding MotionGrip, Mode=TwoWay}\"", xaml);
            Assert.Contains("ResetMotionGripCommand", xaml);
            Assert.Contains("Binding Pad_Gyro_Grip_Header,", xaml);
            // The five gyro-rate cards are named so the page can hide them on
            // an accelerometer-only device, which now sees the Gyro tab.
            foreach (var card in new[] { "GyroPassthroughCard", "GyroCalibrationCard", "GyroSensitivityCard", "GyroResponseCard", "GyroEngageCard" })
                Assert.Contains($"x:Name=\"{card}\"", xaml);
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("hasGyro = ud.HasGyro || ud.HasAccel;", page);
            Assert.Contains("hasGyroRate = ud.HasGyro;", page);
            Assert.Contains("GyroEngageCard.Visibility = rateVis;", page);
            // A grip change re-references the resting grips like Gyro Recenter.
            Assert.Contains("if (e.PropertyName == nameof(PadViewModel.MotionGrip))", mw);
            Assert.Contains("GyroRecenterApply?.Invoke(capturedPad.PadIndex);", mw);

            string ps = RepoText("PadForge.Engine", "Data", "PadSetting.cs");
            Assert.Contains("sb.Append(MotionGrip); sb.Append('|');", ps);
            Assert.Contains("nameof(MotionGrip)", ps);

            string eng = RepoText("PadForge.Engine", "Common", "Mapping", "SourceCoercion.cs");
            // The gravity helper replaced every inline fetch in the space
            // projections, and both lean readers thread the slot.
            Assert.DoesNotContain("(aux ? GravityProviderAux : GravityProvider)?.Invoke(deviceGuid) ?? (0f, 0f, -1f)", eng);
            Assert.Contains("internal static float ReadGyroLean(MappingSource src, string canonical, string deviceGuid, int slotIndex = -1)", eng);
            Assert.Contains("internal static float ReadMotionLeanValue(MappingSource src, string deviceGuid, bool aux, int slotIndex = -1)", eng);
            string skr = RepoText("PadForge.Engine", "Common", "Mapping", "SourceKindRuntime.cs");
            Assert.Contains("SourceCoercion.ReadGravity(deviceGuid, slotIndex, aux)", skr);

            string des = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (var k in new[] { "Settings_MotionGrip", "Settings_MotionGrip_Tooltip", "Pad_Gyro_Grip_Pointing", "Pad_Gyro_Grip_Sideways", "Pad_Gyro_Grip_WiiWheel", "Pad_Gyro_Grip_Upright", "Pad_ResetMotionGrip", "Pad_Gyro_Grip_Header", "Pad_Gyro_Grip_Subtitle" })
                Assert.Contains($"public string {k} => Get(\"{k}\");", des);
            foreach (var loc in new[] { "", ".de", ".es", ".fr", ".it", ".ja", ".ko", ".nl", ".pt-BR", ".zh-Hans" })
            {
                string resx = RepoText("PadForge.App", "Resources", "Strings", "Strings" + loc + ".resx");
                foreach (var k in new[] { "Settings_MotionGrip", "Settings_MotionGrip_Tooltip", "Pad_Gyro_Grip_Pointing", "Pad_Gyro_Grip_Sideways", "Pad_Gyro_Grip_WiiWheel", "Pad_Gyro_Grip_Upright", "Pad_ResetMotionGrip", "Pad_Gyro_Grip_Header", "Pad_Gyro_Grip_Subtitle" })
                    Assert.Contains($"<data name=\"{k}\"", resx);
            }
        }

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
