using System;
using System.Collections.Generic;
using System.IO;
using PadForge.Resources.Strings;

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
    internal sealed record AutomationProgram(string Name, int ProcessId, string ImageName)
    {
        /// <summary>A program nothing is known of.</summary>
        internal static readonly AutomationProgram Unknown = new AutomationProgram(null, 0, null);

        /// <summary>The program a process ID names now. A one-line command
        /// names no program of its own, so its image stands in for a
        /// name.</summary>
        internal static AutomationProgram FromProcessId(int processId)
        {
            if (processId <= 0) return Unknown;
            string image = null;
            try
            {
                string path = PadForge.Common.Input.ProcessImage.PathOf(unchecked((uint)processId));
                if (!string.IsNullOrEmpty(path)) image = Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                // A path Windows gave that is no path. The ID still shows.
            }
            return new AutomationProgram(
                image == null ? null : PadForge.Common.Input.RunningProcessList.ShortName(image), processId, image);
        }
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
    }

    /// <summary>
    /// What a call asked, kept as its kind and the one thing it named, and put
    /// into words when it is read, in the language in use then.
    /// </summary>
    /// <param name="Kind">The ask.</param>
    /// <param name="Argument">The profile, the controller's number or the
    /// line itself, for the kinds that name one.</param>
    internal readonly record struct AutomationAsk(AutomationAskKind Kind, string Argument = null)
    {
        /// <summary>The longest stretch of a program's own text a sentence
        /// carries.</summary>
        internal const int LongestArgument = 80;

        /// <summary>The ask in plain words.</summary>
        public string Text
        {
            get
            {
                var s = Strings.Instance;
                string argument = Argument ?? string.Empty;
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
        DateTimeOffset Time, AutomationProgram Program, AutomationAsk Asked, string Result, bool Succeeded);

    /// <summary>
    /// The record of what outside programs asked of PadForge (#597): the last
    /// <see cref="Capacity"/> calls, oldest first. Kept in memory for the
    /// session and never written to disk. Any thread may add to it and read
    /// it.
    /// </summary>
    internal sealed class AutomationActivity
    {
        /// <summary>How many calls are kept. An older one gives way to a
        /// newer.</summary>
        internal const int Capacity = 500;

        private readonly object _sync = new object();
        private readonly Queue<AutomationActivityEntry> _entries = new Queue<AutomationActivityEntry>(Capacity);

        /// <summary>Raised after a call is added and after
        /// <see cref="Clear"/>, on the thread that did it. A listener that
        /// draws goes to its own thread.</summary>
        public event EventHandler Changed;

        /// <summary>How many calls are held.</summary>
        public int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        /// <summary>Adds a call, and drops the oldest when the record is
        /// full.</summary>
        public void Add(AutomationActivityEntry entry)
        {
            if (entry == null) return;
            lock (_sync)
            {
                while (_entries.Count >= Capacity) _entries.Dequeue();
                _entries.Enqueue(entry);
            }
            Changed?.Invoke(this, EventArgs.Empty);
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
