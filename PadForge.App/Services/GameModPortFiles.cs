using System;
using System.Globalization;
using System.IO;

namespace PadForge.Services
{
    /// <summary>
    /// The files DSX game mods read their port from (#529).
    ///
    /// <para>Mods built from DSX's v2 example read
    /// <c>C:\Temp\DualSenseX\DualSenseX_PortNumber.txt</c>, and some stop when
    /// it is missing: DualSense4Rockstar's GTA IV client reads it with no
    /// fallback (DualSense4GTAIV/Main/iO.cs:37). Mods built from the v3.1
    /// example read <c>%LOCALAPPDATA%\DSX\DSX_UDP_PortNumber.txt</c> and fall
    /// back to 6969 (Program.cs, FetchPortNumber). DSX writes both. DSY's
    /// first version created the old file when it was missing
    /// (UDP.cs:24-28).</para>
    ///
    /// <para>When the server starts listening, the old file is created when
    /// missing, either file holding another port is rewritten, and the new
    /// file is created only for a port other than 6969, which its readers
    /// fall back to anyway. Both are left alone while a real DSX runs, since
    /// DSX owns them then.</para>
    ///
    /// <para>Both files are the user's. Any signed-in program may replace
    /// C:\Temp\DualSenseX with a link to another folder, and
    /// %LOCALAPPDATA% is the user's own. So PadForge with rights the shell
    /// lacks writes them in a copy started as the desktop user
    /// (<c>PadForge.exe --port-files &lt;port&gt;</c> through
    /// <see cref="Plugins.PluginLauncher"/>, the route a recording's save
    /// takes), which writes where the user may and puts the new file in the
    /// user's own %LOCALAPPDATA%. That copy also follows no link: a path
    /// with a reparse point on it is left alone.</para>
    /// </summary>
    internal static class GameModPortFiles
    {
        internal const string LegacyPath = @"C:\Temp\DualSenseX\DualSenseX_PortNumber.txt";

        internal static string CurrentPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSX", "DSX_UDP_PortNumber.txt");

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests GameModPortFilesOffLimits), never by the app. The
        /// files are the PC's own, read by every DSX mod on it, so the tests
        /// write files of their own through <see cref="WriteTo"/>. False in
        /// production.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Writes the port where the mods look, unless a real DSX
        /// is running. Never throws.</summary>
        internal static void Write(int port)
        {
            if (OffLimitsForTest) return;
            Write(port, new Seams());
        }

        /// <summary>The write's ways out to the PC, each a test's to
        /// replace.</summary>
        internal sealed class Seams
        {
            public Func<bool> RealDsxRunning = GameModPortFiles.RealDsxRunning;
            public Action<int> WriteHere = port => WriteTo(LegacyPath, CurrentPath, port);
        }

        /// <summary>Writes the files in this process, unless a real DSX runs
        /// and owns them. Runs on a pool thread (GameModServer.Start).</summary>
        internal static void Write(int port, Seams seams)
        {
            try
            {
                if (seams.RealDsxRunning()) return;
                seams.WriteHere(port);
            }
            catch
            {
                // A file the mods cannot read is the mods' fallback, never a
                // reason to stop the server.
            }
        }

        /// <summary>Whether the file or any folder above it that exists is
        /// a reparse point: a junction, a symbolic link or a mount point.
        /// Such a path is left alone, so a write never lands where a link
        /// another program made points.</summary>
        internal static bool HasLink(string path)
        {
            for (string p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }

        /// <summary>The rule, on any two paths.</summary>
        internal static void WriteTo(string legacyPath, string currentPath, int port)
        {
            WriteOne(legacyPath, port, createWhenMissing: true);
            WriteOne(currentPath, port, createWhenMissing: port != GameModServer.DefaultPort);
        }

        private static void WriteOne(string path, int port, bool createWhenMissing)
        {
            try
            {
                if (HasLink(path)) return;
                if (File.Exists(path))
                {
                    string held = File.ReadAllText(path).Trim();
                    if (int.TryParse(held, NumberStyles.Integer, CultureInfo.InvariantCulture, out int heldPort)
                        && heldPort == port)
                        return;
                }
                else if (!createWhenMissing)
                {
                    return;
                }
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                // Again after the folders were made, right before the write.
                if (HasLink(path)) return;
                File.WriteAllText(path, port.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Whether a process named DSX runs that is not PadForge's
        /// own stand-in, read from the one process list the app walks
        /// (RunningProcessList, ProcessPresenceTests).</summary>
        private static bool RealDsxRunning()
        {
            var running = Common.Input.RunningProcessList.Read();
            if (running == null) return false;
            uint standIn = (uint)DsxStandIn.ProcessId;
            foreach (var p in running)
                if (p.Id != standIn && string.Equals(p.Name, "DSX", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
