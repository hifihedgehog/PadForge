using HIDMaestro;
using PadForge.Common.Recording;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a game writes to a virtual controller, as a recording takes it
    /// (#600): one line when a report differs from the last, a hundred a
    /// second at most for one controller, and what the cap left unwritten
    /// counted on the next line.
    /// </summary>
    public class RecordingTapsTests
    {
        /// <summary>A game that changes its rumble every frame past the cap,
        /// then holds its last report: that report is written once the cap
        /// lifts, with the count of what went unwritten, and a repeat of it
        /// inside the capped second is not counted twice.</summary>
        [Fact]
        public void TheReportTheCapDropped_IsWrittenWhenItComesAgain_WithTheCount()
        {
            var reports = new RecordingTaps.GameReports();
            string Report(byte value, long second) => reports.Line(0, HMOutputSource.HidOutput, 0x05, new[] { value }, second);

            for (int i = 0; i < 100; i++) Assert.NotNull(Report((byte)i, 5));
            Assert.Null(Report(100, 5));
            Assert.Null(Report(100, 5));
            string line = Report(100, 6);
            Assert.NotNull(line);
            Assert.Contains(" unwritten=1 ", line);
            Assert.EndsWith("bytes=64", line);
            // The positive control: the report the file now shows, again,
            // writes nothing.
            Assert.Null(Report(100, 6));
        }
    }
}
