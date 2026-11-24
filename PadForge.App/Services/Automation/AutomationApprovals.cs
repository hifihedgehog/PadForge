using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation
{
    /// <summary>How a question to the user ended.</summary>
    internal enum ApprovalAnswer
    {
        Allowed,
        Denied,

        /// <summary>Nobody answered in the time given. A change that waited
        /// for the answer is not made.</summary>
        TimedOut,

        /// <summary>The question was never put: its program has
        /// <see cref="AutomationApprovals.MostPerProgram"/> waiting already,
        /// or <see cref="AutomationApprovals.MostInAll"/> wait in all. A
        /// change that waited for the answer is not made.</summary>
        TooMany,
    }

    /// <summary>What the user is asked: who wants it, what it is in a few
    /// words, and the lines that say exactly what would change.</summary>
    /// <param name="Who">The program that asks.</param>
    /// <param name="Title">The change in a few words.</param>
    /// <param name="Detail">One line for each thing at stake, such as the
    /// program a macro would start.</param>
    internal sealed record ApprovalRequest(AutomationProgram Who, string Title, IReadOnlyList<string> Detail);

    /// <summary>A question that waits for the user.</summary>
    /// <param name="Id">What the interface answers it by.</param>
    /// <param name="Request">The question.</param>
    /// <param name="Asked">When it was put.</param>
    /// <param name="Expires">When it ends unanswered.</param>
    internal sealed record PendingApproval(int Id, ApprovalRequest Request, DateTimeOffset Asked, DateTimeOffset Expires);

    /// <summary>
    /// Asks the user before a change a program must not make by itself, and
    /// waits (#597). A call on a worker thread puts its question here and
    /// awaits the answer. The interface shows what waits and answers through
    /// <see cref="Answer"/>, which no outside program can reach. A question
    /// nobody answers in two minutes ends as <see cref="ApprovalAnswer.TimedOut"/>.
    ///
    /// <para>The precedent is the pairing question of Remote Link
    /// (InputService.ApprovePairing): the question goes to the user and the
    /// worker waits two minutes, and what the wait holds outlives a late
    /// answer. Here nothing is blocked while it waits, several questions
    /// can wait at once, and each has an ID.</para>
    /// </summary>
    internal sealed class AutomationApprovals
    {
        /// <summary>How long a question waits.</summary>
        internal static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);

        /// <summary>How many questions one program may have waiting, and
        /// how many wait at most in all. A program that asks in a loop
        /// cannot bury the user in cards, nor keep another program's
        /// question from being asked.</summary>
        internal const int MostPerProgram = 3, MostInAll = 10;

        private sealed class Waiting
        {
            public PendingApproval Pending;
            public TaskCompletionSource<ApprovalAnswer> Done;
            public CancellationTokenSource Clock;
            public CancellationTokenRegistration Canceled;
        }

        private readonly object _sync = new object();
        private readonly List<Waiting> _waiting = new List<Waiting>();
        private readonly TimeSpan _wait;
        private readonly Func<DateTimeOffset> _now;
        private int _lastId;

        /// <param name="wait">How long a question waits. Two minutes when
        /// none is given. A test gives a short one.</param>
        /// <param name="now">The clock the entries are stamped by.</param>
        public AutomationApprovals(TimeSpan? wait = null, Func<DateTimeOffset> now = null)
        {
            _wait = wait ?? DefaultWait;
            _now = now ?? (() => DateTimeOffset.Now);
        }

        /// <summary>How long a question waits for the user.</summary>
        public TimeSpan Wait => _wait;

        /// <summary>Raised when a question joins or leaves
        /// <see cref="Pending"/>, on the thread that made the change. A
        /// listener that draws goes to its own thread.</summary>
        public event EventHandler PendingChanged;

        /// <summary>The questions that wait, oldest first, as a list of the
        /// caller's own.</summary>
        public IReadOnlyList<PendingApproval> Pending
        {
            get
            {
                lock (_sync)
                {
                    var list = new PendingApproval[_waiting.Count];
                    for (int i = 0; i < list.Length; i++) list[i] = _waiting[i].Pending;
                    return list;
                }
            }
        }

        /// <summary>
        /// Puts a question and completes when the user answers it or its time
        /// runs out. Canceling <paramref name="cancel"/> takes the question
        /// away and ends the task as canceled. A question past the limits
        /// (<see cref="MostPerProgram"/>, <see cref="MostInAll"/>) is never
        /// put and ends at once as <see cref="ApprovalAnswer.TooMany"/>. A
        /// program is told apart by its image's path
        /// (<see cref="AutomationProgram.Key"/>).
        /// </summary>
        public Task<ApprovalAnswer> AskAsync(ApprovalRequest request, CancellationToken cancel = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (cancel.IsCancellationRequested) return Task.FromCanceled<ApprovalAnswer>(cancel);

            // Continuations run off the thread that answers, so the
            // interface's click never runs a program's waiting call.
            var waiting = new Waiting
            {
                Done = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously),
                Clock = new CancellationTokenSource(),
            };
            lock (_sync)
            {
                string key = (request.Who ?? AutomationProgram.Unknown).Key;
                int ofProgram = 0;
                foreach (var w in _waiting)
                    if (string.Equals((w.Pending.Request.Who ?? AutomationProgram.Unknown).Key, key, StringComparison.Ordinal)) ofProgram++;
                if (_waiting.Count >= MostInAll || ofProgram >= MostPerProgram)
                {
                    waiting.Clock.Dispose();
                    return Task.FromResult(ApprovalAnswer.TooMany);
                }
                var asked = _now();
                waiting.Pending = new PendingApproval(++_lastId, request, asked, asked + _wait);
                _waiting.Add(waiting);
            }
            // Read before the caller's token is watched: a token canceled in
            // this very moment takes the question away at once, clock and all.
            CancellationToken clock = waiting.Clock.Token;
            waiting.Canceled = cancel.Register(() =>
            {
                if (Take(waiting)) waiting.Done.TrySetCanceled(cancel);
            });
            _ = RunClock(waiting, clock);
            PendingChanged?.Invoke(this, EventArgs.Empty);
            return waiting.Done.Task;
        }

        private async Task RunClock(Waiting waiting, CancellationToken clock)
        {
            try
            {
                await Task.Delay(_wait, clock).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // answered or canceled first
            }
            if (Take(waiting)) waiting.Done.TrySetResult(ApprovalAnswer.TimedOut);
        }

        /// <summary>The user's answer, from the interface. False when no
        /// question of that ID waits any more: it was answered, it timed out,
        /// or its program went away.</summary>
        public bool Answer(int id, bool allowed)
        {
            Waiting waiting;
            lock (_sync) waiting = _waiting.Find(w => w.Pending.Id == id);
            if (waiting == null || !Take(waiting)) return false;
            waiting.Done.TrySetResult(allowed ? ApprovalAnswer.Allowed : ApprovalAnswer.Denied);
            return true;
        }

        /// <summary>Takes a question off the list. True for the one caller
        /// that took it, which then ends its task.</summary>
        private bool Take(Waiting waiting)
        {
            lock (_sync)
            {
                if (!_waiting.Remove(waiting)) return false;
            }
            try { waiting.Clock.Cancel(); } catch (ObjectDisposedException) { }
            waiting.Clock.Dispose();
            waiting.Canceled.Dispose();
            PendingChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
