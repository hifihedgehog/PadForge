using System.Linq;
using PadForge.Engine.Privacy;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A path under the user's profile folder (#600): a recording's scrubber
    /// knows that folder whole and replaces it before the path pass, so the
    /// path pass starts at the folder's stand-in and the folders after it go
    /// too. Only the file's name stays, as for any other path.
    /// </summary>
    public class RecorderProfilePathTests
    {
        private static PrivacyScrubber Scrubber()
        {
            var s = new PrivacyScrubber(new PrivacyEnvironment { UserName = "marisol", ProfileFolder = @"C:\Users\marisol", PcName = "DESKTOP-Q7KX2M" });
            // As a recording teaches it (ProblemRecorder.LiveValues).
            s.Know(PrivateKind.Folder, @"C:\Users\marisol");
            return s;
        }

        [Fact]
        public void APathUnderTheProfileFolder_KeepsOnlyItsFileName()
        {
            var s = Scrubber();
            Assert.Equal(@"loaded <folder>\a.json ok", s.Text(@"loaded C:\Users\marisol\Documents\Secret Plans\a.json ok"));
            Assert.Equal(@"set <folder>\cake.png", s.Text(@"set C:\Users\marisol\Pictures\Grandma Rosa's Party\cake.png"));
            // The profile folder alone stays its stand-in, and scrubbed text
            // comes through again unchanged.
            Assert.Equal("home <folder> now", s.Text(@"home C:\Users\marisol now"));
            Assert.Equal(@"loaded <folder>\a.json ok", s.Text(@"loaded <folder>\a.json ok"));
            // Each replaced folder is counted once.
            Assert.Equal(3, s.Counts().Single(c => c.Kind == PrivateKind.Folder).Places);
        }
    }
}
