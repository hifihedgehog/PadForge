using System;
using System.Threading;
using PadForge.Engine.Common;

namespace PadForge.Services.Automation
{
    /// <summary>What the problem recorder did with a <c>record</c> line.</summary>
    internal enum AutomationRecordOutcome
    {
        /// <summary>Done. The answer is <c>ok</c> and the file's name.</summary>
        Done,
        /// <summary>The recorder is taken up: a recording runs already, or
        /// the last file is still being finished.</summary>
        Busy,
        /// <summary>The recorder cannot do it now: nothing records to stop or
        /// mark, its check found a private value it would let through, or the
        /// file could not be made.</summary>
        NotAvailable,
    }

    /// <summary>The problem recorder's answer to a <c>record</c> line, and
    /// the file it concerns: a name with no folder, or empty.</summary>
    internal readonly record struct AutomationRecordAnswer(AutomationRecordOutcome Outcome, string File)
    {
        /// <summary>Done, for the file named, or for none when
        /// <paramref name="file"/> is empty.</summary>
        internal static AutomationRecordAnswer Done(string file) => new(AutomationRecordOutcome.Done, file ?? string.Empty);

        /// <summary>Not done: the recorder is taken up.</summary>
        internal static AutomationRecordAnswer Busy => new(AutomationRecordOutcome.Busy, string.Empty);

        /// <summary>Not done: the recorder cannot do it now.</summary>
        internal static AutomationRecordAnswer NotAvailable => new(AutomationRecordOutcome.NotAvailable, string.Empty);
    }

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
    /// <item>The Automation pipe, <c>PadForge.Automation</c>, its sessions
    /// and where it stands (<see cref="Sessions"/>,
    /// <see cref="AutomationPipeState"/>), served while Automation or
    /// Plugins is on.</item>
    /// <item>The ways a later part reaches in: the assistant's connection
    /// (<see cref="McpSession"/>), a plugin's token
    /// (<see cref="AcceptPluginToken"/>) and a script's tool call
    /// (<see cref="Call"/>), each empty until that part fills it, and the
    /// engine while it runs (<see cref="AttachEngine"/>).</item>
    /// <item>The command set that answers the pipe's lines
    /// (<see cref="Commands"/>).</item>
    /// <item>The record of what programs asked (<see cref="Activity"/>).</item>
    /// <item>The questions that wait for the user (<see cref="Approvals"/>).</item>
    /// <item>The programs that hold a connection open (<see cref="Clients"/>).</item>
    /// <item>The problem recorder's four ways in (<see cref="RecordStart"/>,
    /// <see cref="RecordStop"/>, <see cref="RecordMark"/>,
    /// <see cref="Recording"/>), empty until a build's recorder fills
    /// them.</item>
    /// <item>What the Automation page calls for its Undo button
    /// (<see cref="CanUndo"/>, <see cref="Undo"/>), empty until a build's
    /// part fills them, and the button absent while they are.</item>
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
        private readonly AutomationPipe _pipe;
        private readonly Func<int, AutomationProgram> _programOf;
        private readonly Func<DateTimeOffset> _now;
        private readonly Timer _retry;

        // Held across every start and stop of the pipe, which the interface's
        // thread and the retry timer both make.
        private readonly object _gate = new object();
        private volatile bool _isOn;
        private volatile bool _pluginsOn;
        private volatile int _level;
        private int _disposed;

        /// <param name="target">What the command set acts on.</param>
        /// <param name="ui">The interface's thread.</param>
        /// <param name="controlPipeName">The control pipe's name. The app
        /// gives none and serves <c>PadForge.Control</c>. A test gives a name
        /// of its own, since a pipe name is one for the whole PC.</param>
        /// <param name="programOf">Names the program behind a process ID. A
        /// test gives its own.</param>
        /// <param name="now">The clock Activity and the questions are
        /// stamped by.</param>
        /// <param name="approvalWait">How long a question waits for the
        /// user. Two minutes when none is given. A test gives a short
        /// one.</param>
        /// <param name="automationPipeName">The Automation pipe's name. The
        /// app gives none and serves <c>PadForge.Automation</c>. A test gives
        /// one of its own.</param>
        public AutomationHost(IAutomationTarget target, IAutomationUi ui, string controlPipeName = null,
            Func<int, AutomationProgram> programOf = null, Func<DateTimeOffset> now = null, TimeSpan? approvalWait = null,
            string automationPipeName = null)
        {
            _programOf = programOf ?? AutomationProgram.FromProcessId;
            _now = now ?? (() => DateTimeOffset.Now);
            Approvals = new AutomationApprovals(approvalWait, _now);
            Commands = new AutomationCommands(target, this, ui);
            _control = new ExternalControlService(AnswerControlLine, controlPipeName);
            _control.StateChanged += OnControlPipeEnded;
            Sessions = new AutomationSessions(this, ui);
            _pipe = new AutomationPipe(Sessions.ServeAsync, automationPipeName);
            _pipe.StateChanged += OnControlPipeEnded;
            _retry = new Timer(_ => RetryControlPipe(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            // A copy of this exe started with switches is the control pipe's
            // most frequent client. Its signer is read now, on a worker, so
            // the first line it sends waits on no check of a large file.
            string self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
                _ = System.Threading.Tasks.Task.Run(() => AuthenticodeSigner.Of(self));
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

        /// <summary>Where the Automation pipe stands.</summary>
        public ExternalControlService.PipeState AutomationPipeState => _pipe.State;

        /// <summary>The Automation pipe itself, for its name and the token
        /// seam tests use.</summary>
        internal AutomationPipe AutomationPipe => _pipe;

        /// <summary>The control pipe itself, for the token seam tests
        /// use.</summary>
        internal ExternalControlService ControlService => _control;

        /// <summary>The programs on the Automation pipe and what they
        /// own.</summary>
        internal AutomationSessions Sessions { get; }

        /// <summary>Whether Plugins is on, which serves the Automation pipe
        /// to plugins while Automation is off.</summary>
        public bool PluginsOn => _pluginsOn;

        /// <summary>The assistant's connection: a session whose hello says
        /// <c>kind=mcp</c> is answered and then handed, stream and all, to
        /// this. Null until the assistant's part sets it, and such a hello
        /// is answered <c>err not-available</c>. Runs on a worker until the
        /// stream ends or the token is canceled.</summary>
        internal Func<AutomationPeer, System.IO.Stream, CancellationToken, System.Threading.Tasks.Task> McpSession { get; set; }

        /// <summary>A plugin's hello carries <c>token=</c>. This takes the
        /// token and the connection's process ID and gives the plugin's
        /// approved name, or null to refuse it with <c>err denied</c>. Null
        /// until the plugins' part sets it, and such a hello is answered
        /// <c>err not-available</c>.</summary>
        internal Func<string, int, AutomationPluginGrant> AcceptPluginToken { get; set; }

        /// <summary>A script's <c>call &lt;tool&gt; {json}</c>: runs the
        /// assistant tool of that name with the tool's own checks and gives
        /// its result. Null until the assistant's part sets it, and such a
        /// line is answered <c>err not-available</c>.</summary>
        internal Func<AutomationCall, CancellationToken, System.Threading.Tasks.Task<AutomationCallResult>> Call { get; set; }

        /// <summary>The engine's state, read on a worker for the programs
        /// that follow it. Set by the app.</summary>
        internal Func<AutomationEngineState> EngineState { get; set; }

        /// <summary>The active profile's name, or null for the default, read
        /// on the interface's thread. Set by the app.</summary>
        internal Func<string> ProfileName { get; set; }

        /// <summary>The time Activity is stamped with.</summary>
        internal DateTimeOffset Now() => _now();

        /// <summary>The engine started: the devices programs added go on its
        /// list, and <c>outputs</c> reads it. Called by the input
        /// service.</summary>
        internal void AttachEngine(IAutomationEngine engine)
        {
            if (Volatile.Read(ref _disposed) != 0 || engine == null) return;
            Sessions.AttachEngine(engine);
            EngineChanged?.Invoke(true);
        }

        /// <summary>The engine stopped.</summary>
        internal void DetachEngine()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Sessions.DetachEngine();
            EngineChanged?.Invoke(false);
        }

        /// <summary>Raised after the engine is attached (true) or detached
        /// (false), on the thread that started or stopped it. The plugins
        /// start and stop with it.</summary>
        internal event Action<bool> EngineChanged;

        /// <summary>Whether an engine is attached now.</summary>
        internal bool EngineAttached => Sessions.Devices.Engine != null;

        /// <summary>Plugins went on or off. While it is on, the Automation
        /// pipe serves plugins with Automation off. Called by the plugins'
        /// part on the interface's thread.</summary>
        internal void ApplyPlugins(bool on)
        {
            if (Volatile.Read(ref _disposed) != 0 || _pluginsOn == on) return;
            var before = _pipe.State;
            _pluginsOn = on;
            lock (_gate)
            {
                if (_isOn || on) EnsureServing();
                else StopServing();
            }
            if (!on && !_isOn) Sessions.CloseAll();
            if (before != _pipe.State) Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The command set. A copy that starts with switches runs
        /// its own lines through <see cref="AutomationCommands.RunOwn"/>.</summary>
        public AutomationCommands Commands { get; }

        /// <summary>What programs asked and what came of it, the last 500
        /// calls. Every line on the control pipe is in it.</summary>
        public AutomationActivity Activity { get; } = new AutomationActivity();

        /// <summary>The questions a program's call put to the user. Nothing
        /// in the command set asks one.</summary>
        public AutomationApprovals Approvals { get; }

        /// <summary>The programs connected over the long-lived pipe.</summary>
        public AutomationClients Clients { get; } = new AutomationClients();

        /// <summary>Starts a problem recording and gives the name of the file
        /// it becomes, or why it cannot start now. Null in a build with no
        /// recorder, where <c>record start</c> answers
        /// <c>error not-available</c>. Called on the interface's thread, as
        /// the other three are.</summary>
        public Func<AutomationRecordAnswer> RecordStart { get; set; }

        /// <summary>Stops the recording and gives its file's name, or why it
        /// cannot. Null as <see cref="RecordStart"/> is.</summary>
        public Func<AutomationRecordAnswer> RecordStop { get; set; }

        /// <summary>Puts a marker with the text given, which can be empty,
        /// into what the recorder captures, and gives the recording's file
        /// name, an empty name for a marker held by Keep the Last 60 Seconds
        /// alone, or why it cannot. Null as <see cref="RecordStart"/>
        /// is.</summary>
        public Func<string, AutomationRecordAnswer> RecordMark { get; set; }

        /// <summary>Whether a recording runs now, for the state line. Null
        /// in a build with no recorder, which the state line reads as
        /// no.</summary>
        public Func<bool> Recording { get; set; }

        private Func<AutomationActivityEntry, bool> _canUndo;
        private Action<AutomationActivityEntry> _undo;

        /// <summary>Raised on the thread that set one of
        /// <see cref="CanUndo"/> and <see cref="Undo"/>, so the Automation
        /// page shows or hides the buttons that call it.</summary>
        public event EventHandler ProvidedChanged;

        /// <summary>Whether the change an Activity entry records can be
        /// undone now. Null in a build where nothing can be undone, and then
        /// no entry offers Undo. Called on the interface's thread.</summary>
        public Func<AutomationActivityEntry, bool> CanUndo
        {
            get => _canUndo;
            set { _canUndo = value; ProvidedChanged?.Invoke(this, EventArgs.Empty); }
        }

        /// <summary>Undoes the change an Activity entry records, as one step.
        /// Null as <see cref="CanUndo"/> is. Called on the interface's
        /// thread, for an entry <see cref="CanUndo"/> said yes to.</summary>
        public Action<AutomationActivityEntry> Undo
        {
            get => _undo;
            set { _undo = value; ProvidedChanged?.Invoke(this, EventArgs.Empty); }
        }

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
            var beforeLong = _pipe.State;
            // The level first: a line that arrives as the pipe opens is
            // judged by the level that is in force from now on.
            _level = (int)level;
            _isOn = on;
            lock (_gate)
            {
                if (on || _pluginsOn) EnsureServing();
                else StopServing();
            }
            // The sessions hear the new level, and off closes the ones it no
            // longer serves once that is written.
            if (changed) Sessions.OnLevel(on, level, _pluginsOn);
            if (changed || before != _control.State || beforeLong != _pipe.State) Changed?.Invoke(this, EventArgs.Empty);
            if (wasOn && !on) TurnedOff?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Under the gate. Takes the pipe's name when it is not
        /// held, and sets the timer when another program holds it.</summary>
        private void EnsureServing()
        {
            // The control pipe serves while Automation is on, the Automation
            // pipe while Automation or Plugins is.
            bool tryAgain = false;
            if (_isOn)
            {
                if (_control.State != ExternalControlService.PipeState.Serving)
                {
                    // A pipe that ended by itself still holds what its start made.
                    _control.Stop();
                    var state = _control.Start();
                    tryAgain |= state is ExternalControlService.PipeState.NameTaken or ExternalControlService.PipeState.Failed;
                }
            }
            else _control.Stop();
            if (_pipe.State != ExternalControlService.PipeState.Serving)
            {
                _pipe.Stop();
                var state = _pipe.Start();
                tryAgain |= state is ExternalControlService.PipeState.NameTaken or ExternalControlService.PipeState.Failed;
            }
            _retry.Change(tryAgain ? RetryEvery : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Under the gate.</summary>
        private void StopServing()
        {
            _retry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _control.Stop();
            _pipe.Stop();
        }

        /// <summary>
        /// Asks again for a pipe name that was not free. The host's own timer
        /// calls it, on a worker, <see cref="RetryEvery"/> after a start that
        /// did not get the name, for as long as Automation is on.
        /// </summary>
        internal void RetryControlPipe()
        {
            ExternalControlService.PipeState before, after, beforeLong, afterLong;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !(_isOn || _pluginsOn)) return;
                before = _control.State;
                beforeLong = _pipe.State;
                EnsureServing();
                after = _control.State;
                afterLong = _pipe.State;
            }
            if (before != after || beforeLong != afterLong) Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The pipe stopped serving with nobody having stopped it:
        /// its name could not be taken again. Called on a worker.</summary>
        private void OnControlPipeEnded()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (_isOn || _pluginsOn) _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                return; // the host went away in this moment
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A close asked over the signal window (#600 item 44),
        /// written into Activity as the pipe's exit is. A posted message
        /// names no sender, so its program is unknown.</summary>
        internal void NoteExitBySignal()
        {
            try
            {
                Activity.Add(new AutomationActivityEntry(_now(), AutomationProgram.Unknown,
                    new AutomationAsk(AutomationAskKind.Exit), AutomationCommands.Ok, true));
            }
            catch
            {
                // A listener's failure does not hold up the close.
            }
        }

        /// <summary>One line from the control pipe: answered by the command
        /// set and written into Activity.</summary>
        private ExternalControlService.Answer AnswerControlLine(ExternalControlService.Request request)
        {
            ExternalControlService.Answer answer;
            try
            {
                // A client the token checks refused runs nothing, and the
                // line is written into Activity as refused.
                answer = request.Refusal != null
                    ? new ExternalControlService.Answer(ExternalControlService.Refused)
                    : Commands.Answer(request.Line, request.Stopping, _programOf(request.ClientProcessId));
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
                _pipe.StateChanged -= OnControlPipeEnded;
                _pipe.Dispose();
            }
            Sessions.Dispose();
        }
    }
}
