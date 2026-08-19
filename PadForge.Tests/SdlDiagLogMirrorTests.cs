using System;
using System.IO;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Diagnostics setting (#303): SdlDiagLog's mirror is now armable at
    /// runtime, because PadForge auto-starts with Windows for many users
    /// and a launch-time PADFORGE_DIAG flag cannot reach those sessions.
    /// These pin the runtime contract: arming writes, disarming stops, a
    /// session marker separates launches, and the file rotates instead of
    /// growing without bound on an always-on machine.
    ///
    /// <para>SdlDiagLog is a process-wide static, so every test disarms in
    /// finally, and content assertions use Contains rather than equality:
    /// any engine code exercised by a parallel test may append lines of
    /// its own. The tests run in the shared-statics collection so nothing
    /// else toggles the mirror mid-test.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SdlDiagLogMirrorTests : IDisposable
    {
        private readonly string _dir;

        public SdlDiagLogMirrorTests()
        {
            _dir = Path.Combine(Path.GetTempPath(),
                "padforge-diag-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            SdlDiagLog.SetMirror(null);
            SdlDiagLog.RotateAtBytes = 8L * 1024 * 1024;
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void SetMirror_ArmsWritesAndStampsASessionMarker()
        {
            string p = Path.Combine(_dir, "diagnostics.log");
            SdlDiagLog.SetMirror(p);
            try
            {
                Assert.True(SdlDiagLog.IsMirroring);
                SdlDiagLog.WriteLine("mirror-test alpha");
                string text = File.ReadAllText(p);
                Assert.Contains("=== diagnostics logging enabled ===", text);
                Assert.Contains("mirror-test alpha", text);
            }
            finally { SdlDiagLog.SetMirror(null); }
        }

        [Fact]
        public void SetMirror_Null_DisarmsAndStopsWriting()
        {
            string p = Path.Combine(_dir, "diagnostics.log");
            SdlDiagLog.SetMirror(p);
            SdlDiagLog.SetMirror(null);

            Assert.False(SdlDiagLog.IsMirroring);
            long before = new FileInfo(p).Length;
            SdlDiagLog.WriteLine("mirror-test after-disarm");
            Assert.Equal(before, new FileInfo(p).Length);
            Assert.DoesNotContain("after-disarm", File.ReadAllText(p));
        }

        /// <summary>An always-on session must not grow the file without
        /// bound: past the cap the file rolls to "{path}.old" and a fresh
        /// file starts. The counter seeds from the existing file size, so
        /// a re-armed mirror over a large leftover file rotates promptly
        /// rather than doubling it first.</summary>
        [Fact]
        public void Mirror_RotatesToOld_PastTheCap()
        {
            string p = Path.Combine(_dir, "diagnostics.log");
            SdlDiagLog.RotateAtBytes = 512;
            SdlDiagLog.SetMirror(p);
            try
            {
                for (int i = 0; i < 40; i++)
                    SdlDiagLog.WriteLine($"rotation-filler line {i:D3} padding padding padding");

                string old = p + ".old";
                Assert.True(File.Exists(old), "the cap never rotated the file");
                Assert.True(new FileInfo(p).Length < 512 + 256,
                    "the live file did not restart after rotation");
                // Continuity: the newest line is in the live file.
                Assert.Contains("rotation-filler line 039", File.ReadAllText(p));
            }
            finally
            {
                SdlDiagLog.SetMirror(null);
                SdlDiagLog.RotateAtBytes = 8L * 1024 * 1024;
            }
        }

        /// <summary>The ring keeps collecting regardless of the mirror, so
        /// the Save Snapshot button has content even when logging was off
        /// during the glitch.</summary>
        [Fact]
        public void Snapshot_CarriesLines_WithNoMirrorArmed()
        {
            Assert.False(SdlDiagLog.IsMirroring);
            SdlDiagLog.WriteLine("snapshot-test unmirrored line");
            Assert.Contains("snapshot-test unmirrored line", SdlDiagLog.Snapshot());
        }
    }
}
