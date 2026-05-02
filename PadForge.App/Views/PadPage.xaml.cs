using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class PadPage : UserControl
    {
        /// <summary>
        /// Raised when the user clicks a controller element to start recording.
        /// The string argument is the TargetSettingName (e.g., "ButtonA", "LeftTrigger").
        /// </summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _currentPadVm;

        /// <summary>
        /// Currently-subscribed <see cref="ExtendedSlotConfig"/> for the active
        /// PadViewModel. Tracked separately from <see cref="_currentPadVm"/>
        /// because <see cref="ApplyProfile"/>'s <c>ApplyExtendedConfigs</c> path
        /// mutates <c>cfg.Customize</c> / <c>cfg.OemNameOverride</c> /
        /// <c>cfg.ProductString</c> on the active slot directly, without
        /// changing DataContext or OutputType. We subscribe to PropertyChanged
        /// on the config instance so the Extended config bar refreshes when
        /// those fields move under us. See recipe
        /// <c>extended-config-bar-profile-switch-stale-ui-recipe.md</c> /
        /// issue #73.
        /// </summary>
        private PadForge.ViewModels.ExtendedSlotConfig _currentExtendedConfig;

        /// <summary>Currently-subscribed PlayStationSlotConfig so we can
        /// keep the HEX color textbox in sync with slider drags. Same
        /// shape as <see cref="_currentExtendedConfig"/>.</summary>
        private PadForge.ViewModels.PlayStationSlotConfig _currentPlayStationConfig;

        public PadPage()
        {
            InitializeComponent();
            Loaded += PadPage_Loaded;
            DataContextChanged += OnDataContextChanged;
        }

        private void PadPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyViewMode();
            SyncTabStripSelection();
            SyncExtendedConfigBar();
            SyncMidiConfigBar();
            SyncLightbarHexBox();
            SyncAudioHexBoxes();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged -= OnMappedDevicesChanged;
            }

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged += OnMappedDevicesChanged;
            }

            // Track the active slot's ExtendedSlotConfig so we can refresh the
            // Extended config bar when a profile switch mutates its fields
            // without changing DataContext or OutputType (issue #73). The
            // config instance is stable for the lifetime of a PadViewModel —
            // no external code reassigns the property — so subscribing here
            // and tearing down on the next DataContext change is enough.
            if (_currentExtendedConfig != null)
                _currentExtendedConfig.PropertyChanged -= OnExtendedConfigBarPropertyChanged;
            _currentExtendedConfig = _currentPadVm?.ExtendedConfig;
            if (_currentExtendedConfig != null)
                _currentExtendedConfig.PropertyChanged += OnExtendedConfigBarPropertyChanged;

            // Mirror the same subscription pattern for PlayStationSlotConfig
            // so the HEX textbox follows RGB slider drags (and any other
            // external mutation).  PlayStationConfig is stable for the
            // PadViewModel's lifetime — no external code reassigns it.
            if (_currentPlayStationConfig != null)
                _currentPlayStationConfig.PropertyChanged -= OnPlayStationConfigChanged;
            _currentPlayStationConfig = _currentPadVm?.PlayStationConfig;
            if (_currentPlayStationConfig != null)
                _currentPlayStationConfig.PropertyChanged += OnPlayStationConfigChanged;

            ApplyViewMode();
            SyncTabStripSelection();
            SyncExtendedConfigBar();
            SyncMidiConfigBar();
            SyncLightbarHexBox();
            SyncAudioHexBoxes();

            // Re-apply the profile dropdowns' SelectedValue after ItemsSource
            // populates. WPF's ComboBox with SelectedValuePath can land on a
            // null selection when the DataContext switch causes SelectedValue
            // to resolve against an in-flight (pre-populated) ItemsSource —
            // which bites fresh slots whose PadViewModel still holds the
            // default OutputType (Xbox=0) so OutputType's setter never
            // raised AvailableProfiles during CreateSlot. Deferring to
            // Loaded-priority lets WPF's binding system populate ItemsSource
            // first, then we force SelectedValue to re-resolve from source.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HMaestroProfileCombo?
                    .GetBindingExpression(System.Windows.Controls.ComboBox.SelectedValueProperty)?
                    .UpdateTarget();
                ExtendedProfileCombo?
                    .GetBindingExpression(System.Windows.Controls.ComboBox.SelectedValueProperty)?
                    .UpdateTarget();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ─────────────────────────────────────────────
        //  2D / 3D Model View
        // ─────────────────────────────────────────────

        private SettingsViewModel GetSettingsVm()
        {
            if (Application.Current.MainWindow?.DataContext is MainViewModel mainVm)
                return mainVm.Settings;
            return null;
        }

        private void ViewModeToggle_Click(object sender, RoutedEventArgs e)
        {
            var settingsVm = GetSettingsVm();
            if (settingsVm != null)
                settingsVm.Use2DControllerView = !settingsVm.Use2DControllerView;
            ApplyViewMode();
        }

        private bool IsExtended()
        {
            // Extended always uses the schematic preview, sized to the
            // active HIDMaestro profile.
            return DataContext is PadViewModel vm
                && vm.OutputType == Engine.VirtualControllerType.Extended;
        }

        private bool IsMidi()
        {
            return DataContext is PadViewModel vm && vm.OutputType == Engine.VirtualControllerType.Midi;
        }

        private bool IsKBM()
        {
            return DataContext is PadViewModel vm && vm.OutputType == Engine.VirtualControllerType.KeyboardMouse;
        }

        private void ApplyViewMode()
        {
            if (ControllerModel3D == null || ControllerModel2D == null || ControllerSchematic == null || MidiPreview == null || KBMPreview == null) return;

            bool isMidi = IsMidi();
            bool isKBM = IsKBM();
            bool isSchematic = IsExtended();
            bool is2D = GetSettingsVm()?.Use2DControllerView ?? false;

            if (isKBM)
            {
                // KB+Mouse: show KBM preview, hide everything else
                ControllerModel3D.Visibility = Visibility.Collapsed;
                ControllerModel2D.Visibility = Visibility.Collapsed;
                ControllerSchematic.Visibility = Visibility.Collapsed;
                MidiPreview.Visibility = Visibility.Collapsed;
                KBMPreview.Visibility = Visibility.Visible;
                ViewModeToggle.Visibility = Visibility.Collapsed;
            }
            else if (isMidi)
            {
                // MIDI: show MIDI preview, hide everything else
                ControllerModel3D.Visibility = Visibility.Collapsed;
                ControllerModel2D.Visibility = Visibility.Collapsed;
                ControllerSchematic.Visibility = Visibility.Collapsed;
                MidiPreview.Visibility = Visibility.Visible;
                KBMPreview.Visibility = Visibility.Collapsed;
                ViewModeToggle.Visibility = Visibility.Collapsed;
            }
            else if (isSchematic)
            {
                // Custom Extended: show schematic view, hide 2D/3D toggle
                ControllerModel3D.Visibility = Visibility.Collapsed;
                ControllerModel2D.Visibility = Visibility.Collapsed;
                ControllerSchematic.Visibility = Visibility.Visible;
                MidiPreview.Visibility = Visibility.Collapsed;
                KBMPreview.Visibility = Visibility.Collapsed;
                ViewModeToggle.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Gamepad preset: standard 2D/3D toggle
                ControllerSchematic.Visibility = Visibility.Collapsed;
                MidiPreview.Visibility = Visibility.Collapsed;
                KBMPreview.Visibility = Visibility.Collapsed;
                ControllerModel3D.Visibility = is2D ? Visibility.Collapsed : Visibility.Visible;
                ControllerModel2D.Visibility = is2D ? Visibility.Visible : Visibility.Collapsed;
                ViewModeToggle.Visibility = Visibility.Visible;

                // E8B9 = Photo/flat icon (shown in 3D mode, click to switch TO 2D)
                // F158 = 3D/cube icon (shown in 2D mode, click to switch TO 3D)
                ViewModeIcon.Text = is2D ? "\uF158" : "\uE8B9";
                ViewModeToggle.ToolTip = is2D ? Strings.Instance.Pad_SwitchTo3D : Strings.Instance.Pad_SwitchTo2D;
            }

            SyncTabVisibility();
            BindActiveModelView();
        }

        private void SyncTabVisibility()
        {
            if (TabSticks == null || TabTriggers == null || TabForceFeedback == null) return;

            bool isKbm = IsKBM();
            bool isMidi = IsMidi();
            // KBM shows Sticks (Mouse X/Y + Scroll) but hides Triggers; MIDI
            // hides both Sticks and Triggers because its mapping surface is
            // CC + note, not stick/trigger.
            TabSticks.Visibility = isMidi ? Visibility.Collapsed : Visibility.Visible;
            TabTriggers.Visibility = (isMidi || isKbm) ? Visibility.Collapsed : Visibility.Visible;

            // Adaptive Triggers, Lighting, and Force Feedback tabs all
            // reflect what the currently-SELECTED physical device on this
            // slot can do. Slots can have multiple devices assigned; the
            // user picks which one's mappings they're editing via the
            // device dropdown, and the configuration tabs follow that
            // selection so a user editing the Xbox controller side of a
            // "DS5 + Xbox both mapped to one slot" setup doesn't see
            // DualSense-specific tabs. When they switch the dropdown to
            // the DualSense, the tabs reappear.
            //
            // Adaptive Triggers: selected device is a DualSense or
            //   DualSense Edge (Sony VID 0x054C, PID 0x0CE6 or 0x0DF2).
            // Lighting: above plus DS4 (PIDs 0x05C4, 0x09CC, 0x0BA0).
            // Force Feedback: selected device's CapType is a stick-class
            //   input (Gamepad / Joystick / Driving / Flight / FirstPerson).
            //   Keyboards / mice / touchpads / MIDI controllers don't
            //   have FFB endpoints, so the tab would be a no-op there.
            bool hasAdaptiveTriggers = false;
            bool hasLightbar = false;
            bool hasForceFeedback = false;
            if (DataContext is PadViewModel vmProfile
                && vmProfile.SelectedMappedDevice != null
                && vmProfile.SelectedMappedDevice.InstanceGuid != Guid.Empty
                && SettingsManager.UserDevices != null)
            {
                Guid selectedGuid = vmProfile.SelectedMappedDevice.InstanceGuid;
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                    {
                        if (ud == null) continue;
                        if (ud.InstanceGuid != selectedGuid) continue;

                        hasForceFeedback =
                            ud.CapType == InputDeviceType.Gamepad
                            || ud.CapType == InputDeviceType.Joystick
                            || ud.CapType == InputDeviceType.Driving
                            || ud.CapType == InputDeviceType.Flight
                            || ud.CapType == InputDeviceType.FirstPerson;

                        if (ud.VendorId == 0x054C)
                        {
                            bool isDualSense = ud.ProdId == 0x0CE6;
                            bool isDualSenseEdge = ud.ProdId == 0x0DF2;
                            bool isDs4 = ud.ProdId == 0x05C4 || ud.ProdId == 0x09CC || ud.ProdId == 0x0BA0;
                            hasAdaptiveTriggers = isDualSense || isDualSenseEdge;
                            hasLightbar = isDualSense || isDualSenseEdge || isDs4;
                        }
                        break;
                    }
                }
            }
            TabForceFeedback.Visibility = hasForceFeedback ? Visibility.Visible : Visibility.Collapsed;
            if (TabAdaptiveTriggers != null)
                TabAdaptiveTriggers.Visibility = hasAdaptiveTriggers ? Visibility.Visible : Visibility.Collapsed;
            if (TabLighting != null)
                TabLighting.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;

            if (MotorBarsGrid != null)
                MotorBarsGrid.Visibility = Visibility.Visible;

            // SelectedConfigTab tag values: 0 Controller, 1 Macros, 2 Mappings,
            // 3 Sticks, 4 Triggers, 5 Force Feedback, 6 Adaptive Triggers,
            // 7 Lighting. Macros, Mappings, and Force Feedback are visible
            // for every VC type. MIDI hides Sticks and Triggers; K+M hides
            // Triggers only. Adaptive Triggers and Lighting are gated on
            // profile capability above. Kick the user back to the Controller
            // tab if they're sitting on a now-hidden one.
            if (DataContext is PadViewModel vm)
            {
                if (isMidi && (vm.SelectedConfigTab == 3 || vm.SelectedConfigTab == 4))
                    vm.SelectedConfigTab = 0;
                else if (isKbm && vm.SelectedConfigTab == 4)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 6 && !hasAdaptiveTriggers)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 7 && !hasLightbar)
                    vm.SelectedConfigTab = 0;
            }
        }

        private void BindActiveModelView()
        {
            bool isMidi = IsMidi();
            bool isKBM = IsKBM();
            bool isSchematic = IsExtended();
            bool is2D = GetSettingsVm()?.Use2DControllerView ?? false;

            // Unbind all first
            ControllerModel3D.Unbind();
            ControllerModel2D.Unbind();
            ControllerSchematic.Unbind();
            MidiPreview.Unbind();
            KBMPreview.Unbind();

            if (DataContext is not PadViewModel vm) return;

            if (isKBM)
            {
                KBMPreview.ControllerElementRecordRequested -= OnModelRecordRequested;
                KBMPreview.ControllerElementRecordRequested += OnModelRecordRequested;
                KBMPreview.Bind(vm);
            }
            else if (isMidi)
            {
                MidiPreview.ControllerElementRecordRequested -= OnModelRecordRequested;
                MidiPreview.ControllerElementRecordRequested += OnModelRecordRequested;
                MidiPreview.Bind(vm);
            }
            else if (isSchematic)
            {
                ControllerSchematic.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerSchematic.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerSchematic.Bind(vm);
            }
            else if (is2D)
            {
                ControllerModel2D.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerModel2D.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerModel2D.Bind(vm);
            }
            else
            {
                ControllerModel3D.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerModel3D.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerModel3D.Bind(vm);
            }
        }

        private void OnModelRecordRequested(object sender, string targetName)
        {
            ControllerElementRecordRequested?.Invoke(this, targetName);
        }

        // ─────────────────────────────────────────────
        //  Custom tab strip
        // ─────────────────────────────────────────────

        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && TryGetTagIndex(rb, out int idx) && DataContext is PadViewModel vm)
                vm.SelectedConfigTab = idx;
        }

        private void SyncTabStripSelection()
        {
            if (DataContext is not PadViewModel vm) return;
            int selected = vm.SelectedConfigTab;

            foreach (var rb in FindVisualChildren<RadioButton>(this))
            {
                if (rb.GroupName == "PadTab" && TryGetTagIndex(rb, out int idx))
                    rb.IsChecked = idx == selected;
            }
        }

        private static bool TryGetTagIndex(FrameworkElement el, out int index)
        {
            if (el.Tag is int i) { index = i; return true; }
            if (el.Tag is string s && int.TryParse(s, out i)) { index = i; return true; }
            index = -1;
            return false;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var desc in FindVisualChildren<T>(child))
                    yield return desc;
            }
        }

        // ─────────────────────────────────────────────
        //  Motor test (click) + hover highlight
        // ─────────────────────────────────────────────

        private void Motor_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
                el.Opacity = 0.7;
        }

        private void Motor_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
                el.Opacity = 1.0;
        }

        private void LeftMotor_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.FireTestLeftMotor();
        }

        private void RightMotor_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.FireTestRightMotor();
        }

        // ─────────────────────────────────────────────
        //  Map All stop button
        // ─────────────────────────────────────────────

        private void MapAllStop_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
                padVm.StopMapAll();
        }

        private void MapAllToggle_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PadViewModel padVm)
            {
                if (padVm.IsMapAllActive)
                    padVm.StopMapAll();
                else if (padVm.MapAllCommand.CanExecute(null))
                    padVm.MapAllCommand.Execute(null);
            }
        }

        private void CalibrateCenter_Click(object sender, RoutedEventArgs e)
        {
            if (((System.Windows.Controls.Button)sender).DataContext is ViewModels.StickConfigItem item)
                item.StartCalibration();
        }

        // ─────────────────────────────────────────────
        //  ViewModel property changed
        // ─────────────────────────────────────────────

        private void OnPadVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.SelectedConfigTab))
                SyncTabStripSelection();
            else if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                SyncExtendedConfigBar();
                SyncMidiConfigBar();
                ApplyViewMode();
            }
            else if (e.PropertyName == nameof(PadViewModel.SelectedMappedDevice))
            {
                // Tabs reflect the selected physical device; refresh on
                // dropdown change.
                SyncTabVisibility();
            }
            else if (e.PropertyName == nameof(PadViewModel.ProfileId))
            {
                // When the user picks a new Extended profile, re-seed every
                // field in the config bar (Name/VID/PID plus layout counts)
                // so the UI reflects the selected profile's identity and
                // capabilities. Without this the fields keep the previous
                // profile's values and only refresh on slot switch.
                if (DataContext is PadViewModel vm
                    && vm.OutputType == Engine.VirtualControllerType.Extended)
                {
                    _syncingExtendedConfig = true;
                    SyncExtendedFields(vm);
                    _syncingExtendedConfig = false;
                }

                // Adaptive Triggers and Lighting tab visibility depend on
                // the active profile's VID/PID (DualSense / DualSense Edge
                // / DS4 capability). A profile switch within the same
                // PlayStation slot type doesn't fire OutputType change, so
                // SyncTabVisibility wouldn't run otherwise — leaving the
                // tabs stale until app relaunch or slot switch. Re-sync
                // here so the tab strip follows profile changes too.
                SyncTabVisibility();
            }
        }

        // ─────────────────────────────────────────────
        //  Extended configuration bar
        // ─────────────────────────────────────────────

        private bool _syncingExtendedConfig;

        /// <summary>
        /// Refreshes the Extended config bar when the active slot's
        /// <see cref="PadForge.ViewModels.ExtendedSlotConfig"/> mutates from
        /// outside the bar's own UI events — currently only the
        /// <c>ApplyProfile</c> path during profile switching. <c>OnDataContextChanged</c>
        /// and the <c>OutputType</c> PropertyChanged trigger already handle
        /// the slot-switch and type-switch cases, so we only need to react
        /// to the three fields ApplyExtendedConfigs writes through:
        /// Customize, OemNameOverride, ProductString.
        ///
        /// <para>The <see cref="_syncingExtendedConfig"/> guard short-circuits
        /// when SyncExtendedFields is mid-flight. SyncExtendedFields writes
        /// only to UI controls (no model writes), so PropertyChanged on the
        /// config instance shouldn't fire from inside it — but the guard is
        /// kept as a defensive belt-and-braces against any indirect path
        /// that might cycle back through SetProperty.</para>
        /// </summary>
        private void OnExtendedConfigBarPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_syncingExtendedConfig) return;

            if (e.PropertyName == nameof(PadForge.ViewModels.ExtendedSlotConfig.Customize)
                || e.PropertyName == nameof(PadForge.ViewModels.ExtendedSlotConfig.OemNameOverride)
                || e.PropertyName == nameof(PadForge.ViewModels.ExtendedSlotConfig.ProductString))
            {
                SyncExtendedConfigBar();
            }
        }

        private void SyncExtendedConfigBar()
        {
            if (DataContext is not PadViewModel vm) return;

            bool isExtended = vm.OutputType == Engine.VirtualControllerType.Extended;

            // Xbox / PlayStation use the compact preset dropdown bar; Extended
            // has its own full config bar with profile + override fields, so
            // hide the compact bar when Extended is active.
            HMaestroProfileBar.Visibility = (vm.HasHMaestroProfileBar && !isExtended)
                ? Visibility.Visible
                : Visibility.Collapsed;

            ExtendedConfigBar.Visibility = isExtended ? Visibility.Visible : Visibility.Collapsed;

            if (isExtended)
            {
                _syncingExtendedConfig = true;
                SyncExtendedFields(vm);
                _syncingExtendedConfig = false;
            }
        }

        private void SyncExtendedFields(PadViewModel vm)
        {
            if (vm?.ExtendedConfig == null) return;

            // Resolve the active HIDMaestro profile and drive every field in
            // the Extended config bar from its metadata. The profile IS the
            // VC's identity in v3 — all fields reflect it directly rather
            // than the v2 Extended per-slot overrides.
            var profile = vm.AvailableProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, vm.ProfileId, System.StringComparison.OrdinalIgnoreCase));

            // ProductString is the OS-visible identity — written to the
            // device registry's iProduct, surfaced to joy.cpl and games via
            // IOCTL_HID_GET_STRING, and used as the Device Manager
            // FriendlyName fallback. HMProfile.Name is catalog-only (SDK
            // search + console). Populate the textbox from ProductString so
            // the value shown is what downstream consumers will see, with
            // Name as a fallback for profiles whose ProductString is unset.
            // Prefer a persisted per-slot ProductString (user-edited) if set;
            // otherwise seed from the active profile's catalog value so the
            // field always shows the OS-visible identity. Falls back to
            // profile.Name for catalog entries where ProductString is unset.
            string persistedProductString = vm.ExtendedConfig?.ProductString ?? string.Empty;
            string profileProductString = !string.IsNullOrEmpty(profile?.ProductString)
                ? profile.ProductString
                : profile?.Name ?? string.Empty;
            ExtendedProductStringBox.Text = !string.IsNullOrEmpty(persistedProductString)
                ? persistedProductString
                : profileProductString;
            ExtendedVidBox.Text = profile != null ? $"0x{profile.VendorId:X4}" : string.Empty;
            ExtendedPidBox.Text = profile != null ? $"0x{profile.ProductId:X4}" : string.Empty;
            ExtendedOemOverrideChk.IsChecked = vm.ExtendedConfig?.OemNameOverride == true;
            ExtendedCustomizeChk.IsChecked = vm.ExtendedConfig?.Customize == true;

            if (profile != null)
            {
                // Layout counts derived from the profile's HID descriptor.
                // HMProfile exposes total AxisCount, ButtonCount, HasHat.
                // Sticks/triggers split is not directly exposed by the SDK,
                // so use the standard gamepad convention: first four axes
                // pair into two sticks (LX/LY/RX/RY), remaining axes are
                // triggers. Works for typical gamepads (6 axes → 2+2);
                // degenerate cases (joysticks with 2-3 axes) collapse to
                // 1 stick + remainder triggers.
                int axes = profile.AxisCount;
                int sticks = System.Math.Min(axes, 4) / 2;
                int triggers = System.Math.Max(0, axes - sticks * 2);

                ExtendedStickCountBox.Text = sticks.ToString();
                ExtendedTriggerCountBox.Text = triggers.ToString();
                ExtendedPovCountBox.Text = (profile.HasHat ? 1 : 0).ToString();
                ExtendedButtonCountBox.Text = profile.ButtonCount.ToString();
            }
            else
            {
                // No profile resolved (e.g. catalog not loaded yet) — fall
                // back to the persisted ExtendedConfig so the UI has something
                // to show rather than blank fields.
                ExtendedStickCountBox.Text = vm.ExtendedConfig.ThumbstickCount.ToString();
                ExtendedTriggerCountBox.Text = vm.ExtendedConfig.TriggerCount.ToString();
                ExtendedPovCountBox.Text = vm.ExtendedConfig.PovCount.ToString();
                ExtendedButtonCountBox.Text = vm.ExtendedConfig.ButtonCount.ToString();
            }

            // Touchpad caps aren't exposed by HMProfile, so the touchpad
            // checkbox is currently informational and defaults to false. The
            // FFB checkbox is bound two-way to ExtendedConfig.ForceFeedbackEnabled
            // (XAML), so its state is restored from the active slot's config
            // automatically; no forced default needed here.
            ExtendedTouchpadChk.IsChecked = false;
        }

        private void ExtendedOverride_Changed(object sender, RoutedEventArgs e)
        {
            // Persist the user-edited Product String to the slot's
            // ExtendedConfig. When OEM Name Override is active, Step 5 uses
            // this value as the label for HMOemNameOverride.Set at VC-create
            // time. VID/PID are profile-defined and not user-editable — those
            // textboxes are display-only for the active profile.
            if (_syncingExtendedConfig) return;
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null) return;
            if (sender == ExtendedProductStringBox)
                vm.ExtendedConfig.ProductString = ExtendedProductStringBox.Text ?? string.Empty;
        }

        private void ExtendedOverride_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ExtendedOverride_Changed(sender, e);
        }

        // ─────────────────────────────────────────────
        //  Lighting tab — HEX color entry
        // ─────────────────────────────────────────────

        /// <summary>Refreshes tab visibility when the slot's
        /// MappedDevices collection changes — covers user
        /// assigning/unassigning devices via the Devices page.</summary>
        private void OnMappedDevicesChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SyncTabVisibility();
        }

        private void OnPlayStationConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            // Keep the HEX textboxes live-synced with the RGB sliders.
            // Skip the refresh while the user is mid-edit in the textbox
            // itself — *_Apply is what's writing the properties at that
            // moment, and overwriting Text would fight the caret position.
            switch (e.PropertyName)
            {
                case nameof(ViewModels.PlayStationSlotConfig.LightbarRed):
                case nameof(ViewModels.PlayStationSlotConfig.LightbarGreen):
                case nameof(ViewModels.PlayStationSlotConfig.LightbarBlue):
                    if (LightbarHexBox != null && !LightbarHexBox.IsKeyboardFocusWithin)
                        SyncLightbarHexBox();
                    break;
                case nameof(ViewModels.PlayStationSlotConfig.AudioLowR):
                case nameof(ViewModels.PlayStationSlotConfig.AudioLowG):
                case nameof(ViewModels.PlayStationSlotConfig.AudioLowB):
                    if (AudioLowHexBox != null && !AudioLowHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioLowHexBox, "Low");
                    break;
                case nameof(ViewModels.PlayStationSlotConfig.AudioMidR):
                case nameof(ViewModels.PlayStationSlotConfig.AudioMidG):
                case nameof(ViewModels.PlayStationSlotConfig.AudioMidB):
                    if (AudioMidHexBox != null && !AudioMidHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioMidHexBox, "Mid");
                    break;
                case nameof(ViewModels.PlayStationSlotConfig.AudioHighR):
                case nameof(ViewModels.PlayStationSlotConfig.AudioHighG):
                case nameof(ViewModels.PlayStationSlotConfig.AudioHighB):
                    if (AudioHighHexBox != null && !AudioHighHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioHighHexBox, "High");
                    break;
            }
        }

        // ── Audio threshold HEX boxes — generic Tag-based handlers ──
        // Each TextBox in the XAML carries Tag="Low" / "Mid" / "High"
        // identifying which color triplet it edits. One set of handlers
        // covers all three; logic mirrors LightbarHexBox_Apply.

        private void SyncAudioHexBoxes()
        {
            SyncOneAudioHex(AudioLowHexBox, "Low");
            SyncOneAudioHex(AudioMidHexBox, "Mid");
            SyncOneAudioHex(AudioHighHexBox, "High");
        }

        private void SyncOneAudioHex(System.Windows.Controls.TextBox box, string tag)
        {
            if (box == null) return;
            if (DataContext is not PadViewModel vm || vm.PlayStationConfig == null) return;
            var (r, g, b) = ReadAudioRgb(vm.PlayStationConfig, tag);
            box.Text = $"{r:X2}{g:X2}{b:X2}";
        }

        private static (byte r, byte g, byte b) ReadAudioRgb(
            ViewModels.PlayStationSlotConfig cfg, string tag) => tag switch
        {
            "Low"  => (cfg.AudioLowR,  cfg.AudioLowG,  cfg.AudioLowB),
            "Mid"  => (cfg.AudioMidR,  cfg.AudioMidG,  cfg.AudioMidB),
            "High" => (cfg.AudioHighR, cfg.AudioHighG, cfg.AudioHighB),
            _ => (0, 0, 0),
        };

        private static void WriteAudioRgb(
            ViewModels.PlayStationSlotConfig cfg, string tag, byte r, byte g, byte b)
        {
            switch (tag)
            {
                case "Low":  cfg.AudioLowR  = r; cfg.AudioLowG  = g; cfg.AudioLowB  = b; break;
                case "Mid":  cfg.AudioMidR  = r; cfg.AudioMidG  = g; cfg.AudioMidB  = b; break;
                case "High": cfg.AudioHighR = r; cfg.AudioHighG = g; cfg.AudioHighB = b; break;
            }
        }

        private void AudioHexBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox box)
                AudioHexBox_Apply(box);
        }

        private void AudioHexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox box)
                AudioHexBox_Apply(box);
        }

        private void AudioHexBox_Apply(System.Windows.Controls.TextBox box)
        {
            if (DataContext is not PadViewModel vm || vm.PlayStationConfig == null) return;
            string tag = box.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;

            string text = (box.Text ?? string.Empty).Trim();
            if (text.StartsWith("#")) text = text.Substring(1);

            if (text.Length == 6
                && byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte b))
            {
                WriteAudioRgb(vm.PlayStationConfig, tag, r, g, b);
            }

            SyncOneAudioHex(box, tag);
        }

        /// <summary>Populates the HEX textbox from the current
        /// PlayStationConfig RGB. Called from DataContextChanged so
        /// switching slots loads the right value, and from
        /// PadPage_Loaded for the initial paint.</summary>
        private void SyncLightbarHexBox()
        {
            if (LightbarHexBox == null) return;
            if (DataContext is not PadViewModel vm || vm.PlayStationConfig == null) return;
            LightbarHexBox.Text = $"{vm.PlayStationConfig.LightbarRed:X2}{vm.PlayStationConfig.LightbarGreen:X2}{vm.PlayStationConfig.LightbarBlue:X2}";
        }

        /// <summary>Parses a HEX color string (with or without leading #)
        /// and writes the components back into PlayStationConfig. The
        /// per-channel sliders auto-update via their TwoWay bindings on
        /// the same observable properties, so no extra UI poke is needed.
        /// Invalid input is silently ignored — the textbox is restored
        /// to the current canonical RGB hex on next focus loss.</summary>
        private void LightbarHexBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                LightbarHexBox_Apply();
        }

        private void LightbarHexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            LightbarHexBox_Apply();
        }

        private void LightbarHexBox_Apply()
        {
            if (LightbarHexBox == null) return;
            if (DataContext is not PadViewModel vm || vm.PlayStationConfig == null) return;

            string text = (LightbarHexBox.Text ?? string.Empty).Trim();
            if (text.StartsWith("#")) text = text.Substring(1);

            if (text.Length == 6
                && byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte b))
            {
                vm.PlayStationConfig.LightbarRed = r;
                vm.PlayStationConfig.LightbarGreen = g;
                vm.PlayStationConfig.LightbarBlue = b;
            }

            // Always reformat the textbox to canonical RRGGBB. Catches
            // both successful parse (echo back normalized form) and
            // failed parse (revert to current truth).
            LightbarHexBox.Text = $"{vm.PlayStationConfig.LightbarRed:X2}{vm.PlayStationConfig.LightbarGreen:X2}{vm.PlayStationConfig.LightbarBlue:X2}";
        }

        private void ExtendedOemOverride_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingExtendedConfig) return;
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null) return;
            vm.ExtendedConfig.OemNameOverride = ExtendedOemOverrideChk.IsChecked == true;
        }

        private void ExtendedCustomize_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingExtendedConfig) return;
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null) return;
            vm.ExtendedConfig.Customize = ExtendedCustomizeChk.IsChecked == true;
        }

        private void ExtendedResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null) return;

            // Resolve the active catalog profile. Every override field below
            // is snapped back to that profile's declared value — the user
            // gets a clean slate matching what HIDMaestro would build if
            // Customize were turned off.
            var profile = vm.AvailableProfiles?.FirstOrDefault(p =>
                string.Equals(p.Id, vm.ProfileId, System.StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;

            int axes = profile.AxisCount;
            int sticks = System.Math.Min(axes, 4) / 2;
            int triggers = System.Math.Max(0, axes - sticks * 2);

            // Write the config first (fires property-changed → persist +
            // triggers Pass 1 destroy/rebuild when Customize is active and
            // the values differ from the applied snapshot). _syncingExtendedConfig
            // blocks the nested SyncExtendedFields call from re-firing these
            // setters through the textbox LostFocus path.
            _syncingExtendedConfig = true;
            try
            {
                vm.ExtendedConfig.ProductString = !string.IsNullOrEmpty(profile.ProductString)
                    ? profile.ProductString
                    : profile.Name ?? string.Empty;
                vm.ExtendedConfig.ThumbstickCount = sticks;
                vm.ExtendedConfig.TriggerCount = triggers;
                vm.ExtendedConfig.PovCount = profile.HasHat ? 1 : 0;
                vm.ExtendedConfig.ButtonCount = profile.ButtonCount;
                vm.ExtendedConfig.OemNameOverride = false;
            }
            finally { _syncingExtendedConfig = false; }

            // Refresh the UI from the freshly-reset config so the textboxes
            // and checkbox reflect the new state.
            SyncExtendedFields(vm);
        }

        /// <summary>
        /// Swallow arrow keys when the preset dropdown is closed. Without this,
        /// the ComboBox handles Up/Down/Left/Right to cycle selections even
        /// with focus held implicitly, which collides with keyboard keys a
        /// user has mapped as input source — pressing Up to drive their
        /// virtual controller would also cycle the preset dropdown.
        /// When the dropdown IS open, arrow keys pass through as normal so
        /// explicit navigation of the list still works.
        /// </summary>
        private void ProfileCombo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not ComboBox cb || cb.IsDropDownOpen) return;
            if (e.Key == Key.Up || e.Key == Key.Down
                || e.Key == Key.Left || e.Key == Key.Right
                || e.Key == Key.PageUp || e.Key == Key.PageDown
                || e.Key == Key.Home || e.Key == Key.End)
            {
                e.Handled = true;
            }
        }

        private void ExtendedCustomValue_Changed(object sender, RoutedEventArgs e)
        {
            ApplyExtendedCustomValues();
        }

        private void ExtendedCustomValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyExtendedCustomValues();
        }

        private void ApplyExtendedCustomValues()
        {
            if (DataContext is not PadViewModel vm) return;

            if (int.TryParse(ExtendedStickCountBox.Text, out int sticks))
                vm.ExtendedConfig.ThumbstickCount = sticks;
            if (int.TryParse(ExtendedTriggerCountBox.Text, out int triggers))
                vm.ExtendedConfig.TriggerCount = triggers;
            if (int.TryParse(ExtendedPovCountBox.Text, out int povs))
                vm.ExtendedConfig.PovCount = povs;
            if (int.TryParse(ExtendedButtonCountBox.Text, out int buttons))
                vm.ExtendedConfig.ButtonCount = buttons;

            // Reflect clamped values back into text boxes
            ExtendedStickCountBox.Text = vm.ExtendedConfig.ThumbstickCount.ToString();
            ExtendedTriggerCountBox.Text = vm.ExtendedConfig.TriggerCount.ToString();
            ExtendedPovCountBox.Text = vm.ExtendedConfig.PovCount.ToString();
            ExtendedButtonCountBox.Text = vm.ExtendedConfig.ButtonCount.ToString();
        }

        private void ExtendedImportBtn_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm) return;

            var mainWindow = Application.Current.MainWindow as MainWindow;
            var settingsService = mainWindow?.SettingsService;
            if (settingsService == null) return;

            var dialog = new ManageProfilesDialog(settingsService)
            {
                Owner = Application.Current.MainWindow
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ImportedProfileId))
            {
                // Auto-select a newly-imported profile on the current slot.
                // Catalog.Reload already ran inside AddUserProfile so the
                // Extended dropdown has the new id before this assignment
                // hits the binding. Dialog returns false on plain close
                // (no import); in that path we don't touch the slot.
                vm.ProfileId = dialog.ImportedProfileId;
            }
        }

        // ─────────────────────────────────────────────
        //  MIDI configuration bar
        // ─────────────────────────────────────────────

        private bool _syncingMidiConfig;

        private void SyncMidiConfigBar()
        {
            if (DataContext is not PadViewModel vm) return;

            bool isMidi = vm.OutputType == Engine.VirtualControllerType.Midi;
            MidiConfigBar.Visibility = isMidi ? Visibility.Visible : Visibility.Collapsed;

            if (isMidi)
            {
                _syncingMidiConfig = true;
                MidiChannelBox.Text = vm.MidiConfig.Channel.ToString();
                MidiCcCountBox.Text = vm.MidiConfig.CcCount.ToString();
                MidiStartCcBox.Text = vm.MidiConfig.StartCc.ToString();
                MidiNoteCountBox.Text = vm.MidiConfig.NoteCount.ToString();
                MidiStartNoteBox.Text = vm.MidiConfig.StartNote.ToString();
                MidiVelocityBox.Text = vm.MidiConfig.Velocity.ToString();
                _syncingMidiConfig = false;
            }
        }

        private void MidiConfig_Changed(object sender, RoutedEventArgs e) => ApplyMidiConfigValues();

        private void MidiConfig_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ApplyMidiConfigValues();
        }

        private void ApplyMidiConfigValues()
        {
            if (DataContext is not PadViewModel vm) return;
            if (_syncingMidiConfig) return;

            int oldCcCount = vm.MidiConfig.CcCount;
            int oldNoteCount = vm.MidiConfig.NoteCount;
            int oldStartCc = vm.MidiConfig.StartCc;
            int oldStartNote = vm.MidiConfig.StartNote;

            if (int.TryParse(MidiChannelBox.Text, out int ch))
                vm.MidiConfig.Channel = ch;
            // Set start values first — they re-clamp counts automatically
            if (int.TryParse(MidiStartCcBox.Text, out int startCc))
                vm.MidiConfig.StartCc = startCc;
            if (int.TryParse(MidiCcCountBox.Text, out int ccCount))
                vm.MidiConfig.CcCount = ccCount;
            if (int.TryParse(MidiStartNoteBox.Text, out int startNote))
                vm.MidiConfig.StartNote = startNote;
            if (int.TryParse(MidiNoteCountBox.Text, out int noteCount))
                vm.MidiConfig.NoteCount = noteCount;
            if (byte.TryParse(MidiVelocityBox.Text, out byte vel))
                vm.MidiConfig.Velocity = vel;

            // Reflect clamped values
            MidiChannelBox.Text = vm.MidiConfig.Channel.ToString();
            MidiCcCountBox.Text = vm.MidiConfig.CcCount.ToString();
            MidiStartCcBox.Text = vm.MidiConfig.StartCc.ToString();
            MidiNoteCountBox.Text = vm.MidiConfig.NoteCount.ToString();
            MidiStartNoteBox.Text = vm.MidiConfig.StartNote.ToString();
            MidiVelocityBox.Text = vm.MidiConfig.Velocity.ToString();

            // Reinitialize mapping rows when counts or start numbers change
            if (vm.MidiConfig.CcCount != oldCcCount || vm.MidiConfig.NoteCount != oldNoteCount ||
                vm.MidiConfig.StartCc != oldStartCc || vm.MidiConfig.StartNote != oldStartNote)
                vm.RebuildMappings();
        }

        // ─────────────────────────────────────────────
        //  Sensitivity curve presets
        // ─────────────────────────────────────────────

        private static string FindPresetSerialized(string displayName)
        {
            return CurveLut.FindSerializedByDisplayName(displayName);
        }

        private void StickPresetX_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is StickConfigItem item)
            {
                var serialized = FindPresetSerialized(name);
                if (serialized != null) item.SensitivityCurveX = serialized;
            }
        }

        private void StickPresetY_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is StickConfigItem item)
            {
                var serialized = FindPresetSerialized(name);
                if (serialized != null) item.SensitivityCurveY = serialized;
            }
        }

        private void TriggerPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is TriggerConfigItem item)
            {
                var serialized = FindPresetSerialized(name);
                if (serialized != null) item.SensitivityCurve = serialized;
            }
        }

        // ─────────────────────────────────────────────
        //  AppVolume process dropdown
        // ─────────────────────────────────────────────

        private void AppVolumeProcessDropDown_Opened(object sender, EventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is MacroAction action)
                action.RefreshAudioProcessesCommand.Execute(null);
        }

        /// <summary>
        /// Populates the device axis picker ComboBox with devices assigned to the current slot.
        /// </summary>
        private void DeviceAxisPicker_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not ComboBox cb || _currentPadVm == null)
                return;

            int slotIndex = _currentPadVm.PadIndex;
            var devices = new List<PadForge.Engine.Data.UserDevice>();

            foreach (var setting in SettingsManager.UserSettings.Items)
            {
                if (setting.MapTo != slotIndex)
                    continue;
                var ud = SettingsManager.UserDevices.Items
                    .Find(d => d.InstanceGuid == setting.InstanceGuid);
                if (ud != null && !devices.Contains(ud))
                    devices.Add(ud);
            }

            cb.ItemsSource = devices;
        }

        /// <summary>
        /// Populates the axis index picker ComboBox with axis-type DeviceObjects
        /// from the device selected in SourceDeviceGuid.
        /// </summary>
        private void DeviceAxisIndexPicker_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not ComboBox cb || cb.DataContext is not MacroAction action)
                return;

            if (action.SourceDeviceGuid == Guid.Empty)
            {
                cb.ItemsSource = null;
                return;
            }

            var ud = SettingsManager.UserDevices.Items
                .Find(d => d.InstanceGuid == action.SourceDeviceGuid);
            if (ud?.DeviceObjects == null)
            {
                cb.ItemsSource = null;
                return;
            }

            var axes = new List<AxisPickerItem>();
            foreach (var obj in ud.DeviceObjects)
            {
                if (obj.IsAxis)
                    axes.Add(new AxisPickerItem(obj.InputIndex, Common.MappingDisplayResolver.LocalizeObjectName(obj.Name)));
            }
            cb.ItemsSource = axes;
        }
    }

    /// <summary>Lightweight wrapper for device axis combo items with localized display name.</summary>
    internal class AxisPickerItem
    {
        public AxisPickerItem(int inputIndex, string displayName)
        {
            InputIndex = inputIndex;
            DisplayName = displayName;
        }
        public int InputIndex { get; }
        public string DisplayName { get; }
        public override string ToString() => DisplayName;
    }
}
