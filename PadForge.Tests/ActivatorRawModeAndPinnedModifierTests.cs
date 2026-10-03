using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two device-identity contracts in the mapping evaluator.
    ///
    /// <para>Force Raw Joystick Mode bypasses SDL's gamepad remapping, so a
    /// gamepad in that mode does not report the trigger layout and its Axis 2
    /// can rest centered. Classifying it as a unipolar trigger made a centered
    /// axis read as half pulled, which is the same shape as the rest-engaged
    /// bug the trigger-scale change fixed for ordinary gamepads.</para>
    ///
    /// <para>A pinned modifier whose device is offline must read released. The
    /// cycle and chord companions already use the all-rest sentinel for this;
    /// the invert-on-hold modifier fell back to the state of whatever device
    /// was being processed and could borrow its matching button.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ActivatorRawModeAndPinnedModifierTests : IDisposable
    {
        private readonly DeviceCollection savedDevices = SettingsManager.UserDevices;

        public void Dispose() => SettingsManager.UserDevices = savedDevices;

        private static bool IsUnipolar(string descriptor, string deviceGuid)
        {
            var m = typeof(InputManager).GetMethod("IsUnipolarActivatorSource",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(m != null, "IsUnipolarActivatorSource is gone");
            return (bool)m.Invoke(null, new object[] { descriptor, deviceGuid });
        }

        private static Guid SeedGamepad(bool forceRaw)
        {
            var id = Guid.NewGuid();
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = id,
                ProductGuid = id,
                InstanceName = "Test pad",
                ProductName = "Test pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                IsEnabled = true,
                ForceRawJoystickMode = forceRaw,
            });
            SettingsManager.UserDevices = devices;
            return id;
        }


        private static Guid SeedVrController()
        {
            var id = Guid.NewGuid();
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = id,
                ProductGuid = id,
                InstanceName = "VR Controller (Right)",
                ProductName = "VR Controller (Right)",
                CapType = InputDeviceType.VrController,
                IsOnline = true,
                IsEnabled = true,
            });
            SettingsManager.UserDevices = devices;
            return id;
        }

        /// <summary>
        /// A VR controller's trigger and grip rest at zero and travel one
        /// way, so they are unipolar even though they sit at Axis 8 and
        /// Axis 9 rather than the gamepad's 2 and 5.
        ///
        /// <para>Without this they took the bipolar branch, where a resting
        /// zero reads as full deflection, and an Axis Past Threshold
        /// activator on the trigger was engaged the moment it was saved.
        /// That is #443 reproduced on a device type added later.</para>
        /// </summary>
        [Theory]
        [InlineData("Axis 8")]
        [InlineData("Axis 9")]
        public void AVrControllerTriggerAndGripAreUnipolar(string descriptor)
        {
            var id = SeedVrController();
            Assert.True(IsUnipolar(descriptor, id.ToString()),
                        descriptor + " read as bipolar, so it rests engaged");
        }

        /// <summary>The VR controller's POSE axes are centered, so they stay
        /// bipolar. Treating the whole device as unipolar would break the
        /// other eight axes to fix two.</summary>
        [Theory]
        [InlineData("Axis 0")]
        [InlineData("Axis 5")]
        [InlineData("Axis 6")]
        [InlineData("Axis 7")]
        public void AVrControllerPoseAndStickAxesStayBipolar(string descriptor)
        {
            var id = SeedVrController();
            Assert.False(IsUnipolar(descriptor, id.ToString()),
                         descriptor + " is a centered axis and must stay bipolar");
        }
        [Theory]
        [InlineData("Axis 2")]
        [InlineData("Axis 5")]
        public void AMappedGamepadTriggerAxisStaysUnipolar(string descriptor)
        {
            var id = SeedGamepad(forceRaw: false);
            Assert.True(IsUnipolar(descriptor, id.ToString()));
        }

        [Theory]
        [InlineData("Axis 2")]
        [InlineData("Axis 5")]
        public void ARawModeGamepadAxisIsBipolar(string descriptor)
        {
            var id = SeedGamepad(forceRaw: true);
            Assert.False(IsUnipolar(descriptor, id.ToString()),
                "raw joystick indices do not carry SDL's trigger layout");
        }

        /// <summary>A slider rests at zero whatever the device does, so raw
        /// mode must not change it. Positive control for the change above.</summary>
        [Fact]
        public void ASliderStaysUnipolarInRawMode()
        {
            var id = SeedGamepad(forceRaw: true);
            Assert.True(IsUnipolar("Slider 0", id.ToString()));
        }

        /// <summary>An invert-on-hold modifier pinned to an offline device
        /// reads released. It never borrows the state of the device being
        /// processed, and it is skipped rather than read against the all-rest
        /// sentinel: that state is bool-like only, and its zeroed axes read a
        /// stick ring or an inverted axis as held (AnyDeviceMissingAxisTests
        /// runs the ring case).</summary>
        [Fact]
        public void AnOfflinePinnedInvertModifierReadsReleased()
        {
            string src = RepoText("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs");
            int at = src.IndexOf("private static bool IsInvertOnHoldActive", StringComparison.Ordinal);
            Assert.True(at > 0, "IsInvertOnHoldActive is gone");
            int end = src.IndexOf("\n        private static", at + 40, StringComparison.Ordinal);
            Assert.True(end > at);
            string body = src.Substring(at, end - at);
            Assert.Matches(@"s = LookupDeviceState\(src\.DeviceGuid\);\s*if \(s == null\) continue;", body);
            Assert.DoesNotContain("LookupDeviceState(src.DeviceGuid) ?? fallbackState", body);
            Assert.DoesNotContain("OfflinePinnedRestState", body);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(
                new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
