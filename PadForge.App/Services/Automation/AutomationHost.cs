using System;
using System.Threading;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Automation's host (#597): the one object that stands between outside
    /// programs and PadForge. It lives as long as the app, is made once the
    /// settings are loaded, and is started and stopped by the Automation
    /// switch. It does not depend on the engine: a line that starts the
    /// engine has to arrive while the engine is stopped.
    ///
    /// <para>What it holds is everything a part of Automation needs to find
    /// in one place:</para>
    /// <list type="bullet">
    /// <item>The switch and the level in force (<see cref="IsOn"/>,
    /// <see cref="Level"/>, <see cref="Allows"/>, <see cref="Apply"/>,
    /// <see cref="Changed"/>, <see cref="TurnedOff"/>).</item>
    /// <item>The control pipe, <c>PadForge.Control</c>, and where it stands
    /// (<see cref="ControlPipe"/>, <see cref="IsServing"/>).</item>
    /// <item>The command set that answers the pipe's lines
    /// (<see cref="Commands"/>).</item>
    /// <item>The record of what programs asked (<see cref="Activity"/>).</item>
    /// <item>The questions that wait for the user (<see cref="Approvals"/>).</item>
    /// <item>The programs that hold a connection open (<see cref="Clients"/>).</item>
    /// <item>The problem recorder's four ways in (<see cref="RecordStart"/>,
    /// <see cref="RecordStop"/>, <see cref="RecordMark"/>,
    /// <see cref="Recording"/>), empty until a build's recorder fills
    /// them.</item>
    /// </list>
    ///
    /// <para><see cref="Apply"/> and <see cref="Dispose"/> are called on the
    /// interface's thread. Everything else can be read from any thread.</para>
    /// </summary>
    internal sealed class AutomationHost : IDisposable
    {
        /// <summary>How long the host waits before it asks again for a pipe
        /// name another program held.</summary>
        internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

        private readonly ExternalControlService _control;
        private readonly Func<int, AutomationProgram> _programOf;
        private readonly Func<DateTimeOffset> _now;
        private readonly Timer _retry;

        // Held across every start and stop of the pipe, which the interface's
        // thread and the retry timer both make.
        private readonly object _gate = new object();
        private volatile bool _isOn;
        private volatile int _level;
        private int _disposed;

        /// <param name="target">What the command set acts on.</param>
        /// <param name="ui">The interface's thread.</param>
        /// <param name="controlPipeName">The control pipe's name. The app
        /// gives none and serves <c>PadForge.Control</c>. A test gives a name
        /// of its own, since a pipe name is one for the whole PC.</param>
        /// <param name="programOf">Names the program behind a process ID. A
        /// test gives its own.</param>
        /// <param name="now">The clock Activity is stamped by.</param>
        public AutomationHost(IAutomationTarget target, IAutomationUi ui, string controlPipeName = null,
            Func<int, AutomationProgram> programOf = null, Func<DateTimeOffset> now = null)
        {
            _programOf = programOf ?? AutomationProgram.FromProcessId;
            _now = now ?? (() => DateTimeOffset.Now);
            Commands = new AutomationCommands(target, this, ui);
            _control = new ExternalControlService(AnswerControlLine, controlPipeName);
            _control.StateChanged += OnControlPipeEnded;
            _retry = new Timer(_ => RetryControlPipe(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Whether Automation is on: the user's switch, which no
        /// program can turn.</summary>
        public bool IsOn => _isOn;

        /// <summary>The level in force. It says what a program may do while
        /// <see cref="IsOn"/> is true, and nothing while it is false.</summary>
        public AutomationLevel Level => (AutomationLevel)_level;

        /// <summary>Whether a program may do what needs
        /// <paramref name="needed"/> now: Automation is on and its level is
        /// that high. Every call a program makes asks this.</summary>
        public bool Allows(AutomationLevel needed) => _isOn && _level >= (int)needed;

        /// <summary>Raised when <see cref="IsOn"/>, <see cref="Level"/> or
        /// <see cref="ControlPipe"/> changed. It comes on the thread that
        /// made the change, which is a worker's when the pipe changed by
        /// itself. A listener that draws goes to its own thread.</summary>
        public event EventHandler Changed;

        /// <summary>Raised on the interface's thread when Automation goes
        /// from on to off, after the pipe has stopped. No program can release
        /// a profile it held any more, so the app releases it.</summary>
        public event EventHandler TurnedOff;

        /// <summary>Where the control pipe stands: stopped, serving, its
        /// name held by another program, or refused by Windows.</summary>
        public ExternalControlService.PipeState ControlPipe => _control.State;

        /// <summary>Whether the control pipe answers programs now.</summary>
        public bool IsServing => _control.State == ExternalControlService.PipeState.Serving;

        /// <summary>The command set. A copy that starts with switches runs
        /// its own lines through <see cref="AutomationCommands.RunOwn"/>.</summary>
        public AutomationCommands Commands { get; }

        /// <summary>What programs asked and what came of it, the last 500
        /// calls. Every line on the control pipe is in it.</summary>
        public AutomationActivity Activity { get; } = new AutomationActivity();

        /// <summary>The questions a program's call put to the user. Nothing
        /// in the command set asks one.</summary>
        public AutomationApprovals Approvals { get; } = new AutomationApprovals();

        /// <summary>The programs connected over the long-lived pipe.</summary>
        public AutomationClients Clients { get; } = new AutomationClients();

        /// <summary>Starts a problem recording and gives the file's name, or
        /// nothing when one cannot start now. Null in a build with no
        /// recorder, where <c>record start</c> answers
        /// <c>error not-available</c>. Called on the interface's thread, as
        /// the other three are.</summary>
        public Func<string> RecordStart { get; set; }

        /// <summary>Stops the recording and gives the file's name, or nothing
        /// when none runs. Null as <see cref="RecordStart"/> is.</summary>
        public Func<string> RecordStop { get; set; }

        /// <summary>Puts a marker with the text given, which can be empty,
        /// into the recording and gives the file's name, or nothing when none
        /// runs. Null as <see cref="RecordStart"/> is.</summary>
        public Func<string, string> RecordMark { get; set; }

        /// <summary>Whether a recording runs now, for the state line. Null
        /// in a build with no recorder, which the state line reads as
        /// no.</summary>
        public Func<bool> Recording { get; set; }

        /// <summary>
        /// Takes the Automation switch and its level as the settings hold
        /// them. On starts the control pipe and off stops it.
        /// </summary>
        public void Apply(bool on, AutomationLevel level)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            bool wasOn = _isOn;
            bool changed = wasOn != on || _level != (int)level;
            var before = _control.State;
            // The level first: a line that arrives as the pipe opens is
            // judged by the level that is in force from now on.
            _level = (int)level;
            _isOn = on;
            lock (_gate)
            {
                if (on) EnsureServing();
                else StopServing();
            }
            if (changed || before != _control.State) Changed?.Invoke(this, EventArgs.Empty);
            if (wasOn && !on) TurnedOff?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Under the gate. Takes the pipe's name when it is not
        /// held, and sets the timer when another program holds it.</summary>
        private void EnsureServing()
        {
            if (_control.State == ExternalControlService.PipeState.Serving) return;
            // A pipe that ended by itself still holds what its start made.
            _control.Stop();
            var state = _control.Start();
            bool tryAgain = state == ExternalControlService.PipeState.NameTaken
                || state == ExternalControlService.PipeState.Failed;
            _retry.Change(tryAgain ? RetryEvery : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Under the gate.</summary>
        private void StopServing()
        {
            _retry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _control.Stop();
        }

        /// <summary>
        /// Asks again for a pipe name that was not free. The host's own timer
        /// calls it, on a worker, <see cref="RetryEvery"/> after a start that
        /// did not get the name, for as long as Automation is on.
        /// </summary>
        internal void RetryControlPipe()
        {
            ExternalControlService.PipeState before, after;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_isOn) return;
                before = _control.State;
                EnsureServing();
                after = _control.State;
            }
            if (before != after) Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The pipe stopped serving with nobody having stopped it:
        /// its name could not be taken again. Called on a worker.</summary>
        private void OnControlPipeEnded()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (_isOn) _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                return; // the host went away in this moment
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>One line from the control pipe: answered by the command
        /// set and written into Activity.</summary>
        private ExternalControlService.Answer AnswerControlLine(ExternalControlService.Request request)
        {
            ExternalControlService.Answer answer;
            try
            {
                answer = Commands.Answer(request.Line, request.Stopping);
            }
            catch (OperationCanceledException) when (request.Stopping.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                answer = new ExternalControlService.Answer(AutomationCommands.Internal);
            }

            string result = answer.Line ?? AutomationCommands.Internal;
            bool succeeded = result.StartsWith(AutomationCommands.Ok, StringComparison.Ordinal);
            var asked = AutomationCommands.Describe(request.Line);
            // The state line is long and says nothing here that ok does not.
            if (succeeded && asked.Kind == AutomationAskKind.State) result = AutomationCommands.Ok;
            try
            {
                Activity.Add(new AutomationActivityEntry(_now(), _programOf(request.ClientProcessId), asked, result, succeeded));
            }
            catch
            {
                // A listener's failure is not the caller's: the answer goes out.
            }
            return answer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate)
            {
                _retry.Dispose();
                _control.StateChanged -= OnControlPipeEnded;
                _control.Dispose();
            }
        }
    }
}
