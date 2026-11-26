using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// What PadForge reads of a plugin's file (#597): its SHA-256, its
    /// Authenticode signer (Engine.Common.AuthenticodeSigner, the one reader of
    /// a program's signer the request card and Activity use too), and how
    /// Windows runs it.
    /// </summary>
    internal static class PluginFile
    {
        /// <summary>
        /// Opens the file so no program can change, replace or delete it
        /// while the handle is open, and gives its SHA-256 as hex. The
        /// handle is held from the hash through the start and for as long
        /// as the plugin runs, so what runs is what was hashed. Null when
        /// the file cannot be opened.
        /// </summary>
        internal static FileStream OpenHeld(string path, out string sha256)
        {
            sha256 = null;
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            try
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
                stream.Position = 0;
                return stream;
            }
            catch (IOException)
            {
                stream.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Show in Folder: Explorer opens the file's folder with the file
        /// selected, or the folder alone when the file is gone. It starts as
        /// the user, through the desktop shell, as --settings-folder and the
        /// recording review's Show in Explorer do. The test host starts
        /// nothing (ShellLaunch.OffLimitsForTest).
        /// </summary>
        internal static void ShowInFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            PadForge.Common.ShellLaunch.Start(PadForge.Common.ShellLaunch.Explorer,
                PadForge.Services.SettingsFolderRun.ExplorerArguments(path, File.Exists));
        }

        /// <summary>The signer and signing key of the held file, read on the
        /// handle that was hashed, when the signature it carries verifies
        /// with revocation checked
        /// (<see cref="PadForge.Engine.Common.AuthenticodeSigner.VerifiedHeld"/>).
        /// Null otherwise.</summary>
        internal static PadForge.Engine.Common.AuthenticodeSignature SignatureOf(string path, FileStream held)
            => held == null ? null : PadForge.Engine.Common.AuthenticodeSigner.VerifiedHeld(path, held.SafeFileHandle);

        /// <summary>The file's SHA-256 as hex, or null when it cannot be
        /// read.</summary>
        internal static string Sha256(string path)
        {
            using var held = OpenHeld(path, out string sha);
            return sha;
        }

        /// <summary>
        /// How Windows runs the file. A program runs itself. A batch file
        /// runs in the command processor. A PowerShell script runs in
        /// Windows PowerShell with no profile, under a policy that lets the
        /// approved file run. Any other script runs in the program Windows
        /// opens its kind of file with (<c>AssocQueryString</c>,
        /// <c>ASSOCSTR_EXECUTABLE</c>), with the script as its argument.
        /// Null when Windows has no such program. Every start is in the
        /// file's own folder.
        /// </summary>
        internal static PluginCommand CommandFor(string path, Func<string, string> programFor = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(full);
            string extension = Path.GetExtension(full).ToLowerInvariant();
            switch (extension)
            {
                case ".exe":
                case ".com":
                    return new PluginCommand(full, Quote(full), folder);
                case ".bat":
                case ".cmd":
                {
                    string cmd = SystemProgram("cmd.exe");
                    return new PluginCommand(cmd, Quote(cmd) + " /d /c \"" + Quote(full) + "\"", folder);
                }
                case ".ps1":
                {
                    string powershell = SystemProgram(@"WindowsPowerShell\v1.0\powershell.exe");
                    return new PluginCommand(powershell,
                        Quote(powershell) + " -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(full), folder);
                }
            }
            string program = (programFor ?? AssociatedProgram)(extension);
            if (string.IsNullOrEmpty(program)) return null;
            // A kind of file that runs itself names itself, which is no
            // program for a script.
            if (string.Equals(Path.GetFileName(program), "%1", StringComparison.Ordinal)
                || string.Equals(Path.GetFullPath(program), full, StringComparison.OrdinalIgnoreCase)) return null;
            return new PluginCommand(program, Quote(program) + " " + Quote(full), folder);
        }

        private static string SystemProgram(string relative)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relative);

        /// <summary>A command-line argument in the quotes CommandLineToArgvW
        /// reads back as the same text.</summary>
        internal static string Quote(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return "\"\"";
            var sb = new StringBuilder(argument.Length + 2).Append('"');
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        /// <summary>The program Windows opens files of this extension with,
        /// or null.</summary>
        internal static string AssociatedProgram(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return null;
            uint size = 0;
            int hr = AssocQueryStringW(AssocfInitIgnoreUnknown | AssocfNoTruncate, AssocstrExecutable, extension, "open", null, ref size);
            if (size == 0 || (hr != 0 && hr != SFalse)) return null;
            var buffer = new StringBuilder((int)size);
            hr = AssocQueryStringW(AssocfInitIgnoreUnknown | AssocfNoTruncate, AssocstrExecutable, extension, "open", buffer, ref size);
            if (hr != 0) return null;
            string program = buffer.ToString();
            return File.Exists(program) ? program : null;
        }

        private const uint AssocfNoTruncate = 0x00000020;
        private const uint AssocfInitIgnoreUnknown = 0x00000400;
        private const int AssocstrExecutable = 2;
        private const int SFalse = 1;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int AssocQueryStringW(uint flags, int what, string association, string extra,
            StringBuilder output, ref uint size);
    }
}
