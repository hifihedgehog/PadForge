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
