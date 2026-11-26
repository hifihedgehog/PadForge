using System;
using System.Linq;
using System.Reflection;
using PadForge.Common;
using PadForge.Common.Input.Touchpads;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The scroll wheels a mouse row reads (#600): Wheel Travel Sideways is
    /// offered only where the mouse's reader writes the sideways wheel, and
    /// Remote Link carries both wheels' units with nothing lost between its
    /// frames.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseWheelReadersTests
    {
        [Fact]
        public void ATouchpadsMouseRow_IsOfferedNoSidewaysWheel()
        {
            var g = Guid.NewGuid();
            var ud = new UserDevice
            {
                InstanceGuid = g, ProductGuid = g, InstanceName = "Touchpad", ProductName = "Touchpad",
                CapType = InputDeviceType.Mouse, IsOnline = true, IsEnabled = true,
                Device = new TouchpadMouseDevice("tp-path", "mouse-path", g, "Touchpad", 0, 0, null,
                    consume: () => (false, 0, 0, 0, 0)),
            };
            var choices = MappingDisplayResolver.BuildInputChoices(ud);
            Assert.Contains(choices, c => c.Descriptor == SourceCoercion.WheelTravelDescriptor);
            Assert.DoesNotContain(choices, c => c.Descriptor == SourceCoercion.WheelTravelSidewaysDescriptor);
        }

        private static (object acc, MethodInfo accumulate, MethodInfo drain) Accumulator()
        {
            var t = typeof(InputService).GetNestedType("RemoteDeltaAccumulator", BindingFlags.NonPublic);
            Assert.NotNull(t);
            return (Activator.CreateInstance(t, nonPublic: true), t.GetMethod("Accumulate"), t.GetMethod("DrainInto"));
        }

        private static CustomInputState Poll(int wheel = 0, int wheelH = 0)
        {
            var s = new CustomInputState { MouseRawWheel = wheel, MouseRawWheelH = wheelH };
            s.Axis[0] = s.Axis[1] = s.Axis[2] = 32767;
            return s;
        }

        [Fact]
        public void RemoteLink_CarriesEveryNotchOfBothWheels_AndNoneTwice()
        {
            var (acc, accumulate, drain) = Accumulator();
            for (int i = 0; i < 8; i++)
                accumulate.Invoke(acc, new object[] { Poll(i == 3 ? 120 : 0, i == 5 ? -240 : 0), (long)i, true });
            // The outgoing frame is a clone of the last poll's state.
            var frame = Poll(120, -240);
            drain.Invoke(acc, new object[] { frame, true });
            Assert.Equal(120, frame.MouseRawWheel * 8);
            Assert.Equal(-240, frame.MouseRawWheelH * 8);

            var idle = Poll(120, -240);
            drain.Invoke(acc, new object[] { idle, true });
            Assert.Equal(0, idle.MouseRawWheel);
            Assert.Equal(0, idle.MouseRawWheelH);
        }
    }
}
