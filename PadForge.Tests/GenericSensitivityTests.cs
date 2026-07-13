using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the #9 generic per-source <see cref="MappingSource.Sensitivity"/>:
    /// an analog multiplier applied to plain "Axis N" / "Slider N" reads (and the
    /// abstract Gamepad sticks / triggers that canonicalize to them), then re-clamped.
    /// It is mutually exclusive with the specialized gyro / mouse / IR sensitivities,
    /// which apply in their own reader branches and return before the generic path.
    /// </summary>
    public class GenericSensitivityTests
    {
        private static CustomInputState CenteredState()
        {
            var s = new CustomInputState();
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            return s;
        }

        [Fact]
        public void Sensitivity_ScalesBipolarAxis()
        {
            var s = CenteredState();
            s.Axis[0] = 32768 + 16384; // +0.5 deflection
            var src = new MappingSource { Descriptor = "Axis 0", Sensitivity = 1.0 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 2);

            src.Sensitivity = 2.0; // 0.5 -> 1.0
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 2);

            src.Sensitivity = 0.5; // 0.5 -> 0.25
            Assert.Equal(0.25f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 2);
        }

        [Fact]
        public void Sensitivity_ClampsAfterScaling()
        {
            var s = CenteredState();
            s.Axis[0] = 65535; // already +1.0
            var src = new MappingSource { Descriptor = "Axis 0", Sensitivity = 3.0 };
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void Sensitivity_ScalesTrigger()
        {
            var s = CenteredState();
            s.Axis[2] = 16384; // ~0.25 of the 0..65535 unipolar range
            var src = new MappingSource { Descriptor = "Axis 2", Sensitivity = 2.0 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForTriggerTarget(s, src), 2);
        }

        [Fact]
        public void Sensitivity_RidesGamepadStickAlias()
        {
            var s = CenteredState();
            s.Axis[0] = 65535; // Gamepad LeftStickX full right
            var src = new MappingSource { Descriptor = "Gamepad LeftStickX", Sensitivity = 0.5 };
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Fact]
        public void Sensitivity_ZeroReadsAsUnity()
        {
            var s = CenteredState();
            s.Axis[0] = 65535;
            // A persisted 0 from a legacy row must read as the 1.0 default, not
            // zero the source (matches the specialized sensitivities' guard).
            var src = new MappingSource { Descriptor = "Axis 0", Sensitivity = 0.0 };
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 3);
        }

        [Theory]
        [InlineData("Axis 0", true)]
        [InlineData("Slider 1", true)]
        [InlineData("Gamepad LeftStickX", true)]
        [InlineData("Gamepad LeftTrigger", true)]
        [InlineData("Gamepad ButtonA", false)] // digital, no analog knob
        [InlineData("Button 0", false)]
        [InlineData("Gyro Pitch", false)]      // specialized sensitivity
        [InlineData("Mouse Position X", false)]
        [InlineData("IR Pointer X", false)]
        [InlineData("Touchpad 0 Finger 0 Pressure", false)]
        public void IsGenericSensitivityDescriptor_GatesAnalogGenericOnly(string descriptor, bool expected)
        {
            Assert.Equal(expected, SourceCoercion.IsGenericSensitivityDescriptor(descriptor));
        }

        [Fact]
        public void Sensitivity_SurvivesCloneAndPersistedRoundTrip()
        {
            var src = new MappingSource { Descriptor = "Axis 0", Sensitivity = 2.5 };
            Assert.Equal(2.5, src.Clone().Sensitivity);

            var ser = new XmlSerializer(typeof(MappingSource));
            string xml;
            using (var sw = new StringWriter()) { ser.Serialize(sw, src); xml = sw.ToString(); }
            MappingSource restored;
            using (var sr = new StringReader(xml)) { restored = (MappingSource)ser.Deserialize(sr); }
            Assert.Equal(2.5, restored.Sensitivity);
        }

        [Fact]
        public void Sensitivity_DefaultsToUnity()
        {
            Assert.Equal(1.0, new MappingSource().Sensitivity);
        }
    }
}
