using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Common.Input.Flydigi;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The poll thread's Read step allocates nothing a pass. The runtime
    /// meter read 48 bytes a device a pass on a PC with twelve rows (about
    /// 580 bytes a pass, 580 KB a second at 1000 Hz): the Flydigi Apex
    /// read hook captured its parameters in the lambda of its open, so the
    /// closure was made at the method's entry for every device, an Apex pad
    /// or not.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PollReadAllocationTests
    {
        private static UserDevice Pad(ushort vid, ushort pid)
        {
            var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Read probe");
            wrapper.SetConnected(true);
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = new CustomInputState(),
                IsOnline = true, CapType = InputDeviceType.Gamepad, VendorId = vid, ProdId = pid,
            };
        }

        [Fact]
        public void TheFlydigiReadHook_AllocatesNothingForAnotherPad()
        {
            var ud = Pad(0x054C, 0x0CE6);
            var state = new CustomInputState();
            Assert.False(FlydigiApexService.IsCandidate(ud.VendorId, ud.ProdId));
            Assert.Equal(new long[] { 0, 0, 0 },
                AllocationWindow.Clean(() => FlydigiApexService.OnPolled(ud, state), 1000));
        }

        /// <summary>The dispatcher keepalive asks every created slot each
        /// pass whether a swipe pulse lives on it. The pulse cells stay after
        /// their burst ends, so the answer is read without walking them.</summary>
        [Fact]
        public void TheSwipePulseKeepalive_AllocatesNothingWithEndedPulses()
        {
            try
            {
                TouchpadPulseService.Clear();
                TouchpadPulseService.Pulse(0, Guid.NewGuid(), 0.5f, nowMs: 0);
                TouchpadPulseService.Pulse(1, Guid.NewGuid(), 0.5f, nowMs: 0);
                Assert.Equal(new long[] { 0, 0, 0 },
                    AllocationWindow.Clean(() => TouchpadPulseService.IsSlotActive(0), 1000));
            }
            finally { TouchpadPulseService.Clear(); }
        }

        [Fact]
        public void TheReadStep_AllocatesLessThanAByteAPass()
        {
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            var created = SettingsManager.SlotCreated;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                var im = new InputManager();
                var assigned = Pad(0x054C, 0x0CE6);
                SettingsManager.UserDevices.Items.Add(assigned);
                SettingsManager.UserDevices.Items.Add(Pad(0x045E, 0x0B13));
                SettingsManager.UserDevices.Items.Add(Pad(0x1234, 0x5678));
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = assigned.InstanceGuid, MapTo = 0 });
                SettingsManager.SlotCreated[0] = true;
                // A swipe tick on slot 0 that has since ended: the state the
                // pulse cells keep after the first swipe haptic of a session,
                // until a profile switch clears them.
                TouchpadPulseService.Clear();
                TouchpadPulseService.Pulse(0, assigned.InstanceGuid, 0.5f, nowMs: 0);
                var step = (Action)Delegate.CreateDelegate(typeof(Action), im,
                    typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.NonPublic | BindingFlags.Instance));
                foreach (long bytes in AllocationWindow.Clean(step, 1000))
                    Assert.True(bytes < 1000, $"the Read step allocated {bytes} bytes over 1000 passes");
            }
            finally
            {
                TouchpadPulseService.Clear();
                SettingsManager.UserDevices = devices;
                SettingsManager.UserSettings = settings;
                SettingsManager.SlotCreated = created;
            }
        }
    }
}
