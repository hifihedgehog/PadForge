using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Resources.Strings;
using PadForge.Engine.Common;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// A program that reached PadForge from outside, as the user is shown it
    /// (#597). The process ID and the image name are what Windows says of the
    /// other end of a pipe. They are for the user's eyes and decide nothing: a
    /// program can stage both.
    /// </summary>
    /// <param name="Name">What the program calls itself, or its image name
    /// without the extension when it gave none. Null when neither is known.</param>
    /// <param name="ProcessId">The process's ID, or 0.</param>
    /// <param name="ImageName">The file name of the program's image, such as
    /// powershell.exe, with no folder. Null when Windows would not say.</param>
    /// <param name="ImagePath">The image's full path, which the user is
    /// shown: a file name alone says nothing of where a program runs from,
    /// and a program can copy itself to any name. Null when Windows would
    /// not say.</param>
    /// <param name="Signer">Who signed the image, when Windows trusts its
    /// signature (<see cref="AuthenticodeSigner"/>), or null.</param>
    internal sealed record AutomationProgram(string Name, int ProcessId, string ImageName,
        string ImagePath = null, string Signer = null)
    {
        /// <summary>A program nothing is known of.</summary>
        internal static readonly AutomationProgram Unknown = new AutomationProgram(null, 0, null);

        /// <summary>The program a process ID names now. A one-line command
        /// names no program of its own, so its image stands in for a
        /// name.</summary>
        internal static AutomationProgram FromProcessId(int processId)
        {
            if (processId <= 0) return Unknown;
            string image = null, path = null;
            try
            {
                path = PadForge.Common.Input.ProcessImage.PathOf(unchecked((uint)processId));
                if (!string.IsNullOrEmpty(path)) image = Path.GetFileName(path);
                else path = null;
            }
            catch (ArgumentException)
            {
                // A path Windows gave that is no path. The ID still shows.
                path = null;
            }
            return new AutomationProgram(
                image == null ? null : PadForge.Common.Input.RunningProcessList.ShortName(image), processId, image,
                path, AuthenticodeSigner.Of(path));
        }

        /// <summary>What Activity counts a program's entries by: its image's
        /// path, which a program cannot change by calling itself something
        /// else, and its process where Windows gave no path.</summary>
        internal string Key => !string.IsNullOrEmpty(ImagePath) ? ImagePath.ToUpperInvariant()
            : !string.IsNullOrEmpty(ImageName) ? ImageName.ToUpperInvariant() + "|" + ProcessId
            : "|" + ProcessId + "|" + Name;
    }

    /// <summary>What a call can ask for, one for each sentence Activity
    /// writes.</summary>
    internal enum AutomationAskKind
    {
        Unknown,
        ActivateProfile,
        DeactivateProfile,
        QueryProfile,
        ShowWindow,
        HideWindow,
        MinimizeWindow,
        Exit,
        EngineOn,
        EngineOff,
        ControllersOn,
        ControllersOff,
        ControllerOn,
        ControllerOff,
        Snapshot,
        RecordStart,
        RecordStop,
        RecordMark,
        Version,
        State,

        // The Automation pipe's verbs (#597).
        Connected,
        Disconnected,
        Devices,
        Caps,
        Read,
        Outputs,
        Subscribe,
        Unsubscribe,
        Declare,
        Set,
        Gone,
        Own,
        Release,
        Value,
        Call,

        /// <summary>A tool an assistant or a script's <c>call</c> line ran
        /// (#597): the argument is the tool's name and the detail what it
        /// named.</summary>
        Tool,

        /// <summary>The pipe's <c>rest</c>: every control of a program's
        /// device at rest (#600).</summary>
        Rest,
    }

    /// <summary>
    /// What a call asked, kept as its kind and the one thing it named, and put
    /// into words when it is read, in the language in use then.
    /// </summary>
    /// <param name="Kind">The ask.</param>
    /// <param name="Argument">The profile, the controller's number or the
    /// line itself, for the kinds that name one.</param>
    /// <param name="Count">How many times the program asked it, as a stream
    /// of lines on the Automation pipe and refused lines of one kind are one
    /// entry.</param>
    /// <param name="Mixed">Whether the asks counted named different things,
    /// of which <paramref name="Argument"/> is the last.</param>
    /// <param name="Detail">For a tool, what the call named: a profile, a
    /// setting, a controller. Null for none.</param>
    internal readonly record struct AutomationAsk(AutomationAskKind Kind, string Argument = null, int Count = 1, bool Mixed = false, string Detail = null)
    {
        /// <summary>The longest stretch of a program's own text a sentence
        /// carries.</summary>
        internal const int LongestArgument = 80;

        /// <summary>The ask in plain words, with its count when it came more
        /// than once.</summary>
        public string Text
            => Count <= 1 ? Once
                : Mixed ? string.Format(Strings.Instance.Automation_Asked_RepeatedLast_Format, Once, Count)
                : string.Format(Strings.Instance.Automation_Asked_Repeated_Format, Once, Count);

        /// <summary>The ask once, in plain words.</summary>
        private string Once
        {
            get
            {
                var s = Strings.Instance;
                // A program's own text, with no character that could make it
                // read as something else.
                string argument = ShownText.Clean(Argument ?? string.Empty);
                // The one-character ellipsis the interface's strings use.
                if (argument.Length > LongestArgument) argument = argument.Substring(0, LongestArgument) + "…";
                return Kind switch
                {
                    AutomationAskKind.ActivateProfile => string.Format(s.Automation_Asked_Activate_Format, argument),
                    AutomationAskKind.DeactivateProfile => s.Automation_Asked_Deactivate,
                    AutomationAskKind.QueryProfile => s.Automation_Asked_Query,
                    AutomationAskKind.ShowWindow => s.Automation_Asked_Show,
                    AutomationAskKind.HideWindow => s.Automation_Asked_Hide,
                    AutomationAskKind.MinimizeWindow => s.Automation_Asked_Minimize,
                    AutomationAskKind.Exit => s.Automation_Asked_Exit,
                    AutomationAskKind.EngineOn => s.Automation_Asked_EngineOn,
                    AutomationAskKind.EngineOff => s.Automation_Asked_EngineOff,
                    AutomationAskKind.ControllersOn => s.Automation_Asked_ControllersOn,
                    AutomationAskKind.ControllersOff => s.Automation_Asked_ControllersOff,
                    AutomationAskKind.ControllerOn => string.Format(s.Automation_Asked_ControllerOn_Format, argument),
                    AutomationAskKind.ControllerOff => string.Format(s.Automation_Asked_ControllerOff_Format, argument),
                    AutomationAskKind.Snapshot => s.Automation_Asked_Snapshot,
                    AutomationAskKind.RecordStart => s.Automation_Asked_RecordStart,
                    AutomationAskKind.RecordStop => s.Automation_Asked_RecordStop,
                    AutomationAskKind.RecordMark => s.Automation_Asked_RecordMark,
                    AutomationAskKind.Version => s.Automation_Asked_Version,
                    AutomationAskKind.State => s.Automation_Asked_State,
                    AutomationAskKind.Connected => s.Automation_Asked_Connected,
                    AutomationAskKind.Disconnected => s.Automation_Asked_Disconnected,
                    AutomationAskKind.Devices => s.Automation_Asked_Devices,
                    AutomationAskKind.Caps => string.Format(s.Automation_Asked_Caps_Format, argument),
                    AutomationAskKind.Read => string.Format(s.Automation_Asked_Read_Format, argument),
                    AutomationAskKind.Outputs => string.Format(s.Automation_Asked_Outputs_Format, argument),
                    AutomationAskKind.Subscribe => string.Format(s.Automation_Asked_Subscribe_Format, argument),
                    AutomationAskKind.Unsubscribe => string.Format(s.Automation_Asked_Unsubscribe_Format, argument),
                    AutomationAskKind.Declare => string.Format(s.Automation_Asked_Declare_Format, argument),
                    AutomationAskKind.Set => string.Format(s.Automation_Asked_Set_Format, argument),
                    AutomationAskKind.Gone => string.Format(s.Automation_Asked_Gone_Format, argument),
                    AutomationAskKind.Own => string.Format(s.Automation_Asked_Own_Format, argument),
                    AutomationAskKind.Release => string.Format(s.Automation_Asked_Release_Format, argument),
                    AutomationAskKind.Value => string.Format(s.Automation_Asked_Value_Format, argument),
                    AutomationAskKind.Call => string.Format(s.Automation_Asked_Call_Format, argument),
                    AutomationAskKind.Tool => AssistantToolWords.Asked(Argument, Detail),
                    AutomationAskKind.Rest => string.Format(s.Automation_Asked_Rest_Format, argument),
                    _ => string.Format(s.Automation_Asked_Unknown_Format, argument),
                };
            }
        }
    }

    /// <summary>One call a program made.</summary>
    /// <param name="Time">When it came.</param>
    /// <param name="Program">Who made it.</param>
    /// <param name="Asked">What it asked.</param>
    /// <param name="Result">What PadForge answered, as it answered: the
    /// response's fixed words.</param>
    /// <param name="Succeeded">Whether the answer was a yes.</param>
    internal sealed record AutomationActivityEntry(
        DateTimeOffset Time, AutomationProgram Program, AutomationAsk Asked, string Result, bool Succeeded)
    {
        /// <summary>Whether the entry records a question the user was asked
        /// and how it ended. The part that asks sets it.</summary>
        public bool HoldsApproval { get; init; }

        /// <summary>Whether the entry records something done: a change, an
        /// input sent or held, or a question the user answered. No other
        /// program's lines push one out of Activity.</summary>
        public bool Keeps => HoldsApproval || Spell != 0 || (Succeeded && !ReadsOnly && !AutomationActivity.OnlyReads(Asked.Kind));

        /// <summary>Whether the call only read, for a kind that can do
        /// either: an assistant's tool, by the tool's own hint.</summary>
        public bool ReadsOnly { get; init; }

        /// <summary>The undo journal's entry a tool's change joined, which
        /// the Activity card's Undo restores, or 0 for a call that changed
        /// nothing (#597).</summary>
        public int Spell { get; init; }
    }

    /// <summary>
    /// The record of what outside programs asked of PadForge (#597), oldest
    /// first. Kept in memory for the session and never written to disk. Any
    /// thread may add to it and read it.
    ///
    /// <para>One program cannot push the record of what it or another
    /// program did out of it:</para>
    /// <list type="bullet">
    /// <item>A refused line merges with the program's last refused line of
    /// the same kind and answer into one entry with a count, whatever it
    /// named. A successful ask merges with the same ask the program made
    /// before, unless it is a tool call or a question the user answered,
    /// which are each their own.</item>
    /// <item>A program holds at most <see cref="PerProgram"/> entries. Past
    /// that its own oldest entry that records nothing done goes, and only
    /// when it has none of those its own oldest entry.</item>
    /// <item>The record holds <see cref="Capacity"/> entries. Past that the
    /// program adding gives up its own oldest entry that records nothing
    /// done. A line of its that records nothing done is left out when it has
    /// no such entry to give up, so it pushes out no other program's. An
    /// entry that records something done always goes in, and then the
    /// oldest entry that records nothing done of the program that holds the
    /// most gives way, or, where every entry records something done, that
    /// program's oldest.</item>
    /// </list>
    /// <para>A program is told apart by its image's path
    /// (<see cref="AutomationProgram.Key"/>), which it cannot change by
    /// calling itself something else.</para>
    /// </summary>
    internal sealed class AutomationActivity
    {
        /// <summary>How many calls are kept.</summary>
        internal const int Capacity = 500;

        /// <summary>How many calls of one program are kept.</summary>
        internal const int PerProgram = 50;

        private readonly object _sync = new object();
        private readonly List<AutomationActivityEntry> _entries = new List<AutomationActivityEntry>(Capacity);

        /// <summary>Raised after a call is added and after
        /// <see cref="Clear"/>, on the thread that did it. A listener that
        /// draws goes to its own thread.</summary>
        public event EventHandler Changed;

        /// <summary>How many calls are held.</summary>
        public int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>The asks that only read and change nothing.</summary>
        internal static bool OnlyReads(AutomationAskKind kind) => kind is AutomationAskKind.Unknown
            or AutomationAskKind.QueryProfile or AutomationAskKind.Version or AutomationAskKind.State
            or AutomationAskKind.Connected or AutomationAskKind.Disconnected or AutomationAskKind.Devices
            or AutomationAskKind.Caps or AutomationAskKind.Read or AutomationAskKind.Outputs
            or AutomationAskKind.Subscribe or AutomationAskKind.Unsubscribe;

        /// <summary>Adds a call by the rules above.</summary>
        public void Add(AutomationActivityEntry entry)
        {
            if (entry == null) return;
            bool changed;
            lock (_sync) changed = AddLocked(entry);
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }

        private bool AddLocked(AutomationActivityEntry entry)
        {
            string key = KeyOf(entry);
            int same = MergeTarget(entry, key);
            if (same >= 0)
            {
                var old = _entries[same];
                _entries.RemoveAt(same);
                bool mixed = old.Asked.Mixed || !string.Equals(old.Asked.Argument, entry.Asked.Argument, StringComparison.Ordinal)
                    || !string.Equals(old.Asked.Detail, entry.Asked.Detail, StringComparison.Ordinal);
                entry = entry with { Asked = entry.Asked with { Count = old.Asked.Count + entry.Asked.Count, Mixed = mixed } };
            }
            else if (_entries.Count >= Capacity && !entry.Keeps && OldestOf(key, keepers: false) < 0)
            {
                // The record is full of other programs' entries and of this
                // program's that record something done. A line that records
                // nothing done pushes none of them out.
                return false;
            }
            _entries.Add(entry);

            if (CountOf(key) > PerProgram)
            {
                int oldest = OldestOf(key, keepers: false);
                if (oldest < 0) oldest = OldestOf(key, keepers: true);
                if (oldest >= 0) _entries.RemoveAt(oldest);
            }
            while (_entries.Count > Capacity)
            {
                int oldest = OldestOf(key, keepers: false);
                if (oldest < 0 || ReferenceEquals(_entries[oldest], entry))
                {
                    string largest = LargestHolder();
                    oldest = OldestOf(largest, keepers: false);
                    if (oldest < 0) oldest = OldestOf(largest, keepers: true);
                }
                if (oldest < 0) break;
                _entries.RemoveAt(oldest);
            }
            return true;
        }

        private static string KeyOf(AutomationActivityEntry entry) => (entry.Program ?? AutomationProgram.Unknown).Key;

        /// <summary>The newest entry of the program that the entry merges
        /// with, or -1.</summary>
        private int MergeTarget(AutomationActivityEntry entry, string key)
        {
            if (entry.HoldsApproval) return -1;
            if (entry.Succeeded && entry.Asked.Kind == AutomationAskKind.Call) return -1;
            // A change an assistant made carries its own Undo, which a merge
            // would lose.
            if (entry.Spell != 0) return -1;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.HoldsApproval || e.Spell != 0 || e.Succeeded != entry.Succeeded || e.Asked.Kind != entry.Asked.Kind) continue;
                if (!string.Equals(KeyOf(e), key, StringComparison.Ordinal) || !Equals(e.Program, entry.Program)) continue;
                if (entry.Succeeded)
                {
                    if (string.Equals(e.Asked.Argument, entry.Asked.Argument, StringComparison.Ordinal)
                        && string.Equals(e.Asked.Detail, entry.Asked.Detail, StringComparison.Ordinal)
                        && string.Equals(e.Result, entry.Result, StringComparison.Ordinal))
                        return i;
                }
                else if (string.Equals(ResultCode(e.Result), ResultCode(entry.Result), StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>An answer's error code: <c>error</c> or <c>err</c> and
        /// the word after it.</summary>
        private static string ResultCode(string result)
        {
            if (string.IsNullOrEmpty(result)) return string.Empty;
            string[] words = result.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return words.Length >= 2 ? words[0] + " " + words[1] : result;
        }

        private int CountOf(string key)
        {
            int n = 0;
            foreach (var e in _entries) if (string.Equals(KeyOf(e), key, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>The program's oldest entry that records nothing done, or
        /// with <paramref name="keepers"/> its oldest of any kind. -1 when it
        /// has none.</summary>
        private int OldestOf(string key, bool keepers)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!keepers && e.Keeps) continue;
                if (string.Equals(KeyOf(e), key, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private string LargestHolder()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string largest = null;
            int most = 0;
            foreach (var e in _entries)
            {
                string key = KeyOf(e);
                counts.TryGetValue(key, out int n);
                counts[key] = ++n;
                if (n > most) { most = n; largest = key; }
            }
            return largest;
        }

        /// <summary>Empties the record.</summary>
        public void Clear()
        {
            lock (_sync) _entries.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The calls held now, oldest first, as a list of the
        /// caller's own.</summary>
        public IReadOnlyList<AutomationActivityEntry> Snapshot()
        {
            lock (_sync) return _entries.ToArray();
        }
    }
}
