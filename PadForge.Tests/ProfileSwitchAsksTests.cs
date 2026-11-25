using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Telemetry;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// A program's profile switch (#600) that would turn on an extra that
    /// listens on the network, off now, asks the user on the question card
    /// first, naming what would start, and the program is answered at once,
    /// <c>error waiting</c>. Allow makes the switch, Don't Allow and no answer
    /// leave the profile as it was, and a switch that starts nothing new, or
    /// one the user's own command line makes, runs at once with no card. The
    /// indirect routes meet the same: a macro a program saves that switches
    /// to such a profile, a profile it imports, and its delete of the profile
    /// in force.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ProfileSwitchAsksTests
    {
        private static readonly AutomationProgram Launcher = new("Launcher", 4242, "launcher.exe", @"C:\Games\launcher.exe");

        private sealed class Setup : IDisposable
        {
            public readonly RecordingAutomationTarget Target = new();
            public readonly InlineAutomationUi Ui = new();
            public readonly AutomationHost Host;

            public Setup(TimeSpan? approvalWait = null, IAutomationTarget target = null)
            {
                Host = TestConsent.AllowAll(new AutomationHost(target ?? Target, Ui, "PadForge.Tests.ProfileAsks." + Guid.NewGuid().ToString("N"),
                    approvalWait: approvalWait));
                Host.Apply(true, AutomationLevel.Commands);
            }

            public string Line(string line, AutomationProgram who = null) => Host.Commands.Answer(line, CancellationToken.None, who).Line;

            /// <summary>Runs what the card's answer queued for the
            /// interface's thread, once it is queued.</summary>
            public void RunPosted(int count)
            {
                var clock = Stopwatch.StartNew();
                while (Ui.Posted.Count < count)
                {
                    Assert.True(clock.ElapsedMilliseconds < 10000, "nothing was queued for the interface's thread");
                    Thread.Sleep(10);
                }
                foreach (var work in Ui.Posted.ToList()) work();
                Ui.Posted.Clear();
            }

            public AutomationActivityEntry Outcome(string result)
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    var entry = Host.Activity.Snapshot().LastOrDefault(e => e.Result == result);
                    if (entry != null) return entry;
                    Assert.True(clock.ElapsedMilliseconds < 10000, "Activity holds no " + result + ": "
                        + string.Join(" | ", Host.Activity.Snapshot().Select(e => e.Result)));
                    Thread.Sleep(10);
                }
            }

            public void Dispose() => Host.Dispose();
        }

        private static ProfileStarts Starts(string name, params string[] extras) => new(name, extras);

        // ── The command set ──

        [Fact]
        public void ASwitchThatStartsAListener_AsksFirst_AndDontAllowLeavesTheProfile()
        {
            using var setup = new Setup();
            var s = Strings.Instance;
            setup.Target.Starts = Starts("Racing", s.Dashboard_WebController, s.Dashboard_GameMods);
            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate racing", Launcher));
            Assert.Empty(setup.Target.Calls);

            var pending = Assert.Single(setup.Host.Approvals.Pending);
            Assert.Same(Launcher, pending.Request.Who);
            Assert.Equal(string.Format(s.Automation_Asked_Activate_Format, "Racing"), pending.Request.Title);
            Assert.Equal(new[] { AssistantToolWords.TurnsOn(s.Dashboard_WebController), AssistantToolWords.TurnsOn(s.Dashboard_GameMods), s.Automation_Ask_Network },
                pending.Request.Detail);

            Assert.True(setup.Host.Approvals.Answer(pending.Id, allowed: false));
            Assert.Equal(Launcher, setup.Outcome("error declined").Program);
            Assert.Empty(setup.Ui.Posted);
            Assert.Empty(setup.Target.Calls);
        }

        [Fact]
        public void Allow_MakesTheSwitch_WhatTheCardNamedAndNoMore()
        {
            using var setup = new Setup();
            var s = Strings.Instance;
            setup.Target.Starts = Starts("Racing", s.Dashboard_WebController);
            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            Assert.True(setup.Host.Approvals.Answer(Assert.Single(setup.Host.Approvals.Pending).Id, allowed: true));
            setup.RunPosted(1);
            Assert.Equal(new[] { "activate Racing" }, setup.Target.Calls);
            Assert.Equal(AutomationAskKind.ActivateProfile, setup.Outcome("ok Racing").Asked.Kind);

            // A switch that would by then start more than the card named is
            // not made: Motion Server went off while the card showed.
            setup.Target.Calls.Clear();
            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            Assert.True(setup.Host.Approvals.Answer(Assert.Single(setup.Host.Approvals.Pending).Id, allowed: true));
            setup.Target.Starts = Starts("Racing", s.Dashboard_WebController, s.Dashboard_MotionServer);
            setup.RunPosted(1);
            Assert.Empty(setup.Target.Calls);
            setup.Outcome(AutomationCommands.NotAvailable);
        }

        [Fact]
        public void ReturningToTheDefault_AsksTheSame()
        {
            using var setup = new Setup();
            var s = Strings.Instance;
            setup.Target.Starts = Starts(s.Profile_Default, s.Automation_Ask_HeadTrackingUdp);
            Assert.Equal(AutomationCommands.Waiting, setup.Line("deactivate", Launcher));
            var pending = Assert.Single(setup.Host.Approvals.Pending);
            Assert.Equal(s.Automation_Asked_Deactivate, pending.Request.Title);
            Assert.Equal(new[] { AssistantToolWords.TurnsOn(s.Automation_Ask_HeadTrackingUdp), s.Automation_Ask_Network }, pending.Request.Detail);
            Assert.True(setup.Host.Approvals.Answer(pending.Id, allowed: true));
            setup.RunPosted(1);
            Assert.Equal(new[] { "deactivate" }, setup.Target.Calls);
        }

        [Fact]
        public void NoAnswerInTheCardsTime_LeavesTheProfile()
        {
            using var setup = new Setup(approvalWait: TimeSpan.FromMilliseconds(200));
            setup.Target.Starts = Starts("Racing", Strings.Instance.Dashboard_GameTelemetry);
            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            setup.Outcome("error timed-out");
            Assert.Empty(setup.Ui.Posted);
            Assert.Empty(setup.Target.Calls);
        }

        /// <summary>The positive controls: a switch that starts nothing new,
        /// a launcher's ordinary profile, runs at once with no card, in one
        /// visit to the interface's thread. A starting copy's own command
        /// line is the user's own, and runs at once whatever it
        /// starts.</summary>
        [Fact]
        public void ASwitchThatStartsNothingNew_AndTheUsersOwnLine_RunAtOnce()
        {
            using var setup = new Setup();
            Assert.Equal("ok Racing", setup.Line("activate Racing", Launcher));
            Assert.Equal(new[] { "activate Racing" }, setup.Target.Calls);
            Assert.Equal(1, setup.Ui.Invoked);
            Assert.Equal("ok default", setup.Line("deactivate", Launcher));
            Assert.Empty(setup.Host.Approvals.Pending);

            setup.Target.Calls.Clear();
            setup.Target.Starts = Starts("Racing", Strings.Instance.Dashboard_WebController);
            Assert.Equal("ok Racing", setup.Host.Commands.RunOwn("activate Racing"));
            Assert.Equal("ok default", setup.Host.Commands.RunOwn("deactivate"));
            Assert.Equal(new[] { "activate Racing", "deactivate" }, setup.Target.Calls);
            Assert.Empty(setup.Host.Approvals.Pending);
        }

        [Fact]
        public void ACardThatCannotBePut_IsSaidSo_AndNothingSwitches()
        {
            using var setup = new Setup();
            setup.Target.Starts = Starts("Racing", Strings.Instance.Dashboard_WebController);
            for (int i = 0; i < AutomationApprovals.MostPerProgram; i++)
                Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            Assert.Equal(AutomationCommands.TooManyQuestions, setup.Line("activate Racing", Launcher));
            Assert.Equal(AutomationApprovals.MostPerProgram, setup.Host.Approvals.Pending.Count);
            Assert.Empty(setup.Target.Calls);
        }

        // ── Over the real pipes ──

        /// <summary>What a program's switch to PadForge's own switch does to
        /// the profile in force: it follows ActivateProfile, as
        /// InputService.ExternalActivate does.</summary>
        private sealed class SwitchingTarget : IAutomationTarget
        {
            public ProfileStarts Starts = ProfileStarts.None;
            public string ActivateProfile(string nameOrId)
            {
                SettingsManager.ActiveProfileId = "racing-1";
                return "ok Racing";
            }
            public string DeactivateProfile() => throw new InvalidOperationException();
            public ProfileStarts NetworkStarts(string nameOrId) => Starts;
            public string QueryProfile() => throw new InvalidOperationException();
            public void ShowWindow() => throw new InvalidOperationException();
            public bool HideToTray() => throw new InvalidOperationException();
            public void MinimizeWindow() => throw new InvalidOperationException();
            public void MaximizeWindow() => throw new InvalidOperationException();
            public void Exit() => throw new InvalidOperationException();
            public EngineChange SetEngine(bool on) => throw new InvalidOperationException();
            public void SetAllControllers(bool on) => throw new InvalidOperationException();
            public bool SetController(int number, bool on) => throw new InvalidOperationException();
            public string SaveSnapshot(bool scrubbed) => throw new InvalidOperationException();
            public AutomationState ReadState() => throw new InvalidOperationException();
        }

        /// <summary>The control pipe's client reads <c>error waiting</c>,
        /// and the card names it as Activity does (the host's program of the
        /// pipe's client).</summary>
        [Fact]
        public void OverTheControlPipe_TheClientReadsWaiting_AndTheCardNamesIt()
        {
            string pipe = "PadForge.Tests.ProfileAsksControl." + Guid.NewGuid().ToString("N");
            var target = new RecordingAutomationTarget { Starts = Starts("Racing", Strings.Instance.Dashboard_WebController) };
            using var host = TestConsent.AllowAll(new AutomationHost(target, new InlineAutomationUi(), pipe,
                pid => new AutomationProgram("launcher", pid, "launcher.exe")));
            host.Apply(true, AutomationLevel.Commands);
            Assert.Equal(AutomationCommands.Waiting, ControlPipeClient.Exchange(pipe, "activate Racing", 5000));
            var pending = Assert.Single(host.Approvals.Pending);
            Assert.Equal("launcher", pending.Request.Who.Name);
            Assert.Equal(Environment.ProcessId, pending.Request.Who.ProcessId);
            Assert.Empty(target.Calls);
            // What the command line says for it, by the code's own words.
            Assert.Equal("waiting", ClientRun.CodeOf(AutomationCommands.Waiting));
            Assert.Equal(AutomationLine.WordsFor("waiting"), AutomationLine.FromCommandSet(AutomationCommands.Waiting).Substring("err waiting ".Length));
        }

        /// <summary>A script's session reads <c>err waiting</c> with its
        /// words, the card names the script, and once the user allows it a
        /// script that follows the profile hears the change.</summary>
        [Fact]
        public async System.Threading.Tasks.Task OverTheAutomationPipe_TheScriptReadsWaiting_AndAFollowerHearsTheSwitch()
        {
            string saved = SettingsManager.ActiveProfileId;
            string pipe = "PadForge.Tests.ProfileAsksPipe." + Guid.NewGuid().ToString("N");
            var target = new SwitchingTarget { Starts = Starts("Racing", Strings.Instance.Dashboard_WebController) };
            var ui = new InlineAutomationUi();
            try
            {
                SettingsManager.ActiveProfileId = null;
                using var host = TestConsent.AllowAll(new AutomationHost(target, ui, "PadForge.Tests.ProfileAsksControl." + Guid.NewGuid().ToString("N"),
                    automationPipeName: pipe));
                host.ProfileName = () => SettingsManager.ActiveProfileId == null ? null : "Racing";
                host.Apply(true, AutomationLevel.Read);
                using var client = new AutomationTestClient(pipe);
                Assert.StartsWith("ok padforge=", client.Final("hello name=\"Lap Timer\" kind=script"));
                Assert.Equal("ok", client.Final("sub profile"));
                await System.Threading.Tasks.Task.Delay(300);

                Assert.Equal("err waiting " + AutomationLine.WordsFor("waiting"), client.Final("activate Racing"));
                Assert.NotEqual(AutomationLine.WordsFor("no-such-code"), AutomationLine.WordsFor("waiting"));
                var pending = Assert.Single(host.Approvals.Pending);
                Assert.Equal("Lap Timer", pending.Request.Who.Name);
                Assert.Null(SettingsManager.ActiveProfileId);

                Assert.True(host.Approvals.Answer(pending.Id, allowed: true));
                var clock = Stopwatch.StartNew();
                // The switch, then the follower's event, each through the
                // interface's thread, which here is this test's.
                int ran = 0;
                string heard = null;
                while (heard == null)
                {
                    Assert.True(clock.ElapsedMilliseconds < 10000, "no profile event came. The profile in force: " + SettingsManager.ActiveProfileId);
                    while (ran < ui.Posted.Count) ui.Posted[ran++]();
                    heard = client.Event(100);
                }
                Assert.Equal("e profile \"Racing\"", heard);
                Assert.Equal("racing-1", SettingsManager.ActiveProfileId);
            }
            finally
            {
                SettingsManager.ActiveProfileId = saved;
            }
        }

        // ── The app's own switch, end to end ──

        /// <summary>The command set over PadForge's own profile switch
        /// (InputService.ExternalActivate), as the window's target drives it.
        /// The members a switch does not use are never called.</summary>
        private sealed class InputTarget : IAutomationTarget
        {
            private readonly InputService _input;
            public InputTarget(InputService input) => _input = input;
            public string ActivateProfile(string nameOrId) => _input.ExternalActivate(nameOrId);
            public string DeactivateProfile() => _input.ExternalDeactivate();
            public ProfileStarts NetworkStarts(string nameOrId) => _input.ExternalNetworkStarts(nameOrId);
            public string QueryProfile() => throw new InvalidOperationException();
            public void ShowWindow() => throw new InvalidOperationException();
            public bool HideToTray() => throw new InvalidOperationException();
            public void MinimizeWindow() => throw new InvalidOperationException();
            public void MaximizeWindow() => throw new InvalidOperationException();
            public void Exit() => throw new InvalidOperationException();
            public EngineChange SetEngine(bool on) => throw new InvalidOperationException();
            public void SetAllControllers(bool on) => throw new InvalidOperationException();
            public bool SetController(int number, bool on) => throw new InvalidOperationException();
            public string SaveSnapshot(bool scrubbed) => throw new InvalidOperationException();
            public AutomationState ReadState() => throw new InvalidOperationException();
        }

        private static void OnSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { failure = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The process-wide state a profile switch writes, put back
        /// after each test.</summary>
        private sealed class SavedStatics : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
            private readonly string _active = SettingsManager.ActiveProfileId;
            private readonly ProfileData _default = SettingsManager.PendingDefaultSnapshot;
            private readonly bool _pin = SettingsManager.ExternalProfilePinActive;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
            private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
            private readonly bool _udp = HeadTrackingRuntime.Enabled, _freeTrack = HeadTrackingRuntime.FreeTrackEnabled,
                _openXr = HeadTrackingRuntime.OpenXrEnabled, _telemetry = GameTelemetryRuntime.Enabled;

            public SavedStatics()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.ActiveProfileId = null;
                SettingsManager.PendingDefaultSnapshot = null;
                SettingsManager.ExternalProfilePinActive = false;
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            }

            public void Dispose()
            {
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                SettingsManager.Profiles = _profiles;
                SettingsManager.ActiveProfileId = _active;
                SettingsManager.PendingDefaultSnapshot = _default;
                SettingsManager.ExternalProfilePinActive = _pin;
                SettingsManager.SlotMappingSets = _sets;
                Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
                Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
                HeadTrackingRuntime.Enabled = _udp;
                HeadTrackingRuntime.FreeTrackEnabled = _freeTrack;
                HeadTrackingRuntime.OpenXrEnabled = _openXr;
                GameTelemetryRuntime.Enabled = _telemetry;
            }
        }

        private static (bool Web, bool Motion, bool Mods, bool Telemetry, bool Udp) Five(DashboardViewModel d)
            => (d.EnableWebController, d.EnableDsuMotionServer, d.EnableGameModServer, d.EnableGameTelemetry, d.HeadTrackingEnabled);

        /// <summary>A profile that turns on all five. Don't Allow leaves the
        /// default in force, unheld, with all five off. Allow puts the
        /// profile in force and holds it, with all five on. A second switch
        /// to it starts nothing new and asks nothing.</summary>
        [Fact]
        public void PadForgesOwnSwitch_WaitsForTheCard_AndStaysAsItWasWhenRefused() => OnSta(() =>
        {
            using var statics = new SavedStatics();
            var vm = new MainViewModel();
            var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var s = Strings.Instance;
            SettingsManager.Profiles.Add(new ProfileData
            {
                Id = "racing-1",
                Name = "Racing",
                EnableWebController = true,
                EnableDsuMotionServer = true,
                EnableGameModServer = true,
                EnableGameTelemetry = true,
                EnableHeadTracking = true,
                HeadTrackingIndependentInputs = true,
            });
            SettingsManager.Profiles.Add(new ProfileData { Id = "plain-1", Name = "Plain" });
            using var setup = new Setup(target: new InputTarget(input));

            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            var pending = Assert.Single(setup.Host.Approvals.Pending);
            Assert.Equal(new[] { s.Dashboard_WebController, s.Dashboard_MotionServer, s.Dashboard_GameMods, s.Dashboard_GameTelemetry, s.Automation_Ask_HeadTrackingUdp }
                .Select(AssistantToolWords.TurnsOn).Append(s.Automation_Ask_Network), pending.Request.Detail);
            Assert.True(setup.Host.Approvals.Answer(pending.Id, allowed: false));
            setup.Outcome("error declined");
            Assert.Null(SettingsManager.ActiveProfileId);
            Assert.False(SettingsManager.ExternalProfilePinActive);
            Assert.Equal((false, false, false, false, false), Five(vm.Dashboard));

            // A profile that starts nothing new switches at once.
            Assert.Equal("ok Plain", setup.Line("activate Plain", Launcher));
            Assert.Equal("plain-1", SettingsManager.ActiveProfileId);
            Assert.True(SettingsManager.ExternalProfilePinActive);
            Assert.Empty(setup.Host.Approvals.Pending);

            Assert.Equal(AutomationCommands.Waiting, setup.Line("activate Racing", Launcher));
            Assert.True(setup.Host.Approvals.Answer(Assert.Single(setup.Host.Approvals.Pending).Id, allowed: true));
            setup.RunPosted(1);
            setup.Outcome("ok Racing");
            Assert.Equal("racing-1", SettingsManager.ActiveProfileId);
            Assert.Equal((true, true, true, true, true), Five(vm.Dashboard));
            Assert.Equal("ok Racing", setup.Line("activate Racing", Launcher));
            Assert.Empty(setup.Host.Approvals.Pending);
        });

        /// <summary>The default turns on what it held when a named profile
        /// came into force: a program's deactivate, and its delete of the
        /// profile in force, which puts the default in force too, read that
        /// from the same place (InputService.ExternalNetworkStarts with
        /// null).</summary>
        [Fact]
        public void ReturningToADefaultThatListens_AsksFirst() => OnSta(() =>
        {
            using var statics = new SavedStatics();
            var vm = new MainViewModel();
            var input = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var s = Strings.Instance;
            SettingsManager.Profiles.Add(new ProfileData { Id = "plain-1", Name = "Plain" });
            using var setup = new Setup(target: new InputTarget(input));
            // The default listens: Web Controller is on before any switch.
            vm.Dashboard.EnableWebController = true;
            Assert.True(input.ExternalNetworkStarts(null).Nothing);

            Assert.Equal("ok Plain", setup.Line("activate Plain", Launcher));
            Assert.False(vm.Dashboard.EnableWebController);
            Assert.Equal(new[] { s.Dashboard_WebController }, input.ExternalNetworkStarts(null).Extras);

            Assert.Equal(AutomationCommands.Waiting, setup.Line("deactivate", Launcher));
            Assert.True(setup.Host.Approvals.Answer(Assert.Single(setup.Host.Approvals.Pending).Id, allowed: false));
            setup.Outcome("error declined");
            Assert.Equal("plain-1", SettingsManager.ActiveProfileId);
            Assert.True(SettingsManager.ExternalProfilePinActive);
            Assert.False(vm.Dashboard.EnableWebController);

            Assert.Equal(AutomationCommands.Waiting, setup.Line("deactivate", Launcher));
            Assert.True(setup.Host.Approvals.Answer(Assert.Single(setup.Host.Approvals.Pending).Id, allowed: true));
            setup.RunPosted(1);
            setup.Outcome("ok default");
            Assert.Null(SettingsManager.ActiveProfileId);
            Assert.False(SettingsManager.ExternalProfilePinActive);
            Assert.True(vm.Dashboard.EnableWebController);
        });

        // ── The routes around the command set ──

        /// <summary>A macro a program saves that switches to such a profile
        /// names what that profile turns on, so save_macro's card asks. Next
        /// and Previous name every profile the round can land on. An imported
        /// profile names Head Tracking's UDP input with the other
        /// four.</summary>
        [Fact]
        public void AMacroOrAProfileThatSwitchesToOne_NamesWhatItTurnsOn()
        {
            using var statics = new SavedStatics();
            var s = Strings.Instance;
            SettingsManager.Profiles.Add(new ProfileData { Id = "r", Name = "Racing", EnableWebController = true });
            SettingsManager.Profiles.Add(new ProfileData { Id = "h", Name = "Heads", EnableHeadTracking = true });
            ActionData Step(SwitchProfileMode mode, string name = null)
                => new() { Type = MacroActionType.ProfileShortcut, ProfileShortcutMode = mode, ProfileShortcutName = name };
            MacroData Macro(ActionData step) => new() { Actions = new[] { step } };

            Assert.Equal(new[] { AssistantToolWords.TurnsOn(s.Dashboard_WebController) }, AssistantScan.Macro(Macro(Step(SwitchProfileMode.Specific, "racing"))));
            Assert.Empty(AssistantScan.Macro(Macro(Step(SwitchProfileMode.Specific, "Nowhere"))));
            Assert.Empty(AssistantScan.Macro(Macro(Step(SwitchProfileMode.ToggleWindow))));
            Assert.Equal(new[] { AssistantToolWords.TurnsOn(s.Dashboard_WebController), AssistantToolWords.TurnsOn(s.Automation_Ask_HeadTrackingUdp) },
                AssistantScan.Macro(Macro(Step(SwitchProfileMode.Next))));
            // The default is in the round too.
            SettingsManager.PendingDefaultSnapshot = new ProfileData { EnableGameTelemetry = true };
            Assert.Contains(AssistantToolWords.TurnsOn(s.Dashboard_GameTelemetry), AssistantScan.Macro(Macro(Step(SwitchProfileMode.Previous))));

            // An imported profile, its own switches and its macros' switches.
            var imported = new ProfileData { EnableHeadTracking = true, Macros = new[] { Macro(Step(SwitchProfileMode.Specific, "Racing")) } };
            Assert.Equal(new[] { AssistantToolWords.TurnsOn(s.Dashboard_WebController), AssistantToolWords.TurnsOn(s.Automation_Ask_HeadTrackingUdp) },
                AssistantScan.Profile(imported));
        }

        /// <summary>What a switch would start reads the five against what is
        /// on now: each one on in the profile and off now, and nothing for
        /// one already on, or a profile with no opinion of Head
        /// Tracking.</summary>
        [Fact]
        public void WhatASwitchStarts_IsWhatTheProfileTurnsOnThatIsOffNow() => OnSta(() =>
        {
            var vm = new DashboardViewModel();
            var s = Strings.Instance;
            var all = new ProfileData
            {
                EnableWebController = true, EnableDsuMotionServer = true, EnableGameModServer = true, EnableGameTelemetry = true, EnableHeadTracking = true,
            };
            Assert.Equal(new[] { s.Dashboard_WebController, s.Dashboard_MotionServer, s.Dashboard_GameMods, s.Dashboard_GameTelemetry, s.Automation_Ask_HeadTrackingUdp },
                AssistantScan.NetworkStarts(all, vm));
            Assert.Empty(AssistantScan.NetworkStarts(new ProfileData { EnableHeadTracking = null }, vm));
            Assert.Empty(AssistantScan.NetworkStarts(new ProfileData { EnableHeadTracking = false }, vm));
            bool udp = HeadTrackingRuntime.Enabled, telemetry = GameTelemetryRuntime.Enabled;
            try
            {
                vm.EnableGameTelemetry = true;
                vm.HeadTrackingEnabled = true;
                Assert.Equal(new[] { s.Dashboard_WebController, s.Dashboard_MotionServer, s.Dashboard_GameMods }, AssistantScan.NetworkStarts(all, vm));
            }
            finally
            {
                HeadTrackingRuntime.Enabled = udp;
                GameTelemetryRuntime.Enabled = telemetry;
            }
        });

        // ── A program's change that lets the foreground watcher switch ──

        /// <summary>A program's turning on Switch Profiles by Game names every
        /// listener of every profile with a game, on or off now, and the
        /// default's, which the watcher returns to when the game leaves the
        /// front (#600): a listener turned off after the user allowed it
        /// comes back with the watcher's switch, which asks nothing. The
        /// profile in force and the default in force hold the live setup. A
        /// profile with no game names nothing, nor does the default while no
        /// profile has a game. The switch on already changes nothing. Test
        /// not run yet.</summary>
        [Fact]
        public void TurningOnSwitchingByGame_NamesEveryListenerOfEveryProfileTheWatcherCouldReach() => OnSta(() =>
        {
            using var statics = new SavedStatics();
            var vm = new MainViewModel();
            var s = Strings.Instance;
            const string path = nameof(SettingsViewModel.EnableAutoProfileSwitching);
            string N(string profile, string extra) => AssistantToolWords.ProfileTurnsOn(profile, extra);
            vm.Dashboard.EnableDsuMotionServer = true;
            Assert.Empty(AssistantScan.NetworkLines(path, vm.Settings, vm));

            SettingsManager.Profiles.Add(new ProfileData
            {
                Id = "racing-1", Name = "Racing", ExecutableNames = @"C:\Games\race.exe", EnableWebController = true, EnableDsuMotionServer = true,
            });
            SettingsManager.Profiles.Add(new ProfileData { Id = "plain-1", Name = "Plain", ExecutableNames = @"C:\Games\plain.exe" });
            SettingsManager.Profiles.Add(new ProfileData { Id = "nogame-1", Name = "No Game", EnableGameTelemetry = true });
            SettingsManager.Profiles.Add(new ProfileData
            {
                Id = "flight-1", Name = "Flight", ExecutableNames = @"C:\Games\fly.exe|C:\Games\fly2.exe", EnableHeadTracking = true,
            });

            // Motion Server is on now and still named. The default is in
            // force and holds the live setup.
            Assert.Equal(new[]
            {
                N("Racing", s.Dashboard_WebController), N("Racing", s.Dashboard_MotionServer),
                N("Flight", s.Automation_Ask_HeadTrackingUdp), N(s.Profile_Default, s.Dashboard_MotionServer),
            }, AssistantScan.NetworkLines(path, vm.Settings, vm));

            // Flight in force holds the live setup with its own opinion of
            // Head Tracking's input, and the default holds its snapshot.
            SettingsManager.ActiveProfileId = "flight-1";
            SettingsManager.PendingDefaultSnapshot = new ProfileData { EnableGameTelemetry = true };
            Assert.Equal(new[]
            {
                N("Racing", s.Dashboard_WebController), N("Racing", s.Dashboard_MotionServer),
                N("Flight", s.Dashboard_MotionServer), N("Flight", s.Automation_Ask_HeadTrackingUdp), N(s.Profile_Default, s.Dashboard_GameTelemetry),
            }, AssistantScan.NetworkLines(path, vm.Settings, vm));

            vm.Settings.EnableAutoProfileSwitching = true;
            Assert.Empty(AssistantScan.NetworkLines(path, vm.Settings, vm));
            Assert.Null(AssistantScan.NetworkLines(path, vm.Settings, null));
            Assert.Null(AssistantScan.NetworkLines(nameof(DashboardViewModel.EnableRemoteLink), vm.Dashboard, vm));
        });

        /// <summary>A game added while Switch Profiles by Game is on names what
        /// the profile holds and what the default holds, each with its
        /// profile, on or off now (#600). The same game in another case, or a
        /// game taken off, adds none. An imported profile names what it
        /// holds, and with games while switching the default's too. Test not
        /// run yet.</summary>
        [Fact]
        public void AGameAddedNamesWhatTheProfileAndTheDefaultHold_OnOrOffNow() => OnSta(() =>
        {
            using var statics = new SavedStatics();
            var vm = new MainViewModel();
            var s = Strings.Instance;
            string N(string profile, string extra) => AssistantToolWords.ProfileTurnsOn(profile, extra);
            string[] before = { @"C:\Games\race.exe" };
            Assert.True(AssistantScan.AddsAGame(before, new[] { @"C:\Games\race.exe", @"C:\Games\race2.exe" }));
            Assert.False(AssistantScan.AddsAGame(before, new[] { @"c:\games\RACE.EXE" }));
            Assert.False(AssistantScan.AddsAGame(new[] { @"C:\Games\race.exe", @"C:\Games\race2.exe" }, before));
            Assert.True(AssistantScan.AddsAGame(Array.Empty<string>(), before));

            var racing = new ProfileData { Id = "racing-1", Name = "Racing", EnableGameModServer = true, EnableGameTelemetry = true };
            SettingsManager.Profiles.Add(racing);
            vm.Dashboard.EnableGameModServer = true;
            Assert.Equal(new[] { s.Dashboard_GameMods, s.Dashboard_GameTelemetry }, AssistantScan.HeldExtras(racing, vm.Dashboard));
            Assert.Equal(new[] { N("Racing", s.Dashboard_GameMods), N("Racing", s.Dashboard_GameTelemetry), N(s.Profile_Default, s.Dashboard_GameMods) },
                AssistantScan.WatcherLines("Racing", AssistantScan.HeldExtras(racing, vm.Dashboard), vm.Dashboard));

            SettingsManager.ActiveProfileId = "racing-1";
            SettingsManager.PendingDefaultSnapshot = new ProfileData { EnableWebController = true };
            Assert.Equal(new[] { s.Dashboard_GameMods }, AssistantScan.HeldExtras(racing, vm.Dashboard));
            Assert.Equal(new[] { N(s.Profile_Default, s.Dashboard_WebController) }, AssistantScan.DefaultLines(vm.Dashboard));

            var imported = new ProfileData { Name = "Shooter", EnableDsuMotionServer = true, ExecutableNames = @"C:\Games\shoot.exe" };
            Assert.Equal(new[] { N("Shooter", s.Dashboard_MotionServer) }, AssistantScan.ImportLines(imported, switchingByGame: false, vm.Dashboard));
            Assert.Equal(new[] { N("Shooter", s.Dashboard_MotionServer), N(s.Profile_Default, s.Dashboard_WebController) },
                AssistantScan.ImportLines(imported, switchingByGame: true, vm.Dashboard));
            imported.ExecutableNames = string.Empty;
            Assert.Equal(new[] { N("Shooter", s.Dashboard_MotionServer) }, AssistantScan.ImportLines(imported, true, vm.Dashboard));
        });

        /// <summary>The window hands the reach lines to each route that adds a
        /// profile with games, or a game (#600): Import Profile's Import, Get
        /// Profiles' Save and Save and Apply, and import_profile, and the
        /// window's data to the New and Save As dialogs. Test not run
        /// yet.</summary>
        [Fact]
        public void TheWindowsRoutesThatAddAGame_HandTheirCardsTheReachLines()
        {
            string main = System.IO.File.ReadAllText(System.IO.Path.Combine(ReachMarkup.AppRoot, "MainWindow.xaml.cs"));
            Assert.Contains("PadForge.Common.Reach.SetStarts(dlg.ImportButton,", main);
            Assert.Contains("Services.Automation.AssistantScan.ImportLines(result.Profile, SwitchingByGame(), _viewModel.Dashboard)", main);
            Assert.Contains("PadForge.Common.Reach.SetStarts(dlg.SaveApplyButton, saves);", main);
            Assert.Contains("PadForge.Common.Reach.SetStarts(dlg.SaveButton, saves);", main);
            Assert.Contains("dialog.ForNew(_viewModel);", main);
            Assert.Contains("dialog.ForSaveAs(_viewModel);", main);
            string assistant = System.IO.File.ReadAllText(System.IO.Path.Combine(ReachMarkup.AppRoot, "MainWindow.Assistant.cs"));
            Assert.Contains("AssistantScan.DefaultLines(Vm.Dashboard)", assistant);
        }

        /// <summary>Save Current Setup Here puts the live setup into a stored
        /// profile (#600), and its card names every extra that listens on the
        /// network on now, with the profile, whether or not the profile held
        /// it before. Head Tracking's input stays out, since the live setup
        /// carries no opinion of it. The window's Save Current Setup Here
        /// hands these lines to its confirm. Test not run yet.</summary>
        [Fact]
        public void SaveCurrentSetupHere_NamesEveryListenerTheLiveSetupWouldPutInTheProfile() => OnSta(() =>
        {
            using var statics = new SavedStatics();
            var vm = new MainViewModel();
            var s = Strings.Instance;
            var rally = new ProfileData { Id = "rally-1", Name = "Rally", EnableWebController = true };
            Assert.Empty(AssistantScan.SaveHereLines(rally, vm.Dashboard));
            vm.Dashboard.EnableWebController = true;
            vm.Dashboard.EnableGameModServer = true;
            vm.Dashboard.HeadTrackingEnabled = true;
            Assert.Equal(new[] { AssistantToolWords.ProfileTurnsOn("Rally", s.Dashboard_WebController), AssistantToolWords.ProfileTurnsOn("Rally", s.Dashboard_GameMods) },
                AssistantScan.SaveHereLines(rally, vm.Dashboard));
            Assert.Empty(AssistantScan.SaveHereLines(null, vm.Dashboard));

            string main = System.IO.File.ReadAllText(System.IO.Path.Combine(ReachMarkup.AppRoot, "MainWindow.xaml.cs"));
            int at = main.IndexOf("private void OnSaveSetupHere(", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = main.IndexOf("_inputService.SaveCurrentSetupInto(", at, StringComparison.Ordinal);
            Assert.True(end > at);
            Assert.Contains("starts: () => Services.Automation.AssistantScan.SaveHereLines(into, _viewModel.Dashboard)", main.Substring(at, end - at));
        });
    }
}
