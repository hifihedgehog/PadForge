using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The status line says why the headset is not arriving (issue #403).
    ///
    /// <para>The OpenXR backend can fail four distinguishable ways and the
    /// code already tells them apart: nothing installed, a runtime with no
    /// headset, a runtime that will not grant a background session, and a
    /// session that threw. To a user they all look the same, which is a row
    /// whose axes never move. If the line cannot name them, the distinction
    /// the code makes is worth nothing.</para>
    /// </summary>
    public class OpenXrHeadTrackerStatusTests
    {
        private static string Status(HeadTrackerDevice device) => (string)typeof(InputService)
            .GetMethod("BuildHeadTrackerStatus", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { device });

        /// <summary>A row with OpenXR on and no live source, reporting the
        /// given state. The row falls back to its last observed state when
        /// the source object is gone, which is the seam this uses.</summary>
        private static HeadTrackerDevice RowInState(OpenXrSourceState state)
        {
            var device = new HeadTrackerDevice(
                udp: false, port: 4242, freeTrack: false, configVersion: 0, now: null,
                freeTrackFactory: null, configureFirewall: _ => { },
                openXr: true, openXrManifest: null);
            device.AttachForTest();
            typeof(HeadTrackerDevice)
                .GetField("_openXrLastState", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(device, state);
            return device;
        }

        [Theory]
        [InlineData(OpenXrSourceState.NoRuntime, "HeadTracker_StatusOpenXrNoRuntime")]
        [InlineData(OpenXrSourceState.NoHeadset, "HeadTracker_StatusOpenXrNoHeadset")]
        [InlineData(OpenXrSourceState.NotSupported, "HeadTracker_StatusOpenXrNotSupported")]
        [InlineData(OpenXrSourceState.Failed, "HeadTracker_StatusOpenXrFailed")]
        [InlineData(OpenXrSourceState.Connecting, "HeadTracker_StatusOpenXrConnecting")]
        [InlineData(OpenXrSourceState.Ended, "HeadTracker_StatusOpenXrEnded")]
        public void EveryFailureStateGetsItsOwnLine(OpenXrSourceState state, string key)
        {
            using var device = RowInState(state);
            Assert.Equal(state, device.OpenXrState);

            string expected = (string)typeof(Strings).GetProperty(key).GetValue(Strings.Instance);
            Assert.False(string.IsNullOrWhiteSpace(expected), key + " has no text");
            Assert.Equal(expected, Status(device));
        }

        /// <summary>A session the runtime ended reads as ended, not as one
        /// still starting, and the state is appended so every earlier value
        /// keeps its number.</summary>
        [Fact]
        public void AnEndedSessionIsAppendedAndNamed()
        {
            Assert.Equal(7, (int)OpenXrSourceState.Ended);
            Assert.Equal(OpenXrSourceState.Ended, Enum.GetValues<OpenXrSourceState>()[^1]);
            Assert.NotEqual(Strings.Instance.HeadTracker_StatusOpenXrConnecting,
                Strings.Instance.HeadTracker_StatusOpenXrEnded);
        }

        /// <summary>Each message is distinct. Four states collapsing onto one
        /// sentence would leave the user exactly where they started.</summary>
        [Fact]
        public void TheFailureLinesAreAllDifferent()
        {
            var s = Strings.Instance;
            var lines = new List<string>
            {
                s.HeadTracker_StatusOpenXrNoRuntime,
                s.HeadTracker_StatusOpenXrNoHeadset,
                s.HeadTracker_StatusOpenXrNotSupported,
                s.HeadTracker_StatusOpenXrFailed,
                s.HeadTracker_StatusOpenXrConnecting,
                s.HeadTracker_StatusOpenXrEnded,
            };
            foreach (var line in lines) Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.Equal(lines.Count, new HashSet<string>(lines).Count);
        }

        /// <summary>A delivering headset names the runtime it came from, so a
        /// user with more than one installed can see which one answered.</summary>
        [Fact]
        public void ADeliveringHeadsetNamesItsRuntime()
        {
            using var device = RowInState(OpenXrSourceState.Running);
            device.InjectOpenXrPose(new double[HeadPose.PoseCount]);
            device.GetCurrentState();
            Assert.Equal(HeadTrackerSource.OpenXr, device.Source);
            Assert.Equal(string.Format(Strings.Instance.HeadTracker_StatusOpenXr_Format,
                                       device.OpenXrRuntimeName), Status(device));
        }

        /// <summary>A row with OpenXR off never mentions it, so the two older
        /// backends read exactly as they did.</summary>
        [Fact]
        public void AnOpenXrFreeRowSaysNothingAboutIt()
        {
            using var device = new HeadTrackerDevice(
                udp: false, port: 4242, freeTrack: false, configVersion: 0, now: null,
                freeTrackFactory: null, configureFirewall: _ => { });
            device.AttachForTest();
            Assert.Equal(Strings.Instance.Common_Stopped, Status(device));
        }

        /// <summary>A manifest that names nothing ends as "no runtime".
        ///
        /// <para>This drives the real source rather than a field, so it
        /// covers the path a user hits after uninstalling the runtime they
        /// picked, and it proves the state is reached promptly rather than
        /// leaving the row stuck on Connecting.</para></summary>
        [Fact]
        public void AMissingRuntimeSettlesAsNoRuntime()
        {
            string missing = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "padforge-no-such-runtime.json");
            var source = new OpenXrHeadPoseSource(() => missing, _ => { });
            try
            {
                source.Start();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (source.State == OpenXrSourceState.Connecting && DateTime.UtcNow < deadline)
                    Thread.Sleep(25);
                Assert.Equal(OpenXrSourceState.NoRuntime, source.State);
            }
            finally { source.Dispose(); }

            using var device = RowInState(OpenXrSourceState.NoRuntime);
            Assert.Equal(Strings.Instance.HeadTracker_StatusOpenXrNoRuntime, Status(device));
        }
    }
}
