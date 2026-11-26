using System;
using System.Diagnostics;
using System.IO;
using PadForge.Common.Recording;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The sweep of the recordings folder (#600), which runs in the copy with
    /// rights in a folder a program without rights can change: it deletes
    /// only files of the names PadForge gives, and never a file a link
    /// planted since the sweep began points at. Nothing outside a temp
    /// folder the test owns is touched, and cmd.exe's mklink makes the
    /// junction, which needs no rights.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecordingStoreSweepTests
    {
        private static void Junction(string link, string target)
        {
            using var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            mklink.StandardOutput.ReadToEnd();
            Assert.True(mklink.WaitForExit(10000) && mklink.ExitCode == 0, "the junction was made");
        }

        private static string Old(string path, string text)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            return path;
        }

        /// <summary>A file of a name PadForge never gives is left, though it
        /// ends as a half-made one does. A copy of PadForge's own name goes:
        /// the positive control.</summary>
        [Fact]
        public void TheSweep_DeletesOnlyTheNamesPadForgeGives()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, RecordingFolder.Name);
            Directory.CreateDirectory(folder);
            try
            {
                string planted = Old(Path.Combine(folder, ProblemRecorder.FilePrefix + "x.tmp"), "planted");
                string plantedCopy = Old(Path.Combine(folder, ProblemRecorder.FilePrefix + "x" + RecordingReview.CopySuffix), "planted");
                string copy = Old(Path.Combine(folder, ProblemRecorder.FilePrefix + "20200101-000000.0123abcd" + RecordingReview.CopySuffix), "copy");
                string view = Old(Path.Combine(folder, ProblemRecorder.FilePrefix + "20200101-000000.zip." + Guid.NewGuid().ToString("N") + ProblemRecorder.TempSuffix), "view");
                string text = Old(Path.Combine(folder, ProblemRecorder.FilePrefix + "20200101-000000.0123abcd" + ProblemRecorder.PartialSuffix + ProblemRecorder.TempSuffix), "text");

                Assert.Equal(3, RecordingStore.Sweep(folder, DateTime.UtcNow, ProblemRecorder.KeptUnsaved));
                Assert.True(File.Exists(planted));
                Assert.True(File.Exists(plantedCopy));
                Assert.False(File.Exists(copy));
                Assert.False(File.Exists(view));
                Assert.False(File.Exists(text));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        /// <summary>The folder is swapped for a junction to another folder
        /// between the sweep's listing and its deletes, the other folder
        /// holding a file of the listed name: that file stays.</summary>
        [Fact]
        public void AFolderSwappedForALink_AfterTheListing_LosesNothingItPointsAt()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.RecorderTests", Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, RecordingFolder.Name);
            string moved = Path.Combine(root, "moved");
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(outside);
            string name = ProblemRecorder.FilePrefix + "20200101-000000.zip." + Guid.NewGuid().ToString("N") + ProblemRecorder.TempSuffix;
            Old(Path.Combine(folder, name), "half made");
            string target = Old(Path.Combine(outside, name), "not PadForge's");
            var before = RecordingStore.SweepSeam;
            RecordingStore.SweepSeam = () =>
            {
                Directory.Move(folder, moved);
                Junction(folder, outside);
            };
            try
            {
                RecordingStore.Sweep(folder, DateTime.UtcNow, ProblemRecorder.KeptUnsaved);
                Assert.True(File.Exists(target), "the sweep deleted the file the link points at");
                Assert.Equal("not PadForge's", File.ReadAllText(target));
            }
            finally
            {
                RecordingStore.SweepSeam = before;
                // The junction goes first, as a link: never what it names.
                if (Directory.Exists(folder)) Directory.Delete(folder);
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
