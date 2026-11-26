using System;
using System.Collections.Generic;
using PadForge.Common;
using PadForge.Engine.Privacy;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The system part of a recording (#600 item 32) gives the processor
    /// count once, on the Windows build's line. In the statics' collection
    /// with HidHideSnapshotStateTests, the other test that sets the system
    /// part's two seams.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class RecorderSystemPartTests
    {
        [Fact]
        public void TheSystemPart_GivesTheProcessorCountOnce()
        {
            var images = RecorderHost.RunningImages;
            var state = DiagnosticsLogControl.StateSeam;
            RecorderHost.RunningImages = () => Array.Empty<string>();
            DiagnosticsLogControl.StateSeam = () => Array.Empty<string>();
            try
            {
                var lines = new List<string>();
                new RecorderHost().SystemLines(new PrivacyScrubber(new PrivacyEnvironment()), lines);
                string count = " " + Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " processors";
                var line = Assert.Single(lines, l => l.Contains(" processors", StringComparison.Ordinal));
                Assert.StartsWith("Windows build ", line);
                Assert.Contains(count, line);
            }
            finally
            {
                RecorderHost.RunningImages = images;
                DiagnosticsLogControl.StateSeam = state;
            }
        }
    }
}
