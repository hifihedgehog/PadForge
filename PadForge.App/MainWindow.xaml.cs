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

        // Pumps SDL's event queue on the UI thread so hidapi controller hot-plug
        // works in-process (#116). See where it is started in the constructor.
        private System.Windows.Threading.DispatcherTimer _sdlPumpTimer;
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
        // Profile switcher flyout (#175 item 8): own open/close-debounce
        // pair so it never cross-suppresses the controller-type popup.
        private Popup _profileSwitcherPopup;
        private DateTime _profilePopupClosedAt;

        /// <summary>When non-null, the next recording result goes to this mapping's NegSourceDescriptor.</summary>
        private MappingItem _pendingNegMapping;
        /// <summary>Saved positive descriptor while recording the negative direction.</summary>
        private string _savedPosDescriptor;
        /// <summary>Set when the neg-first quadrant chain starts its second
        /// (positive) phase. PromoteNegDescriptorToExtraSource empties
        /// NegSourceDescriptor at the end of phase one, so the normal-path
        /// "neg isn't already mapped" emptiness check would re-prompt for the
        /// direction that was just recorded. The flag marks the row whose
        /// chain is already complete; consumed by the next completion.</summary>
        private MappingItem _negChainCompletedMapping;

        // #111 single-button recording for a non-Direct primary kind. The one
        // row Record button records the kind's own inputs. Ramp / Incremental
        // capture Up then Down in sequence. Invert On Hold captures the modifier.
        private enum KindRecStage { None, Up, Down }
        private MappingItem _kindRecordMapping;   // non-null while a kind record is in flight
        private KindRecStage _kindRecordStage;
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

            // Steel ground tracks the theme from first paint (#175). The
            // XAML default is Visible, which is wrong when the app starts
            // in Light.
            UpdateSteelLayer();

            // First-run welcome (#175): shown once, marker-gated.
            Loaded += MaybeShowFirstRun;

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
            // ItemInvoked fires even when SelectsOnInvoked=false (used for
            // AddController). Wired here ONLY. BuildNavigationItems used to
            // subscribe it a second time.
            NavView.ItemInvoked += NavView_ItemInvoked;

            // Fade compact icons in when pane closes, fade out when it opens.
            NavView.PaneClosed += (_, _) =>
            {
                _isCardFading = true;
                // Clear any leftover animation state from previous cycle.
                foreach (var mi in NavView.MenuItems)
                    if (mi is NavigationViewItem nvi && IsCardFadeItem(nvi))
                    { nvi.BeginAnimation(UIElement.OpacityProperty, null); nvi.Opacity = 0; }
                UpdateAllControllerCardMode(compact: true);

                // Delay for pane animation, then fade in, then unlock.
                var delayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                delayTimer.Tick += (s2, e2) =>
                {
                    delayTimer.Stop();
                    foreach (var mi in NavView.MenuItems)
                        if (mi is NavigationViewItem nvi && IsCardFadeItem(nvi))
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
                    if (mi is NavigationViewItem nvi && IsCardFadeItem(nvi))
                    { nvi.BeginAnimation(UIElement.OpacityProperty, null); nvi.Opacity = 0; }
                UpdateAllControllerCardMode(compact: false);

                var delayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                delayTimer.Tick += (s2, e2) =>
                {
                    delayTimer.Stop();
                    foreach (var mi in NavView.MenuItems)
                        if (mi is NavigationViewItem nvi && IsCardFadeItem(nvi))
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
            // PadPage too: the pages are instantiated eagerly (visibility-
            // toggled, not navigated), so PadPage's whole binding tree
            // evaluates at window realization. Without a pad context it
            // inherits this window's MainViewModel and every path fails
            // (479 distinct binding path errors in one second on the
            // 2026-07-12 launch trace). Navigation re-points it at the
            // selected pad.
            if (_viewModel.Pads.Count > 0)
                PadPageView.DataContext = _viewModel.Pads[0];

            // Create services.
            _settingsService = new SettingsService(_viewModel);
            _inputService = new InputService(_viewModel) { SettingsService = _settingsService };
            // Crash / ProcessExit net: quiet all hardware outputs so an
            // abnormal exit can't leave a pad rumbling (discussion #179).
            App.PanicQuiesce = () => _inputService.PanicQuiesceOutputs();
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
            // Drop-zone import (#175): dropped .pfprofile files run the same
            // consumer as the Import button's file dialog.
            ProfilesPageView.ImportProfileFile = ImportProfileFromFile;
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
            _viewModel.Settings.ResetRequested += (s, e) =>
            {
                // Reset to Defaults also tears down every virtual controller
                // (user report 2026-07-06: the left rail and dashboard kept
                // their slots after a reset, which is not "defaults"). Same
                // per-slot sequence as the delete buttons, highest slot first
                // so each removal's kernel-slot bubble-down cascade never
                // re-shuffles a slot this loop is about to delete anyway.
                if (_viewModel.SelectedPadIndex >= 0)
                    SelectNavItemByTag("Dashboard");
                for (int i = PadForge.Common.Input.InputManager.MaxPads - 1; i >= 0; i--)
                {
                    if (!SettingsManager.SlotCreated[i]) continue;
                    bool hadActiveVc = _inputService.IsHmVcAt(i);
                    var info = _deviceService.DeleteSlot(i);
                    _inputService.OnSlotDeleted(i, info.Type, info.OldGroupPosition,
                        deletedSlotHadActiveVc: hadActiveVc);
                }
                _settingsService.ResetToDefaults();
                // Strip driver-side HidHide cloaks now. The reset wiped
                // every per-device HidHideEnabled record and the app
                // whitelist, but the desired-state diff only runs inside
                // ApplyDeviceHiding, and store clears fire none of the
                // property-changed hooks that normally trigger it. The
                // purge covers the diff's blind spot: cloaks persisted by
                // "keep cloaks between launches" in a session where the
                // engine never started are not in the in-process managed
                // set, so the diff alone would leave them asserted.
                _inputService.PurgeStaleHidHideCloaks();
                _inputService.ApplyDeviceHiding();
                _viewModel.Devices.RefreshSlotButtons();
                _inputService.RefreshDeviceList();
                _viewModel.RefreshNavControllerItems();
            };
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
            _viewModel.Settings.BrowseCommunityConfigsRequested += OnBrowseCommunityConfigs;
            _viewModel.Settings.ClearWorkshopCacheRequested += OnClearWorkshopCache;
            _viewModel.Settings.CheckWorkshopUpdatesRequested += OnCheckWorkshopUpdates;

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
                     or nameof(SettingsViewModel.EnableAutoProfileSwitching)
                     or nameof(SettingsViewModel.EnableCommunityConfigLookup)
                     or nameof(SettingsViewModel.ShowLegacyWorkshopConfigs))
                    _settingsService.MarkDirty();
            };

            // Persist DSU / Remote Link / web controller / overlay settings on
            // change (Dashboard VM). This list must cover every Dashboard
            // property LoadAppSettings restores and BuildAppSettings writes:
            // a persisted property missing here changes live state but never
            // marks the file dirty, so a normal close (which saves only when
            // IsDirty) silently discards it. The Remote Link trio was missing.
            _viewModel.Dashboard.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(DashboardViewModel.EnableDsuMotionServer)
                     or nameof(DashboardViewModel.DsuMotionServerPort)
                     or nameof(DashboardViewModel.EnableWebController)
                     or nameof(DashboardViewModel.WebControllerPort)
                     or nameof(DashboardViewModel.EnableRemoteLink)
                     or nameof(DashboardViewModel.AutoReconnect)
                     or nameof(DashboardViewModel.RemoteLinkPort)
                     or nameof(DashboardViewModel.EnableTouchpadOverlay)
                     or nameof(DashboardViewModel.TouchpadOverlayOpacity)
                     or nameof(DashboardViewModel.TouchpadOverlayMonitor)
                     or nameof(DashboardViewModel.TouchpadOverlayLeft)
                     or nameof(DashboardViewModel.TouchpadOverlayTop)
                     or nameof(DashboardViewModel.TouchpadOverlayWidth)
                     or nameof(DashboardViewModel.TouchpadOverlayHeight)
                     or nameof(DashboardViewModel.EnableMenuOverlay))
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
                // And persist it. The whitelist took effect immediately but
                // never marked the settings dirty, so a session whose only
                // change was a whitelist edit discarded it on close and the
                // path was gone on next launch.
                //
                // Marked HERE rather than at the mutation site because this
                // event is the funnel every whitelist change already raises,
                // and the LOAD path (SettingsService's Clear-and-repopulate)
                // deliberately does not raise it, so this cannot dirty the
                // file merely by reading it.
                _settingsService?.MarkDirty();
            };

            // Wire MIDI Services install/uninstall commands.
            _viewModel.Settings.InstallMidiServicesRequested += async (s, e) =>
            {
                _viewModel.SetStatus(Strings.Instance.Status_DownloadingMidi, persist: true);
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
                    _viewModel.SetStatus(string.Format(Strings.Instance.Status_MidiInstallFailed_Format, ex.Message), persist: true);
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

                // Player-identity idle floor (#191): slot creation and
                // device (un)assignment both renumber and re-home pads.
                _inputService.ReseedPlayerIdentities();

                // Issue #83. Controller-audio sinks follow assignments.
                PadForge.Common.Input.AudioPassthroughService.Reconcile();
                PadForge.Common.Input.WiiSpeakerService.Reconcile();
                PadForge.Common.Input.HapticToneService.Reconcile();

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

            // Wire devices page Bluetooth pairing (Wii controllers, issue #116).
            _viewModel.Devices.PairRequested += (s, e) =>
            {
                var dialog = new Views.PairDeviceDialog { Owner = this };
                dialog.ShowDialog();
                // A just-paired Wii controller is grabbed by SDL mid-pairing and
                // dropped. Force SDL to cleanly re-open it so it appears without
                // an app restart (#116).
                _inputService.RescanWiiControllers();
                _inputService.RefreshDeviceList();
                _viewModel.StatusText = Strings.Instance.Status_DeviceListRefreshed;
            };

            // Pump SDL's event queue on the UI thread (the SDL_Init thread).
            // SDL's hidapi posts its device-change messages to a hidden window
            // on this thread; dispatching them is what makes SDL re-scan for
            // connected/removed controllers. Without this, a device that drops on
            // a read hiccup never returns and a freshly-paired one never appears
            // until an app restart (#116). 100 ms is well under SDL's own
            // detection cadence and negligible cost.
            _sdlPumpTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(100)
            };
            _sdlPumpTimer.Tick += (s, e) => _inputService.PumpSdlEvents();
            _sdlPumpTimer.Start();

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
                pad.GyroCalibrateRequested += async (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    // Busy-guard (round eight, R6): the MaxValue hold IS
                    // the in-flight flag, so a double-click cannot start
                    // two samplers racing each other's labels and writes.
                    if (pvm.GyroCalibrationLabelHoldUntilUtc == DateTime.MaxValue) return;
                    var selected = pvm.SelectedMappedDevice;
                    if (selected == null || selected.InstanceGuid == Guid.Empty) return;
                    var ud = PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(selected.InstanceGuid);
                    if (ud == null || !ud.HasGyro) return;
                    var us = PadForge.Common.Input.SettingsManager.FindSettingByInstanceGuidAndSlot(selected.InstanceGuid, pvm.PadIndex);
                    var ps = us?.GetPadSetting();
                    if (ps == null) return;
                    var calibratedGuid = selected.InstanceGuid;
                    int generation = pvm.GyroCalibrationGeneration;
                    // A pass already owns this profile (round eleven).
                    // Auto-calibration fires on device connect and runs
                    // 1.5 s, which is exactly when a user plugs in a gyro
                    // pad and reaches for this button, and the calibrator
                    // refuses a second concurrent pass. Reporting that
                    // refusal as "Couldn't calibrate" blamed a healthy,
                    // stationary pad for the most likely gesture on the
                    // tab. Round nine introduced the refusal and round
                    // ten taught the auto lane and the Reset handler what
                    // it means; this caller was never taught. Show the
                    // run that IS happening instead.
                    if (GyroCalibratorService.IsSampling(ps))
                    {
                        pvm.GyroCalibrationLabel =
                            PadForge.Resources.Strings.Strings.Instance.Settings_GyroCalibrating;
                        pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.UtcNow.AddSeconds(2);
                        return;
                    }
                    // Hold the label for the run (round seven, R1): the
                    // 30 Hz tick otherwise clobbers "Calibrating…" within
                    // one frame, and a motion-rejected run looked exactly
                    // like nothing happening, so the Calibrate button read
                    // as dead.
                    pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.MaxValue;
                    pvm.GyroCalibrationLabel = PadForge.Resources.Strings.Strings.Instance.Settings_GyroCalibrating;
                    bool ok = false;
                    try { ok = await _inputService.GyroCalibrator.RecalibrateAsync(ud, ps); }
                    catch { }
                    // Post-await re-validation (round eight, R6): if the
                    // pad's selection moved to a DIFFERENT device during
                    // the run, neither banner belongs to what is now on
                    // screen. Just release the hold; the tick shows the
                    // current device's own state.
                    // A Reset during the run voids this result (round
                    // nine, R5): the write-guard already discarded the
                    // measurement, so reporting "Couldn't calibrate"
                    // would blame the user's own abort, and would sit on
                    // top of whatever the Reset's own auto-fire produced.
                    if (pvm.SelectedMappedDevice?.InstanceGuid != calibratedGuid
                        || pvm.GyroCalibrationGeneration != generation)
                    {
                        pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.MinValue;
                        return;
                    }
                    if (ok)
                    {
                        // Write the fresh label DIRECTLY (round eight,
                        // R6): the 30 Hz tick is skipped while the window
                        // is minimized, the page hidden, or the selection
                        // non-motion, and delegating to it left
                        // "Calibrating…" pinned indefinitely in those
                        // states.
                        // Format from the stamp the run just wrote, not
                        // DateTime.Now (round nine, R10): the two are read
                        // an instant apart and disagree across a minute
                        // boundary, and after an aux-only upgrade (which
                        // leaves the primary stamp untouched) "now" would
                        // claim a calibration that was never stamped.
                        var shown = DateTime.TryParse(ps.GyroCalibratedAtUtc,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var stamped)
                            ? stamped.ToLocalTime()
                            : DateTime.Now;
                        pvm.GyroCalibrationLabel = string.Format(
                            PadForge.Resources.Strings.Strings.Instance.Settings_GyroLastCalibrated_Format,
                            shown);
                        pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.MinValue;
                    }
                    else
                    {
                        pvm.GyroCalibrationLabel = PadForge.Resources.Strings.Strings.Instance.Settings_GyroCalibrateFailed;
                        pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.UtcNow.AddSeconds(5);
                    }
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
                    // Void any in-flight manual run BEFORE clearing the
                    // data, so its completion releases quietly instead of
                    // blaming the user for their own abort (round nine,
                    // R5), and release the label hold so the tick governs
                    // again.
                    pvm.GyroCalibrationGeneration++;
                    // Release the label hold ONLY when no manual run owns
                    // it (round ten): the MaxValue hold doubles as the
                    // busy-guard, so clearing it mid-run re-admitted the
                    // Calibrate button, whose start was then refused by
                    // the per-profile in-flight guard and rendered as
                    // "Couldn't calibrate" on a perfectly healthy pad.
                    // A run that owns the hold releases it itself, and
                    // the generation bump above already voids its result.
                    if (pvm.GyroCalibrationLabelHoldUntilUtc != DateTime.MaxValue)
                        pvm.GyroCalibrationLabelHoldUntilUtc = DateTime.MinValue;
                    _inputService.GyroCalibrator.ResetCalibration(ps);
                    _inputService.ClearGyroAutoCalibLatch(ud.InstanceGuid, pvm.PadIndex);
                    pvm.GyroCalibrationLabel = PadForge.Resources.Strings.Strings.Instance.Settings_GyroNeverCalibrated;
                    // Keep the tooltip's promise (round eight, R8): fire
                    // the auto-calibration scan now instead of waiting for
                    // the next incidental device event. Any in-flight run
                    // is refused by the calibrator's per-profile
                    // in-flight guard, and its own result is discarded by
                    // the write-guard, so this cannot double-sample or
                    // revert the reset (round nine, R4/R5).
                    _inputService.RequestGyroAutoCalibration();
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

                // Record button on the Menus tab's host picker (#9 B-17).
                // Same freeform-recorder toggle as the Aim Engage picker
                // above; the recorded descriptor folds onto a host choice
                // (stick axes / stick click pick the stick, any touchpad
                // read picks the pad) and inputs with no hover surface are
                // ignored.
                pad.MenuHostRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    var menu = pvm.SelectedMenu;
                    if (menu == null) return;
                    if (menu.HostRecording)
                    {
                        _recorderService.CancelRecording();
                        menu.HostRecording = false;
                        return;
                    }
                    // Four targets share the one freeform recorder: the
                    // opener fold, the Custom steer axes, and the Click
                    // input. The command aimed PendingRecordTarget before
                    // raising; capture it so a re-aim mid-record cannot
                    // misroute the callback.
                    var target = menu.PendingRecordTarget;
                    menu.BeginRecord(target);
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        menu.HostRecording = false;
                        if (menu.TryApplyRecorded(target, descriptor))
                            _settingsService.MarkDirty();
                    });
                };

                // Record button on the Mouse Gestures card's custom
                // activation picker (discussion #216). Same freeform-recorder
                // toggle as the Aim Engage picker above; the VM setters push
                // the pair into the active mouse's MouseGestureSettings entry
                // and the PropertyChanged hook marks dirty + refreshes the
                // Mappings-tab picker.
                pad.MouseGestureCustomEngageRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    if (pvm.MouseGestureCustomEngageRecording)
                    {
                        _recorderService.CancelRecording();
                        pvm.MouseGestureCustomEngageRecording = false;
                        return;
                    }
                    pvm.MouseGestureCustomEngageRecording = true;
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        pvm.MouseGestureCustomEngageButton = descriptor ?? "";
                        pvm.MouseGestureCustomEngageDeviceGuid = deviceGuid ?? "";
                        pvm.MouseGestureCustomEngageRecording = false;
                        _settingsService.MarkDirty();
                    });
                };

                // Record button on the haptic-mirror engage picker (#185).
                // Same freeform-recorder toggle as the Aim Engage picker above;
                // the result lands on the SELECTED device's DeviceConfig
                // (resolved at callback time, so a device switch mid-recording
                // writes to whichever config is then active, like every other
                // DeviceConfig-backed control).
                pad.MirrorEngageRecordRequested += (s, e) =>
                {
                    if (s is not PadViewModel pvm) return;
                    if (pvm.MirrorEngageRecording)
                    {
                        _recorderService.CancelRecording();
                        pvm.MirrorEngageRecording = false;
                        return;
                    }
                    pvm.MirrorEngageRecording = true;
                    _recorderService.StartRecordingFreeform(pvm.PadIndex, (deviceGuid, descriptor) =>
                    {
                        var cfg = pvm.DeviceConfig;
                        if (cfg != null)
                        {
                            cfg.AudioMirrorEngageButton = descriptor ?? "";
                            cfg.AudioMirrorEngageDeviceGuid = deviceGuid ?? "";
                            pvm.OnMirrorEngageSelectedInputRefresh();
                        }
                        pvm.MirrorEngageRecording = false;
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
                        nameof(PadViewModel.TouchpadEnableTouchSpots) or
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
                        nameof(PadViewModel.TouchpadMouseInvertY) or
                        // Absolute-pointer stretch (#9 B-15). Without these
                        // the card's edits never mark dirty and revert on
                        // restart (the Motion Lean lesson, audit lens 1m F2).
                        nameof(PadViewModel.TouchpadPointerStretchX) or
                        nameof(PadViewModel.TouchpadPointerStretchY) or
                        nameof(PadViewModel.TouchpadSwipeHapticsEnabled) or
                        nameof(PadViewModel.TouchpadSwipeHapticsIntensity);

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

                    // Mouse-gesture fields (#200): same contract as the
                    // touchpad branch above. PropertyChanged fires before the
                    // setter's tail-call Sync, so Sync runs explicitly here,
                    // then MarkDirty queues the autosave.
                    bool isMouseGestureField = e.PropertyName is
                        nameof(PadViewModel.MouseGesturesEnabled) or
                        nameof(PadViewModel.MouseGestureButtons) or
                        nameof(PadViewModel.MouseGestureCustomEngageButton) or
                        nameof(PadViewModel.MouseGestureCustomEngageDeviceGuid) or
                        nameof(PadViewModel.MouseGestureFlickThreshold) or
                        nameof(PadViewModel.MouseGestureCooldownMs);

                    if (isMouseGestureField)
                    {
                        _settingsService.MarkDirty();
                        if (s is PadViewModel pvmMouse)
                        {
                            // Sync first (PropertyChanged fires before the
                            // setter's tail-call Sync), then rebuild the
                            // Mappings-tab picker so newly-checked gesture
                            // buttons' entries appear (and unchecked ones
                            // disappear) immediately.
                            pvmMouse.SyncMouseGestureSettingsToActiveDevice();
                            _inputService?.RefreshAvailableInputsForSlot(pvmMouse);
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
                        nameof(PadViewModel.GyroAimEngageMode) or nameof(PadViewModel.GyroEngageStickSide) or
                        nameof(PadViewModel.GyroEngageStickDirection) or
                        nameof(PadViewModel.IrSensorBarPos) or nameof(PadViewModel.IrSensorBarCompPercent) or
                        nameof(PadViewModel.IrSmoothingPercent) or
                        nameof(PadViewModel.PointerMode) or nameof(PadViewModel.PointerFpsSpeed) or
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
                        nameof(PadViewModel.SteeringLockLightbarFadeMs) or
                        // Motion Lean steering tunables. Without these, edits
                        // to only this card never dirtied settings and were
                        // lost on exit (audit lens 1m, F2).
                        nameof(PadViewModel.MotionSteerInnerDz) or
                        nameof(PadViewModel.MotionSteerOuterDz) or
                        nameof(PadViewModel.MotionSteerOrientIndex) or
                        // Flick Stick card tunables (#225), same per-(device,
                        // slot) extended-mapping persistence as Motion Lean.
                        nameof(PadViewModel.FlickCountsPer360) or
                        nameof(PadViewModel.FlickTime) or
                        nameof(PadViewModel.FlickThreshold) or
                        nameof(PadViewModel.FlickSnapMode) or
                        nameof(PadViewModel.FlickSnapStrength) or
                        nameof(PadViewModel.FlickForwardDeadzone) or
                        nameof(PadViewModel.FlickSmoothing) or
                        nameof(PadViewModel.FlickOnEngage))
                    {
                        _settingsService.MarkDirty();
                    }
                };

                // Extended custom stick/trigger config changes (indices 2+) trigger autosave.
                pad.ConfigItemDirtyCallback = () => _settingsService.MarkDirty();

                // Structural menu edits (add / remove / duplicate, kind,
                // cell count, center, enabled) change which "Menu N Item K"
                // descriptors exist, so the slot's input pickers rebuild.
                // Label / name typing does not fire this.
                pad.MenusStructureChanged = () => _inputService?.RefreshAvailableInputsForSlot(capturedPad);

                // Steering-mode change (incl. Reset all) re-stamps the engine MappingSets now
                // so the stick stops/starts steering immediately, not on the 2s autosave.
                pad.SteeringModeChangedCallback = () => _settingsService.PushUiExtraSourcesIntoSlotMappingSets();

                // ExtendedConfig property changes (preset, counts) trigger autosave.
                pad.ExtendedConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();

                // KbmConfig property changes (SOCD mode / pairs) trigger autosave.
                pad.KbmConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();

                // MidiConfig property changes (channel, velocity, CC/note ranges)
                // trigger autosave. The PadPage MIDI fields write straight into
                // this nested object, which raises PropertyChanged on itself and
                // not on the PadViewModel, so without this anchor a MIDI-only
                // edit never marked the file dirty and close discarded it.
                pad.MidiConfig.PropertyChanged += (s, e) => _settingsService.MarkDirty();

                // DeviceConfig changes (Lighting tab, Adaptive Triggers tab)
                // — autosave + sync audio capture when audio-to-lightbar
                // toggles. Audio-to-lightbar reuses the same WASAPI capture
                // as audio-rumble, so the capture lifecycle gates on either
                // feature being on for any created slot.
                // Forwarded event follows the per-device DeviceConfig
                // anchor across SelectedMappedDevice swaps. Subscribing
                // to pad.ActiveDeviceConfigPropertyChanged instead
                // of pad.DeviceConfig.PropertyChanged means edits
                // on whichever device the user has selected route here.
                pad.ActiveDeviceConfigPropertyChanged += (s, e) =>
                {
                    _settingsService.MarkDirty();
                    if (e.PropertyName == nameof(ViewModels.DeviceSlotConfig.AudioLightbarEnabled))
                        _inputService.SyncAudioBassDetector();
                    // Guide Button LED (#209): apply mode / brightness
                    // edits immediately instead of waiting for the 30 s
                    // slow lane. The writers change-detect, so dragging
                    // the slider costs one device write per new value.
                    if (e.PropertyName == nameof(ViewModels.DeviceSlotConfig.GuideLedMode)
                        || e.PropertyName == nameof(ViewModels.DeviceSlotConfig.GuideLedBrightness))
                        _inputService.ApplyGuideLeds();
                };
            }

            // Recorder completion marks settings dirty + clear flash + advance Map All.
            _recorderService.RecordingCompleted += (s, result) =>
            {
                _settingsService.MarkDirty();
                var activePad = _viewModel.SelectedPad;
                if (activePad == null) return;

                // #111 audit fix B (backstop). If a different mapping's recording
                // completed while a kind recording was pending on another row, clear
                // that orphaned state so its Record button leaves Stop. Skipped during
                // the kind's own Up->Down sequence, where result.Mapping is the kind row.
                if (_kindRecordMapping != null && !ReferenceEquals(result.Mapping, _kindRecordMapping))
                {
                    _kindRecordMapping.IsRecording = false;
                    _kindRecordMapping = null;
                    _kindRecordStage = KindRecStage.None;
                }

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
                    // A Param recording (Up / Down / Modifier) only updated a field on
                    // an existing source. That source is an extra, or the row's primary
                    // kind holder which is NOT in ExtraSources. Adding it here would
                    // spawn a stray secondary row (#111). Only new-source paths append.
                    if (!result.IsParamRecording
                        && parent != null && !parent.ExtraSources.Contains(result.ExtraSource))
                        parent.ExtraSources.Add(result.ExtraSource);  // fires EnsureCombineModeDefault + WireExtraSource via CollectionChanged
                    CommitRecordedMappingSet();

                    // #111 single-button kind recording. After the Up key lands,
                    // auto-advance to capture Down with the same Record button so
                    // the user never juggles separate record controls.
                    if (result.IsParamRecording && _kindRecordMapping != null
                        && ReferenceEquals(_kindRecordMapping, parent))
                    {
                        if (_kindRecordStage == KindRecStage.Up)
                        {
                            _kindRecordStage = KindRecStage.Down;
                            _recorderService.StartRecordingExtraSourceParam(
                                parent, result.ExtraSource, activePad.PadIndex,
                                RecorderService.ParamTarget.Down);
                            if (_recorderService.IsRecording)
                            {
                                activePad.CurrentRecordingTarget = parent.TargetSettingName;
                                _viewModel.SetStatus(string.Format(
                                    Strings.Instance.Status_RecordKindDown_Format, parent.TargetLabel), persist: true);
                                return; // keep recording; Down phase is live
                            }
                        }
                        // Down captured (or single-shot modifier, or Down failed to
                        // start): the sequence is done. Drop the row's Stop state.
                        _kindRecordStage = KindRecStage.None;
                        _kindRecordMapping.IsRecording = false;
                        _kindRecordMapping = null;
                    }

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
                        _viewModel.SetStatus(string.Format(Strings.Instance.Status_NowMap_Format, negMapping.TargetLabel, dirHint), persist: true);

                        // Switch to Controller tab so the 3D directional arrow is visible.
                        activePad.SelectedConfigTab = 0;

                        // Update recording target to pos for flash/arrow.
                        activePad.CurrentRecordingTarget = negMapping.TargetSettingName;

                        // Start recording — result will go to SourceDescriptor via normal path.
                        // Neutralize baseline so the previous POV/button press doesn't block detection.
                        // Mark the chain so the pos completion doesn't re-prompt
                        // for the neg that phase one just promoted into extras.
                        _savedPosDescriptor = null;
                        _negChainCompletedMapping = negMapping;
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
                bool negChainDone = ReferenceEquals(result.Mapping, _negChainCompletedMapping);
                _negChainCompletedMapping = null;
                var rgNormal = ResolveGuidFor(result.Mapping);
                if (rgNormal != Guid.Empty)
                    InputService.ResolveDisplayText(result.Mapping, rgNormal);
                result.Mapping.SyncSelectedInputFromDescriptor();

                // If a directional input (button, POV, slider) was recorded for a bidirectional axis,
                // auto-prompt for neg direction (but only if neg isn't already mapped — avoids
                // re-prompting after a neg-quadrant click that already auto-prompted for pos).
                if (result.Type != MapType.Axis && result.Mapping.HasNegDirection
                    && string.IsNullOrEmpty(result.Mapping.NegSourceDescriptor)
                    && !negChainDone)
                {
                    // Save the positive descriptor before the recorder overwrites it.
                    _savedPosDescriptor = result.Mapping.SourceDescriptor;
                    _pendingNegMapping = result.Mapping;

                    // Neg X = left, Neg Y = up (Y inverted by NegateAxis in Step 3).
                    bool isXAxis2 = result.Mapping.TargetSettingName.Contains("AxisX")
                        || result.Mapping.TargetLabel.EndsWith(" X", StringComparison.Ordinal);
                    string dirHint = isXAxis2 ? Strings.Instance.Status_DirectionLeft : Strings.Instance.Status_DirectionUp;
                    _viewModel.SetStatus(string.Format(Strings.Instance.Status_NowMap_Format, result.Mapping.TargetLabel, dirHint), persist: true);

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
                _negChainCompletedMapping = null;

                // #111 kind recording (Ramp / Incremental / Invert-On-Hold):
                // the row's IsRecording is set by hand at the start site
                // because RecorderService only marks the param extraSource,
                // and CancelRecording deliberately leaves the mapping alone
                // when an extraSource is active. Without this, a timed-out
                // kind record left the row's button stuck showing Stop and
                // the stale mapping latched until an unrelated recording
                // completed (round 34).
                if (_kindRecordMapping != null)
                {
                    _kindRecordMapping.IsRecording = false;
                    _kindRecordMapping = null;
                    _kindRecordStage = KindRecStage.None;
                }

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
                    // Haptic-mirror engage record button (#185).
                    if (activePad.MirrorEngageRecording)
                        activePad.MirrorEngageRecording = false;
                    // Mouse-gesture custom activation record button (#216).
                    if (activePad.MouseGestureCustomEngageRecording)
                        activePad.MouseGestureCustomEngageRecording = false;
                    // Menus tab host / steer / click recording (#9). Its
                    // freeform completion callback is what normally clears
                    // this, and CancelRecording drops that callback without
                    // invoking it, so a timeout left the button stuck at Stop
                    // and consumed the user's next click as a cancel
                    // (round 34).
                    if (activePad.SelectedMenu != null && activePad.SelectedMenu.HostRecording)
                        activePad.SelectedMenu.HostRecording = false;
                }
            };

            // Wire click-to-record from controller visual elements.
            PadPageView.ControllerElementRecordRequested += (s, targetName) =>
            {
                var padVm = _viewModel.SelectedPad;
                if (padVm == null) return;

                // Any new click abandons a half-finished quadrant chain.
                _negChainCompletedMapping = null;

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
                    _negChainCompletedMapping = null;
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
                pad.CopyMacroRequested += (s, e) => OnCopyMacro(capturedPad);
                pad.PasteMacroRequested += (s, e) => OnPasteMacro(capturedPad);
                pad.CopyMacroFromRequested += (s, e) => OnCopyMacroFrom(capturedPad);
            }

            // Build the sidebar navigation items dynamically.
            BuildNavigationItems();

            // Wire Dashboard "Add Controller" to show type-selection popup.
            DashboardPageView.AddControllerRequested += (s, e) =>
            {
                ShowControllerTypePopup(DashboardPageView.AddControllerCardElement, PlacementMode.Bottom);
            };

            // Wire the active-profile pill (#175 item 8): click opens the
            // switcher flyout above the status bar, and an applied
            // auto-switch flares it. The status bar is the pill's only
            // home. The pad-page copy read as a per-pad profile selector
            // (user report 2026-07-05), but profiles are global.
            StatusProfilePill.Clicked += (s, e) =>
                ShowProfileSwitcherPopup(StatusProfilePill, PlacementMode.Top);
            _inputService.AutoProfileSwitchApplied += () =>
                StatusProfilePill.Flare();

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
                    _viewModel.SetStatus(Strings.Instance.Status_StoppingEngine, persist: true);
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
                // Order is load-bearing (2026-07-22 automap-loss root
                // cause): ReAutoMapSlot authors the per-device legacy
                // PadSettings, and the MERGE must fold them into the
                // slot MappingSet BEFORE OutputType is set, because the
                // setter's RebuildMappings rebuilds the grid view from
                // the set, and the save pipeline REGENERATES settings
                // from that view. With the old order (type first, merge
                // after) the view rebuilt from the pre-merge set showed
                // every new-type target as a sourceless skeleton, and
                // the next view-driven save persisted that skeleton over
                // the merged truth and regenerated the SELECTED device's
                // PadSetting raw-less. Multi-controller Nintendo
                // switches lost their automaps exactly this way.
                SettingsManager.ReAutoMapSlot(args.SlotIndex, args.Type);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[args.SlotIndex].OutputType = args.Type;
                _inputService.MoveSlotToGroupTail(args.SlotIndex);
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

            // Only now does RemoteLink.IdentityProtection hold the persisted
            // choice, so this is the earliest point the dropdown can show it.
            _inputService?.SeedIdentityProtectionDisplay();

            // Load profile shortcuts after settings are loaded.
            LoadProfileShortcuts();

            // First-run legacy driver cleanup (v2 → v3 upgrade path).
            Dispatcher.BeginInvoke(new Action(MaybeOfferLegacyDriverCleanup),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // Restore main window position/size/state.
            var mw = _viewModel.Settings;
            // (-1, -1) is the legacy "never saved" sentinel (SettingsViewModel
            // defaults). ANY other value is a real saved position, negative
            // coordinates included: a monitor left of or above the primary has
            // negative virtual-desktop coordinates, and the old "both >= 0"
            // gate silently discarded those users' position on every launch
            // while still restoring size (round 34). Requiring the restored
            // rect to intersect the CURRENT virtual screen also stops a
            // position saved on a since-removed monitor from stranding the
            // window offscreen, which the old gate could not catch either.
            if (mw.MainWindowLeft != -1 || mw.MainWindowTop != -1)
            {
                double rw = mw.MainWindowWidth > 0 ? mw.MainWindowWidth : Width;
                double rh = mw.MainWindowHeight > 0 ? mw.MainWindowHeight : Height;
                var saved = new Rect(mw.MainWindowLeft, mw.MainWindowTop, rw, rh);
                var desktop = new Rect(
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (saved.IntersectsWith(desktop))
                {
                    Left = mw.MainWindowLeft;
                    Top = mw.MainWindowTop;
                }
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

            // Status decay (#175 item 7): every StatusText write cancels any
            // fade in progress and restores full opacity, so fresh text never
            // inherits a dimmed or mid-animation state.
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusText))
                {
                    StatusMessageText.BeginAnimation(UIElement.OpacityProperty, null);
                    StatusMessageText.Opacity = 1.0;
                }
                // The nav flames read engine-level state (IsEngineRunning
                // and the idle-following HasActiveSlots) but re-render only
                // on per-slot property changes, so an engine idle/stop
                // transition alone left every flame stale at its last heat
                // (part of the owner's 2026-07-24 idle-vs-forging
                // contradiction). Re-render the rail on those transitions.
                else if (e.PropertyName is nameof(MainViewModel.HasActiveSlots)
                         or nameof(MainViewModel.IsEngineRunning))
                {
                    RefreshAllControllerNavItems();
                }
            };

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
                // Status decay (#175 item 7) rides this always-on 5 s lane
                // (started in the constructor, never stopped) instead of a
                // timer of its own. The engine's 30 Hz UI timer would be
                // the wrong host: it dies with the engine, and "Engine
                // stopped." would burn in exactly like the bug being fixed.
                SweepStatusMessage();
            };
            _driverStatusTimer.Start();
        }

        /// <summary>
        /// Clears an info-class status bar message once it has outlived its
        /// ~5 s decay window (#175 item 7). Persistent messages (failures,
        /// active prompts) are left alone until the next write. With OS
        /// animations on, a short opacity fade runs first and the clear
        /// happens on its Completed callback, which re-checks eligibility
        /// so a write landing mid-fade survives at full opacity.
        /// </summary>
        private void SweepStatusMessage()
        {
            if (!_viewModel.IsStatusDecayDue) return;
            if (!MotionEnabled)
            {
                _viewModel.ClearDecayedStatus();
                return;
            }
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                1.0, 0.0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (s, ev) =>
            {
                _viewModel.ClearDecayedStatus();  // no-op if restamped mid-fade
                StatusMessageText.BeginAnimation(UIElement.OpacityProperty, null);
                StatusMessageText.Opacity = 1.0;
            };
            StatusMessageText.BeginAnimation(UIElement.OpacityProperty, fade);
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

            // Ambient-motion gate: the always-on breathes (rail heat ring,
            // dashboard/profiles auras, selection pulses) pay WPF's fixed
            // per-animation-frame pipeline cost (~18% of a core per breathe
            // at 60fps, measured in isolation 2026-07-16) even when the app
            // is buried behind a game. Foreground state flips the probe; the
            // XAML breathes react through their trigger conditions and the
            // code-built rail ring re-evaluates via the section rebuild.
            // Every effect stays exactly as designed whenever the app is
            // visible and focused.
            Activated += (_, _) => SetAmbientMotion(true);
            Deactivated += (_, _) => SetAmbientMotion(false);
            StateChanged += (_, _) =>
            {
                bool minimized = WindowState == WindowState.Minimized;
                // Code-side rate gates (dashboard publisher) throttle harder
                // when nothing can render at all vs merely unfocused.
                PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized = minimized;
                if (minimized) SetAmbientMotion(false);
                else if (IsActive) SetAmbientMotion(true);
            };

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
                _viewModel.SetStatus(Strings.Instance.Status_SDL3NotFoundDetail, persist: true);
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

            // Commit an in-progress TextBox edit before the dirty check:
            // closing from the title bar never moves focus, so an
            // UpdateSourceTrigger=LostFocus Text binding still holds the
            // typed value and the save below would drop it (the
            // DevicesPage.IdleDisconnect_LostFocus force-commit pattern).
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox focusedTb)
                focusedTb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            // Save settings synchronously (fast, UI-bound data).
            if (_settingsService.IsDirty)
                _settingsService.Save();

            // Stop driver status polling.
            _driverStatusTimer?.Stop();
            _driverStatusTimer = null;

            // Cancel any in-flight Workshop update check.
            _workshopUpdateCts?.Cancel();

            // Stop the SDL event pump BEFORE the disposal Task.Run below reaches
            // SDL_Quit: the 100ms pump fires SDL_PumpEvents/SDL_UpdateJoysticks on
            // the UI thread, and left running it races SDL teardown on the worker
            // thread (undefined SDL concurrency -> intermittent crash-on-exit).
            _sdlPumpTimer?.Stop();
            _sdlPumpTimer = null;

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
        private const string SwitchSvgPath = Common.ControllerIcons.SwitchSvgPath;
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

            // ItemInvoked is already wired once with the other NavigationView
            // events during construction. Subscribing again here made the
            // handler run TWICE per nav click, so NavigateToTag and its
            // mapping-grid rehydration both ran twice.

            NavView.MenuItems.Clear();

            // Rail icons wear the same ember their page headers do; the
            // DynamicResource reference keeps them tracking theme swaps.
            static FontIcon EmberNavIcon(string glyph)
            {
                var icon = new FontIcon
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Glyph = glyph,
                };
                icon.SetResourceReference(FontIcon.ForegroundProperty, "EmberBrush");
                return icon;
            }

            // Dashboard.
            _navDashboard = new NavigationViewItem
            {
                Content = Strings.Instance.Dashboard_Title,
                Tag = "Dashboard",
                Icon = EmberNavIcon("\uF404")
            };
            NavView.MenuItems.Add(_navDashboard);

            // Profiles.
            _navProfiles = new NavigationViewItem
            {
                Tag = "Profiles",
                Icon = EmberNavIcon("\uE8F1"),
                Content = Strings.Instance.Profiles_Title
            };
            NavView.MenuItems.Add(_navProfiles);

            // Devices.
            _navDevices = new NavigationViewItem
            {
                Content = Strings.Instance.Devices_Title,
                Tag = "Devices",
                Icon = EmberNavIcon("\uE772")
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
                            // Collapsed rail: the mini card is a rendered
                            // bitmap, so its flame heat only tracks state
                            // if the icon re-renders here too.
                            if (!NavView.IsPaneOpen)
                            {
                                capturedMenuItem.Icon = RenderCompactCardIcon(capturedNavItem);
                                SyncCollapsedIconSelection(capturedMenuItem);
                            }
                        }
                    };
                    navItem.PropertyChanged += handler;
                    _navItemHandlers.Add((navItem, handler));
                }

                // "Add Controller" entry (visible if any controller type has remaining capacity).
                // Minified twin of the Dashboard's Add Controller card; collapses to a bare "+".
                if (HasAnyControllerTypeCapacity())
                {
                    NavView.MenuItems.Add(BuildAddControllerNavItem());
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
            SuppressBuiltInSelectionVisual(menuItem);
            AttachCollapsedIconHover(menuItem);
            System.Windows.Automation.AutomationProperties.SetName(menuItem, navItem.Tag);
            UpdateControllerNavItemContent(menuItem, navItem);
            if (collapsed)
            {
                menuItem.Icon = RenderCompactCardIcon(navItem);
                SyncCollapsedIconSelection(menuItem);
            }
            return menuItem;
        }

        /// <summary>Heat state for a slot's flame (#175), shared by the
        /// expanded pill and the collapsed mini card so both read the
        /// same liveness: ember = engine running + VC live (or igniting),
        /// gold = enabled but engine stopped / awaiting devices, cold
        /// outline = slot disabled.</summary>
        private void ComputeFlameHeat(NavControllerItemViewModel navItem, out bool lit, out bool cooling)
        {
            if (!navItem.IsEnabled) { lit = false; cooling = false; }
            else if (navItem.IsInitializing) { lit = true; cooling = false; }
            // Nothing mapped reads COLD (outline flame, steel rim), matching the
            // Dashboard card's HasMappedDevices==false state. Without this, an empty
            // slot and a slot awaiting its assigned devices both looked gold/cooling.
            else if (navItem.MappedDeviceCount == 0) { lit = false; cooling = false; }
            else if (!_viewModel.IsEngineRunning) { lit = false; cooling = true; }
            else if (!navItem.IsVirtualControllerConnected) { lit = false; cooling = true; }
            // Engine idle with the VC parked (inactivity timeout 0 = never
            // destroy, all devices offline): idle is not forging. Gold,
            // agreeing with the instrument line's Idle instead of
            // contradicting it (owner repro 2026-07-24). Ordered after the
            // VC check so the ordinary awaiting state keeps its own gold.
            // With the timeout armed this state is transient: the engine
            // stays awake through the grace and the teardown lands here as
            // the not-connected branch above.
            else if (!_viewModel.HasActiveSlots) { lit = false; cooling = true; }
            else { lit = true; cooling = false; }
        }

        /// <summary>
        /// Builds the flame glyph for a slot's heat state (#175).
        /// lit: ember fill. cooling: gold fill. cold: outline only.
        /// Geometry comes from the shared FlameOuterGeometry resource
        /// (MDI "fire", with its own inner-tongue cutout).
        /// </summary>
        private static System.Windows.Controls.Grid BuildFlameGlyph(double size, bool lit, bool cooling)
        {
            var grid = new System.Windows.Controls.Grid { Width = size, Height = size };
            var flame = new System.Windows.Shapes.Path
            {
                Data = (System.Windows.Media.Geometry)Application.Current.Resources["FlameOuterGeometry"],
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            if (lit)
            {
                flame.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C));
                // Ember bloom (#175 glow sweep): static effect on the lit
                // flame, small-glyph radius. Never animated. The card ring
                // breathe stays the only animated effect.
                flame.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.5
                };
            }
            else if (cooling)
            {
                flame.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xB4, 0x34));
                // Faint gold bloom for the cooling flame.
                flame.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Color.FromRgb(0xE8, 0xB4, 0x34),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.35
                };
            }
            else
            {
                flame.Fill = System.Windows.Media.Brushes.Transparent;
                flame.StrokeThickness = 1.1;
                flame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorTertiaryBrush");
            }
            grid.Children.Add(flame);
            return grid;
        }

        /// <summary>Reduced motion (#175 item 98): one gate for every
        /// code-driven Forever animation in this window. Mirrors the OS
        /// animation preference. When false, callers hold a static effect
        /// at mid opacity instead of starting the loop.</summary>
        private static bool MotionEnabled => System.Windows.SystemParameters.ClientAreaAnimation;

        /// <summary>Carries the mini-card delete X from the row builder to
        /// the card-level hover wiring inside one
        /// UpdateControllerNavItemContent pass (single-threaded UI build).</summary>
        private Wpf.Ui.Controls.Button _pendingSidebarDeleteBtn;

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
            bool isNintendo = iconKey == "NintendoControllerIcon";
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
            var deleteBtn = new Wpf.Ui.Controls.Button
            {
                Content = deleteIcon,
                ToolTip = Strings.Instance.Main_DeleteVC,
                Tag = navItem.PadIndex,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                // Compact footprint (#175 clip report 2026-07-06): the
                // style's 28px box plus this 6px margin costs 34px of dock
                // width vs the 21px of the plain X it replaced (9px glyph +
                // 3px padding each side + 6px margin), and the row only has
                // ~193px: 223 pane + 40 item-margin reclaim - 40 icon column
                // - 2 item border - 6 card margin - 22 card chrome. Budget
                // holds at the two-digit worst case (slot "16" and "#16",
                // both reachable at 16 slots): 19 power + 18 slot# + 120.3
                // seg row (2 margin + 5x19 tiles + 4 label margin + 19.3 for
                // "#16", Cascadia @11 measured) + 28 X = 185.3, ~7.7 slack.
                // 22px + Padding 2 keeps the shared chrome (local values
                // beat style setters); the X never shrinks below 22.
                Width = 22,
                Height = 22,
                Padding = new Thickness(2),
                // Hover-revealed (#175): the rail reads as state, actions
                // appear on intent. Revealed at full strength on CARD hover
                // (user report 2026-07-06: the row-level reveal engaged
                // sporadically and the chrome did not match the Devices
                // row X). EmberIconButtonHot is the shared X treatment.
                Opacity = 0
            };
            deleteBtn.SetResourceReference(FrameworkElement.StyleProperty, "EmberIconButtonHot");
            deleteBtn.Click += OnSidebarDeleteSlot;
            System.Windows.Controls.DockPanel.SetDock(deleteBtn, System.Windows.Controls.Dock.Right);
            row.Children.Add(deleteBtn);
            // Reveal rides the whole CARD's hover (wired below, once the
            // card exists) so the X cannot flicker while the pointer moves
            // between the card's child panels.
            _pendingSidebarDeleteBtn = deleteBtn;

            // Flame power toggle (#175): heat encodes liveness.
            // Ember = forging (enabled, engine running, VC live).
            // Gold = cooling (enabled but engine stopped / awaiting devices).
            // Outline = cold (disabled). Flashing ember = igniting.
            // Heat comes from the shared helper so the collapsed mini
            // card's flame can never diverge from the expanded pill's.
            string powerTooltip;
            bool isInitializing = navItem.IsInitializing;
            ComputeFlameHeat(navItem, out bool lit, out bool cooling);
            if (!navItem.IsEnabled)
            {
                powerTooltip = Strings.Instance.Common_Disabled;
                isInitializing = false;
            }
            else if (isInitializing)
            {
                powerTooltip = Strings.Instance.Main_Initializing;
            }
            else if (navItem.MappedDeviceCount == 0)
            {
                // Mirror ComputeFlameHeat's nothing-mapped branch, which the
                // tooltip chain lacked: an EMPTY slot showed the cold outline
                // flame while its tooltip read "Awaiting devices", claiming it
                // was waiting for devices it does not have (round 34). The
                // flame's own comment says these two states must not look
                // alike, and the text has to agree with it.
                powerTooltip = Strings.Instance.Dashboard_NoDevice;
                isInitializing = false;
            }
            else if (!_viewModel.IsEngineRunning)
            {
                powerTooltip = Strings.Instance.Main_EngineStopped;
            }
            else if (navItem.IsCreateFailed)
            {
                // The truth outranks the awaiting-devices default: a failed
                // create with online devices is not waiting for anything.
                powerTooltip = Strings.Instance.Main_VcFailed;
            }
            else if (!navItem.IsVirtualControllerConnected)
            {
                // Gold reflects "no live VC" (slot has never created a VC, or
                // its VC was torn down by the HM-inactivity timeout). During
                // the grace period the VC is still alive even with devices
                // offline, so the flame stays ember until teardown.
                powerTooltip = Strings.Instance.Main_AwaitingDevices;
            }
            else if (!_viewModel.HasActiveSlots)
            {
                // Engine idle with the VC parked (timeout 0, devices
                // offline): idle, not forging, matching ComputeFlameHeat's
                // parked branch and the instrument line.
                powerTooltip = Strings.Instance.Common_Idle;
            }
            else
            {
                powerTooltip = Strings.Instance.Main_Active;
            }

            var flameGlyph = BuildFlameGlyph(13, lit, cooling);

            // Apply flashing opacity animation when igniting. Reduced motion
            // (#175 item 98, SystemParameters.ClientAreaAnimation): no flash
            // loop, the flame holds steady ember.
            if (isInitializing && MotionEnabled)
            {
                var flashAnimation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0,
                    To = 0.15,
                    Duration = new Duration(TimeSpan.FromMilliseconds(500)),
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                flameGlyph.BeginAnimation(System.Windows.UIElement.OpacityProperty, flashAnimation);
            }

            var powerBtn = new System.Windows.Controls.Button
            {
                Content = flameGlyph,
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

            // Global slot number. The pill reads "what this slot IS" (#175):
            // flame, number, type + instance. Type switching lives on the
            // dashboard cards and the Pad page where there is room for it.
            var slotNumber = new System.Windows.Controls.TextBlock
            {
                Text = $"{navItem.SlotNumber}",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("TelemetryFontFamily"),
                FontSize = 12,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                // Tight (#175 clip report): interior slack keeps the
                // right-aligned type segment from clipping its left edge.
                // The 16px box holds two digits: "16" measures 14.1 in
                // Cascadia @12, so slot 10-16 fits without widening.
                Margin = new Thickness(2, 0, 0, 0),
                Width = 16,
                TextAlignment = TextAlignment.Center
            };
            slotNumber.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            row.Children.Add(slotNumber);

            // Type switcher returns to the rail (user direction, iteration 15):
            // the dashboard segment in mini form. Active type is ember-filled;
            // the card rebuilds on OutputType change via RefreshNavControllerItems.
            // Read the cached status, not the live probe. This method runs once
            // per rail card, and DriverInstaller.IsMidiServicesInstalled()
            // enumerates the whole HKLM uninstall key under BOTH the 64-bit and
            // 32-bit registry views, so a 16-slot rail rebuild paid 32 hive
            // walks on the UI thread. RefreshMidiServicesStatus() keeps this
            // property current on a 5 s timer and runs in the constructor
            // before the rail's first build, so the value here is the same
            // answer at worst five seconds older.
            bool hasMidi = _viewModel.Settings.IsMidiServicesInstalled;
            var segRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            System.Windows.Controls.Button MakeTypeButton(UIElement content, bool active, System.Windows.RoutedEventHandler click, string tip, bool enabled)
            {
                var b = new System.Windows.Controls.Button
                {
                    Content = content,
                    // 3px sides (#175 two-digit fit): at 4px the worst case
                    // (slot "16" plus "#16") ran the row to 195.3 vs the
                    // ~193 budget and the right-aligned segment shaved the
                    // Xbox button's left edge again. 3px lands the five
                    // tiles at 19px each (11px glyph untouched, still above
                    // the 17px legibility floor) and the row at 185.3.
                    Padding = new Thickness(3, 2, 3, 2),
                    MinWidth = 0,
                    MinHeight = 0,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 0, 2, 0),
                    ToolTip = tip,
                    Tag = navItem.PadIndex,
                    IsEnabled = enabled,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                b.SetResourceReference(FrameworkElement.StyleProperty,
                    active ? "MiniTypeButtonActiveStyle" : "MiniTypeButtonStyle");
                if (active)
                    b.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, "EmberSegGradient");
                else
                    b.Background = System.Windows.Media.Brushes.Transparent;
                b.Click += click;
                return b;
            }

            UIElement TypeLogo(string data, bool active)
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse(data),
                    Width = 11,
                    Height = 11,
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
                if (active)
                    path.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF3, 0xE8));
                else
                    path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorTertiaryBrush");
                return path;
            }

            UIElement TypeGlyph(string glyph, bool active)
            {
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 11
                };
                if (active)
                    tb.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF3, 0xE8));
                else
                    tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                return tb;
            }

            segRow.Children.Add(MakeTypeButton(TypeLogo(XboxSvgPath, isXbox), isXbox, OnSidebarTypeXbox, Strings.Instance.ControllerType_Xbox, true));
            segRow.Children.Add(MakeTypeButton(TypeLogo(DS4SvgPath, isPlayStation), isPlayStation, OnSidebarTypePlayStation, Strings.Instance.ControllerType_PlayStation, true));
            segRow.Children.Add(MakeTypeButton(TypeLogo(SwitchSvgPath, isNintendo), isNintendo, OnSidebarTypeNintendo, Strings.Instance.ControllerType_Nintendo, true));
            segRow.Children.Add(MakeTypeButton(TypeLogo(ExtendedSvgPath, isExtended), isExtended, OnSidebarTypeExtended, Strings.Instance.ControllerType_Extended, true));
            segRow.Children.Add(MakeTypeButton(TypeGlyph("\uE961", isKbm), isKbm, OnSidebarTypeKeyboardMouse, Strings.Instance.ControllerType_KeyboardMouse, true));
            segRow.Children.Add(MakeTypeButton(TypeGlyph("\uE8D6", isMidi), isMidi, OnSidebarTypeMidi, hasMidi ? Strings.Instance.ControllerType_MIDI : Strings.Instance.Main_MIDI_RequiresMidiServices, hasMidi || isMidi));
            var instanceLabel = new System.Windows.Controls.TextBlock
            {
                // "#{n}" to match the slot card seg (#175 iter 71); guard in
                // case InstanceLabel ever already carries the prefix.
                Text = string.IsNullOrEmpty(navItem.InstanceLabel) || navItem.InstanceLabel.Contains('#')
                    ? navItem.InstanceLabel
                    : "#" + navItem.InstanceLabel,
                // Telemetry mono (#175 font sweep): the "#N" token matches
                // the dashboard seg's instance label face.
                FontFamily = (System.Windows.Media.FontFamily)FindResource("TelemetryFontFamily"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                // Fixed box (user report 2026-07-06): the segment is
                // right-anchored, so an auto-width token slid the five type
                // tiles left when the instance number gained a digit
                // (stacked cards' icons no longer lined up). 20 holds the
                // worst case ("#16" = 19.3, Cascadia @11, iteration 107
                // math) and left alignment anchors the "#" column too.
                Width = 20,
                TextAlignment = TextAlignment.Left
            };
            instanceLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            segRow.Children.Add(instanceLabel);

            row.Children.Add(segRow);

            // Flat host (#175 iteration 2): a boxed card inside a rail of
            // flat text items read as a foreign object on real pixels.
            // The Border stays only as the drag/drop + click surface; heat
            // lives in the flame color alone.
            var card = new System.Windows.Controls.Border
            {
                // Pill geometry (#175 pitch .slotpill): radius 10, 10/6 padding.
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(1),
                // Fixed width, not MinWidth (user 2026-07-06): stacked cards
                // must read congruent whatever their digit count. 233 =
                // worst-case row 204.34 (slot 16 + "#16" + the SIX-tile
                // type segment, Nintendo added 2026-07-19; iteration 107
                // math + 19) + 20 padding + 2 border + 6.7 headroom,
                // inside the 236 outer cap the widened 244 pane allows.
                Width = 233,
                Child = row,
                Tag = navItem.PadIndex,
                // Glow clearance (#175 clip report): the rail clips at the
                // pill's bounds, truncating the heat ring left and right.
                // 3px, not more: every horizontal pixel here narrows the
                // type segment's dock area, which clips the Xbox button's
                // left edge when the row runs out of slack.
                Margin = new Thickness(3, 2, 3, 2)
            };
            card.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

            // Heat ring (#175 artifact): live slots carry an ember rim and a
            // breathing glow so the rail doubles as an engine monitor.
            if (lit)
            {
                card.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0x6B, 0x2C));
                var ring = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C),
                    BlurRadius = 20,
                    ShadowDepth = 0,
                    Opacity = 0.25,
                };
                card.Effect = ring;
                // Reduced motion (#175 item 98): breathe swaps for a static
                // glow pinned near the spec's rgba(...,0.22) point of the
                // 0.25-0.60 breathe range. The ambient gate swaps the same
                // way while the app is backgrounded (see SetAmbientMotion):
                // a static ring is visually the breathe's midpoint, and the
                // rebuild on re-activation restarts the phase-locked loop.
                if (MotionEnabled && PadForge.Common.AmbientMotionProbe.Instance.IsAppActive)
                {
                    var breathe = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0.25,
                        To = 0.60,
                        Duration = System.TimeSpan.FromSeconds(1.6),
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                        EasingFunction = new System.Windows.Media.Animation.SineEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                        // Phase-lock to a global 3.2s clock: card rebuilds restart
                        // the animation, and without this the glow visibly jumps
                        // to cycle start each time.
                        BeginTime = System.TimeSpan.FromMilliseconds(
                            -(System.DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 3200.0)),
                    };
                    // 30fps ambient rate. Every animation frame pays WPF's
                    // fixed pipeline cost (measured 2026-07-16: ~18% of a
                    // core per permanent breathe at the default 60fps,
                    // independent of caching or which property animates), so
                    // halving the rate halves that budget. A 1.6s ease-in-out
                    // sine on a soft blur has no spatial motion; 30 samples a
                    // second is beyond what the fade can show. This is a
                    // considered rate for ambient loops, not the earlier
                    // blanket 15fps cap that stood in for unfixed mechanisms.
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(breathe, 30);
                    ring.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, breathe);
                }
                else
                {
                    ring.Opacity = 0.40;
                }
            }
            else if (cooling)
            {
                card.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x2E, 0xFF, 0x6B, 0x2C));
                // Awaiting devices (mapped but not live): a static faint ember bloom,
                // matching the Dashboard awaiting card. No breathe. The breathe is the
                // live state's tell.
                card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C),
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.16,
                };
            }
            else
            {
                card.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            }

            // Cache placement follows WPF's rule that a CacheMode on the SAME
            // node as an Effect is ignored for the effect path (learned the
            // hard way twice tonight, profiled both times). Effect-bearing
            // pills (lit ring, cooling bloom) cache their CONTENT, so the
            // animated ring's per-frame ApplyEffect re-renders "chrome +
            // one texture blit" instead of re-tessellating every glyph in
            // the row. Effect-free pills cache the whole card, chrome
            // included, so a neighbor's blur pad recomposes them as blits.
            // Cards rebuild wholesale on any content change, so staleness is
            // impossible; the hover-lift transform rides the card, which a
            // BitmapCache survives without invalidating.
            if (card.Effect != null)
                row.CacheMode = new System.Windows.Media.BitmapCache();
            else
                card.CacheMode = new System.Windows.Media.BitmapCache();

            // Selection = focus. The focused pill gets a bright pulsing ember/gold
            // rounded-rect border PLUS a wide pulsing bloom, color and pace tracking
            // the slot's activation: ember and quick when live, gold and slower when
            // idle, dim and slowest when disabled. Both are drawn by a PillGlowAdorner
            // in the window adorner layer, ABOVE the pane's scroll viewport, because
            // the viewport's ScrollContentPresenter clips its content (a drop shadow
            // on the card itself is truncated ~5px out, at the viewport edge). The
            // adorner is the same unclipped surface the reorder drag uses, so the
            // bloom renders in full. The card keeps its resting heat border underneath.
            if (IsControllerTagSelected(navItem.Tag))
            {
                var selColor = cooling
                    ? System.Windows.Media.Color.FromRgb(0xE8, 0xB4, 0x34)   // gold, idle
                    : System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C);  // ember, live/disabled
                // Drop the card's own Effect: a lit pill carries the breathing heat
                // ring here, which the scroll viewport clips and would read as a
                // clipped selection glow. The unclipped adorner is the sole bloom now.
                card.Effect = null;
                ShowSelectionGlowAdorner(card, selColor, lit, cooling);
            }

            if (!navItem.IsEnabled)
                card.Opacity = 0.6;
            else if (!lit && !cooling)
                card.Opacity = 0.72;   // cold: nothing mapped, the card recedes (Dashboard parity)

            // Hover engagement (#175): every mini card lifts 2px on hover and blooms
            // faint ember. The bloom rides the adorner layer (like the selection
            // bloom) so the pane scroll viewport does not clip it; the selected pill
            // is skipped since it already wears the stronger selection bloom.
            var cardLift = new System.Windows.Media.TranslateTransform();
            card.RenderTransform = cardLift;
            card.MouseEnter += (s, e) =>
            {
                var up = new System.Windows.Media.Animation.DoubleAnimation(-2, System.TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                cardLift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, up);
                if (!IsControllerTagSelected(navItem.Tag))
                    ShowHoverGlowAdorner(card);
            };
            card.MouseLeave += (s, e) =>
            {
                var down = new System.Windows.Media.Animation.DoubleAnimation(0, System.TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                cardLift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, down);
                RemoveHoverGlowAdorner();
            };

            // X reveal follows the card's own hover state (stable across
            // child-panel boundaries), matching the Devices row X.
            if (_pendingSidebarDeleteBtn is Wpf.Ui.Controls.Button cardDeleteBtn)
            {
                _pendingSidebarDeleteBtn = null;
                card.MouseEnter += (s, e) => cardDeleteBtn.Opacity = 1.0;
                card.MouseLeave += (s, e) => cardDeleteBtn.Opacity = 0;
            }

            // Drag reordering. Mouse-down recorded here, threshold + movement tracked at NavView level.
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

        /// <summary>Re-renders every controller nav card in its CURRENT pane
        /// mode. The flames read engine-level inputs (stopped, idle) that
        /// change without any per-slot property firing, so those transitions
        /// re-render here rather than leaving each flame stale at its last
        /// heat (the 2026-07-24 idle-vs-forging contradiction).</summary>
        private void RefreshAllControllerNavItems()
            => UpdateAllControllerCardMode(compact: !NavView.IsPaneOpen);

        /// <summary>
        /// Swaps all controller NavigationViewItem cards between full and compact mode.
        /// Compact mode shows a mini card: flame heat glyph + slot number on top row,
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
                            SyncCollapsedIconSelection(nvi);
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

            // The Add Controller entry rides the same crossfade: minified card
            // when expanded, bare "+" when collapsed.
            foreach (var mi in NavView.MenuItems)
                if (mi is NavigationViewItem addNvi && addNvi.Tag?.ToString() == "AddController")
                { UpdateAddControllerCardMode(addNvi, compact); break; }
        }

        /// <summary>True for nav items that participate in the pane
        /// collapse/expand opacity crossfade: the controller mini cards
        /// (Tag "Pad…") and the Add Controller entry.</summary>
        private static bool IsCardFadeItem(NavigationViewItem nvi)
        {
            var t = nvi.Tag?.ToString();
            return t != null && (t.StartsWith("Pad") || t == "AddController");
        }

        /// <summary>Builds the drawer "Add Controller" entry as a minified twin
        /// of the Dashboard's Add Controller card (dashed steel outline, muted
        /// "+" glyph + label). Expanded it wears the card; collapsed it
        /// crossfades down to a bare "+" symbol on the same 200/200/220 ms
        /// window as the controller mini cards. Every brush is DynamicResource,
        /// so both themes track live (no baked bitmap like the mini cards).</summary>
        private NavigationViewItem BuildAddControllerNavItem()
        {
            bool collapsed = !NavView.IsPaneOpen;
            var item = new NavigationViewItem
            {
                Tag = "AddController",
                Content = BuildAddControllerCard(),
                Margin = collapsed ? new Thickness(0) : new Thickness(-40, 0, 0, 0),
                Icon = collapsed ? MakeAddControllerCollapsedIcon() : null,
            };
            SuppressBuiltInSelectionVisual(item);
            AttachCollapsedIconHover(item);
            System.Windows.Automation.AutomationProperties.SetName(item, Strings.Instance.Main_AddController);
            return item;
        }

        /// <summary>Gives the COLLAPSED-rail icon of a mini-card entry the same
        /// subtle hover feedback the expanded pill has: a 2 px lift plus a faint
        /// ember bloom. The expanded card wires its own hover on the card Border
        /// (BuildFlameGlyph card), but when the pane is collapsed that Border is
        /// not shown, the bitmap/glyph Icon is, so this drives the Icon element
        /// directly. Gated to the collapsed state so it never double-lifts the
        /// expanded card. The Icon is rebuilt on every pane toggle, so the
        /// handlers resolve nvi.Icon at hover time rather than capturing it.</summary>
        private void AttachCollapsedIconHover(NavigationViewItem nvi)
        {
            var hoverGlow = TryFindResource("NeutralHoverGlow") as System.Windows.Media.Effects.Effect;
            nvi.MouseEnter += (s, e) =>
            {
                if (NavView.IsPaneOpen) return;
                if (nvi.Icon is not System.Windows.FrameworkElement icon) return;
                if (icon.RenderTransform is not System.Windows.Media.TranslateTransform lift)
                {
                    lift = new System.Windows.Media.TranslateTransform();
                    icon.RenderTransform = lift;
                }
                lift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(-2, System.TimeSpan.FromMilliseconds(130))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
                // A selected icon already wears the stronger selection glow, so
                // only unselected icons take the faint hover bloom.
                if (!IsControllerTagSelected(nvi.Tag?.ToString()) && hoverGlow != null)
                    icon.Effect = hoverGlow;
            };
            nvi.MouseLeave += (s, e) =>
            {
                if (nvi.Icon is not System.Windows.FrameworkElement icon) return;
                if (icon.RenderTransform is System.Windows.Media.TranslateTransform lift)
                    lift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, System.TimeSpan.FromMilliseconds(250))
                        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } });
                // Once the transient hover bloom lifts, restore the pulsing
                // selection glow (or clear it).
                if (ReferenceEquals(icon.Effect, hoverGlow))
                {
                    if (IsControllerTagSelected(nvi.Tag?.ToString())) SyncCollapsedIconSelection(nvi);
                    else icon.Effect = null;
                }
            };
        }

        /// <summary>Drives <see cref="PadForge.Common.AmbientMotionProbe"/>
        /// from window activation/minimize and rebuilds the controller rail
        /// so the code-built heat rings re-evaluate their gate. XAML breathes
        /// react on their own through trigger conditions. Guarded on actual
        /// state change: dialogs toggle activation constantly and the rail
        /// rebuild is not free.</summary>
        private void SetAmbientMotion(bool active)
        {
            var probe = PadForge.Common.AmbientMotionProbe.Instance;
            if (probe.IsAppActive == active) return;
            probe.IsAppActive = active;
            RebuildControllerSection();
        }

        /// <summary>True when this tag is the focused controller page (a "PadN"
        /// tag equal to the app-wide selection).</summary>
        private bool IsControllerTagSelected(string tag)
            => tag != null && tag.StartsWith("Pad") && tag == _viewModel.SelectedNavTag;

        /// <summary>Applies or clears the pulsing selection glow on a controller
        /// item's COLLAPSED icon. The icon's Effect slot is free (the flame
        /// bloom is baked into the bitmap), so it takes an animated ember bloom
        /// directly, matching the expanded pill's selection pulse.</summary>
        private void SyncCollapsedIconSelection(NavigationViewItem nvi)
        {
            if (nvi.Icon is not System.Windows.FrameworkElement icon) return;
            if (!IsControllerTagSelected(nvi.Tag?.ToString()))
            {
                icon.Effect = null;
                return;
            }
            var glow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C),
                // Kept at the expanded pill's bloom radius; the 48px collapsed rail
                // gives the icon ~12px a side, so a 12px bloom fits without clipping.
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.6,
            };
            icon.Effect = glow;
            if (MotionEnabled && PadForge.Common.AmbientMotionProbe.Instance.IsAppActive)
            {
                var pulse = new System.Windows.Media.Animation.DoubleAnimation(0.5, 0.95,
                    System.TimeSpan.FromMilliseconds(1200))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.SineEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                    BeginTime = System.TimeSpan.FromMilliseconds(
                        -(System.DateTime.UtcNow.TimeOfDay.TotalMilliseconds % 2400.0)),
                };
                // 30fps ambient rate + foreground gate: same measured
                // rationale as the heat ring (every permanent animation frame
                // pays the fixed pipeline cost; a soft pulse cannot show 60
                // samples a second). The static glow above remains while
                // backgrounded.
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulse, 30);
                glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse);
            }
        }

        // The pane's controller pills live inside a DynamicScrollViewer whose
        // ScrollContentPresenter clips its content to the viewport for scrolling.
        // That clip is intrinsic to the presenter (it holds regardless of
        // ClipToBounds or any explicit Clip, both of which are already false on the
        // whole ancestor chain), so a drop shadow on a pill is always truncated ~5px
        // out, at the viewport edge. The reorder drag renders its ghost in the
        // window's adorner layer instead, which sits ABOVE the scroller and is not
        // clipped (that is why a dragged card reaches the window edges). The pill
        // glows take the same route: the selected and hovered pills get their bloom
        // from a PillGlowAdorner in that layer rather than from the card's Effect.
        private PillGlowAdorner _selGlowAdorner;
        private System.Windows.Documents.AdornerLayer _selGlowLayer;
        private PillGlowAdorner _hoverGlowAdorner;
        private System.Windows.Documents.AdornerLayer _hoverGlowLayer;

        /// <summary>Replaces the selected pill's glow adorner. The pill may not be in
        /// the visual tree yet on a fresh rebuild, so it waits for Loaded. The adorner
        /// is added to the NavView's adorner layer (the outer, unclipped one the drag
        /// uses), and the whole adorner (border + bloom) breathes via its Opacity.</summary>
        private void ShowSelectionGlowAdorner(System.Windows.Controls.Border card,
            System.Windows.Media.Color color, bool lit, bool cooling)
        {
            RemoveSelectionGlowAdorner();
            void Attach()
            {
                if (_selGlowAdorner != null || card.ActualWidth <= 0) return;
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(NavView);
                if (layer == null) return;
                var adorner = new PillGlowAdorner(NavView, card, color, 10, 2, 20, 0.6, () => NavView.IsPaneOpen);
                layer.Add(adorner);
                _selGlowAdorner = adorner;
                _selGlowLayer = layer;
                if (MotionEnabled && PadForge.Common.AmbientMotionProbe.Instance.IsAppActive)
                {
                    double lo = lit ? 0.55 : cooling ? 0.45 : 0.35;
                    double periodMs = lit ? 1100 : cooling ? 1600 : 2200;
                    var phase = System.TimeSpan.FromMilliseconds(
                        -(System.DateTime.UtcNow.TimeOfDay.TotalMilliseconds % (periodMs * 2.0)));
                    // NOTE (corrected 2026-07-16): element Opacity is a
                    // DEPENDENT animation in WPF, not composite-time. The old
                    // claim here was wrong. The adorner carries a BitmapCache
                    // so each tick recomposes a cached texture instead of
                    // re-rasterizing the stroke. Gated on foreground +
                    // 30fps for the same measured reasons as the heat ring;
                    // the ungated static adorner keeps the selection visible.
                    var selBreathe = new System.Windows.Media.Animation.DoubleAnimation(lo, 1.0,
                        System.TimeSpan.FromMilliseconds(periodMs))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                        EasingFunction = new System.Windows.Media.Animation.SineEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                        BeginTime = phase,
                    };
                    System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(selBreathe, 30);
                    adorner.BeginAnimation(System.Windows.UIElement.OpacityProperty, selBreathe);
                }
            }
            if (card.IsLoaded && card.ActualWidth > 0)
                Attach();
            else
            {
                System.Windows.RoutedEventHandler h = null;
                h = (s, e) => { card.Loaded -= h; Attach(); };
                card.Loaded += h;
            }
        }

        private void RemoveSelectionGlowAdorner()
        {
            if (_selGlowAdorner == null) return;
            _selGlowAdorner.Detach();
            (_selGlowLayer ?? System.Windows.Documents.AdornerLayer.GetAdornerLayer(_selGlowAdorner.AdornedElement))
                ?.Remove(_selGlowAdorner);
            _selGlowAdorner = null;
            _selGlowLayer = null;
        }

        /// <summary>The hovered pill's ember bloom, also in the NavView adorner layer
        /// (above the scroll viewport) so it is not clipped. Skipped for the selected
        /// pill, which already wears the stronger selection bloom.</summary>
        private void ShowHoverGlowAdorner(System.Windows.Controls.Border card)
        {
            RemoveHoverGlowAdorner();
            if (card.ActualWidth <= 0) return;
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(NavView);
            if (layer == null) return;
            var adorner = new PillGlowAdorner(NavView, card,
                System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C), 10, 2, 14, 0.4, () => NavView.IsPaneOpen);
            layer.Add(adorner);
            _hoverGlowAdorner = adorner;
            _hoverGlowLayer = layer;
        }

        private void RemoveHoverGlowAdorner()
        {
            if (_hoverGlowAdorner == null) return;
            _hoverGlowAdorner.Detach();
            (_hoverGlowLayer ?? System.Windows.Documents.AdornerLayer.GetAdornerLayer(_hoverGlowAdorner.AdornedElement))
                ?.Remove(_hoverGlowAdorner);
            _hoverGlowAdorner = null;
            _hoverGlowLayer = null;
        }

        /// <summary>Draws a pill's rounded-rect ember border plus a soft DropShadow bloom
        /// in the window adorner layer. It adorns the NavView, NOT the pill: WPF clips an
        /// adorner whose adorned element is inside a ScrollViewer to that viewport (the
        /// clip being escaped), which is why the reorder drag also adorns the NavView. A
        /// DropShadowEffect sizes its blurred output to the drawn-content bounds, so the
        /// bloom would truncate ~5px out at the tight border rect; a near-invisible
        /// padding rect widens those bounds to the full blur. The pill's rect is
        /// transformed into NavView coordinates each render, so the glow tracks the pill
        /// through scroll and the hover lift.</summary>
        private sealed class PillGlowAdorner : System.Windows.Documents.Adorner
        {
            private readonly FrameworkElement _target;
            private readonly Visual _root;
            private readonly System.Windows.Media.Color _color;
            private readonly double _radius, _borderThickness, _pad;
            private System.Windows.Media.SolidColorBrush _padBrush;
            private System.Windows.Media.Pen _borderPen;
            private static T Frozen<T>(T f) where T : System.Windows.Freezable { f.Freeze(); return (T)(object)f; }
            private readonly Func<bool> _shouldRender;
            private EventHandler _onLayout;

            public PillGlowAdorner(UIElement root, FrameworkElement target, System.Windows.Media.Color color,
                double radius, double borderThickness, double blur, double opacity, Func<bool> shouldRender) : base(root)
            {
                _root = root;
                _target = target;
                _color = color;
                _radius = radius;
                _borderThickness = borderThickness;
                _pad = blur + 6;
                _shouldRender = shouldRender;
                IsHitTestVisible = false;
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
                // The breathe animates this adorner's ELEMENT Opacity, which is
                // a DEPENDENT animation (ordinary UIPropertyMetadata): each
                // tick dirties the adorner and, uncached, re-rasterized the
                // rounded-rect pen stroke every frame. Cached, the tick
                // recomposes a texture. The Effect stays outside the cache by
                // WPF's rules and keeps blooming.
                CacheMode = new System.Windows.Media.BitmapCache();
                _onLayout = (s, e) => InvalidateVisual();
                _target.LayoutUpdated += _onLayout;
            }

            public void Detach()
            {
                if (_onLayout == null) return;
                _target.LayoutUpdated -= _onLayout;
                _onLayout = null;
            }

            protected override void OnRender(System.Windows.Media.DrawingContext dc)
            {
                if (_target == null || !_target.IsVisible || _target.ActualWidth <= 0 || _target.ActualHeight <= 0)
                    return;
                // Only while the pill is actually shown (pane open). When the pane
                // collapses the pill is hidden but the adorner survives; without this
                // it would draw the pill's border at a stale position out in midair.
                if (_shouldRender != null && !_shouldRender()) return;
                if (System.Windows.PresentationSource.FromVisual(_target) == null) return;
                System.Windows.Point tl;
                try { tl = _target.TransformToVisual(_root).Transform(new System.Windows.Point(0, 0)); }
                catch { return; }
                double w = _target.ActualWidth, h = _target.ActualHeight;

                // Near-invisible padding rect (alpha 1): widens the adorner's drawn-content
                // bounds so the DropShadowEffect's blur is not clipped to the border rect.
                // It casts a negligible shadow of its own.
                // Frozen, hoisted resources: color and thickness are
                // ctor-fixed, and LayoutUpdated re-renders this adorner on
                // every dispatcher layout pass while a pill wears the glow.
                _padBrush ??= Frozen(new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(1, _color.R, _color.G, _color.B)));
                if (_borderPen == null)
                {
                    _borderPen = new System.Windows.Media.Pen(
                        Frozen(new System.Windows.Media.SolidColorBrush(_color)), _borderThickness);
                    _borderPen.Freeze();
                }
                dc.DrawRectangle(_padBrush, null,
                    new Rect(tl.X - _pad, tl.Y - _pad, w + 2 * _pad, h + 2 * _pad));

                // The ember border: outlines the pill and is the source the effect blooms.
                double t = _borderThickness / 2;
                dc.DrawRoundedRectangle(null, _borderPen,
                    new Rect(tl.X + t, tl.Y + t, w - _borderThickness, h - _borderThickness),
                    _radius, _radius);
            }
        }

        // The controller tag currently wearing the selection glow (null when a
        // non-controller page is focused), for change-gated re-glowing.
        private string _glowSelectedControllerTag;

        /// <summary>On a controller selection change, re-glows just the two
        /// affected pills: the newly focused one gains the selection bloom, the
        /// previously focused one reverts. The expanded card recomputes its
        /// Effect via a Content rebuild (the heat ring and selection glow share
        /// the one Effect slot); the collapsed icon is set directly.</summary>
        private void RefreshControllerSelectionVisuals()
        {
            if (_navDashboard == null) return;
            string sel = _viewModel.SelectedNavTag;
            if (sel == null || !sel.StartsWith("Pad")) sel = null;
            if (sel == _glowSelectedControllerTag) return;
            string prev = _glowSelectedControllerTag;
            _glowSelectedControllerTag = sel;

            // Leaving all pads: no pill's rebuild will run to swap the adorner, so
            // drop it here. Pad->pad hands off inside ShowSelectionGlowAdorner.
            if (sel == null) RemoveSelectionGlowAdorner();

            foreach (var mi in NavView.MenuItems)
            {
                if (mi is not NavigationViewItem nvi) continue;
                var t = nvi.Tag?.ToString();
                if (t == null || !t.StartsWith("Pad")) continue;
                if (t != prev && t != sel) continue;

                NavControllerItemViewModel navItem = null;
                if (_viewModel.NavControllerItems != null)
                    foreach (var n in _viewModel.NavControllerItems)
                        if (n.Tag == t) { navItem = n; break; }
                if (navItem != null) UpdateControllerNavItemContent(nvi, navItem);
                SyncCollapsedIconSelection(nvi);
            }
        }

        /// <summary>Unasserts the WPF-UI NavigationViewItem's built-in row
        /// selection visuals (the selected/hover/pressed MainBorder highlight
        /// and the left ActiveRectangle indicator pill) for one item, so the
        /// controller mini cards and the Add Controller entry show only our
        /// own selection feedback. The Compact template (PaneDisplayMode=Left)
        /// paints those via DynamicResource brush keys, so shadowing the keys
        /// in the item's local resource scope transparently overrides them for
        /// this item alone. Standard entries (Dashboard, About) never get this
        /// call and keep the default selection highlight. Keys verified against
        /// the cloned Wpf.Ui 4.3.0 NavigationViewCompact.xaml + its theme
        /// dictionaries. Transparent is theme-neutral, so dark and light both
        /// resolve correctly.</summary>
        private static void SuppressBuiltInSelectionVisual(NavigationViewItem item)
        {
            var clear = System.Windows.Media.Brushes.Transparent;
            item.Resources["NavigationViewItemBackground"] = clear;
            item.Resources["NavigationViewItemBackgroundSelected"] = clear;
            item.Resources["NavigationViewItemBackgroundPointerOver"] = clear;
            item.Resources["NavigationViewItemBackgroundPressed"] = clear;
            item.Resources["NavigationViewItemBackgroundDisabled"] = clear;
            item.Resources["NavigationViewSelectionIndicatorForeground"] = clear;
        }

        /// <summary>The muted "+" the collapsed rail shows for Add Controller.
        /// Tertiary-grey to match the Dashboard card's add sign, DynamicResource
        /// so it re-tints on a theme flip.</summary>
        private static FontIcon MakeAddControllerCollapsedIcon()
        {
            var icon = new FontIcon
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                Glyph = "",
            };
            icon.SetResourceReference(FontIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
            return icon;
        }

        /// <summary>The expanded Add Controller card: a minified copy of the
        /// Dashboard tile (DashboardPage.xaml), sized to the mini-card pill
        /// (212 wide, radius 10). WPF Border can't dash, so a Rectangle wears
        /// the dashed SteelLineBrush outline; a centered "+" glyph + label sit
        /// in TextFillColorTertiaryBrush. Hover matches the mini cards: a 2 px
        /// lift plus a faint ember bloom.</summary>
        private System.Windows.Controls.Border BuildAddControllerCard()
        {
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            var label = new TextBlock
            {
                Text = Strings.Instance.Main_AddController,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(glyph);
            content.Children.Add(label);

            var dash = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 10,
                RadiusY = 10,
                StrokeThickness = 1.5,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 },
                SnapsToDevicePixels = true,
            };
            dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SteelLineBrush");

            var grid = new Grid();
            grid.Children.Add(dash);
            grid.Children.Add(content);

            var card = new System.Windows.Controls.Border
            {
                // Exact mini-card pill footprint so the entry stacks congruently
                // and the dashed outline (plus its hover glow) traces the same
                // rectangle a solid pill does: radius 10, width 233, 3/2 margin,
                // and MinHeight 36 = the pill's rendered height (the fixed 22 px
                // delete-button row + 12 px padding + 2 px border in
                // UpdateControllerNavItemContent). NO Padding here: like the
                // Dashboard card, the Rectangle must fill the full card bounds,
                // not sit inset. Inset shrinks the box and makes the glow hug a
                // smaller inner outline.
                CornerRadius = new CornerRadius(10),
                Width = 233,
                MinHeight = 36,
                Margin = new Thickness(3, 2, 3, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = grid,
            };

            // Hover: 2 px lift + faint ember bloom, the same feedback the mini
            // cards use.
            var lift = new System.Windows.Media.TranslateTransform();
            card.RenderTransform = lift;
            var hoverGlow = TryFindResource("NeutralHoverGlow") as System.Windows.Media.Effects.Effect;
            card.MouseEnter += (s, e) =>
            {
                var up = new System.Windows.Media.Animation.DoubleAnimation(-2, System.TimeSpan.FromMilliseconds(130))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                lift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, up);
                if (card.Effect == null && hoverGlow != null) card.Effect = hoverGlow;
            };
            card.MouseLeave += (s, e) =>
            {
                var down = new System.Windows.Media.Animation.DoubleAnimation(0, System.TimeSpan.FromMilliseconds(250))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
                lift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, down);
                if (ReferenceEquals(card.Effect, hoverGlow)) card.Effect = null;
            };
            return card;
        }

        /// <summary>Swaps the Add Controller entry between the expanded card
        /// (Content) and the collapsed "+" (Icon), mirroring
        /// UpdateAllControllerCardMode for the mini cards so both ride the same
        /// PaneClosed/PaneOpened crossfade.</summary>
        private void UpdateAddControllerCardMode(NavigationViewItem nvi, bool compact)
        {
            if (compact)
            {
                nvi.Icon = MakeAddControllerCollapsedIcon();
                nvi.Margin = new Thickness(0);
            }
            else
            {
                nvi.Icon = null;
                nvi.Margin = new Thickness(-40, 0, 0, 0);
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

            return new Wpf.Ui.Controls.ImageIcon { Source = rtb };
        }

        /// <summary>
        /// Builds a compact mini card for collapsed sidebar: two rows stacked vertically.
        /// Row 1: Flame heat glyph + slot number. Row 2: Type icon + subgroup number.
        /// The flame replaced the old MDL2 gamepad glyph (user report 2026-07-07:
        /// it clashed with the #175 identity) and carries the same heat
        /// semantics as the expanded pill's flame.
        /// </summary>
        private System.Windows.Controls.Border BuildCompactCard(NavControllerItemViewModel navItem)
        {
            var mdl2 = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
            bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            var fgBrush = new System.Windows.Media.SolidColorBrush(
                isDark ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
            fgBrush.Freeze();

            // Row 1: Flame heat glyph + slot number
            ComputeFlameHeat(navItem, out bool lit, out bool cooling);
            var row1 = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var compactFlame = BuildFlameGlyph(11, lit, cooling);
            compactFlame.VerticalAlignment = VerticalAlignment.Center;
            row1.Children.Add(compactFlame);
            row1.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = navItem.SlotNumber.ToString(),
                // Telemetry mono (#175 font sweep): slot-number token, same
                // face as the expanded pill's slot number.
                FontFamily = (System.Windows.Media.FontFamily)FindResource("TelemetryFontFamily"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            });

            // Row 2: Type icon + subgroup number
            var row2 = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            string iconKey = navItem.IconKey;
            if (iconKey == "XboxControllerIcon" || iconKey == "DS4ControllerIcon"
                || iconKey == "NintendoControllerIcon" || iconKey == "ExtendedControllerIcon")
            {
                string svgPath = iconKey == "XboxControllerIcon" ? XboxSvgPath
                    : iconKey == "DS4ControllerIcon" ? DS4SvgPath
                    : iconKey == "NintendoControllerIcon" ? SwitchSvgPath : ExtendedSvgPath;
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
                // Telemetry mono (#175 font sweep): instance token, same
                // face as the expanded pill's "#N" label.
                FontFamily = (System.Windows.Media.FontFamily)FindResource("TelemetryFontFamily"),
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
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Xbox);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Xbox;
                _inputService.MoveSlotToGroupTail(padIndex);
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
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.PlayStation);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.PlayStation;
                _inputService.MoveSlotToGroupTail(padIndex);
                // Stale-guard the Mappings view (see OnSidebarTypeXbox).
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
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Extended);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Extended;
                _inputService.MoveSlotToGroupTail(padIndex);
                // Stale-guard the Mappings view (see OnSidebarTypeXbox).
                _viewModel.Pads[padIndex].MappingsViewLoaded = false;
                _settingsService.MarkDirty();
            }
        }

        /// <summary>Handles sidebar Nintendo type button click.</summary>
        private void OnSidebarTypeNintendo(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int padIndex)
            {
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Nintendo);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Nintendo;
                _inputService.MoveSlotToGroupTail(padIndex);
                // Stale-guard the Mappings view (see OnSidebarTypeXbox).
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
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.KeyboardMouse);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.KeyboardMouse;
                _inputService.MoveSlotToGroupTail(padIndex);
                // Stale-guard the Mappings view (see OnSidebarTypeXbox).
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
                // Merge BEFORE the type set: see the dashboard
                // SlotTypeChangeRequested handler for the 2026-07-22
                // automap-loss root cause this order prevents.
                SettingsManager.ReAutoMapSlot(padIndex, VirtualControllerType.Midi);
                SettingsService.RefreshMappingSetsFromLegacy();
                _viewModel.Pads[padIndex].OutputType = VirtualControllerType.Midi;
                _inputService.MoveSlotToGroupTail(padIndex);
                // Stale-guard the Mappings view (see OnSidebarTypeXbox).
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
            // VisualBrush render: RTB.Render(card) bakes in the hover-lift
            // RenderTransform (-2px at drag start), cutting the ghost's top.
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(
                    new System.Windows.Media.VisualBrush(card) { Stretch = System.Windows.Media.Stretch.None },
                    null, new System.Windows.Rect(0, 0, card.ActualWidth, card.ActualHeight));
            }
            rtb.Render(dv);
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
                Padding = new Thickness(6),
                // 1px stroke (#175): flyouts carry the same edge as cards.
                BorderThickness = new Thickness(1)
            };
            border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
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
                // Raised steel (#175): the flyout sits one step above the card fill.
                var color = dark ? System.Windows.Media.Color.FromRgb(0x1B, 0x23, 0x33)
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
            // across all six groups). When the global total is at the cap
            // every "Add" button disables uniformly. Per-type counts are
            // kept for the at-capacity tooltip text.
            int xboxCount = 0, playstationCount = 0, nintendoCount = 0, extendedCount = 0, midiCount = 0, kbmCount = 0;
            int totalActive = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                totalActive++;
                switch (_viewModel.Pads[i].OutputType)
                {
                    case VirtualControllerType.Xbox: xboxCount++; break;
                    case VirtualControllerType.PlayStation: playstationCount++; break;
                    case VirtualControllerType.Nintendo: nintendoCount++; break;
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

            // Nintendo button, theme-aware icon fill. Uses the Switch logo
            // to represent the Nintendo family in the UI.
            var nintendoPopupPath = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(SwitchSvgPath),
                Width = 28,
                Height = 28,
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            nintendoPopupPath.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
            bool nintendoAtCapacity = nintendoCount >= SettingsManager.MaxNintendoSlots;
            bool nintendoDisabled = globalAtCapacity || nintendoAtCapacity;
            if (nintendoDisabled) nintendoPopupPath.Opacity = 0.35;
            var nintendoBtn = new System.Windows.Controls.Button
            {
                Content = nintendoPopupPath,
                ToolTip = nintendoAtCapacity
                        ? string.Format(Strings.Instance.Main_Nintendo_Max_Format, SettingsManager.MaxNintendoSlots)
                        : Strings.Instance.ControllerType_Nintendo,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8),
                MinWidth = 0,
                Cursor = nintendoDisabled ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(nintendoBtn, "AddNintendoBtn");
            nintendoBtn.Click += (s, e) =>
            {
                if (nintendoDisabled) return;
                popup.IsOpen = false;
                int newSlot = _deviceService.CreateSlot(VirtualControllerType.Nintendo);
                if (newSlot >= 0)
                {
                    int nav = FindLastSlotOfType(VirtualControllerType.Nintendo);
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSlot(nav >= 0 ? nav : newSlot)));
                }
            };
            stack.Children.Add(nintendoBtn);

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
            System.Windows.Automation.AutomationProperties.SetAutomationId(extendedBtn, "AddRawBtn");
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
        /// Shows the profile switcher flyout (#175 item 8) anchored at a
        /// profile pill. Mirrors ShowControllerTypePopup's construction
        /// (raised-steel surface, separate shadow element, outside-click
        /// dismiss, live theme tracking); rows list every profile with a
        /// lit flame on the active one, and a click runs the same path as
        /// the Profiles page Load button.
        /// </summary>
        private void ShowProfileSwitcherPopup(UIElement anchor, PlacementMode placement = PlacementMode.Bottom)
        {
            // If the popup is already open, close it instead of opening a duplicate.
            if (_profileSwitcherPopup != null && _profileSwitcherPopup.IsOpen)
            {
                _profileSwitcherPopup.IsOpen = false;
                _profileSwitcherPopup = null;
                return;
            }

            // Suppress reopening if the popup was just dismissed within the same click cycle.
            if ((DateTime.UtcNow - _profilePopupClosedAt).TotalMilliseconds < 300)
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
                _profileSwitcherPopup = null;
                _profilePopupClosedAt = DateTime.UtcNow;
            };
            _profileSwitcherPopup = popup;

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
                Padding = new Thickness(6),
                // 1px stroke (#175): flyouts carry the same edge as cards.
                BorderThickness = new Thickness(1)
            };
            border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
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
                // Raised steel (#175): the flyout sits one step above the card fill.
                var color = dark ? System.Windows.Media.Color.FromRgb(0x1B, 0x23, 0x33)
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

            var stack = new System.Windows.Controls.StackPanel();

            // One row per profile (ProfileItems already includes the Default
            // entry). Flame grammar matches the Profiles page cards: lit
            // ember on the active row, cold outline on the rest.
            foreach (var item in _viewModel.Settings.ProfileItems)
            {
                var flame = new System.Windows.Shapes.Path
                {
                    Data = TryFindResource("FlameOuterGeometry") as System.Windows.Media.Geometry,
                    Width = 11,
                    Height = 13,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                if (item.IsActive)
                {
                    flame.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "EmberBrush");
                    // Rendered lit only on the active row, so the glow is
                    // static (#175 glow sweep).
                    flame.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x2C),
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Opacity = 0.5
                    };
                }
                else
                {
                    flame.Fill = System.Windows.Media.Brushes.Transparent;
                    flame.StrokeThickness = 1.1;
                    flame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorTertiaryBrush");
                }

                var name = new System.Windows.Controls.TextBlock
                {
                    Text = item.Name,
                    // Body face set explicitly (#175 font sweep): a code-built
                    // Popup sits outside the window's tree, so the window-level
                    // BodyFontFamily never inherits into it.
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("BodyFontFamily"),
                    MaxWidth = 240,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Name,
                    VerticalAlignment = VerticalAlignment.Center
                };
                name.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                    item.IsActive ? "EmberHotBrush" : "TextFillColorPrimaryBrush");

                var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(flame);
                row.Children.Add(name);

                var rowBtn = new System.Windows.Controls.Button
                {
                    Content = row,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 6, 8, 6),
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(rowBtn,
                    "SwitchProfileBtn_" + (item.IsDefault ? "Default" : item.Id));
                var captured = item;
                rowBtn.Click += (s, e) =>
                {
                    popup.IsOpen = false;
                    ActivateProfileFromSwitcher(captured);
                };
                stack.Children.Add(rowBtn);
            }

            // Long profile lists scroll instead of running off-screen.
            // Visible bar per the app scrollbar canon (ComboBox dropdowns
            // keep theirs visible too).
            var scroller = new System.Windows.Controls.ScrollViewer
            {
                Content = stack,
                MaxHeight = 320,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Visible
            };
            border.Child = scroller;
            popup.Child = container;
            popup.IsOpen = true;
        }

        /// <summary>
        /// Applies a profile chosen in the switcher flyout: the same path
        /// as the Profiles page Load button (OnLoadProfile / OnRevertToDefault),
        /// plus the manual-override note the shortcut path makes in
        /// InputService.UiTimer_Tick so auto-switching won't immediately
        /// fight the choice.
        /// </summary>
        private void ActivateProfileFromSwitcher(ViewModels.ProfileListItem item)
        {
            if (item == null || item.IsActive) return;

            // Mirror of the shortcut manual path: record the override
            // BEFORE the switch, with the pre-switch active id.
            _inputService.NoteManualProfileSwitch();

            if (item.IsDefault)
            {
                OnRevertToDefault(this, EventArgs.Empty);
                return;
            }

            _inputService.LoadProfile(item.Id);
            var profile = SettingsManager.Profiles.Find(p => p.Id == item.Id);
            if (profile != null)
            {
                _viewModel.Settings.ActiveProfileInfo = profile.Name;
                _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileLoaded_Format, profile.Name);
            }
            _settingsService.MarkDirty();
        }

        /// <summary>
        /// Programmatically navigates to a controller slot page (e.g., "Pad1").
        /// </summary>
        private void NavigateToSlot(int slotIndex)
        {
            SelectNavItemByTag($"Pad{slotIndex + 1}");
        }

        /// <summary>
        /// Returns the slot at the TAIL of the type's group order list, or -1
        /// if none. Used after CreateSlot to navigate to the newly added slot,
        /// which CreateSlot appends to that order list.
        ///
        /// <para>Pad INDEX order is not group order: CreateSlot reuses the
        /// first free pad index, so after a middle-slot delete the new slot's
        /// index is lower than its same-type siblings. Scanning indices then
        /// returned a pre-existing slot and the Add-Controller handlers
        /// navigated the user to someone else's config (round 34).</para>
        /// </summary>
        private int FindLastSlotOfType(VirtualControllerType type)
        {
            var order = SettingsManager.SlotOrders.GetOrderFor(type);
            for (int p = order.Count - 1; p >= 0; p--)
            {
                int pad = order[p];
                if (pad >= 0 && pad < InputManager.MaxPads
                    && SettingsManager.SlotCreated[pad]
                    && _viewModel.Pads[pad].OutputType == type)
                    return pad;
            }
            // Types with no order list (MIDI / KeyboardMouse) keep the old
            // index scan: they have no group-order concept to be wrong about.
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
            if (!NavView.IsPaneOpen)
            {
                // Collapsed: the pill is hidden, so drop its glow adorner (the rail
                // shows selection via the icon glow). Prevents a stale midair border.
                RemoveSelectionGlowAdorner();
                RemoveHoverGlowAdorner();
            }
            else
            {
                // Expanded: rebuild the selected pill so its glow adorner re-attaches.
                Dispatcher.BeginInvoke(new Action(ReshowSelectionGlow),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Re-attaches the selection glow adorner to the currently selected
        /// pill (used after the pane expands, since the pill is rebuilt fresh).</summary>
        private void ReshowSelectionGlow()
        {
            string sel = _viewModel.SelectedNavTag;
            if (sel == null || !sel.StartsWith("Pad")) return;
            foreach (var mi in NavView.MenuItems)
            {
                if (mi is not NavigationViewItem nvi || nvi.Tag?.ToString() != sel) continue;
                NavControllerItemViewModel navItem = null;
                if (_viewModel.NavControllerItems != null)
                    foreach (var n in _viewModel.NavControllerItems)
                        if (n.Tag == sel) { navItem = n; break; }
                if (navItem != null) UpdateControllerNavItemContent(nvi, navItem);
                break;
            }
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

            // The tool's hit rect is BAKED at registration, and the title-bar
            // button moves with the window's right edge, so a single
            // registration went stale on the first resize: the relayed
            // WM_MOUSEMOVE carries current coordinates that no longer fall
            // inside the recorded rect, and the tooltip stopped appearing
            // (round 34). Re-register on geometry and DPI changes.
            // AddNativeTooltipTool already sends TTM_DELTOOL before
            // TTM_ADDTOOL, which is exactly what makes this safe to repeat.
            SizeChanged += (s, ev) => AddNativeTooltipTool(source.Handle);
            DpiChanged += (s, ev) => AddNativeTooltipTool(source.Handle);

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

            // Re-glow the drawer's selected controller card + collapsed icon.
            RefreshControllerSelectionVisuals();

            // Swap visible page.
            DashboardPageView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            DevicesPageView.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
            ProfilesPageView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPageView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            AboutPageView.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;

            bool isPad = tag.StartsWith("Pad") && tag.Length >= 4 && int.TryParse(tag.Substring(3), out _);
            PadPageView.Visibility = isPad ? Visibility.Visible : Visibility.Collapsed;

            // Mirror the focused controller onto the Dashboard so its card wears
            // the same selection glow (-1 = no controller focused).
            int selectedPadIndex = -1;
            if (isPad && int.TryParse(tag.Substring(3), out int selPad1Based))
                selectedPadIndex = selPad1Based - 1;
            _viewModel.Dashboard?.SetSelectedPad(selectedPadIndex);

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
                ExecutablePaths = exePaths,
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
                ExecutablePaths = exePaths,
            };
            SettingsService.UpdateTopologyCounts(listItem, snapshot.SlotCreated, snapshot.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileCreated_Format, name);
        }

        private void OnDeleteProfile(object sender, EventArgs e)
        {
            var selected = _viewModel.Settings.SelectedProfile;
            // The command's CanExecute already blocks the built-in Default.
            // The gate stays here too so no other invoker can destroy it.
            if (selected == null || selected.IsDefault) return;

            // Destructive-verb guard (#175 phase 2 item 1c): deleting a
            // profile destroys its saved slot/mapping snapshot, so ask
            // first through the shared confirm.
            bool confirmed = Views.ConfirmDialog.Show(
                this,
                Strings.Instance.Profiles_DeleteConfirmTitle,
                string.Format(Strings.Instance.Profiles_DeleteConfirm_Format, selected.Name),
                Strings.Instance.Common_Delete);
            if (!confirmed) return;

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
                _viewModel.SetStatus(ex.Message, persist: true);
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

            ImportProfileFromFile(dlg.FileName);
        }

        /// <summary>File-consuming half of profile import, shared by the
        /// Import button's dialog and the Profiles page drop zone (#175).</summary>
        private void ImportProfileFromFile(string filePath)
        {
            var profile = PadForge.Common.ProfileTransfer.Import(filePath, out var packages);
            if (profile == null)
            {
                _viewModel.SetStatus(Strings.Instance.Status_ProfileImportFailed, persist: true);
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
                ExecutablePaths = profile.ExecutableNames ?? string.Empty,
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();
            _viewModel.StatusText = string.Format(Strings.Instance.Status_ProfileImported_Format, profile.Name, packages.Count);
        }

        /// <summary>Opens the Steam Workshop browse dialog (#9). Always opens:
        /// with the opt-in off, the dialog presents its cold-forge state and
        /// the enable action flips the same persisted setting the Settings
        /// card toggles.</summary>
        private void OnBrowseCommunityConfigs(object sender, EventArgs e)
        {
            var dlg = new Views.WorkshopBrowseDialog(_viewModel.Settings) { Owner = this };
            dlg.ImportSink = AddWorkshopProfile;
            dlg.ShowDialog();
            // Post-import, the dossier summary rides the status line
            // (design flow step 4: what came across, in one line).
            if (dlg.ImportedProfileName != null)
            {
                _viewModel.StatusText = string.Format(Strings.Instance.Status_WorkshopImported_Format,
                    dlg.ImportedProfileName, dlg.ImportedClean, dlg.ImportedPartial, dlg.ImportedSkipped);
            }
        }

        /// <summary>Purges the Workshop cache directory (Settings card button).</summary>
        private void OnClearWorkshopCache(object sender, EventArgs e)
        {
            try
            {
                new PadForge.SteamWorkshop.Cache.SteamWorkshopCache().Clear();
                _viewModel.StatusText = Strings.Instance.Status_WorkshopCacheCleared;
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(ex.Message, persist: true);
            }
        }

        /// <summary>Non-null while an update check runs. Doubles as the
        /// reentrancy guard and is cancelled by OnClosing so an in-flight
        /// Steam query never outlives shutdown.</summary>
        private System.Threading.CancellationTokenSource _workshopUpdateCts;

        /// <summary>Checks every Workshop-imported profile for a newer
        /// Workshop version (#9 Phase D): batch-queries
        /// GetPublishedFileDetails over the stored SteamWorkshopSource ids
        /// and compares time_updated. Results ride the status line, except
        /// when something is stale, which gets a dialog listing the profiles
        /// and offering Browse Community Configs as the re-import route.
        /// With the opt-in off this never touches the network.</summary>
        private async void OnCheckWorkshopUpdates(object sender, EventArgs e)
        {
            if (!_viewModel.Settings.EnableCommunityConfigLookup)
            {
                _viewModel.StatusText = Strings.Instance.Status_WorkshopUpdatesOptInRequired;
                return;
            }
            if (_workshopUpdateCts != null) return;

            var imported = SettingsManager.Profiles
                .Where(p => p.WorkshopSource != null && p.WorkshopSource.PublishedFileId != 0)
                .Select(p => (p.Name, Source: p.WorkshopSource))
                .ToList();
            if (imported.Count == 0)
            {
                _viewModel.StatusText = Strings.Instance.Status_WorkshopNoImportedProfiles;
                return;
            }

            _workshopUpdateCts = new System.Threading.CancellationTokenSource();
            var ct = _workshopUpdateCts.Token;
            _viewModel.StatusText = Strings.Instance.Status_WorkshopCheckingUpdates;
            try
            {
                var gate = new PadForge.SteamWorkshop.DelegateSteamWorkshopGate(
                    () => _viewModel.Settings.EnableCommunityConfigLookup);
                var client = new PadForge.SteamWorkshop.Api.SteamRemoteStorageClient(gate);
                var ids = imported.Select(x => (long)x.Source.PublishedFileId).Distinct().ToList();
                var details = await client.GetDetailsAsync(ids, ct);
                if (ct.IsCancellationRequested) return;

                var freshById = new Dictionary<ulong, PadForge.SteamWorkshop.Api.Dto.PublishedFileDetails>();
                foreach (var d in details)
                {
                    // Per-item result 1 is OK. Removed or banned items come
                    // back with another code and stay unreported.
                    if (d.Result == 1 && ulong.TryParse(d.PublishedFileId, out var id))
                        freshById[id] = d;
                }

                var stale = new List<string>();
                foreach (var (name, source) in imported)
                {
                    if (freshById.TryGetValue(source.PublishedFileId, out var fresh) &&
                        fresh.TimeUpdated > source.TimeUpdated)
                    {
                        string title = string.IsNullOrWhiteSpace(fresh.Title) ? source.Title : fresh.Title;
                        stale.Add(string.Format(Strings.Instance.Workshop_UpdateRow_Format, name, title));
                    }
                }

                if (stale.Count == 0)
                {
                    _viewModel.StatusText = string.Format(
                        Strings.Instance.Status_WorkshopProfilesCurrent_Format, imported.Count);
                    return;
                }

                _viewModel.StatusText = string.Empty;
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = Strings.Instance.Workshop_UpdatesTitle,
                    Content = Strings.Instance.Workshop_UpdatesBody + "\n\n" + string.Join("\n", stale),
                    PrimaryButtonText = Strings.Instance.Profiles_BrowseCommunity,
                    CloseButtonText = Strings.Instance.Common_Close,
                };
                var result = await dialog.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                    OnBrowseCommunityConfigs(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown cancelled the query. Nothing to report.
            }
            catch (System.Net.Http.HttpRequestException)
            {
                _viewModel.SetStatus(Strings.Instance.Workshop_ErrorBody, persist: true);
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // HttpClient timeout, not user cancellation (that case is
                // filtered above). Same calm connectivity sentence as the
                // browse dialog's error matrix.
                _viewModel.SetStatus(Strings.Instance.Workshop_ErrorBody, persist: true);
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(ex.Message, persist: true);
            }
            finally
            {
                _workshopUpdateCts?.Dispose();
                _workshopUpdateCts = null;
            }
        }

        /// <summary>Registers a Workshop-translated profile through the same
        /// steps the .pfprofile Import path takes (name dedup, registry add,
        /// list item with topology counts, MarkDirty), then optionally loads
        /// it as the active profile. Returns the deduped display name.</summary>
        private string AddWorkshopProfile(Services.ProfileData profile, bool applyAfter)
        {
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
                ExecutablePaths = profile.ExecutableNames ?? string.Empty,
            };
            SettingsService.UpdateTopologyCounts(listItem, profile.SlotCreated, profile.SlotControllerTypes);
            _viewModel.Settings.ProfileItems.Add(listItem);
            _settingsService.MarkDirty();

            if (applyAfter)
            {
                _inputService.LoadProfile(profile.Id);
                _viewModel.Settings.ActiveProfileInfo = profile.Name;
                _settingsService.MarkDirty();
            }

            return profile.Name;
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
            selected.ExecutablePaths = newExePaths;
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

                    // #111 audit fix B. A fresh recording supersedes any in-flight
                    // kind recording on another row. Clear that row's stuck Stop
                    // state so its button does not linger showing "recording".
                    if (_kindRecordMapping != null && !ReferenceEquals(_kindRecordMapping, mi))
                    {
                        _kindRecordMapping.IsRecording = false;
                        _kindRecordMapping = null;
                        _kindRecordStage = KindRecStage.None;
                    }

                    // #111 non-Direct primary kind. The one Record button drives
                    // the kind's own inputs, never a conflicting Direct descriptor.
                    // Ramp / Incremental record Up then Down (see the sequential
                    // advance in RecordingCompleted). Invert On Hold records the
                    // single modifier. The per-direction pickers stay for manual
                    // edits, but there is only ever one Record button on the row.
                    var pk = mi.PrimaryKindSource;
                    if (pk != null && !mi.IsPrimaryDirect)
                    {
                        var firstTarget = pk.UsesUpDownKeys
                            ? RecorderService.ParamTarget.Up
                            : RecorderService.ParamTarget.Modifier;
                        _kindRecordMapping = mi;
                        _kindRecordStage = pk.UsesUpDownKeys ? KindRecStage.Up : KindRecStage.None;
                        mi.IsRecording = true;
                        _recorderService.StartRecordingExtraSourceParam(mi, pk, capturedPad.PadIndex, firstTarget);
                        if (_recorderService.IsRecording)
                        {
                            capturedPad.CurrentRecordingTarget = mi.TargetSettingName;
                            _viewModel.SetStatus(pk.UsesUpDownKeys
                                ? string.Format(Strings.Instance.Status_RecordKindUp_Format, mi.TargetLabel)
                                : string.Format(Strings.Instance.Status_RecordingPrompt_Format, mi.TargetLabel), persist: true);
                        }
                        else
                        {
                            mi.IsRecording = false;
                            _kindRecordMapping = null;
                            _kindRecordStage = KindRecStage.None;
                        }
                        return;
                    }

                    // Y axes: record neg (up in game) first due to NegateAxis inversion.
                    // For standard gamepad: TargetSettingName contains "AxisY".
                    // For Extended custom sticks: TargetSettingName is "RawAxisN" — check label for "Y".
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
                // #111 clear any in-flight single-button kind recording so a
                // re-click starts fresh and the row button leaves its Stop state.
                if (_kindRecordMapping != null)
                {
                    _kindRecordMapping.IsRecording = false;
                    _kindRecordMapping = null;
                    _kindRecordStage = KindRecStage.None;
                }
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
                    or nameof(MappingItem.IrPointerSensitivity)
                    or nameof(MappingItem.Sensitivity)
                    or nameof(MappingItem.PrimarySourceDeviceGuid)
                    or nameof(MappingItem.CombineMode)
                    or nameof(MappingItem.CombineExpression)
                    or nameof(MappingItem.NoInherit)
                    or nameof(MappingItem.TrimDeadzone)
                    or nameof(MappingItem.TrimRate)
                    or nameof(MappingItem.TrimResetOnRelease))
                {
                    _settingsService.MarkDirty();

                    // Dirty-gate trap (#155): MarkDirty only schedules a
                    // 250 ms debounced save, so the domain MappingSet the
                    // engine reads (and that RefreshMappingsCore mirrors
                    // BACK into the VM) still holds the pre-edit value. If a
                    // reload fires inside that window (device select, layer
                    // switch, or a device blip on a churning rig), it
                    // reverts the just-made edit from the stale domain and
                    // the following save then persists the revert. Unchecking
                    // Reset on Release was lost exactly this way. Push the
                    // per-row-shape fields to the domain immediately, the
                    // same idiom device selection already uses, so the
                    // domain never trails the VM. The SuppressMappingEditPush
                    // guard skips this while RefreshMappingsCore is itself
                    // mutating the VM (its own setters raise PropertyChanged),
                    // so the push never re-enters mid-reload with a
                    // half-cleared row and clobbers the shared domain object.
                    if (!InputService.SuppressMappingEditPush
                        && e.PropertyName is nameof(MappingItem.CombineMode)
                        or nameof(MappingItem.CombineExpression)
                        or nameof(MappingItem.NoInherit)
                        or nameof(MappingItem.TrimDeadzone)
                        or nameof(MappingItem.TrimRate)
                        or nameof(MappingItem.TrimResetOnRelease))
                        _settingsService.PushUiExtraSourcesIntoSlotMappingSets();
                }
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
                        or nameof(MappingSourceItem.IrPointerSensitivity)
                        or nameof(MappingSourceItem.Sensitivity)
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
            else if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(d))
                d = d.Substring(1);
            else if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
                d = d.Substring(1);
            // "Axis N" / "Slider N" plus the abstract Gamepad sticks /
            // triggers that canonicalize to one (#9), so a recording over
            // an alias primary joins it instead of overwriting it.
            return PadForge.Engine.Common.Mapping.SourceCoercion.IsGenericSensitivityDescriptor(d);
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

            // Theme applies re-derive the accent from the system color, so
            // pin Ember again, then re-evaluate the steel ground (#175).
            PadForge.Common.EmberTheme.ApplyAccent();
            UpdateSteelLayer();
        }

        // Ember steel ground is a dark-theme surface. In Light the Mica
        // backdrop stands alone (#175).
        private void UpdateSteelLayer()
        {
            bool dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                        == Wpf.Ui.Appearance.ApplicationTheme.Dark;
            SteelLayer.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────
        //  First-run welcome + spotlight tour (#175)
        // ─────────────────────────────────────────────

        /// <summary>Pre-v4 builds tracked tour completion with a marker
        /// file beside the exe. The flag lives in PadForge.xml now (the
        /// single settings file); this path exists only to honor and
        /// delete a leftover marker on upgrade.</summary>
        private static string LegacyFirstRunMarkerPath =>
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PadForge.firstrun");

        private int _tourStep = -1;

        private (FrameworkElement Target, string Title, string Body)[] BuildTourStops() => new[]
        {
            ((FrameworkElement)DashboardPageView.EngineCard, Strings.Instance.Tour_Engine_Title, Strings.Instance.Tour_Engine_Body),
            (DashboardPageView.SlotsItemsControl, Strings.Instance.Tour_Cards_Title, Strings.Instance.Tour_Cards_Body),
            (NavView, Strings.Instance.Tour_Slots_Title, Strings.Instance.Tour_Slots_Body),
            (DashboardPageView.ServicesSection, Strings.Instance.Tour_Services_Title, Strings.Instance.Tour_Services_Body),
            (StatusBarBorder, Strings.Instance.Tour_Status_Title, Strings.Instance.Tour_Status_Body),
        };

        private void MaybeShowFirstRun(object sender, RoutedEventArgs e)
        {
            Loaded -= MaybeShowFirstRun;
            // Upgrade migration: a pre-v4 marker file means the tour was
            // already completed. Fold it into the settings flag and remove
            // the file from disk.
            try
            {
                if (System.IO.File.Exists(LegacyFirstRunMarkerPath))
                {
                    if (!_viewModel.Settings.FirstRunTourCompleted)
                    {
                        _viewModel.Settings.FirstRunTourCompleted = true;
                        _settingsService?.MarkDirty();
                    }
                    System.IO.File.Delete(LegacyFirstRunMarkerPath);
                }
            }
            catch { }
            if (!_viewModel.Settings.FirstRunTourCompleted)
                FirstRunOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>Re-runs the welcome tour (Settings button).</summary>
        public void StartFirstRunTour()
        {
            // Actually navigate. IsActive alone only restyles the nav item,
            // which left the tour highlighting over the Settings page.
            NavigateToTag("Dashboard");
            foreach (var mi in NavView.MenuItems)
            {
                if (mi is NavigationViewItem nvi && nvi.Tag?.ToString() == "Dashboard")
                {
                    nvi.IsActive = true;
                    break;
                }
            }
            WelcomePanel.Visibility = Visibility.Visible;
            TourCanvas.Visibility = Visibility.Collapsed;
            FirstRunOverlay.Visibility = Visibility.Visible;
        }

        private void FirstRunBegin_Click(object sender, RoutedEventArgs e)
        {
            // The tour's first four stops live on the Dashboard. Make sure
            // it is the visible page even if the user navigated away
            // before clicking Begin.
            NavigateToTag("Dashboard");
            WelcomePanel.Visibility = Visibility.Collapsed;
            TourCanvas.Visibility = Visibility.Visible;
            ShowTourStep(0);
        }

        private void FirstRunSkip_Click(object sender, RoutedEventArgs e) => CompleteFirstRun();

        private void TourNext_Click(object sender, RoutedEventArgs e) => ShowTourStep(_tourStep + 1);

        private void ShowTourStep(int index)
        {
            var stops = BuildTourStops();
            if (index >= stops.Length)
            {
                CompleteFirstRun();
                return;
            }
            _tourStep = index;
            var (target, title, body) = stops[index];
            TourStepLabel.Text = $"{index + 1} / {stops.Length}";
            TourTitle.Text = title;
            TourBody.Text = body;
            TourNextBtn.Content = index == stops.Length - 1
                ? Strings.Instance.FirstRun_Done
                : Strings.Instance.FirstRun_Next;
            target.BringIntoView();
            // Position after the BringIntoView scroll has been laid out.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => PositionTourStep(target)));
        }

        private void PositionTourStep(FrameworkElement target)
        {
            try
            {
                double w = target.ActualWidth, h = target.ActualHeight;
                var origin = target.TransformToVisual(FirstRunOverlay).Transform(new Point(0, 0));
                if (ReferenceEquals(target, NavView))
                {
                    // Highlight only the pane strip, not the whole view.
                    w = NavView.IsPaneOpen ? NavView.OpenPaneLength : NavView.CompactPaneLength;
                    h = NavView.ActualHeight;
                }

                double ow = FirstRunOverlay.ActualWidth, oh = FirstRunOverlay.ActualHeight;

                // Every edge of the ring stays on-screen. Edge-flush targets
                // (nav pane, status bar) and taller-than-viewport ones (slot
                // grid, services group) used to push ring sides past the
                // window, which cut them off. Page rings stop above the
                // status bar; the status bar's own ring stops at the window
                // edge instead.
                double statusTop = StatusBarBorder.TransformToVisual(FirstRunOverlay).Transform(new Point(0, 0)).Y;
                double left = Math.Max(origin.X - 4, 2);
                double top = Math.Max(origin.Y - 4, 2);
                double right = Math.Min(origin.X + w + 4, ow - 2);
                double bottom = Math.Min(origin.Y + h + 4,
                    ReferenceEquals(target, StatusBarBorder) ? oh - 2 : statusTop - 6);
                System.Windows.Controls.Canvas.SetLeft(TourHighlight, left);
                System.Windows.Controls.Canvas.SetTop(TourHighlight, top);
                TourHighlight.Width = Math.Max(0, right - left);
                TourHighlight.Height = Math.Max(0, bottom - top);

                double tipX = origin.X + w + 14;
                double tipY = origin.Y;
                if (tipX + 332 > ow)
                {
                    tipX = Math.Max(12, Math.Min(origin.X, ow - 332));
                    tipY = origin.Y + h + 12;
                }
                if (tipY + 200 > oh) tipY = Math.Max(12, oh - 212);
                if (tipY < 12) tipY = 12;
                System.Windows.Controls.Canvas.SetLeft(TourTip, tipX);
                System.Windows.Controls.Canvas.SetTop(TourTip, tipY);
            }
            catch
            {
                // Target not laid out yet; keep the previous position.
            }
        }

        private void CompleteFirstRun()
        {
            FirstRunOverlay.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
            TourCanvas.Visibility = Visibility.Collapsed;
            _tourStep = -1;
            // Persisted in PadForge.xml (the one-time-gate pattern
            // LegacyDriverCleanupOffered established): set at the mutation
            // site, then MarkDirty schedules the save.
            _viewModel.Settings.FirstRunTourCompleted = true;
            _settingsService?.MarkDirty();
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

            ScheduleSelectionGlowReattach();
        }

        private void OnLocationOrSizeChanged(object sender, EventArgs e)
        {
            if (sender != null && e is SizeChangedEventArgs)
                ScheduleSelectionGlowReattach();

            // Only save when in Normal state (Maximized position/size is system-managed).
            if (WindowState != WindowState.Normal) return;
            var mw = _viewModel.Settings;
            mw.MainWindowLeft = Left;
            mw.MainWindowTop = Top;
            mw.MainWindowWidth = Width;
            mw.MainWindowHeight = Height;
            _settingsService.MarkDirty();
        }

        private System.Windows.Threading.DispatcherTimer _glowReattachTimer;

        /// <summary>Rebuilds the selected mini card's glow adorner after a
        /// window state change or resize. The adorner's cached texture and the
        /// adorner layer's arrange can land a frame apart across those
        /// transitions, leaving the pill border drawn at a stale offset until
        /// the next selection change. Debounced so a drag-resize re-attaches
        /// once at the end, not per tick.</summary>
        private void ScheduleSelectionGlowReattach()
        {
            if (_glowSelectedControllerTag == null) return;
            if (_glowReattachTimer == null)
            {
                _glowReattachTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(150) };
                _glowReattachTimer.Tick += (_, _) =>
                {
                    _glowReattachTimer.Stop();
                    if (_glowSelectedControllerTag == null) return;
                    // Defeat the change gate so the same pill re-attaches at
                    // its current geometry.
                    _glowSelectedControllerTag = null;
                    RefreshControllerSelectionVisuals();
                };
            }
            _glowReattachTimer.Stop();
            _glowReattachTimer.Start();
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
                _viewModel.SetStatus(Strings.Instance.Status_NoDeviceToCopyFrom, persist: true);
                return;
            }

            try
            {
                var copyOutputType = padVm.OutputType;
                bool copyIsExtended = copyOutputType is VirtualControllerType.Extended
                    or VirtualControllerType.Nintendo
                    /* raw-surface types always use the dynamic layout */;

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
                var psConfigs = _settingsService.BuildDeviceConfigSnapshotForSlot(padVm.PadIndex);
                if (psConfigs != null && psConfigs.Length > 0)
                    ps.SlotDeviceConfigsJson = System.Text.Json.JsonSerializer.Serialize(psConfigs, jsonOpts);
                var extCfg = _settingsService.BuildExtendedConfigSnapshotForSlot(padVm.PadIndex);
                if (extCfg != null)
                    ps.SlotExtendedConfigJson = System.Text.Json.JsonSerializer.Serialize(extCfg, jsonOpts);
                var midiCfg = _settingsService.BuildMidiConfigSnapshotForSlot(padVm.PadIndex);
                if (midiCfg != null)
                    ps.SlotMidiConfigJson = System.Text.Json.JsonSerializer.Serialize(midiCfg, jsonOpts);
                var kbmCfg = _settingsService.BuildKbmConfigSnapshotForSlot(padVm.PadIndex);
                if (kbmCfg != null)
                    ps.SlotKbmConfigJson = System.Text.Json.JsonSerializer.Serialize(kbmCfg, jsonOpts);

                // Carry the slot's shift authoring (activators + Base appearance)
                // so Copy / Paste includes shift layers like Copy From (#119).
                ps.SlotShiftActivatorsJson = InputService.BuildShiftLayerSnapshotJson(padVm.PadIndex);

                // Carry the slot's menus (#9 B-17) the same way. Menus live on
                // the MappingSet like shift authoring, so Copy must snapshot
                // them or Paste loses the Menus tab.
                ps.SlotMenusJson = InputService.BuildMenusSnapshotJson(padVm.PadIndex);

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
                _viewModel.SetStatus(string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message), persist: true);
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
                    _viewModel.SetStatus(Strings.Instance.Status_InvalidClipboard, persist: true);
                    return;
                }

                var targetType = padVm.OutputType;
                bool targetIsExtended = targetType is VirtualControllerType.Extended
                    or VirtualControllerType.Nintendo
                    /* raw-surface types always use the dynamic layout */;

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

                // Apply the slot's shift authoring (activators + Base appearance)
                // on a same-layout paste, after the row replace above rebuilt the
                // MappingSet. Matches Copy From so shift layers round-trip (#119).
                if (!string.IsNullOrEmpty(ps.SlotShiftActivatorsJson)
                    && MappingTranslation.IsSameLayout(srcType, srcIsExtended, targetType, targetIsExtended))
                {
                    InputService.ApplyShiftLayerSnapshotJson(padVm.PadIndex, ps.SlotShiftActivatorsJson);
                }

                // Restore the slot's menus after the row replace, same gate and
                // ordering as the shift authoring above. Wiped otherwise: the
                // fresh rows-only MappingSet from ApplySlotMappingSetFromRows
                // carries no menus (#9 B-17 clipboard round-trip).
                if (!string.IsNullOrEmpty(ps.SlotMenusJson)
                    && MappingTranslation.IsSameLayout(srcType, srcIsExtended, targetType, targetIsExtended))
                {
                    InputService.ApplyMenusSnapshotJson(padVm.PadIndex, ps.SlotMenusJson);
                }

                _inputService.ApplyPadSettingToCurrentDeviceTranslated(
                    padVm.PadIndex, ps,
                    srcType, srcIsExtended,
                    targetType, targetIsExtended);

                // Unpack the per-slot config tabs that travelled through the
                // clipboard JSON as opaque strings on PadSetting. Same semantics
                // as the in-process Copy From: device features copy
                // unconditionally (physical-device passthrough), Extended /
                // MIDI gated on matching slot type by their Apply methods.
                if (!string.IsNullOrEmpty(ps.SlotDeviceConfigsJson))
                {
                    try
                    {
                        var psConfigs = System.Text.Json.JsonSerializer.Deserialize<ViewModels.DeviceSlotConfigData[]>(ps.SlotDeviceConfigsJson);
                        _settingsService.ApplyDeviceSlotConfigsToSlot(padVm.PadIndex, psConfigs);
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
                if (!string.IsNullOrEmpty(ps.SlotKbmConfigJson))
                {
                    try
                    {
                        var kbmCfg = System.Text.Json.JsonSerializer.Deserialize<ViewModels.KbmSlotConfigData>(ps.SlotKbmConfigJson);
                        _settingsService.ApplyKbmConfigToSlot(padVm.PadIndex, kbmCfg);
                    }
                    catch { /* malformed payload, SOCD paste skipped */ }
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
                    catch { /* malformed payload, per-device paste skipped */ }
                }

                // Rebuild the shift-layer tab strip so pasted layers show up
                // immediately instead of being invisible until relaunch (#119).
                var pastedMs = SettingsManager.SlotMappingSets != null
                    && padVm.PadIndex >= 0 && padVm.PadIndex < SettingsManager.SlotMappingSets.Length
                    ? SettingsManager.SlotMappingSets[padVm.PadIndex] : null;
                padVm.RebuildLayerTabs(pastedMs?.ShiftActivators);
                // Refresh the Menus tab from the restored set so pasted menus
                // show up immediately instead of staying invisible until relaunch.
                padVm.ReloadMenus();
                // Same for the Bass Shakers tab (#236). Note the data flow:
                // the paste deliberately PRESERVES the destination's
                // rumble-audio config (ApplySlotMappingSetFromRows), so the
                // reload re-anchors the card onto the fresh set object that
                // now carries the destination's own config.
                padVm.ReloadRumbleAudio();
                // And the SOCD card (#240), same lifetime.
                padVm.ReloadSocd();

                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_SettingsPasted;
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(string.Format(Strings.Instance.Status_PasteFailed_Format, ex.Message), persist: true);
            }
        }

        // ── Macro clipboard (#112) ──

        private void OnCopyMacro(PadViewModel padVm)
        {
            try
            {
                var macro = padVm.SelectedMacro;
                if (macro == null) return;
                var data = SettingsService.BuildMacroDataForMacro(macro, padVm.PadIndex);
                Clipboard.SetText(SettingsService.SerializeMacrosToClipboard(new[] { data }));
                _viewModel.StatusText = Strings.Instance.Status_MacroCopied;
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(string.Format(Strings.Instance.Status_CopyFailed_Format, ex.Message), persist: true);
            }
        }

        /// <summary>True when ANY slot's layer set declares the mask, so a
        /// copied or pasted macro may keep its scope. Round four (R11)
        /// widened this from destination-only: the runtime gate honors a
        /// mask the macro's own slot does not declare (the split-config
        /// fallback), so the old rule stripped scopes the engine supports,
        /// including on a SAME-slot paste of an imported macro, ungating
        /// it globally. Only a true orphan (no slot declares it) is
        /// stripped, and missing machinery fails OPEN, matching the gate:
        /// keeping a scope is never data loss, destroying one is.</summary>
        private static bool DestinationDeclaresLayer(PadForge.ViewModels.PadViewModel padVm, string mask)
        {
            if (string.IsNullOrEmpty(mask)) return true;
            if (string.Equals(mask, "Base", StringComparison.Ordinal)) return true;
            var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
            if (sets == null) return true; // no layer machinery: fail open, like the gate
            var destSet = padVm != null && padVm.PadIndex >= 0 && padVm.PadIndex < sets.Length
                ? sets[padVm.PadIndex] : null;
            foreach (var set in sets)
            {
                var acts = set?.ShiftActivators;
                if (acts == null) continue;
                bool declares = false;
                foreach (var a in acts)
                {
                    if (a == null) continue;
                    if (string.Equals(a.LayerMask, mask, StringComparison.Ordinal)
                        || PadForge.Common.Input.InputManager.PipeListContains(a.CycleLayers, mask))
                    { declares = true; break; }
                }
                if (!declares) continue;
                // The destination's own set always counts. A FOREIGN slot
                // counts only when it is the same import (round five, X17):
                // keeping a mask an unrelated pad happens to own leaves the
                // macro gated by that pad's controller and unrepresentable
                // in the destination's own picker.
                if (ReferenceEquals(set, destSet)) return true;
                if (destSet != null
                    && PadForge.Common.Input.InputManager.SlotSharesImportDomain(destSet, mask))
                    return true;
            }
            return false;
        }

        private void OnPasteMacro(PadViewModel padVm)
        {
            try
            {
                var env = SettingsService.TryParseMacroClipboard(Clipboard.GetText());
                if (env == null) { _viewModel.SetStatus(Strings.Instance.Status_MacroClipboardInvalid, persist: true); return; }
                MacroItem last = null;
                foreach (var md in env.Macros)
                {
                    var macro = SettingsService.LoadMacroFromData(md, padVm.OutputType, padVm.ExtendedConfig?.ButtonCount, padVm.ProfileId);
                    macro.PadIndex = padVm.PadIndex;
                    // A layer scope belongs to the SOURCE slot's layer set
                    // (audit 2026-07-25, C8). Carrying it across slots left
                    // the copy gated on a foreign slot's layer through the
                    // split-config fallback, and on a slot with no layers
                    // the picker could not show or clear it. Keep the mask
                    // only when the destination declares it.
                    if (!DestinationDeclaresLayer(padVm, macro.LayerMask)) macro.LayerMask = "";
                    padVm.Macros.Add(macro);
                    last = macro;
                }
                if (last != null) padVm.SelectedMacro = last;
                _settingsService.MarkDirty();
                _viewModel.StatusText = Strings.Instance.Status_MacroPasted;
            }
            catch (Exception ex)
            {
                _viewModel.SetStatus(string.Format(Strings.Instance.Status_PasteFailed_Format, ex.Message), persist: true);
            }
        }

        /// <summary>VC-level macro Copy From (#112): pick another virtual controller
        /// and append its macros to this one. Mirrors the Mappings-tab Copy From, which
        /// is VC-to-VC. A copied macro whose trigger is bound to a device not present on
        /// this VC pastes with that trigger unresolved (re-record it), same as Paste.</summary>
        /// <summary>Formats a slot as the Copy From dialog's VC label, e.g.
        /// "Virtual Controller 3 — Xbox 2". Shared by the mapping and macro Copy From
        /// pickers (#112) so they read identically.</summary>
        private static string FormatVcSlotName(int slotIndex, VirtualControllerType outputType)
        {
            int globalNum = SettingsManager.SlotOrders.GetGlobalSlotNumber(slotIndex);
            int inGroupNum = SettingsManager.SlotOrders.GetOrderFor(outputType).IndexOf(slotIndex) + 1;
            string typeName = ControllerTypeDisplayName(outputType);
            string vcWord = Strings.Instance.Main_VirtualController_Format.Replace("{0}", globalNum.ToString());
            return inGroupNum > 0 ? $"{vcWord} — {typeName} {inGroupNum}" : $"{vcWord} — {typeName}";
        }

        private static string ControllerTypeDisplayName(VirtualControllerType t) => t switch
        {
            VirtualControllerType.Xbox          => Strings.Instance.ControllerType_Xbox,
            VirtualControllerType.PlayStation   => Strings.Instance.ControllerType_PlayStation,
            VirtualControllerType.Nintendo      => Strings.Instance.ControllerType_Nintendo,
            VirtualControllerType.Extended      => Strings.Instance.ControllerType_Extended,
            VirtualControllerType.KeyboardMouse => Strings.Instance.ControllerType_KeyboardMouse,
            VirtualControllerType.Midi          => Strings.Instance.ControllerType_MIDI,
            _ => t.ToString(),
        };

        private void OnCopyMacroFrom(PadViewModel padVm)
        {
            // Same dialog, naming, and GUID display as the Mappings-tab Copy From, but
            // listing virtual controllers that have macros rather than mappings (#112).
            var entries = new List<CopyFromDialog.DeviceEntry>();
            for (int i = 0; i < _viewModel.Pads.Count; i++)
            {
                if (i == padVm.PadIndex) continue;
                var src = _viewModel.Pads[i];
                if (src.Macros.Count == 0) continue;

                Guid donor = src.SelectedMappedDevice?.InstanceGuid
                    ?? (src.MappedDevices.FirstOrDefault()?.InstanceGuid ?? Guid.Empty);
                entries.Add(new CopyFromDialog.DeviceEntry
                {
                    Name = FormatVcSlotName(i, src.OutputType),
                    SlotLabel = donor != Guid.Empty ? $"{donor:D}" : string.Empty,
                    LayoutLabel = string.Empty,
                    InstanceGuid = donor,
                    SourceSlot = i,
                });
            }

            if (entries.Count == 0)
            {
                _viewModel.SetStatus(Strings.Instance.Status_MacroNoSource, persist: true);
                return;
            }

            var dialog = new CopyFromDialog(entries) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedEntry == null) return;

            int srcSlot = dialog.SelectedEntry.SourceSlot;
            if (srcSlot < 0 || srcSlot >= _viewModel.Pads.Count) return;
            var source = _viewModel.Pads[srcSlot];

            MacroItem last = null;
            foreach (var macro in source.Macros.ToList())
            {
                var data = SettingsService.BuildMacroDataForMacro(macro, padVm.PadIndex);
                var clone = SettingsService.LoadMacroFromData(data, padVm.OutputType, padVm.ExtendedConfig?.ButtonCount, padVm.ProfileId);
                clone.PadIndex = padVm.PadIndex;
                // Same rule as the paste path above (audit C8).
                if (!DestinationDeclaresLayer(padVm, clone.LayerMask)) clone.LayerMask = "";
                padVm.Macros.Add(clone);
                last = clone;
            }
            if (last != null) padVm.SelectedMacro = last;
            _settingsService.MarkDirty();
            _viewModel.StatusText = Strings.Instance.Status_MacroCopiedFrom;
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
                // Snapshot under the UserSettings lock, then build entries OUTSIDE it.
                // AddEntry's unmapped path calls FindDeviceByInstanceGuid (which takes
                // UserDevices.SyncRoot); holding UserSettings.SyncRoot while doing so
                // inverts the canonical UserDevices->UserSettings order and can deadlock
                // against the dashboard timer.
                List<UserSetting> snapshot;
                lock (SettingsManager.UserSettings.SyncRoot) { snapshot = settings.ToList(); }
                {
                    foreach (var us in snapshot)
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
                                var donorUs = snapshot.FirstOrDefault(
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
                    isExtended = outputType is VirtualControllerType.Extended
                        or VirtualControllerType.Nintendo;
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
                    primary = FormatVcSlotName(us.MapTo, outputType);
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

            if (entries.Count == 0)
            {
                _viewModel.SetStatus(Strings.Instance.Status_NoOtherDevices, persist: true);
                return;
            }

            var dialog = new CopyFromDialog(entries) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedEntry != null)
            {
                var srcEntry = dialog.SelectedEntry;
                var targetOutputType = padVm.OutputType;
                bool targetIsExtended = targetOutputType is VirtualControllerType.Extended
                    or VirtualControllerType.Nintendo
                    /* raw-surface types always use the dynamic layout */;

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
                // Refresh the Menus tab so menus carried by ReplaceSlotMappingSet
                // show up immediately instead of staying invisible until relaunch.
                padVm.ReloadMenus();
                // Same for the Bass Shakers tab (#236): its config rides the
                // copied MappingSet.
                padVm.ReloadRumbleAudio();
                // And the SOCD card (#240), same lifetime.
                padVm.ReloadSocd();

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
            _viewModel.SetStatus(statusMessage, persist: true);
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
                _viewModel.SetStatus(string.Format(Strings.Instance.Status_DriverOperationFailed_Format, ex.Message), persist: true);
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

        // Last MIDI-installed state the drawer cards were rendered against.
        // null until the first status sweep records the baseline.
        private bool? _lastMidiInstalledForNav;

        private void RefreshMidiServicesStatus()
        {
            bool installed = false;
            try
            {
                installed = DriverInstaller.IsMidiServicesInstalled();
                _viewModel.Settings.IsMidiServicesInstalled = installed;
                _viewModel.Dashboard.IsMidiServicesInstalled = installed;
                _viewModel.Settings.MidiServicesVersion = installed ? "Windows MIDI Services" : string.Empty;
            }
            catch
            {
                installed = false;
                _viewModel.Settings.IsMidiServicesInstalled = false;
                _viewModel.Dashboard.IsMidiServicesInstalled = false;
            }

            // The drawer pills reflect MIDI availability only through the MIDI
            // type tile's enabled state. RefreshControllerNavItemsInPlace tears
            // down and rebuilds every pill's whole Content subtree (restarting
            // the breathing heat ring and hover transforms), so calling it on
            // the always-on 5 s lane produced a visible ~5 s "bounce". Rebuild
            // only when the installed state actually flips. The first sweep
            // just records the baseline: the cards were already built against
            // the current state at startup, so no rebuild is needed for it.
            if (_navDashboard != null && _lastMidiInstalledForNav != installed)
            {
                bool firstSweep = _lastMidiInstalledForNav == null;
                _lastMidiInstalledForNav = installed;
                if (!firstSweep) RefreshControllerNavItemsInPlace();
            }
        }

    }
}
