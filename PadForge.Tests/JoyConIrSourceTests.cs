using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine-level coverage for the #151 right Joy-Con NIR "IR Brightness"
    /// source: the cover-as-button threshold, the analog trigger read, the
    /// bipolar read, and the state clone. The SDL fork posts the MCU's
    /// average-intensity byte scaled 0..32767 on joystick axis 6; the wrapper
    /// normalizes that to CustomInputState.JoyConIrIntensity in 0..1, which is
    /// the surface these tests drive.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class JoyConIrSourceTests
    {
        private static MappingSource Src(int deadZone = 0, bool invert = false) => new()
        {
            Descriptor = "IR Brightness",
            DeadZone = deadZone,
            Invert = invert,
        };

        private static CustomInputState State(float intensity) => new()
        {
            JoyConIrIntensity = intensity,
        };

        [Theory]
        [InlineData(0.00f, false)] // camera off / uncovered: below any threshold
        [InlineData(0.10f, false)] // dim: below the 25% default used here
        [InlineData(0.60f, true)]  // covered: bright, above threshold
        [InlineData(1.00f, true)]  // fully saturated
        public void CoverAsButton_FiresAboveThreshold(float intensity, bool expected)
        {
            bool pressed = SourceCoercion.EvaluateForButtonTarget(
                State(intensity), Src(), globalThresholdPercent: 25);
            Assert.Equal(expected, pressed);
        }

        [Fact]
        public void PerRowDeadZone_OverridesGlobalThreshold()
        {
            // 40% intensity: above a 25% global threshold but below a 60%
            // per-row DeadZone override, so the override must win.
            var state = State(0.40f);
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, Src(), 25));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, Src(deadZone: 60), 25));
        }

        [Theory]
        [InlineData(0.00f)]
        [InlineData(0.37f)]
        [InlineData(1.00f)]
        public void ProximityAsTrigger_PassesIntensityThrough(float intensity)
        {
            float v = SourceCoercion.EvaluateForTriggerTarget(State(intensity), Src());
            Assert.Equal(intensity, v, precision: 5);
        }

        [Fact]
        public void TriggerInvert_FlipsToOneMinus()
        {
            // Invert on a trigger target reads "uncovered = pulled": 1 - v.
            float v = SourceCoercion.EvaluateForTriggerTarget(State(0.25f), Src(invert: true));
            Assert.Equal(0.75f, v, precision: 5);
        }

        [Fact]
        public void BipolarRead_IsUnipolarIntensity()
        {
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(State(0.8f), Src());
            Assert.Equal(0.8f, v, precision: 5);
        }

        [Fact]
        public void Clone_CopiesJoyConIrIntensity()
        {
            var state = State(0.42f);
            var clone = state.Clone();
            Assert.Equal(0.42f, clone.JoyConIrIntensity, precision: 5);
        }

        [Fact]
        public void HasJoyConIr_GatesOnRightJoyConIdentity()
        {
            // Standalone right Joy-Con has the camera (SDL names it exactly
            // "Nintendo Switch Joy-Con (R)"); the left Joy-Con and the combined
            // pair ("(L/R)") do not, and neither does a non-Nintendo VID. The
            // Switch 2 right Joy-Con shares the Nintendo VID and ends with the
            // same "Joy-Con (R)" (SDL_ble_switch2joystick.c:1331) but carries a
            // mouse sensor, not an IR camera, so the gate must be exact.
            Assert.True(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (R)" }.HasJoyConIr);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (L)" }.HasJoyConIr);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch Joy-Con (L/R)" }.HasJoyConIr);
            Assert.False(new UserDevice { VendorId = 0x057E, ProductName = "Nintendo Switch 2 Joy-Con (R)" }.HasJoyConIr);
            Assert.False(new UserDevice { VendorId = 0x045E, ProductName = "Nintendo Switch Joy-Con (R)" }.HasJoyConIr);
        }
    }
}
