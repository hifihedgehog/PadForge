using System;
using System.IO;

namespace PadForge.Common
{
    /// <summary>
    /// Settings-driven control of the engine diagnostics mirror (#303).
    /// PadForge auto-starts with Windows for many users, so the
    /// PADFORGE_DIAG launch flag was unusable for exactly the long-running
    /// sessions that need a trace. The Diagnostics setting arms the same
    /// mirror at runtime and persists in PadForge.xml, so it survives
    /// restarts.
    ///
    /// <para>Files live beside the exe, like everything PadForge writes
    /// (owner ruling 2026-08-19). The no-stray-files rule bans files the
    /// app drops WITHOUT explicit user action; these exist only because
    /// the user ticked the toggle or clicked the snapshot button, the same
    /// standing as a SaveFileDialog export.</para>
    /// </summary>
    internal static class DiagnosticsLogControl
    {
        internal static string Folder => AppDomain.CurrentDomain.BaseDirectory;

        internal static string LogPath => Path.Combine(Folder, "diagnostics.log");

        /// <summary>Arms or disarms the mirror to match the setting. Called
        /// from the settings load (cold start) and from the toggle's
        /// PropertyChanged, so the writer needs no restart. Never throws.</summary>
        internal static void Apply(bool enabled)
        {
            try
            {
                Engine.SdlDiagLog.SetMirror(enabled ? LogPath : null);
            }
            catch
            {
                // Diagnostics must not take settings load down.
            }
        }

        /// <summary>Writes the in-memory ring (the last 4,000 engine events,
        /// collected whether or not logging is on) to a timestamped file
        /// and returns its path. Lets a user capture the moments around a
        /// glitch after the fact without having had logging enabled.
        ///
        /// <para>The state at that moment goes into the history first, so the
        /// snapshot ends with it and diagnostics.log carries it too
        /// (#541): HidHide's list, the XInput interfaces Windows lists and
        /// each virtual controller's node
        /// (<see cref="HidHideController.DescribeSnapshotState"/>).</para></summary>
        internal static string SaveSnapshot()
        {
            foreach (string line in SnapshotStateLines())
                Engine.SdlDiagLog.WriteLine(line);
            string path = Path.Combine(Folder,
                $"diag-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Engine.SdlDiagLog.Snapshot() + Environment.NewLine);
            return path;
        }

        /// <summary>Test seam for the state lines, null in production, where
        /// <see cref="HidHideController.DescribeSnapshotState"/> reads them.</summary>
        internal static Func<IReadOnlyList<string>> StateSeam;

        /// <summary>The state lines, never throwing: a read that failed
        /// leaves its own line, and the snapshot is written regardless.</summary>
        internal static IReadOnlyList<string> SnapshotStateLines()
        {
            try
            {
                var seam = StateSeam;
                return seam != null ? seam() : HidHideController.DescribeSnapshotState();
            }
            catch (Exception ex)
            {
                return new[] { "STATE err:" + ex.GetType().Name };
            }
        }
    }
}
