using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The microphone rows came from a walk of Windows' capture endpoints
    /// every 4 s, about 100 ms of the audio service's time a walk. Windows
    /// reports every endpoint that comes, goes, changes state or is renamed,
    /// so the walk runs after a report, and every 4 s only when the report
    /// could not be registered.
    /// </summary>
    public class MicEndpointWatchTests
    {
        [Fact]
        public void AWatchedListIsWalkedOnlyAfterAReport()
        {
            Assert.False(InputManager.MicSweepDue(running: false, watched: true, stale: false, now: 10_000, nextAt: 0));
            Assert.True(InputManager.MicSweepDue(running: false, watched: true, stale: true, now: 10_000, nextAt: 0));
            // One walk at a time, and a burst of reports is one walk.
            Assert.False(InputManager.MicSweepDue(running: true, watched: true, stale: true, now: 10_000, nextAt: 0));
            Assert.False(InputManager.MicSweepDue(running: false, watched: true, stale: true, now: 10_000, nextAt: 10_001));
        }

        /// <summary>A registration that stops reporting would freeze the
        /// rows, so a watched list is walked when the device-change gate says
        /// so too, and the gate is asked only when a walk could start.</summary>
        [Fact]
        public void AWatchedListIsWalkedWhenTheDeviceGateSays()
        {
            int asks = 0;
            bool Yes() { asks++; return true; }
            bool No() { asks++; return false; }
            Assert.True(InputManager.MicSweepDue(running: false, watched: true, stale: false, now: 10_000, nextAt: 0, changed: Yes));
            Assert.False(InputManager.MicSweepDue(running: false, watched: true, stale: false, now: 10_000, nextAt: 0, changed: No));
            Assert.Equal(2, asks);
            Assert.False(InputManager.MicSweepDue(running: true, watched: true, stale: false, now: 10_000, nextAt: 0, changed: Yes));
            Assert.False(InputManager.MicSweepDue(running: false, watched: true, stale: false, now: 9_999, nextAt: 10_000, changed: Yes));
            Assert.Equal(2, asks);
        }

        /// <summary>The recognizer service was started by the 4 s walk,
        /// which retried a Start that failed. The walk runs after a report
        /// now, so the 4 s pass retries the Start while no service runs.</summary>
        [Fact]
        public void TheFourSecondPassRetriesAVoiceStartThatFailed()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs")).Replace("\r\n", "\n");
            int pass = source.IndexOf("_micReconcileAt = now + _micSweepIntervalMs;", System.StringComparison.Ordinal);
            Assert.True(pass > 0, "the 4 s pass moved");
            int end = source.IndexOf("var current = _micEndpointSnapshot;", pass, System.StringComparison.Ordinal);
            Assert.True(end > pass);
            string body = source.Substring(pass, end - pass);
            Assert.Contains("if (PadForge.Services.VoiceMacroService.Active == null && !PadForge.Services.VoiceMacroService.SuppressStart && !_micSweepRunning)", body);
            Assert.Contains("System.Threading.Tasks.Task.Run(() => PadForge.Services.VoiceMacroService.Start());", body);
        }

        /// <summary>The walk check runs on every device pass (2 s, 5 s with no
        /// slot online), and a lambda that captured a local made a closure and
        /// a delegate on each. The gate's ask is made once.</summary>
        [Fact]
        public void TheMicGateAskIsMadeOnce()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("_micGateAsk ??= () => _micScan.Due(Environment.TickCount64)", source);
            Assert.DoesNotContain("() => _micScan.Due(now)", source);
        }

        [Fact]
        public void AnUnwatchedListKeepsTheTimedWalk()
        {
            Assert.True(InputManager.MicSweepDue(running: false, watched: false, stale: false, now: 10_000, nextAt: 10_000));
            Assert.False(InputManager.MicSweepDue(running: false, watched: false, stale: false, now: 9_999, nextAt: 10_000));
        }

        [Fact]
        public void EveryReportThatCanChangeTheRowsMarksTheListStale()
        {
            var watch = new InputManager.MicEndpointWatch(null);
            Assert.True(watch.Stale);
            IMMNotificationClient client = watch;
            void Check(System.Action report)
            {
                watch.Take();
                Assert.False(watch.Stale);
                report();
                Assert.True(watch.Stale);
            }
            Check(() => client.OnDeviceAdded("id"));
            Check(() => client.OnDeviceRemoved("id"));
            Check(() => client.OnDeviceStateChanged("id", DeviceState.Active));
            Check(() => client.OnPropertyValueChanged("id", new PropertyKey(System.Guid.Empty, 14)));
            Check(() => client.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "id"));
        }
    }
}
