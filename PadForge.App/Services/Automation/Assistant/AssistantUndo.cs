using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// A program's changes, undone as one step (#597). Before a program's
    /// first change after a quiet spell of <see cref="QuietSpell"/>, the
    /// state is taken: the active profile's controllers, assignments,
    /// mappings and macros (InputService.SnapshotCurrentProfile), the list of
    /// profiles, and a copy of the app settings, which that snapshot does not
    /// hold. Every change of the program until the next quiet spell joins the
    /// same run, and Activity marks each one's entry with the run. Undo, from
    /// the program's own tool or from the Activity card, puts the state back
    /// (InputService.ApplyProfile and the app settings copy) and says what
    /// came back.
    ///
    /// <para>Only the newest run can be undone: putting an older one back
    /// would also take away what came after it. Called on the interface's
    /// thread.</para>
    /// </summary>
    internal sealed class AssistantUndo
    {
        /// <summary>How long a program goes without a change before its
        /// next change starts a new run.</summary>
        internal static readonly TimeSpan QuietSpell = TimeSpan.FromSeconds(60);

        /// <summary>How many runs are kept. An older one can no longer be
        /// undone anyway.</summary>
        internal const int Kept = 8;

        private sealed class Run
        {
            public int Id;
            public int Program;
            public AssistantUndoState State;
            public DateTimeOffset Last;
            public bool Undone;
            public readonly List<string> Changes = new List<string>();
        }

        private readonly Func<AssistantUndoState> _take;
        private readonly Func<AssistantUndoState, string> _restore;
        private readonly Func<DateTimeOffset> _now;
        private readonly TimeSpan _quiet;
        private readonly List<Run> _runs = new List<Run>();
        private int _lastId;

        public AssistantUndo(Func<AssistantUndoState> take, Func<AssistantUndoState, string> restore,
            Func<DateTimeOffset> now = null, TimeSpan? quiet = null)
        {
            _take = take ?? throw new ArgumentNullException(nameof(take));
            _restore = restore ?? throw new ArgumentNullException(nameof(restore));
            _now = now ?? (() => DateTimeOffset.Now);
            _quiet = quiet ?? QuietSpell;
        }

        /// <summary>Raised after a run starts or is undone, so the Activity
        /// card asks again which entries offer Undo.</summary>
        public event EventHandler Changed;

        /// <summary>A program is about to make a change: the run it joins,
        /// taken fresh when its last change was a quiet spell ago or another
        /// program changed something since.</summary>
        public int BeginChange(int program, string tool, string detail)
        {
            var now = _now();
            var newest = _runs.Count > 0 ? _runs[^1] : null;
            Run run;
            if (newest != null && !newest.Undone && newest.Program == program && now - newest.Last < _quiet)
            {
                run = newest;
            }
            else
            {
                run = new Run { Id = ++_lastId, Program = program, State = _take() };
                _runs.Add(run);
                while (_runs.Count > Kept) _runs.RemoveAt(0);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            run.Last = now;
            string what = AssistantToolWords.Asked(tool, detail);
            if (!run.Changes.Contains(what)) run.Changes.Add(what);
            return run.Id;
        }

        /// <summary>Whether the run an Activity entry joined can be undone
        /// now: it is the newest and not undone yet.</summary>
        public bool CanUndo(AutomationActivityEntry entry)
        {
            if (entry == null || entry.Spell == 0 || _runs.Count == 0) return false;
            var newest = _runs[^1];
            return newest.Id == entry.Spell && !newest.Undone;
        }

        /// <summary>Undoes the run an Activity entry joined, from the
        /// Activity card.</summary>
        public string Undo(AutomationActivityEntry entry)
        {
            if (!CanUndo(entry)) throw new AssistantRefusal("nothing-to-undo", "That run of changes can no longer be undone.");
            return UndoRun(_runs[^1]);
        }

        /// <summary>Undoes a program's newest run, from its own tool.</summary>
        public string UndoNewestOf(int program)
        {
            var newest = _runs.Count > 0 ? _runs[^1] : null;
            if (newest == null || newest.Undone || newest.Program != program)
            {
                bool own = _runs.Any(r => r.Program == program && !r.Undone);
                throw new AssistantRefusal("nothing-to-undo", own
                    ? "Another program changed PadForge after this connection did, so undoing this connection's changes would take that away too. Nothing was undone."
                    : "This connection has changed nothing that can be undone.");
            }
            return UndoRun(newest);
        }

        private string UndoRun(Run run)
        {
            string back = _restore(run.State);
            run.Undone = true;
            Changed?.Invoke(this, EventArgs.Empty);
            string changes = string.Join(", ", run.Changes.Take(6));
            return back + (changes.Length > 0 ? " Undone: " + changes + "." : string.Empty);
        }
    }
}
