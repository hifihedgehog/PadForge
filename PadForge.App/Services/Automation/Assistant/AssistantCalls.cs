using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The one path every tool call takes (#597), an assistant's through its
    /// session's server and a script's through its <c>call</c> line. In
    /// order: the tool is found, its level is asked of the host, the
    /// program's rate is checked, the arguments are read as the tool's type,
    /// the tool runs, what it returns is scrubbed of this PC's private
    /// values, and the call is written into Activity. A change a person must
    /// answer waits for the request card, up to <see cref="AnswerWait"/>, and
    /// then returns a pending result the program asks about again.
    /// </summary>
    internal sealed class AssistantCalls
    {
        /// <summary>How long a call waits for the user's answer before it
        /// returns a pending result. Below the minute a host gives a call.</summary>
        internal static readonly TimeSpan DefaultAnswerWait = TimeSpan.FromSeconds(45);

        /// <summary>Calls a program may make each second. As many may come at
        /// once after a quiet second.</summary>
        internal const int CallsPerSecond = 20;

        private readonly AutomationHost _host;
        private readonly IAutomationUi _ui;
        private readonly Dictionary<string, AssistantToolDefinition> _tools;
        private readonly object _sync = new object();
        private readonly Dictionary<int, AutomationLineRate> _rates = new();
        private readonly Dictionary<int, PendingCall> _pending = new();

        public AssistantCalls(AutomationHost host, IAssistantApp app, IAssistantSettings settings, IAutomationUi ui,
            AssistantUndo undo, IReadOnlyList<AssistantToolDefinition> tools, TimeSpan? answerWait = null, Func<long> nowMs = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            App = app;
            Settings = settings;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            Undo = undo;
            Tools = tools;
            _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
            AnswerWait = answerWait ?? DefaultAnswerWait;
            _nowMs = nowMs ?? (() => Environment.TickCount64);
        }

        private readonly Func<long> _nowMs;

        public AutomationHost Host => _host;
        public IAssistantApp App { get; }
        public IAssistantSettings Settings { get; }
        public AssistantUndo Undo { get; }
        public IReadOnlyList<AssistantToolDefinition> Tools { get; }
        public TimeSpan AnswerWait { get; }

        /// <summary>The tools the level in force allows, in the one
        /// order.</summary>
        public IEnumerable<AssistantToolDefinition> Allowed()
            => _host.IsOn ? Tools.Where(t => _host.Allows(t.Level)) : Enumerable.Empty<AssistantToolDefinition>();

        /// <summary>Runs one call to its outcome. Never throws for the
        /// program's mistakes: they come back as failures with words.</summary>
        public async Task<AssistantOutcome> CallAsync(AssistantCaller caller, string name, JsonElement arguments, CancellationToken stopping)
        {
            if (string.IsNullOrEmpty(name) || !_tools.TryGetValue(name, out var tool))
            {
                var unknown = AssistantOutcome.Failure("unknown-tool", "PadForge has no tool named " + (name ?? string.Empty) + ". List the tools again.", unknownTool: true);
                Record(caller, name, null, unknown, 0, readsOnly: true, asked: false);
                return unknown;
            }

            var invocation = new AssistantInvocation(this, caller, tool, _ui, stopping);
            AssistantOutcome outcome;
            if (!_host.Allows(tool.Level))
            {
                outcome = AssistantOutcome.Failure("not-allowed", _host.IsOn
                    ? "The Automation level the user picked does not allow " + name + ". It needs " + LevelWords(tool.Level)
                      + ". Only the user can raise the level, on PadForge's Automation page."
                    : "Automation is off in PadForge. Only the user can turn it on, on the Automation page under Extras.");
            }
            else if (!TakeRate(caller.Key))
            {
                outcome = AssistantOutcome.Failure("rate-limited", "Too many calls a second. Wait a moment, then call again.");
            }
            else
            {
                outcome = await RunAsync(invocation, arguments).ConfigureAwait(false);
            }
            // A call that waits for the user is written as waiting, and its
            // end as an entry of its own. Only a change that was made can be
            // undone from its entry.
            if (invocation.Pending)
                Record(caller, name, invocation.Detail, AssistantOutcome.Failure("waiting", "the user has not answered yet"), 0, tool.ReadOnly, asked: true);
            else
                Record(caller, name, invocation.Detail, outcome, outcome.Ok ? invocation.Spell : 0, tool.ReadOnly, invocation.Asked);
            return outcome;
        }

        private async Task<AssistantOutcome> RunAsync(AssistantInvocation invocation, JsonElement arguments)
        {
            var tool = invocation.Tool;
            object args = AssistantJson.Read(arguments, tool.Arguments, out string problem);
            if (args == null)
                return AssistantOutcome.Failure("bad-argument", problem + " The tool's input schema lists what it takes.");
            object result;
            try
            {
                result = await tool.Run(invocation, args, invocation.Stopping).ConfigureAwait(false);
            }
            catch (AssistantRefusal refusal)
            {
                return AssistantOutcome.Failure(refusal.Code, refusal.Words);
            }
            catch (OperationCanceledException) when (invocation.Stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("Automation: the tool " + tool.Name + " failed: " + error.GetType().Name + ": " + error.Message);
                return AssistantOutcome.Failure("internal", "PadForge ran into a problem doing this. Nothing more is known here. Tell the user, who can save a diagnostics snapshot.");
            }
            return Success(result, tool.Result);
        }

        /// <summary>A result as the program gets it, scrubbed: every string
        /// in it is searched for this PC's private values.</summary>
        internal AssistantOutcome Success(object result, Type type)
        {
            JsonNode node = JsonSerializer.SerializeToNode(result, type, AssistantJson.Options) ?? new JsonObject();
            var scrubber = App?.MakeScrubber();
            if (scrubber != null) node = AssistantJson.ScrubStrings(node, (property, text) => Scrub(scrubber, property, text));
            string json = node.ToJsonString(AssistantJson.Options);
            using var doc = JsonDocument.Parse(json);
            return new AssistantOutcome { Ok = true, Json = json, Structured = doc.RootElement.Clone() };
        }

        /// <summary>The properties whose strings are PadForge's own words
        /// for a thing: IDs, numbers, inputs, kinds and states. They are
        /// searched for this PC's private values, and the scrubber's patterns
        /// for free text, which an ID or a step's kind can look like, are
        /// left out of them.</summary>
        internal static readonly HashSet<string> Identifiers = new(StringComparer.Ordinal)
        {
            "id", "profile_id", "device", "source_device", "preset", "target", "input", "source", "negative_source", "kind", "type",
            "state", "tool", "engine", "window", "automation_level", "padforge_version", "combine", "step_kinds", "file", "uri",
            "digest", "label",
        };

        /// <summary>A file's bytes, which are the file as PadForge writes it
        /// for the user, as its own Export button does: a scrubbed file would
        /// not import.</summary>
        internal const string FileBytes = "content_base64";

        private static string Scrub(PadForge.Engine.Privacy.PrivacyScrubber scrubber, string property, string text)
        {
            if (property == FileBytes) return text;
            return property != null && Identifiers.Contains(property)
                ? scrubber.SearchFinished(text, count: false)
                : scrubber.Text(text);
        }

        private static string LevelWords(AutomationLevel level) => level switch
        {
            AutomationLevel.Read => "the Read level",
            AutomationLevel.SendInputs => "the Send Inputs level",
            AutomationLevel.ChangeSettings => "the Change Settings level",
            _ => "the Commands level",
        };

        private bool TakeRate(int key)
        {
            lock (_sync)
            {
                if (!_rates.TryGetValue(key, out var rate))
                    _rates[key] = rate = new AutomationLineRate(CallsPerSecond, _nowMs());
                return rate.Take(_nowMs());
            }
        }

        /// <summary>A program left: its rate and its waiting requests go.</summary>
        internal void Forget(int key)
        {
            lock (_sync)
            {
                _rates.Remove(key);
                foreach (var id in _pending.Where(p => p.Value.Key == key).Select(p => p.Key).ToList())
                    _pending.Remove(id);
            }
        }

        // ── Activity ──

        /// <summary>One Activity entry for a call. <paramref name="readsOnly"/>
        /// is the tool's own hint: a call that only reads records nothing
        /// done, so it never stands in another program's way.
        /// <paramref name="asked"/> marks an entry of a question the user
        /// was asked, which Activity keeps and never merges.</summary>
        private void Record(AssistantCaller caller, string tool, string detail, AssistantOutcome outcome, int spell, bool readsOnly, bool asked)
        {
            string result = outcome.Ok ? "ok" : AutomationLine.Error(outcome.Code, outcome.Words);
            try
            {
                _host.Activity.Add(new AutomationActivityEntry(_host.Now(), caller.Program,
                    new AutomationAsk(AutomationAskKind.Tool, tool ?? string.Empty, Detail: detail), result, outcome.Ok)
                    { Spell = spell, ReadsOnly = readsOnly, HoldsApproval = asked });
            }
            catch
            {
                // A listener's failure is not the program's.
            }
        }

        // ── Questions to the user ──

        private sealed class PendingCall
        {
            public int Key;
            public string Tool;
            public string Detail;
            public volatile string State = "waiting";
            public string Json;
            public string Words;
        }

        /// <summary>
        /// Asks the user, then makes the change on the interface's thread
        /// when the user allows it. Within <see cref="AnswerWait"/> the call
        /// returns what the change returned, or a refusal. After it, the call
        /// returns a pending result, the question goes on waiting, and the
        /// change is made when an answer comes.
        /// </summary>
        internal async Task<object> AskThenAsync(AssistantInvocation invocation, AssistantAsk ask, Func<object> change)
        {
            var who = invocation.Caller.Program;
            // The request card holds a few questions at a time
            // (AutomationApprovals.MostPerProgram and MostInAll). A question
            // past either is never put: the approvals answer it TooMany at
            // once, and the call is refused in plain words.
            var request = new ApprovalRequest(who, ask.Title, ask.Detail);
            Task<ApprovalAnswer> answer = _host.Approvals.AskAsync(request, invocation.Caller.Session?.Lifetime ?? invocation.Stopping);
            if (answer.IsCompletedSuccessfully && answer.Result == ApprovalAnswer.TooMany)
                throw TooManyQuestions();
            invocation.Asked = true;
            int id = _host.Approvals.Pending.FirstOrDefault(p => ReferenceEquals(p.Request, request))?.Id ?? 0;
            var first = await Task.WhenAny(answer, Task.Delay(AnswerWait, invocation.Stopping)).ConfigureAwait(false);
            if (first == answer)
                return Finish(invocation, await answer.ConfigureAwait(false), change);

            var pending = new PendingCall { Key = invocation.Caller.Key, Tool = invocation.Tool.Name, Detail = invocation.Detail };
            lock (_sync) _pending[id] = pending;
            _ = answer.ContinueWith(done => Settle(invocation, pending, done, change), TaskScheduler.Default);
            var result = (AssistantChangeResult)Activator.CreateInstance(invocation.Tool.Result);
            result.Done = false;
            result.PendingRequest = id;
            result.Message = "The user has not answered yet. PadForge shows the question until it is answered or two minutes pass. Call check_request with "
                + id + " to learn how it ended. Do not ask again meanwhile.";
            invocation.Pending = true;
            return result;
        }

        private static AssistantRefusal TooManyQuestions()
            => new AssistantRefusal("too-many-questions",
                "Too many questions already wait for the user. Nothing changed. Wait for check_request to say how the waiting ones ended, then ask again.");

        private object Finish(AssistantInvocation invocation, ApprovalAnswer answer, Func<object> change)
        {
            switch (answer)
            {
                case ApprovalAnswer.Allowed:
                    return invocation.Change(change);
                case ApprovalAnswer.Denied:
                    throw new AssistantRefusal("declined", "The user did not allow it. Nothing changed. Ask again only if the user asks for it.");
                case ApprovalAnswer.TooMany:
                    throw TooManyQuestions();
                default:
                    throw new AssistantRefusal("timed-out", "The user did not answer in two minutes, so nothing changed. Ask the user before trying again.");
            }
        }

        /// <summary>A late answer: the change is made or not, and the
        /// outcome waits for check_request and goes into Activity.</summary>
        private void Settle(AssistantInvocation invocation, PendingCall pending, Task<ApprovalAnswer> done, Func<object> change)
        {
            AssistantOutcome outcome;
            var later = invocation.Later();
            try
            {
                if (done.IsCanceled) throw new AssistantRefusal("timed-out", "The program left before the user answered, so nothing changed.");
                object result = Finish(later, done.Result, change);
                outcome = Success(result, invocation.Tool.Result);
            }
            catch (AssistantRefusal refusal)
            {
                outcome = AssistantOutcome.Failure(refusal.Code, refusal.Words);
            }
            catch (Exception error)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("Automation: a change the user allowed failed: " + error.Message);
                outcome = AssistantOutcome.Failure("internal", "PadForge ran into a problem making the change the user allowed.");
            }
            pending.Json = outcome.Ok ? outcome.Json : null;
            pending.Words = outcome.Ok ? null : outcome.Words;
            pending.State = outcome.Ok ? "done" : outcome.Code switch
            {
                "declined" => "declined",
                "timed-out" => "timed_out",
                _ => "failed",
            };
            Record(invocation.Caller, pending.Tool, pending.Detail, outcome, outcome.Ok ? later.Spell : 0, invocation.Tool.ReadOnly, asked: true);
        }

        /// <summary>How a pending request of this program stands. Refuses a
        /// request that is not the program's own.</summary>
        internal AssistantRequestState Check(AssistantCaller caller, int id)
        {
            PendingCall pending;
            lock (_sync) _pending.TryGetValue(id, out pending);
            if (pending == null || pending.Key != caller.Key)
                throw new AssistantRefusal("unknown-request", "No request " + id + " of this connection is known. A request lasts as long as the connection that made it.");
            var state = new AssistantRequestState { RequestId = id, Tool = pending.Tool, State = pending.State, Message = pending.Words };
            if (pending.Json != null)
            {
                using var doc = JsonDocument.Parse(pending.Json);
                state.Result = doc.RootElement.Clone();
            }
            if (pending.State == "waiting") state.Message = "The user has not answered yet. Ask again in a while.";
            return state;
        }
    }

    /// <summary>How a request stands, for check_request.</summary>
    internal sealed class AssistantRequestState
    {
        [System.ComponentModel.Description("The request's ID.")]
        public int RequestId { get; set; }

        [System.ComponentModel.Description("The tool that made it.")]
        public string Tool { get; set; }

        [System.ComponentModel.Description("waiting, done, declined, timed_out or failed.")]
        public string State { get; set; }

        [System.ComponentModel.Description("What the tool returned once the change was made.")]
        public JsonElement? Result { get; set; }

        [System.ComponentModel.Description("What happened, in a sentence.")]
        public string Message { get; set; }
    }

    /// <summary>
    /// One call while it runs: who made it, what it may reach, and the
    /// helpers a tool uses to run on the interface's thread, to make a change
    /// the undo journal follows, and to ask the user first.
    /// </summary>
    internal sealed class AssistantInvocation
    {
        private readonly AssistantCalls _calls;
        private readonly IAutomationUi _ui;

        internal AssistantInvocation(AssistantCalls calls, AssistantCaller caller, AssistantToolDefinition tool, IAutomationUi ui, CancellationToken stopping)
        {
            _calls = calls;
            Caller = caller;
            Tool = tool;
            _ui = ui;
            Stopping = stopping;
        }

        public AssistantCaller Caller { get; }
        public AssistantToolDefinition Tool { get; }
        public CancellationToken Stopping { get; }
        public AssistantCalls Calls => _calls;
        public IAssistantApp App => _calls.App;
        public IAssistantSettings Settings => _calls.Settings;
        public AutomationHost Host => _calls.Host;

        /// <summary>What Activity names beside the tool: the profile, the
        /// setting or the controller. The program's text is shown as it
        /// gave it, cut short.</summary>
        public string Detail { get; set; }

        /// <summary>The undo journal's entry the change joined, or 0.</summary>
        public int Spell { get; internal set; }

        /// <summary>Whether the call returned a pending result.</summary>
        public bool Pending { get; internal set; }

        /// <summary>Whether the call put a question to the user.</summary>
        public bool Asked { get; internal set; }

        /// <summary>The same call for a change made after it returned:
        /// nothing it does may wait on the program's own token.</summary>
        internal AssistantInvocation Later() => new AssistantInvocation(_calls, Caller, Tool, _ui, CancellationToken.None) { Detail = Detail };

        /// <summary>Runs <paramref name="work"/> on the interface's thread
        /// and gives what it returns. Its refusals come back as they were
        /// thrown.</summary>
        public T OnUi<T>(Func<T> work)
        {
            T value = default;
            _ui.Invoke(() =>
            {
                value = work();
                return string.Empty;
            }, Stopping);
            return value;
        }

        public void OnUi(Action work) => OnUi(() => { work(); return 0; });

        /// <summary>Makes a change on the interface's thread, with the undo
        /// journal's state taken first when this is the program's first
        /// change after a quiet spell.</summary>
        public object Change(Func<object> change)
        {
            return OnUi(() =>
            {
                if (_calls.Undo != null) Spell = _calls.Undo.BeginChange(Caller.Key, Tool.Name, Detail);
                return change();
            });
        }

        /// <summary>Asks the user, then makes the change if allowed.</summary>
        public Task<object> AskThenAsync(AssistantAsk ask, Func<object> change) => _calls.AskThenAsync(this, ask, change);

        /// <summary>Asks first when <paramref name="ask"/> names something
        /// to ask about, else makes the change at once.</summary>
        public Task<object> ChangeAsync(AssistantAsk ask, Func<object> change)
            => ask == null || ask.Detail.Count == 0 ? Task.FromResult(Change(change)) : AskThenAsync(ask, change);
    }
}
