using System;
using System.Collections;
using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Step 2 forgets the tilt state of every device without a gyro and an
    /// accelerometer on every pass. A device that has none is left alone
    /// without the removal's lock, and one that has one loses it, as
    /// before.
    /// </summary>
    public class GyroTiltForgetTests
    {
        private static IDictionary States(InputManager im)
            => (IDictionary)typeof(InputManager).GetField("_gyroTiltStates", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(im);

        private static object NewState()
            => Activator.CreateInstance(typeof(InputManager).GetNestedType("GyroTiltDeviceState", BindingFlags.NonPublic), nonPublic: true);

        [Fact]
        public void ForgettingRemovesAKeptState_AndLeavesTheOthers()
        {
            var im = new InputManager();
            var states = States(im);
            Guid kept = Guid.NewGuid(), gone = Guid.NewGuid(), never = Guid.NewGuid();
            states[kept] = NewState();
            states[gone] = NewState();
            im.ForgetGyroTiltGravity(never);
            Assert.Equal(2, states.Count);
            im.ForgetGyroTiltGravity(gone);
            Assert.Single(states);
            Assert.True(states.Contains(kept));
            Assert.False(states.Contains(gone));
            im.ForgetGyroTiltGravity(gone);
            Assert.Single(states);
        }
    }
}
