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
        private System.Windows.Forms.NotifyIcon _notifyIcon;

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
            _viewModel.Settings.SaveAsProfileRequested += OnSaveAsProfile;
            _viewModel.Settings.DeleteProfileRequested += OnDeleteProfile;
            _viewModel.Settings.EditProfileRequested += OnEditProfile;
            _viewModel.Settings.LoadProfileRequested += OnLoadProfile;
            _viewModel.Settings.RevertToDefaultRequested += OnRevertToDefault;

            // Apply registry Run key when Start at Login is toggled.
            // Persist DSU server settings on change.
            _viewModel.Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.StartAtLogin))
                    Common.StartupHelper.SetStartupEnabled(_viewModel.Settings.StartAtLogin);
                else if (e.PropertyName is nameof(SettingsViewModel.EnableDsuMotionServer)
                         or nameof(SettingsViewModel.DsuMotionServerPort))
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

            // Wire device service events (assign to slot, hide, etc.).
            _deviceService.WireEvents();

            // Refresh PadPage dropdowns immediately after device assignment changes.
            _deviceService.DeviceAssignmentChanged += (s, e) => _inputService.UpdatePadDeviceInfo();

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
                }));
            };

            DashboardPageView.SlotEnabledToggled += (s, args) =>
            {
                _deviceService.SetSlotEnabled(args.SlotIndex, args.IsEnabled);
                // Refresh sidebar so power button updates.
                _viewModel.RefreshNavControllerItems();
            };

            DashboardPageView.SlotTypeChangeRequested += (s, args) =>
            {
                _viewModel.Pads[args.SlotIndex].OutputType = args.IsXbox
                    ? VirtualControllerType.Xbox360
                    : VirtualControllerType.DualShock4;
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

            // Auto-start engine. Must be in the constructor (not OnLoaded) because
            // OnLoaded only fires when the window is rendered — which never happens
            // when starting minimized to tray.
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

            // Detect HidHide driver.
            RefreshHidHideStatus();

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

        /// <summary>Index in NavView.MenuItems where the first controller entry goes (after Dashboard + separator).</summary>
        private const int ControllerInsertIndex = 2;

        /// <summary>Re-entrancy guard for <see cref="RebuildControllerSection"/>.</summary>
        private bool _rebuildingControllerSection;

        /// <summary>
        /// Programmatically builds the NavigationView menu items.
        /// Static items: Dashboard, separators, Devices, Profiles.
        /// Dynamic items: controller entries + "Add" button, rebuilt when NavControllerItems changes.
        /// </summary>
        private void BuildNavigationItems()
        {
            NavView.MenuItems.Clear();

            // Dashboard.
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = "Dashboard",
                Tag = "Dashboard",
                Icon = new SymbolIcon(Symbol.Home)
            });

            NavView.MenuItems.Add(new NavigationViewItemSeparator());

            // Controller entries (initially none — populated dynamically).
            RebuildControllerSection();

            // Subscribe to the single "done" event instead of CollectionChanged.
            // Deferred to the next dispatcher frame so the NavigationView's internal
            // ItemsRepeater completes its current layout pass before we tear down
            // and rebuild MenuItems. Without this, the ItemsRepeater's cached index
            // goes stale and crashes in MeasureOverride.
            _viewModel.NavControllerItemsRefreshed += (s, e) =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RebuildControllerSection();
                    _viewModel.Devices.RefreshSlotButtons();
                }));

            // Subscribe to OutputType changes on each pad to refresh sidebar.
            foreach (var pad in _viewModel.Pads)
            {
                pad.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PadViewModel.OutputType))
                        _viewModel.RefreshNavControllerItems();
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

                NavView.MenuItems.Add(new NavigationViewItemSeparator());

                // Devices.
                NavView.MenuItems.Add(new NavigationViewItem
                {
                    Content = "Devices",
                    Tag = "Devices",
                    Icon = new SymbolIcon(Symbol.AllApps)
                });

                // Profiles.
                var profiles = new NavigationViewItem
                {
                    Tag = "Profiles",
                    Icon = new FontIcon { Glyph = "\uE8F1" }
                };
                profiles.Content = "Profiles";
                NavView.MenuItems.Add(profiles);
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
            bool isXbox = navItem.IconKey != "DS4ControllerIcon";

            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };

            // Power button (green = enabled + connected, yellow = enabled + no devices, red = disabled).
            System.Windows.Media.SolidColorBrush powerColor;
            if (!navItem.IsEnabled)
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)); // red
            else if (navItem.ConnectedDeviceCount == 0)
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
            else
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green

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
                ToolTip = !navItem.IsEnabled ? "Disabled"
                    : navItem.ConnectedDeviceCount == 0 ? "Awaiting controllers"
                    : "Active",
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
                Text = $"#{navItem.SlotNumber}",
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
                Opacity = isXbox ? 0.3 : 1.0,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            ds4Btn.Click += OnSidebarTypeDS4;
            row.Children.Add(ds4Btn);

            // Per-type instance label.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.InstanceLabel,
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });

            // Delete button.
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
            row.Children.Add(deleteBtn);

            // Wrap in a rounded card border.
            var card = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Child = row
            };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SystemControlBackgroundChromeMediumLowBrush");

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
            int xboxCount = 0, ds4Count = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                if (_viewModel.Pads[i].OutputType == VirtualControllerType.Xbox360)
                    xboxCount++;
                else if (_viewModel.Pads[i].OutputType == VirtualControllerType.DualShock4)
                    ds4Count++;
            }
            return xboxCount < SettingsManager.MaxXbox360Slots || ds4Count < SettingsManager.MaxDS4Slots;
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

            var popup = new Popup
            {
                StaysOpen = false,
                Placement = placement,
                PlacementTarget = anchor,
                AllowsTransparency = true
            };
            popup.Closed += (s, e) => _controllerTypePopup = null;
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
            int xboxCount = 0, ds4Count = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                if (_viewModel.Pads[i].OutputType == VirtualControllerType.Xbox360)
                    xboxCount++;
                else if (_viewModel.Pads[i].OutputType == VirtualControllerType.DualShock4)
                    ds4Count++;
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
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPopupPath,
                ToolTip = xboxAtCapacity ? $"Xbox 360 (max {SettingsManager.MaxXbox360Slots})" : "Xbox 360",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !xboxAtCapacity,
                Opacity = xboxAtCapacity ? 0.35 : 1.0
            };
            xboxBtn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Xbox360);
                if (newSlot >= 0)
                {
                    // NavigateToSlot must run after the deferred RebuildControllerSection
                    // so the new nav item exists. Both are queued via BeginInvoke in order.
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(newSlot)));
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
            var ds4Btn = new System.Windows.Controls.Button
            {
                Content = ds4PopupPath,
                ToolTip = ds4AtCapacity ? $"DualShock 4 (max {SettingsManager.MaxDS4Slots})" : "DualShock 4",
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsEnabled = !ds4AtCapacity,
                Opacity = ds4AtCapacity ? 0.35 : 1.0
            };
            ds4Btn.Click += (s, e) =>
            {
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.DualShock4);
                if (newSlot >= 0)
                {
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(newSlot)));
                }
            };
            stack.Children.Add(ds4Btn);

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
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Find the AddController nav item to anchor the popup.
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

            bool isPad = tag.StartsWith("Pad") && tag.Length == 4;
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

            _viewModel.Settings.ProfileItems.Add(new ViewModels.ProfileListItem
            {
                Id = snapshot.Id,
                Name = snapshot.Name,
                Executables = FormatExePaths(exePaths)
            });

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
            }

            _settingsService.MarkDirty();
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

            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile == null) return;

            // Snapshot current state before switching away from default.
            if (SettingsManager.ActiveProfileId == null)
                _inputService.RefreshDefaultSnapshot();

            _inputService.ApplyProfile(profile);
            SettingsManager.ActiveProfileId = profile.Id;
            _viewModel.Settings.ActiveProfileInfo = profile.Name;
            _viewModel.StatusText = $"Profile loaded: {profile.Name}";
        }

        private void OnRevertToDefault(object sender, EventArgs e)
        {
            if (SettingsManager.ActiveProfileId == null)
                return;

            _inputService.ApplyDefaultProfile();
            SettingsManager.ActiveProfileId = null;
            _viewModel.Settings.ActiveProfileInfo = "Default";
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
            refreshStatus();
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
    }
}
