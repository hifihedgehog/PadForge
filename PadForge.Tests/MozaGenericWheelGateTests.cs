using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #282 (Moza): the generic-wheel spring gate. One predicate now serves
    /// both the Wheel tab's hasGenericWheel check and TryApplyAutoCenterSpring,
    /// and these tests lock its contract: device TYPE decides wheel identity,
    /// axis count does not. The old inline gates required NumHapticAxes &lt;= 1,
    /// which hid the Wheel tab for wheelbase-plus-pedals composites that report
    /// two haptic axes despite a working spring (the Moza shape, per
    /// hid-universal-pidff routing all ten Moza PIDs to the generic PID driver).
    /// </summary>
    public class MozaGenericWheelGateTests
    {
        private const uint Spring = 1u << 7;      // SDL_HAPTIC_SPRING (SDL3Minimal.cs:804)
        private const uint Constant = 1u << 0;    // SDL_HAPTIC_CONSTANT
        private const int Driving = 22;           // InputDeviceType.Driving (InputTypes.cs:79)
        private const int Gamepad = 21;           // InputDeviceType.Gamepad

        [Fact]
        public void SpringCapableWheel_Passes()
        {
            Assert.True(ForceFeedbackState.IsGenericWheelSpringCapable(
                hasHaptic: true, hapticFeatures: Spring | Constant, inputDeviceType: Driving));
        }

        [Fact]
        public void MultiAxisComposite_StillPasses_AxisCountIsNotConsulted()
        {
            // The predicate takes no axis count at all. This test exists so a
            // future "optimization" that reintroduces the parameter has to
            // delete a test that names the Moza wheelbase-plus-pedals case.
            Assert.True(ForceFeedbackState.IsGenericWheelSpringCapable(
                hasHaptic: true, hapticFeatures: Spring, inputDeviceType: Driving));
        }

        [Fact]
        public void SpringCapableGamepad_Refused()
        {
            // The axis clause used to be what kept gamepads out of the spring.
            // Type is the guard now, so a hypothetical spring-advertising
            // gamepad must still be refused.
            Assert.False(ForceFeedbackState.IsGenericWheelSpringCapable(
                hasHaptic: true, hapticFeatures: Spring, inputDeviceType: Gamepad));
        }

        [Fact]
        public void WheelWithoutSpring_Refused()
        {
            Assert.False(ForceFeedbackState.IsGenericWheelSpringCapable(
                hasHaptic: true, hapticFeatures: Constant, inputDeviceType: Driving));
        }

        [Fact]
        public void WheelWithoutHaptic_Refused()
        {
            Assert.False(ForceFeedbackState.IsGenericWheelSpringCapable(
                hasHaptic: false, hapticFeatures: Spring, inputDeviceType: Driving));
        }

        [Fact]
        public void NullDevice_Refused()
        {
            Assert.False(ForceFeedbackState.IsGenericWheelSpringCapable(null));
        }
    }
}
