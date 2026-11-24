using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The command set (#597): one executor for a line, whoever sends it. The
    /// control pipe hands it the lines of outside programs, and a copy of
    /// PadForge that starts with switches hands it its own.
    ///
    /// <para>A line is a verb and its arguments. Verbs and answers are fixed
    /// ASCII, never localized: this is a machine interface. An answer is
    /// <c>ok</c> with what was done, or <c>error</c> and one word.</para>
    ///
    /// <list type="table">
    /// <item><term><c>activate &lt;name or id&gt;</c></term><description><c>ok &lt;name&gt;</c>, <c>error unknown-profile</c></description></item>
    /// <item><term><c>deactivate</c></term><description><c>ok default</c></description></item>
    /// <item><term><c>query</c></term><description><c>ok &lt;name or default&gt; pinned</c> or <c>unpinned</c></description></item>
    /// <item><term><c>show</c>, <c>minimize</c>, <c>exit</c></term><description><c>ok</c></description></item>
    /// <item><term><c>hide</c></term><description><c>ok</c>, or <c>error not-available</c> with no tray icon to come back through</description></item>
    /// <item><term><c>engine on</c>, <c>engine off</c></term><description><c>ok on</c>, <c>ok off</c>, <c>error busy</c> while a stop is under way, <c>error internal</c> when the engine did not come up</description></item>
    /// <item><term><c>controllers on</c>, <c>controllers off</c></term><description><c>ok on</c>, <c>ok off</c></description></item>
    /// <item><term><c>slot &lt;1-16&gt; on</c>, <c>off</c></term><description><c>ok &lt;n&gt; on</c>, <c>error unknown-slot</c></description></item>
    /// <item><term><c>snapshot</c></term><description><c>ok &lt;file name&gt;</c></description></item>
    /// <item><term><c>record start</c>, <c>stop</c>, <c>mark [text]</c></term><description><c>ok &lt;file name&gt;</c>, <c>error not-available</c>, <c>error busy</c></description></item>
    /// <item><term><c>version</c></term><description><c>ok &lt;version&gt; r&lt;build&gt; &lt;commit&gt;</c></description></item>
    /// <item><term><c>state</c></term><description><c>ok</c> and one line of JSON</description></item>
    /// </list>
    ///
    /// <para>Every one of them is allowed at the lowest level, Commands, and
    /// refused with <c>error not-allowed</c> while Automation is off. A line
    /// PadForge cannot read is answered before that question is asked:
    /// <c>error empty</c>, <c>error unknown-command</c>,
    /// <c>error bad-argument</c>.</para>
    ///
    /// <para>What a line does runs on the interface's thread, the recorder's
    /// part included. <c>version</c> touches nothing and is answered where
    /// it arrives.</para>
    /// </summary>
    internal sealed class AutomationCommands
    {
        internal const string Ok = "ok";
        internal const string Empty = "error empty";
        internal const string UnknownCommand = "error unknown-command";
        internal const string Internal = "error internal";
        internal const string BadArgument = "error bad-argument";
        internal const string Busy = "error busy";
        internal const string NotAllowed = "error not-allowed";
        internal const string NotAvailable = "error not-available";
        internal const string UnknownSlot = "error unknown-slot";

        /// <summary>The level every line of the command set needs.</summary>
        internal const AutomationLevel Needs = AutomationLevel.Commands;

        private readonly IAutomationTarget _target;
        private readonly AutomationHost _host;
        private readonly IAutomationUi _ui;

        public AutomationCommands(IAutomationTarget target, AutomationHost host, IAutomationUi ui)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <summary>A line read into what it asks.</summary>
        private readonly struct Command
        {
            public Command(AutomationAskKind kind, string argument = null, bool on = false, int number = 0, string error = null)
            {
                Kind = kind;
                Argument = argument;
                On = on;
                Number = number;
                Error = error;
            }

            public AutomationAskKind Kind { get; }
            public string Argument { get; }
            public bool On { get; }
            public int Number { get; }

            /// <summary>The answer to a line that cannot be read, or
            /// null.</summary>
            public string Error { get; }
        }

        /// <summary>
        /// Answers a line an outside program sent. Called on a worker thread.
        /// A line that reads is run only while Automation allows it.
        /// </summary>
        public ExternalControlService.Answer Answer(string line, CancellationToken stopping)
        {
            var command = Read(line);
            if (command.Error != null) return new ExternalControlService.Answer(command.Error);
            if (!_host.Allows(Needs)) return new ExternalControlService.Answer(NotAllowed);
            return Run(command, stopping);
        }

        /// <summary>
        /// Runs a line of this copy's own start and gives the answer. It is
        /// the user's own command line, so Automation is not asked: a start
        /// applies its switches whether Automation is on or off, as
        /// <c>--profile</c> always has. Called on the interface's thread.
        /// </summary>
        public string RunOwn(string line)
        {
            var command = Read(line);
            if (command.Error != null) return command.Error;
            try
            {
                var answer = Run(command, CancellationToken.None);
                answer.AfterWritten?.Invoke();
                return answer.Line;
            }
            catch
            {
                return Internal;
            }
        }

        /// <summary>What a line asks, for Activity. A line that cannot be
        /// read is its own description.</summary>
        public static AutomationAsk Describe(string line)
        {
            var command = Read(line);
            return command.Error != null
                ? new AutomationAsk(AutomationAskKind.Unknown, (line ?? string.Empty).Trim())
                : new AutomationAsk(command.Kind, command.Argument);
        }

        private static Command Read(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return new Command(AutomationAskKind.Unknown, error: Empty);

            // The verb and the rest. A profile's name can hold spaces, so
            // only the first word is the verb.
            line = line.Trim();
            int space = line.IndexOf(' ');
            string verb = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

            switch (verb)
            {
                case "activate":
                    return rest.Length == 0
                        ? new Command(AutomationAskKind.Unknown, error: Empty)
                        : new Command(AutomationAskKind.ActivateProfile, rest);

                // The two oldest verbs take no argument and never minded one.
                case "deactivate": return new Command(AutomationAskKind.DeactivateProfile);
                case "query": return new Command(AutomationAskKind.QueryProfile);

                case "show": return Alone(AutomationAskKind.ShowWindow, rest);
                case "hide": return Alone(AutomationAskKind.HideWindow, rest);
                case "minimize": return Alone(AutomationAskKind.MinimizeWindow, rest);
                case "exit": return Alone(AutomationAskKind.Exit, rest);
                case "snapshot": return Alone(AutomationAskKind.Snapshot, rest);
                case "version": return Alone(AutomationAskKind.Version, rest);
                case "state": return Alone(AutomationAskKind.State, rest);

                case "engine":
                    return TryOnOff(rest, out bool engineOn)
                        ? new Command(engineOn ? AutomationAskKind.EngineOn : AutomationAskKind.EngineOff, on: engineOn)
                        : new Command(AutomationAskKind.Unknown, error: BadArgument);

                case "controllers":
                    return TryOnOff(rest, out bool allOn)
                        ? new Command(allOn ? AutomationAskKind.ControllersOn : AutomationAskKind.ControllersOff, on: allOn)
                        : new Command(AutomationAskKind.Unknown, error: BadArgument);

                case "slot":
                {
                    string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length != 2
                        || !int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                        || number < 1 || number > 16
                        || !TryOnOff(words[1], out bool slotOn))
                        return new Command(AutomationAskKind.Unknown, error: BadArgument);
                    return new Command(slotOn ? AutomationAskKind.ControllerOn : AutomationAskKind.ControllerOff,
                        number.ToString(CultureInfo.InvariantCulture), slotOn, number);
                }

                case "record":
                {
                    int cut = rest.IndexOf(' ');
                    string what = (cut < 0 ? rest : rest.Substring(0, cut)).ToLowerInvariant();
                    string text = cut < 0 ? string.Empty : rest.Substring(cut + 1).Trim();
                    switch (what)
                    {
                        case "start": return Alone(AutomationAskKind.RecordStart, text);
                        case "stop": return Alone(AutomationAskKind.RecordStop, text);
                        case "mark": return new Command(AutomationAskKind.RecordMark, text);
                        default: return new Command(AutomationAskKind.Unknown, error: BadArgument);
                    }
                }

                default:
                    return new Command(AutomationAskKind.Unknown, error: UnknownCommand);
            }
        }

        /// <summary>A verb that takes nothing after it.</summary>
        private static Command Alone(AutomationAskKind kind, string rest)
            => rest.Length == 0 ? new Command(kind) : new Command(AutomationAskKind.Unknown, error: BadArgument);

        private static bool TryOnOff(string word, out bool on)
        {
            on = string.Equals(word, "on", StringComparison.OrdinalIgnoreCase);
            return on || string.Equals(word, "off", StringComparison.OrdinalIgnoreCase);
        }

        private static string OnOff(bool on) => on ? "on" : "off";

        private ExternalControlService.Answer Run(Command command, CancellationToken stopping)
        {
            switch (command.Kind)
            {
                case AutomationAskKind.ActivateProfile:
                    return OnUi(() => _target.ActivateProfile(command.Argument), stopping);
                case AutomationAskKind.DeactivateProfile:
                    return OnUi(_target.DeactivateProfile, stopping);
                case AutomationAskKind.QueryProfile:
                    return OnUi(_target.QueryProfile, stopping);

                case AutomationAskKind.ShowWindow:
                    return OnUi(() => { _target.ShowWindow(); return Ok; }, stopping);
                case AutomationAskKind.HideWindow:
                    return OnUi(() => _target.HideToTray() ? Ok : NotAvailable, stopping);
                case AutomationAskKind.MinimizeWindow:
                    return OnUi(() => { _target.MinimizeWindow(); return Ok; }, stopping);

                case AutomationAskKind.Exit:
                    // Answered first. PadForge closes once the answer is in
                    // the pipe, or the program that asked would read a
                    // broken pipe in place of its ok.
                    return new ExternalControlService.Answer(Ok, () => _ui.Post(_target.Exit));

                case AutomationAskKind.EngineOn:
                case AutomationAskKind.EngineOff:
                    return OnUi(() => _target.SetEngine(command.On) switch
                    {
                        EngineChange.Done => Ok + " " + OnOff(command.On),
                        EngineChange.Busy => Busy,
                        _ => Internal,
                    }, stopping);

                case AutomationAskKind.ControllersOn:
                case AutomationAskKind.ControllersOff:
                    return OnUi(() => { _target.SetAllControllers(command.On); return Ok + " " + OnOff(command.On); }, stopping);

                case AutomationAskKind.ControllerOn:
                case AutomationAskKind.ControllerOff:
                    return OnUi(() => _target.SetController(command.Number, command.On)
                        ? Ok + " " + command.Argument + " " + OnOff(command.On)
                        : UnknownSlot, stopping);

                case AutomationAskKind.Snapshot:
                    return OnUi(() => Ok + " " + _target.SaveSnapshot(), stopping);

                case AutomationAskKind.RecordStart:
                    return OnUi(() => Recorder(_host.RecordStart), stopping);
                case AutomationAskKind.RecordStop:
                    return OnUi(() => Recorder(_host.RecordStop), stopping);
                case AutomationAskKind.RecordMark:
                    return OnUi(() =>
                    {
                        var mark = _host.RecordMark;
                        return Recorder(mark == null ? null : () => mark(command.Argument ?? string.Empty));
                    }, stopping);

                case AutomationAskKind.Version:
                    return new ExternalControlService.Answer(VersionLine());

                case AutomationAskKind.State:
                    return OnUi(() => Ok + " " + StateJson(_target.ReadState()), stopping);

                default:
                    return new ExternalControlService.Answer(UnknownCommand);
            }
        }

        private ExternalControlService.Answer OnUi(Func<string> work, CancellationToken stopping)
            => new ExternalControlService.Answer(_ui.Invoke(work, stopping));

        /// <summary>The problem recorder, which the host is handed when the
        /// build has one. With none the answer is
        /// <c>error not-available</c>. A recorder that names no file could
        /// not do it now: it records already, or it does not record.</summary>
        private static string Recorder(Func<string> call)
        {
            if (call == null) return NotAvailable;
            string file = call();
            return string.IsNullOrEmpty(file) ? Busy : Ok + " " + file;
        }

        /// <summary><c>ok &lt;version&gt; r&lt;build&gt; &lt;commit&gt;</c>. A build
        /// made without git has no commit, and says build 0 and a dash, so
        /// the answer always has its three parts.</summary>
        internal static string VersionLine()
            => Ok + " " + BuildIdentity.VersionText(BuildIdentity.Version)
                + " r" + BuildIdentity.BuildNumber.ToString(CultureInfo.InvariantCulture)
                + " " + (BuildIdentity.Commit.Length > 0 ? BuildIdentity.Commit : "-");

        /// <summary>
        /// The state as one line of JSON. The writer escapes every character
        /// outside ASCII, so the line reads the same whatever code page
        /// carries it, and a name can hold no line break.
        /// </summary>
        internal string StateJson(AutomationState state)
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("version", BuildIdentity.VersionText(BuildIdentity.Version));
                json.WriteNumber("build", BuildIdentity.BuildNumber);
                json.WriteString("commit", BuildIdentity.Commit);
                json.WriteString("engine", Word(state.Engine));

                json.WriteStartObject("profile");
                json.WriteString("name", state.ProfileName);
                json.WriteString("id", state.ProfileId);
                json.WriteBoolean("pinned", state.ProfilePinned);
                json.WriteEndObject();

                json.WriteString("window", Word(state.Window));

                json.WriteStartObject("automation");
                json.WriteBoolean("on", _host.IsOn);
                json.WriteString("level", AutomationLevels.Word(_host.Level));
                json.WriteEndObject();

                json.WriteBoolean("recording", _host.Recording?.Invoke() ?? false);

                json.WriteStartArray("controllers");
                foreach (var controller in state.Controllers)
                {
                    json.WriteStartObject();
                    json.WriteNumber("slot", controller.Slot);
                    json.WriteString("name", controller.Name);
                    json.WriteString("type", Word(controller.Type));
                    json.WriteBoolean("on", controller.On);
                    json.WriteNumber("devices", controller.Devices);
                    json.WriteNumber("online", controller.Online);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        private static string Word(AutomationEngineState engine) => engine switch
        {
            AutomationEngineState.Running => "running",
            AutomationEngineState.Idle => "idle",
            AutomationEngineState.Stopping => "stopping",
            _ => "stopped",
        };

        private static string Word(AutomationWindowState window) => window switch
        {
            AutomationWindowState.Minimized => "minimized",
            AutomationWindowState.Tray => "tray",
            _ => "shown",
        };

        /// <summary>A controller type's word. Fixed here, apart from the
        /// type's name in code, which a rename would change.</summary>
        internal static string Word(VirtualControllerType type) => type switch
        {
            VirtualControllerType.Xbox => "xbox",
            VirtualControllerType.PlayStation => "playstation",
            VirtualControllerType.Nintendo => "nintendo",
            VirtualControllerType.Extended => "extended",
            VirtualControllerType.KeyboardMouse => "keyboard-mouse",
            VirtualControllerType.Midi => "midi",
            VirtualControllerType.Vr => "vr",
            _ => "unknown",
        };
    }
}
