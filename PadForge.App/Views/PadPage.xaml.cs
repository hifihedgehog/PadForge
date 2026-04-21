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
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;

            ApplyViewMode();
            SyncTabStripSelection();
            SyncExtendedConfigBar();
            SyncMidiConfigBar();

            // Re-apply the profile dropdowns' SelectedValue after ItemsSource
            // populates. WPF's ComboBox with SelectedValuePath can land on a
            // null selection when the DataContext switch causes SelectedValue
            // to resolve against an in-flight (pre-populated) ItemsSource —
            // which bites fresh slots whose PadViewModel still holds the
            // default OutputType (Microsoft=0) so OutputType's setter never
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
            // Extended always uses the schematic preview in v3. The old
            // ExtendedConfig.IsGamepadPreset branch was a v2 hold-over that
            // routed Extended slots with a default Xbox 360 preset into
            // the 2D/3D Xbox controller model, which looks wrong for any
            // non-Xbox HIDMaestro profile (wheels, HOTAS, F710, etc.).
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
            bool hideAllGamepadTabs = isMidi;
            var vis = hideAllGamepadTabs ? Visibility.Collapsed : Visibility.Visible;
            // KBM shows Sticks (Mouse X/Y + Scroll) but hides Triggers and FFB
            TabSticks.Visibility = (isMidi) ? Visibility.Collapsed : Visibility.Visible;
            TabTriggers.Visibility = (isMidi || isKbm) ? Visibility.Collapsed : Visibility.Visible;
            TabForceFeedback.Visibility = (isMidi || isKbm) ? Visibility.Collapsed : Visibility.Visible;

            if (MotorBarsGrid != null)
                MotorBarsGrid.Visibility = (isMidi || isKbm) ? Visibility.Collapsed : Visibility.Visible;

            // If on a hidden tab, switch back to Controller tab
            if ((isMidi || isKbm) && DataContext is PadViewModel vm && vm.SelectedConfigTab >= 3)
                vm.SelectedConfigTab = 0;
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
            }
        }

        // ─────────────────────────────────────────────
        //  Extended configuration bar
        // ─────────────────────────────────────────────

        private bool _syncingExtendedConfig;

        private void SyncExtendedConfigBar()
        {
            if (DataContext is not PadViewModel vm) return;

            bool isExtended = vm.OutputType == Engine.VirtualControllerType.Extended;

            // Microsoft / PlayStation use the compact preset dropdown bar; Extended
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

            // Touchpad and rumble caps aren't exposed by HMProfile directly,
            // so leave as user-facing defaults. Rumble routes through
            // HMController.OutputReceived unconditionally — profiles without
            // physical rumble simply never deliver output packets.
            ExtendedTouchpadChk.IsChecked = false;
            ExtendedRumbleChk.IsChecked = true;
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
