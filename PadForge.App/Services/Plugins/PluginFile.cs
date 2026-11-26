using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// What PadForge reads of a plugin's file (#600): its SHA-256, its
    /// Authenticode signer (Engine.Common.AuthenticodeSigner, the one reader of
    /// a program's signer the request card and Activity use too), and how
    /// Windows runs it.
    /// </summary>
    internal static class PluginFile
    {
        /// <summary>
        /// Opens the file so no program can change, replace or delete it
        /// while the handle is open, and gives its SHA-256 as hex. The
        /// handle is held from the hash through the start until the
        /// plugin's hello, so what starts is what was hashed. After the
        /// hello the file is free to change, and a change shows Approve and
        /// Restart (PluginManager.CheckFiles). Null when the file cannot be
        /// opened.
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

        /// <summary>The key that signed the held file, read on the handle that
        /// was hashed as the confirmation reads the signer: no revocation
        /// check and nothing fetched
        /// (<see cref="PadForge.Engine.Common.AuthenticodeSigner.SignedHeld"/>).
        /// Null for a file whose own signature does not hold.</summary>
        internal static string KeyOf(string path, FileStream held)
            => held == null ? null : PadForge.Engine.Common.AuthenticodeSigner.SignedHeld(path, held.SafeFileHandle)?.Key;

        /// <summary>
        /// The path the held file was reached at, every junction and link on
        /// the way followed (GetFinalPathNameByHandle), with the <c>\\?\</c>
        /// prefix taken off a path short enough to do without it. A start is
        /// made from this path: a junction or link above the file that a
        /// program points elsewhere after the hash leaves the path naming the
        /// held file, whose folder cannot be renamed while it is held. Null
        /// with the Windows error when it cannot be read.
        /// </summary>
        internal static string FinalPath(FileStream held, out int error)
        {
            error = 0;
            if (held == null) return null;
            var buffer = new StringBuilder(MaxPath);
            uint length = GetFinalPathNameByHandleW(held.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(held.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            }
            if (length == 0 || length >= buffer.Capacity)
            {
                error = Marshal.GetLastWin32Error();
                if (error == 0) error = ErrorNotEnoughMemory;
                return null;
            }
            string path = buffer.ToString(0, (int)length);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                string unc = @"\\" + path.Substring(8);
                return unc.Length < MaxPath ? unc : path;
            }
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length - 4 < MaxPath) return path.Substring(4);
            return path;
        }

        private const int MaxPath = 260;
        private const int ErrorNotEnoughMemory = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path,
            uint size, uint flags);

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
        /// approved file run. Any other script runs in the command that
        /// opens its kind of file for the desktop's user
        /// (<see cref="PluginPrograms.OpenCommand"/>), with the script in
        /// place of <c>%1</c> (<see cref="FromOpen"/>). A Python script with
        /// no such command, or one whose program is not on this PC, runs in
        /// the py launcher. Without a program, the command carries why
        /// (<see cref="PluginCommand.Refusal"/>): none named, the one named
        /// not there, or the registry not read. Every start is in the file's
        /// own folder.
        /// </summary>
        /// <param name="kindOf">The path whose extension picks the kind of
        /// start, the one the user approved, when <paramref name="path"/> is
        /// where it led: a link named pad.py that reaches an .exe still runs
        /// in Python's program. Null takes <paramref name="path"/>'s.</param>
        internal static PluginCommand CommandFor(string path, PluginPrograms programs = null, string kindOf = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(full);
            string extension = Path.GetExtension(kindOf ?? full).ToLowerInvariant();
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
            programs ??= PluginPrograms.OfShellUser();
            string open;
            try { open = programs.OpenCommand(extension); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return PluginCommand.Without(PluginRefusal.ProgramUnreadable);
            }
            bool missing = false;
            var command = open == null ? null : FromOpen(open, full, folder, programs, out missing);
            if (command != null) return command;
            if (extension is ".py" or ".pyw")
            {
                // CPython's "Using Python on Windows": with no program for
                // .py files, a script runs by name through py.
                string py;
                try { py = programs.PyLauncher(windowed: extension == ".pyw"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return PluginCommand.Without(PluginRefusal.ProgramUnreadable);
                }
                if (py != null) return new PluginCommand(py, Quote(py) + " " + Quote(full), folder);
                return PluginCommand.Without(missing ? PluginRefusal.ProgramMissing : PluginRefusal.NoPython);
            }
            return PluginCommand.Without(missing ? PluginRefusal.ProgramMissing : PluginRefusal.NoProgram);
        }

        /// <summary>
        /// The line an open command runs for one script: its program, then
        /// its words with the script in place of <c>%1</c> or <c>%L</c>, and
        /// <c>%*</c> and the other numbered places left out, as the shell
        /// fills them for one file with no more. A command with no place
        /// for the file takes it last. AutoHotkey v2's launcher.ahk is left
        /// out, and its interpreter runs the script: the launcher exits as
        /// soon as it has started the interpreter when its parent is
        /// explorer.exe (AutoHotkeyUX launcher.ahk, LaunchScript: waitClose
        /// is false when ProcessGetParent names explorer.exe), the parent
        /// every plugin starts under, so the process PadForge watches would
        /// end at once. Null when its program is not a file that is there
        /// (<paramref name="missing"/> set), or is the script itself.
        /// </summary>
        internal static PluginCommand FromOpen(string open, string full, string folder, PluginPrograms programs, out bool missing)
        {
            missing = false;
            string line = open.Trim();
            string program, rest;
            if (line.StartsWith('"'))
            {
                int end = line.IndexOf('"', 1);
                if (end < 0) return null;
                program = line.Substring(1, end - 1);
                rest = line.Substring(end + 1);
            }
            else
            {
                // An unquoted program ends at its .exe, so a path with a
                // space in it is still read whole.
                int exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                int cut = exe >= 0 ? exe + 4 : line.IndexOf(' ');
                if (cut < 0) cut = line.Length;
                program = line.Substring(0, cut);
                rest = line.Substring(cut);
            }
            if (program.Length == 0 || program.Contains('%')) return null;
            if (!programs.Exists(program))
            {
                missing = true;
                return null;
            }
            try
            {
                if (string.Equals(Path.GetFullPath(program), full, StringComparison.OrdinalIgnoreCase)) return null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

            var sb = new StringBuilder(Quote(program));
            bool placed = false;
            foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(rest, "\"[^\"]*\"|\\S+"))
            {
                string text = word.Value;
                string bare = text.Trim('"');
                if (bare is "%1" or "%L" or "%l")
                {
                    sb.Append(' ').Append(Quote(full));
                    placed = true;
                }
                else if (bare.Length == 2 && bare[0] == '%' && (bare[1] == '*' || char.IsDigit(bare[1])))
                {
                    // %* and %2 to %9: nothing more is handed to it.
                }
                else if (string.Equals(Path.GetFileName(bare), "launcher.ahk", StringComparison.OrdinalIgnoreCase))
                {
                    // AutoHotkey's launcher would exit under explorer.exe.
                }
                else if (text.Contains("%1") || text.Contains("%L") || text.Contains("%l"))
                {
                    sb.Append(' ').Append(text.Replace("%1", full).Replace("%L", full).Replace("%l", full));
                    placed = true;
                }
                else sb.Append(' ').Append(text);
            }
            if (!placed) sb.Append(' ').Append(Quote(full));
            return new PluginCommand(program, sb.ToString(), folder);
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

    }
}
