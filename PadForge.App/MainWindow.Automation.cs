using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;

namespace PadForge
{
    /// <summary>
    /// The main window's part in Automation (#597): it makes the host once
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
        /// on is the tray too, as Start Minimized is.</summary>
        internal static CommandLineSwitches.WindowStart FirstWindowState(
            CommandLineSwitches.WindowStart asked, bool startMinimized, bool minimizeToTray)
        {
            switch (asked)
            {
                case CommandLineSwitches.WindowStart.Shown:
                case CommandLineSwitches.WindowStart.Tray:
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
            // The status line names the programs connected.
            _automation.Clients.Changed += (s, e) => Dispatcher.BeginInvoke(new Action(ShowAutomationState));
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
            // and reach the engine while it runs (#597).
            _automation.ProfileName = () => _inputService?.ActiveProfileForState().Name;
            _inputService?.AttachAutomation(_automation);
            // The script libraries PadForge carries, which the page's Save a
            // Library writes.
            _automation.Libraries = AutomationLibraries.Instance;
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
        }

        /// <summary>The host's line for where both its pipes stand: off, on
        /// with no program connected, on with the programs connected by name,
        /// or why programs cannot connect. While the control pipe serves, an
        /// Automation pipe whose name another program holds, or that Windows
        /// refused, is said in place of the programs.</summary>
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
                    if (clients == null || clients.Count == 0) return s.Automation_Status_NoPrograms;
                    var names = new List<string>();
                    foreach (var client in clients)
                    {
                        string name = !string.IsNullOrEmpty(client.Name) ? client.Name
                            : !string.IsNullOrEmpty(client.ImageName) ? client.ImageName
                            : s.Automation_Program_Unknown;
                        if (!names.Contains(name)) names.Add(name);
                    }
                    return string.Format(s.Automation_Status_Connected_Format, string.Join(", ", names));
                case ExternalControlService.PipeState.NameTaken:
                    return string.Format(s.Automation_Status_NameTaken_Format, ExternalControlService.PipeName);
                case ExternalControlService.PipeState.Failed:
                    return string.Format(s.Automation_Status_Failed_Format, ExternalControlService.PipeName);
                default:
                    return s.Common_Stopped;
            }
        }

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

        /// <summary>Shows the window and brings it to the front: the tray
        /// icon's Show, for a second start of PadForge and for the show
        /// command. A window that was only behind another comes forward
        /// too.</summary>
        internal void ShowForACaller()
        {
            RestoreFromTray();
            ForceToForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
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

        /// <summary>Closes PadForge past Close to System Tray: the tray
        /// icon's Exit, and the exit command.</summary>
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

            public string SaveSnapshot() => System.IO.Path.GetFileName(DiagnosticsLogControl.SaveSnapshot());

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
                };
            }
        }
    }
}
