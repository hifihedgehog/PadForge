using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using PadForge.Engine;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>What the command set acts on in these tests: it records
    /// each call and answers what a test set.</summary>
    internal sealed class RecordingAutomationTarget : IAutomationTarget
    {
        public readonly List<string> Calls = new List<string>();
        public string ActivateAnswer = "ok Racing";
        public bool HasTray = true;
        public EngineChange EngineAnswer = EngineChange.Done;
        public HashSet<int> Controllers = new HashSet<int> { 1, 2 };
        public string SnapshotName = "diag-snapshot-20261123-150000.log";
        public Exception Throws;
        public AutomationState State = new AutomationState();

        private void Note(string call)
        {
            Calls.Add(call);
            if (Throws != null) throw Throws;
        }

        public string ActivateProfile(string nameOrId) { Note("activate " + nameOrId); return ActivateAnswer; }
        public string DeactivateProfile() { Note("deactivate"); return "ok default"; }
        public string QueryProfile() { Note("query"); return "ok default unpinned"; }
        public void ShowWindow() => Note("show");
        public bool HideToTray() { Note("hide"); return HasTray; }
        public void MinimizeWindow() => Note("minimize");
        public void Exit() => Note("exit");
        public EngineChange SetEngine(bool on) { Note("engine " + (on ? "on" : "off")); return EngineAnswer; }
        public void SetAllControllers(bool on) => Note("controllers " + (on ? "on" : "off"));
        public bool SetController(int number, bool on) { Note("slot " + number + (on ? " on" : " off")); return Controllers.Contains(number); }
        public string SaveSnapshot() { Note("snapshot"); return SnapshotName; }
        public AutomationState ReadState() { Note("state"); return State; }
    }

    /// <summary>The interface's thread in these tests: work runs in place,
    /// and what is posted waits until the test runs it.</summary>
    internal sealed class InlineAutomationUi : IAutomationUi
    {
        public readonly List<Action> Posted = new List<Action>();
        public int Invoked;

        public string Invoke(Func<string> work, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            Invoked++;
            return work();
        }

        public void Post(Action work) => Posted.Add(work);
    }

    /// <summary>
    /// The command set (#597): one executor for a line, used by the control
    /// pipe and by a starting copy's own steps. Driven here through a
    /// recorder, with no window, no engine and no pipe.
    /// </summary>
    public class AutomationCommandsTests
    {
        private sealed class Setup : IDisposable
        {
            public readonly RecordingAutomationTarget Target = new RecordingAutomationTarget();
            public readonly InlineAutomationUi Ui = new InlineAutomationUi();
            public readonly AutomationHost Host;

            public Setup(bool on = true, AutomationLevel level = AutomationLevel.Commands)
            {
                // A pipe name is one for the whole PC, so the host serves a
                // name of this test's own.
                Host = new AutomationHost(Target, Ui, "PadForge.Tests.Commands." + Guid.NewGuid().ToString("N"));
                if (on) Host.Apply(true, level);
            }

            public AutomationCommands Commands => Host.Commands;

            public string Line(string line) => Commands.Answer(line, CancellationToken.None).Line;

            public void Dispose() => Host.Dispose();
        }

        // ── Each line, its call and its answer ──

        [Theory]
        [InlineData("activate Racing", "activate Racing", "ok Racing")]
        [InlineData("ACTIVATE My Halo Profile", "activate My Halo Profile", "ok Racing")]
        [InlineData("  activate   spaced name  ", "activate spaced name", "ok Racing")]
        [InlineData("deactivate", "deactivate", "ok default")]
        [InlineData("query", "query", "ok default unpinned")]
        [InlineData("show", "show", "ok")]
        [InlineData("hide", "hide", "ok")]
        [InlineData("minimize", "minimize", "ok")]
        [InlineData("engine on", "engine on", "ok on")]
        [InlineData("engine off", "engine off", "ok off")]
        [InlineData("Engine OFF", "engine off", "ok off")]
        [InlineData("controllers on", "controllers on", "ok on")]
        [InlineData("controllers off", "controllers off", "ok off")]
        [InlineData("slot 1 on", "slot 1 on", "ok 1 on")]
        [InlineData("slot 2 off", "slot 2 off", "ok 2 off")]
        [InlineData("slot  2   OFF", "slot 2 off", "ok 2 off")]
        [InlineData("snapshot", "snapshot", "ok diag-snapshot-20261123-150000.log")]
        public void ALine_DoesWhatItsControlDoes_AndAnswers(string line, string call, string answer)
        {
            using var setup = new Setup();
            Assert.Equal(answer, setup.Line(line));
            Assert.Equal(new[] { call }, setup.Target.Calls);
            // Every state touch runs on the interface's thread.
            Assert.Equal(1, setup.Ui.Invoked);
        }

        /// <summary>The three verbs of #366 answer in the words they always
        /// had, whatever the app's own code answers through them.</summary>
        [Fact]
        public void TheProfileVerbs_KeepTheirAnswers()
        {
            using var setup = new Setup();
            setup.Target.ActivateAnswer = "error unknown-profile";
            Assert.Equal("error unknown-profile", setup.Line("activate Nope"));
            Assert.Equal("error empty", setup.Line("activate"));
            Assert.Equal("error empty", setup.Line("activate   "));
            Assert.Equal("error empty", setup.Line(""));
            Assert.Equal("error empty", setup.Line("   "));
            Assert.Equal("error empty", setup.Line(null));
            Assert.Equal("error unknown-command", setup.Line("launch"));
            // The two that take no argument never minded one.
            Assert.Equal("ok default", setup.Line("deactivate now"));
            Assert.Equal("ok default unpinned", setup.Line("query please"));
        }

        [Fact]
        public void Version_IsTheBuildsThreeParts()
        {
            using var setup = new Setup();
            string line = setup.Line("version");
            string[] parts = line.Split(' ');
            Assert.Equal(4, parts.Length);
            Assert.Equal("ok", parts[0]);
            Assert.Equal(BuildIdentity.VersionText(BuildIdentity.Version), parts[1]);
            Assert.Equal("r" + BuildIdentity.BuildNumber, parts[2]);
            Assert.Equal(BuildIdentity.Commit.Length > 0 ? BuildIdentity.Commit : "-", parts[3]);
            Assert.Equal(AutomationCommands.VersionLine(), line);
            // It touches no state, so it asks nothing of the interface's thread.
            Assert.Empty(setup.Target.Calls);
            Assert.Equal(0, setup.Ui.Invoked);
        }

        // ── Each error ──

        [Theory]
        [InlineData("show now")]
        [InlineData("hide it")]
        [InlineData("minimize 1")]
        [InlineData("exit 0")]
        [InlineData("snapshot here")]
        [InlineData("version 2")]
        [InlineData("state all")]
        [InlineData("engine")]
        [InlineData("engine up")]
        [InlineData("engine on now")]
        [InlineData("controllers")]
        [InlineData("controllers all")]
        [InlineData("slot")]
        [InlineData("slot 2")]
        [InlineData("slot on")]
        [InlineData("slot 0 on")]
        [InlineData("slot 17 on")]
        [InlineData("slot -1 on")]
        [InlineData("slot two on")]
        [InlineData("slot 2 up")]
        [InlineData("slot 2 on off")]
        [InlineData("record")]
        [InlineData("record pause")]
        [InlineData("record start now")]
        [InlineData("record stop now")]
        public void ALineWithAWrongArgument_IsBadArgument_AndTouchesNothing(string line)
        {
            using var setup = new Setup();
            Assert.Equal("error bad-argument", setup.Line(line));
            Assert.Equal(AutomationCommands.BadArgument, setup.Line(line));
            Assert.Empty(setup.Target.Calls);
            Assert.Equal(0, setup.Ui.Invoked);
        }

        [Fact]
        public void AControllerNumberNoControllerHas_IsUnknownSlot()
        {
            using var setup = new Setup();
            Assert.Equal("error unknown-slot", setup.Line("slot 9 on"));
            Assert.Equal(new[] { "slot 9 on" }, setup.Target.Calls);
        }

        [Fact]
        public void AnEngineThatIsStillStopping_IsBusy_AndOneThatDidNotComeUpIsInternal()
        {
            using var setup = new Setup();
            setup.Target.EngineAnswer = EngineChange.Busy;
            Assert.Equal("error busy", setup.Line("engine on"));
            setup.Target.EngineAnswer = EngineChange.Failed;
            Assert.Equal("error internal", setup.Line("engine on"));
        }

        [Fact]
        public void HideWithNoTrayIcon_IsNotAvailable()
        {
            using var setup = new Setup();
            setup.Target.HasTray = false;
            Assert.Equal("error not-available", setup.Line("hide"));
        }

        [Fact]
        public void ACallThatFails_IsInternal_OnTheStartsOwnLineToo()
        {
            using var setup = new Setup();
            setup.Target.Throws = new InvalidOperationException("boom");
            Assert.Equal("error internal", setup.Commands.RunOwn("engine on"));
            // On the pipe the failure is the pipe's to turn into the answer
            // (ExternalControlService), and the host's (AutomationHost).
            Assert.Throws<InvalidOperationException>(() => setup.Line("engine on"));
        }

        // ── The recorder, which another part of the build fills ──

        [Fact]
        public void TheRecorderLines_AreNotAvailable_UntilTheHostIsHandedARecorder()
        {
            using var setup = new Setup();
            Assert.Equal("error not-available", setup.Line("record start"));
            Assert.Equal("error not-available", setup.Line("record stop"));
            Assert.Equal("error not-available", setup.Line("record mark"));
            Assert.Equal("error not-available", setup.Line("record mark lap three"));

            var marks = new List<string>();
            const string zip = "PadForge-recording-20261123-150000.zip";
            setup.Host.RecordStart = () => AutomationRecordAnswer.Done(zip);
            setup.Host.RecordStop = () => AutomationRecordAnswer.Done(zip);
            setup.Host.RecordMark = text => { marks.Add(text); return AutomationRecordAnswer.Done(zip); };
            Assert.Equal("ok " + zip, setup.Line("record start"));
            Assert.Equal("ok " + zip, setup.Line("RECORD Stop"));
            Assert.Equal("ok " + zip, setup.Line("record mark"));
            Assert.Equal("ok " + zip, setup.Line("record mark lap three"));
            Assert.Equal(new[] { "", "lap three" }, marks);
            // The recorder is reached on the interface's thread, as every
            // other command's work is: eight lines so far, eight calls.
            Assert.Equal(8, setup.Ui.Invoked);

            // A marker Keep the Last 60 Seconds holds has no file yet.
            setup.Host.RecordMark = text => AutomationRecordAnswer.Done(string.Empty);
            Assert.Equal("ok", setup.Line("record mark"));

            // The recorder's refusals come back as its error words.
            setup.Host.RecordStart = () => AutomationRecordAnswer.Busy;
            setup.Host.RecordStop = () => AutomationRecordAnswer.NotAvailable;
            setup.Host.RecordMark = text => AutomationRecordAnswer.NotAvailable;
            Assert.Equal("error busy", setup.Line("record start"));
            Assert.Equal("error not-available", setup.Line("record stop"));
            Assert.Equal("error not-available", setup.Line("record mark"));
        }

        // ── exit answers first ──

        [Fact]
        public void Exit_AnswersFirst_AndClosesAfterTheAnswerIsWritten()
        {
            using var setup = new Setup();
            var answer = setup.Commands.Answer("exit", CancellationToken.None);
            Assert.Equal("ok", answer.Line);
            // Nothing has closed yet: the answer is not written.
            Assert.Empty(setup.Target.Calls);
            Assert.Empty(setup.Ui.Posted);
            Assert.NotNull(answer.AfterWritten);

            // The pipe runs this once the answer is in it. It only queues the
            // close for the interface's thread, and never waits for it.
            answer.AfterWritten();
            Assert.Empty(setup.Target.Calls);
            var close = Assert.Single(setup.Ui.Posted);
            close();
            Assert.Equal(new[] { "exit" }, setup.Target.Calls);
        }

        [Fact]
        public void NoOtherLine_LeavesWorkForAfterItsAnswer()
        {
            using var setup = new Setup();
            foreach (string line in new[] { "activate Racing", "deactivate", "query", "show", "hide", "minimize", "engine on",
                         "controllers off", "slot 1 on", "snapshot", "version", "state", "record start", "nope", "" })
                Assert.Null(setup.Commands.Answer(line, CancellationToken.None).AfterWritten);
        }

        // ── The level ──

        [Fact]
        public void WithAutomationOff_EveryLineThatReadsIsRefused_AndNothingIsTouched()
        {
            using var setup = new Setup(on: false);
            foreach (string line in new[] { "activate Racing", "deactivate", "query", "show", "hide", "minimize", "exit",
                         "engine on", "controllers off", "slot 1 on", "snapshot", "record start", "version", "state" })
            {
                var answer = setup.Commands.Answer(line, CancellationToken.None);
                Assert.Equal("error not-allowed", answer.Line);
                Assert.Null(answer.AfterWritten);
            }
            Assert.Empty(setup.Target.Calls);
            Assert.Equal(0, setup.Ui.Invoked);

            // A line that cannot be read is told so before the level is asked.
            Assert.Equal("error empty", setup.Line(""));
            Assert.Equal("error unknown-command", setup.Line("launch"));
            Assert.Equal("error bad-argument", setup.Line("engine up"));
        }

        [Theory]
        [InlineData(AutomationLevel.Commands)]
        [InlineData(AutomationLevel.Read)]
        [InlineData(AutomationLevel.SendInputs)]
        [InlineData(AutomationLevel.ChangeSettings)]
        public void EveryLevel_AllowsTheCommandSet(AutomationLevel level)
        {
            using var setup = new Setup(level: level);
            Assert.Equal(AutomationLevel.Commands, AutomationCommands.Needs);
            Assert.Equal("ok on", setup.Line("engine on"));
            Assert.Equal("ok Racing", setup.Line("activate Racing"));
        }

        /// <summary>A first start applies its switches whether Automation is
        /// on or off: they are the user's own start.</summary>
        [Fact]
        public void TheStartsOwnLines_RunWithAutomationOff()
        {
            using var setup = new Setup(on: false);
            Assert.Equal("ok Racing", setup.Commands.RunOwn("activate Racing"));
            Assert.Equal("ok off", setup.Commands.RunOwn("controllers off"));
            Assert.Equal("ok 2 off", setup.Commands.RunOwn("slot 2 off"));
            Assert.Equal("error not-available", setup.Commands.RunOwn("record start"));
            Assert.Equal("error unknown-command", setup.Commands.RunOwn("launch"));
            Assert.Equal(new[] { "activate Racing", "controllers off", "slot 2 off" }, setup.Target.Calls);
        }

        /// <summary>A pipe that stops while a line waits for the interface's
        /// thread lets the line go, so the stop never waits on it.</summary>
        [Fact]
        public void ALineStopsWaiting_WhenThePipeStops()
        {
            using var setup = new Setup();
            using var stopping = new CancellationTokenSource();
            stopping.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => setup.Commands.Answer("engine on", stopping.Token));
            Assert.Empty(setup.Target.Calls);
        }

        // ── The state line ──

        [Fact]
        public void State_IsOneLineOfJson_WithEveryPart()
        {
            using var setup = new Setup(level: AutomationLevel.SendInputs);
            setup.Host.Recording = () => true;
            setup.Target.State = new AutomationState
            {
                Engine = AutomationEngineState.Idle,
                ProfileName = "Café \"Racing\"\nline",
                ProfileId = "0123456789abcdef0123456789abcdef",
                ProfilePinned = true,
                Window = AutomationWindowState.Tray,
                Controllers = new[]
                {
                    new AutomationControllerState(1, "Xbox 360 Controller", VirtualControllerType.Xbox, true, 2, 1),
                    new AutomationControllerState(2, "DualSense", VirtualControllerType.PlayStation, false, 0, 0),
                },
            };

            string line = setup.Line("state");
            Assert.StartsWith("ok {", line, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            // Nothing outside ASCII, so any code page carries it.
            Assert.All(line, c => Assert.InRange(c, ' ', '~'));

            using var json = JsonDocument.Parse(line.Substring(3));
            var root = json.RootElement;
            Assert.Equal(new[] { "version", "build", "commit", "engine", "profile", "window", "automation", "recording", "controllers" },
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(BuildIdentity.VersionText(BuildIdentity.Version), root.GetProperty("version").GetString());
            Assert.Equal(BuildIdentity.BuildNumber, root.GetProperty("build").GetInt32());
            Assert.Equal(BuildIdentity.Commit, root.GetProperty("commit").GetString());
            Assert.Equal("idle", root.GetProperty("engine").GetString());
            Assert.Equal("Café \"Racing\"\nline", root.GetProperty("profile").GetProperty("name").GetString());
            Assert.Equal("0123456789abcdef0123456789abcdef", root.GetProperty("profile").GetProperty("id").GetString());
            Assert.True(root.GetProperty("profile").GetProperty("pinned").GetBoolean());
            Assert.Equal("tray", root.GetProperty("window").GetString());
            Assert.True(root.GetProperty("automation").GetProperty("on").GetBoolean());
            Assert.Equal("send-inputs", root.GetProperty("automation").GetProperty("level").GetString());
            Assert.True(root.GetProperty("recording").GetBoolean());

            var controllers = root.GetProperty("controllers").EnumerateArray().ToList();
            Assert.Equal(2, controllers.Count);
            Assert.Equal(new[] { "slot", "name", "type", "on", "devices", "online" }, controllers[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, controllers[0].GetProperty("slot").GetInt32());
            Assert.Equal("Xbox 360 Controller", controllers[0].GetProperty("name").GetString());
            Assert.Equal("xbox", controllers[0].GetProperty("type").GetString());
            Assert.True(controllers[0].GetProperty("on").GetBoolean());
            Assert.Equal(2, controllers[0].GetProperty("devices").GetInt32());
            Assert.Equal(1, controllers[0].GetProperty("online").GetInt32());
            Assert.Equal("playstation", controllers[1].GetProperty("type").GetString());
            Assert.False(controllers[1].GetProperty("on").GetBoolean());
        }

        [Fact]
        public void State_OnTheDefaultProfile_WithNoControllerAndNoRecorder()
        {
            using var setup = new Setup();
            using var json = JsonDocument.Parse(setup.Line("state").Substring(3));
            var root = json.RootElement;
            Assert.Equal("stopped", root.GetProperty("engine").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("profile").GetProperty("name").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("profile").GetProperty("id").ValueKind);
            Assert.False(root.GetProperty("profile").GetProperty("pinned").GetBoolean());
            Assert.Equal("shown", root.GetProperty("window").GetString());
            Assert.Equal("commands", root.GetProperty("automation").GetProperty("level").GetString());
            Assert.False(root.GetProperty("recording").GetBoolean());
            Assert.Equal(0, root.GetProperty("controllers").GetArrayLength());
        }

        [Fact]
        public void TheStateLinesWords_AreFixed()
        {
            using var setup = new Setup();
            string Engine(AutomationEngineState engine)
            {
                setup.Target.State = new AutomationState { Engine = engine };
                using var json = JsonDocument.Parse(setup.Line("state").Substring(3));
                return json.RootElement.GetProperty("engine").GetString();
            }
            string Window(AutomationWindowState window)
            {
                setup.Target.State = new AutomationState { Window = window };
                using var json = JsonDocument.Parse(setup.Line("state").Substring(3));
                return json.RootElement.GetProperty("window").GetString();
            }
            Assert.Equal(new[] { "stopped", "stopping", "idle", "running" },
                new[] { AutomationEngineState.Stopped, AutomationEngineState.Stopping, AutomationEngineState.Idle, AutomationEngineState.Running }.Select(Engine));
            Assert.Equal(new[] { "shown", "minimized", "tray" },
                new[] { AutomationWindowState.Shown, AutomationWindowState.Minimized, AutomationWindowState.Tray }.Select(Window));

            // Every controller type has its word, apart from its name in code.
            var words = Enum.GetValues<VirtualControllerType>().ToDictionary(t => t, t => AutomationCommands.Word(t));
            Assert.Equal(7, words.Count);
            Assert.Equal("xbox", words[VirtualControllerType.Xbox]);
            Assert.Equal("playstation", words[VirtualControllerType.PlayStation]);
            Assert.Equal("nintendo", words[VirtualControllerType.Nintendo]);
            Assert.Equal("extended", words[VirtualControllerType.Extended]);
            Assert.Equal("keyboard-mouse", words[VirtualControllerType.KeyboardMouse]);
            Assert.Equal("midi", words[VirtualControllerType.Midi]);
            Assert.Equal("vr", words[VirtualControllerType.Vr]);
            Assert.DoesNotContain("unknown", words.Values);

            Assert.Equal(new[] { "commands", "read", "send-inputs", "change-settings" },
                Enum.GetValues<AutomationLevel>().Select(AutomationLevels.Word));
        }

        // ── What Activity says a line asked ──

        [Theory]
        [InlineData("activate Racing", "ActivateProfile", "Racing")]
        [InlineData("deactivate", "DeactivateProfile", null)]
        [InlineData("query", "QueryProfile", null)]
        [InlineData("show", "ShowWindow", null)]
        [InlineData("hide", "HideWindow", null)]
        [InlineData("minimize", "MinimizeWindow", null)]
        [InlineData("exit", "Exit", null)]
        [InlineData("engine on", "EngineOn", null)]
        [InlineData("engine off", "EngineOff", null)]
        [InlineData("controllers on", "ControllersOn", null)]
        [InlineData("controllers off", "ControllersOff", null)]
        [InlineData("slot 3 on", "ControllerOn", "3")]
        [InlineData("slot 3 off", "ControllerOff", "3")]
        [InlineData("snapshot", "Snapshot", null)]
        [InlineData("record start", "RecordStart", null)]
        [InlineData("record stop", "RecordStop", null)]
        [InlineData("record mark lap three", "RecordMark", "lap three")]
        [InlineData("version", "Version", null)]
        [InlineData("state", "State", null)]
        [InlineData("launch the rockets", "Unknown", "launch the rockets")]
        [InlineData("engine up", "Unknown", "engine up")]
        [InlineData("", "Unknown", "")]
        public void ALine_IsDescribedForActivity(string line, string kind, string argument)
        {
            var asked = AutomationCommands.Describe(line);
            Assert.Equal(kind, asked.Kind.ToString());
            Assert.Equal(argument ?? string.Empty, asked.Argument ?? string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(asked.Text));
        }

        [Fact]
        public void EveryAsk_HasItsOwnWords_AndNamesWhatItNamed()
        {
            var texts = Enum.GetValues<AutomationAskKind>().Select(kind => new AutomationAsk(kind, "ARG").Text).ToList();
            Assert.Equal(texts.Count, texts.Distinct().Count());
            Assert.Contains("Racing", new AutomationAsk(AutomationAskKind.ActivateProfile, "Racing").Text, StringComparison.Ordinal);
            Assert.Contains("7", new AutomationAsk(AutomationAskKind.ControllerOn, "7").Text, StringComparison.Ordinal);
            Assert.Contains("7", new AutomationAsk(AutomationAskKind.ControllerOff, "7").Text, StringComparison.Ordinal);
            Assert.Contains("launch", new AutomationAsk(AutomationAskKind.Unknown, "launch").Text, StringComparison.Ordinal);

            // A program's own text is cut where a sentence can still hold it.
            string longText = new AutomationAsk(AutomationAskKind.Unknown, new string('x', 500)).Text;
            Assert.True(longText.Length < 200, longText.Length.ToString());
            Assert.Contains("…", longText, StringComparison.Ordinal);
        }

        /// <summary>The verbs and the answers are fixed ASCII. Localizing a
        /// machine interface would break every script the moment the user
        /// changes language, so no answer reads from the strings.</summary>
        [Fact]
        public void TheGrammarIsNeverLocalized()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/Automation/AutomationCommands.cs");
            foreach (string verb in new[] { "activate", "deactivate", "query", "show", "hide", "minimize", "exit", "snapshot",
                         "version", "state", "engine", "controllers", "slot", "record" })
                Assert.Contains("case \"" + verb + "\":", source, StringComparison.Ordinal);
            foreach (string word in new[] { "error empty", "error unknown-command", "error internal", "error bad-argument",
                         "error busy", "error not-allowed", "error not-available", "error unknown-slot" })
                Assert.Contains("\"" + word + "\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Strings.Instance", source, StringComparison.Ordinal);
            Assert.DoesNotContain("using PadForge.Resources.Strings", source, StringComparison.Ordinal);

            // The profile verbs' own words are the app's, and fixed there too.
            string service = OffLimitsSource.Read("PadForge.App/Services/InputService.cs");
            string activate = FenceSource.Body(service, "internal string ExternalActivate(string idOrName)");
            Assert.Contains("return \"error unknown-profile\";", activate, StringComparison.Ordinal);
            Assert.Contains("return \"ok \" + target.Name;", activate, StringComparison.Ordinal);
            Assert.Contains("return \"ok default\";", FenceSource.Body(service, "internal string ExternalDeactivate()"), StringComparison.Ordinal);
            Assert.Contains("return \"ok \" + name + \" \" + pin;", FenceSource.Body(service, "internal string ExternalQuery()"), StringComparison.Ordinal);
            // Status text is localized (it is UI), but no answer is.
            foreach (string member in new[] { "internal string ExternalActivate(string idOrName)", "internal string ExternalDeactivate()", "internal string ExternalQuery()" })
                Assert.DoesNotContain("return Strings.Instance", FenceSource.Body(service, member), StringComparison.Ordinal);
        }
    }
}
