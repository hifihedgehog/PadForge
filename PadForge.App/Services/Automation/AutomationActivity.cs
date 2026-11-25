using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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

        /// <summary><c>maximize</c>, the line of <c>--maximized</c>
        /// (#600 item 44). Appended.</summary>
        MaximizeWindow,
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
                    AutomationAskKind.MaximizeWindow => s.Automation_Asked_Maximize,
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
        /// input sent or held, or a question the user answered. The page
        /// folds a run of a program's identical calls into one line only
        /// while none of them did anything.</summary>
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
    /// The record of what outside programs asked of PadForge (#597, #600),
    /// oldest first. Every call is kept for the session, up to
    /// <see cref="Capacity"/>. Past that the oldest go, and
    /// <see cref="Dropped"/> counts them. Kept in memory and never written
    /// to disk: it leaves memory only by Export Activity… or as a part of a
    /// problem recording (<see cref="ToText"/>). Any thread may add to it
    /// and read it.
    ///
    /// <para>Each call stays as it came. The Automation page shows a run of
    /// one program's identical calls that did nothing as one line that
    /// opens into them, so a busy program's reads take one line and every
    /// call stays on the record (DashboardViewModel.AutomationPage).</para>
    /// </summary>
    internal sealed class AutomationActivity
    {
        /// <summary>How many calls are kept.</summary>
        internal const int Capacity = 100_000;

        private readonly object _sync = new object();
        private readonly int _capacity;
        private AutomationActivityEntry[] _ring = new AutomationActivityEntry[64];
        private int _start, _count;
        private long _added, _dropped;
        private int _generation;

        /// <summary>Keeps <see cref="Capacity"/> calls.</summary>
        public AutomationActivity() : this(Capacity) { }

        /// <summary>Keeps <paramref name="capacity"/> calls, for a test that
        /// passes the limit without making 100,000.</summary>
        internal AutomationActivity(int capacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        /// <summary>Raised after a call is added and after
        /// <see cref="Clear"/>, on the thread that did it. A listener that
        /// draws goes to its own thread.</summary>
        public event EventHandler Changed;

        /// <summary>How many calls are held.</summary>
        public int Count
        {
            get { lock (_sync) return _count; }
        }

        /// <summary>How many of the oldest calls went once the record was
        /// full, since it was last cleared.</summary>
        public long Dropped
        {
            get { lock (_sync) return _dropped; }
        }

        /// <summary>The asks that only read and change nothing.</summary>
        internal static bool OnlyReads(AutomationAskKind kind) => kind is AutomationAskKind.Unknown
            or AutomationAskKind.QueryProfile or AutomationAskKind.Version or AutomationAskKind.State
            or AutomationAskKind.Connected or AutomationAskKind.Disconnected or AutomationAskKind.Devices
            or AutomationAskKind.Caps or AutomationAskKind.Read or AutomationAskKind.Outputs
            or AutomationAskKind.Subscribe or AutomationAskKind.Unsubscribe;

        /// <summary>Keeps a call. Once the record is full the oldest
        /// goes.</summary>
        public void Add(AutomationActivityEntry entry)
        {
            if (entry == null) return;
            lock (_sync)
            {
                if (_count == _capacity)
                {
                    _ring[_start] = null;
                    _start = (_start + 1) % _ring.Length;
                    _count--;
                    _dropped++;
                }
                else if (_count == _ring.Length)
                {
                    var grown = new AutomationActivityEntry[Math.Min(_capacity, _ring.Length * 2)];
                    for (int i = 0; i < _count; i++) grown[i] = _ring[(_start + i) % _ring.Length];
                    _ring = grown;
                    _start = 0;
                }
                _ring[(_start + _count) % _ring.Length] = entry;
                _count++;
                _added++;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Empties the record and its count of calls that
        /// went.</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _ring = new AutomationActivityEntry[64];
                _start = _count = 0;
                _added = _dropped = 0;
                _generation++;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The calls held now, oldest first, as a list of the
        /// caller's own.</summary>
        public IReadOnlyList<AutomationActivityEntry> Snapshot()
        {
            lock (_sync) return CopyLocked(0, _count);
        }

        private AutomationActivityEntry[] CopyLocked(int offset, int n)
        {
            var copy = new AutomationActivityEntry[n];
            for (int i = 0; i < n; i++) copy[i] = _ring[(_start + offset + i) % _ring.Length];
            return copy;
        }

        /// <summary>What came since a reader's last look, so a page pays for
        /// what is new and never walks the whole record.</summary>
        /// <param name="Generation">Changes when the record is cleared. A
        /// reader whose generation differs starts again from
        /// <paramref name="New"/>.</param>
        /// <param name="Added">The number of the newest call. Calls are
        /// numbered from 1 in the order they came.</param>
        /// <param name="FirstKept">The number of the oldest call still held.
        /// A reader lets go of every call it holds with a lower number.</param>
        /// <param name="New">The calls after the reader's last, oldest
        /// first.</param>
        internal readonly record struct Delta(int Generation, long Added, long FirstKept, IReadOnlyList<AutomationActivityEntry> New);

        /// <summary>The calls numbered after <paramref name="after"/> that
        /// the record still holds. A reader of another
        /// <paramref name="generation"/> gets every call held.</summary>
        internal Delta Since(int generation, long after)
        {
            lock (_sync)
            {
                long firstKept = _added - _count + 1;
                if (generation != _generation) after = 0;
                long from = Math.Max(after + 1, firstKept);
                int n = (int)Math.Max(0, _added - from + 1);
                return new Delta(_generation, _added, firstKept, CopyLocked((int)(from - firstKept), n));
            }
        }

        /// <summary>
        /// The record as plain text, oldest first, for Export Activity… and
        /// the problem recording: a head line with the count of calls and
        /// how many went, then one tab-separated line a call with its local
        /// time to the millisecond, the program, its image and signer, what
        /// it asked and what came of it in the interface's words, and
        /// PadForge's answer as it was sent.
        /// </summary>
        internal string ToText()
        {
            IReadOnlyList<AutomationActivityEntry> calls;
            long dropped;
            lock (_sync)
            {
                calls = CopyLocked(0, _count);
                dropped = _dropped;
            }
            var s = Strings.Instance;
            var b = new StringBuilder();
            b.Append(string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.Automation_Export_Head_Format), calls.Count),
                calls.Count, dropped)).Append("\r\n");
            foreach (var e in calls)
            {
                var who = e.Program ?? AutomationProgram.Unknown;
                string name = ShownText.Clean(!string.IsNullOrEmpty(who.Name) ? who.Name : who.ImageName);
                string image = ShownText.Clean(!string.IsNullOrEmpty(who.ImagePath) ? who.ImagePath : who.ImageName);
                string signer = ShownText.Clean(who.Signer);
                string result = e.Succeeded ? s.Automation_Result_Done : PadForge.ViewModels.AutomationActivityItem.RefusalWords(e.Result);
                b.Append(e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\t')
                 .Append(OneLine(name)).Append('\t')
                 .Append(OneLine(string.IsNullOrEmpty(signer) || string.IsNullOrEmpty(image) ? image : image + " (" + signer + ")")).Append('\t')
                 .Append(OneLine(e.Asked.Text)).Append('\t')
                 .Append(OneLine(result)).Append('\t')
                 .Append(OneLine(ShownText.Clean(e.Result))).Append("\r\n");
            }
            return b.ToString();
        }

        /// <summary>A field on one line: tabs and line breaks become
        /// spaces.</summary>
        private static string OneLine(string text)
            => string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
