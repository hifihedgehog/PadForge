using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the translator v11 per-source curve/range channel
    /// (<see cref="MappingSource.ParamCurveExponent"/> /
    /// <see cref="MappingSource.ParamRangeOuter"/>): applied in the generic
    /// bipolar tail after Sensitivity, outer-range rescale FIRST and then the
    /// sign-preserving |x|^e shaping. Off by default, so every existing source
    /// keeps exact pass-through behavior.
    /// </summary>
    public class CurveRangeShapingTests
    {
        private static CustomInputState CenteredState()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            return s;
        }

        [Fact]
        public void Defaults_AreOff_AndIdentity()
        {
            Assert.Equal(0.0, new MappingSource().ParamCurveExponent);
            Assert.Equal(0.0, new MappingSource().ParamRangeOuter);

            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource { Descriptor = "Axis 0" };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void ExponentOne_IsIdentity()
        {
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource { Descriptor = "Axis 0", ParamCurveExponent = 1.0 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void CurveExponent_ShapesMagnitude_PreservesSign()
        {
            var s = CenteredState();
            var src = new MappingSource { Descriptor = "Axis 0", ParamCurveExponent = 2.0 };

            s.Axis[0] = 32768 + 16384; // +0.5
            Assert.Equal(0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 - 16384; // -0.5: same magnitude, sign carried
            Assert.Equal(-0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void RelaxedExponent_AmplifiesSlowRange()
        {
            var s = CenteredState();
            s.Axis[0] = 32768 + 8192; // +0.25
            var src = new MappingSource { Descriptor = "Axis 0", ParamCurveExponent = 0.5 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void RangeOuter_ReachesFullDeflectionAtTheRadius()
        {
            var s = CenteredState();
            var src = new MappingSource { Descriptor = "Axis 0", ParamRangeOuter = 0.5 };

            s.Axis[0] = 32768 + 13107; // +0.4 -> 0.8
            Assert.Equal(0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 + 19660; // +0.6, past the radius -> clamped 1.0
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);

            s.Axis[0] = 32768 - 13107; // -0.4 -> -0.8, sign carried
            Assert.Equal(-0.8f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void Order_IsOuterRescaleThenCurve()
        {
            // 0.4 / 0.5 = 0.8, then 0.8^2 = 0.64. The reversed order would
            // give 0.4^2 / 0.5 = 0.32, so this pins the seam's ordering.
            var s = CenteredState();
            s.Axis[0] = 32768 + 13107; // +0.4
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamRangeOuter = 0.5,
                ParamCurveExponent = 2.0,
            };
            Assert.Equal(0.64f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void AppliesAfterSensitivity_OnTheClampedValue()
        {
            // Sensitivity 2 lifts +0.4 to the +0.8 clamp input, then the
            // curve squares it: 0.8^2 = 0.64. Curve-before-sensitivity
            // would give (0.4^2) * 2 = 0.32.
            var s = CenteredState();
            s.Axis[0] = 32768 + 13107; // +0.4
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                Sensitivity = 2.0,
                ParamCurveExponent = 2.0,
            };
            Assert.Equal(0.64f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void Invert_FlipsAfterShaping_MagnitudeUnchanged()
        {
            // The evaluator's Invert negates the already-shaped value; the
            // magnitude math never sees a second sign operation.
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                Invert = true,
                ParamCurveExponent = 2.0,
            };
            Assert.Equal(-0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void RidesTheGamepadStickAlias()
        {
            // The translator stamps "Gamepad ...Stick" descriptors; they
            // canonicalize to the same generic tail.
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // LeftStickX +0.5
            var src = new MappingSource
            {
                Descriptor = "Gamepad LeftStickX",
                ParamCurveExponent = 2.0,
            };
            Assert.Equal(0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void CenterStaysCentered_UnderBothParams()
        {
            var s = CenteredState();
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamRangeOuter = 0.5,
                ParamCurveExponent = 2.0,
            };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 5);
        }

        [Fact]
        public void SurvivesCloneAndPersistedRoundTrip()
        {
            var src = new MappingSource
            {
                Descriptor = "Axis 0",
                ParamCurveExponent = 2.5,
                ParamRangeOuter = 0.879,
            };
            var clone = src.Clone();
            Assert.Equal(2.5, clone.ParamCurveExponent);
            Assert.Equal(0.879, clone.ParamRangeOuter);

            var ser = new XmlSerializer(typeof(MappingSource));
            string xml;
            using (var sw = new StringWriter()) { ser.Serialize(sw, src); xml = sw.ToString(); }
            MappingSource restored;
            using (var sr = new StringReader(xml)) { restored = (MappingSource)ser.Deserialize(sr); }
            Assert.Equal(2.5, restored.ParamCurveExponent);
            Assert.Equal(0.879, restored.ParamRangeOuter);
        }
    }
}
