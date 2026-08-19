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
    /// <para>Files live under %LOCALAPPDATA%\PadForge (the folder the
    /// Workshop cache and voice models already use), never beside the exe:
    /// the standing rule is that only PadForge.xml and crash.log may exist
    /// there.</para>
    /// </summary>
    internal static class DiagnosticsLogControl
    {
        internal static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PadForge");

        internal static string LogPath => Path.Combine(Folder, "diagnostics.log");

        /// <summary>Arms or disarms the mirror to match the setting. Called
        /// from the settings load (cold start) and from the toggle's
        /// PropertyChanged, so the writer needs no restart. Never throws.</summary>
        internal static void Apply(bool enabled)
        {
            try
            {
                if (enabled) Directory.CreateDirectory(Folder);
                Engine.SdlDiagLog.SetMirror(enabled ? LogPath : null);
            }
            catch
            {
                // Diagnostics must not take settings load down.
            }
        }

        /// <summary>Writes the in-memory ring (the last ~400 engine events,
        /// collected whether or not logging is on) to a timestamped file
        /// and returns its path. Lets a user capture the moments around a
        /// glitch after the fact without having had logging enabled.</summary>
        internal static string SaveSnapshot()
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder,
                $"diag-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Engine.SdlDiagLog.Snapshot() + Environment.NewLine);
            return path;
        }
    }
}
