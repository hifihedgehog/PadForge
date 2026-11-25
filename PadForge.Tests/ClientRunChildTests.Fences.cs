using System;
using System.Collections.Generic;
using PadForge.Services;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  The fences a test's child carries
        // ─────────────────────────────────────────────

        /// <summary>The variables of a test's child: the test's own pipe
        /// under the name PadForge's web helper reads it by
        /// (WebHelper.PipeVariable), which makes the child's run a test's
        /// (ClientRun.Seams.ForThisRun).</summary>
        internal static Func<string, string> ChildVariables(string pipe)
            => name => name == WebHelper.PipeVariable ? pipe : null;

        /// <summary>A test's child saves no recording (#600 item 52).
        /// <c>--record save</c> reads the recordings folder in the user's
        /// temp folder, where the PadForge that runs on the PC keeps the
        /// recordings that wait for review, and makes the folder when it is
        /// missing (RecordingSave.Run through RecordingFolder.Get). The
        /// child's seam says so and gives 1, and never reaches that
        /// code.</summary>
        [Fact]
        public void ATestsChild_SavesNoRecording()
        {
            var seams = ClientRun.Seams.ForThisRun(ChildVariables("PadForge.Tests.Client.save." + Guid.NewGuid().ToString("N")));
            Assert.True(seams.TestsChild);
            var errors = new List<string>();
            int code = seams.RecordSave(@"C:\Users\someone\AppData\Local\Temp\PadForge Recordings\PadForge-recording-1.zip", @"C:\Saved\copy.zip", errors.Add);
            Assert.Equal(ClientRun.AnsweredAnError, code);
            Assert.Equal(new[] { ClientRun.TestsChildSavesNothing }, errors);

            // The run takes the seam: the switch ends there, with the same
            // code and words.
            var said = new List<string>();
            seams.Say = said.Add;
            seams.SayError = said.Add;
            seams.Tell = said.Add;
            seams.TellError = said.Add;
            Assert.True(ClientRun.TryRun(new[] { "--record", "save", @"C:\a.zip", @"C:\b.zip" }, out int exit, seams));
            Assert.Equal(ClientRun.AnsweredAnError, exit);
            Assert.Equal(new[] { ClientRun.TestsChildSavesNothing }, said);
        }
    }
}
