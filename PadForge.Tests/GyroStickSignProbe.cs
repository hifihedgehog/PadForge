using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Tests
{
    /// <summary>Reads a gyro source the way a mapping row reads it, for the
    /// sign pins on gyro-to-stick producers. A stick target goes through
    /// SourceEvaluator, which applies the axis-frame correction, and then
    /// through InputManager.WriteBipolarAxisTarget, which negates Y. The
    /// value returned is the Gamepad field the row writes, in XInput's sign:
    /// X right positive, Y up positive. The mouse target goes through the
    /// gyro mouse lane and the screen-space negation the KBM controller
    /// applies to it, so its value is screen counts: X right positive, Y
    /// down positive.</summary>
    internal static class GyroStickSignProbe
    {
        private static readonly MethodInfo WriteBipolar = typeof(InputManager).GetMethod(
            "WriteBipolarAxisTarget", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(InputManager), "WriteBipolarAxisTarget");

        /// <summary>The stick field a row on <paramref name="target"/>
        /// writes for this state.</summary>
        internal static short Written(CustomInputState state, MappingSource src, string target)
        {
            float v = SourceEvaluator.EvaluateForBipolarAxisTarget(
                state, src, 0, target, 0, null, 0.016, "");
            object[] args = { target, v, default(Gamepad) };
            WriteBipolar.Invoke(null, args);
            var gp = (Gamepad)args[2];
            return target switch
            {
                "LeftThumbAxisX" => gp.ThumbLX,
                "LeftThumbAxisY" => gp.ThumbLY,
                "RightThumbAxisX" => gp.ThumbRX,
                "RightThumbAxisY" => gp.ThumbRY,
                _ => throw new ArgumentException("Not a stick target: " + target, nameof(target)),
            };
        }

        /// <summary>The screen counts a KbmMouseX or KbmMouseY row moves
        /// the cursor by for this state, after the KBM controller's
        /// negation into screen space.</summary>
        internal static float ScreenCounts(CustomInputState state, MappingSource src, bool forX)
        {
            var (cx, cy) = SourceCoercion.ReadGyroMouseCounts(state, src, 0, "", 0.016f, forX);
            return forX ? -cx : -cy;
        }

        /// <summary>Runs <paramref name="read"/> on a centered device with
        /// the given gyro rates, in rad/s on SDL's axes (positive pitch is
        /// the nose up, positive yaw the nose left, positive roll the top
        /// to the left), under unit tuning in Local space.</summary>
        internal static T AtRate<T>(Func<CustomInputState, T> read,
            float pitch = 0, float yaw = 0, float roll = 0)
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;
                SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                {
                    SensH = 1, SensV = 1, OutputCurve = "Linear", Space = "Local", Grip = "Pointing",
                };
                var state = new CustomInputState();
                for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
                state.Gyro[0] = pitch;
                state.Gyro[1] = yaw;
                state.Gyro[2] = roll;
                return read(state);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }
    }
}
