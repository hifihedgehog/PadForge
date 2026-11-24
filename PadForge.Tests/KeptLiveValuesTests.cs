using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The privacy scrubber's values that come from Windows' services, the
    /// audio endpoints' names and the network adapters' addresses, are read
    /// once and kept until Windows reports a change (#597). Under a load of
    /// one busy process per core their reads took 371 ms and 68 ms at the
    /// median, up to 584 ms and 389 ms, on the line handler of an Automation
    /// read, and were the whole tail of a devices request.
    /// </summary>
    public class KeptLiveValuesTests
    {
        [Fact]
        public void TheAudioEndpointsNames_AreKeptUntilWindowsReportsAChange_Live()
        {
            string[] first = AudioMuteService.EndpointNames();
            string[] again = AudioMuteService.EndpointNames();
            if (!AudioMuteService.EndpointNamesKept)
            {
                // No audio stack, or the notification was refused: each call
                // reads again, as before.
                Assert.NotSame(first, again);
                return;
            }
            Assert.Same(first, again);
            Assert.Equal(AudioMuteService.EnumerateEndpoints().Select(e => e.FriendlyName).OrderBy(n => n, StringComparer.Ordinal),
                first.OrderBy(n => n, StringComparer.Ordinal));

            AudioMuteService.NamesChangedForTest();
            Assert.False(AudioMuteService.EndpointNamesKept);
            string[] read = AudioMuteService.EndpointNames();
            Assert.NotSame(first, read);
            Assert.Equal(first, read);
            Assert.True(AudioMuteService.EndpointNamesKept);
        }

        [Fact]
        public void TheAdaptersAddresses_AreKeptUntilWindowsReportsAChange_Live()
        {
            var first = RecorderHost.Addresses.Current();
            var again = RecorderHost.Addresses.Current();
            Assert.True(RecorderHost.Addresses.Kept);
            Assert.Same(first, again);

            RecorderHost.Addresses.Changed();
            Assert.False(RecorderHost.Addresses.Kept);
            var read = RecorderHost.Addresses.Current();
            Assert.NotSame(first, read);
            Assert.Equal(first, read);
            Assert.True(RecorderHost.Addresses.Kept);
        }

        [Fact]
        public void TheScrubbersHostValues_ReadTheKeptValues()
        {
            string host = OffLimitsSource.Read("PadForge.App/Services/ProblemRecorderHost.cs");
            string live = FenceSource.Body(host, "public virtual void LiveValues(List<(PrivateKind Kind, string Value)> into)");
            Assert.Contains("AudioMuteService.EndpointNames()", live, StringComparison.Ordinal);
            Assert.Contains("into.AddRange(Addresses.Current());", live, StringComparison.Ordinal);
            Assert.DoesNotContain("EnumerateEndpoints()", live, StringComparison.Ordinal);
            Assert.DoesNotContain("GetAllNetworkInterfaces()", live, StringComparison.Ordinal);
            string audio = OffLimitsSource.Read("PadForge.App/Common/Input/AudioMuteService.cs");
            string watch = FenceSource.Body(audio, "private sealed class NameWatch : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient");
            foreach (string report in new[] { "OnDeviceStateChanged(", "OnDeviceAdded(", "OnDeviceRemoved(", "OnPropertyValueChanged(" })
                Assert.Contains("=> NamesChanged();", watch.Split('\n').Single(l => l.Contains(report, StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Contains("NetworkChange.NetworkAddressChanged += (_, _) => Changed();", host, StringComparison.Ordinal);
        }
    }
}
