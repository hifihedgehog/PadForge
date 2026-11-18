using System;
using System.IO;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A rumble capability SDL finds after a joystick is open
    /// reaches PadForge. The fork's Bluetooth Switch 2 driver sets the
    /// property when a recovery finds the vibration characteristic the
    /// connect could not (hifihedgehog/SDL#49), and PadForge had read it at
    /// the open alone: the joystick is asked again every five seconds until
    /// it has rumble, and the rumble pass makes the cache loading did not.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class LateRumbleCapabilityTests
    {
        // A handle no other test's wrapper holds. Only the reader stood in
        // below is handed it.
        private static readonly IntPtr Handle = new IntPtr(0x51494C);

        private static void Set(SdlDeviceWrapper wrapper, string name, object value)
            => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);

        /// <summary>Runs the body with the reader answering for this file's
        /// handle and SDL's own reader answering for every other one.</summary>
        private static void WithReader(Func<bool> answer, Action<Func<int>> body)
        {
            var real = SdlDeviceWrapper.RumbleCapabilityReader;
            int asked = 0;
            SdlDeviceWrapper.RumbleCapabilityReader = joystick =>
            {
                if (joystick != Handle) return real(joystick);
                asked++;
                return answer();
            };
            try { body(() => asked); }
            finally { SdlDeviceWrapper.RumbleCapabilityReader = real; }
        }

        [Fact]
        public void AJoystickWithoutRumbleIsAskedAgainEveryFiveSecondsUntilItHasIt()
        {
            using var wrapper = new SdlDeviceWrapper();
            bool sdlSays = false;
            WithReader(() => sdlSays, asked =>
            {
                try
                {
                    Set(wrapper, "VendorId", (ushort)0x057E);
                    Set(wrapper, "ProductId", (ushort)0x2066);
                    Set(wrapper, "Joystick", Handle);
                    Assert.False(wrapper.HasRumble, "control: the open found no rumble");
                    Assert.Equal(5000, SdlDeviceWrapper.RumbleCapabilityRereadMs);

                    const long start = 1_000_000;
                    Assert.False(wrapper.TakeLateRumbleCapability(start));
                    Assert.Equal(1, asked());
                    // Not again within the five seconds.
                    Assert.False(wrapper.TakeLateRumbleCapability(start + 4999));
                    Assert.Equal(1, asked());
                    Assert.False(wrapper.TakeLateRumbleCapability(start + 5000));
                    Assert.Equal(2, asked());
                    Assert.False(wrapper.HasRumble);

                    // SDL sets the property. The next read that is due takes it.
                    sdlSays = true;
                    Assert.False(wrapper.TakeLateRumbleCapability(start + 9999));
                    Assert.False(wrapper.HasRumble);
                    Assert.True(wrapper.TakeLateRumbleCapability(start + 10000));
                    Assert.Equal(3, asked());
                    Assert.True(wrapper.HasRumble);

                    // A capability found is kept, and SDL is not asked again.
                    sdlSays = false;
                    Assert.False(wrapper.TakeLateRumbleCapability(start + 60000));
                    Assert.Equal(3, asked());
                    Assert.True(wrapper.HasRumble);
                }
                finally { Set(wrapper, "Joystick", IntPtr.Zero); }
            });
        }

        [Fact]
        public void AJoystickThatIsNotOpenIsNotAsked()
        {
            using var wrapper = new SdlDeviceWrapper();
            WithReader(() => true, asked =>
            {
                Assert.False(wrapper.TakeLateRumbleCapability(1_000_000));
                Assert.False(wrapper.HasRumble);
                Assert.Equal(0, asked());
            });
        }

        /// <summary>The state read asks on both read paths, beside the
        /// firmware it reads again for the same reason, and not in the
        /// gamepad path alone.</summary>
        [Fact]
        public void TheStateReadAsksOnBothReadPaths()
        {
            string source = RepoText("PadForge.Engine", "Common", "SdlDeviceWrapper.cs").Replace("\r\n", "\n");
            int read = source.IndexOf("public CustomInputState GetCurrentState(bool forceRaw = false)", StringComparison.Ordinal);
            int end = source.IndexOf("\n        }\n", read, StringComparison.Ordinal);
            Assert.True(read >= 0 && end > read);
            string body = source.Substring(read, end - read);
            int firmware = body.IndexOf("FirmwareText = ReadFirmwareText(Joystick);", StringComparison.Ordinal);
            int ask = body.IndexOf("TakeLateRumbleCapability(now);", StringComparison.Ordinal);
            int done = body.LastIndexOf("return state;", StringComparison.Ordinal);
            Assert.True(firmware >= 0 && ask > firmware && done > ask, "the state read does not ask after the firmware read");
            // Once, and the gamepad path has no second copy.
            Assert.Equal(ask, body.LastIndexOf("TakeLateRumbleCapability(", StringComparison.Ordinal));
            int gamepad = source.IndexOf("private CustomInputState GetGamepadState()", StringComparison.Ordinal);
            Assert.True(gamepad > end);
            Assert.DoesNotContain("TakeLateRumbleCapability(", source.Substring(gamepad));
        }

        /// <summary>Loading makes a rumble cache for a device that rumbles
        /// then. A device that gained rumble afterward has none, and the
        /// rumble pass returned before it could send a level.</summary>
        [Fact]
        public void TheRumblePassMakesTheCacheOfADeviceThatGainedRumble()
        {
            var manager = new InputManager();
            var pass = typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var web = new WebControllerDevice("late-rumble-" + Guid.NewGuid().ToString("N"), "Browser pad") { HasRumble = false };
            var device = new UserDevice();
            device.LoadFromWebDevice(web);
            Assert.Null(device.ForceFeedbackState);
            Assert.False(InputManager.NeedsARumbleCache(device));

            // Control: a device without rumble gets no cache from the pass.
            pass.Invoke(manager, new object[] { device });
            Assert.Null(device.ForceFeedbackState);

            web.HasRumble = true;
            Assert.True(InputManager.NeedsARumbleCache(device));
            pass.Invoke(manager, new object[] { device });
            Assert.NotNull(device.ForceFeedbackState);
            Assert.False(InputManager.NeedsARumbleCache(device), "a device with a cache needs none");

            Assert.False(InputManager.NeedsARumbleCache(null));
            Assert.False(InputManager.NeedsARumbleCache(new UserDevice()));
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string path = dir.FullName;
            foreach (string part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }
    }
}
