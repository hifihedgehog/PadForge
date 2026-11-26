using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using PadForge.Engine.Common;
using PadForge.Services.Install;

namespace PadForge.Services.Automation
{
    /// <summary>One allowed program as PadForge.xml keeps it (#600).</summary>
    public sealed class AllowedProgramData
    {
        /// <summary>What the program is known by: its kind, its image's
        /// path and, for a script, its script file.</summary>
        [XmlAttribute] public string Key { get; set; }

        /// <summary>Its name as the Automation page lists it.</summary>
        [XmlAttribute] public string Name { get; set; }

        /// <summary>script, assistant or command-line.</summary>
        [XmlAttribute] public string Kind { get; set; }

        /// <summary>The program's full path: the interpreter for a script,
        /// the program that started PadForge's helper for an assistant.</summary>
        [XmlAttribute] public string Path { get; set; }

        /// <summary>A script's file, or empty.</summary>
        [XmlAttribute] public string Script { get; set; }

        /// <summary>Who signed the program, when Windows trusts its
        /// signature.</summary>
        [XmlAttribute] public string Signer { get; set; }

        /// <summary>The level allowed, as AutomationLevel's number.</summary>
        [XmlAttribute] public int Level { get; set; }

        /// <summary>When it was allowed, in UTC.</summary>
        [XmlAttribute] public DateTime Allowed { get; set; }

        /// <summary>When it last connected, in UTC, or the minimum.</summary>
        [XmlAttribute] public DateTime LastUsed { get; set; }

        /// <summary>Its reach (#600 DESIGN-2 N3) while it is held to its own
        /// devices and the ones picked. Absent for all of PadForge.</summary>
        [XmlElement("Reach")] public ReachData Reach { get; set; }
    }

    /// <summary>A program, as consent knows it (#600).</summary>
    /// <param name="Key">What it is known by (<see cref="AutomationConsent.KeyOf"/>).</param>
    /// <param name="Name">Its name, as the page lists it.</param>
    /// <param name="Kind">script, assistant or command-line.</param>
    /// <param name="Path">Its full path.</param>
    /// <param name="Script">A script's file, or null.</param>
    /// <param name="Signer">Who signed it, or null.</param>
    /// <param name="Level">The level allowed.</param>
    internal sealed record AllowedProgram(string Key, string Name, string Kind, string Path, string Script, string Signer, AutomationLevel Level)
    {
        public DateTimeOffset Allowed { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastUsed { get; init; }

        /// <summary>What it may drive and change (#600 DESIGN-2 N3), null
        /// for all of PadForge.</summary>
        public ProgramReach Reach { get; init; }

        /// <summary>Whether the connection is a copy of PadForge.exe, an
        /// assistant's helper or the command line, named by the program
        /// that started it. Read from the connection, never kept.</summary>
        public bool ThroughHelper { get; init; }

        /// <summary>Whether Windows named no program for it, so its key is
        /// the connection's process ID and nothing tells it from another
        /// program. Never kept.</summary>
        public bool Unknown { get; init; }
    }

    /// <summary>
    /// Consent per program (#600): the programs the user allowed to work
    /// with PadForge at Read or above, and the level each asked for. A
    /// program that connects at Read or above for the first time waits for
    /// the user's answer on the question card, which names its path, its
    /// signer and, for a script, its interpreter and script file. Adding an
    /// assistant on its row on the Automation page is the consent for it
    /// (<see cref="AllowAssistant"/>). The list is kept in PadForge.xml and
    /// listed on the Automation page with Remove.
    ///
    /// <para>Consent names programs to the user. It is no barrier against
    /// software already running as the user, which could start an allowed
    /// program itself.</para>
    ///
    /// <para>Any thread may read and change it. <see cref="Changed"/> is
    /// raised after a change, on the thread that made it.</para>
    /// </summary>
    internal sealed class AutomationConsent
    {
        /// <summary>The app's own list, which PadForge.xml loads and saves.
        /// A test gives the sessions one of its own.</summary>
        internal static readonly AutomationConsent Shared = new AutomationConsent();

        /// <summary>The most programs the list keeps. The longest unused go
        /// first.</summary>
        internal const int MostPrograms = 64;

        private readonly object _sync = new object();
        private readonly List<AllowedProgram> _programs = new();

        /// <summary>Raised after the list changes.</summary>
        public event EventHandler Changed;

        /// <summary>The arguments <see cref="Changed"/> carries when the
        /// list was loaded from PadForge.xml, which holds it already.</summary>
        internal static readonly EventArgs FromFile = new EventArgs();

        /// <summary>The programs allowed, in the order allowed.</summary>
        public IReadOnlyList<AllowedProgram> Programs
        {
            get { lock (_sync) return _programs.ToArray(); }
        }

        /// <summary>Set by a test's own list, never the app's: every program
        /// reads as allowed at every level, so a test of something else
        /// raises no card.</summary>
        internal bool AllowsEveryProgramForTest { get; init; }

        /// <summary>The record of a program by its key, or null.</summary>
        public AllowedProgram Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (AllowsEveryProgramForTest) return new AllowedProgram(key, null, null, null, null, null, AutomationLevel.ChangeSettings);
            lock (_sync) return _programs.Find(p => string.Equals(p.Key, key, StringComparison.Ordinal));
        }

        /// <summary>Allows a program at a level, or raises one already
        /// allowed to it. A program allowed at a higher level keeps it.</summary>
        public void Allow(AllowedProgram program)
        {
            if (program == null || string.IsNullOrEmpty(program.Key)) return;
            lock (_sync)
            {
                int at = _programs.FindIndex(p => p.Key == program.Key);
                if (at >= 0)
                {
                    // The reach the user set on its row stays (#600
                    // DESIGN-2 N3), with the devices picked. A row with all
                    // of PadForge takes the own devices a program asked for
                    // on the card the user allowed.
                    var old = _programs[at];
                    _programs[at] = program with
                    {
                        Level = (AutomationLevel)Math.Max((int)old.Level, (int)program.Level), Allowed = old.Allowed,
                        Reach = ProgramReach.Within(old.Reach, program.Reach),
                    };
                }
                else
                {
                    _programs.Add(program);
                    while (_programs.Count > MostPrograms)
                        _programs.Remove(_programs.OrderBy(p => p.LastUsed ?? p.Allowed).First());
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Allows an assistant the user added on its row (#600):
        /// the program that will start PadForge's helper, at the level it
        /// works at, which the user's level caps. The file the row found is
        /// read for the program it runs: an npm shim for its interpreter and
        /// script, a link for its target (<see cref="AssistantIdentity"/>),
        /// so the key is the one its connection carries.</summary>
        public void AllowAssistant(string hostPath, string name, AutomationLevel level = AutomationLevel.ChangeSettings)
        {
            if (string.IsNullOrWhiteSpace(hostPath)) return;
            var identity = AssistantIdentity.Of(hostPath);
            string path = identity.Path ?? hostPath;
            Allow(new AllowedProgram(AssistantIdentity.ConsentKey(identity), name, "assistant", path, identity.Script,
                AuthenticodeSigner.Of(path), level));
        }

        /// <summary>The consent key of the assistant a row found by its
        /// file.</summary>
        internal static string AssistantKeyOf(string hostPath) => AssistantIdentity.ConsentKey(AssistantIdentity.Of(hostPath));

        /// <summary>Takes an assistant's consent away when the user removes
        /// it from its row.</summary>
        public bool RemoveAssistant(string hostPath) => Remove(AssistantKeyOf(hostPath));

        /// <summary>
        /// The allowed assistant a connection belongs to (#600): itself when
        /// it is on the list, else an assistant added through its npm
        /// package's script whose package holds the connection's program.
        /// Codex's and Copilot's npm launchers start a native program inside
        /// their package (codex-cli/bin/codex.js, npm-loader.js), and that
        /// program starts PadForge.exe. The connection then goes by the added
        /// assistant's key.
        /// </summary>
        internal AllowedProgram Covering(AllowedProgram who)
        {
            if (who == null || who.Kind != "assistant" || who.Script != null || string.IsNullOrEmpty(who.Path) || Find(who.Key) != null)
                return who;
            foreach (var program in Programs)
                if (program.Kind == "assistant" && program.Script != null && AssistantIdentity.InsidePackageOf(program.Script, who.Path))
                    return who with { Key = program.Key };
            return who;
        }

        /// <summary>Sets a program's reach (#600 DESIGN-2 N3): null for all
        /// of PadForge, else its own devices and the ones picked. False for
        /// a program not on the list.</summary>
        public bool SetReach(string key, ProgramReach reach)
        {
            bool changed;
            lock (_sync)
            {
                int at = _programs.FindIndex(p => p.Key == key);
                changed = at >= 0 && !Equals(_programs[at].Reach, reach);
                if (changed) _programs[at] = _programs[at] with { Reach = reach };
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            return changed;
        }

        /// <summary>Takes a program off the list. It asks again the next time
        /// it connects.</summary>
        public bool Remove(string key)
        {
            bool removed;
            lock (_sync) removed = _programs.RemoveAll(p => p.Key == key) > 0;
            if (removed) Changed?.Invoke(this, EventArgs.Empty);
            return removed;
        }

        /// <summary>Notes that a program connected, for the page's line and
        /// for which goes first past the limit, with the signer read of its
        /// file at this connection, so its row names the file as it is now.
        /// A connection of another file under the key (the program inside
        /// an npm package, which Covering keys by the package's row) leaves
        /// the row's signer as it was. Raises no change of its own: the
        /// next save keeps it.</summary>
        public void Used(string key, DateTimeOffset when, string path = null, string signer = null)
        {
            lock (_sync)
            {
                int at = _programs.FindIndex(p => p.Key == key);
                if (at < 0) return;
                var row = _programs[at];
                bool sameFile = path != null && string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase);
                _programs[at] = row with { LastUsed = when, Signer = sameFile ? signer : row.Signer };
            }
        }

        /// <summary>The list from PadForge.xml, in place of what was
        /// held. <see cref="Changed"/> carries <see cref="FromFile"/>, so a
        /// listener that keeps the list unsaved can leave it, and the open
        /// sessions of the programs work within the list loaded
        /// (AutomationSessions.Reconcile).</summary>
        public void Load(IEnumerable<AllowedProgramData> saved)
        {
            lock (_sync)
            {
                _programs.Clear();
                foreach (var d in saved ?? Array.Empty<AllowedProgramData>())
                {
                    if (d == null || string.IsNullOrEmpty(d.Key) || _programs.Any(p => p.Key == d.Key)) continue;
                    var level = AutomationLevels.FromSaved(d.Level);
                    if (level < AutomationLevel.Read) continue;
                    _programs.Add(new AllowedProgram(d.Key, d.Name, d.Kind, d.Path, string.IsNullOrEmpty(d.Script) ? null : d.Script, d.Signer, level)
                    {
                        Allowed = new DateTimeOffset(DateTime.SpecifyKind(d.Allowed, DateTimeKind.Utc)),
                        LastUsed = d.LastUsed > DateTime.MinValue ? new DateTimeOffset(DateTime.SpecifyKind(d.LastUsed, DateTimeKind.Utc)) : null,
                        Reach = ProgramReach.FromData(d.Reach),
                    });
                    if (_programs.Count >= MostPrograms) break;
                }
            }
            Changed?.Invoke(this, FromFile);
        }

        /// <summary>The list as PadForge.xml keeps it, or null when it is
        /// empty.</summary>
        public AllowedProgramData[] Save()
        {
            lock (_sync)
            {
                if (_programs.Count == 0) return null;
                return _programs.Select(p => new AllowedProgramData
                {
                    Key = p.Key, Name = p.Name, Kind = p.Kind, Path = p.Path, Script = p.Script ?? string.Empty, Signer = p.Signer,
                    Level = (int)p.Level, Allowed = p.Allowed.UtcDateTime, LastUsed = p.LastUsed?.UtcDateTime ?? DateTime.MinValue,
                    Reach = p.Reach?.ToData(),
                }).ToArray();
            }
        }

        /// <summary>A program's key: its kind, its path and its script,
        /// without regard to case, since Windows' paths have none. A command's
        /// words in place of a script keep their case.</summary>
        internal static string KeyOf(string kind, string path, string script, bool scriptIsWords = false)
            => kind + "|" + (path ?? string.Empty).ToUpperInvariant()
               + (string.IsNullOrEmpty(script) ? string.Empty : "|" + (scriptIsWords ? script : script.ToUpperInvariant()));

        /// <summary>Whether a key names the program's process number in
        /// place of its file: <see cref="Identify"/> keys a process by
        /// "?" and its number when Windows gave neither its path nor its
        /// image name. No path holds a "?", so the mark is that one
        /// character after the kind.</summary>
        internal static bool NamesOnlyAProcess(string key)
            => key != null && key.Contains("|?", StringComparison.Ordinal);

        // ─────────────────────────────────────────────
        //  Who a connection is
        // ─────────────────────────────────────────────

        /// <summary>The interpreters whose program is the script they run:
        /// the script's file is named on the card beside them.</summary>
        internal static readonly string[] Interpreters =
        {
            "python.exe", "pythonw.exe", "py.exe", "pyw.exe", "powershell.exe", "pwsh.exe", "powershell_ise.exe",
            "autohotkey.exe", "autohotkey32.exe", "autohotkey64.exe", "autohotkeyu32.exe", "autohotkeyu64.exe", "autohotkeya32.exe",
            "wscript.exe", "cscript.exe", "node.exe", "cmd.exe", "ruby.exe", "perl.exe", "lua.exe", "java.exe", "javaw.exe", "dotnet.exe",
        };

        /// <summary>
        /// Who a connection is for consent, from what Windows says of its
        /// process alone: never from what it calls itself. A script run by
        /// an interpreter is its interpreter and its script file, read from
        /// its command line with a limited query (the one OpenRGB's address
        /// is read with, OpenRgbServerPort.CommandLineOf). A copy of
        /// PadForge.exe (an assistant's helper or the command line) is the
        /// program that started it, read from its process with the same
        /// limited query.
        /// </summary>
        internal static AllowedProgram Identify(AutomationClientProcess process, string kindWord, string claimedName)
        {
            string path = process?.ImagePath;
            string image = process?.ImageName ?? (path == null ? null : System.IO.Path.GetFileName(path));
            string kind = kindWord == "assistant" ? "assistant" : "script";
            if (image != null && IsPadForge(path))
            {
                // A helper of PadForge's own: the program behind it asks.
                int parent = ParentOf(process.Id, out string parentPath);
                string name = kind == "assistant"
                    ? claimedName
                    : PadForge.Resources.Strings.Strings.Instance.Automation_Consent_CommandLine;
                if (kind != "assistant") kind = "command-line";
                // The program that started it ended before it was read, or
                // Windows would not name it: it is a program nobody can name,
                // keyed by the helper's process as any such program is, and
                // never named as PadForge itself.
                if (parent <= 0 || parentPath == null)
                    return new AllowedProgram(KeyOf(kind, "?" + process.Id, null), string.IsNullOrWhiteSpace(name) ? null : name, kind,
                        null, null, null, AutomationLevel.Commands) { ThroughHelper = true, Unknown = true };
                // A program an interpreter runs is its script, as for a
                // script that connects itself: one answer for node.exe must
                // not cover every program node.exe runs.
                string parentImage = System.IO.Path.GetFileName(parentPath);
                bool parentWords = false;
                string parentScript = Interpreters.Contains(parentImage.ToLowerInvariant()) ? ScriptOf(parent, parentImage, out parentWords) : null;
                // An assistant goes by its script alone, as its row keys it,
                // when the script is a file named in full. A relative name
                // or a command's words go with the interpreter that runs
                // them.
                string key = kind == "assistant" && parentScript != null && System.IO.Path.IsPathFullyQualified(parentScript)
                    || kind == "assistant" && parentScript == null
                    ? AssistantIdentity.ConsentKey(new AssistantIdentity.Identity(parentPath, parentScript, null))
                    : KeyOf(kind, parentPath, parentScript, parentWords);
                return new AllowedProgram(key,
                    string.IsNullOrWhiteSpace(name) ? ShortName(parentScript ?? parentPath) : name, kind,
                    parentPath, parentScript, AuthenticodeSigner.Of(parentPath), AutomationLevel.Commands)
                { ThroughHelper = true };
            }
            string script = null;
            bool words = false;
            if (image != null && Interpreters.Contains(image.ToLowerInvariant()))
                script = ScriptOf(process.Id, image, out words);
            string named = !string.IsNullOrWhiteSpace(claimedName) ? claimedName : ShortName(script ?? path);
            return new AllowedProgram(KeyOf(kind, path ?? image ?? "?" + (process?.Id ?? 0), script, words), named, kind, path, script,
                process?.Signer, AutomationLevel.Commands) { Unknown = path == null && image == null };
        }

        private static string ShortName(string path)
            => string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);

        /// <summary>Whether a path is this PadForge.exe.</summary>
        private static bool IsPadForge(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string own = Environment.ProcessPath;
            return own != null && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Test seam: a process's command line, by its ID.</summary>
        internal static Func<int, string> CommandLineSeam;

        /// <summary>Test seam: a process's image path, by its ID, for the
        /// program behind PadForge's own helper.</summary>
        internal static Func<int, string> ParentPathSeam;

        /// <summary>Test seam: a process's parent, by its ID.</summary>
        internal static Func<int, int> ParentSeam;

        /// <summary>Test seam: a process's start, by its ID, in
        /// GetProcessTimes' units, in place of the one Windows keeps, so a
        /// test can give a process's parent ID to a younger process.</summary>
        internal static Func<int, long> StartSeam;

        /// <summary>The script a process runs: the first word after the
        /// interpreter that names a file by its extension, as the command
        /// line wrote it, with the interpreter's own switches and their
        /// values (such as Python's -X utf8 and PowerShell's
        /// -ExecutionPolicy Bypass) passed over.
        /// PowerShell's -File names the script next. Python's -m names a
        /// module. A run with no file, -c, -Command, -EncodedCommand or
        /// none, is named by its words (<see cref="WordsOf"/>).</summary>
        internal static string ScriptOf(int processId, string image) => ScriptOf(processId, image, out _);

        /// <summary><see cref="ScriptOf(int, string)"/>, and whether what it
        /// gives is a command's words, whose case the key keeps: Windows'
        /// paths have no case, and a command's text has.</summary>
        internal static string ScriptOf(int processId, string image, out bool words)
        {
            words = false;
            string line = CommandLineSeam != null ? CommandLineSeam(processId)
                : processId > 0 ? PadForge.Common.Input.Peripherals.OpenRgbServerPort.CommandLineOf(unchecked((uint)processId)) : null;
            var args = PadForge.Common.Input.Peripherals.OpenRgbServerPort.Split(line);
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if (a.Length == 0) continue;
                if (a.StartsWith("-", StringComparison.Ordinal) || a.StartsWith("/", StringComparison.Ordinal))
                {
                    // PowerShell's -File names the script next.
                    if ((a.Equals("-File", StringComparison.OrdinalIgnoreCase) || a.Equals("-f", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
                        return AsWritten(args[i + 1]);
                    // Python's -m names a module: no file.
                    if (a.Equals("-m", StringComparison.Ordinal) && i + 1 < args.Count) return "-m " + args[i + 1];
                    if (a.Equals("-c", StringComparison.Ordinal) || a.Equals("-Command", StringComparison.OrdinalIgnoreCase)) return WordsOf(args, out words);
                    continue;
                }
                // A switch's value, such as utf8 or Bypass, names no file.
                if (!System.IO.Path.HasExtension(a)) continue;
                return AsWritten(a);
            }
            return WordsOf(args, out words);
        }

        /// <summary>How much of a command line's words a card and a row
        /// show.</summary>
        internal const int LongestWords = 200;

        /// <summary>A run with no script file, named by the words of its
        /// command line after the interpreter, as -m names a module: each
        /// distinct command is its own program. Words past
        /// <see cref="LongestWords"/> are cut and the rest named by a hash
        /// of all of them, so two long commands are still told apart. Null
        /// for a run with no words, the interpreter alone.</summary>
        private static string WordsOf(IReadOnlyList<string> args, out bool isWords)
        {
            isWords = args.Count > 1;
            var words = new System.Text.StringBuilder();
            for (int i = 1; i < args.Count; i++)
            {
                if (words.Length > 0) words.Append(' ');
                string a = args[i];
                if (a.Length == 0 || a.IndexOf(' ') >= 0) words.Append('"').Append(a).Append('"');
                else words.Append(a);
            }
            if (words.Length == 0) return null;
            string all = words.ToString();
            string shown = ProgramDeviceDeclaration.Clean(all, LongestWords);
            if (shown == all) return shown;
            // Cut, or cleaned of characters that hide text: a hash of all of
            // it keeps the key its own.
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(all));
            return shown + (all.Length > LongestWords ? "\u2026" : string.Empty) + " " + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>A script's path as the command line wrote it. A full
        /// path is normalized. A relative one is left as it is: it names a
        /// file in the client's own folder, which sits in the client's
        /// memory and is never read, and PadForge's folder would name
        /// another file.</summary>
        private static string AsWritten(string path)
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return path;
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        private const int ProcessBasicInformation = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public uint ExitStatus;
            public IntPtr PebBaseAddress;
            public UIntPtr AffinityMask;
            public int BasePriority;
            public UIntPtr UniqueProcessId;
            public UIntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref PROCESS_BASIC_INFORMATION information,
            int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>
        /// The process that started a process and its image's path, read
        /// with limited queries, as .NET's Process.ParentProcessId reads the
        /// ID (ProcessBasicInformation). Windows keeps that ID after the
        /// parent ends and gives it to a later process, so a parent counts
        /// only when it started before the child, as .NET's IsParentOf
        /// judges one (Process.Win32.cs, StartTime before the child's), and
        /// its start and path are read through one handle. 0 and a null path
        /// when it is gone, younger, or cannot be read.
        /// </summary>
        internal static int ParentOf(int processId, out string path)
        {
            path = null;
            if (ParentSeam != null)
            {
                int seen = ParentSeam(processId);
                if (seen > 0) path = ParentPathSeam != null ? ParentPathSeam(seen) : PadForge.Common.Input.ProcessImage.PathOf(unchecked((uint)seen));
                return seen;
            }
            if (processId <= 0) return 0;
            IntPtr child = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (child == IntPtr.Zero) return 0;
            try
            {
                var info = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(child, ProcessBasicInformation, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0) return 0;
                int parent = unchecked((int)info.InheritedFromUniqueProcessId.ToUInt64());
                if (parent <= 0 || !StartOf(child, processId, out long childStarted)) return 0;
                IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, parent);
                if (handle == IntPtr.Zero) return 0;
                try
                {
                    if (!StartOf(handle, parent, out long parentStarted) || !StartedBefore(parentStarted, childStarted)) return 0;
                    path = ParentPathSeam != null ? ParentPathSeam(parent) : ImagePathOf(handle);
                    return path == null ? 0 : parent;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            finally
            {
                CloseHandle(child);
            }
        }

        /// <summary>Whether a process that started at
        /// <paramref name="parentStarted"/> started before one that started
        /// at <paramref name="childStarted"/>, both as GetProcessTimes gives
        /// them.</summary>
        internal static bool StartedBefore(long parentStarted, long childStarted) => parentStarted < childStarted;

        /// <summary>When an open process started, read through its handle,
        /// or what <see cref="StartSeam"/> gives for its ID.</summary>
        private static bool StartOf(IntPtr handle, int processId, out long started)
        {
            if (StartSeam != null) { started = StartSeam(processId); return true; }
            return GetProcessTimes(handle, out started, out _, out _, out _);
        }

        /// <summary>The image's full path of an open process, or null.</summary>
        private static string ImagePathOf(IntPtr handle)
        {
            var buffer = new char[1024];
            while (true)
            {
                uint size = (uint)buffer.Length;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return new string(buffer, 0, (int)size);
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || buffer.Length >= 32768) return null;
                buffer = new char[buffer.Length * 4];
            }
        }

        /// <summary>A level as a hello names it: commands, read,
        /// send-inputs or send_inputs, change-settings or change_settings.
        /// False for any other word.</summary>
        internal static bool TryLevel(string word, out AutomationLevel level)
        {
            switch ((word ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "commands": level = AutomationLevel.Commands; return true;
                case "read": level = AutomationLevel.Read; return true;
                case "send-inputs": level = AutomationLevel.SendInputs; return true;
                case "change-settings": level = AutomationLevel.ChangeSettings; return true;
                default: level = AutomationLevel.Commands; return false;
            }
        }
    }
}
