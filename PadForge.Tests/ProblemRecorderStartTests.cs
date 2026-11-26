using System;
using System.Diagnostics;
using System.IO;
using PadForge.Engine.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The recorder's step as PadForge starts: the files left
    /// unfinished, and the seven-day sweep, whose test seam the other
    /// sweep tests of this collection set.</summary>
    [Collection("RecordingFolderStatics")]
    public class ProblemRecorderStartTests
    {
        [Fact]
        public void ALeftOverFileNoFinishTakes_GoesWithTheSevenDaySweep()
        {
            using var host = new RecorderTestHost();
            string left = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "20261123-100000" + ProblemRecorder.PartialSuffix);
            File.WriteAllText(left, RecordingCodec.FirstLine + "\r\n" + RecordingCodec.StatusRecording + "\r\n"
                + new string('x', BoundedLineReader.LongestLine + 1) + "\r\n");
            var recorder = new ProblemRecorder(host);
            // Within the week it waits, as a recording waits for its review.
            Assert.Empty(recorder.TidyAtStart(DateTime.UtcNow));
            Assert.True(File.Exists(left));
            // A week on, the sweep takes it.
            Assert.Empty(recorder.TidyAtStart(DateTime.UtcNow + ProblemRecorder.KeptUnsaved + TimeSpan.FromDays(1)));
            Assert.Empty(Directory.GetFiles(host.Dir));
        }

        [Fact]
        public void TheStepAtTheStart_SweepsOldRecordings_WhateverALeftOverFileDoes()
        {
            using var host = new RecorderTestHost();
            var recorder = new ProblemRecorder(host) { FrozenClock = Stopwatch.GetTimestamp() };
            // A recording that waited for its review, and a file left
            // unfinished whose finish throws what no filter names.
            Assert.True(recorder.Snapshot(wait: true).Ok);
            string waiting = Assert.Single(Directory.GetFiles(host.Dir));
            string left = Path.Combine(host.Dir, ProblemRecorder.FilePrefix + "20261123-100000" + ProblemRecorder.PartialSuffix);
            File.WriteAllText(left, RecordingCodec.FirstLine + "\r\n" + RecordingCodec.StatusRecording + "\r\n" + RecordingCodec.EventsHeader + "\r\n");
            host.LiveValuesThrow = true;

            var finished = recorder.TidyAtStart(DateTime.UtcNow + ProblemRecorder.KeptUnsaved + TimeSpan.FromDays(1));
            Assert.Empty(finished);
            Assert.False(File.Exists(waiting), "the recording that waited past its days is still there");
            // The left-over file no finish took is a week old by that clock
            // too, and goes with it.
            Assert.False(File.Exists(left), "the left-over file no finish took is still there");
        }
    }
}
