using System;
using System.IO;
using System.Linq;

namespace PadForge.Tests
{
    /// <summary>The engine's stop and the crash quiesce end every level
    /// PadForge left on a pad, including the ones SDL never took.</summary>
    public class EngineStopRumbleTests
    {
        [Fact]
        public void TheStopSweepZeroesAnXboxOnePadTheRawWriterLeftRunning()
        {
            // SDL never took an Xbox One+ pad's level, so its stop skips the
            // pad as a repeat, and the pad kept the raw report's level.
            string code = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            int sweep = code.IndexOf("private void StopAllForceFeedback()", StringComparison.Ordinal);
            Assert.True(sweep > 0);
            int zero = code.IndexOf("try { XboxImpulseHidWriter.Write(ud, 0, 0, 0, 0); }", sweep, StringComparison.Ordinal);
            int sdl = code.IndexOf("try { ud.ForceFeedbackState.StopDeviceForces(ud.Device); }", sweep, StringComparison.Ordinal);
            Assert.True(zero > sweep && sdl > zero, "the raw zero must come before the stop that clears the record it reads");
            Assert.Contains("if (ud.ForceFeedbackState.IsActive\n                                && PadForge.Engine.XboxControllerIdentity.IsImpulseTriggerDevice(ud.VendorId, ud.ProdId))", code);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }
    }
}
