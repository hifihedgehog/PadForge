using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using FontIcon = Wpf.Ui.Controls.FontIcon;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Forces the window to the foreground even when another app owns focus.
        /// Briefly sets WPF Topmost to push above all windows, then clears it
        /// so the window behaves normally. No synthetic input injected.
        /// </summary>
        private void ForceToForeground(IntPtr hwnd)
        {
            Topmost = true;
            Activate();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                () => Topmost = false);
        }

        private readonly MainViewModel _viewModel;
        private InputService _inputService;
        private SettingsService _settingsService;

        /// <summary>Exposes the app's SettingsService to dialogs / pages
        /// that need to persist runtime changes (e.g. the Import from
        /// Device dialog's AddUserProfile call). Reachable via
        /// Application.Current.MainWindow cast.</summary>
        public SettingsService SettingsService => _settingsService;
        private RecorderService _recorderService;
        private DeviceService _deviceService;
        private Popup _controllerTypePopup;
        private DateTime _popupClosedAt;

        /// <summary>When non-null, the next recording result goes to this mapping's NegSourceDescriptor.</summary>
        private MappingItem _pendingNegMapping;
        /// <summary>Saved positive descriptor while recording the negative direction.</summary>
        private string _savedPosDescriptor;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Threading.DispatcherTimer _driverStatusTimer;

        // Drag reorder state for sidebar controller cards.
        private Point _cardDragStartPoint;
        private System.Windows.Controls.Border _cardDragSource;
        private CardDragAdorner _dragAdorner;
        private InsertionLineAdorner _insertionAdorner;
        private System.Windows.Documents.AdornerLayer _dragAdornerLayer;

        /// <summary>
        /// Window-level PreviewMouseDown handler that drops WPF keyboard
        /// focus when the user clicks anywhere that doesn't legitimately
        /// need sustained keyboard input. Wired in the constructor; see
        /// the comment block there for the rationale (the assigned-
        /// devices ComboBox is the primary offender, but the same logic
        /// keeps focus from sticking on any control whose default
        /// keyboard handling would intercept the user's next key press).
        ///
        /// <para>Tunneling event: runs on the way down, before the
        /// click target's bubbling MouseDown handler. Our ClearFocus
        /// happens first; if the click target is an input element that
        /// genuinely needs focus, its own handler reasserts focus during
        /// the bubbling phase. Net effect: focus correctly tracks the
        /// most recently-clicked focusable input, and parks at "no
        /// focus" when the user clicks neutral surfaces.</para>
        ///
        /// <para>Preserve list = controls that consume keyboard input
        /// after the click and would otherwise be broken by losing
        /// focus mid-interaction:
        /// <list type="bullet">
        /// <item><c>ComboBox</c> — text-search and arrow navigation</item>
        /// <item><c>TextBoxBase</c> (TextBox, RichTextBox) — text entry</item>
        /// <item><c>PasswordBox</c> — password entry</item>
        /// <item><c>ListBox</c> / <c>ListView</c> — arrow-key item selection</item>
        /// </list>
        /// Other controls (Button, CheckBox, Slider, RadioButton, tabs,
        /// nav items, Border, Grid, TextBlock, etc.) either fire their
        /// effect on click without needing follow-up keyboard input, or
        /// don't take keyboard focus at all.</para>
        ///
        /// <para>Walk includes both visual and logical tree: ComboBoxes
        /// dropped down via popup live in a separate visual tree, but
        /// their template parts inside the main window do live in the
        /// visual tree. The logical-tree fallback covers cases where
        /// VisualTreeHelper.GetParent returns null (Run, Hyperlink,
        /// templated content roots).</para>
        /// </summary>
        private static void MainWindow_PreviewMouseDown_ClearFocus(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var d = e.OriginalSource as System.Windows.DependencyObject;
            while (d != null)
            {
                if (d is System.Windows.Controls.ComboBox
                    || d is System.Windows.Controls.Primitives.TextBoxBase
                    || d is System.Windows.Controls.PasswordBox
                    || d is System.Windows.Controls.ListBox)
                    return;

                var parent = System.Windows.Media.VisualTreeHelper.GetParent(d);
                if (parent == null && d is System.Windows.FrameworkElement fe)
                    parent = System.Windows.LogicalTreeHelper.GetParent(fe);
                d = parent;
            }
            System.Windows.Input.Keyboard.ClearFocus();
        }

        public MainWindow()
        {
            InitializeComponent();

            // wpfui's TitleBarButton fires its Command twice on a finger tap
            // because two paths converge: a Win32 hwnd hook catches WM_NCLBUTTONUP
            // (NC area, via WM_NCHITTEST returning HTMAXBUTTON / HTMINBUTTON /
            // HTCLOSE) and calls InvokeClick(); AND WPF promotes the same touch
            // event up through Stylus → Mouse → Button.Click. Maximize is
            // bistate so the second click reverses the action — minimize and
            // close hide their second click because the target disappears.
            //
            // The Win32 path is the one we want to keep (it's how Snap Layouts
            // hover and the proper NC-area drag work). Suppress the WPF path
            // by consuming touch events at the TitleBar before they propagate
            // into the buttons. Stylus.IsPressAndHoldEnabled on the window
            // doesn't help here because the bug isn't press-and-hold; it's
            // straight touch promotion.
            AppTitleBar.PreviewTouchDown += (_, e) => e.Handled = true;
            AppTitleBar.PreviewTouchUp   += (_, e) => e.Handled = true;

            // Click-outside-clears-keyboard-focus.
            //
            // WPF's default: keyboard focus stays on whatever was last
            // clicked-into until the user clicks another focusable
            // element. The assigned-devices ComboBox at the top of the
            // controller page is the worst offender — once focused, its
            // built-in arrow-key navigation and letter-key text-search
            // swallow keys aimed at anything else. After mapping a key
            // and trying to test the mapping, the user's test presses
            // shift the dropdown selection (each letter / arrow jumps
            // the ComboBox to a different device entry) instead of
            // visibly exercising the mapping.
            //
            // Clicking the surrounding page should drop ComboBox focus
            // — but Grid / StackPanel / Border aren't focusable, so a
            // click on them doesn't move WPF's keyboard focus off the
            // ComboBox. This handler simulates the click-outside-clears-
            // focus behavior every other Windows app has by default.
            //
            // Preserve focus only when the click hits something that
            // legitimately needs sustained keyboard input (text entry,
            // ComboBox text-search, list arrow navigation). Clear focus
            // for everything else (background canvas, labels, buttons
            // whose handlers fire on click without follow-up keyboard
            // input, etc.).
            PreviewMouseDown += MainWindow_PreviewMouseDown_ClearFocus;

            // Wire NavigationView events in code-behind (WPF UI uses TypedEventHandler).
            NavView.SelectionChanged += NavView_SelectionChanged;
            NavView.ItemInvoked += NavView_ItemInvoked;

            // Fade compact icons in when pane closes, fade out when it opens.
            NavView.PaneClosed += (_, _) =>
            {
                _isCardFading = true;
                // Clear any leftover animation state from previous cycle.
                foreach (var mi in NavView.MenuItems)
                    if (mi is NavigationViewItem nvi && nvi.Tag?.ToString()?.StartsWith("Pad") == true)
                    { nvi.BeginAnimation(UIElement.OpacityProperty, null); nvi.Opacity = 0; }
                UpdateAllControllerCardMode(compact: true);

                // Delay for pane animation, then fade in, then unlock.
                var delayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                delayTimer.Tick += (s2, e2) =>
                {
                    delayTimer.Stop();
                    foreach (var mi in NavView.MenuItems)
                        if (mi is NavigationViewItem nvi && nvi.Tag?.ToString()?.StartsWith("Pad") == true)
                            nvi.BeginAnimation(UIElement.OpacityProperty,
                                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

                    // Unlock after fade completes.
                    var unlockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                    unlockTimer.Tick += (s3, e3) =>
                    {
                        unlockTimer.Stop();
                        _isCardFading = false;
                        if (_rebuildPendingAfterFade) { _rebuildPendingAfterFade = false; RebuildControllerSection(); }
                    };
                    unlockTimer.Start();
                };
                delayTimer.Start();
            };
            NavView.PaneOpened += (_, _) =>
            {
                _isCardFading = true;
                foreach (var mi in NavView.MenuItems)
                    if (mi is NavigationViewItem nvi && nvi.Tag?.ToString()?.StartsWith("Pad") == true)
                    { nvi.BeginAnimation(UIElement.OpacityProperty, null); nvi.Opacity = 0; }
                UpdateAllControllerCardMode(compact: false);

                var delayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                delayTimer.Tick += (s2, e2) =>
                {
                    delayTimer.Stop();
                    foreach (var mi in NavView.MenuItems)
                        if (mi is NavigationViewItem nvi && nvi.Tag?.ToString()?.StartsWith("Pad") == true)
                            nvi.BeginAnimation(UIElement.OpacityProperty,
                                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

                    var unlockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                    unlockTimer.Tick += (s3, e3) =>
                    {
                        unlockTimer.Stop();
                        _isCardFading = false;
                        if (_rebuildPendingAfterFade) { _rebuildPendingAfterFade = false; RebuildControllerSection(); }
                    };
                    unlockTimer.Start();
                };
                delayTimer.Start();
            };



            // Fallback click handler — WPF UI's SelectionChanged may not fire
            // without TargetPageType navigation. Catch clicks directly.
            NavView.PreviewMouseLeftButtonUp += (s, e) =>
            {
                // Walk up from the clicked element to find the NavigationViewItem.
                var elem = e.OriginalSource as DependencyObject;
                while (elem != null)
                {
                    if (elem is NavigationViewItem nvi && nvi.Tag != null)
                    {
                        var tag = nvi.Tag.ToString();
                        if (tag == "AddController")
                            ShowControllerTypePopup(nvi);
                        else if (!_rebuildingControllerSection)
                        {
                            // Clear active state on all items, set on clicked one.
                            foreach (var mi in NavView.MenuItems)
                                if (mi is NavigationViewItem other) other.IsActive = false;
                            foreach (var mi in NavView.FooterMenuItems)
                                if (mi is NavigationViewItem other) other.IsActive = false;
                            nvi.IsActive = true;
                            NavigateToTag(tag);
                        }
                        break;
                    }
                    elem = System.Windows.Media.VisualTreeHelper.GetParent(elem);
                }
            };

            // Close Add Controller popup on window events.
            LocationChanged += (_, _) => CloseControllerPopup();
            SizeChanged += (_, _) => CloseControllerPopup();
            Deactivated += (_, _) => CloseControllerPopup();

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
            _inputService = new InputService(_viewModel) { SettingsService = _settingsService };
            _recorderService = new RecorderService(_viewModel);
            // Expose the recorder to PadPage so the shift activator dialog
            // can use freeform recording for its Record buttons.
            Views.PadPage.Recorder = _recorderService;
            Views.PadPage.InputService = _inputService;
            _deviceService = new DeviceService(_viewModel, _settingsService);
            // Per-device calibrate-gyro button on the Devices page goes
            // through this static reference to reach the shared
            // GyroCalibratorService.
            Views.DevicesPage.InputService = _inputService;
            ProfilesPageView.InputService = _inputService;
            ProfilesPageView.OnShortcutsChanged = SaveProfileShortcuts;
            _inputService.ToggleMainWindow = () => Dispatcher.Invoke(() =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

                if (!IsVisible)
                {
                    RestoreFromTray();
                    ForceToForeground(hwnd);
                }
                else if (WindowState == WindowState.Minimized || !IsActive)
                {
                    // Minimized or behind other windows — bring to foreground.
                    if (_isFullScreen)
                    {
                        WindowStyle = WindowStyle.None;
                        WindowState = WindowState.Maximized;
                    }
                    else
                        WindowState = WindowState.Normal;
                    Activate();
                    ForceToForeground(hwnd);
                }
                else
                {
                    // Foreground and visible — minimize.
                    if (_viewModel.Settings.MinimizeToTray)
                    {
                        Hide();
                        _notifyIcon.Visible = true;
                    }
                    else
                        WindowState = WindowState.Minimized;
                }
            });

            // #91 — bulk VC enable/disable from a profile-shortcut combo.
            // Rule: if any created slot is currently enabled, disable all
            // created slots; else (all already disabled) enable all. Uncreated
            // slots are not touched. DeviceService.SetSlotEnabled is the
            // canonical setter — calling it per-slot keeps SettingsManager,
            // SettingsService.MarkDirty, and the Step5 dispatcher reconciliation
            // path all wired up the same way the per-slot sidebar power toggle uses.
            _inputService.ToggleVCsDisabled = () => Dispatcher.Invoke(() =>
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
                bool target = !anyEnabled;
                for (int i = 0; i < InputManager.MaxPads; i++)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    if (SettingsManager.SlotEnabled[i] == target) continue;
                    _deviceService.SetSlotEnabled(i, target);
                }
                _viewModel.RefreshNavControllerItems();
            });

            // Wire driver uninstall guards — lambda queries the ViewModel's Pads for active slot types.
            _viewModel.Settings.HasAnyMidiSlots = () =>
            {
                for (int i = 0; i < InputManager.MaxPads; i++)
                    if (SettingsManager.SlotCreated[i] &&
                        _viewModel.Pads[i].OutputType == VirtualControllerType.Midi)
                        return true;
                return false;
            };
            _viewModel.Settings.HasAnyHidHideDevices = () =>
            {
                var devices = SettingsManager.UserDevices;
                if (devices == null) return false;
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                        if (ud.HidHideEnabled) return true;
                }
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
                // Recalculate input suppression sets after save pushes ViewModel mappings to PadSettings.
                _inputService.ApplyDeviceHiding();
            };
            _settingsService.AutoSaved += (s, e) =>
            {
                if (SettingsManager.ActiveProfileId == null)
                    _inputService.RefreshDefaultSnapshot();
                // Recalculate input suppression sets after save pushes ViewModel mappings to PadSettings.
                _inputService.ApplyDeviceHiding();
            };
            _viewModel.Settings.ReloadRequested += (s, e) => _settingsService.Reload();
            _viewModel.Settings.ResetRequested += (s, e) => _settingsService.ResetToDefaults();
            _viewModel.Settings.OpenSettingsFolderRequested += OnOpenSettingsFolder;
            _viewModel.Settings.ThemeChanged += OnThemeChanged;
            _viewModel.Settings.NewProfileRequested += OnNewProfile;
            _viewModel.Settings.SaveAsProfileRequested += OnSaveAsProfile;
            _viewModel.Settings.DeleteProfileRequested += OnDeleteProfile;
            _viewModel.Settings.ExportProfileRequested += OnExportProfile;
            _viewModel.Settings.ImportProfileRequested += OnImportProfile;
            _viewModel.Settings.EditProfileRequested += OnEditProfile;
            _viewModel.Settings.LoadProfileRequested += OnLoadProfile;
            _viewModel.Settings.RevertToDefaultRequested += OnRevertToDefault;

            // Persist Settings VM changes (theme, polling, checkboxes) and handle login toggle.
            _viewModel.Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.StartAtLogin))
                    Common.StartupHelper.SetStartupEnabled(_viewModel.Settings.StartAtLogin);

                if (e.PropertyName is nameof(SettingsViewModel.SelectedThemeIndex)
                     or nameof(SettingsViewModel.AutoStartEngine)
                     or nameof(SettingsViewModel.MinimizeToTray)
                     or nameof(SettingsViewModel.StartMinimized)
                     or nameof(SettingsViewModel.StartAtLogin)
                     or nameof(SettingsViewModel.EnablePollingOnFocusLoss)
                     or nameof(SettingsViewModel.PollingRateMs)
                     or nameof(SettingsViewModel.HmInactivityDestroyTimeoutSeconds)
                     or nameof(SettingsViewModel.EnableInputHiding)
                     or nameof(SettingsViewModel.KeepHidHideCloaksBetweenLaunches)
                     or nameof(SettingsViewModel.Use2DControllerView)
                     or nameof(SettingsViewModel.EnableAutoProfileSwitching))
                    _settingsService.MarkDirty();
            };

            // Persist DSU / web controller server settings on change (Dashboard VM).
            _viewModel.Dashboard.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DashboardViewModel.EnableDsuMotionServer)
                     or nameof(DashboardViewModel.DsuMotionServerPort)
                     or nameof(DashboardViewModel.EnableWebController)
                     or nameof(DashboardViewModel.WebControllerPort)
                     or nameof(DashboardViewModel.EnableTouchpadOverlay)
                     or nameof(DashboardViewModel.TouchpadOverlayOpacity)
                     or nameof(DashboardViewModel.TouchpadOverlayMonitor)
                     or nameof(DashboardViewModel.TouchpadOverlayLeft)
                     or nameof(DashboardViewModel.TouchpadOverlayTop)
                     or nameof(DashboardViewModel.TouchpadOverlayWidth)
                     or nameof(DashboardViewModel.TouchpadOverlayHeight))
                    _settingsService.MarkDirty();
            };

            // Wire HidHide install/uninstall commands.
            _viewModel.Settings.InstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_InstallingHidHide, DriverInstaller.InstallHidHide, RefreshHidHideStatus);
            _viewModel.Settings.UninstallHidHideRequested += async (s, e) => await RunDriverOperationAsync(
                Strings.Instance.Status_UninstallingHidHide, DriverInstaller.UninstallHidHide, RefreshHidHideStatus);

            // Wire HidHide whitelist add (file browser).
            _viewModel.Settings.AddWhitelistPathRequested += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Strings.Instance.FileDialog_SelectWhitelist,
                    Filter = Strings.Instance.FileDialog_ExeFilter,
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(this) == true)
                {
                    string path = dlg.FileName;
                    if (!_viewModel.Settings.HidHideWhitelistPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _viewModel.Settings.HidHideWhitelistPaths.Add(path);
                        _viewModel.Settings.RaiseWhitelistChanged();
                    }
                }
            };

            // Wire HidHide whitelist changes → re-apply device hiding.
            _viewModel.Settings.WhitelistChanged += (s, e) =>
            {
                _inputService?.ApplyDeviceHiding();
            };

            // Wire MIDI Services install/uninstall commands.
            _viewModel.Settings.InstallMidiServicesRequested += async (s, e) =>
            {
                _viewModel.StatusText = Strings.Instance.Status_DownloadingMidi;
                DriverOverlayText.Text = Strings.Instance.Status_DownloadingInstallingMidi;
                DriverOverlay.Visibility = Visibility.Visible;
                try
                {
                    await DriverInstaller.InstallMidiServicesAsync();
                    _viewModel.StatusText = Strings.Instance.Common_Ready;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _viewModel.StatusText = Strings.Instance.Status_OperationCancelled;
                }
                catch (Exception ex)
                {
                    _viewModel.StatusText = string.Format(Strings.Instance.Status_MidiInstallFailed_Format, ex.Message);
                }
                finally
                {
                    DriverOverlay.Visibility = Visibility.Collapsed;
                    RefreshMidiServicesStatus();
                }
            };
            _viewModel.Settings.UninstallMidiServicesRequested += async (s, e) =>
            {
                // The uninstall guard prevents this when MIDI slots are active, but
                // MIDI *input* enumeration (issue #128) loads the SDK runtime whenever
                // services are installed — tear those connections down first.
                _inputService?.ShutdownMidiInputs();
                // Abandon the initializer rather than disposing it — Dispose() calls
                // into the runtime, which crashes if the service is being removed.
                Common.Input.MidiVirtualController.Shutdown(skipDispose: true);
                await RunDriverOperationAsync(
                    Strings.Instance.Status_UninstallingMidi, DriverInstaller.UninstallMidiServices, RefreshMidiServicesStatus);
            };

            // Wire device service events (assign to slot, hide, etc.).
            _deviceService.WireEvents();

            // Refresh PadPage dropdowns and Devices-page slot buttons after assignment changes.
            _deviceService.DeviceAssignmentChanged += (s, e) =>
            {
                // Assigning a device auto-maps it and rebuilds the slot's
                // MappingSet, leaving every pad's Mappings ViewModel momentarily
                // behind. RefreshDeviceList below re-selects the slot's device,
                // which fires OnSelectedDeviceChanged → SaveViewModelToPadSetting
                // BEFORE RefreshMappingsToViewModel reloads the ViewModel. Mark
                // the mapping views stale up front so that save skips its
                // destructive clear+rewrite instead of wiping the fresh auto-map
                // (the DualSense-to-an-occupied-slot "only the Share button maps"
                // bug; see PadViewModel.MappingsViewLoaded). RefreshMappingsToViewModel
                // clears the flag again once the ViewModel is current.
                foreach (var p in _viewModel.Pads)
                    p.MappingsViewLoaded = false;

                _inputService.RefreshDeviceList();
                _viewModel.Devices.RefreshSlotButtons();

                // Issue #83 — controller-audio sinks follow assignments.
                PadForge.Common.Input.AudioPassthroughService.Reconcile();

                // Issue #61 fix — bring the per-VC MappingSets up to
                // date with every assigned device's PadSetting BEFORE
                // re-syncing the PadViewModels. Adding a new device
                // populates its PadSetting with auto-mapped descriptors;
                // running the additive merge here makes those sources
                // visible in the Mappings tab immediately, instead of
                // only after the user toggles the Device dropdown
                // (which used to be the only thing that re-pulled
                // descriptors from the new device's PadSetting).
                SettingsService.RefreshMappingSetsFromLegacy();

                // Two distinct refreshes per pad. They're independent
                // now that the Mappings tab is fully decoupled from the
                // assigned-device dropdown:
                //   • RefreshMappingsToViewModel — per-VC mapping pass,
                //     reads the slot's MappingSet (just updated by
                //     RefreshMappingSetsFromLegacy above with the new
                //     device's auto-mapped sources). Always runs.
                //   • LoadPadSettingToViewModel — per-device TUNING load
                //     (deadzones, FFB, lighting, etc.). Runs only when
                //     a device is selected, since tuning needs a device.
                // Previously these were collapsed into a single call,
                // which meant the mapping refresh was gated on having a
                // selected device — that broke auto-mapping visibility
                // after first assignment when the dropdown was still
                // pointing at Empty.
                for (int i = 0; i < _viewModel.Pads.Count; i++)
                {
                    var padVm = _viewModel.Pads[i];
                    InputService.RefreshMappingsToViewModel(padVm);
                    var selected = padVm.SelectedMappedDevice;
                    if (selected != null && selected.InstanceGuid != Guid.Empty)
                        InputService.LoadPadSettingToViewModel(padVm, selected.InstanceGuid);
                    _inputService.RefreshAvailableInputsForSlot(padVm);
                }
            };

            // Re-apply device hiding when a toggle changes.
            _deviceService.DeviceHidingStateChanged += (s, e) =>
            {
                _inputService.ApplyDeviceHiding();
                _inputService.RefreshMappingDropdowns();
                _viewModel.Settings.RefreshDriverGuards();
            };

            // After assigning a device to a slot, navigate to that controller page.
            _deviceService.NavigateToSlotRequested += (s, slotIndex) => NavigateToSlot(slotIndex);

            // Engine signaled an HM virtual controller's inactivity timeout
            // fired (all mapped devices offline past the configured threshold).
            // Tear down only the inactive slot's live VC (so its kernel
            // slot frees) and run the Xbox bubble-up cascade. The slot
            // configuration (mappings, profile, devices, SlotCreated,
            // SlotEnabled, SlotOrders position) is durable and stays in
            // PadForge.xml. The slot transitions to "awaiting devices"
            // and recreates its VC automatically when its mapped devices
            // come back online.
            _inputService.SlotInactivityTimedOut += (s, padIndex) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _inputService.OnSlotInactivityTimedOut(padIndex);
                }));
            };

            // After a profile apply, replicate the user's manual fix
            // (click the controller card on the sidebar) by running the
            // same codified gesture they perform: SelectNavItemByTag
            // flips IsActive on every NavigationViewItem before calling
            // NavigateToTag. Deferred to Background so the sidebar
            // rebuild settles first.

            // Wire devices page refresh.
            _viewModel.Devices.RefreshRequested += (s, e) =>
            {
                _inputService.RefreshDeviceList();
                _viewModel.StatusText = Strings.Instance.Status_DeviceListRefreshed;
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
                    // Controller preview tab: rumble all devices in the slot (null = no filter).
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, null, true, false);
                };
                pad.TestRightMotorRequested += (s, e) =>
                {
                    // Controller preview tab: rumble all devices in the slot (null = no filter).
                    if (s is PadViewModel pvm)
                        _inputService.SendTestRumble(pvm.PadIndex, null, false, true);
                };
                pad.TestLeftImpulseTriggerRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestImpulseTrigger(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid, true, false);
                };
                pad.TestRightImpulseTriggerRequested += (s, e) =>
                {
                    if (s is PadViewModel pvm)
                        _inputService.SendTestImpulseTrigger(pvm.PadIndex, pvm.SelectedMappedDevice?.InstanceGuid, false, true);
                };

                // v3.3 — Gyro tab calibrate / reset wired to the slot's
                // currently-selected mapped device. Both bias AND tuning
                // are per-(device, slot) on the PadSetting; the handlers
                // look up the slot's PadSetting via the same key the
                // mapping editor uses (InstanceGuid + slot index).
                pad.GyroCalibrateRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    var selected = pvm.SelectedMappedDevice;
                    if (selected == null || selected.InstanceGuid == Guid.Empty) return;
                    var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(selected.InstanceGuid);
                    if (ud == null || !ud.HasGyro) return;
                    var us = PadForge.Common.Input.SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, pvm.PadIndex);
                    var ps = us?.GetPadSetting();
                    if (ps == null) return;
                    pvm.GyroCalibrationLabel = PadForge.Resources.Strings.Strings.Instance.Settings_GyroCalibrating;
                    _ = _inputService.GyroCalibrator.RecalibrateAsync(ud, ps);
                };
                pad.GyroResetCalibrationRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    var selected = pvm.SelectedMappedDevice;
                    if (selected == null || selected.InstanceGuid == Guid.Empty) return;
                    var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(selected.InstanceGuid);
                    if (ud == null || !ud.HasGyro) return;
                    var us = PadForge.Common.Input.SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, pvm.PadIndex);
                    var ps = us?.GetPadSetting();
                    if (ps == null) return;
                    _inputService.GyroCalibrator.ResetCalibration(ps);
                    _inputService.ClearGyroAutoCalibLatch(ud.InstanceGuid, pvm.PadIndex);
                    pvm.GyroCalibrationLabel = PadForge.Resources.Strings.Strings.Instance.Settings_GyroNeverCalibrated;
                };

                // Record button on the Aim Engage picker. Toggles like
                // the mapping table's per-row record: idle click starts
                // a freeform recorder session, click again while
                // recording cancels. Result lands directly in
                // GyroAimEngageButton + DeviceGuid.
                pad.GyroAimEngageRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    if (pvm.GyroAimEngageRecording)
                    {
                        _recorderService.CancelRecording();
                        pvm.GyroAimEngageRecording = false;
                        return;
                    }
                    pvm.GyroAimEngageRecording = true;
                    // Callback param order matches RecorderService.CompleteRecording's
                    // fb(fbGuid, fbDesc) call site — deviceGuid first, descriptor second.
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        pvm.GyroAimEngageButton = descriptor ?? "";
                        pvm.GyroAimEngageDeviceGuid = deviceGuid ?? "";
                        pvm.GyroAimEngageRecording = false;
                        _settingsService.MarkDirty();
                    });
                };

                // Record buttons on the two Trigger Routing activators (#102).
                // Same freeform-recorder toggle as the Aim Engage picker above.
                pad.LeftTriggerRouteActivatorRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    if (pvm.LeftTriggerRouteActivatorRecording)
                    {
                        _recorderService.CancelRecording();
                        pvm.LeftTriggerRouteActivatorRecording = false;
                        return;
                    }
                    pvm.LeftTriggerRouteActivatorRecording = true;
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        pvm.LeftTriggerRouteActivator = descriptor ?? "";
                        pvm.LeftTriggerRouteActivatorDeviceGuid = deviceGuid ?? "";
                        pvm.LeftTriggerRouteActivatorRecording = false;
                        _settingsService.MarkDirty();
                    });
                };

                pad.RightTriggerRouteActivatorRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    if (pvm.RightTriggerRouteActivatorRecording)
                    {
                        _recorderService.CancelRecording();
                        pvm.RightTriggerRouteActivatorRecording = false;
                        return;
                    }
                    pvm.RightTriggerRouteActivatorRecording = true;
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        pvm.RightTriggerRouteActivator = descriptor ?? "";
                        pvm.RightTriggerRouteActivatorDeviceGuid = deviceGuid ?? "";
                        pvm.RightTriggerRouteActivatorRecording = false;
                        _settingsService.MarkDirty();
                    });
                };
            }

            // Wire recorder for each pad's mapping rows.
            // Also listen for CollectionChanged so new mappings (from RebuildMappings) get wired.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;

                // Wire existing mappings.
                foreach (var mapping in pad.Mappings)
                    WireMappingItemEvents(mapping, capturedPad);

                // Re-wire when mappings are rebuilt (OutputType or Extended config change).
                pad.Mappings.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MappingItem mi in e.NewItems)
                            WireMappingItemEvents(mi, capturedPad);
                    }
                };

                // Pad setting changes (deadzones, force feedback, etc.) trigger autosave.
                pad.PropertyChanged += (s, e) =>
                {
                    // Touchpad-gesture properties get the full
                    // MarkDirty + Sync + RefreshAvailableInputsForSlot
                    // treatment so the picker re-reads PadSetting and
                    // the next save commits the gesture-tab toggle.
                    //
                    // CRITICAL: this branch MUST NOT trip for the
                    // non-touchpad scalar / gyro fields below. During
                    // startup load (LoadPadSettingToViewModel), those
                    // scalars get set from PadSetting one by one — if
                    // we called Sync for each of them, Sync would write
                    // the VM's transient touchpad-fields defaults (all
                    // false) into PadSetting BEFORE
                    // LoadTouchpadGestureSettingsForActiveDevice ever
                    // runs to populate them from the loaded entry.
                    // Result: on every relaunch, the loaded TouchpadSettings
                    // entry gets clobbered by the VM's empty defaults
                    // and persisted as false. The toggle reverts.
                    bool isTouchpadField = e.PropertyName is
                        nameof(PadViewModel.TouchpadGesturesEnabled) or
                        nameof(PadViewModel.TouchpadGestureMode) or
                        nameof(PadViewModel.TouchpadCooldownMs) or
                        nameof(PadViewModel.TouchpadEnableFourWaySwipes) or
                        nameof(PadViewModel.TouchpadEnableEightWaySwipes) or
                        nameof(PadViewModel.TouchpadSwipeDistanceThreshold) or
                        nameof(PadViewModel.TouchpadSwipeTimeWindowMs) or
                        nameof(PadViewModel.TouchpadEnableRadialZones) or
                        nameof(PadViewModel.TouchpadRadialZoneCount) or
                        nameof(PadViewModel.TouchpadRadialCenterDeadzone) or
                        nameof(PadViewModel.TouchpadEnableTaps) or
                        nameof(PadViewModel.TouchpadTapTimeWindowMs) or
                        nameof(PadViewModel.TouchpadMultiTapGapMs) or
                        nameof(PadViewModel.TouchpadEnableLongPress) or
                        nameof(PadViewModel.TouchpadLongPressTimeWindowMs) or
                        nameof(PadViewModel.TouchpadEnableTwoFingerSwipes) or
                        nameof(PadViewModel.TouchpadEnablePinchSpread) or
                        nameof(PadViewModel.TouchpadEnableRotate) or
                        nameof(PadViewModel.TouchpadEnableThreeFingerGestures) or
                        nameof(PadViewModel.TouchpadEnableFourFingerGestures) or
                        nameof(PadViewModel.TouchpadEnableFiveFingerGestures) or
                        nameof(PadViewModel.TouchpadEnableShapeGestures) or
                        nameof(PadViewModel.TouchpadGestureMatchThreshold) or
                        nameof(PadViewModel.TouchpadEnableJoystickOutput) or
                        nameof(PadViewModel.TouchpadJoystickMaxRadius) or
                        nameof(PadViewModel.TouchpadJoystickInnerDeadzone) or
                        nameof(PadViewModel.TouchpadJoystickDPadMode) or
                        nameof(PadViewModel.TouchpadJoystickDPadActivationThreshold) or
                        nameof(PadViewModel.TouchpadMouseSensitivityX) or
                        nameof(PadViewModel.TouchpadMouseSensitivityY) or
                        nameof(PadViewModel.TouchpadMouseInvertX) or
                        nameof(PadViewModel.TouchpadMouseInvertY);

                    if (isTouchpadField)
                    {
                        _settingsService.MarkDirty();
                        if (s is PadViewModel pvm2)
                        {
                            // Order matters: the VM setter fires this
                            // PropertyChanged BEFORE its tail-call
                            // SyncTouchpadGestureSettingsToActiveDevice
                            // writes the new value into
                            // PadSetting.TouchpadSettings. Calling Sync
                            // explicitly first ensures PadSetting is up
                            // to date when the picker re-reads it.
                            pvm2.SyncTouchpadGestureSettingsToActiveDevice();
                            _inputService?.RefreshAvailableInputsForSlot(pvm2);
                        }
                        return;
                    }

                    // Scalar tuning fields — deadzones, sticks, triggers,
                    // FFB, audio rumble, constant force, gyro. Just mark
                    // dirty; the next autosave's UpdatePadSettingsFromViewModels
                    // pass writes them all to PadSetting from the VM.
                    if (e.PropertyName is
                        nameof(PadViewModel.LeftDeadZoneX) or nameof(PadViewModel.LeftDeadZoneY) or
                        nameof(PadViewModel.RightDeadZoneX) or nameof(PadViewModel.RightDeadZoneY) or
                        nameof(PadViewModel.LeftAntiDeadZoneX) or nameof(PadViewModel.LeftAntiDeadZoneY) or
                        nameof(PadViewModel.RightAntiDeadZoneX) or nameof(PadViewModel.RightAntiDeadZoneY) or
                        nameof(PadViewModel.LeftLinear) or nameof(PadViewModel.RightLinear) or
                        nameof(PadViewModel.LeftSensitivityCurveX) or nameof(PadViewModel.LeftSensitivityCurveY) or
                        nameof(PadViewModel.RightSensitivityCurveX) or nameof(PadViewModel.RightSensitivityCurveY) or
                        nameof(PadViewModel.LeftTriggerSensitivityCurve) or nameof(PadViewModel.RightTriggerSensitivityCurve) or
                        nameof(PadViewModel.LeftMaxRangeX) or nameof(PadViewModel.LeftMaxRangeY) or
                        nameof(PadViewModel.RightMaxRangeX) or nameof(PadViewModel.RightMaxRangeY) or
                        nameof(PadViewModel.LeftCenterOffsetX) or nameof(PadViewModel.LeftCenterOffsetY) or
                        nameof(PadViewModel.RightCenterOffsetX) or nameof(PadViewModel.RightCenterOffsetY) or
                        nameof(PadViewModel.LeftTriggerDeadZone) or nameof(PadViewModel.RightTriggerDeadZone) or
                        nameof(PadViewModel.LeftTriggerAntiDeadZone) or nameof(PadViewModel.RightTriggerAntiDeadZone) or
                        nameof(PadViewModel.LeftTriggerMaxRange) or nameof(PadViewModel.RightTriggerMaxRange) or
                        nameof(PadViewModel.ForceOverallGain) or nameof(PadViewModel.LeftMotorStrength) or
                        nameof(PadViewModel.RightMotorStrength) or nameof(PadViewModel.SwapMotors) or
                        nameof(PadViewModel.WheelRotationRange) or nameof(PadViewModel.WheelAutoCenter) or
                        nameof(PadViewModel.WheelRpmLeds) or
                        nameof(PadViewModel.ImpulseOverallGain) or
                        nameof(PadViewModel.ImpulseLeftStrength) or nameof(PadViewModel.ImpulseRightStrength) or
                        nameof(PadViewModel.ImpulseSwapTriggers) or
                        nameof(PadViewModel.AudioRumbleEnabled) or nameof(PadViewModel.AudioRumbleSensitivity) or
                        nameof(PadViewModel.AudioRumbleCutoffHz) or nameof(PadViewModel.AudioRumbleLeftMotor) or
                        nameof(PadViewModel.AudioRumbleRightMotor) or
                        nameof(PadViewModel.AudioRumbleTriggersEnabled) or
                        nameof(PadViewModel.AudioRumbleTriggersSensitivity) or nameof(PadViewModel.AudioRumbleTriggersCutoffHz) or
                        nameof(PadViewModel.AudioRumbleLeftTrigger) or nameof(PadViewModel.AudioRumbleRightTrigger) or
                        nameof(PadViewModel.ConstantForceEnabled) or
                        nameof(PadViewModel.ConstantForceX) or nameof(PadViewModel.ConstantForceY) or
                        nameof(PadViewModel.ConstantTriggerForceEnabled) or
                        nameof(PadViewModel.ConstantTriggerForceLeft) or nameof(PadViewModel.ConstantTriggerForceRight) or
                        // Trigger rumble routing (#102). Per-trigger Source / Mode / Scale / Activator,
                        // all per-(device, slot). Without these the card's edits never mark dirty and
                        // revert on restart (the Activator descriptor is also marked dirty by its record
                        // handler, but the dropdowns and scale need this gate too).
                        nameof(PadViewModel.LeftTriggerRouteSource) or nameof(PadViewModel.RightTriggerRouteSource) or
                        nameof(PadViewModel.LeftTriggerRouteMode) or nameof(PadViewModel.RightTriggerRouteMode) or
                        nameof(PadViewModel.LeftTriggerRouteScale) or nameof(PadViewModel.RightTriggerRouteScale) or
                        nameof(PadViewModel.LeftTriggerRouteActivator) or nameof(PadViewModel.RightTriggerRouteActivator) or
                        nameof(PadViewModel.LeftTriggerRouteActivatorDeviceGuid) or nameof(PadViewModel.RightTriggerRouteActivatorDeviceGuid) or
                        nameof(PadViewModel.LeftTriggerRouteActivatorMode) or nameof(PadViewModel.RightTriggerRouteActivatorMode) or
                        nameof(PadViewModel.OutputType) or
                        // Gyro tab — v3.3 + JoyShockMapper-canon fields all per-(device, slot).
                        nameof(PadViewModel.GyroSensitivityH) or nameof(PadViewModel.GyroSensitivityV) or
                        nameof(PadViewModel.GyroDeadZoneDegPerSec) or nameof(PadViewModel.GyroSmoothingAlpha) or
                        nameof(PadViewModel.GyroAcceleration) or nameof(PadViewModel.GyroOutputCurve) or
                        nameof(PadViewModel.GyroSensitivityUnits) or nameof(PadViewModel.GyroEasyAimStickThreshold) or
                        nameof(PadViewModel.GyroSpace) or
                        nameof(PadViewModel.GyroPlayerSpaceYawRelaxFactor) or
                        nameof(PadViewModel.GyroWorldSpaceSideReductionThreshold) or
                        nameof(PadViewModel.GyroTighteningThresholdDegPerSec) or
                        nameof(PadViewModel.GyroSmoothingThresholdDegPerSec) or
                        nameof(PadViewModel.GyroSmoothingWindowMs) or
                        nameof(PadViewModel.GyroRealWorldCalibration) or
                        nameof(PadViewModel.GyroAimEngageButton) or nameof(PadViewModel.GyroAimEngageDeviceGuid) or
                        nameof(PadViewModel.GyroAimEngageMode) or
                        nameof(PadViewModel.GyroInvertPitch) or nameof(PadViewModel.GyroInvertYawRoll) or
                        nameof(PadViewModel.GyroApplyTuningToPassthrough) or
                        // Steering at-lock feedback (#94) — per-slot toggles + tunables.
                        nameof(PadViewModel.SteeringLockRumbleEnabled) or
                        nameof(PadViewModel.SteeringLockTriggerVibEnabled) or
                        nameof(PadViewModel.SteeringLockLightbarEnabled) or
                        nameof(PadViewModel.SteeringLockATResistanceEnabled) or
                        nameof(PadViewModel.SteeringLockPulseMs) or
                        nameof(PadViewModel.SteeringLockLightbarColor) or
                        nameof(PadViewModel.SteeringLockLightbarColorSource) or
                        nameof(PadViewModel.SteeringLockLightbarPaletteCsv) or
                        nameof(PadViewModel.SteeringLockLightbarHoldMs) or
                        nameof(PadViewModel.SteeringLockLightbarFadeMs))
                    {
                        _settingsService.MarkDirty();
                    }
                };

                // Extended custom stick/trigger config changes (indices 2+) trigger autosave.
                pad.ConfigItemDirtyCallback = () => _settingsService.MarkDirty();

                // Steering-mode change (incl. Reset all) re-stamps the engine MappingSets now
                // so the stick stops/starts steering immediately, not on the 2s autosave.
                pad.SteeringModeChangedCallback = () => _settingsService.PushUiExtraSourcesIntoSlotMappingSets();

                // ExtendedConfig property changes (preset, counts) trigger autosave.
                pad.ExtendedConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();

                // PlayStationConfig changes (Lighting tab, Adaptive Triggers tab)
                // — autosave + sync audio capture when audio-to-lightbar
                // toggles. Audio-to-lightbar reuses the same WASAPI capture
                // as audio-rumble, so the capture lifecycle gates on either
                // feature being on for any created slot.
                // Forwarded event follows the per-device PlayStationConfig
                // anchor across SelectedMappedDevice swaps. Subscribing
                // to pad.ActivePlayStationConfigPropertyChanged instead
                // of pad.PlayStationConfig.PropertyChanged means edits
                // on whichever device the user has selected route here.
                pad.ActivePlayStationConfigPropertyChanged += (s, e) =>
                {
                    _settingsService.MarkDirty();
                    if (e.PropertyName == nameof(ViewModels.PlayStationSlotConfig.AudioLightbarEnabled))
                        _inputService.SyncAudioBassDetector();
                };
            }

            // Recorder completion marks settings dirty + clear flash + advance Map All.
            _recorderService.RecordingCompleted += (s, result) =>
            {
                _settingsService.MarkDirty();
                var activePad = _viewModel.SelectedPad;
                if (activePad == null) return;

                // ─────────────────────────────────────────────────────────
                //  REGRESSION GUARD — the Device dropdown must NOT influence
                //  which physical device a recorded mapping is attached to.
                //
                //  The Mappings tab + the controller preview are PER-VC, not
                //  per-device. When the user records (preview quadrant click,
                //  per-row Record button, Map All), RecorderService listens to
                //  EVERY device assigned to the slot and the first one to fire
                //  wins — `RecorderService.CompleteRecording` then stamps the
                //  row (or extra source) with that winning device's GUID via
                //  `PrimarySourceDeviceGuid` / `MappingSourceItem.DeviceGuid`.
                //  That is the authoritative device assignment.
                //
                //  Therefore: do NOT re-stamp `PrimarySourceDeviceGuid` here
                //  from `activePad.SelectedMappedDevice`. A previous change did
                //  exactly that ("stamp from the dropdown so the row is
                //  device-correct immediately") and it OVERWROTE the correct
                //  winning-device GUID with whatever happened to be selected in
                //  the dropdown — so a button physically pressed on device A
                //  would get filed under device B. The row is already
                //  device-correct the moment CompleteRecording returns; the
                //  only thing that genuinely needed fixing was the per-VC
                //  MappingSet being stale until the next debounced save (see
                //  CommitRecordedMappingSet below).
                //
                //  `deviceGuid` here is ONLY a fallback for display-text
                //  resolution when a row somehow has no recorded device origin
                //  yet — it is never written back onto the mapping.
                // ─────────────────────────────────────────────────────────
                Guid deviceGuid = activePad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                // Pick the device whose DeviceObjects metadata is used to turn
                // a raw descriptor ("Button 5") into a friendly label ("A"):
                // the device that actually fired the recording (CompleteRecording
                // stamped its GUID on the row), NOT the dropdown selection.
                // Falls back to the dropdown device only when the row has no
                // recorded device origin — e.g. a Map All Y first-phase row
                // whose primary slot is still empty.
                Guid ResolveGuidFor(MappingItem m)
                    => (m != null && Guid.TryParse(m.PrimarySourceDeviceGuid, out var g) && g != Guid.Empty)
                        ? g : deviceGuid;

                // Push the just-recorded UI state into the in-memory per-VC
                // MappingSet right now, rather than waiting for the debounced
                // SaveToFile to call PushUiExtraSourcesIntoSlotMappingSets.
                // The Mappings DataGrid binds to the per-VC MappingItems (which
                // are already current after recording), but RefreshMappingsCore
                // re-hydrates those items FROM the MappingSet — so until the
                // MappingSet catches up, a tab switch / device-dropdown toggle /
                // device-assignment change would re-read stale rows. Committing
                // here keeps the MappingSet authoritative immediately, which is
                // the actual fix for "the Mappings table doesn't reflect what I
                // just recorded until I toggle the device dropdown".
                void CommitRecordedMappingSet() => _settingsService.PushUiExtraSourcesIntoSlotMappingSets();

                // ─────────────────────────────────────────────────────────
                //  Extra-source recording: the result targeted a source that
                //  was ADDED to the row, not the row's primary. This happens
                //  when:
                //    • the user clicked a stick quadrant on a row whose
                //      primary is already an analog axis (see the
                //      ControllerElementRecordRequested handler — recording
                //      another input there must NOT clobber the axis), or
                //    • the user used the per-row "+ Add source → Record"
                //      affordance on a multi-source row.
                //
                //  In both cases the correct behavior is: append the recorded
                //  source to the row (if it isn't already there — the per-row
                //  affordance adds it before recording, the quadrant path
                //  passes a detached MappingSourceItem so it lands here),
                //  commit, and STOP. We must NOT fall through to the primary-
                //  recording / bipolar-auto-prompt logic below: that would
                //  re-sync the PRIMARY's SelectedInput against this extra
                //  source's descriptor and could re-prompt for the negative
                //  direction — i.e. it would clobber the existing primary,
                //  which is exactly the bug this branch exists to prevent.
                // ─────────────────────────────────────────────────────────
                if (result.ExtraSource != null)
                {
                    var parent = result.Mapping;
                    if (parent != null && !parent.ExtraSources.Contains(result.ExtraSource))
                        parent.ExtraSources.Add(result.ExtraSource);  // fires EnsureCombineModeDefault + WireExtraSource via CollectionChanged
                    CommitRecordedMappingSet();
                    if (activePad.IsMapAllActive)
                        activePad.OnMapAllItemCompleted();
                    else
                        activePad.CurrentRecordingTarget = null;
                    return;
                }

                // ── Neg-recording mode: redirect result to NegSourceDescriptor ──
                if (_pendingNegMapping != null)
                {
                    var negMapping = _pendingNegMapping;
                    _pendingNegMapping = null;

                    if (result.Type == MapType.Axis && negMapping.HasNegDirection)
                    {
                        // A full analog axis covers both directions,
                        // so write it to the primary descriptor and clear neg.
                        negMapping.LoadDescriptor(result.Descriptor);
                        negMapping.NegSourceDescriptor = string.Empty;
                        _savedPosDescriptor = null;
                        var rgAxis = ResolveGuidFor(negMapping);
                        if (rgAxis != Guid.Empty)
                            InputService.ResolveDisplayText(negMapping, rgAxis);
                        negMapping.SyncSelectedInputFromDescriptor();
                        CommitRecordedMappingSet();

                        _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, negMapping.TargetLabel, negMapping.SourceDisplayText);

                        if (activePad.IsMapAllActive)
                            activePad.OnMapAllItemCompleted();
                        else
                            activePad.CurrentRecordingTarget = null;
                        return;
                    }

                    // Button recorded — write to neg descriptor.
                    // The recorder always writes to SourceDescriptor (line 358 of RecorderService),
                    // so undo that: redirect the value to NegSourceDescriptor instead.
                    negMapping.NegSourceDescriptor = result.Descriptor;
                    bool hadSavedPos = _savedPosDescriptor != null;
                    if (hadSavedPos)
                    {
                        // Came from auto-prompt (pos already recorded) — restore saved positive.
                        negMapping.SourceDescriptor = _savedPosDescriptor;
                        _savedPosDescriptor = null;
                    }
                    else
                    {
                        // First recording for this axis (e.g., Y first phase in Map All).
                        // The recorder contaminated SourceDescriptor — clear it so only
                        // NegSourceDescriptor holds this recording.
                        negMapping.SourceDescriptor = string.Empty;
                    }

                    var rgNeg = ResolveGuidFor(negMapping);
                    if (rgNeg != Guid.Empty)
                    {
                        InputService.ResolveDisplayText(negMapping, rgNeg);
                        InputService.ResolveNegDisplayText(negMapping, rgNeg);
                    }
                    negMapping.SyncSelectedInputFromDescriptor();

                    // Issue #61 — promote the freshly-recorded Neg into a
                    // visible ExtraSource on the row so the table reflects
                    // the bipolar pair immediately, without the user having
                    // to toggle the Device dropdown to trigger a load-time
                    // migration. CompleteRecording already stamped the row's
                    // device origin from the device that fired, so the
                    // promoted source inherits the correct DeviceGuid.
                    negMapping.PromoteNegDescriptorToExtraSource();

                    if (!hadSavedPos && negMapping.HasNegDirection && !activePad.IsMapAllActive)
                    {
                        // Came from a neg-quadrant click — now auto-prompt for the positive direction.
                        // (Map All handles the second phase itself via MapAllRecordingNeg.)
                        bool isXAxis = negMapping.TargetSettingName.Contains("AxisX")
                            || negMapping.TargetLabel.EndsWith(" X", StringComparison.Ordinal);
                        string dirHint = isXAxis ? Strings.Instance.Status_DirectionRight : Strings.Instance.Status_DirectionDown;
                        _viewModel.StatusText = string.Format(Strings.Instance.Status_NowMap_Format, negMapping.TargetLabel, dirHint);

                        // Switch to Controller tab so the 3D directional arrow is visible.
                        activePad.SelectedConfigTab = 0;

                        // Update recording target to pos for flash/arrow.
                        activePad.CurrentRecordingTarget = negMapping.TargetSettingName;

                        // Start recording — result will go to SourceDescriptor via normal path.
                        // Neutralize baseline so the previous POV/button press doesn't block detection.
                        _savedPosDescriptor = null;
                        _recorderService.StartRecording(negMapping, activePad.PadIndex, deviceGuid, neutralizeBaseline: true);
                        return;
                    }

                    CommitRecordedMappingSet();
                    _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, negMapping.TargetLabel, negMapping.SourceDisplayText);

                    if (activePad.IsMapAllActive)
                    {
                        if (!hadSavedPos)
                        {
                            // Y axis first phase came through _pendingNegMapping (neg=up recorded).
                            // Tell Map All a second phase (pos=down) is still needed.
                            activePad.MapAllRecordingNeg = true;
                        }
                        else
                        {
                            // X axis: both pos and neg were recorded in one round
                            // (normal path auto-prompted neg). Clear the flag so
                            // OnMapAllItemCompleted advances to the next mapping.
                            activePad.MapAllRecordingNeg = false;
                        }
                        activePad.OnMapAllItemCompleted();
                    }
                    else
                        activePad.CurrentRecordingTarget = null;
                    return;
                }

                // ── Normal recording ──
                var rgNormal = ResolveGuidFor(result.Mapping);
                if (rgNormal != Guid.Empty)
                    InputService.ResolveDisplayText(result.Mapping, rgNormal);
                result.Mapping.SyncSelectedInputFromDescriptor();

                // If a directional input (button, POV, slider) was recorded for a bidirectional axis,
                // auto-prompt for neg direction (but only if neg isn't already mapped — avoids
                // re-prompting after a neg-quadrant click that already auto-prompted for pos).
                if (result.Type != MapType.Axis && result.Mapping.HasNegDirection
                    && string.IsNullOrEmpty(result.Mapping.NegSourceDescriptor))
                {
                    // Save the positive descriptor before the recorder overwrites it.
                    _savedPosDescriptor = result.Mapping.SourceDescriptor;
                    _pendingNegMapping = result.Mapping;

                    // Neg X = left, Neg Y = up (Y inverted by NegateAxis in Step 3).
                    bool isXAxis2 = result.Mapping.TargetSettingName.Contains("AxisX")
                        || result.Mapping.TargetLabel.EndsWith(" X", StringComparison.Ordinal);
                    string dirHint = isXAxis2 ? Strings.Instance.Status_DirectionLeft : Strings.Instance.Status_DirectionUp;
                    _viewModel.StatusText = string.Format(Strings.Instance.Status_NowMap_Format, result.Mapping.TargetLabel, dirHint);

                    if (activePad.IsMapAllActive)
                    {
                        activePad.MapAllRecordingNeg = true;
                        // Update the Map All overlay prompt to show the correct direction.
                        int idx = activePad.MapAllCurrentIndex;
                        activePad.MapAllPromptText = string.Format(Strings.Instance.Status_MapPrompt_Format, result.Mapping.TargetLabel, dirHint, idx + 1, activePad.Mappings.Count);
                    }

                    // Switch to Controller tab so the 3D directional arrow is visible.
                    activePad.SelectedConfigTab = 0;

                    // Update the recording target to the neg setting for flash/arrow.
                    activePad.CurrentRecordingTarget = result.Mapping.NegSettingName;

                    // Start recording again for the neg direction.
                    // Neutralize baseline so the previous POV/button press doesn't block detection.
                    _recorderService.StartRecording(result.Mapping, activePad.PadIndex, deviceGuid,
                        neutralizeBaseline: true, negRecording: true);
                    return;
                }

                // If a full analog axis was recorded for a bidirectional target, clear neg (axis covers both directions).
                if (result.Mapping.HasNegDirection && result.Type == MapType.Axis)
                {
                    result.Mapping.NegSourceDescriptor = string.Empty;
                }

                CommitRecordedMappingSet();
                _viewModel.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, result.Mapping.TargetLabel, result.Mapping.SourceDisplayText);

                if (activePad.IsMapAllActive)
                    activePad.OnMapAllItemCompleted();
                else
                    activePad.CurrentRecordingTarget = null;
            };

            // Recording timeout clears flash + advances Map All.
            _recorderService.RecordingTimedOut += (s, e) =>
            {
                // If we were waiting for neg, restore the positive descriptor.
                if (_pendingNegMapping != null && _savedPosDescriptor != null)
                    _pendingNegMapping.SourceDescriptor = _savedPosDescriptor;
                _pendingNegMapping = null;
                _savedPosDescriptor = null;

                var activePad = _viewModel.SelectedPad;
                if (activePad != null)
                {
                    if (activePad.IsMapAllActive)
                        activePad.OnMapAllItemCompleted();
                    else
                        activePad.CurrentRecordingTarget = null;
                    // Aim Engage Record button: timeout also clears the
                    // recording flag so the icon flips back to Record.
                    if (activePad.GyroAimEngageRecording)
                        activePad.GyroAimEngageRecording = false;
                    // Trigger Routing activator record buttons (#102).
                    if (activePad.LeftTriggerRouteActivatorRecording)
                        activePad.LeftTriggerRouteActivatorRecording = false;
                    if (activePad.RightTriggerRouteActivatorRecording)
                        activePad.RightTriggerRouteActivatorRecording = false;
                }
            };

            // Wire click-to-record from controller visual elements.
            PadPageView.ControllerElementRecordRequested += (s, targetName) =>
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm == null) return;

                // Toggle: if already recording this element, cancel.
                if (padVm.CurrentRecordingTarget == targetName)
                {
                    _recorderService.CancelRecording();
                    padVm.CurrentRecordingTarget = null;
                    _pendingNegMapping = null;
                    _savedPosDescriptor = null;
                    return;
                }

                // Check if this is a neg target (e.g., "LeftThumbAxisXNeg").
                bool isNegTarget = targetName.EndsWith("Neg", StringComparison.Ordinal);
                string posTargetName = isNegTarget ? targetName.Substring(0, targetName.Length - 3) : targetName;

                var mapping = padVm.Mappings.FirstOrDefault(m =>
                    string.Equals(m.TargetSettingName, posTargetName, StringComparison.OrdinalIgnoreCase));
                if (mapping == null) return;

                // NOTE: this `deviceGuid` is passed to StartRecording purely
                // for API shape — RecorderService ignores it and listens to
                // EVERY device on the slot (the first to fire wins). The
                // dropdown selection has no bearing on what gets recorded; see
                // the big REGRESSION GUARD comment in the RecordingCompleted
                // handler above.
                Guid deviceGuid = padVm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                // ─────────────────────────────────────────────────────────
                //  Don't let a quadrant click clobber an already-mapped axis.
                //
                //  If a bipolar stick-axis row (HasNegDirection) already has a
                //  full analog axis / slider as its primary, the user clicking
                //  one of the directional quadrants and recording a button is
                //  asking to ADD a button-direction ON TOP of the axis — not
                //  to replace the axis. The default primary-recording path
                //  would overwrite SourceDescriptor with the button (and, for
                //  the neg quadrant, also clear the primary), destroying the
                //  axis mapping. Instead, route the recording into a NEW extra
                //  source:
                //    • Pass a DETACHED MappingSourceItem (not yet in
                //      mapping.ExtraSources). RecorderService.CompleteRecording
                //      writes the descriptor/device/Invert onto it, then the
                //      RecordingCompleted handler appends it to the row (which
                //      is also what triggers EnsureCombineModeDefault so the
                //      row picks MaxAbs and the axis + button coexist). If the
                //      user cancels / times out, the detached item is simply
                //      discarded — nothing to clean up.
                //    • negRecording: isNegTarget so a button recorded for the
                //      negative quadrant is stored with Invert=true → the
                //      engine reads a press as the −1 (negative) direction.
                //
                //  Empty primaries, or button-class primaries, keep the classic
                //  "first mapping fills the primary; neg quadrant fills
                //  NegSourceDescriptor (then auto-prompts for the positive)"
                //  flow untouched — see the neg-recording branch in
                //  RecordingCompleted. We deliberately scope this to ANALOG
                //  primaries only: that is the precise case where the old
                //  behavior silently destroyed an existing mapping.
                // ─────────────────────────────────────────────────────────
                if (mapping.HasNegDirection && DescriptorIsAnalogAxis(mapping.SourceDescriptor))
                {
                    var extra = new MappingSourceItem
                    {
                        Kind = "Direct",
                        DeadZone = mapping.MappingDeadZone,
                        // Descriptor / DeviceGuid / Invert are filled in by
                        // RecorderService.CompleteRecording once an input fires.
                    };
                    _recorderService.StartRecordingExtraSource(mapping, extra, padVm.PadIndex,
                        negRecording: isNegTarget);
                    if (_recorderService.IsRecording)
                        padVm.CurrentRecordingTarget = targetName;
                    return;
                }

                if (isNegTarget)
                    _pendingNegMapping = mapping;

                _recorderService.StartRecording(mapping, padVm.PadIndex, deviceGuid,
                    negRecording: isNegTarget);

                // Only show arrow/flash if recording actually started (device available).
                if (_recorderService.IsRecording)
                    padVm.CurrentRecordingTarget = targetName;
            };

            // Wire Map All events for each pad.
            foreach (var pad in _viewModel.Pads)
            {
                var capturedPad = pad;
                pad.MapAllRecordRequested += (s, mapping) =>
                {
                    Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                    // Y axes record neg (up in game) first due to NegateAxis inversion.
                    // Pre-set _pendingNegMapping so the recorder result goes to NegSourceDescriptor.
                    // For Extended custom sticks: label ends with " Y" (e.g. "Stick 1 Y").
                    bool isYAxis = mapping.HasNegDirection
                        && (mapping.TargetSettingName.Contains("AxisY")
                            || mapping.TargetLabel.EndsWith(" Y", StringComparison.Ordinal));
                    bool isYFirstPhase = isYAxis && !capturedPad.MapAllRecordingNeg;
                    if (isYFirstPhase)
                        _pendingNegMapping = mapping;

                    // For non-Y bidirectional axes (X), clear stale neg descriptor so the
                    // auto-prompt for the opposite direction fires after the first button
                    // is recorded (auto-prompt is gated by NegSourceDescriptor being empty).
                    if (mapping.HasNegDirection && !isYAxis)
                        mapping.NegSourceDescriptor = string.Empty;

                    _recorderService.StartRecording(mapping, capturedPad.PadIndex, deviceGuid,
                        negRecording: isYFirstPhase);
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
                {
                    WireMacroRecording(macro, capturedPad.PadIndex);
                    WireMacroDirty(macro);
                }

                // Wire macros added later + mark dirty on add/remove.
                pad.Macros.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (MacroItem macro in e.NewItems)
                        {
                            WireMacroRecording(macro, capturedPad.PadIndex);
                            WireMacroDirty(macro);
                        }
                    }
                    _settingsService.MarkDirty();
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

                    // Capture VC-active state BEFORE the delete so the
                    // bubble-down cascade in OnSlotDeleted knows whether
                    // the removed slot was holding a kernel slot. If it
                    // wasn't, the active VCs at higher positions don't
                    // need to bubble down — their kernel slots weren't
                    // displaced by an inactive neighbor's removal.
                    bool hadActiveVc = _inputService.IsHmVcAt(slotIndex);
                    var info = _deviceService.DeleteSlot(slotIndex);
                    _inputService.OnSlotDeleted(slotIndex, info.Type, info.OldGroupPosition,
                        deletedSlotHadActiveVc: hadActiveVc);
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
                {
                    // Show "Stopping" state immediately, then run the heavy
                    // engine teardown on a thread-pool task so the UI stays
                    // responsive while AwaitPendingLifecycleTasks +
                    // DestroyAllVirtualControllers + HMContext.Dispose
                    // grind through (multi-second per Microsoft xinputhid
                    // VC).  Sidebar rebuild fires when the teardown
                    // completes.
                    _viewModel.IsEngineRunning = false;
                    _viewModel.Dashboard.EngineStateKey = "Stopping";
                    _viewModel.Dashboard.EngineStatus = Strings.Instance.Common_Stopping;
                    _viewModel.StatusText = Strings.Instance.Status_StoppingEngine;
                    _viewModel.RefreshCommands();
                    System.Threading.Tasks.Task.Run(() => _inputService.Stop())
                        .ContinueWith(_ => RebuildControllerSection(),
                            System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                }
                else
                {
                    _inputService.Start();
                    // Force full sidebar card rebuild — engine state affects power icon colors
                    // but isn't a NavControllerItemViewModel property, so in-place updates miss it.
                    RebuildControllerSection();
                }
            };

            DashboardPageView.SlotTypeChangeRequested += (s, args) =>
            {
                // Re-automap devices before setting OutputType so that when
                // RebuildMappings fires, the PadSetting already has correct mappings.
                SettingsManager.ReAutoMapSlot(args.SlotIndex, args.Type);
                _viewModel.Pads[args.SlotIndex].OutputType = args.Type;
                _inputService.MoveSlotToGroupTail(args.SlotIndex);
                // ReAutoMapSlot rewrites the PadSettings; the per-slot
                // MappingSet still references the prior shape. Re-merge so
                // newly-auto-mapped fields (Motion passthrough on Sony,
                // touchpad rows on Sony, etc.) populate as MappingSet rows
                // without waiting for a save+reload.
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view — see OnSidebarTypeXbox / PadViewModel.MappingsViewLoaded.
                // RefreshDeviceList below can re-select the slot's device and fire the
                // mapping-persisting save; the flag keeps it from writing the pre-change view.
                _viewModel.Pads[args.SlotIndex].MappingsViewLoaded = false;
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

            DashboardPageView.SlotCardClicked += (s, slotIndex) =>
            {
                NavigateToSlot(slotIndex);
            };

            // Window events.
            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += OnStateChanged;
            LocationChanged += OnLocationOrSizeChanged;
            SizeChanged += OnLocationOrSizeChanged;

            // Live language switching: refresh sidebar nav items when culture changes.
            Strings.CultureChanged += OnCultureChanged;

            // ── Early initialization (before window is shown) ──
            // Settings must be loaded before Show() so App.OnStartup can
            // decide whether to show the window at all (start-minimized-to-tray).
            _settingsService.Initialize();

            // Load profile shortcuts after settings are loaded.
            LoadProfileShortcuts();

            // First-run legacy driver cleanup (v2 → v3 upgrade path).
            Dispatcher.BeginInvoke(new Action(MaybeOfferLegacyDriverCleanup),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // Restore main window position/size/state.
            var mw = _viewModel.Settings;
            if (mw.MainWindowLeft >= 0 && mw.MainWindowTop >= 0)
            {
                Left = mw.MainWindowLeft;
                Top = mw.MainWindowTop;
            }
            if (mw.MainWindowWidth > 0) Width = mw.MainWindowWidth;
            if (mw.MainWindowHeight > 0) Height = mw.MainWindowHeight;
            if (!mw.MainWindowFullScreen && mw.MainWindowState == 2)
                WindowState = WindowState.Maximized;

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

            // Per-group ordering is built into settings load now (see
            // SlotOrders.RebuildFromCurrentTopology in SettingsService).
            // Nothing to do here at startup.

            // Detect drivers early (before sidebar rebuild) so power icons show correct
            // colors even when starting minimized to tray (where OnLoaded never fires).
            RefreshHidHideStatus();
            RefreshMidiServicesStatus();
            StartDriverStatusTimer();

            // Populate sidebar and dashboard with saved slots regardless of engine state,
            // so virtual controllers are visible for configuration even when the engine is off.
            _viewModel.RefreshNavControllerItems();
            RefreshDashboardActiveSlots();

            // Virtual controllers are created on demand by the engine
            // (CreateHMaestroController) when slots become active. No
            // pre-creation needed at startup.
            if (_viewModel.Settings.AutoStartEngine)
                _inputService.Start();

            // If the App.OnStartup orphan-sweep task is still running, show
            // a startup overlay so the user sees "Cleaning up previous
            // session…" rather than a blank-looking window. Hide the
            // overlay as soon as the task finishes. Skip the overlay when
            // the task completed before we got here (common case — no
            // orphans means the sweep returns in milliseconds) so there's
            // no flash on a normal launch.
            var sweep = App.OrphanSweepTask;
            if (sweep != null && !sweep.IsCompleted)
            {
                StartupOverlay.Visibility = System.Windows.Visibility.Visible;
                sweep.ContinueWith(_ =>
                {
                    StartupOverlay.Visibility = System.Windows.Visibility.Collapsed;
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        /// <summary>Whether the app should start minimized (to taskbar).</summary>
        public bool ShouldStartMinimized { get; private set; }

        /// <summary>Whether the app should start hidden to the system tray.</summary>
        public bool ShouldStartMinimizedToTray { get; private set; }

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        private void StartDriverStatusTimer()
        {
            if (_driverStatusTimer != null) return;
            _driverStatusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _driverStatusTimer.Tick += (s, ev) =>
            {
                RefreshHidHideStatus();
                RefreshMidiServicesStatus();
            };
            _driverStatusTimer.Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Restore fullscreen after window is fully rendered — setting WindowStyle.None
            // in the constructor gets overridden by FluentWindow initialization.
            if (_viewModel.Settings.MainWindowFullScreen)
            {
                _isFullScreen = true;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                FullScreenIcon.Text = "\uE73F";
            }

            SetupNativeTooltip();

            // Driver detection and timer are initialized in the constructor so they
            // work even when starting minimized to tray (where OnLoaded never fires).

            // Populate diagnostic info.
            _viewModel.Settings.ApplicationVersion =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            _viewModel.Settings.RuntimeVersion = Environment.Version.ToString();

            // Check SDL3.dll availability.
            try
            {
                var sdlVersion = SDL3.SDL.SDL_Linked_Version();
                _viewModel.Settings.SdlVersion = $"SDL {sdlVersion.major}.{sdlVersion.minor}.{sdlVersion.patch}";
            }
            catch (DllNotFoundException)
            {
                _viewModel.Settings.SdlVersion = Strings.Instance.Status_SDL3NotFound;
                _viewModel.StatusText = Strings.Instance.Status_SDL3NotFoundDetail;
            }
            catch
            {
                _viewModel.Settings.SdlVersion = Strings.Instance.Common_Unknown;
            }

            // Select the first nav item.
            if (NavView.MenuItems.Count > 0)
                if (NavView.MenuItems[0] is NavigationViewItem first) first.IsActive = true;
        }

        private async void MaybeOfferLegacyDriverCleanup()
        {
            // async void entry point scheduled via Dispatcher.BeginInvoke —
            // any exception that escapes here unwinds through the dispatcher
            // and shows a user-facing "unexpected error" dialog. Wrap the
            // whole flow (including the pre-dialog registry reads, which
            // can throw on permission / corrupt-hive edge cases) so a
            // detection failure never pops the unhandled-exception UI.
            try
            {
                if (_viewModel.Settings.LegacyDriverCleanupOffered) return;

                bool hasExtended = DriverInstaller.IsExtendedInstalled();
                bool hasViGEm = DriverInstaller.GetViGEmVersion() != null;
                if (!hasExtended && !hasViGEm)
                {
                    _viewModel.Settings.LegacyDriverCleanupOffered = true;
                    _settingsService?.MarkDirty();
                    return;
                }

                var found = new System.Collections.Generic.List<string>();
                if (hasViGEm) found.Add("ViGEmBus");
                if (hasExtended) found.Add("vJoy");

                // Ensure window is visible so the dialog isn't hidden behind tray mode.
                if (!IsVisible) { try { Show(); } catch (InvalidOperationException) { } }
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();

                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "Legacy Driver Cleanup",
                    Content =
                        $"PadForge v3 uses HIDMaestro and no longer needs the legacy driver(s): {string.Join(", ", found)}.\n\n" +
                        "Would you like PadForge to uninstall them now? This requires elevation and may take a moment.",
                    PrimaryButtonText = "Uninstall",
                    CloseButtonText = "Keep",
                };

                var result = await dialog.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    try
                    {
                        if (hasViGEm) DriverInstaller.UninstallViGEmBus();
                        if (hasExtended) DriverInstaller.UninstallVJoy();
                    }
                    catch (Exception ex)
                    {
                        var err = new Wpf.Ui.Controls.MessageBox
                        {
                            Title = "Legacy Driver Cleanup",
                            Content = $"Cleanup encountered an error: {ex.Message}\n\nYou can retry later from Settings.",
                            CloseButtonText = "OK",
                        };
                        _ = await err.ShowDialogAsync();
                    }
                }

                _viewModel.Settings.LegacyDriverCleanupOffered = true;
                _settingsService?.MarkDirty();
            }
            catch
            {
                // Detection or prompt failed. Swallowing here is correct —
                // a legacy-cleanup offer glitch should never surface as an
                // unhandled-exception dialog on first launch. The next
                // launch will retry since LegacyDriverCleanupOffered is
                // only flipped on success paths above.
            }
        }

        private bool _shutdownComplete;

        private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_shutdownComplete)
                return; // Second close call after async shutdown — let it through.

            // Cancel the close so the window stays visible during shutdown.
            e.Cancel = true;

            // Show shutdown overlay and ensure window is visible.
            ShutdownOverlay.Visibility = System.Windows.Visibility.Visible;
            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                try { Show(); } catch (InvalidOperationException) { }
                WindowState = WindowState.Normal;
            }

            // Save settings synchronously (fast, UI-bound data).
            if (_settingsService.IsDirty)
                _settingsService.Save();

            // Stop driver status polling.
            _driverStatusTimer?.Stop();
            _driverStatusTimer = null;

            // Dispose tray icon and helper window.
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_trayMenuHost != null)
            {
                _trayMenuHost.Close();
                _trayMenuHost = null;
            }

            // Unwire device service.
            _deviceService?.UnwireEvents();

            // Run the slow shutdown work (controller disposal, Extended node removal) off the UI thread.
            await System.Threading.Tasks.Task.Run(() =>
            {
                _recorderService?.Dispose();
                _inputService?.Dispose();
                Common.Input.MidiInputRuntime.Shutdown();
                Common.Input.MidiVirtualController.Shutdown();
            });

            // All done — close for real.
            _shutdownComplete = true;
            Close();
        }

        // ─────────────────────────────────────────────
        //  Navigation
        // ─────────────────────────────────────────────

        // SVG path data for controller type icons — shared via ControllerIcons static class.
        private const string XboxSvgPath = Common.ControllerIcons.XboxSvgPath;
        private const string DS4SvgPath = Common.ControllerIcons.DS4SvgPath;
        private const string ExtendedSvgPath = Common.ControllerIcons.ExtendedSvgPath;


        /// <summary>Static nav items whose Content must be refreshed on culture change.</summary>
        private NavigationViewItem _navDashboard, _navProfiles, _navDevices;

        /// <summary>Index in NavView.MenuItems where the first controller entry goes (after Dashboard, Profiles, Devices).</summary>
        private const int ControllerInsertIndex = 3;

        /// <summary>Re-entrancy guard for <see cref="RebuildControllerSection"/>.</summary>
        private bool _rebuildingControllerSection;

        /// <summary>Tracks PropertyChanged subscriptions so they can be unsubscribed on rebuild.</summary>
        private readonly List<(System.ComponentModel.INotifyPropertyChanged Source, System.ComponentModel.PropertyChangedEventHandler Handler)> _navItemHandlers = new();

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

            // ItemInvoked fires even when SelectsOnInvoked=false (used for AddController).
            NavView.ItemInvoked += NavView_ItemInvoked;

            NavView.MenuItems.Clear();

            // Dashboard.
            _navDashboard = new NavigationViewItem
            {
                Content = Strings.Instance.Dashboard_Title,
                Tag = "Dashboard",
                Icon = new FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uF404" }
            };
            NavView.MenuItems.Add(_navDashboard);

            // Profiles.
            _navProfiles = new NavigationViewItem
            {
                Tag = "Profiles",
                Icon = new FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uE8F1" },
                Content = Strings.Instance.Profiles_Title
            };
            NavView.MenuItems.Add(_navProfiles);

            // Devices.
            _navDevices = new NavigationViewItem
            {
                Content = Strings.Instance.Devices_Title,
                Tag = "Devices",
                Icon = new FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uE772" }
            };
            NavView.MenuItems.Add(_navDevices);

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
                        RefreshDashboardActiveSlots();
                        _viewModel.Devices.RefreshSlotButtons();
                        // SlotOrders changed — recompute every device row's
                        // badge numbers so they track the dashboard cards'
                        // visual ordering, not the original create order.
                        _viewModel.Devices.RefreshAllSlotBadges();
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

        private void OnCultureChanged()
        {
            if (_navDashboard != null) _navDashboard.Content = Strings.Instance.Dashboard_Title;
            if (_navProfiles != null) _navProfiles.Content = Strings.Instance.Profiles_Title;
            if (_navDevices != null) _navDevices.Content = Strings.Instance.Devices_Title;

            // Footer items — use x:Name references directly.
            NavSettings.Content = Strings.Instance.Settings_Title;
            NavAbout.Content = Strings.Instance.About_Title;

            // Refresh "Add Controller" and controller card labels by rebuilding the dynamic section.
            RebuildControllerSection();

            // Persist the language change.
            _settingsService.MarkDirty();
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
            if (_rebuildingControllerSection || _isDraggingCard)
                return;
            if (_isCardFading)
            {
                _rebuildPendingAfterFade = true;
                return;
            }

            // Save current selection tag before tearing down items.
            string selectedTag = (NavView.SelectedItem as NavigationViewItem)?.Tag?.ToString();

            _rebuildingControllerSection = true;
            try
            {
                // Unsubscribe old PropertyChanged handlers to prevent leaks.
                foreach (var (source, handler) in _navItemHandlers)
                    source.PropertyChanged -= handler;
                _navItemHandlers.Clear();

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
                    System.ComponentModel.PropertyChangedEventHandler handler = (s, e) =>
                    {
                        if (e.PropertyName is nameof(NavControllerItemViewModel.InstanceLabel)
                            or nameof(NavControllerItemViewModel.IconKey)
                            or nameof(NavControllerItemViewModel.IsEnabled)
                            or nameof(NavControllerItemViewModel.SlotNumber)
                            or nameof(NavControllerItemViewModel.ConnectedDeviceCount)
                            or nameof(NavControllerItemViewModel.IsInitializing)
                            or nameof(NavControllerItemViewModel.IsVirtualControllerConnected))
                        {
                            UpdateControllerNavItemContent(capturedMenuItem, capturedNavItem);
                        }
                    };
                    navItem.PropertyChanged += handler;
                    _navItemHandlers.Add((navItem, handler));
                }

                // "Add Controller" button (visible if any controller type has remaining capacity).
                if (HasAnyControllerTypeCapacity())
                {
                    var addItem = new NavigationViewItem
                    {
                        Tag = "AddController",
                        Icon = new FontIcon { FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Glyph = "\uE710" }, // + icon
                        Content = Strings.Instance.Main_AddController
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
                if ((match ?? fallback) is NavigationViewItem restoreItem) restoreItem.IsActive = true;
            }

            // Refresh uninstall button guards (disabled when slots of that type exist).
            _viewModel.Settings.RefreshDriverGuards();
        }

        /// <summary>
        /// Creates a NavigationViewItem with two-line content for a virtual controller slot.
        /// </summary>
        private NavigationViewItem CreateControllerNavItem(NavControllerItemViewModel navItem)
        {
            bool collapsed = !NavView.IsPaneOpen;
            var menuItem = new NavigationViewItem
            {
                Tag = navItem.Tag,
                Margin = collapsed ? new Thickness(0) : new Thickness(-40, 0, 0, 0)
            };
            System.Windows.Automation.AutomationProperties.SetName(menuItem, navItem.Tag);
            UpdateControllerNavItemContent(menuItem, navItem);
            if (collapsed)
                menuItem.Icon = RenderCompactCardIcon(navItem);
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
            if (_isDraggingCard) return;
            string iconKey = navItem.IconKey;
            bool isXbox = iconKey == "XboxControllerIcon";
            bool isPlayStation = iconKey == "DS4ControllerIcon";
            bool isExtended = iconKey == "ExtendedControllerIcon";
            bool isMidi = iconKey == "MidiControllerIcon";
            bool isKbm = iconKey == "KeyboardMouseControllerIcon";

            var row = new System.Windows.Controls.DockPanel();

            // Delete button — docked right so it stays at the far end of the card.
            var deleteIcon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE711",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 9
            };
            deleteIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            var deleteBtn = new System.Windows.Controls.Button
            {
                Content = deleteIcon,
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = Strings.Instance.Main_DeleteVC,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Opacity = 0.5
            };
            deleteBtn.Click += OnSidebarDeleteSlot;
            System.Windows.Controls.DockPanel.SetDock(deleteBtn, System.Windows.Controls.Dock.Right);
            row.Children.Add(deleteBtn);

            // Power button (green = enabled + active, yellow = enabled + warning, red = disabled,
            // flashing green = initializing).
            var outputType = _viewModel.Pads[navItem.PadIndex].OutputType;
            System.Windows.Media.SolidColorBrush powerColor;
            string powerTooltip;
            bool isInitializing = navItem.IsInitializing;
            if (!navItem.IsEnabled)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)); // red
                powerTooltip = Strings.Instance.Common_Disabled;
                isInitializing = false;
            }
            else if (isInitializing)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                powerTooltip = Strings.Instance.Main_Initializing;
            }
            else if (!_viewModel.IsEngineRunning)
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = Strings.Instance.Main_EngineStopped;
            }
            else if (!navItem.IsVirtualControllerConnected)
            {
                // Yellow reflects "no live VC" (slot has never created a VC, or
                // its VC was torn down by the HM-inactivity timeout). During
                // the grace period the VC is still alive even with devices
                // offline, so the indicator stays green until teardown.
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)); // yellow/amber
                powerTooltip = Strings.Instance.Main_AwaitingDevices;
            }
            else
            {
                powerColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)); // green
                powerTooltip = Strings.Instance.Main_Active;
            }

            var powerTextBlock = new System.Windows.Controls.TextBlock
            {
                Text = PowerGlyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = powerColor
            };

            // Apply flashing opacity animation when initializing.
            if (isInitializing)
            {
                var flashAnimation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.15,
                    Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                powerTextBlock.BeginAnimation(System.Windows.UIElement.OpacityProperty, flashAnimation);
            }

            var powerBtn = new System.Windows.Controls.Button
            {
                Content = powerTextBlock,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
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
                Margin = new Thickness(3, 0, 0, 0),
                Width = 16,
                TextAlignment = TextAlignment.Center
            });

            // Separator.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "|",
                FontSize = 12,
                Opacity = 0.3,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, -3, 6, 0)
            });

            // Type-switch buttons: Xbox / PlayStation / Extended / KBM / MIDI — shown for all cards.
            // HIDMaestro is always available (embedded in HIDMaestro.Core.dll), so the
            // Xbox / PlayStation / Extended categories are always enabled. MIDI still
            // depends on Windows MIDI Services.
            bool hasMidi = DriverInstaller.IsMidiServicesInstalled();

            // Xbox type button — use SetResourceReference for theme-aware Fill.
            var xboxPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPath,
                ToolTip = Strings.Instance.ControllerType_Xbox,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isXbox ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            xboxBtn.Click += OnSidebarTypeXbox;
            row.Children.Add(xboxBtn);

            // PlayStation type button — use SetResourceReference for theme-aware Fill.
            var playstationPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            playstationPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            var playstationBtn = new System.Windows.Controls.Button
            {
                Content = playstationPath,
                ToolTip = Strings.Instance.ControllerType_PlayStation,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isPlayStation ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            playstationBtn.Click += OnSidebarTypePlayStation;
            row.Children.Add(playstationBtn);

            // Extended type button — use SetResourceReference for theme-aware Fill.
            var extendedPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(ExtendedSvgPath),
                Width = 13,
                Height = 13,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            extendedPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            var extendedBtn = new System.Windows.Controls.Button
            {
                Content = extendedPath,
                ToolTip = Strings.Instance.ControllerType_Extended,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isExtended ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            extendedBtn.Click += OnSidebarTypeExtended;
            row.Children.Add(extendedBtn);

            // Keyboard+Mouse type button — MDL2 glyph E961 (always available).
            var kbmBtn = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "\uE961",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13
                },
                ToolTip = Strings.Instance.ControllerType_KeyboardMouse,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isKbm ? 1.0 : 0.3,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            kbmBtn.Click += OnSidebarTypeKeyboardMouse;
            row.Children.Add(kbmBtn);

            // MIDI type button — MDL2 glyph (music note).
            var midiBtn = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "\uE8D6",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13
                },
                ToolTip = hasMidi ? Strings.Instance.ControllerType_MIDI : Strings.Instance.Main_MIDI_RequiresMidiServices,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Opacity = isMidi ? 1.0 : 0.3,
                Cursor = hasMidi ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No,
                Margin = new Thickness(1, 0, 0, 0),
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center
            };
            midiBtn.Click += OnSidebarTypeMidi;
            row.Children.Add(midiBtn);

            // Per-type instance label.
            row.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.InstanceLabel,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                Width = 12,
                TextAlignment = TextAlignment.Center
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
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

            // Drag reordering — mouse-down recorded here, threshold + movement tracked at NavView level.
            card.PreviewMouseLeftButtonDown += OnCardDragStart;

            // Cross-panel: accept device drops from Devices page.
            card.AllowDrop = true;
            card.Drop += OnSidebarCardDrop;
            card.DragOver += OnSidebarCardDragOver;
            card.DragLeave += OnSidebarCardDragLeave;

            menuItem.Content = card;

            // Never touch Icon here — PaneClosed/PaneOpened handlers manage it exclusively.
            // Re-rendering the bitmap on every 30Hz update causes visual flashing.
        }

        /// <summary>
        /// Swaps all controller NavigationViewItem cards between full and compact mode.
        /// Compact mode shows a mini card: gamepad icon + slot number on top row,
        /// type icon + subgroup number on bottom row.
        /// </summary>
        private void UpdateAllControllerCardMode(bool compact)
        {
            var navItems = _viewModel.NavControllerItems;
            if (navItems == null) return;

            foreach (var navItem in navItems)
            {
                foreach (var mi in NavView.MenuItems)
                {
                    if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == navItem.Tag)
                    {
                        if (compact)
                        {
                            // Render compact card to bitmap and set as Icon
                            // (WPF UI only shows Icon in compact mode, not Content).
                            nvi.Icon = RenderCompactCardIcon(navItem);
                            // Reset margin for compact mode so the item doesn't extend outside pane.
                            nvi.Margin = new Thickness(0);
                        }
                        else
                        {
                            nvi.Icon = null;
                            nvi.Margin = new Thickness(-40, 0, 0, 0);
                            UpdateControllerNavItemContent(nvi, navItem);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Renders a compact card visual to a BitmapSource and wraps it in an ImageIcon
        /// so it can be displayed in NavigationViewItem.Icon (which only accepts IconElement).
        /// </summary>
        private Wpf.Ui.Controls.ImageIcon RenderCompactCardIcon(NavControllerItemViewModel navItem)
        {
            var visual = BuildCompactCard(navItem);

            // Measure and arrange the visual so it has a size.
            visual.Measure(new Size(40, 40));
            visual.Arrange(new Rect(0, 0, 40, 40));
            visual.UpdateLayout();

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            int pw = (int)Math.Ceiling(40 * dpi.DpiScaleX);
            int ph = (int)Math.Ceiling(40 * dpi.DpiScaleY);

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);

            var img = new System.Windows.Controls.Image
            {
                Source = rtb,
                Width = 36,
                Height = 36
            };

            return new Wpf.Ui.Controls.ImageIcon { Source = rtb };
        }

        /// <summary>
        /// Builds a compact mini card for collapsed sidebar: two rows stacked vertically.
        /// Row 1: Gamepad icon + slot number. Row 2: Type icon + subgroup number.
        /// </summary>
        private System.Windows.Controls.Border BuildCompactCard(NavControllerItemViewModel navItem)
        {
            var mdl2 = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
            bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            var fgBrush = new System.Windows.Media.SolidColorBrush(
                isDark ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
            fgBrush.Freeze();

            // Row 1: Gamepad icon + slot number
            var row1 = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row1.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "\uE7FC",
                FontFamily = mdl2,
                FontSize = 11,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            row1.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.SlotNumber.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            });

            // Row 2: Type icon + subgroup number
            var row2 = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            string iconKey = navItem.IconKey;
            if (iconKey == "XboxControllerIcon" || iconKey == "DS4ControllerIcon" || iconKey == "ExtendedControllerIcon")
            {
                string svgPath = iconKey == "XboxControllerIcon" ? XboxSvgPath
                    : iconKey == "DS4ControllerIcon" ? DS4SvgPath : ExtendedSvgPath;
                var path = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse(svgPath),
                    Width = 10, Height = 10,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    Fill = fgBrush
                };
                row2.Children.Add(path);
            }
            else
            {
                string glyph = iconKey == "MidiControllerIcon" ? "\uE8D6" : "\uE961";
                row2.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = glyph,
                    FontFamily = mdl2,
                    FontSize = 10,
                    Foreground = fgBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            row2.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.InstanceLabel ?? "",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var stack = new System.Windows.Controls.StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(row1);
            stack.Children.Add(row2);

            var card = new System.Windows.Controls.Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Tag = navItem.PadIndex,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            return card;
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
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Xbox);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Xbox;
                _inputService.MoveSlotToGroupTail(padIndex);
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view: the rebuild above re-auto-mapped
                // the slot, but the OutputType setter's RebuildMappings reloaded the
                // ViewModel from the pre-change MappingSet. Same guard as the device-
                // assignment path (see PadViewModel.MappingsViewLoaded); cleared on
                // the next RefreshMappingsCore.
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar PlayStation type button click.</summary>
        private void OnSidebarTypePlayStation(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.PlayStation);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.PlayStation;
                _inputService.MoveSlotToGroupTail(padIndex);
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view — see OnSidebarTypeXbox / PadViewModel.MappingsViewLoaded.
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar Extended (custom DI) type button click.</summary>
        private void OnSidebarTypeExtended(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Extended);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Extended;
                _inputService.MoveSlotToGroupTail(padIndex);
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view — see OnSidebarTypeXbox / PadViewModel.MappingsViewLoaded.
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar Keyboard+Mouse type button click.</summary>
        private void OnSidebarTypeKeyboardMouse(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.KeyboardMouse);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.KeyboardMouse;
                _inputService.MoveSlotToGroupTail(padIndex);
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view — see OnSidebarTypeXbox / PadViewModel.MappingsViewLoaded.
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar MIDI type button click.</summary>
        private void OnSidebarTypeMidi(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!DriverInstaller.IsMidiServicesInstalled()) return;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Midi);
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Midi;
                _inputService.MoveSlotToGroupTail(padIndex);
                SettingsService.RefreshMappingSetsFromLegacy();
                // Stale-guard the Mappings view — see OnSidebarTypeXbox / PadViewModel.MappingsViewLoaded.
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
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

                    bool hadActiveVc = _inputService.IsHmVcAt(slotIndex);
                    var info = _deviceService.DeleteSlot(slotIndex);
                    _inputService.OnSlotDeleted(slotIndex, info.Type, info.OldGroupPosition,
                        deletedSlotHadActiveVc: hadActiveVc);
                }));
            }
        }

        // ─────────────────────────────────────────────
        //  Sidebar card drag reordering (manual mouse capture)
        // ─────────────────────────────────────────────

        private bool _isDraggingCard;
        private bool _isCardFading;
        private bool _rebuildPendingAfterFade;
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
            if (cards.Count == 0) return;

            _dragSourcePadIndex = padIndex;
            _dragSourceVisualPos = cards.FindIndex(c => c.PadIndex == padIndex);
            if (_dragSourceVisualPos < 0) return;

            _dragAdornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(NavView);
            if (_dragAdornerLayer == null) return;

            // Snapshot the card visual before hiding.
            var snapshot = CaptureCardVisual(_cardDragSource);
            if (snapshot == null) return;

            // Compute mouse offset from card top-left so the adorner doesn't jump.
            var cardTopLeft = _cardDragSource.TranslatePoint(new Point(0, 0), NavView);
            var mousePos = System.Windows.Input.Mouse.GetPosition(NavView);
            var grabOffset = new Point(mousePos.X - cardTopLeft.X, mousePos.Y - cardTopLeft.Y);

            _dragAdorner = new CardDragAdorner(NavView, snapshot, _cardDragSource.RenderSize, grabOffset);
            _dragAdornerLayer.Add(_dragAdorner);

            var accentBrush = (System.Windows.Media.Brush)FindResource("SystemAccentColorSecondaryBrush");
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
            if (cards.Count == 0) return;

            // If cursor has moved outside the card column horizontally (e.g. dragged
            // into the main content area for cross-panel assignment), don't trigger
            // any swap/insert reordering — just clear indicators.
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
                    // Cursor is in the middle zone — swap mode.
                    if (i != _dragSourceVisualPos)
                    {
                        isSwap = true;
                        swapCardIndex = i;
                    }
                    else
                    {
                        // Source's own middle zone: pin dropIndex to the
                        // source's visual position so the insertion math
                        // resolves to "no move" instead of falling through
                        // to the default cards.Count (which would jam the
                        // card to the bottom on release).
                        dropIndex = _dragSourceVisualPos;
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
                    var accent = FindResource("SystemAccentColorSecondaryBrush") as System.Windows.Media.Brush
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
                    // Insert mode: convert dropIndex to target visual position
                    // (still in GLOBAL sidebar coordinates here).
                    int targetGlobalPos;
                    if (_dragDropIndex <= _dragSourceVisualPos)
                        targetGlobalPos = _dragDropIndex;
                    else if (_dragDropIndex <= _dragSourceVisualPos + 1)
                        targetGlobalPos = _dragSourceVisualPos; // no move
                    else
                        targetGlobalPos = _dragDropIndex - 1;

                    if (targetGlobalPos != _dragSourceVisualPos)
                    {
                        // MoveSlot expects a position WITHIN the source's
                        // group's order list. Translate the global sidebar
                        // position to group-local by subtracting the count
                        // of cards in earlier groups. IsInsertionInSameTypeGroup
                        // upstream guarantees the target is in the same group
                        // as the source, so the translated position is in
                        // [0, group_size).
                        var sourceType = _viewModel.Pads[_dragSourcePadIndex].OutputType;
                        var cardsAtDrop = GetControllerCardBounds();
                        int startOfGroup = -1;
                        for (int i = 0; i < cardsAtDrop.Count; i++)
                        {
                            if (_viewModel.Pads[cardsAtDrop[i].PadIndex].OutputType == sourceType)
                            {
                                startOfGroup = i;
                                break;
                            }
                        }
                        if (startOfGroup >= 0)
                        {
                            int srcPad = _dragSourcePadIndex;
                            int tgtGroupLocalPos = targetGlobalPos - startOfGroup;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _inputService.MoveSlot(srcPad, tgtGroupLocalPos);
                                _settingsService.MarkDirty();
                            }));
                            handled = true;
                        }
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
            private readonly Point _grabOffset;
            private Point _position;

            public CardDragAdorner(UIElement adornedElement, System.Windows.Media.ImageSource snapshot, Size cardSize, Point grabOffset)
                : base(adornedElement)
            {
                _brush = new System.Windows.Media.ImageBrush(snapshot);
                _size = cardSize;
                _grabOffset = grabOffset;
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
                        _position.X - _grabOffset.X,
                        _position.Y - _grabOffset.Y,
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
        /// <summary>
        /// Returns true if at least one virtual controller type has remaining capacity.
        /// </summary>
        private bool HasAnyControllerTypeCapacity()
        {
            // Total active slots is the binding constraint (MaxPads = 16).
            // Per-group caps are also 16 each, so checking total is correct
            // for "Add Controller" availability.
            int total = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
                if (SettingsManager.SlotCreated[i]) total++;
            return total < InputManager.MaxPads;
        }

        /// <summary>
        /// Shows a popup anchored to the given element with controller type buttons.
        /// Clicking a button creates a new slot of that type and navigates to it.
        /// </summary>
        private void ShowControllerTypePopup(UIElement anchor, PlacementMode placement = PlacementMode.Right)
        {
            // If the popup is already open, close it instead of opening a duplicate.
            if (_controllerTypePopup != null && _controllerTypePopup.IsOpen)
            {
                _controllerTypePopup.IsOpen = false;
                _controllerTypePopup = null;
                return;
            }

            // Suppress reopening if the popup was just dismissed within the same click cycle.
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

            // Separate shadow element pattern: shadow on empty border behind,
            // content border on top without Effect (avoids corner artifacts).
            var container = new System.Windows.Controls.Grid { Margin = new Thickness(10) };

            var shadowBorder = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8,
                    Opacity = 0.3,
                    ShadowDepth = 2
                }
            };
            container.Children.Add(shadowBorder);

            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6)
            };
            container.Children.Add(border);

            // Dismiss on any click outside the popup content.
            System.Windows.Input.MouseButtonEventHandler dismissHandler = (s, e) =>
            {
                if (!popup.IsOpen) return;
                if (e.OriginalSource is DependencyObject source)
                {
                    var parent = source;
                    while (parent != null)
                    {
                        if (parent == border) return;
                        parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                    }
                }
                popup.IsOpen = false;
            };
            PreviewMouseDown += dismissHandler;
            popup.Closed += (s, e) => PreviewMouseDown -= dismissHandler;

            // Theme-aware popup background.
            void ApplyPopupTheme()
            {
                bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                    == Wpf.Ui.Appearance.ApplicationTheme.Dark;
                var color = dark ? System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A)
                                 : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3);
                var brush = new System.Windows.Media.SolidColorBrush(color);
                shadowBorder.Background = brush;
                border.Background = brush;
            }
            ApplyPopupTheme();

            // Live-update if theme changes while popup is open.
            void OnThemeChangedWhileOpen(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color _)
                => Dispatcher.BeginInvoke(ApplyPopupTheme);
            Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChangedWhileOpen;
            popup.Closed += (s, e) =>
            {
                Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChangedWhileOpen;
            };

            var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            // Total active slots is the binding constraint (MaxPads = 16
            // across all five groups). When the global total is at the cap
            // every "Add" button disables uniformly. Per-type counts are
            // kept for the at-capacity tooltip text.
            int xboxCount = 0, playstationCount = 0, extendedCount = 0, midiCount = 0, kbmCount = 0;
            int totalActive = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                totalActive++;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox: xboxCount++; break;
                    case VirtualControllerType.PlayStation: playstationCount++; break;
                    case VirtualControllerType.Extended: extendedCount++; break;
                    case VirtualControllerType.Midi: midiCount++; break;
                    case VirtualControllerType.KeyboardMouse: kbmCount++; break;
                }
            }
            bool globalAtCapacity = totalActive >= InputManager.MaxPads;

            // Xbox button — theme-aware icon fill. Uses the Xbox 360 SVG
            // asset to represent the Xbox family in the UI.
            var xboxPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(XboxSvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            xboxPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            bool xboxAtCapacity = xboxCount >= SettingsManager.MaxXbox360Slots;
            bool xboxDisabled = globalAtCapacity || xboxAtCapacity;
            if (xboxDisabled) xboxPopupPath.Opacity = 0.35;
            var xboxBtn = new System.Windows.Controls.Button
            {
                Content = xboxPopupPath,
                ToolTip = xboxAtCapacity
                        ? string.Format(Strings.Instance.Main_Xbox_Max_Format, SettingsManager.MaxXbox360Slots)
                        : Strings.Instance.ControllerType_Xbox,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = xboxDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(xboxBtn, "AddXbox360Btn");
            xboxBtn.Click += (s, e) =>
            {
                if (xboxDisabled) return;
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Xbox);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.Xbox);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(xboxBtn);

            // PlayStation button — theme-aware icon fill. Uses the DS4 SVG
            // asset to represent the PlayStation family in the UI.
            var playstationPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(DS4SvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            playstationPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            bool playstationAtCapacity = playstationCount >= SettingsManager.MaxPlayStationSlots;
            bool playstationDisabled = globalAtCapacity || playstationAtCapacity;
            if (playstationDisabled) playstationPopupPath.Opacity = 0.35;
            var playstationBtn = new System.Windows.Controls.Button
            {
                Content = playstationPopupPath,
                ToolTip = playstationAtCapacity
                        ? string.Format(Strings.Instance.Main_PlayStation_Max_Format, SettingsManager.MaxPlayStationSlots)
                        : Strings.Instance.ControllerType_PlayStation,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = playstationDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            // AutomationId kept as "AddDS4Btn" for stable UI-automation hookup.
            System.Windows.Automation.AutomationProperties.SetAutomationId(playstationBtn, "AddDS4Btn");
            playstationBtn.Click += (s, e) =>
            {
                if (playstationDisabled) return;
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.PlayStation);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.PlayStation);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(playstationBtn);

            // Extended button — theme-aware icon fill.
            var extendedPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(ExtendedSvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            extendedPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            bool extendedAtCapacity = extendedCount >= SettingsManager.MaxExtendedSlots;
            bool extendedDisabled = globalAtCapacity || extendedAtCapacity;
            if (extendedDisabled) extendedPopupPath.Opacity = 0.35;
            var extendedBtn = new System.Windows.Controls.Button
            {
                Content = extendedPopupPath,
                ToolTip = extendedAtCapacity
                        ? string.Format(Strings.Instance.Main_Extended_Max_Format, SettingsManager.MaxExtendedSlots)
                        : Strings.Instance.ControllerType_Extended,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = extendedDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(extendedBtn, "AddExtendedBtn");
            extendedBtn.Click += (s, e) =>
            {
                if (extendedDisabled) return;
                popup.IsOpen = false;

                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Extended);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.Extended);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(extendedBtn);

            // Keyboard+Mouse button — MDL2 glyph E961, theme-aware foreground.
            var kbmPopupIcon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE961",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            kbmPopupIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            bool kbmAtCapacity = kbmCount >= SettingsManager.MaxKeyboardMouseSlots;
            bool kbmDisabled = globalAtCapacity || kbmAtCapacity;
            if (kbmDisabled) kbmPopupIcon.Opacity = 0.35;
            var kbmPopupBtn = new System.Windows.Controls.Button
            {
                Content = kbmPopupIcon,
                ToolTip = kbmAtCapacity ? string.Format(Strings.Instance.Main_KBM_Max_Format, SettingsManager.MaxKeyboardMouseSlots)
                        : Strings.Instance.ControllerType_KeyboardMouse,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = kbmDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(kbmPopupBtn, "AddKeyboardMouseBtn");
            kbmPopupBtn.Click += (s, e) =>
            {
                if (kbmDisabled) return;
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.KeyboardMouse);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.KeyboardMouse);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(kbmPopupBtn);

            // MIDI button — theme-aware icon fill.
            var midiPopupIcon = new System.Windows.Controls.TextBlock
            {
                Text = "\uE8D6",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            midiPopupIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            bool midiAvailable = DriverInstaller.IsMidiServicesInstalled();
            bool midiAtCapacity = midiCount >= SettingsManager.MaxMidiSlots;
            bool midiDisabled = !midiAvailable || globalAtCapacity || midiAtCapacity;
            if (midiDisabled) midiPopupIcon.Opacity = 0.35;
            string midiTooltip = !midiAvailable ? Strings.Instance.Main_MIDI_RequiresMidiServices
                               : midiAtCapacity ? string.Format(Strings.Instance.Main_MIDI_Max_Format, SettingsManager.MaxMidiSlots)
                               : Strings.Instance.ControllerType_MIDI;
            var midiBtn = new System.Windows.Controls.Button
            {
                Content = midiPopupIcon,
                ToolTip = midiTooltip,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = midiDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(midiBtn, "AddMidiBtn");
            midiBtn.Click += (s, e) =>
            {
                if (midiDisabled) return;
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Midi);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.Midi);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(midiBtn);

            border.Child = stack;
            popup.Child = container;
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
        /// Used after CreateSlot to navigate to the newly added slot, which is
        /// always the tail of its group's order list.
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
            // Clear all active states first.
            foreach (var mi in NavView.MenuItems)
                if (mi is NavigationViewItem other) other.IsActive = false;
            foreach (var mi in NavView.FooterMenuItems)
                if (mi is NavigationViewItem other) other.IsActive = false;

            // Set active on the matching item.
            foreach (var mi in NavView.MenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    nvi.IsActive = true;
                    break;
                }
            }
            foreach (var mi in NavView.FooterMenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == tag)
                {
                    nvi.IsActive = true;
                    break;
                }
            }

            // Navigate content (WPF UI doesn't reliably fire SelectionChanged).
            NavigateToTag(tag);
        }

        /// <summary>
        /// Handles clicks on non-selectable NavigationView items (e.g. "Add Controller").
        /// SelectsOnInvoked=false prevents the blue indicator from moving to these items,
        /// but ItemInvoked still fires so we can show the popup.
        /// </summary>
        private void NavView_ItemInvoked(NavigationView sender,
            RoutedEventArgs args)
        {
            // Check for AddController click.
            if (args.OriginalSource is NavigationViewItem nvi
                && nvi.Tag?.ToString() == "AddController")
            {
                ShowControllerTypePopup(nvi);
                return;
            }

            // Fallback navigation: if SelectionChanged didn't fire, handle it here.
            if (sender.SelectedItem is NavigationViewItem selected)
            {
                var tag = selected.Tag?.ToString() ?? "Dashboard";
                if (!_rebuildingControllerSection)
                    NavigateToTag(tag);
            }
        }

        private static T FindVisualChildByType<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match && predicate(match))
                    return match;
                var result = FindVisualChildByType(child, predicate);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name)
                    return fe;
                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// Re-renders controller nav item content in-place (no collection modification)
        /// to refresh driver availability cursors/tooltips.
        /// </summary>
        private void RefreshControllerNavItemsInPlace()
        {
            var navItems = _viewModel.NavControllerItems;
            if (navItems == null) return;
            foreach (var navItem in navItems)
            {
                // Find the existing NavigationViewItem by tag.
                foreach (var mi in NavView.MenuItems)
                {
                    if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == navItem.Tag)
                    {
                        UpdateControllerNavItemContent(nvi, navItem);
                        break;
                    }
                }
            }
        }

        private void CloseControllerPopup()
        {
            if (_controllerTypePopup != null && _controllerTypePopup.IsOpen)
            {
                _controllerTypePopup.IsOpen = false;
                _controllerTypePopup = null;
            }
        }

        private void PaneToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
        }

        private void BrandingBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private bool _isFullScreen;
        private IntPtr _nativeTooltip;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct TOOLINFO
        {
            public int cbSize;
            public int uFlags;
            public IntPtr hwnd;
            public IntPtr uId;
            public RECT rect;
            public IntPtr hinst;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPTStr)]
            public string lpszText;
            public IntPtr lParam;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int pt_x;
            public int pt_y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetMessageTime();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

        private void SetupNativeTooltip()
        {
            var source = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (source == null) return;

            const int WS_POPUP = unchecked((int)0x80000000);
            const int TTS_ALWAYSTIP = 0x01;
            const int TTS_NOPREFIX = 0x02;
            const int WS_EX_TOPMOST = 0x08;

            _nativeTooltip = CreateWindowEx(WS_EX_TOPMOST, "tooltips_class32", "",
                WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                0, 0, 0, 0, source.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_nativeTooltip == IntPtr.Zero) return;

            // Add a non-tracking tool with the button's client rect.
            AddNativeTooltipTool(source.Handle);

            // Relay mouse messages so the tooltip handles delay/positioning natively.
            FullScreenBtn.MouseMove += (s, ev) => RelayMouseMessage(source.Handle, 0x0200); // WM_MOUSEMOVE
            FullScreenBtn.MouseLeave += (s, ev) => RelayMouseMessage(source.Handle, 0x02A3); // WM_MOUSELEAVE
        }

        private void AddNativeTooltipTool(IntPtr hwnd)
        {
            var source = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            var topLeft = FullScreenBtn.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
            var sz = FullScreenBtn.RenderSize;

            var ti = new TOOLINFO();
            ti.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(TOOLINFO));
            ti.uFlags = 0; // no TTF_SUBCLASS, no TTF_TRACK — we relay manually
            ti.hwnd = hwnd;
            ti.uId = (IntPtr)9999;
            ti.rect.left = (int)(topLeft.X * dpiX);
            ti.rect.top = (int)(topLeft.Y * dpiY);
            ti.rect.right = (int)((topLeft.X + sz.Width) * dpiX);
            ti.rect.bottom = (int)((topLeft.Y + sz.Height) * dpiY);
            ti.lpszText = Strings.Instance.Main_FullScreen;

            var pti = System.Runtime.InteropServices.Marshal.AllocHGlobal(ti.cbSize);
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(ti, pti, false);
                SendMessage(_nativeTooltip, 0x0433, IntPtr.Zero, pti); // TTM_DELTOOLW (remove old)
                SendMessage(_nativeTooltip, 0x0432, IntPtr.Zero, pti); // TTM_ADDTOOLW
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pti);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref System.Drawing.Point lpPoint);

        private void RelayMouseMessage(IntPtr hwnd, int msgType)
        {
            GetCursorPos(out var screenPt);
            var clientPt = screenPt;
            ScreenToClient(hwnd, ref clientPt);

            var msg = new MSG();
            msg.hwnd = hwnd;
            msg.message = msgType;
            msg.wParam = IntPtr.Zero;
            msg.lParam = (IntPtr)((clientPt.Y << 16) | (clientPt.X & 0xFFFF));
            msg.time = GetMessageTime();
            msg.pt_x = screenPt.X;
            msg.pt_y = screenPt.Y;

            var pMsg = System.Runtime.InteropServices.Marshal.AllocHGlobal(
                System.Runtime.InteropServices.Marshal.SizeOf(typeof(MSG)));
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(msg, pMsg, false);
                SendMessage(_nativeTooltip, 0x0407, IntPtr.Zero, pMsg); // TTM_RELAYEVENT
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(pMsg);
            }
        }

        // Touch-input debounce for toggle buttons. On a touchscreen, a single
        // finger tap generates both a touch event chain and a promoted mouse
        // click, so Click fires twice for one tap. The toggle then flips and
        // flips back, making the tap appear to do nothing. Track the last
        // invocation tick and swallow a second invocation that lands within
        // TouchClickDebounceMs of the first. Mouse clicks on a regular
        // pointer aren't affected — they don't fire back-to-back that fast.
        private const int TouchClickDebounceMs = 400;
        private long _lastFullScreenClickTick;
        private long _lastMaximizeClickTick;

        private void FullScreenBtn_Click(object sender, RoutedEventArgs e)
        {
            long now = Environment.TickCount64;
            if (now - _lastFullScreenClickTick < TouchClickDebounceMs) return;
            _lastFullScreenClickTick = now;

            if (_isFullScreen)
            {
                // Exit full screen.
                _isFullScreen = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                FullScreenIcon.Text = "\uE740";
            }
            else
            {
                // Enter full screen.
                _isFullScreen = true;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                FullScreenIcon.Text = "\uE73F";
            }
            _viewModel.Settings.MainWindowFullScreen = _isFullScreen;
            _settingsService.MarkDirty();
        }

        private void TitleBar_MaximizeClicked(Wpf.Ui.Controls.TitleBar sender, RoutedEventArgs args)
        {
            long now = Environment.TickCount64;
            if (now - _lastMaximizeClickTick < TouchClickDebounceMs) return;
            _lastMaximizeClickTick = now;

            if (_isFullScreen)
            {
                // Exit full screen before TitleBar toggles maximize/restore.
                _isFullScreen = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                FullScreenIcon.Text = "\uE740";
            }
        }

        private void TitleBar_CloseClicked(Wpf.Ui.Controls.TitleBar sender, RoutedEventArgs args)
        {
            // Handled via OnClosing for tray minimize support.
        }

        private void NavView_SelectionChanged(NavigationView sender,
            RoutedEventArgs args)
        {
            CloseControllerPopup();

            if (_rebuildingControllerSection)
                return;

            if (NavView.SelectedItem is NavigationViewItem item)
                NavigateToTag(item.Tag?.ToString() ?? "Dashboard");
        }

        private void NavigateToTag(string tag)
        {
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
                {
                    InputService.RefreshMappingsToViewModel(padVm);
                    var selected = padVm.SelectedMappedDevice;
                    if (selected != null && selected.InstanceGuid != Guid.Empty)
                        _inputService.RefreshAvailableInputsForSlot(padVm);
                    PadPageView.DataContext = padVm;
                }
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
            if (dialog.ShowDialog() != true) return;

            string name = dialog.ProfileName;
            string exePaths = string.Join("|", dialog.ExecutablePaths);
            var profile = _inputService.CreateEmptyProfile(name, exePaths);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Executables = InputService.FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileCreatedEmpty_Format, name);
        }

        private void OnSaveAsProfile(object sender, EventArgs e)
        {
            var dialog = new Views.ProfileDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string name = dialog.ProfileName;
            string exePaths = string.Join("|", dialog.ExecutablePaths);
            var snapshot = _inputService.CreateSnapshotProfile(name, exePaths);

            var listItem = new ViewModels.ProfileListItem
            {
                Id = snapshot.Id,
                Name = snapshot.Name,
                Executables = InputService.FormatExePaths(exePaths),
            };
            SettingsService.UpdateTopologyCounts(listItem, snapshot.SlotCreated, snapshot.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileCreated_Format, name);
        }

        private void OnDeleteProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            bool wasActive = _inputService.DeleteProfile(selected.Id);
            _viewModel.Settings.ProfileItems.Remove(selected);
            _viewModel.Settings.SelectedProfile = null;
            if (wasActive)
                _viewModel.Settings.ActiveProfileInfo = Strings.Instance.Common_Default;
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileDeleted_Format, selected.Name);
        }

        /// <summary>Exports the selected profile (with its referenced
        /// sound packages bundled) to a shareable .pfprofile file.</summary>
        private void OnExportProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            // The Default entry isn't a stored ProfileData row — export a
            // snapshot of the current settings instead.
            ProfileData profile;
            if (selected.IsDefault)
            {
                profile = _inputService.SnapshotCurrentProfile();
                if (profile == null) return;
                profile.Name = selected.Name;
            }
            else
            {
                profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
                if (profile == null) return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Instance.Profiles_Export,
                FileName = profile.Name + PadForge.Common.ProfileTransfer.FileExtension,
                Filter = $"PadForge profile (*{PadForge.Common.ProfileTransfer.FileExtension})|*{PadForge.Common.ProfileTransfer.FileExtension}",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                PadForge.Common.ProfileTransfer.Export(profile, dlg.FileName);
                _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileExported_Format, profile.Name);
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = ex.Message;
            }
        }

        /// <summary>Imports a .pfprofile: bundled sound packages land next
        /// to the exe and register; the profile joins the registry (name
        /// deduped). The user activates it via the existing Load button.</summary>
        private void OnImportProfile(object sender, EventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.Instance.Profiles_Import,
                Filter = $"PadForge profile (*{PadForge.Common.ProfileTransfer.FileExtension})|*{PadForge.Common.ProfileTransfer.FileExtension}|All files|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var profile = PadForge.Common.ProfileTransfer.Import(dlg.FileName, out var packages);
            if (profile == null)
            {
                _viewModel.StatusText = Strings.Instance.Status_ProfileImportFailed;
                return;
            }

            // Dedup the display name against existing profiles.
            string baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Imported profile" : profile.Name.Trim();
            string name = baseName;
            int n = 2;
            while (SettingsManager.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                name = $"{baseName} ({n++})";
            profile.Name = name;

            SettingsManager.Profiles.Add(profile);
            var listItem = new ViewModels.ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Executables = InputService.FormatExePaths(profile.ExecutableNames ?? string.Empty),
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileImported_Format, profile.Name, packages.Count);
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
            if (dialog.ShowDialog() != true) return;

            string newName = dialog.ProfileName;
            string newExePaths = string.Join("|", dialog.ExecutablePaths);
            _inputService.EditProfile(selected.Id, newName, newExePaths);

            selected.Name = newName;
            selected.Executables = InputService.FormatExePaths(newExePaths);
            if (SettingsManager.ActiveProfileId == selected.Id)
                _viewModel.Settings.ActiveProfileInfo = newName;
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileUpdated_Format, newName);
        }

        private void OnLoadProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            if (selected == null) return;

            if (selected.IsDefault) { OnRevertToDefault(sender, e); return; }

            _inputService.LoadProfile(selected.Id);
            var profile = SettingsManager.Profiles.Find(p => p.Id == selected.Id);
            if (profile != null)
            {
                _viewModel.Settings.ActiveProfileInfo = profile.Name;
                _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileLoaded_Format, profile.Name);
            }
            _settingsService.MarkDirty();
        }

        private void OnRevertToDefault(object sender, EventArgs e)
        {
            _inputService.RevertToDefaultProfile();
            _viewModel.Settings.ActiveProfileInfo = Strings.Instance.Common_Default;
            _viewModel.StatusText = Strings.Instance.Status_ProfileRevertedDefault;
            _settingsService.MarkDirty();
        }

        private void LoadProfileShortcuts()
        {
            var shortcuts = SettingsManager.GlobalMacros;
            if (shortcuts == null) return;

            foreach (var data in shortcuts)
            {
                var vm = new ViewModels.ProfileShortcutViewModel(data,
                    s => { _viewModel.Settings.ProfileShortcuts.Remove(s); SaveProfileShortcuts(); },
                    _ => SaveProfileShortcuts());
                _viewModel.Settings.ProfileShortcuts.Add(vm);
            }
        }

        private void SaveProfileShortcuts()
        {
            SettingsManager.GlobalMacros = _viewModel.Settings.ProfileShortcuts
                .Select(s => s.Data)
                .ToArray();
            _settingsService.MarkDirty();
        }

        /// <summary>
        /// Wires StartRecordingRequested, StopRecordingRequested, and PropertyChanged
        /// for a single MappingItem. Called both on initial setup and when Mappings
        /// are rebuilt (OutputType change, Extended config change).
        /// </summary>
        private void WireMappingItemEvents(MappingItem mapping, PadViewModel capturedPad)
        {
            mapping.StartRecordingRequested += (s, e) =>
            {
                if (s is MappingItem mi)
                {
                    Guid deviceGuid = capturedPad.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                    // Y axes: record neg (up in game) first due to NegateAxis inversion.
                    // For standard gamepad: TargetSettingName contains "AxisY".
                    // For Extended custom sticks: TargetSettingName is "ExtendedAxisN" — check label for "Y".
                    //
                    // negRecording=true on Y axes is what makes ShouldAutoInvert
                    // return axisPositive (instead of !axisPositive) so an UP
                    // press records as non-inverted on the user-facing Y-axis
                    // convention. Any sibling record button on the same row
                    // (e.g. the per-source Record on an ExtraSource — see
                    // WireExtraSource below) must mirror this isYAxis check
                    // or the two buttons will record the same physical press
                    // with opposite Invert flags.
                    bool isYAxis = mi.HasNegDirection
                        && (mi.TargetSettingName.Contains("AxisY")
                            || mi.TargetLabel.EndsWith(" Y", StringComparison.Ordinal));
                    if (isYAxis)
                        _pendingNegMapping = mi;

                    _recorderService.StartRecording(mi, capturedPad.PadIndex, deviceGuid,
                        negRecording: isYAxis);

                    // Only show arrow/flash if recording actually started (device available).
                    if (_recorderService.IsRecording)
                        capturedPad.CurrentRecordingTarget = isYAxis ? mi.NegSettingName : mi.TargetSettingName;
                }
            };
            mapping.StopRecordingRequested += (s, e) =>
            {
                _recorderService.CancelRecording();
                capturedPad.CurrentRecordingTarget = null;
            };

            // Mapping descriptor changes (inversion, half-axis, source) trigger autosave.
            mapping.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(MappingItem.SourceDescriptor)
                    or nameof(MappingItem.NegSourceDescriptor)
                    or nameof(MappingItem.IsInverted)
                    or nameof(MappingItem.IsHalfAxis)
                    or nameof(MappingItem.IsBidirectional)
                    or nameof(MappingItem.MappingDeadZone)
                    or nameof(MappingItem.GyroSensitivity)
                    or nameof(MappingItem.MouseCursorSensitivity)
                    or nameof(MappingItem.PrimarySourceDeviceGuid)
                    or nameof(MappingItem.CombineMode)
                    or nameof(MappingItem.CombineExpression)
                    or nameof(MappingItem.NoInherit))
                    _settingsService.MarkDirty();
            };

            // Phase 2C — wire record events for ExtraSources rows. New
            // sources added to the collection get the same wiring so
            // their per-row record buttons trigger cross-device recording
            // through the parent MappingItem.
            //
            // negRecording parity with the primary Record button (see the
            // mapping.StartRecordingRequested handler above): Y-axis targets
            // pass negRecording=true so ShouldAutoInvert returns axisPositive
            // (rather than !axisPositive) for a Y-axis target. Without this
            // the extra source records an UP press as Invert=true (the
            // !axisPositive branch) while the primary records the same UP
            // press as Invert=false (the axisPositive branch). The two
            // record buttons on the same row are supposed to agree on the
            // convention; the parameter has to match.
            void WireExtraSource(MappingSourceItem msi)
            {
                msi.StartRecordingRequested += (s, e) =>
                {
                    bool isYAxisExtra = mapping.HasNegDirection
                        && (mapping.TargetSettingName.Contains("AxisY")
                            || mapping.TargetLabel.EndsWith(" Y", StringComparison.Ordinal));
                    _recorderService.StartRecordingExtraSource(mapping, msi, capturedPad.PadIndex,
                        negRecording: isYAxisExtra);
                };
                msi.StopRecordingRequested += (s, e) =>
                    _recorderService.CancelRecording();
                msi.StartParamRecordingRequested += (s, e) =>
                {
                    var t = e.Target switch
                    {
                        MappingSourceItem.ParamRecordTarget.Up       => RecorderService.ParamTarget.Up,
                        MappingSourceItem.ParamRecordTarget.Down     => RecorderService.ParamTarget.Down,
                        MappingSourceItem.ParamRecordTarget.Modifier => RecorderService.ParamTarget.Modifier,
                        _ => RecorderService.ParamTarget.None,
                    };
                    _recorderService.StartRecordingExtraSourceParam(mapping, msi, capturedPad.PadIndex, t);
                };
                msi.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName is nameof(MappingSourceItem.DeviceGuid)
                        or nameof(MappingSourceItem.Descriptor)
                        or nameof(MappingSourceItem.Invert)
                        or nameof(MappingSourceItem.HalfAxis)
                        or nameof(MappingSourceItem.Bidirectional)
                        or nameof(MappingSourceItem.DeadZone)
                        or nameof(MappingSourceItem.GyroSensitivity)
                        or nameof(MappingSourceItem.MouseCursorSensitivity)
                        or nameof(MappingSourceItem.Kind)
                        or nameof(MappingSourceItem.ParamUp)
                        or nameof(MappingSourceItem.ParamDown)
                        or nameof(MappingSourceItem.ParamRate)
                        or nameof(MappingSourceItem.ParamSticky)
                        or nameof(MappingSourceItem.ParamMin)
                        or nameof(MappingSourceItem.ParamMax)
                        or nameof(MappingSourceItem.ParamModifier)
                        or nameof(MappingSourceItem.ParamAttackTime)
                        or nameof(MappingSourceItem.ParamReleaseTime)
                        or nameof(MappingSourceItem.ParamAutocenter)
                        or nameof(MappingSourceItem.ParamReverseMultiplier))
                        _settingsService.MarkDirty();
                };
            }
            foreach (var msi in mapping.ExtraSources) WireExtraSource(msi);
            // The primary's kind holder (#111 follow-up) records its Up/Down/Modifier
            // keys and marks dirty through the same wiring as an extra source.
            if (mapping.PrimaryKindSource != null) WireExtraSource(mapping.PrimaryKindSource);
            mapping.ExtraSources.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (MappingSourceItem msi in e.NewItems)
                        WireExtraSource(msi);
                _settingsService.MarkDirty();
            };
        }

        /// <summary>True when a mapping descriptor names an analog axis or
        /// slider (with or without an I/H prefix) — a continuous input, not
        /// a button / POV / touchpad. Used to decide whether a stick-quadrant
        /// recording should be added alongside the primary as an extra source
        /// instead of overwriting it.</summary>
        private static bool DescriptorIsAnalogAxis(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return false;
            string d = descriptor;
            if (d.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                d = d.Substring(2);
            else if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
                d = d.Substring(1);
            else if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
                d = d.Substring(1);
            return d.StartsWith("Axis ", StringComparison.Ordinal)
                || d.StartsWith("Slider ", StringComparison.Ordinal);
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

            // Wire per-variable RecordRequested for the custom-expression
            // editor — both for variables present at load time and for any
            // added later via the "+ Add Variable" button.
            foreach (var v in macro.TriggerExpressionVariables)
                WireExpressionVariableRecording(v, padIndex);
            macro.TriggerExpressionVariables.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (MacroExpressionVariable v in e.NewItems)
                        WireExpressionVariableRecording(v, padIndex);
            };
        }

        private void WireExpressionVariableRecording(MacroExpressionVariable variable, int padIndex)
        {
            if (variable == null) return;
            variable.RecordRequested += (s, e) =>
            {
                if (s is not MacroExpressionVariable v) return;
                if (v.IsRecording)
                    _inputService.StartExpressionVariableRecording(v, padIndex);
                else
                    _inputService.StopExpressionVariableRecording();
            };
        }

        /// <summary>
        /// Wires a macro and its actions to trigger auto-save on any property change.
        /// </summary>
        private void WireMacroDirty(MacroItem macro)
        {
            macro.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            foreach (var action in macro.Actions)
                action.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            macro.Actions.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (MacroAction action in e.NewItems)
                        action.PropertyChanged += (s2, e2) => _settingsService.MarkDirty();
                _settingsService.MarkDirty();
            };
            // Custom-expression variables: any rebind should mark the
            // settings file dirty so the new binding round-trips through
            // SaveToFile.
            foreach (var v in macro.TriggerExpressionVariables)
                v.PropertyChanged += (s, e) => _settingsService.MarkDirty();
            macro.TriggerExpressionVariables.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (MacroExpressionVariable v in e.NewItems)
                        v.PropertyChanged += (s2, e2) => _settingsService.MarkDirty();
                _settingsService.MarkDirty();
            };
        }

        private void OnThemeChanged(object sender, int themeIndex)
        {
            if (themeIndex == 1)
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            else if (themeIndex == 2)
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            else
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        }

        // ─────────────────────────────────────────────
        //  System tray
        // ─────────────────────────────────────────────

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = Strings.Instance.Common_PadForge;

            // Load icon from the running executable.
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

            // Use WPF ContextMenu for themed tray menu (no WinForms ContextMenuStrip).
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                    Dispatcher.BeginInvoke(() => ShowTrayContextMenu());
            };

            // Double-click to restore.
            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        /// <summary>Invisible helper window that keeps WPF UI styles available for the tray context menu
        /// even when the main window is hidden.</summary>
        private Window _trayMenuHost;

        private void ShowTrayContextMenu()
        {
            // Ensure the invisible host window exists so the context menu inherits WPF UI styles.
            if (_trayMenuHost == null)
            {
                _trayMenuHost = new Window
                {
                    Width = 0, Height = 0,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Opacity = 0,
                };
                _trayMenuHost.Show();
            }

            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = _trayMenuHost,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            };

            var showItem = new System.Windows.Controls.MenuItem
            {
                Header = Strings.Instance.Main_ShowPadForge,
                FontWeight = FontWeights.SemiBold,
            };
            showItem.Click += (s, e) => RestoreFromTray();
            menu.Items.Add(showItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem
            {
                Header = "Exit",
            };
            exitItem.Click += (s, e) => { _notifyIcon.Visible = false; Close(); };
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
            {
                Hide();
                _notifyIcon.Visible = true;
            }

            // Persist window state (Normal or Maximized, not Minimized).
            if (WindowState != WindowState.Minimized)
            {
                _viewModel.Settings.MainWindowState = (int)WindowState;
                _settingsService.MarkDirty();
            }
        }

        private void OnLocationOrSizeChanged(object sender, EventArgs e)
        {
            // Only save when in Normal state (Maximized position/size is system-managed).
            if (WindowState != WindowState.Normal) return;
            var mw = _viewModel.Settings;
            mw.MainWindowLeft = Left;
            mw.MainWindowTop = Top;
            mw.MainWindowWidth = Width;
            mw.MainWindowHeight = Height;
            _settingsService.MarkDirty();
        }

        private bool _isRestoring;

        private void RestoreFromTray()
        {
            if (_isRestoring || IsVisible) return;
            _isRestoring = true;
            try
            {
                if (_isFullScreen)
                    WindowStyle = WindowStyle.None;
                Show();
                WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;
                Activate();
                _notifyIcon.Visible = false;
                if (_isFullScreen)
                    ForceToForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);

                // Re-apply theme so TitleBar ButtonsForeground updates (stale when
                // the window was never rendered, e.g. start-minimized-to-tray).
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    OnThemeChanged(this, _viewModel.Settings.SelectedThemeIndex);

                    // TitleBar.ButtonsForeground may not resolve on first show.
                    // Clear any stale local value so the XAML binding re-establishes,
                    // then nudge with a direct set that won't stick past the next
                    // theme change (ClearValue lets the binding win again next time).
                    FullScreenIcon.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
                    FullScreenIcon.InvalidateProperty(System.Windows.Controls.TextBlock.ForegroundProperty);

                    // If the binding still hasn't resolved, force-set as fallback.
                    if (FullScreenIcon.Foreground is not System.Windows.Media.SolidColorBrush scb
                        || scb.Color == System.Windows.Media.Colors.Black)
                    {
                        bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                            == Wpf.Ui.Appearance.ApplicationTheme.Dark;
                        if (isDark)
                            FullScreenIcon.Foreground = System.Windows.Media.Brushes.White;
                    }
                });
            }
            finally { _isRestoring = false; }
        }

        // ─────────────────────────────────────────────
        //  Copy / Paste / Copy From
        // ─────────────────────────────────────────────

        private void OnCopySettings(PadViewModel padVm)
        {
            var ps = _inputService.GetCurrentPadSetting(padVm.PadIndex);
            if (ps == null)
            {
                _viewModel.StatusText = Strings.Instance.Status_NoDeviceToCopyFrom;
                return;
            }

            try
            {
                var copyOutputType = padVm.OutputType;
                bool copyIsExtended = copyOutputType == VirtualControllerType.Extended
                    /* Extended always uses dynamic layout */;

                // Snapshot the slot's full MappingSet so Copy → Paste carries
                // every device's contribution — not just the slot's currently-
                // selected device's slice. Preserves source DeviceGuids so a
                // multi-device slot round-trips intact. The device-scoped slice
                // is dropped to keep the paste path unambiguous; the whole-slot
                // snapshot supersedes it.
                ps.SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(padVm.PadIndex);
                ps.DeviceScopedMultiSourceRows = null;

                // Bundle the per-slot config tabs (Lighting / Adaptive Triggers /
                // Mic LED / Player LED / audio-reactive / palette for PlayStation,
                // custom layout for Extended, CC + note layout for MIDI). These
                // live on PadViewModel, not PadSetting, so the clipboard JSON
                // carries them as opaque DTO-serialised strings on PadSetting and
                // OnPasteSettings unpacks + applies via SettingsService.
                var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
                var psConfigs = _settingsService.BuildPlayStationConfigSnapshotForSlot(padVm.PadIndex);
                if (psConfigs != null && psConfigs.Length > 0)
                    ps.SlotPlayStationConfigsJson = System.Text.Json.JsonSerializer.Serialize(psConfigs, jsonOpts);
                var extCfg = _settingsService.BuildExtendedConfigSnapshotForSlot(padVm.PadIndex);
                if (extCfg != null)
                    ps.SlotExtendedConfigJson = System.Text.Json.JsonSerializer.Serialize(extCfg, jsonOpts);
                var midiCfg = _settingsService.BuildMidiConfigSnapshotForSlot(padVm.PadIndex);
                if (midiCfg != null)
                    ps.SlotMidiConfigJson = System.Text.Json.JsonSerializer.Serialize(midiCfg, jsonOpts);

                // Bundle EVERY device's PadSetting on the source slot so
                // per-device tuning (deadzones, sensitivity, FFB, Gyro,
                // TouchpadSettings) round-trips for all devices, not just
                // the currently-selected one. Paste matches each entry to
                // a target-slot device by InstanceGuid first, ProductGuid
                // fallback. Outer `ps` still carries the selected device's
                // tuning for legacy compat with older paste payloads.
                var perDevice = InputService.BuildPerDeviceSettingsSnapshot(
                    padVm.PadIndex, copyOutputType, copyIsExtended);
                if (perDevice != null && perDevice.Length > 0)
                    ps.SlotPerDeviceSettingsJson = System.Text.Json.JsonSerializer.Serialize(perDevice, jsonOpts);

                Clipboard.SetText(ps.ToJson(copyOutputType, copyIsExtended));
                _viewModel.StatusText = Strings.Instance.Status_SettingsCopied;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message);
            }
        }

        private void OnPasteSettings(PadViewModel padVm)
        {
            try
            {
                string json = Clipboard.GetText();
                var ps = PadSetting.FromJson(json,
                    out VirtualControllerType srcType, out bool srcIsExtended);
                if (ps == null)
                {
                    _viewModel.StatusText = Strings.Instance.Status_InvalidClipboard;
                    return;
                }

                var targetType = padVm.OutputType;
                bool targetIsExtended = targetType == VirtualControllerType.Extended
                    /* Extended always uses dynamic layout */;

                // Whole-slot snapshot → replace the target's MappingSet
                // wholesale BEFORE the PadSetting tuning copy. Preserves
                // source DeviceGuids so a paste onto a slot with the same
                // devices restores both devices' contributions. The
                // ApplyPadSettingToCurrentDeviceTranslated call below would
                // otherwise route only the device-scoped slice through
                // ApplyMultiSourceRowsToCurrentDevice, retargeting all
                // sources onto the currently-selected device.
                if (ps.SlotMultiSourceRows != null && ps.SlotMultiSourceRows.Count > 0
                    && MappingTranslation.IsSameLayout(srcType, srcIsExtended, targetType, targetIsExtended))
                {
                    InputService.ApplySlotMappingSetFromRows(padVm.PadIndex, ps.SlotMultiSourceRows);
                    ps.DeviceScopedMultiSourceRows = null;
                }

                _inputService.ApplyPadSettingToCurrentDeviceTranslated(
                    padVm.PadIndex, ps,
                    srcType, srcIsExtended,
                    targetType, targetIsExtended);

                // Unpack the per-slot config tabs that travelled through the
                // clipboard JSON as opaque strings on PadSetting. Same semantics
                // as the in-process Copy From: PlayStation features copy
                // unconditionally (physical-device passthrough), Extended /
                // MIDI gated on matching slot type by their Apply methods.
                if (!string.IsNullOrEmpty(ps.SlotPlayStationConfigsJson))
                {
                    try
                    {
                        var psConfigs = System.Text.Json.JsonSerializer.Deserialize<ViewModels.PlayStationSlotConfigData[]>(ps.SlotPlayStationConfigsJson);
                        _settingsService.ApplyPlayStationConfigsToSlot(padVm.PadIndex, psConfigs);
                    }
                    catch { /* malformed payload — Lighting tab paste skipped */ }
                }
                if (!string.IsNullOrEmpty(ps.SlotExtendedConfigJson))
                {
                    try
                    {
                        var extCfg = System.Text.Json.JsonSerializer.Deserialize<ViewModels.ExtendedSlotConfigData>(ps.SlotExtendedConfigJson);
                        _settingsService.ApplyExtendedConfigToSlot(padVm.PadIndex, extCfg);
                    }
                    catch { /* malformed payload — Extended layout paste skipped */ }
                }
                if (!string.IsNullOrEmpty(ps.SlotMidiConfigJson))
                {
                    try
                    {
                        var midiCfg = System.Text.Json.JsonSerializer.Deserialize<ViewModels.MidiSlotConfigData>(ps.SlotMidiConfigJson);
                        _settingsService.ApplyMidiConfigToSlot(padVm.PadIndex, midiCfg);
                    }
                    catch { /* malformed payload — MIDI layout paste skipped */ }
                }

                // Per-device tuning for EVERY device on the source slot,
                // not just the currently-selected one. Match by InstanceGuid
                // first (perfect round-trip on same machine), ProductGuid
                // fallback (same model, different physical unit). Entries
                // with no target match are skipped. The outer ApplyPadSetting
                // call above already wrote the selected device — applying
                // the per-device array now will overwrite it with the same
                // (or fresher) data, which is fine and idempotent.
                if (!string.IsNullOrEmpty(ps.SlotPerDeviceSettingsJson))
                {
                    try
                    {
                        var perDevice = System.Text.Json.JsonSerializer.Deserialize<
                            PadForge.Engine.Data.PerDeviceSettingsEntry[]>(ps.SlotPerDeviceSettingsJson);
                        _inputService.ApplyPerDeviceSettingsToSlot(padVm.PadIndex, perDevice,
                            srcType, srcIsExtended, targetType, targetIsExtended);
                    }
                    catch { /* malformed payload — per-device paste skipped */ }
                }

                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_SettingsPasted;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_PasteFailed_Format, ex.Message);
            }
        }

        private void OnCopyFrom(PadViewModel padVm)
        {
            // Flush all pad UIs to storage so source PadSettings reflect current state.
            _inputService.FlushAllPadViewModels();

            // Build list of all devices that have configured settings.
            var entries = new List<CopyFromDialog.DeviceEntry>();
            // Mappings are stored per-slot (in the slot's MappingSet) since the
            // multi-source merge, so Copy From lists one entry per source slot
            // rather than one per assigned device — a slot with N devices used
            // to surface N duplicates with the same SlotLabel. Per-device tuning
            // (deadzones / FFB / sensitivity) still differs; we pick the slot's
            // currently-selected device as the tuning donor for each entry.
            // Unmapped devices (MapTo < 0) keep one entry per device since they
            // have no slot to dedupe under.
            var slotChosenDevice = new Dictionary<int, Guid>();
            var settings = SettingsManager.UserSettings?.Items;
            if (settings != null)
            {
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    foreach (var us in settings)
                    {
                        // Skip this slot entirely — can't Copy From self.
                        if (us.MapTo == padVm.PadIndex) continue;

                        var ps = us.GetPadSetting();
                        // Eligibility: the slot's per-VC MappingSet is the v3
                        // source of truth (PadSetting descriptors mirror only
                        // the currently-visible layer, so a slot whose UI is
                        // sitting on an empty shift layer would falsely test
                        // as "not configured" via PadSetting.HasAnyMapping).
                        bool slotHasRows = us.MapTo >= 0 && InputService.SlotHasAnyMapping(us.MapTo);
                        if (ps == null) continue;
                        if (!slotHasRows && !ps.HasAnyMapping) continue;

                        // For mapped slots, dedupe: only one entry per source
                        // slot, donor = the slot's currently-selected device,
                        // falling back to the first device we encounter with a
                        // valid PadSetting.
                        if (us.MapTo >= 0)
                        {
                            if (slotChosenDevice.ContainsKey(us.MapTo)) continue;

                            Guid donor = Guid.Empty;
                            if (us.MapTo < _viewModel.Pads.Count)
                                donor = _viewModel.Pads[us.MapTo].SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

                            // Promote the donor's UserSetting if it's not the
                            // current iteration's `us` — otherwise this `us` is
                            // the chosen donor.
                            if (donor != Guid.Empty && donor != us.InstanceGuid)
                            {
                                var donorUs = settings.FirstOrDefault(
                                    u => u != null && u.MapTo == us.MapTo && u.InstanceGuid == donor);
                                if (donorUs != null)
                                {
                                    var donorPs = donorUs.GetPadSetting();
                                    if (donorPs != null && (slotHasRows || donorPs.HasAnyMapping))
                                    {
                                        AddEntry(entries, donorUs, donorPs);
                                        slotChosenDevice[us.MapTo] = donor;
                                        continue;
                                    }
                                }
                            }

                            AddEntry(entries, us, ps);
                            slotChosenDevice[us.MapTo] = us.InstanceGuid;
                        }
                        else
                        {
                            // Unmapped (MapTo < 0) — one entry per device.
                            AddEntry(entries, us, ps);
                        }
                    }
                }
            }

            void AddEntry(List<CopyFromDialog.DeviceEntry> list, UserSetting us, PadSetting ps)
            {
                var outputType = VirtualControllerType.Xbox;
                bool isExtended = false;
                if (us.MapTo >= 0 && us.MapTo < _viewModel.Pads.Count)
                {
                    var srcPad = _viewModel.Pads[us.MapTo];
                    outputType = srcPad.OutputType;
                    isExtended = outputType == VirtualControllerType.Extended;
                }

                // Primary line identifies the SLOT, not the device. The
                // mappings copy is per-slot since the multi-source merge,
                // so the dialog reads as: "Virtual Controller {global#} —
                // {Type} {in-group#}" (e.g. "Virtual Controller 3 — Xbox 2").
                // Unmapped UserSettings (rare; MapTo < 0) fall back to the
                // device name since they have no slot identity.
                string primary;
                if (us.MapTo >= 0)
                {
                    int globalNum = SettingsManager.SlotOrders.GetGlobalSlotNumber(us.MapTo);
                    int inGroupNum = SettingsManager.SlotOrders.GetOrderFor(outputType).IndexOf(us.MapTo) + 1;
                    string typeName = ControllerTypeDisplayName(outputType);
                    string vcWord = Strings.Instance.Main_VirtualController_Format.Replace("{0}", globalNum.ToString());
                    primary = inGroupNum > 0
                        ? $"{vcWord} — {typeName} {inGroupNum}"
                        : $"{vcWord} — {typeName}";
                }
                else
                {
                    var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                    primary = ud?.InstanceName;
                    if (string.IsNullOrEmpty(primary)) primary = ud?.ProductName;
                    if (string.IsNullOrEmpty(primary)) primary = us.InstanceGuid.ToString();
                }

                list.Add(new CopyFromDialog.DeviceEntry
                {
                    Name = primary,
                    SlotLabel = $"{us.InstanceGuid:D}",
                    LayoutLabel = string.Empty,
                    InstanceGuid = us.InstanceGuid,
                    PadSetting = ps,
                    OutputType = outputType,
                    IsExtended = isExtended,
                    SourceSlot = us.MapTo,
                });
            }

            static string ControllerTypeDisplayName(VirtualControllerType t) => t switch
            {
                VirtualControllerType.Xbox          => Strings.Instance.ControllerType_Xbox,
                VirtualControllerType.PlayStation   => Strings.Instance.ControllerType_PlayStation,
                VirtualControllerType.Extended      => Strings.Instance.ControllerType_Extended,
                VirtualControllerType.KeyboardMouse => Strings.Instance.ControllerType_KeyboardMouse,
                VirtualControllerType.Midi          => Strings.Instance.ControllerType_MIDI,
                _ => t.ToString(),
            };

            if (entries.Count == 0)
            {
                _viewModel.StatusText = Strings.Instance.Status_NoOtherDevices;
                return;
            }

            var dialog = new CopyFromDialog(entries) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedEntry != null)
            {
                var srcEntry = dialog.SelectedEntry;
                var targetOutputType = padVm.OutputType;
                bool targetIsExtended = targetOutputType == VirtualControllerType.Extended
                    /* Extended always uses dynamic layout */;

                // Issue #61 — "Copy From" is a SLOT-level copy. Replace this
                // slot's per-VC MappingSet with the SOURCE slot's wholesale
                // (every device's sources, all extra sources, combine modes,
                // Custom formulas) — the device the user picked in the dialog
                // only identifies which slot to copy from. The old behavior
                // copied just the picked device's slice, so a slot whose
                // source rows mixed devices (e.g. a gamepad axis + keyboard
                // buttons on the same stick row) lost the keyboard half.
                if (srcEntry.SourceSlot >= 0 && srcEntry.SourceSlot != padVm.PadIndex)
                {
                    InputService.ReplaceSlotMappingSet(padVm.PadIndex, srcEntry.SourceSlot);
                    // Don't also run the per-device multi-source merge below —
                    // ReplaceSlotMappingSet already set the whole table, and
                    // merging the picked device's slice on top would just
                    // reorder its sources.
                    srcEntry.PadSetting.DeviceScopedMultiSourceRows = null;
                }
                else if (srcEntry.SourceSlot >= 0)
                {
                    // Niche: copying from a different device on THIS SAME slot.
                    // Can't wholesale-replace (source == target slot), so fall
                    // back to merging the picked device's slice onto whichever
                    // device receives the copy.
                    srcEntry.PadSetting.DeviceScopedMultiSourceRows =
                        InputService.ExtractDeviceScopedRowsForSlot(srcEntry.SourceSlot, srcEntry.InstanceGuid);
                }

                // Pick the device on THIS slot that receives the per-device
                // tuning copy (deadzones / sensitivity / FFB, plus the legacy
                // single-source descriptors which RefreshMappingsCore now
                // ignores in favor of the MappingSet). If the source device
                // is also mapped to this slot, copy onto that same device —
                // so "Copy From [a gamepad on another slot]" lands on the
                // matching gamepad here instead of being re-tagged onto
                // whatever's selected (e.g. the slot's keyboard, which has no
                // analog axes — that re-tag produced phantom "keyboard Axis 0"
                // sources, doubled every row, and overwrote hand-crafted
                // multi-source rows). Falls back to the selected device when
                // the source device isn't on this slot.
                Guid? targetDeviceOverride = null;
                bool sourceDeviceOnThisSlot = false;
                if (SettingsManager.UserSettings?.Items != null)
                {
                    lock (SettingsManager.UserSettings.SyncRoot)
                    {
                        foreach (var u in SettingsManager.UserSettings.Items)
                        {
                            if (u != null && u.InstanceGuid == srcEntry.InstanceGuid && u.MapTo == padVm.PadIndex)
                            { sourceDeviceOnThisSlot = true; break; }
                        }
                    }
                }
                if (sourceDeviceOnThisSlot)
                    targetDeviceOverride = srcEntry.InstanceGuid;

                _inputService.ApplyPadSettingToCurrentDeviceTranslated(
                    padVm.PadIndex, srcEntry.PadSetting,
                    srcEntry.OutputType, srcEntry.IsExtended,
                    targetOutputType, targetIsExtended,
                    targetDeviceOverride);

                // Apply EVERY source-slot device's per-device tuning, not
                // just the one the dialog highlighted. Match by InstanceGuid
                // first (perfect round-trip), ProductGuid fallback (same model,
                // different unit). The dialog's SelectedEntry only identifies
                // WHICH slot to copy from; the slot's per-device tuning for
                // gyro, touchpad-tab settings, deadzones, etc. comes along
                // for every device.
                if (srcEntry.SourceSlot >= 0 && srcEntry.SourceSlot != padVm.PadIndex)
                {
                    var perDevice = InputService.BuildPerDeviceSettingsSnapshot(
                        srcEntry.SourceSlot, srcEntry.OutputType, srcEntry.IsExtended);
                    if (perDevice != null && perDevice.Length > 0)
                        _inputService.ApplyPerDeviceSettingsToSlot(padVm.PadIndex, perDevice,
                            srcEntry.OutputType, srcEntry.IsExtended,
                            targetOutputType, targetIsExtended);
                }

                // PadSetting carries deadzones, sensitivity, FFB, mapping
                // descriptors. The per-slot config tabs (Lighting / custom
                // Extended layout / MIDI CC+note layout) live on PadViewModel,
                // not PadSetting — clone those explicitly so a "Copy From"
                // actually copies the whole slot, not just half.
                if (srcEntry.SourceSlot >= 0)
                    _settingsService.CopySlotConfigsAcrossSlots(srcEntry.SourceSlot, padVm.PadIndex);

                // Rebuild the target slot's shift-layer tab strip from the
                // freshly-copied activators so layers authored on the source
                // slot show up here instead of being invisible until the next
                // app launch.
                var targetMs = SettingsManager.SlotMappingSets != null
                    && padVm.PadIndex >= 0 && padVm.PadIndex < SettingsManager.SlotMappingSets.Length
                    ? SettingsManager.SlotMappingSets[padVm.PadIndex] : null;
                padVm.RebuildLayerTabs(targetMs?.ShiftActivators);

                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_SettingsCopiedFromDevice;
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
                _viewModel.StatusText = Strings.Instance.Common_Ready;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined UAC prompt.
                _viewModel.StatusText = Strings.Instance.Status_OperationCancelled;
            }
            catch (Exception ex)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_DriverOperationFailed_Format, ex.Message);
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
            // Iterate per-group in the fixed visual order so the dashboard
            // matches the sidebar's per-group rendering. Slot indices are
            // stable identifiers; group order owns the layout.
            var activeSlots = new System.Collections.Generic.List<int>();
            int totalActive = 0;
            foreach (var groupType in Engine.VirtualControllerGroups.InOrder)
            {
                foreach (int padIndex in SettingsManager.SlotOrders.GetOrderFor(groupType))
                {
                    if (padIndex < 0 || padIndex >= InputManager.MaxPads) continue;
                    if (!SettingsManager.SlotCreated[padIndex]) continue;
                    activeSlots.Add(padIndex);
                    totalActive++;
                }
            }
            // Total active slots is bounded by MaxPads. Per-group caps don't
            // matter for "Add Controller" availability when the global cap is
            // the binding constraint.
            bool canAddMore = totalActive < InputManager.MaxPads;
            _viewModel.Dashboard.RefreshActiveSlots(activeSlots, canAddMore);
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

        private void RefreshMidiServicesStatus()
        {
            try
            {
                bool installed = DriverInstaller.IsMidiServicesInstalled();
                _viewModel.Settings.IsMidiServicesInstalled = installed;
                _viewModel.Dashboard.IsMidiServicesInstalled = installed;
                _viewModel.Settings.MidiServicesVersion = installed ? "Windows MIDI Services" : string.Empty;
            }
            catch
            {
                _viewModel.Settings.IsMidiServicesInstalled = false;
                _viewModel.Dashboard.IsMidiServicesInstalled = false;
            }
            if (_navDashboard != null) RefreshControllerNavItemsInPlace();
        }

    }
}
