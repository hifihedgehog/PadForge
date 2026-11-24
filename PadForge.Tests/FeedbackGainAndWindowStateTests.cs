using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the force-feedback gain findings and the window-state
    /// findings in the 2026-09-15 audit. The engine halves are behavioral; the
    /// window halves are contracts against the source, because they concern
    /// notification wiring a test cannot observe.
    /// </summary>
    public class FeedbackGainAndWindowStateTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        private static string Step2() => Read("PadForge.App", "Common", "Input",
            "InputManager.Step2.UpdateInputStates.cs");
        private static string MainWindow() => Read("PadForge.App", "MainWindow.xaml.cs");

        // C65: the reused payload kept the previous device's gain.

        /// <summary>The combined vibration object is one instance reused for
        /// every device on every tick. A frame with no directional force
        /// cleared two flags and left the rest, and the wheel rumble read
        /// multiplies by the device gain with no flag to gate on, so a
        /// previous effect's gain silently attenuated the next device's buzz
        /// until something overwrote it. Neutral is 255.</summary>
        [Fact]
        public void AScalarFrameResetsTheWholeDirectionalPayload()
        {
            string src = Step2();
            int i = src.IndexOf("// Clear stale directional data from previous frame.",
                StringComparison.Ordinal);
            Assert.True(i > 0, "the clear is gone");
            int end = src.IndexOf("\n            }", i, StringComparison.Ordinal);
            string body = src.Substring(i, end - i);

            Assert.Contains("HasDirectionalData = false;", body);
            Assert.Contains("HasConditionData = false;", body);
            Assert.Contains("DeviceGain = 255;", body);
            Assert.Contains("SignedMagnitude = 0;", body);
            Assert.Contains("ConditionAxisCount = 0;", body);
        }

        /// <summary>The neutral value really is what the field defaults to and
        /// what the constant-force evaluator writes, so 255 is the right
        /// neutral rather than an invented one.</summary>
        [Fact]
        public void TwoFiftyFiveIsTheNeutralDeviceGain()
        {
            Assert.Equal((byte)255, new Vibration().DeviceGain);
        }

        // C69: the wheel buzz had the overall gain applied twice.

        /// <summary>The motors reaching the wheel rumble read were already
        /// scaled by the overall gain, and the helper applies it again, so the
        /// setting was squared: fifty percent produced a quarter-strength
        /// buzz. The directional twin reads the signed magnitude, which the
        /// per-device scale never touches, so it keeps its gain.</summary>
        [Fact]
        public void TheWheelBuzzAppliesTheOverallGainOnlyOnce()
        {
            string src = Step2();
            Assert.Contains("ForceFeedbackState.ComputeWheelRumbleLevel(cv, 100)", src);
            Assert.DoesNotContain("ComputeWheelRumbleLevel(cv, overallGain)", src);
            // The directional reads keep theirs.
            Assert.Contains("ComputeWheelSteeringLevel(cv, overallGain)", src);
        }

        /// <summary>The helper itself is unchanged, so a zero gain still
        /// silences and a full gain still buzzes.</summary>
        [Fact]
        public void TheHelperStillHonorsTheGainItIsGiven()
        {
            var v = new Vibration { LeftMotorSpeed = 40000, RightMotorSpeed = 40000, DeviceGain = 255 };

            // Zero gain is silent at every phase, so this one is absolute.
            Assert.Equal(0, ForceFeedbackState.ComputeWheelRumbleLevel(v, 0));

            // Full gain oscillates: the level is mag * sin(phase) with phase
            // taken from the wall clock, so it is negative for half of every
            // period and truncates to zero at the two crossings. Sample across
            // real time until a non-zero lands, the way the round 20 suite
            // does, rather than pinning one reading.
            bool buzzed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!buzzed && sw.ElapsedMilliseconds < 500)
            {
                if (ForceFeedbackState.ComputeWheelRumbleLevel(v, 100) != 0) buzzed = true;
                else System.Threading.Thread.Sleep(1);
            }
            Assert.True(buzzed, "full gain never produced a wheel level");
        }

        // C64: the gain came from a different device than the force.

        /// <summary>The force is taken from the first device that HAS one and
        /// the gain was taken from the first device processed. On a slot where
        /// those differ, the second device's effect was scaled by the first
        /// device's setting.</summary>
        [Fact]
        public void TheGainComesFromTheDeviceThatProducedTheForce()
        {
            string src = Step2();
            Assert.Contains("PadSetting directionalPadSetting = null;", src);
            Assert.Contains("directionalPadSetting = devicePs;", src);
            Assert.Contains("(directionalPadSetting ?? firstPadSetting)?.ForceOverall", src);
        }

        // C212, C193, C194, C196, C206, C215, C208: window and persistence.

        /// <summary>Leaving full screen through the title bar cleared the
        /// runtime flag and left the saved one set, so the next launch came up
        /// full screen and skipped the saved maximized restore.</summary>
        [Fact]
        public void LeavingFullScreenThroughTheTitleBarIsRecorded()
        {
            string cs = MainWindow();
            int i = cs.IndexOf("// Exit full screen before TitleBar toggles maximize/restore.",
                StringComparison.Ordinal);
            Assert.True(i > 0, "the title-bar exit is gone");
            string body = cs.Substring(i, Math.Min(900, cs.Length - i));

            Assert.Contains("MainWindowFullScreen = false;", body);
        }

        /// <summary>The balloon timer decided the window was up from the
        /// window STATE, and closing to tray hides a window whose state is
        /// still Normal, so the icon went down while the window was hidden and
        /// the running app had no way back.</summary>
        [Fact]
        public void TheBalloonTimerJudgesVisibilityNotWindowState()
        {
            string cs = MainWindow();
            var m = Regex.Match(cs,
                @"if \(_notifyIcon != null && IsVisible\s*\r?\n\s*&& WindowState != WindowState\.Minimized");
            Assert.True(m.Success, "the balloon timer still hides the icon by window state alone");
        }

        /// <summary>Every one of these is persisted on both legs and has a
        /// control, and nothing marked the file dirty when it changed, so an
        /// isolated edit in a clean session was lost on close.</summary>
        [Theory]
        [InlineData("nameof(DashboardViewModel.EnableAutomation)")]
        [InlineData("nameof(DashboardViewModel.AutomationLevel)")]
        [InlineData("nameof(DashboardViewModel.EnablePlugins)")]
        [InlineData("nameof(DashboardViewModel.PluginListRevision)")]
        [InlineData("nameof(DashboardViewModel.TestControllerOn)")]
        [InlineData("nameof(DashboardViewModel.TestControllerSpeed)")]
        [InlineData("nameof(DashboardViewModel.ReplayOn)")]
        [InlineData("nameof(DashboardViewModel.ReplayRepeat)")]
        [InlineData("nameof(PadViewModel.FlickRotationOffset)")]
        [InlineData("nameof(MappingItem.InvertOutput)")]
        [InlineData("nameof(MappingItem.ParamAccel)")]
        [InlineData("nameof(MappingSourceItem.InvertOutput)")]
        [InlineData("nameof(MappingSourceItem.ParamAccel)")]
        public void EveryPersistedEditMarksTheFileDirty(string entry)
        {
            Assert.Contains(entry, MainWindow());
        }

        /// <summary>Copy From swaps in a new mapping set and the Keep Awake
        /// card reads through the live set, so it needs the same reload the
        /// paste path already does.</summary>
        [Fact]
        public void CopyFromReloadsTheKeepAwakeCard()
        {
            string cs = MainWindow();
            int reloads = Regex.Matches(cs, @"padVm\.ReloadKeepAwake\(\);").Count;
            Assert.True(reloads >= 2,
                $"only {reloads} site reloads Keep Awake, so one wholesale copy path still does not");
        }

        /// <summary>Applying a Workshop profile is a manual switch, like the
        /// Load button, so it records the override and releases a scripted
        /// hold. It was the one lane that did not.</summary>
        [Fact]
        public void ApplyingAWorkshopProfileCountsAsAManualSwitch()
        {
            string cs = MainWindow();
            int i = cs.IndexOf("if (applyAfter)", StringComparison.Ordinal);
            Assert.True(i > 0, "the apply branch is gone");
            string body = cs.Substring(i, Math.Min(700, cs.Length - i));

            Assert.Contains("NoteManualProfileSwitch();", body);
            Assert.True(body.IndexOf("NoteManualProfileSwitch();", StringComparison.Ordinal)
                      < body.IndexOf("LoadProfile(profile.Id);", StringComparison.Ordinal),
                "the override must be recorded before the switch, like the Load button does");
        }

        /// <summary>The tooltip struct carries a marshaled string, so the
        /// native copy it allocates has to be destroyed as well as the block
        /// freed. This runs on every size and DPI change.</summary>
        [Fact]
        public void TheNativeTooltipReleasesItsMarshaledString()
        {
            string cs = MainWindow();
            Assert.Contains("Marshal.DestroyStructure<TOOLINFO>(pti);", cs);
            int destroy = cs.IndexOf("DestroyStructure<TOOLINFO>(pti);", StringComparison.Ordinal);
            int free = cs.IndexOf("FreeHGlobal(pti);", destroy, StringComparison.Ordinal);
            Assert.True(free > destroy, "the block is freed before its contents are destroyed");
        }
    }
}
