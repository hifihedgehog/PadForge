using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// VR motion controllers as mappable rows (issue #403).
    ///
    /// <para>The reporter asked for "the controllers and headset position".
    /// The headset went first because it needs no action set at all. A
    /// controller needs one, and what it produces has two independent
    /// validity flags: the pose can be tracked while the controls are not
    /// ours to read, because another application holds focus. Publishing
    /// either when it is not valid pins whatever the user last did.</para>
    /// </summary>
    // These read HeadTrackingRuntime's per-axis ranges live, so they
    // share the statics collection rather than racing a test that pins one.
    [Collection("SettingsManagerStatics")]
    public class OpenXrHandDeviceTests
    {
        private static OpenXrHandDevice Row(OpenXrHand hand, Func<long> now = null)
        {
            var device = new OpenXrHandDevice(hand, now);
            device.Open();
            return device;
        }

        private static OpenXrHandState Tracked(double x = 0, double y = 0, double z = 0)
            => new() { PoseValid = true, TX = x, TY = y, TZ = z };

        [Fact]
        public void TheTwoHandsAreSeparateDevices()
        {
            using var left = Row(OpenXrHand.Left);
            using var right = Row(OpenXrHand.Right);

            Assert.NotEqual(left.InstanceGuid, right.InstanceGuid);
            Assert.NotEqual(left.DevicePath, right.DevicePath);
            Assert.NotEqual(left.SdlInstanceId, right.SdlInstanceId);
            Assert.Equal("VR Controller (Left)", left.Name);
            Assert.Equal("VR Controller (Right)", right.Name);
            Assert.Equal(InputDeviceType.VrController, left.GetInputDeviceType());
        }

        /// <summary>A resting controller must not answer the (Any Device)
        /// wildcard. Issue #431 was exactly this: a head tracker sitting
        /// still held both triggers at half pull on every slot.</summary>
        [Fact]
        public void AControllerNeverAnswersTheAnyDeviceWildcard()
        {
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.VrController));
        }

        [Fact]
        public void EveryControlIsNamedAndReachable()
        {
            using var device = Row(OpenXrHand.Right);
            var objects = device.GetDeviceObjects();
            Assert.Equal(OpenXrHandDevice.AxisCount + OpenXrHandDevice.ButtonCount, objects.Length);
            Assert.Equal("Trigger", objects[OpenXrHandDevice.AxisTrigger].Name);
            Assert.Equal("Thumbstick X", objects[OpenXrHandDevice.AxisStickX].Name);
            Assert.Equal("Menu Button",
                objects[OpenXrHandDevice.AxisCount + OpenXrHandDevice.ButtonMenu].Name);
            // Typed as buttons, which is how the Devices page finds a row's
            // named buttons (DevicesViewModel.RebuildNamedButtons).
            for (int i = 0; i < OpenXrHandDevice.ButtonCount; i++)
                Assert.Equal(ObjectGuid.Button, objects[OpenXrHandDevice.AxisCount + i].ObjectTypeGuid);
        }

        /// <summary>A trigger rests at zero and a stick rests at center. The
        /// two conventions live in one state array, and mixing them up leaves
        /// a trigger reading half pulled with nothing touching it.</summary>
        [Fact]
        public void AFreshRowRestsCorrectlyForBothConventions()
        {
            using var device = Row(OpenXrHand.Left);
            var state = device.GetCurrentState();
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisStickX]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisYaw]);
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisSqueeze]);
        }

        [Fact]
        public void PoseAxesFollowTheHeadTrackerConvention()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);

            device.InjectForTest(Tracked(y: 10));       // 10 cm up
            Assert.True(device.GetCurrentState().Axis[OpenXrHandDevice.AxisY] < HeadPose.AxisCenter,
                "raising the hand should read as a stick pushed up");

            device.InjectForTest(Tracked(x: 10));       // 10 cm right
            Assert.True(device.GetCurrentState().Axis[OpenXrHandDevice.AxisX] > HeadPose.AxisCenter);
        }

        /// <summary>
        /// A tracked pose with unsynchronized controls publishes the pose and
        /// releases the controls.
        ///
        /// <para>xrSyncActions answers XR_SESSION_NOT_FOCUSED, a SUCCESS
        /// code, whenever another application holds focus. That is the
        /// ordinary case for a background client, and treating it as live
        /// would hold a trigger down for as long as the game had focus.</para>
        /// </summary>
        [Fact]
        public void AnUnsynchronizedActionSetReleasesTheControls()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);

            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = true, TX = 12,
                ControlsActive = true, Trigger = 1f, PrimaryButton = true,
            });
            var held = device.GetCurrentState();
            Assert.True(held.Axis[OpenXrHandDevice.AxisTrigger] > 60000);
            Assert.True(held.Buttons[OpenXrHandDevice.ButtonPrimary]);

            device.InjectForTest(new OpenXrHandState { PoseValid = true, TX = 12, ControlsActive = false });
            var released = device.GetCurrentState();
            Assert.Equal(0, released.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.False(released.Buttons[OpenXrHandDevice.ButtonPrimary]);
            // The pose is still good, so it keeps reporting.
            Assert.True(released.Axis[OpenXrHandDevice.AxisX] > HeadPose.AxisCenter);
        }

        /// <summary>Losing tracking centers the pose axes but leaves live
        /// controls alone. The two are independent on purpose.</summary>
        [Fact]
        public void LosingThePoseDoesNotReleaseTheButtons()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Left, () => now);
            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = false, ControlsActive = true, PrimaryButton = true, Trigger = 0.5f,
            });
            var state = device.GetCurrentState();
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisX]);
            Assert.True(state.Buttons[OpenXrHandDevice.ButtonPrimary]);
            Assert.True(state.Axis[OpenXrHandDevice.AxisTrigger] > 30000);
        }

        /// <summary>A controller set down mid-game releases everything.</summary>
        [Fact]
        public void AControllerThatGoesQuietReleasesEverything()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Right, () => now);
            device.InjectForTest(new OpenXrHandState
            {
                PoseValid = true, TX = 20, ControlsActive = true, Trigger = 1f, MenuButton = true,
            });
            Assert.NotEqual(0, device.GetCurrentState().Axis[OpenXrHandDevice.AxisTrigger]);

            now += OpenXrHandDevice.SilenceMs + 1;
            var state = device.GetCurrentState();
            Assert.Equal(0, state.Axis[OpenXrHandDevice.AxisTrigger]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[OpenXrHandDevice.AxisX]);
            Assert.False(state.Buttons[OpenXrHandDevice.ButtonMenu]);
            Assert.False(device.IsLive);
        }

        /// <summary>An idle sample is not a sample. A sleeping controller
        /// publishing nothing must not hold the silence timer open, or its
        /// row would claim to be live forever.</summary>
        [Fact]
        public void AnIdleSampleDoesNotCountAsLiveness()
        {
            long now = 1000;
            using var device = Row(OpenXrHand.Left, () => now);
            device.InjectForTest(OpenXrHandState.Empty);
            Assert.False(device.IsLive);

            device.InjectForTest(Tracked(x: 5));
            Assert.True(device.IsLive);
        }
        /// <summary>
        /// A live thumbstick reaches the row's stick axes.
        ///
        /// <para>The action was created and bound on all three interaction
        /// profiles from the first version and never read, so both axes sat
        /// at center forever. The old tests asserted the axis NAME and its
        /// RESTING value, which is exactly what let that ship.</para>
        /// </summary>
        [Fact]
        public void AThumbstickPushReachesTheStickAxes()
        {
            using var device = Row(OpenXrHand.Right);
            device.Publish(new OpenXrHandState
            {
                ControlsActive = true,
                ThumbstickX = 1f,
                ThumbstickY = 1f,
            });

            var state = device.GetCurrentState();
            Assert.True(state.Axis[OpenXrHandDevice.AxisStickX] > HeadPose.AxisCenter,
                        "stick X never left center");
            // Y is stick-oriented, so a forward push reads at the low end.
            Assert.True(state.Axis[OpenXrHandDevice.AxisStickY] < HeadPose.AxisCenter,
                        "stick Y never left center");
        }

        /// <summary>The two hands are separate products, so the offline-row
        /// adoption that matches on ProductGuid cannot hand one hand's saved
        /// mappings to the other.</summary>
        [Fact]
        public void EachHandIsItsOwnProduct()
        {
            using var left = Row(OpenXrHand.Left);
            using var right = Row(OpenXrHand.Right);
            Assert.NotEqual(left.ProductGuid, right.ProductGuid);
            Assert.NotEqual(left.InstanceGuid, right.InstanceGuid);
        }

        /// <summary>
        /// The action layer actually READS the thumbstick.
        ///
        /// <para>The device test above proves the mapping from the state
        /// struct to the axes. It cannot see this defect, because it
        /// publishes the struct directly: the bug was that nothing ever
        /// filled the struct. The action was created, bound on all three
        /// interaction profiles, and never read, and only the layer that
        /// talks to the runtime can be asked about that.</para>
        /// </summary>
        [Fact]
        public void TheActionLayerReadsTheThumbstick()
        {
            string src = RepoSource("PadForge.Engine", "Common", "OpenXr", "OpenXrActions.cs");
            int at = src.IndexOf("public OpenXrHandState Read(", StringComparison.Ordinal);
            Assert.True(at > 0, "Read was not found");
            string body = src.Substring(at);
            Assert.Contains("state.ThumbstickX", body);
            Assert.Contains("state.ThumbstickY", body);
            // Through the runtime, not from a default. A stub assigning zero
            // would satisfy the two lines above.
            Assert.Contains("Vector2(_stick", body);

            // And the type it reads through has to exist, or the read above
            // could only ever be a stub.
            string interop = RepoSource("PadForge.Engine", "Common", "OpenXr", "OpenXrInterop.cs");
            Assert.Contains("XrActionStateVector2f", interop);
            Assert.Contains("XR_TYPE_ACTION_STATE_VECTOR2F = 25", interop);
        }

        /// <summary>Every entry point the action layer asks for has a case in
        /// the session's resolver. The resolver answers null for a name it
        /// does not list, and the thumbstick reader went unresolved that way,
        /// so the sticks sat at center while the test above passed.</summary>
        [Fact]
        public void TheResolverAnswersEveryEntryPointTheActionsResolve()
        {
            string actions = RepoSource("PadForge.Engine", "Common", "OpenXr", "OpenXrActions.cs");
            string session = RepoSource("PadForge.Engine", "Common", "OpenXr", "OpenXrSession.cs");
            var names = System.Text.RegularExpressions.Regex.Matches(actions, @"resolve\(""(xr[A-Za-z0-9]+)""\)");
            Assert.True(names.Count >= 10, "the resolve calls were not found");
            foreach (System.Text.RegularExpressions.Match m in names)
                Assert.Contains("\"" + m.Groups[1].Value + "\" =>", session);
        }

        private static string RepoSource(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var all = new System.Collections.Generic.List<string> { dir.FullName };
            all.AddRange(parts);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(all.ToArray()));
        }

    }
}
