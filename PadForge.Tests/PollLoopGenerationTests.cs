using System;
using System.IO;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Stop bumps the run generation so a loop that outlives its join retires on
    /// its next check. The mouse injector's loop read the stamp, and the poll
    /// loop took the generation as a parameter and never read it, so a stalled
    /// poll thread would have kept running the pipeline. Both loop heads must
    /// test the stamp.
    /// </summary>
    public class PollLoopGenerationTests
    {
        private static string Source()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "InputManager.cs"));
        }

        private static string FirstLoopHead(string src, string method)
        {
            int start = src.IndexOf(method, StringComparison.Ordinal);
            Assert.True(start >= 0, method);
            int loop = src.IndexOf("while (", start, StringComparison.Ordinal);
            int end = src.IndexOf('\n', loop);
            return src.Substring(loop, end - loop);
        }

        private static bool ChecksGeneration(string head)
            => head.Contains("_runGeneration) == generation", StringComparison.Ordinal);

        [Theory]
        [InlineData("private void PollingLoop(int generation, int pollRun)")]
        [InlineData("private void MouseInjectorLoop(int generation)")]
        public void EveryEngineLoopRetiresOnAStaleGeneration(string method)
        {
            Assert.True(ChecksGeneration(FirstLoopHead(Source(), method)), method);
        }

        [Fact]
        public void ThePinRejectsALoopThatOnlyWatchesRunning()
        {
            Assert.False(ChecksGeneration("while (_running)"));
        }
    }
}
