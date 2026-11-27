using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PadForge.Services.Install
{
    /// <summary>
    /// Which of the assistant programs PadForge adds itself to are on this
    /// PC, and the file of each, whose icon the Automation page shows (#600).
    /// Read in the install run, with the user's own token and folders, so a
    /// program installed for the user is found under an administrator's
    /// window as well, and read again in PadForge with the shell user's
    /// variables for the file an Add allows (<see cref="FindForUser"/>). Each place below is where the maker's installer puts
    /// the program, read on this PC on 2026-11-24 where it is installed
    /// here.
    ///
    /// <para>Uses no part of WPF.</para>
    /// </summary>
    internal static class AssistantPrograms
    {
        /// <summary>Test seam: a package's root folder by its family name,
        /// in place of the registry. Null in production.</summary>
        internal static Func<string, string> PackageRootSeam;

        /// <summary>Test seam: the Program Files folders, in place of
        /// Windows'. Null in production.</summary>
        internal static Func<IReadOnlyList<string>> ProgramFilesSeam;

        /// <summary>The package family of the desktop Claude app, as Windows
        /// names it: Claude_pzs8sxrjxfjjc (Get-AppxPackage on this PC).</summary>
        internal const string ClaudeFamily = "Claude_pzs8sxrjxfjjc";

        // ── The search as the desktop shell's user ──

        /// <summary>The variables of the user whose places a search on this
        /// thread reads, or null for this process's own.</summary>
        [ThreadStatic] private static IReadOnlyDictionary<string, string> _user;

        /// <summary>
        /// The program's own file for an assistant as the signed-in user
        /// finds it, read in this process (#600): the file an Add allows. The
        /// check copy started as the user reports a program too, and a
        /// program of the user can rewrite its report before PadForge reads
        /// it, so the grant never rests on the report. The search reads the
        /// user's Path and folders as they stand now
        /// (UserVariables.OfTheUser). Null when the program is not
        /// found.
        /// </summary>
        internal static string FindForUser(string key)
        {
            // The test host reads no variables of the machine's user.
            if (UserVariablesSeam == null && UserInstall.OffLimitsForTest) return Find(key);
            var variables = TheUsersVariables();
            return variables == null ? Find(key) : FindAs(variables, key);
        }

        /// <summary><see cref="Find"/> with the Path, PATHEXT, USERPROFILE and
        /// LOCALAPPDATA of <paramref name="variables"/> in place of this
        /// process's.</summary>
        internal static string FindAs(IReadOnlyDictionary<string, string> variables, string key)
        {
            var before = _user;
            _user = variables;
            try { return Find(key); }
            finally { _user = before; }
        }

        /// <summary>Test seam: the user's variables. Null reads them
        /// (UserVariables.OfTheUser).</summary>
        internal static Func<IReadOnlyDictionary<string, string>> UserVariablesSeam;

        private static IReadOnlyDictionary<string, string> TheUsersVariables()
        {
            if (UserVariablesSeam != null) return UserVariablesSeam();
            string[] lines = UserVariables.OfTheUser();
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int equals = line.IndexOf('=', 1);
                if (equals > 0 && !variables.ContainsKey(line.Substring(0, equals))) variables[line.Substring(0, equals)] = line.Substring(equals + 1);
            }
            return variables;
        }

        /// <summary>A command on the Path, as UserInstall.FindCommand finds
        /// it, on the Path of the user a search reads for.</summary>
        private static string Command(string name)
        {
            var user = _user;
            if (user == null) return UserInstall.FindCommand(name);
            // The user's own Path, read with rights: FindCommandOn looks only
            // in folders on a drive of this PC.
            return UserInstall.FindCommandOn(name, user.TryGetValue("PATH", out string p) ? p : null,
                user.TryGetValue("PATHEXT", out string x) ? x : null);
        }

        /// <summary>A folder of the user a search reads for.</summary>
        private static string Folder(UserFolder folder)
        {
            var user = _user;
            if (user == null) return UserFolders.Of(folder);
            string name = folder switch
            {
                UserFolder.LocalData => "LOCALAPPDATA",
                UserFolder.RoamingData => "APPDATA",
                _ => "USERPROFILE",
            };
            return user.TryGetValue(name, out string value) && IsOnADrive(value) ? value : null;
        }

        /// <summary>A full path on a drive of this PC: never a share nor a
        /// device path (\\?\, \\.\).</summary>
        internal static bool IsOnADrive(string path)
            => !string.IsNullOrEmpty(path) && path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
               && (path[2] == '\\' || path[2] == '/') && Path.IsPathFullyQualified(path);

        /// <summary>The program's own file for an assistant, or null when it
        /// is not on this PC.</summary>
        internal static string Find(string key) => key switch
        {
            // The packaged app keeps its exe under app\ in its package
            // folder (C:\Program Files\WindowsApps\Claude_6.28899.6.0_x64__
            // pzs8sxrjxfjjc\app\claude.exe here).
            "claude-desktop" => Existing(PackageRoot(ClaudeFamily) is { } root ? Path.Combine(root, "app", "claude.exe") : null),
            "claude-code" => Command("claude") ?? CommandOf("claude-code"),
            "codex" => Command("codex"),
            "gemini-cli" => Command("gemini"),
            "copilot-cli" => Command("copilot"),
            "vscode" => VsCode(),
            "visual-studio" => VisualStudio(),
            "zed" => Existing(Local("Programs", "Zed", "Zed.exe")) ?? Command("zed"),
            "junie" => Command("junie"),
            _ => null,
        };

        /// <summary>The program's own command where it keeps one off the
        /// PATH, or null. Claude Code's extension for VS Code carries the
        /// same claude.exe under resources\native-binary, and its own
        /// <c>mcp add</c> wrote the entry there (run on this PC on
        /// 2026-11-24, version 2.1.331), so a user who has Claude Code only
        /// through the extension can add PadForge too.</summary>
        internal static string CommandOf(string key)
        {
            if (key != "claude-code") return null;
            string profile = Folder(UserFolder.Profile);
            if (string.IsNullOrEmpty(profile)) return null;
            // The native installer's own place first.
            string native = Existing(Path.Combine(profile, ".local", "bin", "claude.exe"));
            if (native != null) return native;
            string extensions = Path.Combine(profile, ".vscode", "extensions");
            if (!Directory.Exists(extensions)) return null;
            return Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
                .Select(d => (Folder: d, Version: VersionIn(Path.GetFileName(d))))
                .Where(d => d.Version != null)
                .OrderByDescending(d => d.Version)
                .Select(d => Existing(Path.Combine(d.Folder, "resources", "native-binary", "claude.exe")))
                .FirstOrDefault(f => f != null);
        }

        /// <summary>The version in an extension's folder name,
        /// anthropic.claude-code-2.1.331-win32-x64.</summary>
        private static Version VersionIn(string folder)
        {
            const string Lead = "anthropic.claude-code-";
            if (!folder.StartsWith(Lead, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = folder.Substring(Lead.Length);
            int dash = rest.IndexOf('-');
            return Version.TryParse(dash < 0 ? rest : rest.Substring(0, dash), out var v) ? v : null;
        }

        /// <summary>VS Code puts bin\code.cmd on the PATH beside Code.exe's
        /// folder (C:\Program Files\Microsoft VS Code\bin here). Its user
        /// setup installs under %LOCALAPPDATA%\Programs.</summary>
        private static string VsCode()
        {
            string code = Command("code");
            if (code != null && Path.GetDirectoryName(Path.GetDirectoryName(code)) is { } folder
                && Existing(Path.Combine(folder, "Code.exe")) is { } exe)
                return exe;
            return Existing(Local("Programs", "Microsoft VS Code", "Code.exe"))
                ?? ProgramFiles().Select(p => Existing(Path.Combine(p, "Microsoft VS Code", "Code.exe"))).FirstOrDefault(f => f != null);
        }

        /// <summary>Visual Studio's devenv.exe, under Program Files\Microsoft
        /// Visual Studio\&lt;version&gt;\&lt;edition&gt;\Common7\IDE (18 for
        /// Visual Studio 2026 here, 2022 before it), the newest
        /// version first.</summary>
        private static string VisualStudio()
        {
            foreach (string programs in ProgramFiles())
            {
                string root = Path.Combine(programs, "Microsoft Visual Studio");
                if (!Directory.Exists(root)) continue;
                var found = Directory.EnumerateDirectories(root)
                    .OrderByDescending(d => MajorOf(Path.GetFileName(d)))
                    .SelectMany(version => Directory.EnumerateDirectories(version))
                    .Select(edition => Existing(Path.Combine(edition, "Common7", "IDE", "devenv.exe")))
                    .FirstOrDefault(f => f != null);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The major version a Visual Studio folder names: up to
        /// 2022 its folder is its year (2017 is 15, 2019 is 16, 2022 is 17),
        /// and from Visual Studio 2026 its major version (18). -1 for any
        /// other folder.</summary>
        internal static int MajorOf(string folder)
        {
            if (!int.TryParse(folder, out int n)) return -1;
            return n switch
            {
                2017 => 15,
                2019 => 16,
                2022 => 17,
                _ => n >= 2000 ? -1 : n,
            };
        }

        private static IReadOnlyList<string> ProgramFiles()
        {
            if (ProgramFilesSeam == null && UserInstall.OffLimitsForTest) return Array.Empty<string>();
            var seam = ProgramFilesSeam;
            if (seam != null) return seam();
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Local(params string[] parts)
        {
            string local = Folder(UserFolder.LocalData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(new[] { local }.Concat(parts).ToArray());
        }

        private static string Existing(string file) => !string.IsNullOrEmpty(file) && File.Exists(file) ? file : null;

        /// <summary>
        /// A packaged app's root folder: Windows lists each package installed
        /// for the user under HKEY_CURRENT_USER\Software\Classes\Local
        /// Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\
        /// Repository\Packages, by its full name (family name, version,
        /// architecture, publisher ID), with its PackageRootFolder. The
        /// newest version whose folder is there wins.
        /// </summary>
        internal static string PackageRoot(string family)
        {
            if (PackageRootSeam == null && UserInstall.OffLimitsForTest) return null;
            var seam = PackageRootSeam;
            if (seam != null) return seam(family);
            int underscore = family.IndexOf('_');
            if (underscore <= 0) return null;
            string name = family.Substring(0, underscore), publisher = family.Substring(underscore + 1);
            try
            {
                using var packages = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                if (packages == null) return null;
                var found = new List<(Version Version, string Root)>();
                foreach (string full in packages.GetSubKeyNames())
                {
                    // Name_Version_Architecture_ResourceId_PublisherId.
                    string[] parts = full.Split('_');
                    if (parts.Length != 5 || !string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(parts[4], publisher, StringComparison.OrdinalIgnoreCase)
                        || !Version.TryParse(parts[1], out var version))
                        continue;
                    using var key = packages.OpenSubKey(full);
                    if (key?.GetValue("PackageRootFolder") is string root && Directory.Exists(root)) found.Add((version, root));
                }
                return found.OrderByDescending(f => f.Version).Select(f => f.Root).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>Where a program that takes skills only as an upload takes
        /// them, in its own menus' words, which stay as the program writes
        /// them: Claude's "Customize > Skills" (claude.com/docs
        /// skills/how-to, research R6 of #600).</summary>
        internal static readonly IReadOnlyDictionary<string, string> UploadPlace = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude-desktop"] = "Customize, Skills",
        };

        /// <summary>
        /// The name each program gives itself as an MCP client
        /// (<c>clientInfo.name</c> in its initialize), by which PadForge
        /// tells which program a connected assistant is. Only names read at
        /// their source are listed: claude-code from Claude Code 2.1.331's
        /// initialize to a stub server (run on this PC on 2026-11-24), and
        /// codex-mcp-client, a string in Codex 0.153.3's own binary. VS
        /// Code's editor names its client Visual Studio Code, but the entry
        /// PadForge writes is read by its Copilot harness, whose name was not
        /// read, so VS Code is not listed. A program not listed is never said
        /// to be connected, nor to have not connected since: its row says
        /// Added.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> ClientNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["claude-code"] = "claude-code",
            ["codex"] = "codex-mcp-client",
        };

        /// <summary>The program a connected assistant is, by the name its
        /// copy of PadForge.exe --mcp gave in its hello: the client's own
        /// name, then its host's image in brackets
        /// (AutomationSession.Hello). Null when it names none of
        /// <see cref="ClientNames"/>.</summary>
        internal static string KeyOfClient(string claimedName)
        {
            if (string.IsNullOrEmpty(claimedName)) return null;
            foreach (var pair in ClientNames)
                if (string.Equals(claimedName, pair.Value, StringComparison.Ordinal)
                    || claimedName.StartsWith(pair.Value + " (", StringComparison.Ordinal))
                    return pair.Key;
            return null;
        }
    }
}
