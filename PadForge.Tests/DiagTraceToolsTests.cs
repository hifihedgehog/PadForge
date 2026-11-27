using System;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The trace tools under tools start the deployed PadForge with
    /// PADFORGE_DIAG set. PadForge runs with rights, and with them it takes
    /// the variable only for a file directly in its own folder
    /// (SdlDiagLog.MirrorPathWithRights), so each tool's mirror is a file
    /// there that PadForge counts as its own. These tests read the scripts'
    /// text and run nothing.
    /// </summary>
    public class DiagTraceToolsTests
    {
        [Theory]
        [InlineData("btaudio-trace.ps1")]
        [InlineData("button-trace.ps1")]
        [InlineData("effect-trace.ps1")]
        [InlineData("diag-sweep.ps1")]
        public void EachTool_ArmsAMirrorPadForgeWithRightsTakes(string tool)
        {
            string script = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", tool)));

            var armed = Regex.Match(script, @"PADFORGE_DIAG = '?\$(\w+)'?");
            Assert.True(armed.Success, tool + " sets no PADFORGE_DIAG");
            string mirrorVariable = armed.Groups[1].Value;
            var made = Regex.Match(script, @"\$" + mirrorVariable + @" = Join-Path \(Split-Path -Parent \$(\w+)\) '([^']+)'");
            Assert.True(made.Success, tool + " arms $" + mirrorVariable + ", which is not a file in the exe's folder");
            var exe = Regex.Match(script, @"\$" + made.Groups[1].Value + @" = '([^']+)'");
            Assert.True(exe.Success, tool + " names no default exe");

            string mirror = Path.Combine(Path.GetDirectoryName(exe.Groups[1].Value), made.Groups[2].Value);
            Assert.Equal(mirror, PadForge.Engine.SdlDiagLog.MirrorPathWithRights(mirror, exe.Groups[1].Value));
        }

        /// <summary>PersonaVerify reads the mirror the trace tools arm by
        /// default.</summary>
        [Fact]
        public void PersonaVerify_ReadsThatMirrorByDefault()
        {
            string program = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("tools", "PersonaVerify", "Program.cs")));
            Assert.Contains("args[0] : @\"C:\\PadForge\\diag.log\";", program, StringComparison.Ordinal);
        }

        /// <summary>Each engine start deletes the diag.log builds before v4
        /// left beside the exe, and keeps the one a bench armed through
        /// PADFORGE_DIAG, which the trace tools read while it grows.</summary>
        [Fact]
        public void TheEngineStart_KeepsTheMirrorABenchArmed_AndDeletesAStaleOne()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.DiagLog." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string log = Path.Combine(dir, "diag.log");
                File.WriteAllText(log, "trace");
                InputManager.DeleteStaleDiagLog(dir, log);
                Assert.True(File.Exists(log), "the mirror a bench armed was deleted");
                InputManager.DeleteStaleDiagLog(dir, Path.Combine(dir, "sub", "..", "DIAG.LOG"));
                Assert.True(File.Exists(log), "the mirror a bench armed by another spelling was deleted");

                InputManager.DeleteStaleDiagLog(dir, null);
                Assert.False(File.Exists(log));
                File.WriteAllText(log, "stale");
                InputManager.DeleteStaleDiagLog(dir, Path.Combine(dir, "other.log"));
                Assert.False(File.Exists(log));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
