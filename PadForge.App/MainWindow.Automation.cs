using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Engine.Common;

namespace PadForge
{
    /// <summary>
    /// The main window's part in Automation (#600): it makes the host once
    /// the settings are loaded, keeps it in step with the Automation switch,
    /// runs the steps of this copy's own command line, and is what the
    /// command set acts on. Each command goes through the method the
    /// interface's own control calls.
    /// </summary>
    public partial class MainWindow
    {
        private AutomationHost _automation;
        private Views.AutomationRequestCard _requestCard;

        /// <summary>Automation's host, for the parts of the app that serve
        /// outside programs. Null until the settings are loaded.</summary>
        internal AutomationHost Automation => _automation;

        /// <summary>Where the window first is, from what the command line
        /// says and, where it says nothing, the Start Minimized settings.
        /// <c>--tray</c> is the tray whatever the settings say.
        /// <c>--minimized</c> minimizes, which with Minimize to System Tray
        /// on is the tray too, as Start Minimized is. <c>--maximized</c>
        /// shows the window maximized whatever the settings say.</summary>
        internal static CommandLineSwitches.WindowStart FirstWindowState(
            CommandLineSwitches.WindowStart asked, bool startMinimized, bool minimizeToTray)
        {
            switch (asked)
            {
                case CommandLineSwitches.WindowStart.Shown:
                case CommandLineSwitches.WindowStart.Tray:
                case CommandLineSwitches.WindowStart.Maximized:
                    return asked;
                case CommandLineSwitches.WindowStart.Minimized:
                    return minimizeToTray ? CommandLineSwitches.WindowStart.Tray : CommandLineSwitches.WindowStart.Minimized;
                default:
                    if (!startMinimized) return CommandLineSwitches.WindowStart.Shown;
                    return minimizeToTray ? CommandLineSwitches.WindowStart.Tray : CommandLineSwitches.WindowStart.Minimized;
            }
        }

        /// <summary>Makes the host and puts it in step with the settings just
        /// loaded. From here the Automation switch starts and stops it.</summary>
        private void StartAutomationHost()
        {
            _automation = new AutomationHost(new AutomationTarget(this), new DispatcherAutomationUi(Dispatcher));
            _automation.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(ShowAutomationState));
            // The status line and the title bar's mark name the programs
            // connected.
            _automation.Clients.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(ShowAutomationState));
            // The mark hides on Home and on Automation's page (#600 item 13).
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedNavTag)) ShowConnectedMark();
            };
            ExtrasPageView.ExtraShown += (s, e) => ShowConnectedMark();
            // An assistant row and its menu join the catalog through the one
            // seam for live rows (#600 items 1 and 9).
            PadForge.Services.Catalog.LiveCatalog.AddProvider(_viewModel.Dashboard.AssistantCatalogItems);
            // The Automation page's questions, record and the buttons of the
            // parts later builds provide, and the card that asks above other
            // windows from the same list of questions.
            _viewModel.Dashboard.AttachAutomation(_automation, Dispatcher);
            _requestCard = new Views.AutomationRequestCard(_viewModel.Dashboard.AutomationRequests,
                () => new System.Windows.Interop.WindowInteropHelper(this).Handle);
            // Nothing can release a profile a program held once the pipe is
            // gone, so it is let go, and switching by game goes on.
            _automation.TurnedOff += (s, e) => _inputService?.ReleaseExternalPin();
            // The record lines reach the problem recorder, which is joined to
            // the window before this, so a starting copy's --record start
            // finds it at the tail of the engine's start.
            ConnectProblemRecorderToAutomation();
            // The Automation pipe's programs follow the profile by its name
            // and reach the engine while it runs (#600).
            _automation.ProfileName = () => _inputService?.ActiveProfileForState().Name;
            _inputService?.AttachAutomation(_automation);
            // The assistants' server, the call line of scripts, Save the
            // Skill and the Activity card's Undo (#600).
            StartAssistants();
            // Reach PadForge From the Web (#600 item 11).
            StartWebDoor();
            // Answer From Another Device (#600).
            StartAnswerPage();
            _viewModel.Dashboard.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DashboardViewModel.EnableAutomation)
                    or nameof(DashboardViewModel.AutomationLevel))
                    ApplyAutomationSettings();
            };
            // The status line is the host's own words, in the language in
            // use now.
            Strings.CultureChanged += ShowAutomationState;
            ApplyAutomationSettings();
            ShowAutomationState();
        }

        private void ApplyAutomationSettings()
        {
            var dash = _viewModel.Dashboard;
            SettingsManager.EnableAutomation = dash.EnableAutomation;
            SettingsManager.AutomationLevel = dash.AutomationLevel;
            _automation?.Apply(dash.EnableAutomation, dash.AutomationLevel);
        }

        /// <summary>The host's state on the Automation extra: whether it
        /// serves, and its line.</summary>
        private void ShowAutomationState()
        {
            if (_automation == null) return;
            var dash = _viewModel.Dashboard;
            dash.IsAutomationRunning = _automation.IsServing;
            dash.AutomationStatus = AutomationStatusLine(_automation.ControlPipe, _automation.AutomationPipeState, _automation.Clients.Snapshot());
            ShowConnectedMark();
        }

        /// <summary>The host's line for where both its pipes stand: off, on
        /// with no program connected, on with the programs connected by name,
        /// or why programs cannot connect. While the control pipe serves, an
        /// Automation pipe whose name another program holds, or that Windows
        /// refused, is said in place of the programs. A plugin PadForge
        /// started is the Plugins page's to name, not this line's.</summary>
        internal static string AutomationStatusLine(ExternalControlService.PipeState control,
            ExternalControlService.PipeState automation, IReadOnlyList<AutomationClient> clients)
        {
            var s = Strings.Instance;
            switch (control)
            {
                case ExternalControlService.PipeState.Serving:
                    if (automation == ExternalControlService.PipeState.NameTaken)
                        return string.Format(s.Automation_Status_NameTaken_Format, AutomationPipe.PipeName);
                    if (automation == ExternalControlService.PipeState.Failed)
                        return string.Format(s.Automation_Status_Failed_Format, AutomationPipe.PipeName);
                    var names = ConnectedNames(clients, _ => false);
                    if (names.Count == 0) return s.Automation_Status_NoPrograms;
                    return string.Format(s.Automation_Status_Connected_Format, JoinNames(names));
                case ExternalControlService.PipeState.NameTaken:
                    return string.Format(s.Automation_Status_NameTaken_Format, ExternalControlService.PipeName);
                case ExternalControlService.PipeState.Failed:
                    return string.Format(s.Automation_Status_Failed_Format, ExternalControlService.PipeName);
                default:
                    return s.Automation_Status_Off;
            }
        }

        /// <summary>The name a connected program is shown by: an assistant
        /// program's own name when its client names it
        /// (AssistantPrograms.KeyOfClient), else the name it gave, else its
        /// image's.</summary>
        internal static string ClientName(AutomationClient client)
        {
            if (client.Kind == AutomationClientKind.Assistant
                && Services.Install.AssistantPrograms.KeyOfClient(client.Name) is { } key
                && Services.Install.AssistantHosts.Find(key) is { } host)
                return host.Name;
            string name = ShownText.Clean(!string.IsNullOrEmpty(client.Name) ? client.Name : client.ImageName);
            return string.IsNullOrEmpty(name) ? Strings.Instance.Automation_Program_Unknown : name;
        }

        /// <summary>The names of the programs connected, each once, in the
        /// order they connected: those from the web with
        /// <paramref name="web"/>, the others without. Plugins are left
        /// out.</summary>
        internal static List<string> ConnectedNames(IReadOnlyList<AutomationClient> clients, Func<AutomationClient, bool> fromWeb, bool web = false)
        {
            var names = new List<string>();
            foreach (var client in clients ?? Array.Empty<AutomationClient>())
            {
                if (client.Kind == AutomationClientKind.Plugin) continue;
                if (fromWeb(client) != web) continue;
                string name = ClientName(client);
                if (!names.Contains(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>Names as a sentence lists them: A, A and B, A, B and
        /// C, with the language's own words between.</summary>
        internal static string JoinNames(IReadOnlyList<string> names)
        {
            var s = Strings.Instance;
            if (names == null || names.Count == 0) return string.Empty;
            string joined = names[names.Count - 1];
            if (names.Count >= 2) joined = string.Format(s.Automation_Names_Two_Format, names[names.Count - 2], joined);
            for (int i = names.Count - 3; i >= 0; i--) joined = string.Format(s.Automation_Names_List_Format, names[i], joined);
            return joined;
        }

        // ─────────────────────────────────────────────
        //  The connected mark (#600 item 13)
        // ─────────────────────────────────────────────

        /// <summary>Whether a connected program came from the web: a web
        /// assistant through the web helper (#600 item 11).</summary>
        internal static Func<AutomationClient, bool> ClientIsFromWeb = client => client.Kind == AutomationClientKind.Web;

        /// <summary>What the title bar's mark shows: whether it shows, the
        /// count, whether the globe shows, and its words for hover and a
        /// screen reader, and whether the network mark shows.</summary>
        internal readonly record struct MarkState(bool Shows, int Count, bool Web, string Words, bool Network = false);

        /// <summary>The mark shows while a program other than a plugin is
        /// connected, on every page but Home and Automation's, which name the
        /// programs on a card.</summary>
        internal static MarkState ConnectedMarkState(IReadOnlyList<AutomationClient> clients, Func<AutomationClient, bool> fromWeb,
            string navTag, string extra)
        {
            var s = Strings.Instance;
            var local = ConnectedNames(clients, fromWeb);
            var web = ConnectedNames(clients, fromWeb, web: true);
            // The count is of programs connected, two copies of one script
            // two, and the words name each once.
            int count = (clients ?? Array.Empty<AutomationClient>()).Count(c => c.Kind != AutomationClientKind.Plugin);
            if (count == 0) return new MarkState(false, 0, false, null);
            bool named = navTag == "Home" || (navTag == "Extras" && extra == "Automation");
            string words = web.Count == 0 ? string.Format(s.Main_ConnectedMark_Format, JoinNames(local))
                : local.Count == 0 ? string.Format(s.Main_ConnectedMark_WebOnly_Format, JoinNames(web))
                : string.Format(s.Main_ConnectedMark_Web_Format, JoinNames(local), JoinNames(web));
            // An assistant on this network (#600 item 53) is named with its
            // address among the others, and the mark carries the network
            // glyph while one is connected, as the globe marks the web.
            bool network = (clients ?? Array.Empty<AutomationClient>()).Any(c => c.Kind == AutomationClientKind.Network);
            return new MarkState(!named, count, web.Count > 0, words, network);
        }

        /// <summary>Puts the mark in step with the programs connected and the
        /// page shown.</summary>
        private void ShowConnectedMark()
        {
            if (_automation == null) return;
            var state = ConnectedMarkState(_automation.Clients.Snapshot(), ClientIsFromWeb, _viewModel.SelectedNavTag,
                ExtrasPageView.Visibility == Visibility.Visible ? ExtrasPageView.CurrentExtra : null);
            ConnectedMark.Visibility = state.Shows ? Visibility.Visible : Visibility.Collapsed;
            if (state.Count == 0) return;
            // E839 is the board's mark for programs on this PC, E774 the
            // globe, EC27 the network's PCs.
            ConnectedMarkGlyph.Text = state.Web ? "" : state.Network ? "" : "";
            ConnectedMarkCount.Text = state.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ConnectedMark.ToolTip = state.Words;
            System.Windows.Automation.AutomationProperties.SetName(ConnectedMark, state.Words);
        }

        /// <summary>The mark's click opens Automation's page.</summary>
        private void ConnectedMark_Click(object sender, RoutedEventArgs e) => OpenExtra("Automation");

        /// <summary>Runs the steps of this copy's own command line that
        /// belong to <paramref name="phase"/>, in the order given. They are
        /// the user's own start, so Automation is not asked.</summary>
        private void RunStartSteps(CommandLineSwitches.StartPhase phase)
        {
            if (_automation == null) return;
            foreach (var step in App.StartupRequest.Steps)
                if (step.Phase == phase) _automation.Commands.RunOwn(step.Line);
        }

        // ─────────────────────────────────────────────
        //  What a command does, each through the interface's own way
        // ─────────────────────────────────────────────

        private readonly CallerShow _callerShow = new CallerShow();

        /// <summary>Shows the window and brings it to the front for a second
        /// start of PadForge, the show message and the show command. A window
        /// that was only behind another comes forward too. A show within two
        /// seconds of the last is ignored, the front is taken with the right
        /// the caller lent, and where none was lent the taskbar button
        /// flashes (CallerShow). A window in its shutdown is never brought
        /// back (RestoreFromTray), and takes the front for nobody.</summary>
        internal void ShowForACaller()
        {
            if (_shutdownStarted) return;
            if (!_callerShow.Take()) return;
            RestoreFromTray(forceForward: false);
            CallerShow.ComeForward(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        /// <summary><c>maximize</c> (#600 item 44): the same show, with the
        /// same gap and the same lent right, and the window maximized. A
        /// show that does not go ahead maximizes nothing, so the window
        /// never comes forward by another way, and a window in its shutdown
        /// is neither maximized nor brought forward.</summary>
        internal void MaximizeForACaller()
        {
            if (_shutdownStarted) return;
            if (!_callerShow.Take()) return;
            RestoreFromTray(forceForward: false);
            WindowState = WindowState.Maximized;
            CallerShow.ComeForward(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        /// <summary>Sends the window to the tray and puts the tray icon up,
        /// with a pending text edit kept and the settings saved, while the
        /// engine and the timers run on. Close to System Tray and the hide
        /// command both come here.</summary>
        private void HideToTray()
        {
            // Preserve a pending text edit while keeping the engine and timers alive.
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox trayTextBox)
                trayTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (_settingsService.IsDirty) _settingsService.Save();
            _notifyIcon.Visible = true;
            Hide();
        }

        /// <summary>The signal's exit (#600 item 44): a copy of the exe
        /// started with <c>--exit</c> asks for the tray icon's Exit with no
        /// Automation, over the signal pipe or, with rights, the signal
        /// window. Activity keeps it with its program unknown, since the ask
        /// names none. The close runs after the ask's own handling has
        /// returned.</summary>
        internal void ExitForACaller()
        {
            _automation?.NoteExitBySignal();
            Dispatcher.BeginInvoke(new Action(ExitFromTray));
        }

        /// <summary>Closes PadForge past Close to System Tray: the tray
        /// icon's Exit, the exit command and the signal's exit.</summary>
        private void ExitFromTray()
        {
            PrepareForExit();
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            Close();
        }

        /// <summary>Home's engine button: stops a running engine and starts
        /// a stopped one.</summary>
        private void ToggleEngine()
        {
            if (_viewModel.IsEngineRunning) StopEngine();
            else StartEngine();
        }

        /// <summary>Starts the engine and draws the sidebar's controllers
        /// again.</summary>
        private void StartEngine()
        {
            _inputService.Start();
            // Force full sidebar card rebuild. Engine state affects power icon colors
            // but isn't a NavControllerItemViewModel property, so in-place updates miss it.
            RebuildControllerSection();
        }

        /// <summary>Stops the engine without holding the interface's thread:
        /// the stop runs on a worker, and its teardown calls back into this
        /// thread, so a caller here must never wait for it.</summary>
        private void StopEngine()
        {
            // Show "Stopping" state immediately, then run the heavy
            // engine teardown on a thread-pool task so the UI stays
            // responsive while AwaitPendingLifecycleTasks +
            // DestroyAllVirtualControllers + HMContext.Dispose
            // grind through (multi-second per Microsoft xinputhid
            // VC).  Sidebar rebuild fires when the teardown
            // completes. The task is kept so a removal of
            // HIDMaestro can wait out a teardown still in flight.
            ShowEngineStopping();
            _engineStopTask = System.Threading.Tasks.Task.Run(() => _inputService.Stop());
            _engineStopTask.ContinueWith(_ => RebuildControllerSection(),
                System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>The engine's state as the state line gives it. A stop
        /// Home's button began reads as stopping before its worker has
        /// reached the engine.</summary>
        private AutomationEngineState ReadEngineState()
        {
            if (_engineStopTask is { IsCompleted: false }) return AutomationEngineState.Stopping;
            return _inputService.ReadEngineState();
        }

        /// <summary>Turns every created virtual controller on or off.
        /// DeviceService.SetSlotEnabled is the canonical setter. Calling it
        /// per-slot keeps SettingsManager, SettingsService.MarkDirty, and the
        /// Step5 dispatcher reconciliation path all wired up the same way the
        /// per-slot sidebar power toggle uses. Uncreated slots are not
        /// touched.</summary>
        private void SetAllControllersEnabled(bool on)
        {
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                if (SettingsManager.SlotEnabled[i] == on) continue;
                _deviceService.SetSlotEnabled(i, on);
            }
            RefreshSlotPowerViews();
        }

        /// <summary>Bulk VC enable/disable from a profile-shortcut combo
        /// (#91). Rule: if any created slot is currently enabled, disable all
        /// created slots. Otherwise (all already disabled) enable all.</summary>
        private void ToggleAllControllersEnabled()
        {
            bool anyEnabled = false;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (SettingsManager.SlotCreated[i] && SettingsManager.SlotEnabled[i])
                {
                    anyEnabled = true;
                    break;
                }
            }
            SetAllControllersEnabled(!anyEnabled);
        }

        /// <summary>Opens Extras on one extra's page, as a Search result for
        /// that extra does. Nothing happens for a key that has no page.</summary>
        private void OpenExtra(string key)
        {
            if (!ExtrasPageView.HasPage(key)) return;
            SelectNavItemByTag("Extras");
            ExtrasPageView.Show(key);
        }

        /// <summary>The app, as the command set acts on it.</summary>
        private sealed class AutomationTarget : IAutomationTarget
        {
            private readonly MainWindow _window;

            public AutomationTarget(MainWindow window) => _window = window;

            public string ActivateProfile(string nameOrId) => _window._inputService.ExternalActivate(nameOrId);

            public string DeactivateProfile() => _window._inputService.ExternalDeactivate();

            public ProfileStarts NetworkStarts(string nameOrId) => _window._inputService.ExternalNetworkStarts(nameOrId);

            public string QueryProfile() => _window._inputService.ExternalQuery();

            public void ShowWindow() => _window.ShowForACaller();

            public bool HideToTray()
            {
                // With no icon in the tray there would be no way back to a
                // running app, the reason Close to System Tray asks the same.
                if (_window._notifyIcon?.Icon == null) return false;
                _window.HideToTray();
                return true;
            }

            public void MinimizeWindow() => _window.WindowState = WindowState.Minimized;

            public void MaximizeWindow() => _window.MaximizeForACaller();

            public void Exit() => _window.ExitFromTray();

            public EngineChange SetEngine(bool on)
            {
                var state = _window.ReadEngineState();
                if (!on)
                {
                    if (state is AutomationEngineState.Running or AutomationEngineState.Idle) _window.StopEngine();
                    return EngineChange.Done;
                }
                if (state == AutomationEngineState.Stopping) return EngineChange.Busy;
                if (state == AutomationEngineState.Stopped) _window.StartEngine();
                return _window.ReadEngineState() is AutomationEngineState.Running or AutomationEngineState.Idle
                    ? EngineChange.Done
                    : EngineChange.Failed;
            }

            public void SetAllControllers(bool on) => _window.SetAllControllersEnabled(on);

            public bool SetController(int number, bool on)
            {
                int padIndex = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(number);
                if (padIndex < 0) return false;
                _window._deviceService.SetSlotEnabled(padIndex, on);
                _window.RefreshSlotPowerViews();
                return true;
            }

            public string SaveSnapshot() => DiagnosticsLogControl.SaveSnapshot();

            public AutomationState ReadState()
            {
                var (name, id) = _window._inputService.ActiveProfileForState();
                var controllers = new List<AutomationControllerState>();
                // The interface's own order and numbers: the type groups in
                // their fixed order, each in the order the user gave it
                // (RefreshDashboardActiveSlots).
                foreach (var group in Engine.VirtualControllerGroups.InOrder)
                {
                    foreach (int padIndex in SettingsManager.SlotOrders.GetOrderFor(group))
                    {
                        if (padIndex < 0 || padIndex >= InputManager.MaxPads) continue;
                        if (!SettingsManager.SlotCreated[padIndex]) continue;
                        var pad = _window._viewModel.Pads[padIndex];
                        var (assigned, online) = InputService.CountSlotDevices(padIndex);
                        controllers.Add(new AutomationControllerState(controllers.Count + 1, pad.PresetDisplayName,
                            pad.OutputType, SettingsManager.SlotEnabled[padIndex], assigned, online));
                    }
                }
                return new AutomationState
                {
                    Engine = _window.ReadEngineState(),
                    ProfileName = name,
                    ProfileId = id,
                    ProfilePinned = SettingsManager.ExternalProfilePinActive,
                    Window = !_window.IsVisible ? AutomationWindowState.Tray
                        : _window.WindowState == WindowState.Minimized ? AutomationWindowState.Minimized
                        : AutomationWindowState.Shown,
                    Controllers = controllers,
                    StopsBehind = _window._viewModel.Settings?.EnablePollingOnFocusLoss == false,
                };
            }
        }
    }
}
