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

        /// <summary>
        /// Save Snapshot (#600): the state as it stands now and the engine's
        /// recent events, taken as a problem recording with no events of its
        /// own, so it is scrubbed, summarized and read through the same review
        /// before anything is saved. It waits in the user's own recordings
        /// folder, and the window opens its review or offers it on Home. Gives
        /// the recording's file. A program that asked for it is told only
        /// that it waits (<see cref="Services.ProblemRecorder.WaitsWord"/>),
        /// never where.
        ///
        /// <para>The state lines (#541) go into the history first, so
        /// diagnostics.log carries them too, and the recording's system part
        /// holds them: HidHide's list, the XInput interfaces Windows lists
        /// and each virtual controller's node
        /// (<see cref="HidHideController.DescribeSnapshotState"/>).</para>
        /// </summary>
        /// <exception cref="IOException">The recorder refused: its check, its
        /// folder, or the last file still being finished.</exception>
        internal static string SaveSnapshot()
        {
            foreach (string line in SnapshotStateLines())
                Engine.SdlDiagLog.WriteLine(line);
            var result = Services.ProblemRecorder.Instance.Snapshot();
            if (!result.Ok)
                throw new IOException("The snapshot was not taken: " + result.Refusal
                    + (string.IsNullOrEmpty(result.Detail) ? "." : ", " + result.Detail));
            return result.Path;
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
