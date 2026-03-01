using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using ModernWpf.Controls;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge
{
    /// <summary>
    /// MainWindow code-behind. Wires navigation, creates services, manages
    /// the application lifecycle (engine start/stop on window open/close).
    /// </summary>
    public partial class MainWindow
    {
        private readonly MainViewModel _viewModel;
        private InputService _inputService;
        private SettingsService _settingsService;
        private RecorderService _recorderService;
        private DeviceService _deviceService;
        private Popup _controllerTypePopup;
        private DateTime _popupClosedAt;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Threading.DispatcherTimer _driverStatusTimer;
        private bool _previousViGEmInstalled;
        private bool _previousVJoyInstalled;

        // Drag reorder state for sidebar controller cards.
        private Point _cardDragStartPoint;
        private System.Windows.Controls.Border _cardDragSource;
        private CardDragAdorner _dragAdorner;
        private InsertionLineAdorner _insertionAdorner;
        private System.Windows.Documents.AdornerLayer _dragAdornerLayer;

        public MainWindow()
        {
            InitializeComponent();

            // Create root ViewModel.
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Set child DataContexts.
            DashboardPageView.DataContext = _viewModel.Dashboard;
            DevicesPageView.DataContext = _viewModel.Devices;
            SettingsPageView.DataContext = _viewModel.Settings;
            ProfilesPageView.DataContext = _viewModel.Settings;

            // Create services.
            _settingsService = new SettingsService(_viewModel);
            _inputService = new InputService(_viewModel);
            _recorderService = new RecorderService(_viewModel);
            _deviceService = new DeviceService(_viewModel, _settingsService);

            // Wire driver uninstall guards — lambda queries the ViewModel's Pads for active slot types.
            _viewModel.Settings.HasAnyViGEmSlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        (_viewModel.Pads[i].OutputType == VirtualControllerType.Xbox360 ||
                         _viewModel.Pads[i].OutputType == VirtualControllerType.DualShock4))
                        return true;
                return false;
            };
            _viewModel.Settings.HasAnyVJoySlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        _viewModel.Pads[i].OutputType == VirtualControllerType.VJoy)
                        return true;
                return false;
            };

            // Wire engine start/stop commands.
            _viewModel.StartEngineRequested += (s, e) => _inputService.Start();
            _viewModel.StopEngineRequested += (s, e) => _inputService.Stop();

            // Wire settings commands.
            _viewModel.Settings.SaveRequested += (s, e) =>
            {
                _settingsService.Save();
                // Refresh default snapshot so future profile reverts use the latest saved state.
                if (SettingsManager.ActiveProfileId == null)
                    _inputService.RefreshDefaultSnapshot();
            };
            _settingsService.AutoSaved += (s, e) =>
            {
                if (SettingsManager.ActiveProfileId == null)
                    _inputService.RefreshDefaultSnapshot();
            };
            _viewModel.Settings.ReloadRequested += (s, e) => _settingsService.Reload();
            _viewModel.Settings.ResetRequested += (s, e) => _settingsService.ResetToDefaults();
            _viewModel.Settings.OpenSettingsFolderRequested += OnOpenSettingsFolder;
            _viewModel.Settings.ThemeChanged += OnThemeChanged;
            _viewModel.Settings.NewProfileRequested += OnNewProfile;
            _viewModel.Settings.SaveAsProfileRequested += OnSaveAsProfile;
            _viewModel.Settings.DeleteProfileRequested += OnDeleteProfile;
            _viewModel.Settings.EditProfileRequested += OnEditProfile;
            _viewModel.Settings.LoadProfileRequested += OnLoadProfile;
            _viewModel.Settings.RevertToDefaultRequested += OnRevertToDefault;

            // Apply registry Run key when Start at Login is toggled.
            _viewModel.Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.StartAtLogin))
                    Common.StartupHelper.SetStartupEnabled(_viewModel.Settings.StartAtLogin);
            };

            // Persist DSU server settings on change (now on Dashboard VM).
            _viewModel.Dashboard.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DashboardViewModel.EnableDsuMotionServer)
                     or nameof(DashboardViewModel.DsuMotionServerPort))
                    _settingsService.MarkDirty();
            };

            // Wire ViGEm install/uninstall commands.
            _viewModel.Settings.InstallViGEmRequested += async (s, e) => await RunDriverOperationAsync(
                "Installing ViGEmBus…", DriverInstaller.InstallViGEmBus, RefreshViGEmStatus);
            _viewModel.Settings.UninstallViGEmRequested += async (s, e) => await RunDriverOperationAsync(
                "Uninstalling ViGEmBus…", DriverInstaller.UninstallViGEmBus, RefreshViGEmStatus);

            // Wire HidHide install/uninstall commands.
            _viewModel.Settings.InstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                "Installing HidHide…", DriverInstaller.InstallHidHide, RefreshHidHideStatus);
            _viewModel.Settings.UninstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                "Uninstalling HidHide…", DriverInstaller.UninstallHidHide, RefreshHidHideStatus);

            // Wire vJoy install/uninstall commands.
            _viewModel.Settings.InstallVJoyRequested += async (s, e) => await RunDriverOperationAsync(
                "Installing vJoy…", DriverInstaller.InstallVJoy, OnVJoyDriverChanged);
            _viewModel.Settings.UninstallVJoyRequested += async (s, e) => await RunDriverOperationAsync(
                "Uninstalling vJoy…", DriverInstaller.UninstallVJoy, OnVJoyDriverChanged);

            // Wire device service events (assign to slot, hide, etc.).
            _deviceService.WireEvents();

            // Refresh PadPage dropdowns and Devices-page slot buttons after assignment changes.
            _deviceService.DeviceAssignmentChanged += (s, e) =>
            {
                _inputService.RefreshDeviceList();
                _viewModel.Devices.RefreshSlotButtons();
            };

            // After assigning a device to a slot, navigate to that controller page.
            _deviceService.NavigateToSlotRequested += (s, slotIndex) => NavigateToSlot(slotIndex);

            // Wire devices page refresh.
            _viewModel.Devices.RefreshRequested += (s, e) =>
            {
                _inputService.RefreshDeviceList();
                _viewModel.StatusText = "Device list refreshed.";
            };

            // Wire test rumble for each pad (both motors, or individual).
            foreach (var pad in _viewModel.Pads)
            {
                pad.TestRumbleRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid);
                };
                pad.TestLeftMotorRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid, true, false);
                };
                pad.TestRightMotorRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid, false, true);
                };
            }

            // Wire recorder for each pad's mapping rows.
            foreach (var pad in _viewModel.Pads)
            {
                foreach (var mapping in pad.Mappings)
                {
                    var capturedPad = pad;
                    mapping.StartRecordingRequested += (s, e) =>
                    {
                        if (s is MappingItem mi)
                        {
                            capturedPad.CurrentRecordingTarget = mi.TargetSettingName;
                            Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                            _recorderService.StartRecording(mi, capturedPad.PadIndex, deviceGuid);
                        }
                    };
                    mapping.StopRecordingRequested += (s, e) =>
                        _recorderService.CancelRecording();

                    // Mapping descriptor changes (inversion, half-axis, source) trigger autosave.
                    mapping.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName is nameof(MappingItem.SourceDescriptor)
                            or nameof(MappingItem.IsInverted)
                            or nameof(MappingItem.IsHalfAxis))
                            _settingsService.MarkDirty();
                    };
                }

                // Pad setting changes (dead zones, force feedback, etc.) trigger autosave.
                pad.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is
                        nameof(PadViewModel.LeftDeadZoneX) or nameof(PadViewModel.LeftDeadZoneY) or
                        nameof(PadViewModel.RightDeadZoneX) or nameof(PadViewModel.RightDeadZoneY) or
                        nameof(PadViewModel.LeftAntiDeadZoneX) or nameof(PadViewModel.LeftAntiDeadZoneY) or
                        nameof(PadViewModel.RightAntiDeadZoneX) or nameof(PadViewModel.RightAntiDeadZoneY) or
                        nameof(PadViewModel.LeftLinear) or nameof(PadViewModel.RightLinear) or
                        nameof(PadViewModel.LeftTriggerDeadZone) or nameof(PadViewModel.RightTriggerDeadZone) or
                        nameof(PadViewModel.LeftTriggerAntiDeadZone) or nameof(PadViewModel.RightTriggerAntiDeadZone) or
                        nameof(PadViewModel.LeftTriggerMaxRange) or nameof(PadViewModel.RightTriggerMaxRange) or
                        nameof(PadViewModel.ForceOverallGain) or nameof(PadViewModel.LeftMotorStrength) or
                        nameof(PadViewModel.RightMotorStrength) or nameof(PadViewModel.SwapMotors) or
                        nameof(PadViewModel.OutputType))
                        _settingsService.MarkDirty();
                };
            }

            // Recorder completion marks settings dirty + clear flash + advance Map All.
            _recorderService.RecordingCompleted += (s, result) =>
            {
                _settingsService.MarkDirty();
                var activePad = _viewModel.SelectedPad;
                if (activePad != null)
                {
                    // Resolve human-friendly name for the recorded mapping.
                    Guid deviceGuid = activePad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                    if (deviceGuid != Guid.Empty)
                        InputService.ResolveDisplayText(result.Mapping, deviceGuid);

                    // Update status with resolved name.
                    _viewModel.StatusText = $"Recorded \"{result.Mapping.TargetLabel}\" \u2190 {result.Mapping.SourceDisplayText}";

                    if (activePad.IsMapAllActive)
                        activePad.OnMapAllItemCompleted();
                    else
                        activePad.CurrentRecordingTarget = null;
                }
            };

            // Recording timeout clears flash + advances Map All.
            _recorderService.RecordingTimedOut += (s, e) =>
            {
                var activePad = _viewModel.SelectedPad;
                if (activePad != null)
                {
                    if (activePad.IsMapAllActive)
                        activePad.OnMapAllItemCompleted();
                    else
                        activePad.CurrentRecordingTarget = null;
                }
            };

            // Wire click-to-record from controller visual elements.
            PadPageView.ControllerElementRecordRequested += (s, targetName) =>
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm == null) return;

                var mapping = padVm.Mappings.FirstOrDefault(m =>
                    string.Equals(m.TargetSettingName, targetName, StringComparison.OrdinalIgnoreCase));
                if (mapping == null) return;

                padVm.CurrentRecordingTarget = targetName;
                Guid deviceGuid = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                _recorderService.StartRecording(mapping, padVm.PadIndex, deviceGuid);
            };

            // Wire Map All events for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;
                pad.MapAllRecordRequested += (s, mapping) =>
                {
                    Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                    _recorderService.StartRecording(mapping, capturedPad.PadIndex, deviceGuid);
                };
                pad.MapAllCancelRequested += (s, e) =>
                    _recorderService.CancelRecording();
            }

            // Wire macro trigger recording for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;

                // Wire existing macros.
                foreach (var macro in pad.Macros)
                    WireMacroRecording(macro, capturedPad.PadIndex);

                // Wire macros added later.
                pad.Macros.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MacroItem macro in e.NewItems)
                            WireMacroRecording(macro, capturedPad.PadIndex);
                    }
                };
            }

            // Wire copy/paste/copy-from for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;
                pad.CopySettingsRequested += (s, e) => OnCopySettings(capturedPad);
                pad.PasteSettingsRequested += (s, e) => OnPasteSettings(capturedPad);
                pad.CopyFromRequested += (s, e) => OnCopyFrom(capturedPad);
            }

            // Build the sidebar navigation items dynamically.
            BuildNavigationItems();

            // Wire Dashboard "Add Controller" to show type-selection popup.
            DashboardPageView.AddControllerRequested += (s, e) =>
            {
                ShowControllerTypePopup(DashboardPageView.AddControllerCardElement, PlacementMode.Bottom);
            };

            // Wire Dashboard delete + toggle events.
            DashboardPageView.DeleteSlotRequested += (s, slotIndex) =>
            {
                // Deferred — DeleteSlot fires DeviceAssignmentChanged → RebuildControllerSection.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_viewModel.SelectedPadIndex == slotIndex)
                        SelectNavItemByTag("Dashboard");

                    _deviceService.DeleteSlot(slotIndex);
                    _viewModel.Devices.RefreshSlotButtons();
                    _inputService.RefreshDeviceList();
                }));
            };

            DashboardPageView.SlotEnabledToggled += (s, args) =>
            {
                _deviceService.SetSlotEnabled(args.SlotIndex, args.IsEnabled);
                // Refresh sidebar so power button updates.
                _viewModel.RefreshNavControllerItems();
            };

            DashboardPageView.EngineToggleRequested += (s, e) =>
            {
                if (_viewModel.IsEngineRunning)
                    _inputService.Stop();
                else
                    _inputService.Start();
                // Force full sidebar card rebuild — engine state affects power icon colors
                // but isn't a NavControllerItemViewModel property, so in-place updates miss it.
                RebuildControllerSection();
            };

            DashboardPageView.SlotTypeChangeRequested += (s, args) =>
            {
                // Device nodes are created on demand by the engine (CreateVJoyController)
                // when the slot becomes active — same pattern as ViGEm.
                _viewModel.Pads[args.SlotIndex].OutputType = args.Type;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
                _inputService.RefreshDeviceList();
                _viewModel.Devices.RefreshSlotButtons();
            };

            DashboardPageView.SlotSwapRequested += (s, args) =>
            {
                _inputService.SwapSlots(args.PadIndexA, args.PadIndexB);
                _settingsService.MarkDirty();
            };

            DashboardPageView.SlotMoveRequested += (s, args) =>
            {
                _inputService.MoveSlot(args.SourcePadIndex, args.TargetVisualPos);
                _settingsService.MarkDirty();
            };

            // Window events.
            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += OnStateChanged;

            // ── Early initialization (before window is shown) ──
            // Settings must be loaded before Show() so App.OnStartup can
            // decide whether to show the window at all (start-minimized-to-tray).
            _settingsService.Initialize();

            // Sync StartAtLogin with actual registry state (user may have removed it externally).
            _viewModel.Settings.StartAtLogin = Common.StartupHelper.IsStartupEnabled();

            SetupNotifyIcon();

            // Expose start-minimized state for App.OnStartup.
            ShouldStartMinimized = _viewModel.Settings.StartMinimized;
            ShouldStartMinimizedToTray = _viewModel.Settings.StartMinimized
                && _viewModel.Settings.MinimizeToTray;

            // If starting minimized to tray, make the tray icon visible now.
            if (ShouldStartMinimizedToTray)
                _notifyIcon.Visible = true;

            // Enforce type-group ordering (Xbox 360 > DS4 > vJoy) on startup.
            // Handles backward-compat with old configs that have interleaved types.
            if (_inputService.EnsureTypeGroupOrder(silent: true))
                _settingsService.MarkDirty();

            // Populate sidebar and dashboard with saved slots regardless of engine state,
            // so virtual controllers are visible for configuration even when the engine is off.
            _viewModel.RefreshNavControllerItems();
            RefreshDashboardActiveSlots();

            // vJoy device nodes are created on demand by the engine (CreateVJoyController)
            // when slots become active — same pattern as ViGEm. No pre-creation needed.
            if (_viewModel.Settings.AutoStartEngine)
                _inputService.Start();
        }

        /// <summary>Whether the app should start minimized (to taskbar).</summary>
        public bool ShouldStartMinimized { get; private set; }

        /// <summary>Whether the app should start hidden to the system tray.</summary>
        public bool ShouldStartMinimizedToTray { get; private set; }

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Settings and tray icon are already initialized in the constructor
            // (before Show) so that App.OnStartup can decide whether to show the window.

            // Populate diagnostic info.
            _viewModel.Settings.ApplicationVersion =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            _viewModel.Settings.RuntimeVersion = Environment.Version.ToString();

            // Detect ViGEmBus driver.
            RefreshViGEmStatus();
            _previousViGEmInstalled = _viewModel.Dashboard.IsViGEmInstalled;

            // Detect HidHide driver.
            RefreshHidHideStatus();

            // Detect vJoy driver.
            RefreshVJoyStatus();
            _previousVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;

            // Periodically refresh driver install states (every 5 seconds).
            _driverStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _driverStatusTimer.Tick += (s, ev) =>
            {
                bool wasViGEmInstalled = _previousViGEmInstalled;
                RefreshViGEmStatus();
                RefreshHidHideStatus();
                RefreshVJoyStatus();

                bool nowViGEmInstalled = _viewModel.Dashboard.IsViGEmInstalled;
                _previousViGEmInstalled = nowViGEmInstalled;

                // ViGEm installed mid-session: restart engine to recreate ViGEmClient and virtual controllers.
                if (!wasViGEmInstalled && nowViGEmInstalled && _viewModel.IsEngineRunning)
                {
                    _inputService.Stop();
                    _inputService.Start();
                    _viewModel.StatusText = "ViGEmBus detected — engine restarted.";
                }

                // ViGEm status changed: force full sidebar rebuild for power icon colors.
                if (wasViGEmInstalled != nowViGEmInstalled)
                    RebuildControllerSection();

                // vJoy installed/reinstalled mid-session: reset cached state and restart engine.
                bool wasVJoyInstalled = _previousVJoyInstalled;
                bool nowVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;
                _previousVJoyInstalled = nowVJoyInstalled;

                if (wasVJoyInstalled != nowVJoyInstalled)
                {
                    // Always reset cached DLL/registry state on any install status change.
                    PadForge.Common.Input.VJoyVirtualController.ResetState();

                    if (nowVJoyInstalled && _viewModel.IsEngineRunning)
                    {
                        _inputService.Stop(preserveVJoyNodes: true);
                        _inputService.Start();
                        _viewModel.StatusText = "vJoy driver detected — engine restarted.";
                    }
                }
            };
            _driverStatusTimer.Start();

            // Check SDL3.dll availability.
            try
            {
                var sdlVersion = SDL3.SDL.SDL_Linked_Version();
                _viewModel.Settings.SdlVersion = $"SDL {sdlVersion.major}.{sdlVersion.minor}.{sdlVersion.patch}";
            }
            catch (DllNotFoundException)
            {
                _viewModel.Settings.SdlVersion = "SDL3.dll NOT FOUND";
                _viewModel.StatusText = "SDL3.dll not found! Place SDL3.dll next to PadForge.exe. " +
                    "Download from https://github.com/libsdl-org/SDL/releases";
            }
            catch
            {
                _viewModel.Settings.SdlVersion = "Unknown";
            }

            // Select the first nav item.
            if (NavView.MenuItems.Count > 0)
                NavView.SelectedItem = NavView.MenuItems[0];
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Save settings if dirty.
            if (_settingsService.IsDirty)
            {
                _settingsService.Save();
            }

            // Stop driver status polling.
            _driverStatusTimer?.Stop();
            _driverStatusTimer = null;

            // Dispose tray icon.
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            // Unwire device service.
            _deviceService?.UnwireEvents();

            // Stop engine and dispose services.
            _recorderService?.Dispose();
            _inputService?.Dispose();
        }

        // ─────────────────────────────────────────────
        //  Navigation
        // ─────────────────────────────────────────────

        // SVG path data for controller type icons — shared via ControllerIcons static class.
        private const string XboxSvgPath = Common.ControllerIcons.XboxSvgPath;
        private const string DS4SvgPath = Common.ControllerIcons.DS4SvgPath;
        private const string VJoySvgPath = Common.ControllerIcons.VJoySvgPath;

        /// <summary>Index in NavView.MenuItems where the first controller entry goes (after Dashboard, Profiles, Devices).</summary>
        private const int ControllerInsertIndex = 3;

        /// <summary>Re-entrancy guard for <see cref="RebuildControllerSection"/>.</summary>
        private bool _rebuildingControllerSection;

        /// <summary>
        /// Programmatically builds the NavigationView menu items.
        /// Static items: Dashboard, separators, Devices, Profiles.
        /// Dynamic items: controller entries + "Add" button, rebuilt when NavControllerItems changes.
        /// </summary>
        private void BuildNavigationItems()
        {
            // Card drag-reorder: NavView-level handlers for threshold, movement, and drop.
            NavView.PreviewMouseMove += OnNavViewDragMove;
            NavView.PreviewMouseLeftButtonUp += OnNavViewDragEnd;
            NavView.PreviewKeyDown += OnNavViewDragKeyDown;

            NavView.MenuItems.Clear();

            // Dashboard.
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Dashboard",
                Tag = "Dashboard",
                Icon = new SymbolIcon(Symbol.Home)
            });

            // Profiles.
            var profiles = new NavigationViewItem
            {
                Tag = "Profiles",
                Icon = new FontIcon { Glyph = "\uE8F1" },
                Content = "Profiles"
            };
            NavView.MenuItems.Add(profiles);

            // Devices.
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Devices",
                Tag = "Devices",
                Icon = new SymbolIcon(Symbol.AllApps)
            });

            // Controller entries (initially none — populated dynamically).
            RebuildControllerSection();

            // Subscribe to the single "done" event instead of CollectionChanged.
            // Deferred to Background priority so the NavigationView's internal
            // ItemsRepeater completes ALL pending layout/render passes before we
            // tear down and rebuild MenuItems. Normal priority can still interleave
            // with layout, causing the ItemsRepeater's cached index to go stale and
            // crash in ViewManager.GetElementFromElementFactory (MeasureOverride).
            _viewModel.NavControllerItemsRefreshed += (s, e) =>
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        RebuildControllerSection();
                        _viewModel.Devices.RefreshSlotButtons();
                        _inputService.RefreshProfileTopology();
                    }));

            // Subscribe to OutputType changes on each pad to refresh sidebar
            // and profile topology badges (type change doesn't add/remove slots
            // so NavControllerItemsRefreshed won't fire — call topology directly).
            foreach (var pad in _viewModel.Pads)
            {
                pad.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PadViewModel.OutputType))
                    {
                        _viewModel.RefreshNavControllerItems();
                        _inputService.RefreshProfileTopology();
                    }
                };
            }
        }

        /// <summary>
        /// Rebuilds only the controller section of the sidebar (between the two separators).
        /// Preserves Dashboard, Devices, Profiles, and footer items.
        ///
        /// Uses a re-entrancy guard because <see cref="RefreshNavControllerItems"/> fires
        /// CollectionChanged once for Clear() and once per Add(), each of which would
        /// re-trigger this method. The guard ensures only one rebuild occurs.
        ///
        /// Saves and restores the NavView selection tag so that removing/re-adding
        /// Devices or Profiles (which are rebuilt each time) doesn't break the
        /// current navigation state.
        /// </summary>
        private void RebuildControllerSection()
        {
            if (_rebuildingControllerSection)
                return;

            // Save current selection tag before tearing down items.
            string selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag?.ToString();

            _rebuildingControllerSection = true;
            try
            {
                // Remove everything from ControllerInsertIndex onward.
                // This fires NavView_SelectionChanged for intermediate states —
                // the flag suppresses those events (see guard in that handler).
                while (NavView.MenuItems.Count > ControllerInsertIndex)
                    NavView.MenuItems.RemoveAt(ControllerInsertIndex);

                // Add controller entries for each active slot.
                foreach (var navItem in _viewModel.NavControllerItems)
                {
                    var menuItem = CreateControllerNavItem(navItem);
                    NavView.MenuItems.Add(menuItem);

                    var capturedMenuItem = menuItem;
                    var capturedNavItem = navItem;
                    navItem.PropertyChanged += (s, e) =>
                    {
                        // Rebuild content for any visual property change.
                        if (e.PropertyName is nameof(NavControllerItemViewModel.InstanceLabel)
                            or nameof(NavControllerItemViewModel.IconKey)
                            or nameof(NavControllerItemViewModel.IsEnabled)
                            or nameof(NavControllerItemViewModel.SlotNumber)
                            or nameof(NavControllerItemViewModel.ConnectedDeviceCount))
                        {
                            UpdateControllerNavItemContent(capturedMenuItem, capturedNavItem);
                        }
                    };
                }

                // "Add Controller" button (visible if any controller type has remaining capacity).
                if (HasAnyControllerTypeCapacity())
                {
                    var addItem = new NavigationViewItem
                    {
                        Tag = "AddController",
                        Icon = new FontIcon { Glyph = "\uE710" }, // + icon
                        Content = "Add Controller"
                    };
                    NavView.MenuItems.Add(addItem);
                }

            }
            finally
            {
                _rebuildingControllerSection = false;
            }

            // Restore selection AFTER the guard is cleared so
            // NavView_SelectionChanged processes it normally.
            if (!string.IsNullOrEmpty(selectedTag))
            {
                NavigationViewItem match = null;
                NavigationViewItem fallback = null;
                foreach (var mi in NavView.MenuItems)
                {
                    if (mi is NavigationViewItem nvi)
                    {
                        if (nvi.Tag?.ToString() == selectedTag)
                        {
                            match = nvi;
                            break;
                        }
                        if (nvi.Tag?.ToString() == "Dashboard")
                            fallback = nvi;
                    }
                }
                NavView.SelectedItem = match ?? fallback;
            }

            // Refresh uninstall button guards (disabled when slots of that type exist).
            _viewModel.Settings.RefreshDriverGuards();
        }

        /// <summary>
        /// Creates a NavigationViewItem with two-line content for a virtual controller slot.
        /// </summary>
        private NavigationViewItem CreateControllerNavItem(NavControllerItemViewModel navItem)
        {
            var menuItem = new NavigationViewItem { Tag = navItem.Tag };
            UpdateControllerNavItemContent(menuItem, navItem);
            return menuItem;
        }

        // Power button icon: E7E8 = PowerButton glyph in Segoe MDL2 Assets.
        private const string PowerGlyph = "\uE7E8";

        /// <summary>
        /// Updates the Content and Icon of a controller NavigationViewItem.
        /// Compact card with rounded border: [Power] [Gamepad] #N | [Xbox][PS] #N [X]
        /// </summary>
        private void UpdateControllerNavItemContent(NavigationViewItem menuItem, NavControllerItemViewModel navItem)
        {
            string iconKey = navItem.IconKey;
            bool isXbox = iconKey == "XboxControllerIcon";
            bool isDS4 = iconKey == "DS4ControllerIcon";
            bool isVJoy = iconKey == "VJoyControllerIcon";

            var row = new System.Windows.Controls.DockPanel();

            // Delete button — docked right so it stays at the far end of the card.
            var deleteBtn = new System.Windows.Controls.Button
            {
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9 },
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Background = System.Windows.Media.Brushes.Transparent,
                ToolTip = "Delete virtual controller",
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.5
            };
            deleteBtn.Click += OnSidebarDeleteSlot;
            System.Windows.Controls.DockPanel.SetDock(deleteBtn, System.Windows.Controls.Dock.Right);
            row.Children.Add(deleteBtn);

            // Power button (green = enabled + active, yellow = enabled + warning, red = disabled).
            var outputType = _viewModel.Pads[navItem.PadIndex].OutputType;
            System.Windows.Media.SolidColorBrush powerColor;
            string powerTooltip;
            if (!navItem.IsEnabled)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)); // red
                powerTooltip = "Disabled";
            }
            else if (!_viewModel.IsEngineRunning)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = "Engine stopped";
            }
            else if (!_viewModel.Dashboard.IsViGEmInstalled && outputType != VirtualControllerType.VJoy)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = "ViGEmBus not installed";
            }
            else if (navItem.ConnectedDeviceCount == 0)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = "Awaiting controllers";
            }
            else
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                powerTooltip = "Active";
            }

            var powerBtn = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = PowerGlyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = powerColor
                },
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                ToolTip = powerTooltip,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            powerBtn.Click += OnSidebarPowerToggle;
            row.Children.Add(powerBtn);

            // Gamepad icon + global slot number.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "\uE7FC",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{navItem.SlotNumber}",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            });

            // Separator.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "|",
                FontSize = 12,
                Opacity = 0.3,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0)
            });

            // Xbox type button — use SetResourceReference for theme-aware Fill.
            var xboxPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPath,
                ToolTip = "Xbox 360",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isXbox ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            xboxBtn.Click += OnSidebarTypeXbox;
            row.Children.Add(xboxBtn);

            // PS type button — use SetResourceReference for theme-aware Fill.
            var ds4Path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            ds4Path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var ds4Btn = new System.Windows.Controls.Button
            {
                Content = ds4Path,
                ToolTip = "DualShock 4",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isDS4 ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            ds4Btn.Click += OnSidebarTypeDS4;
            row.Children.Add(ds4Btn);

            // vJoy type button — use SetResourceReference for theme-aware Fill.
            var vjoyPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(VJoySvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            vjoyPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            var vjoyBtn = new System.Windows.Controls.Button
            {
                Content = vjoyPath,
                ToolTip = "vJoy",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(2),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isVJoy ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            vjoyBtn.Click += OnSidebarTypeVJoy;
            row.Children.Add(vjoyBtn);

            // Per-type instance label.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.InstanceLabel,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });

            // Wrap in a rounded card border.
            var card = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(2),
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                Child = row,
                Tag = navItem.PadIndex
            };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");

            // Drag reordering — mouse-down recorded here, threshold + movement tracked at NavView level.
            card.PreviewMouseLeftButtonDown += OnCardDragStart;

            // Cross-panel: accept device drops from Devices page.
            card.AllowDrop = true;
            card.Drop += OnSidebarCardDrop;
            card.DragOver += OnSidebarCardDragOver;
            card.DragLeave += OnSidebarCardDragLeave;

            menuItem.Content = card;

            // No left gutter icon — the power button inside the card shows state.
            menuItem.Icon = null;
        }

        /// <summary>Handles sidebar power toggle button click.</summary>
        private void OnSidebarPowerToggle(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                bool newState = !SettingsManager.SlotEnabled[padIndex];
                _deviceService.SetSlotEnabled(padIndex, newState);
                // Refresh nav items so IsEnabled updates and content rebuilds.
                _viewModel.RefreshNavControllerItems();
            }
        }

        /// <summary>Handles sidebar Xbox type button click.</summary>
        private void OnSidebarTypeXbox(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Xbox360;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar DualShock 4 type button click.</summary>
        private void OnSidebarTypeDS4(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.DualShock4;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar vJoy type button click.</summary>
        private void OnSidebarTypeVJoy(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                // Device nodes are created on demand by the engine (CreateVJoyController)
                // when the slot becomes active — same pattern as ViGEm.
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.VJoy;
                _inputService.EnsureTypeGroupOrder();
                _settingsService.MarkDirty();
            }
        }

        /// <summary>
        /// Handles the sidebar delete button click for a virtual controller slot.
        /// </summary>
        private void OnSidebarDeleteSlot(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // Prevent NavigationView selection change.
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int slotIndex)
            {
                // Deferred to next dispatcher frame — DeleteSlot() fires
                // DeviceAssignmentChanged → RebuildControllerSection() which removes
                // the NavViewItem whose child button we're inside of right now.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Navigate away if we're viewing the slot being deleted.
                    if (_viewModel.SelectedPadIndex == slotIndex)
                        SelectNavItemByTag("Dashboard");

                    _deviceService.DeleteSlot(slotIndex);
                }));
            }
        }

        // ─────────────────────────────────────────────
        //  Sidebar card drag reordering (manual mouse capture)
        // ─────────────────────────────────────────────

        private bool _isDraggingCard;
        private int _dragSourcePadIndex;
        private int _dragSourceVisualPos;
        private int _dragDropIndex;
        private bool _dragIsSwapMode;       // true = swap with card under cursor; false = insert between cards
        private int _dragSwapTargetPadIndex = -1;
        private System.Windows.Controls.Border _dragSwapHighlight; // card currently highlighted for swap

        /// <summary>Records the mouse-down point on a card border (per-card handler).</summary>
        private void OnCardDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var card = sender as System.Windows.Controls.Border;

            // Don't initiate drag from button clicks inside the card.
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source, card))
            {
                _cardDragSource = null;
                return;
            }

            _cardDragStartPoint = e.GetPosition(null);
            _cardDragSource = card;
        }

        /// <summary>NavView-level: threshold check when idle, position tracking while dragging.</summary>
        private void OnNavViewDragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                if (_isDraggingCard) EndCardDrag(cancel: true);
                _cardDragSource = null;
                return;
            }

            if (_isDraggingCard)
            {
                UpdateDragPosition(e.GetPosition(NavView));
                return;
            }

            if (_cardDragSource == null) return;

            Vector diff = _cardDragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                BeginCardDrag();
            }
        }

        /// <summary>NavView-level: ends drag on mouse-up.</summary>
        private void OnNavViewDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingCard)
            {
                EndCardDrag(cancel: false);
                e.Handled = true;
            }
            _cardDragSource = null;
        }

        /// <summary>NavView-level: cancels drag on Escape.</summary>
        private void OnNavViewDragKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_isDraggingCard && e.Key == System.Windows.Input.Key.Escape)
            {
                EndCardDrag(cancel: true);
                e.Handled = true;
            }
        }

        private void BeginCardDrag()
        {
            if (_cardDragSource == null || _cardDragSource.Tag is not int padIndex) return;

            var cards = GetControllerCardBounds();
            if (cards.Count < 2) return;

            _dragSourcePadIndex = padIndex;
            _dragSourceVisualPos = cards.FindIndex(c => c.PadIndex == padIndex);
            if (_dragSourceVisualPos < 0) return;

            _dragAdornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(NavView);
            if (_dragAdornerLayer == null) return;

            // Snapshot the card visual before hiding.
            var snapshot = CaptureCardVisual(_cardDragSource);
            if (snapshot == null) return;

            _dragAdorner = new CardDragAdorner(NavView, snapshot, _cardDragSource.RenderSize);
            _dragAdornerLayer.Add(_dragAdorner);

            var accentBrush = (System.Windows.Media.Brush)FindResource("SystemControlHighlightAccentBrush");
            _insertionAdorner = new InsertionLineAdorner(NavView, accentBrush);
            _dragAdornerLayer.Add(_insertionAdorner);

            _cardDragSource.Opacity = 0;
            _isDraggingCard = true;
            _dragDropIndex = _dragSourceVisualPos;
            System.Windows.Input.Mouse.Capture(NavView, System.Windows.Input.CaptureMode.SubTree);
        }

        private void UpdateDragPosition(Point navViewPos)
        {
            _dragAdorner?.UpdatePosition(navViewPos);

            var cards = GetControllerCardBounds();
            if (cards.Count < 2) return;

            // If cursor has moved outside the card column horizontally (e.g. dragged
            // into the main content area for cross-panel assignment), don't trigger
            // any swap/insert reordering — just clear indicators.
            if (cards.Count > 0)
            {
                double colLeft = cards[0].Left;
                double colRight = cards[0].Left + cards[0].Width;
                if (navViewPos.X < colLeft - 20 || navViewPos.X > colRight + 20)
                {
                    _dragIsSwapMode = false;
                    _dragDropIndex = -1;
                    _dragSwapTargetPadIndex = -1;
                    ClearSwapHighlight();
                    _insertionAdorner?.Update(0, 0, 0, false);
                    return;
                }
            }

            // Edge zone = 25% of card height at top/bottom → insert between cards.
            // Middle zone = 50% of card height → swap with that card.
            const double edgeFraction = 0.25;

            bool isSwap = false;
            int swapCardIndex = -1;
            int dropIndex = cards.Count;

            for (int i = 0; i < cards.Count; i++)
            {
                double height = cards[i].Bottom - cards[i].Top;
                double edgeSize = height * edgeFraction;
                double topEdge = cards[i].Top + edgeSize;
                double bottomEdge = cards[i].Bottom - edgeSize;

                if (navViewPos.Y >= topEdge && navViewPos.Y <= bottomEdge)
                {
                    // Cursor is in the middle zone — swap mode (skip if it's the source card).
                    if (i != _dragSourceVisualPos)
                    {
                        isSwap = true;
                        swapCardIndex = i;
                    }
                    break;
                }
                else if (navViewPos.Y < topEdge)
                {
                    // Cursor is above this card's middle zone — insert before this card.
                    dropIndex = i;
                    break;
                }
                // else cursor is below this card's middle zone — continue to next card
            }

            // ── Type-group validation ──
            // Block cross-type reordering: only allow swap/insert within the same type group.
            var sourceType = _viewModel.Pads[_dragSourcePadIndex].OutputType;

            if (isSwap)
            {
                // Reject swap if target is a different type.
                if (_viewModel.Pads[cards[swapCardIndex].PadIndex].OutputType != sourceType)
                    isSwap = false;
            }

            if (!isSwap)
            {
                // Reject insertion outside the source's type group.
                if (!IsInsertionInSameTypeGroup(dropIndex, sourceType, cards))
                    dropIndex = -1;
            }

            _dragIsSwapMode = isSwap;

            if (isSwap)
            {
                // Swap mode: highlight target card, hide insertion line.
                _dragDropIndex = -1;
                _dragSwapTargetPadIndex = cards[swapCardIndex].PadIndex;
                _insertionAdorner?.Update(0, 0, 0, false);
                SetSwapHighlight(cards[swapCardIndex].PadIndex, true);
            }
            else
            {
                // Insert mode: show insertion line, clear swap highlight.
                _dragDropIndex = dropIndex;
                _dragSwapTargetPadIndex = -1;
                ClearSwapHighlight();

                bool noMove = dropIndex < 0 || dropIndex == _dragSourceVisualPos || dropIndex == _dragSourceVisualPos + 1;
                if (noMove || _insertionAdorner == null)
                {
                    _insertionAdorner?.Update(0, 0, 0, false);
                }
                else
                {
                    double lineY;
                    if (dropIndex == 0)
                        lineY = cards[0].Top - 1;
                    else if (dropIndex >= cards.Count)
                        lineY = cards[cards.Count - 1].Bottom + 1;
                    else
                        lineY = (cards[dropIndex - 1].Bottom + cards[dropIndex].Top) / 2;

                    _insertionAdorner.Update(lineY, cards[0].Left, cards[0].Width, true);
                }
            }
        }

        /// <summary>
        /// Returns true if the insertion point at <paramref name="insertionVisualPos"/>
        /// is adjacent to at least one card of the same type as <paramref name="sourceType"/>.
        /// </summary>
        private bool IsInsertionInSameTypeGroup(int insertionVisualPos, VirtualControllerType sourceType, List<CardBounds> cards)
        {
            if (insertionVisualPos < 0) return false;
            // Check the card above the insertion point.
            if (insertionVisualPos > 0)
            {
                int abovePad = cards[insertionVisualPos - 1].PadIndex;
                if (_viewModel.Pads[abovePad].OutputType == sourceType)
                    return true;
            }
            // Check the card below the insertion point.
            if (insertionVisualPos < cards.Count)
            {
                int belowPad = cards[insertionVisualPos].PadIndex;
                if (_viewModel.Pads[belowPad].OutputType == sourceType)
                    return true;
            }
            return false;
        }

        private void SetSwapHighlight(int padIndex, bool highlight)
        {
            // Clear previous highlight if it's a different card.
            if (_dragSwapHighlight != null && (_dragSwapHighlight.Tag is int prevPad) && prevPad != padIndex)
                ClearSwapHighlight();

            if (!highlight) { ClearSwapHighlight(); return; }

            // Find the card Border for this padIndex.
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi &&
                    nvi.Content is System.Windows.Controls.Border card &&
                    card.Tag is int idx && idx == padIndex)
                {
                    var accent = FindResource("SystemControlHighlightAccentBrush") as System.Windows.Media.Brush
                              ?? System.Windows.Media.Brushes.DodgerBlue;
                    card.BorderBrush = accent;
                    _dragSwapHighlight = card;
                    break;
                }
            }
        }

        private void ClearSwapHighlight()
        {
            if (_dragSwapHighlight == null) return;
            _dragSwapHighlight.BorderBrush = System.Windows.Media.Brushes.Transparent;
            _dragSwapHighlight = null;
        }

        private void EndCardDrag(bool cancel)
        {
            System.Windows.Input.Mouse.Capture(null);
            _isDraggingCard = false;

            if (_cardDragSource != null)
                _cardDragSource.Opacity = 1;

            ClearSwapHighlight();

            // Remove adorners.
            if (_dragAdornerLayer != null)
            {
                if (_dragAdorner != null) _dragAdornerLayer.Remove(_dragAdorner);
                if (_insertionAdorner != null) _dragAdornerLayer.Remove(_insertionAdorner);
            }
            _dragAdorner = null;
            _insertionAdorner = null;
            _dragAdornerLayer = null;

            if (!cancel)
            {
                bool handled = false;
                if (_dragIsSwapMode && _dragSwapTargetPadIndex >= 0)
                {
                    // Direct swap between two cards.
                    int srcPad = _dragSourcePadIndex;
                    int tgtPad = _dragSwapTargetPadIndex;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _inputService.SwapSlots(srcPad, tgtPad);
                        _settingsService.MarkDirty();
                    }));
                    handled = true;
                }
                else if (!_dragIsSwapMode && _dragDropIndex >= 0)
                {
                    // Insert mode: convert dropIndex to target visual position.
                    int targetVisualPos;
                    if (_dragDropIndex <= _dragSourceVisualPos)
                        targetVisualPos = _dragDropIndex;
                    else if (_dragDropIndex <= _dragSourceVisualPos + 1)
                        targetVisualPos = _dragSourceVisualPos; // no move
                    else
                        targetVisualPos = _dragDropIndex - 1;

                    if (targetVisualPos != _dragSourceVisualPos)
                    {
                        int srcPad = _dragSourcePadIndex;
                        int tgtPos = targetVisualPos;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _inputService.MoveSlot(srcPad, tgtPos);
                            _settingsService.MarkDirty();
                        }));
                        handled = true;
                    }
                }

                // Cross-panel: sidebar card dropped over a Devices-page device card.
                if (!handled)
                    TryAssignDeviceFromSidebarDrop(_dragSourcePadIndex);
            }

            _cardDragSource = null;
        }

        // ── Helpers ──

        private record struct CardBounds(int PadIndex, double Left, double Top, double Bottom, double Width);

        private List<CardBounds> GetControllerCardBounds()
        {
            var result = new List<CardBounds>();
            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem nvi &&
                    nvi.Content is System.Windows.Controls.Border card &&
                    card.Tag is int padIndex)
                {
                    try
                    {
                        var transform = card.TransformToVisual(NavView);
                        var topLeft = transform.Transform(new Point(0, 0));
                        result.Add(new CardBounds(padIndex, topLeft.X, topLeft.Y,
                            topLeft.Y + card.ActualHeight, card.ActualWidth));
                    }
                    catch { /* not in visual tree */ }
                }
            }
            return result;
        }

        private static System.Windows.Media.ImageSource CaptureCardVisual(System.Windows.Controls.Border card)
        {
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return null;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card);
            int w = (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(card);
            return rtb;
        }

        private static bool IsInsideButton(DependencyObject source, DependencyObject boundary)
        {
            var current = source;
            while (current != null && current != boundary)
            {
                if (current is System.Windows.Controls.Button) return true;
                current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        // ── Cross-panel drag assignment ──

        /// <summary>
        /// When a sidebar controller card is dropped over a Devices-page device card,
        /// assign that device to the controller slot.
        /// </summary>
        private void TryAssignDeviceFromSidebarDrop(int padIndex)
        {
            if (DevicesPageView.Visibility != Visibility.Visible) return;

            var screenPos = System.Windows.Forms.Control.MousePosition;
            var wpfPos = DevicesPageView.PointFromScreen(new Point(screenPos.X, screenPos.Y));

            // Hit-test the Devices page to find a device card Border.
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(DevicesPageView, wpfPos);
            if (hit?.VisualHit == null) return;

            // Walk up from hit element to find a card Border whose DataContext is DeviceRowViewModel.
            DependencyObject current = hit.VisualHit;
            while (current != null)
            {
                if (current is FrameworkElement fe &&
                    fe.DataContext is ViewModels.DeviceRowViewModel device)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                        _deviceService.AssignDeviceToSlot(device.InstanceGuid, padIndex)));
                    return;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }

        /// <summary>
        /// When a Devices-page device card is dropped on a sidebar controller card,
        /// assign that device to the controller slot.
        /// </summary>
        private void OnSidebarCardDrop(object sender, DragEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border card) return;
            if (card.Tag is not int padIndex) return;

            if (e.Data.GetDataPresent("DeviceInstanceGuid"))
            {
                var guid = (Guid)e.Data.GetData("DeviceInstanceGuid");
                Dispatcher.BeginInvoke(new Action(() =>
                    _deviceService.AssignDeviceToSlot(guid, padIndex)));
                e.Handled = true;
            }
        }

        private void OnSidebarCardDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("DeviceInstanceGuid"))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;

                // Highlight the card.
                if (sender is System.Windows.Controls.Border card && card.Tag is int padIndex)
                    SetSwapHighlight(padIndex, true);
            }
        }

        private void OnSidebarCardDragLeave(object sender, DragEventArgs e)
        {
            ClearSwapHighlight();
        }

        // ── Adorners ──

        /// <summary>Renders a bitmap snapshot of the dragged card following the cursor.</summary>
        private class CardDragAdorner : System.Windows.Documents.Adorner
        {
            private readonly System.Windows.Media.ImageBrush _brush;
            private readonly Size _size;
            private Point _position;

            public CardDragAdorner(UIElement adornedElement, System.Windows.Media.ImageSource snapshot, Size cardSize)
                : base(adornedElement)
            {
                _brush = new System.Windows.Media.ImageBrush(snapshot);
                _size = cardSize;
                IsHitTestVisible = false;
            }

            public void UpdatePosition(Point pos)
            {
                _position = pos;
                InvalidateVisual();
            }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                dc.DrawRectangle(_brush, null,
                    new Rect(
                        _position.X - _size.Width / 2,
                        _position.Y - _size.Height / 2,
                        _size.Width, _size.Height));
            }
        }

        /// <summary>Draws a horizontal accent line at the insertion point between cards.</summary>
        private class InsertionLineAdorner : System.Windows.Documents.Adorner
        {
            private readonly System.Windows.Media.Brush _brush;
            private double _y, _x, _width;
            private bool _visible;

            public InsertionLineAdorner(UIElement adornedElement, System.Windows.Media.Brush accentBrush)
                : base(adornedElement)
            {
                _brush = accentBrush;
                IsHitTestVisible = false;
            }

            public void Update(double y, double x, double width, bool visible)
            {
                _y = y; _x = x; _width = width; _visible = visible;
                InvalidateVisual();
            }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                if (!_visible) return;
                dc.DrawRectangle(_brush, null, new Rect(_x, _y - 1, _width, 3));
            }
        }

        /// <summary>
        /// Programmatically navigates to the Devices page.
        /// </summary>
        private void NavigateToDevices()
        {
            SelectNavItemByTag("Devices");
        }

        /// <summary>
        /// Shows a popup anchored to the given element with Xbox 360 and DS4 controller type buttons.
        /// Clicking a button creates a new slot of that type and navigates to it.
        /// </summary>
        /// <summary>
        /// Returns true if at least one virtual controller type has remaining capacity.
        /// </summary>
        private bool HasAnyControllerTypeCapacity()
        {
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox360: xboxCount++; break;
                    case VirtualControllerType.DualShock4: ds4Count++; break;
                    case VirtualControllerType.VJoy: vjoyCount++; break;
                }
            }
            return xboxCount < SettingsManager.MaxXbox360Slots
                || ds4Count < SettingsManager.MaxDS4Slots
                || vjoyCount < SettingsManager.MaxVJoySlots;
        }

        private void ShowControllerTypePopup(UIElement anchor, PlacementMode placement = PlacementMode.Right)
        {
            // If the popup is already open, close it instead of opening a duplicate.
            if (_controllerTypePopup != null && _controllerTypePopup.IsOpen)
            {
                _controllerTypePopup.IsOpen = false;
                _controllerTypePopup = null;
                return;
            }

            // StaysOpen=false closes the popup when the anchor is clicked, then the
            // click handler fires and would immediately reopen it. Suppress reopening
            // if the popup was just dismissed within the same click cycle.
            if ((DateTime.UtcNow - _popupClosedAt).TotalMilliseconds < 300)
                return;

            var popup = new Popup
            {
                StaysOpen = false,
                Placement = placement,
                PlacementTarget = anchor,
                AllowsTransparency = true
            };
            popup.Closed += (s, e) =>
            {
                _controllerTypePopup = null;
                _popupClosedAt = DateTime.UtcNow;
            };
            _controllerTypePopup = popup;

            // Center the popup horizontally below the anchor when using Bottom placement.
            if (placement == PlacementMode.Bottom && anchor is FrameworkElement fe)
            {
                popup.Opened += (s, e) =>
                {
                    if (popup.Child is FrameworkElement popupContent)
                    {
                        popupContent.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                        double popupWidth = popupContent.DesiredSize.Width;
                        double anchorWidth = fe.ActualWidth;
                        popup.HorizontalOffset = (anchorWidth - popupWidth) / 2;
                    }
                };
            }

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    Opacity = 0.3,
                    ShadowDepth = 2
                }
            };
            border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PopupBackgroundBrush");

            var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            // Count existing slots by type for per-type capacity check.
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox360: xboxCount++; break;
                    case VirtualControllerType.DualShock4: ds4Count++; break;
                    case VirtualControllerType.VJoy: vjoyCount++; break;
                }
            }

            // Xbox 360 button — theme-aware icon fill.
            var xboxPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool xboxAtCapacity = xboxCount >= SettingsManager.MaxXbox360Slots;
            bool vigemInstalled = _viewModel.Dashboard.IsViGEmInstalled;
            bool xboxDisabled = xboxAtCapacity || !vigemInstalled;
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPopupPath,
                ToolTip = !vigemInstalled ? "Xbox 360 (ViGEmBus not installed)"
                        : xboxAtCapacity ? $"Xbox 360 (max {SettingsManager.MaxXbox360Slots})"
                        : "Xbox 360",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !xboxDisabled,
                Opacity = xboxDisabled ? 0.35 : 1.0
            };
            xboxBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Xbox360);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.Xbox360);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(xboxBtn);

            // DS4 button — theme-aware icon fill.
            var ds4PopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            ds4PopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool ds4AtCapacity = ds4Count >= SettingsManager.MaxDS4Slots;
            bool ds4Disabled = ds4AtCapacity || !vigemInstalled;
            var ds4Btn = new System.Windows.Controls.Button
            {
                Content = ds4PopupPath,
                ToolTip = !vigemInstalled ? "DualShock 4 (ViGEmBus not installed)"
                        : ds4AtCapacity ? $"DualShock 4 (max {SettingsManager.MaxDS4Slots})"
                        : "DualShock 4",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !ds4Disabled,
                Opacity = ds4Disabled ? 0.35 : 1.0
            };
            ds4Btn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.DualShock4);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.DualShock4);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(ds4Btn);

            // vJoy button — theme-aware icon fill.
            var vjoyPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(VJoySvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            vjoyPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemControlForegroundBaseHighBrush");
            bool vjoyAtCapacity = vjoyCount >= SettingsManager.MaxVJoySlots;
            bool vjoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;
            bool vjoyDisabled = vjoyAtCapacity || !vjoyInstalled;
            var vjoyBtn = new System.Windows.Controls.Button
            {
                Content = vjoyPopupPath,
                ToolTip = !vjoyInstalled ? "vJoy (driver not installed)"
                        : vjoyAtCapacity ? $"vJoy (max {SettingsManager.MaxVJoySlots})"
                        : "vJoy",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !vjoyDisabled,
                Opacity = vjoyDisabled ? 0.35 : 1.0
            };
            vjoyBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;

                // Device nodes are created on demand by the engine (CreateVJoyController)
                // when the slot becomes active — same pattern as ViGEm.
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.VJoy);
                if (newSlot >= 0)
                {
                    _inputService.EnsureTypeGroupOrder();
                    int nav = FindLastSlotOfType(VirtualControllerType.VJoy);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(vjoyBtn);

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Programmatically navigates to a controller slot page (e.g., "Pad1").
        /// </summary>
        private void NavigateToSlot(int slotIndex)
        {
            SelectNavItemByTag($"Pad{slotIndex + 1}");
        }

        /// <summary>
        /// Returns the last created slot index of the given type, or -1 if none.
        /// Used after EnsureTypeGroupOrder to navigate to a newly created slot
        /// whose index may have shifted during re-sorting.
        /// </summary>
        private int FindLastSlotOfType(VirtualControllerType type)
        {
            int last = -1;
            for (int i = 0; i < InputManager.MaxPads; i++)
                if (SettingsManager.SlotCreated[i] && _viewModel.Pads[i].OutputType == type)
                    last = i;
            return last;
        }

        /// <summary>
        /// Selects a NavigationViewItem by its Tag string.
        /// </summary>
        private void SelectNavItemByTag(string tag)
        {
            foreach (var mi in NavView.MenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = nvi;
                    return;
                }
            }
        }

        private void NavView_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            // Skip intermediate selection events fired while RebuildControllerSection
            // is tearing down and re-adding items. The rebuild restores the correct
            // selection after the guard flag is cleared.
            if (_rebuildingControllerSection)
                return;

            string tag;

            if (args.IsSettingsSelected)
            {
                tag = "Settings";
            }
            else if (args.SelectedItem is NavigationViewItem item)
            {
                tag = item.Tag?.ToString() ?? "Dashboard";
            }
            else
            {
                return;
            }

            // "Add Controller" shows a type-selection popup, then creates a slot.
            // Deferred to next dispatcher frame because CreateSlot() triggers
            // RebuildControllerSection() which modifies NavView.MenuItems —
            // doing that synchronously inside SelectionChanged crashes ModernWpf's
            // internal ItemsRepeater layout.
            if (tag == "AddController")
            {
                // Immediately reselect the previous page so the blue selection
                // indicator never visually lands on the "Add Controller" item.
                SelectNavItemByTag(_viewModel.SelectedNavTag ?? "Dashboard");

                // Defer the popup + CreateSlot because they trigger
                // RebuildControllerSection() which modifies NavView.MenuItems —
                // doing that synchronously inside SelectionChanged crashes ModernWpf's
                // internal ItemsRepeater layout.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NavigationViewItem addItem = null;
                    foreach (var mi in NavView.MenuItems)
                    {
                        if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == "AddController")
                        {
                            addItem = nvi;
                            break;
                        }
                    }
                    ShowControllerTypePopup(addItem);
                }));
                return;
            }

            // Update ViewModel navigation state.
            _viewModel.SelectedNavTag = tag;

            // Swap visible page.
            DashboardPageView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            DevicesPageView.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
            ProfilesPageView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPageView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutPageView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            bool isPad = tag.StartsWith("Pad") && tag.Length >= 4 && int.TryParse(tag.Substring(3), out _);
            PadPageView.Visibility = isPad ? Visibility.Visible : Visibility.Collapsed;

            // Update PadPage DataContext to the selected pad.
            if (isPad)
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm != null)
                    PadPageView.DataContext = padVm;
            }

            // Notify InputService which pages are visible (for optimization).
            _inputService.IsDevicesPageVisible = tag == "Devices";
            _inputService.IsPadPageVisible = isPad;
        }

        // ─────────────────────────────────────────────
        //  Settings handlers
        // ─────────────────────────────────────────────

        private void OnOpenSettingsFolder(object sender, EventArgs e)
        {
            string folder = System.IO.Path.GetDirectoryName(_settingsService.SettingsFilePath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }

        private void OnNewProfile(object sender, EventArgs e)
        {
            var dialog = new Views.ProfileDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.ProfileName;
            string exePaths = string.Join("|", dialog.ExecutablePaths);

            // Create an empty shell profile — no VCs, no device assignments.
            var profile = new ProfileData
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(),
                ExecutableNames = exePaths,
                Entries = Array.Empty<ProfileEntry>(),
                PadSettings = Array.Empty<PadSetting>(),
                SlotCreated = new bool[InputManager.MaxPads],
                SlotEnabled = new bool[InputManager.MaxPads],
                SlotControllerTypes = new int[InputManager.MaxPads],
            };

            SettingsManager.Profiles.Add(profile);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Executables = FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);

            _settingsService.MarkDirty();
            _viewModel.StatusText = $"Profile \"{name}\" created (empty).";
        }

        private void OnSaveAsProfile(object sender, EventArgs e)
        {
            var dialog = new Views.ProfileDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.ProfileName;
            // Store full paths pipe-separated.
            string exePaths = string.Join("|", dialog.ExecutablePaths);

            // Snapshot current settings into a new profile.
            var snapshot = _inputService.SnapshotCurrentProfile();
            snapshot.Id = Guid.NewGuid().ToString("N");
            snapshot.Name = name.Trim();
            snapshot.ExecutableNames = exePaths;

            SettingsManager.Profiles.Add(snapshot);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = snapshot.Id,
                Name = snapshot.Name,
                Executables = FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, snapshot.SlotCreated, snapshot.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);

            _settingsService.MarkDirty();
            _viewModel.StatusText = $"Profile \"{name}\" created.";
        }

        /// <summary>
        /// Formats pipe-separated full paths into a display string showing just file names.
        /// </summary>
        private static string FormatExePaths(string pipeSeparatedPaths)
        {
            if (string.IsNullOrEmpty(pipeSeparatedPaths))
                return string.Empty;

            var parts = pipeSeparatedPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                names[i] = System.IO.Path.GetFileName(parts[i]);
            return string.Join(", ", names);
        }

        private void OnDeleteProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            SettingsManager.Profiles.RemoveAll(p => p.Id == selected.Id);

            _viewModel.Settings.ProfileItems.Remove(selected);
            _viewModel.Settings.SelectedProfile = null;

            if (SettingsManager.ActiveProfileId == selected.Id)
            {
                SettingsManager.ActiveProfileId = null;
                _viewModel.Settings.ActiveProfileInfo = "Default";
                // Restore the default profile state so the deleted profile's
                // topology doesn't persist and overwrite the default snapshot.
                _inputService.ApplyDefaultProfile();
            }

            _settingsService.MarkDirty();
            _inputService.RefreshProfileTopology();
            _viewModel.StatusText = $"Profile \"{selected.Name}\" deleted.";
        }

        private void OnEditProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile == null) return;

            var exePaths = string.IsNullOrEmpty(profile.ExecutableNames)
                ? Array.Empty<string>()
                : profile.ExecutableNames.Split('|', StringSplitOptions.RemoveEmptyEntries);

            var dialog = new Views.ProfileDialog { Owner = this };
            dialog.LoadForEdit(profile.Name, exePaths);

            if (dialog.ShowDialog() != true)
                return;

            string newName = dialog.ProfileName;
            string newExePaths = string.Join("|", dialog.ExecutablePaths);

            profile.Name = newName;
            profile.ExecutableNames = newExePaths;

            selected.Name = newName;
            selected.Executables = FormatExePaths(newExePaths);

            // Update active profile label if this profile is currently active.
            if (SettingsManager.ActiveProfileId == profile.Id)
                _viewModel.Settings.ActiveProfileInfo = newName;

            _settingsService.MarkDirty();
            _viewModel.StatusText = $"Profile \"{newName}\" updated.";
        }

        private void OnLoadProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            // Loading the Default profile is equivalent to reverting.
            if (selected.IsDefault)
            {
                OnRevertToDefault(sender, e);
                return;
            }

            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile == null) return;

            // Already on this profile — nothing to do.
            if (SettingsManager.ActiveProfileId == profile.Id)
                return;

            // Save outgoing profile state before switching.
            _inputService.SaveActiveProfileState();

            // Set ActiveProfileId BEFORE ApplyProfile so that
            // RefreshActiveProfileTopologyLabel updates the correct profile.
            SettingsManager.ActiveProfileId = profile.Id;
            _viewModel.Settings.ActiveProfileInfo = profile.Name;
            _inputService.ApplyProfile(profile);
            _viewModel.StatusText = $"Profile loaded: {profile.Name}";
        }

        private void OnRevertToDefault(object sender, EventArgs e)
        {
            if (SettingsManager.ActiveProfileId == null)
                return;

            // Save outgoing profile state before reverting.
            _inputService.SaveActiveProfileState();

            // Set ActiveProfileId BEFORE ApplyProfile so that
            // RefreshActiveProfileTopologyLabel updates the correct profile.
            SettingsManager.ActiveProfileId = null;
            _viewModel.Settings.ActiveProfileInfo = "Default";
            _inputService.ApplyDefaultProfile();
            _viewModel.StatusText = "Profile reverted to Default";
        }

        private void WireMacroRecording(MacroItem macro, int padIndex)
        {
            macro.RecordTriggerRequested += (s, e) =>
            {
                if (s is MacroItem mi)
                {
                    if (mi.IsRecordingTrigger)
                        _inputService.StartMacroTriggerRecording(mi, padIndex);
                    else
                        _inputService.StopMacroTriggerRecording();
                }
            };
        }

        private void OnThemeChanged(object sender, int themeIndex)
        {
            ModernWpf.ThemeManager.Current.ApplicationTheme = themeIndex switch
            {
                1 => ModernWpf.ApplicationTheme.Light,
                2 => ModernWpf.ApplicationTheme.Dark,
                _ => null // System default
            };
        }

        // ─────────────────────────────────────────────
        //  System tray
        // ─────────────────────────────────────────────

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "PadForge";

            // Load icon from the running executable.
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

            // Context menu.
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show PadForge", null, (s, e) => RestoreFromTray());
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => { _notifyIcon.Visible = false; Close(); });
            _notifyIcon.ContextMenuStrip = menu;

            // Double-click to restore.
            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            {
                Hide();
                _notifyIcon.Visible = true;
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            _notifyIcon.Visible = false;
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste / Copy From
        // ─────────────────────────────────────────────

        private void OnCopySettings(PadViewModel padVm)
        {
            var ps = _inputService.GetCurrentPadSetting(padVm.PadIndex);
            if (ps == null)
            {
                _viewModel.StatusText = "No device selected to copy from.";
                return;
            }

            try
            {
                Clipboard.SetText(ps.ToJson());
                _viewModel.StatusText = "Settings copied to clipboard.";
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = $"Failed to copy: {ex.Message}";
            }
        }

        private void OnPasteSettings(PadViewModel padVm)
        {
            try
            {
                string json = Clipboard.GetText();
                var ps = PadSetting.FromJson(json);
                if (ps == null)
                {
                    _viewModel.StatusText = "Clipboard does not contain valid PadForge settings.";
                    return;
                }

                _inputService.ApplyPadSettingToCurrentDevice(padVm.PadIndex, ps);
                _settingsService.MarkDirty();
                _viewModel.StatusText = "Settings pasted from clipboard.";
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = $"Failed to paste: {ex.Message}";
            }
        }

        private void OnCopyFrom(PadViewModel padVm)
        {
            // Build list of all devices that have configured settings.
            var entries = new List<CopyFromDialog.DeviceEntry>();
            var settings = SettingsManager.UserSettings?.Items;
            if (settings != null)
            {
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in settings)
                    {
                        // Skip the currently selected device.
                        if (padVm.SelectedMappedDevice != null &&
                            us.InstanceGuid == padVm.SelectedMappedDevice.InstanceGuid)
                            continue;

                        var ps = us.GetPadSetting();
                        if (ps == null || !ps.HasAnyMapping)
                            continue;

                        // Get the real device name from UserDevice (InstanceName on
                        // UserSetting is often empty).
                        var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                        string name = ud?.InstanceName;
                        if (string.IsNullOrEmpty(name))
                            name = ud?.ProductName;
                        if (string.IsNullOrEmpty(name))
                            name = us.InstanceGuid.ToString();

                        string slot = us.MapTo >= 0 && us.MapTo < InputManager.MaxPads
                            ? $"Player {us.MapTo + 1} — {us.InstanceGuid:D}"
                            : $"Unmapped — {us.InstanceGuid:D}";

                        entries.Add(new CopyFromDialog.DeviceEntry
                        {
                            Name = name,
                            SlotLabel = slot,
                            InstanceGuid = us.InstanceGuid,
                            PadSetting = ps
                        });
                    }
                }
            }

            if (entries.Count == 0)
            {
                _viewModel.StatusText = "No other configured devices found to copy from.";
                return;
            }

            var dialog = new CopyFromDialog(entries) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedPadSetting != null)
            {
                _inputService.ApplyPadSettingToCurrentDevice(padVm.PadIndex, dialog.SelectedPadSetting);
                _settingsService.MarkDirty();
                _viewModel.StatusText = "Settings copied from selected device.";
            }
        }

        // ─────────────────────────────────────────────
        //  Driver install/uninstall helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Runs a driver install/uninstall operation on a background thread,
        /// then refreshes the driver status on the UI thread.
        /// </summary>
        private async Task RunDriverOperationAsync(string statusMessage, Action operation, Action refreshStatus)
        {
            _viewModel.StatusText = statusMessage;
            DriverOverlayText.Text = statusMessage;
            DriverOverlay.Visibility = Visibility.Visible;
            try
            {
                await Task.Run(operation);
                _viewModel.StatusText = "Ready";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined UAC prompt.
                _viewModel.StatusText = "Operation cancelled by user.";
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = $"Driver operation failed: {ex.Message}";
            }
            finally
            {
                DriverOverlay.Visibility = Visibility.Collapsed;
            }
            refreshStatus();
        }

        /// <summary>
        /// Rebuilds the dashboard SlotSummaries from SettingsManager state.
        /// Used at startup and when slots change while the engine is off.
        /// </summary>
        private void RefreshDashboardActiveSlots()
        {
            var activeSlots = new System.Collections.Generic.List<int>();
            int xboxCount = 0, ds4Count = 0, vjoyCount = 0;
            for (int i = 0; i < _viewModel.Pads.Count; i++)
            {
                if (SettingsManager.SlotCreated[i])
                {
                    activeSlots.Add(i);
                    switch (_viewModel.Pads[i].OutputType)
                    {
                        case VirtualControllerType.Xbox360: xboxCount++; break;
                        case VirtualControllerType.DualShock4: ds4Count++; break;
                        case VirtualControllerType.VJoy: vjoyCount++; break;
                    }
                }
            }
            bool canAddMore = xboxCount < SettingsManager.MaxXbox360Slots
                           || ds4Count < SettingsManager.MaxDS4Slots
                           || vjoyCount < SettingsManager.MaxVJoySlots;
            _viewModel.Dashboard.RefreshActiveSlots(activeSlots, canAddMore);
        }

        private void RefreshViGEmStatus()
        {
            try
            {
                bool installed = PadForge.Common.Input.InputManager.CheckViGEmInstalled();
                _viewModel.Settings.IsViGEmInstalled = installed;
                _viewModel.Dashboard.IsViGEmInstalled = installed;

                var version = DriverInstaller.GetViGEmVersion();
                _viewModel.Settings.ViGEmVersion = version ?? string.Empty;
                _viewModel.Dashboard.ViGEmVersion = version ?? string.Empty;

                if (!installed)
                    _viewModel.StatusText = "ViGEmBus driver not detected. Virtual controller output disabled.";
            }
            catch (Exception ex)
            {
                _viewModel.Settings.IsViGEmInstalled = false;
                _viewModel.Dashboard.IsViGEmInstalled = false;
                _viewModel.StatusText = $"ViGEm check failed: {ex.Message}";
            }
        }

        private void RefreshHidHideStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsHidHideInstalled();
                _viewModel.Settings.IsHidHideInstalled = installed;
                _viewModel.Dashboard.IsHidHideInstalled = installed;
                _viewModel.Settings.HidHideVersion = DriverInstaller.GetHidHideVersion() ?? string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsHidHideInstalled = false;
                _viewModel.Dashboard.IsHidHideInstalled = false;
            }
        }

        private void RefreshVJoyStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsVJoyInstalled();
                _viewModel.Settings.IsVJoyInstalled = installed;
                _viewModel.Dashboard.IsVJoyInstalled = installed;
                _viewModel.Settings.VJoyVersion = DriverInstaller.GetVJoyVersion() ?? string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsVJoyInstalled = false;
                _viewModel.Dashboard.IsVJoyInstalled = false;
            }
        }

        /// <summary>
        /// Called after vJoy install/uninstall via PadForge's own buttons.
        /// Resets cached vJoy state and restarts the engine so the new driver
        /// is picked up immediately without requiring a PadForge relaunch.
        /// </summary>
        private void OnVJoyDriverChanged()
        {
            RefreshVJoyStatus();
            _previousVJoyInstalled = _viewModel.Dashboard.IsVJoyInstalled;

            PadForge.Common.Input.VJoyVirtualController.ResetState();

            if (_viewModel.IsEngineRunning)
            {
                // Preserve vJoy device nodes during restart so the DLL's internal
                // device handles stay valid. Nodes are disabled (not removed) during
                // Stop, then re-enabled by EnsureDevicesAvailable when vJoy slots
                // become active — same pattern as "delete last vJoy + re-add".
                _inputService.Stop(preserveVJoyNodes: true);
                _inputService.Start();
                _viewModel.StatusText = _viewModel.Dashboard.IsVJoyInstalled
                    ? "vJoy driver installed — engine restarted."
                    : "vJoy driver removed — engine restarted.";
            }
        }
    }
}
