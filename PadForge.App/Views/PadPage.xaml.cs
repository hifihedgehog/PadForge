using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        /// <summary>RecorderService reference set by MainWindow at startup
        /// so the shift activator dialog's Record buttons can drive a
        /// freeform recording session through the existing infrastructure.
        /// Static so the dialog handlers can read it without threading a
        /// service reference through every code path.</summary>
        public static PadForge.Services.RecorderService Recorder { get; set; }

        /// <summary>Static InputService reference set by MainWindow, used
        /// by the Touchpad tab's recorder dialog + delete-gesture handlers
        /// to add / remove custom gestures from the active profile.</summary>
        public static PadForge.Services.InputService InputService { get; set; }

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
            Unloaded += PadPage_Unloaded;
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
            // Loaded can fire again without a paired Unloaded when the
            // element re-enters the tree — unsubscribe first so handlers
            // never stack.
            PadForge.Common.SoundPackageManager.RegistryChanged -= OnSoundPackageRegistryChanged;
            PadForge.Common.SoundPackageManager.RegistryChanged += OnSoundPackageRegistryChanged;
            RefreshSoundPackages();
        }

        private void PadPage_Unloaded(object sender, RoutedEventArgs e)
        {
            PadForge.Common.SoundPackageManager.RegistryChanged -= OnSoundPackageRegistryChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged -= OnMappedDevicesChanged;
                _currentPadVm.RecordTouchpadGestureRequested -= OnRecordTouchpadGestureRequested;
                _currentPadVm.DeleteTouchpadGestureRequested -= OnDeleteTouchpadGestureRequested;
            }

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged += OnMappedDevicesChanged;
                _currentPadVm.RecordTouchpadGestureRequested += OnRecordTouchpadGestureRequested;
                _currentPadVm.DeleteTouchpadGestureRequested += OnDeleteTouchpadGestureRequested;
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
            bool hasIndicatorLeds = false;
            bool hasForceFeedback = false;
            bool hasGyro = false;
            bool hasIrPointer = false; // #146 Wii Remote IR camera -> Pointer tab
            bool hasImpulseTriggers = false;
            bool hasRumble = false;
            bool hasTouchpad = false;
            bool hasWheel = false;
            bool hasGenericWheel = false;
            // Controller speaker audio is a Sony-only feature: only the DualSense
            // family and the DualShock 4 have a speaker. Same gating shape as
            // adaptive triggers (DualSense) or impulse triggers (Xbox One+).
            bool hasAudio = false;
            int numTouchpads = 0;
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

                        hasGyro = ud.HasGyro;
                        hasIrPointer = ud.HasIrCamera;
                        hasImpulseTriggers = ud.HasRumbleTriggers;
                        hasTouchpad = ud.HasTouchpad;
                        // Native-FFB wheel → the Wheel tab (rotation range, auto-center,
                        // RPM LEDs). Same VID/PID gates the wheel HID writers use.
                        hasWheel =
                            PadForge.Common.Input.LogitechRawHidWriter.IsLogitechWheel(ud.VendorId, ud.ProdId)
                         || PadForge.Common.Input.FanatecRawHidWriter.IsFanatecWheel(ud.VendorId, ud.ProdId)
                         || PadForge.Common.Input.ThrustmasterRawHidWriter.IsThrustmasterWheel(ud.VendorId, ud.ProdId);
                        // Generic (non-vendor) FFB wheel routed through SDL: no native range
                        // or RPM-LED support, but a single-axis spring-capable haptic still
                        // self-centers from the Auto Centering slider (TryApplyAutoCenterSpring).
                        // Show the Wheel tab with only the auto-center row in that case.
                        hasGenericWheel =
                            !hasWheel
                         && ud.CapType == InputDeviceType.Driving
                         && ud.Device != null
                         && ud.Device.HasHaptic
                         && ud.Device.NumHapticAxes <= 1
                         && (ud.Device.HapticFeatures & SDL3.SDL.SDL_HAPTIC_SPRING) != 0;
                        // Pad count drives the Touchpad tab's per-pad
                        // pivot. Most devices = 1; Steam Controller 2026
                        // = 2 (Triton); original Steam Controller = 3.
                        if (hasTouchpad)
                        {
                            try
                            {
                                var st = ud.Device?.GetCurrentState();
                                numTouchpads = st?.Touchpads?.Length ?? 1;
                                if (numTouchpads <= 0) numTouchpads = 1;
                            }
                            catch { numTouchpads = 1; }
                        }

                        if (ud.VendorId == 0x054C)
                        {
                            bool isDualSense = ud.ProdId == 0x0CE6;
                            bool isDualSenseEdge = ud.ProdId == 0x0DF2;
                            bool isDs4 = ud.ProdId == 0x05C4 || ud.ProdId == 0x09CC || ud.ProdId == 0x0BA0;
                            hasAdaptiveTriggers = isDualSense || isDualSenseEdge;
                            hasLightbar = isDualSense || isDualSenseEdge || isDs4;
                            // Speaker audio: DualSense family + DS4 (all have a speaker).
                            hasAudio = isDualSense || isDualSenseEdge || isDs4;
                            // Indicator LEDs (player row + mic LED + brightness)
                            // are DualSense-family only. DS4 has neither.
                            hasIndicatorLeds = isDualSense || isDualSenseEdge;
                        }
                        else if (PadForge.Common.Input.WiiSpeakerService.DeviceHasSpeaker(ud))
                        {
                            // Wii Remote built-in speaker (#146): macro sounds
                            // play through it like the Sony controller speaker.
                            hasAudio = true;
                        }
                        else if (PadForge.Common.Input.HapticToneService.DeviceHasHaptics(ud))
                        {
                            // Switch HD Rumble / Steam Controller haptic tones (#147):
                            // macro sounds play as tones through the actuator.
                            hasAudio = true;
                        }
                        // Grip-motor rumble: modern Xbox (impulse-trigger devices), the
                        // Sony lightbar family (DualSense / Edge / DS4 all rumble), and any
                        // generic SDL gamepad reporting rumble (covers Xbox 360 etc.).
                        hasRumble = hasImpulseTriggers
                                 || hasLightbar
                                 || (ud.Device != null && ud.Device.HasRumble);
                        break;
                    }
                }
            }
            TabForceFeedback.Visibility = hasForceFeedback ? Visibility.Visible : Visibility.Collapsed;
            if (TabAdaptiveTriggers != null)
                TabAdaptiveTriggers.Visibility = hasAdaptiveTriggers ? Visibility.Visible : Visibility.Collapsed;
            if (TabLighting != null)
                TabLighting.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;
            if (TabGyro != null)
                TabGyro.Visibility = hasGyro ? Visibility.Visible : Visibility.Collapsed;
            if (TabImpulseTriggers != null)
                TabImpulseTriggers.Visibility = hasImpulseTriggers ? Visibility.Visible : Visibility.Collapsed;
            if (TabTouchpad != null)
                TabTouchpad.Visibility = hasTouchpad ? Visibility.Visible : Visibility.Collapsed;
            if (TabAudio != null)
                TabAudio.Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed;
            if (TabPointer != null)
                TabPointer.Visibility = hasIrPointer ? Visibility.Visible : Visibility.Collapsed;
            if (TabWheel != null)
                TabWheel.Visibility = (hasWheel || hasGenericWheel) ? Visibility.Visible : Visibility.Collapsed;
            // Rotation range + RPM LEDs are vendor-HID-only; hide them for a generic
            // SDL wheel that only supports the software auto-center spring.
            if (WheelRangeRow != null)
                WheelRangeRow.Visibility = hasWheel ? Visibility.Visible : Visibility.Collapsed;
            if (WheelRpmRow != null)
                WheelRpmRow.Visibility = hasWheel ? Visibility.Visible : Visibility.Collapsed;
            if (IndicatorLedsCard != null)
                IndicatorLedsCard.Visibility = hasIndicatorLeds ? Visibility.Visible : Visibility.Collapsed;

            // Steering Lock Feedback channels — expose each only on a device that can
            // do it. Trigger vibration = Xbox impulse triggers OR DualSense trigger
            // haptics; resistance = DualSense adaptive triggers only; lightbar pulse =
            // DualSense/DS4 lightbar; rumble pulse = grip-motor rumble. Hide the whole
            // card when the selected device can't do any of them.
            bool hasTriggerVib = hasImpulseTriggers || hasAdaptiveTriggers;
            bool anyLockChannel = hasRumble || hasTriggerVib || hasLightbar || hasAdaptiveTriggers;
            if (LockFeedbackCard != null)
                LockFeedbackCard.Visibility = anyLockChannel ? Visibility.Visible : Visibility.Collapsed;
            if (LockRumbleRow != null)
                LockRumbleRow.Visibility = hasRumble ? Visibility.Visible : Visibility.Collapsed;
            if (LockTriggerVibRow != null)
                LockTriggerVibRow.Visibility = hasTriggerVib ? Visibility.Visible : Visibility.Collapsed;
            if (LockLightbarRow != null)
                LockLightbarRow.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;
            if (LockResistanceRow != null)
                LockResistanceRow.Visibility = hasAdaptiveTriggers ? Visibility.Visible : Visibility.Collapsed;
            // Pulse length drives the rumble/trigger pulse channels (the lightbar has its
            // own Hold + Decay; resistance is continuous, no pulse).
            if (LockPulseSection != null)
                LockPulseSection.Visibility = (hasRumble || hasTriggerVib) ? Visibility.Visible : Visibility.Collapsed;
            if (LockLightbarSection != null)
                LockLightbarSection.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;

            // Sync the per-pad pivot to the active device. PadViewModel
            // recomputes MaxTouchpadIndex / SelectedTouchpadIndex and
            // triggers a settings reload for the new (device, pad).
            if (DataContext is PadViewModel vmTouch && hasTouchpad)
            {
                vmTouch.RecomputeTouchpadCountForActiveDevice(numTouchpads);
                vmTouch.LoadTouchpadGestureSettingsForActiveDevice();
            }
            else if (DataContext is PadViewModel vmNoTouch)
            {
                vmNoTouch.RecomputeTouchpadCountForActiveDevice(0);
            }

            if (MotorBarsGrid != null)
                MotorBarsGrid.Visibility = Visibility.Visible;

            // SelectedConfigTab tag values: 0 Controller, 1 Macros, 2 Mappings,
            // 3 Sticks, 4 Triggers, 5 Force Feedback, 6 Adaptive Triggers,
            // 7 Lighting, 8 Gyro, 9 Impulse Triggers, 10 Touchpad, 11 Wheel.
            // Macros, Mappings, and
            // Force Feedback are visible for every VC type. MIDI hides
            // Sticks and Triggers; K+M hides Triggers only. Adaptive
            // Triggers, Lighting, Gyro, and Impulse Triggers are gated on
            // the selected device's capabilities above. Kick the user back
            // to the Controller tab if they're sitting on a now-hidden one.
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
                else if (vm.SelectedConfigTab == 8 && !hasGyro)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 9 && !hasImpulseTriggers)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 10 && !hasTouchpad)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 11 && !hasWheel)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 12 && !hasAudio) // 12 = Audio
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 13 && !hasIrPointer) // 13 = Pointer
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

        /// <summary>The config TabControl uses a header-less template, so WPF's
        /// built-in Ctrl+Tab handling in <c>TabControl.OnKeyDown</c> throws
        /// "Value cannot be null (container)" out of <c>IndexFromContainer</c>
        /// (discussion #140). Catch Ctrl+Tab here in the tunneling PreviewKeyDown,
        /// before that bubbling handler runs, swallow it, and cycle the visible
        /// tab strip ourselves instead.</summary>
        private void ConfigTabControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;
            e.Handled = true;
            try { CycleConfigTab((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }
            catch { /* tab navigation must never crash the app */ }
        }

        /// <summary>Move to the next (or previous) visible config tab, wrapping.
        /// Cycles the visible tab-strip RadioButtons so it matches the tabs shown
        /// for this controller type / device. Setting <c>SelectedConfigTab</c>
        /// updates the content and, via PropertyChanged, the strip highlight.</summary>
        private void CycleConfigTab(bool backward)
        {
            if (DataContext is not PadViewModel vm) return;
            var tags = new List<int>();
            foreach (var rb in FindVisualChildren<RadioButton>(this))
                if (rb.GroupName == "PadTab" && rb.IsVisible && rb.IsEnabled && TryGetTagIndex(rb, out int idx))
                    tags.Add(idx);
            if (tags.Count == 0) return;
            int cur = tags.IndexOf(vm.SelectedConfigTab);
            int next = cur < 0
                ? 0
                : (((cur + (backward ? -1 : 1)) % tags.Count) + tags.Count) % tags.Count;
            vm.SelectedConfigTab = tags[next];
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
        //  Shift mode UI handlers (Issue #61 Phase 6)
        //
        //  + Shift Layer button opens the modal dialog, on Save splices
        //  the new activator into SettingsManager.SlotMappingSets[N].
        //  ShiftActivators and rebuilds the tab strip. Tab clicks set
        //  PadViewModel.ActiveLayerMask which raises LayerActivated;
        //  InputService.OnLayerActivated reloads MappingItems from the
        //  selected layer's MappingRows.
        // ─────────────────────────────────────────────

        // ─────────────────────────────────────────────
        //  Sound macro action handlers (issue #83)
        // ─────────────────────────────────────────────

        /// <summary>Audio tab hub row: jump to the Macros tab with the
        /// clicked sound macro selected.</summary>
        private void SoundMacroRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is PadForge.ViewModels.MacroItem macro)
                _currentPadVm?.OpenSoundMacro(macro);
        }

        /// <summary>Re-enumerate render endpoints right before the mirror
        /// source dropdown opens, so hot-plugged devices show up.</summary>
        private void MirrorSource_DropDownOpened(object sender, EventArgs e)
            => _currentPadVm?.RefreshMirrorSources();

        /// <summary>Choose a sound for the Play Sound action card. When packages
        /// are added, the sounds inside them are offered directly — a filesystem
        /// browse is only needed for a package or loose file that hasn't been
        /// added yet (issue #83). The button's DataContext is the MacroAction.</summary>
        private void BrowseSoundFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PadForge.ViewModels.MacroAction action)
                return;

            var packages = PadForge.Common.SoundPackageManager.Packages;
            if (packages.Count > 0)
            {
                var items = new System.Collections.Generic.List<PickSoundDialog.Item>();
                foreach (var p in packages)
                    foreach (var entry in PadForge.Common.SoundPackageManager.ListSounds(p.Name))
                        items.Add(new PickSoundDialog.Item(
                            $"{System.IO.Path.GetFileName(entry)}  —  {p.Name}",
                            PadForge.Common.SoundPackageManager.MakeRef(p.Name, entry)));

                if (items.Count > 0)
                {
                    var picker = new PickSoundDialog(
                        PadForge.Resources.Strings.Strings.Instance.Macro_Sound_Pick_Description,
                        items, allowBrowse: true, preselectValue: action.SoundFilePath)
                    { Owner = Window.GetWindow(this) };
                    if (picker.ShowDialog() != true) return;
                    if (!picker.BrowseRequested)
                    {
                        if (!string.IsNullOrEmpty(picker.SelectedSound))
                            action.SoundFilePath = picker.SelectedSound;
                        return;
                    }
                    // "Browse files…" — fall through to the filesystem dialog.
                }
            }

            BrowseSoundFileFromDisk(action);
        }

        /// <summary>Filesystem browse for a loose sound file or a <c>.pfsounds</c>
        /// package. Picking a package registers it and offers its sounds; the
        /// action then stores <c>pfsound://Package/entry</c> so a shared profile
        /// resolves on any machine that carries the package file.</summary>
        private void BrowseSoundFileFromDisk(PadForge.ViewModels.MacroAction action)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.Macro_Sound_File_Label,
                Filter = "Audio files and sound packages|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.pfsounds"
                       + "|Sound packages (*.pfsounds)|*.pfsounds|All files|*.*",
                CheckFileExists = true,
            };
            try
            {
                if (!string.IsNullOrEmpty(action.SoundFilePath)
                    && !PadForge.Common.SoundPackageManager.IsPackageRef(action.SoundFilePath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(action.SoundFilePath);
            }
            catch { }
            if (dlg.ShowDialog() != true) return;

            if (dlg.FileName.EndsWith(PadForge.Common.SoundPackageManager.FileExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                string pkg = PadForge.Common.SoundPackageManager.Register(dlg.FileName);
                if (pkg == null) return;
                var sounds = PadForge.Common.SoundPackageManager.ListSounds(pkg);
                if (sounds.Count == 0) return;
                string entry = sounds.Count == 1
                    ? sounds[0]
                    : PromptPickFromList(
                        string.Format(PadForge.Resources.Strings.Strings.Instance.Macro_Sound_PickFromPackage_Format, pkg),
                        sounds);
                if (entry != null)
                    action.SoundFilePath = PadForge.Common.SoundPackageManager.MakeRef(pkg, entry);
                return;
            }

            action.SoundFilePath = dlg.FileName;
        }

        /// <summary>Modal list picker (package sound selection). Returns
        /// the chosen item or null. FluentWindow chrome, same as the other
        /// dialogs.</summary>
        private string PromptPickFromList(string title, System.Collections.Generic.List<string> items)
        {
            var dlg = new PickSoundDialog(title, items) { Owner = Window.GetWindow(this) };
            return dlg.ShowDialog() == true ? dlg.SelectedSound : null;
        }

        // ─────────────────────────────────────────────
        //  Sound Packages card
        // ─────────────────────────────────────────────

        private void OnSoundPackageRegistryChanged(object sender, EventArgs e)
        {
            // The registry can change from non-UI code paths (profile import).
            Dispatcher.BeginInvoke(new Action(RefreshSoundPackages));
        }

        private void RefreshSoundPackages()
        {
            var packages = PadForge.Common.SoundPackageManager.Packages;
            SoundPackagesList.ItemsSource = packages;
            SoundPackagesEmptyText.Visibility = packages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PackageAdd_Click(object sender, RoutedEventArgs e)
        {
            string ext = PadForge.Common.SoundPackageManager.FileExtension;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.Pad_Audio_Packages_Add,
                Filter = $"PadForge sound packages (*{ext})|*{ext}|All files|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            foreach (string file in dlg.FileNames)
                PadForge.Common.SoundPackageManager.Register(file);
        }

        private void PackageCreate_Click(object sender, RoutedEventArgs e)
        {
            var pick = new Microsoft.Win32.OpenFileDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.Pad_Audio_Packages_PickSounds,
                Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg|All files|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (pick.ShowDialog() != true || pick.FileNames.Length == 0) return;

            string ext = PadForge.Common.SoundPackageManager.FileExtension;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.Pad_Audio_Packages_Create,
                FileName = "Sounds" + ext,
                Filter = $"PadForge sound packages (*{ext})|*{ext}",
            };
            if (save.ShowDialog() != true) return;

            string displayName = System.IO.Path.GetFileNameWithoutExtension(save.FileName);
            if (PadForge.Common.SoundPackageManager.ExportPackage(save.FileName, displayName, pick.FileNames))
                PadForge.Common.SoundPackageManager.Register(save.FileName);
        }

        private void PackageRemove_Click(object sender, RoutedEventArgs e)
        {
            if (SoundPackagesList.SelectedItem is PadForge.Common.SoundPackageManager.PackageRef pkg)
                PadForge.Common.SoundPackageManager.Unregister(pkg.Name);
        }

        /// <summary>Preview the action's sound through the pad's configured
        /// output device, at the action's volume (no loop).</summary>
        private void PreviewSoundFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PadForge.ViewModels.MacroAction action)
                return;
            int slot = _currentPadVm?.PadIndex ?? 0;
            PadForge.Common.Input.SoundMacroService.Play(slot, null, action.SoundFilePath, action.SoundVolume, loop: false);
        }

        private void AddShiftLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;

            // Pull the cross-device InputChoice list off the first
            // MappingItem on the slot — InputService.PopulateAvailableInputs
            // populates them identically across rows. Filter to button-class
            // inputs only (v1 activator kind is Button; v2 axis kind comes
            // through a different picker).
            var available = new List<PadForge.ViewModels.InputChoice>();
            var first = _currentPadVm.Mappings.FirstOrDefault();
            if (first?.AvailableInputs != null)
            {
                foreach (var c in first.AvailableInputs)
                {
                    if (c == null) continue;
                    // Pass every choice — the dialog filters internally per
                    // Kind selection (buttons + POVs for Button/Chord;
                    // axes + sliders for Axis).
                    available.Add(c);
                }
            }

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            var existing = slotMs?.ShiftActivators
                ?? new System.Collections.Generic.List<Engine.Data.ShiftActivator>();

            var dlg = new ShiftActivatorDialog(available, existing: null, otherActivators: existing,
                recorder: Recorder, padIndex: _currentPadVm.PadIndex)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            // Splice the new activator into the slot's MappingSet, rebuild
            // the tab strip, switch to the new tab, mark settings dirty.
            slotMs = GetOrCreateSlotMappingSet(_currentPadVm.PadIndex);
            slotMs.ShiftActivators ??= new System.Collections.Generic.List<Engine.Data.ShiftActivator>();
            slotMs.ShiftActivators.Add(dlg.Result);
            _currentPadVm.RebuildLayerTabs(slotMs.ShiftActivators);
            _currentPadVm.ActiveLayerMask = dlg.Result.LayerMask;
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        private void ShiftLayerTab_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            if (sender is not RadioButton rb) return;
            string mask = rb.Tag as string;
            if (string.IsNullOrEmpty(mask)) return;
            _currentPadVm.ActiveLayerMask = mask;
        }

        // ─────────────────────────────────────────────
        //  Shift-layer context-menu handlers (v1.7)
        //
        //  Each handler reads the target layer's LayerMask from the
        //  MenuItem.Tag, then operates against the live MappingSet for
        //  the current slot. Mutations call padVm.ConfigItemDirtyCallback
        //  so the settings file persists the change on the next autosave.
        // ─────────────────────────────────────────────

        /// <summary>Static cross-layer clipboard for Copy / Paste of layer
        /// rows. Static so a user can copy from one slot's layer and paste
        /// into another. Cleared on app exit; not persisted to disk.</summary>
        private static System.Collections.Generic.List<Engine.Data.MappingRow> _shiftLayerClipboard;

        private static string TagToLayerMask(object sender)
        {
            if (sender is FrameworkElement fe && fe.Tag is string s) return s;
            return null;
        }

        private void ShiftLayer_Configure_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;

            // Base has no activator. Configure edits its flyout/tab appearance
            // (name + icon + color), stored on the MappingSet (#119).
            if (string.Equals(mask, "Base", StringComparison.Ordinal))
            {
                var baseMs = GetOrCreateSlotMappingSet(_currentPadVm.PadIndex);
                var bdlg = new ShiftActivatorDialog(
                    baseMs.BaseLayerName ?? "", baseMs.BaseColor ?? "", baseMs.BaseIcon ?? "")
                {
                    Owner = Window.GetWindow(this),
                };
                if (bdlg.ShowDialog() != true || bdlg.Result == null) return;
                baseMs.BaseLayerName = bdlg.Result.LayerName ?? "";
                baseMs.BaseColor = bdlg.Result.Color ?? "";
                baseMs.BaseIcon = bdlg.Result.Icon ?? "";
                _currentPadVm.RebuildLayerTabs(baseMs.ShiftActivators);
                _currentPadVm.ConfigItemDirtyCallback?.Invoke();
                return;
            }

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.ShiftActivators == null) return;
            var existing = slotMs.ShiftActivators.Find(
                a => a != null && string.Equals(a.LayerMask, mask, StringComparison.Ordinal));
            if (existing == null) return;

            var available = new System.Collections.Generic.List<PadForge.ViewModels.InputChoice>();
            var first = _currentPadVm.Mappings.FirstOrDefault();
            if (first?.AvailableInputs != null)
            {
                foreach (var c in first.AvailableInputs)
                {
                    if (c == null) continue;
                    var d = c.Descriptor ?? "";
                    if (d.StartsWith("Button ", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("POV ", StringComparison.OrdinalIgnoreCase))
                        available.Add(c);
                }
            }

            // For Configure, pass the OTHER activators (all except the one
            // being edited) so the duplicate-name validation doesn't reject
            // the activator's own current name.
            var others = new System.Collections.Generic.List<Engine.Data.ShiftActivator>();
            foreach (var a in slotMs.ShiftActivators)
                if (a != null && !ReferenceEquals(a, existing)) others.Add(a);

            var dlg = new ShiftActivatorDialog(available, existing, others,
                recorder: Recorder, padIndex: _currentPadVm.PadIndex)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            // Apply edits in-place. LayerMask may change when the user
            // renames; if so, retag every MappingRow on the old mask to
            // the new mask so the existing authoring stays attached.
            string oldMask = existing.LayerMask;
            existing.LayerName = dlg.Result.LayerName;
            existing.LayerMask = dlg.Result.LayerMask;
            existing.DeviceGuid = dlg.Result.DeviceGuid;
            existing.Descriptor = dlg.Result.Descriptor;
            existing.Mode = dlg.Result.Mode;
            existing.Kind = dlg.Result.Kind;
            existing.InheritUnmapped = dlg.Result.InheritUnmapped;
            existing.ChordSecondDeviceGuid = dlg.Result.ChordSecondDeviceGuid;
            existing.ChordSecondDescriptor = dlg.Result.ChordSecondDescriptor;
            existing.AxisThreshold = dlg.Result.AxisThreshold;
            existing.JumpToLayer = dlg.Result.JumpToLayer;
            existing.CycleLayers = dlg.Result.CycleLayers;
            existing.CyclePrevDeviceGuid = dlg.Result.CyclePrevDeviceGuid;
            existing.CyclePrevDescriptor = dlg.Result.CyclePrevDescriptor;
            existing.CycleWrap = dlg.Result.CycleWrap;
            existing.CycleIncludeBase = dlg.Result.CycleIncludeBase;
            existing.DelayMs = dlg.Result.DelayMs;
            existing.Color = dlg.Result.Color;
            existing.Icon = dlg.Result.Icon;
            existing.PostponeMapping = dlg.Result.PostponeMapping;

            if (!string.Equals(oldMask, existing.LayerMask, StringComparison.Ordinal)
                && slotMs.Rows != null)
            {
                foreach (var r in slotMs.Rows)
                {
                    if (r != null && string.Equals(r.LayerMask, oldMask, StringComparison.Ordinal))
                        r.LayerMask = existing.LayerMask;
                }
            }

            _currentPadVm.RebuildLayerTabs(slotMs.ShiftActivators);
            _currentPadVm.ActiveLayerMask = existing.LayerMask;
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        // ─────────────────────────────────────────────
        //  Inline rename popup
        //
        //  Rename is a name-only change — pop a tiny anchored input next to
        //  the clicked tab rather than reopening the full Configure dialog.
        //  Save commits LayerName (and retags rows when the derived
        //  LayerMask shifts); Cancel restores nothing.
        // ─────────────────────────────────────────────

        private string _renameTargetMask;

        private void ShiftLayer_Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.ShiftActivators == null) return;
            var existing = slotMs.ShiftActivators.Find(
                a => a != null && string.Equals(a.LayerMask, mask, StringComparison.Ordinal));
            if (existing == null) return;

            _renameTargetMask = mask;
            RenameLayerBox.Text = string.IsNullOrEmpty(existing.LayerName)
                ? (existing.LayerMask ?? "")
                : existing.LayerName;
            RenameLayerHint.Visibility = Visibility.Collapsed;

            // Anchor the popup to the clicked tab's RadioButton if we can
            // find it; falls back to the strip itself so the popup is never
            // detached from the page.
            var anchor = FindTabRadioButton(mask) ?? (FrameworkElement)ShiftLayerTabStrip;
            RenameLayerPopup.PlacementTarget = anchor;
            RenameLayerPopup.IsOpen = true;

            // Defer focus + select-all to after the popup's been laid out.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                RenameLayerBox.Focus();
                RenameLayerBox.SelectAll();
            });
        }

        private void RenameLayerBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                RenameLayerSave_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                RenameLayerCancel_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void RenameLayerSave_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null || string.IsNullOrEmpty(_renameTargetMask)) return;
            string newName = (RenameLayerBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(newName))
            {
                RenameLayerHint.Text = PadForge.Resources.Strings.Strings.Instance.Pad_Shift_HintNameRequired;
                RenameLayerHint.Visibility = Visibility.Visible;
                RenameLayerBox.Focus();
                return;
            }

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.ShiftActivators == null) { RenameLayerPopup.IsOpen = false; return; }

            var existing = slotMs.ShiftActivators.Find(
                a => a != null && string.Equals(a.LayerMask, _renameTargetMask, StringComparison.Ordinal));
            if (existing == null) { RenameLayerPopup.IsOpen = false; return; }

            // Reject a name already used by another activator on this slot.
            foreach (var a in slotMs.ShiftActivators)
            {
                if (a == null || ReferenceEquals(a, existing)) continue;
                if (string.Equals(a.LayerName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    RenameLayerHint.Text = PadForge.Resources.Strings.Strings.Instance.Pad_Shift_HintNameDuplicate;
                    RenameLayerHint.Visibility = Visibility.Visible;
                    RenameLayerBox.Focus();
                    RenameLayerBox.SelectAll();
                    return;
                }
            }

            existing.LayerName = newName;
            // The LayerMask is the persisted identity; leave it alone so
            // Base/Rows stay attached. (Configure dialog rebuilds the mask
            // from name; Rename keeps mask stable to avoid retag work.)

            _currentPadVm.RebuildLayerTabs(slotMs.ShiftActivators);
            _currentPadVm.ActiveLayerMask = existing.LayerMask;
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();

            _renameTargetMask = null;
            RenameLayerPopup.IsOpen = false;
        }

        private void RenameLayerCancel_Click(object sender, RoutedEventArgs e)
        {
            _renameTargetMask = null;
            RenameLayerPopup.IsOpen = false;
        }

        private System.Windows.Controls.Primitives.ButtonBase FindTabRadioButton(string mask)
        {
            // Walk the visual tree of the ItemsControl looking for the
            // RadioButton whose Tag equals the target mask. The
            // ItemContainerGenerator returns ContentPresenters; the actual
            // RadioButton lives inside the DataTemplate.
            for (int i = 0; i < ShiftLayerTabStrip.Items.Count; i++)
            {
                var container = ShiftLayerTabStrip.ItemContainerGenerator.ContainerFromIndex(i);
                if (container == null) continue;
                var rb = FindDescendant<System.Windows.Controls.RadioButton>(container);
                if (rb != null && string.Equals(rb.Tag as string, mask, StringComparison.Ordinal))
                    return rb;
            }
            return null;
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var d = FindDescendant<T>(c);
                if (d != null) return d;
            }
            return null;
        }

        private void ShiftLayer_Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.Rows == null) return;

            _shiftLayerClipboard = new System.Collections.Generic.List<Engine.Data.MappingRow>();
            foreach (var r in slotMs.Rows)
            {
                if (r == null) continue;
                if (!string.Equals(r.LayerMask, mask, StringComparison.Ordinal)) continue;
                // Deep-clone so a later Paste isn't a reference share.
                var rc = new Engine.Data.MappingRow
                {
                    Target = r.Target,
                    LayerMask = r.LayerMask,
                    CombineMode = r.CombineMode,
                    CombineExpression = r.CombineExpression,
                    NoInherit = r.NoInherit,
                    Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                };
                if (r.Sources != null)
                    foreach (var s in r.Sources)
                        if (s != null) rc.Sources.Add(CloneSource(s));
                _shiftLayerClipboard.Add(rc);
            }
        }

        private void ShiftLayer_Paste_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;
            if (_shiftLayerClipboard == null || _shiftLayerClipboard.Count == 0) return;

            var slotMs = GetOrCreateSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs.Rows == null)
                slotMs.Rows = new System.Collections.Generic.List<Engine.Data.MappingRow>();

            // Drop existing rows on this layer first so paste is a
            // replace rather than a merge — matches user expectation of
            // "paste rows into layer" overwriting the destination.
            slotMs.Rows.RemoveAll(
                r => r != null && string.Equals(r.LayerMask, mask, StringComparison.Ordinal));

            foreach (var r in _shiftLayerClipboard)
            {
                if (r == null) continue;
                var rc = new Engine.Data.MappingRow
                {
                    Target = r.Target,
                    LayerMask = mask,
                    CombineMode = r.CombineMode,
                    CombineExpression = r.CombineExpression,
                    NoInherit = r.NoInherit,
                    Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                };
                if (r.Sources != null)
                    foreach (var s in r.Sources)
                        if (s != null) rc.Sources.Add(CloneSource(s));
                slotMs.Rows.Add(rc);
            }

            // Force the DataGrid to reflect the pasted rows by triggering
            // a refresh on the active layer.
            if (string.Equals(_currentPadVm.ActiveLayerMask, mask, StringComparison.Ordinal))
            {
                // Re-fire LayerActivated to drive the reload.
                _currentPadVm.ActiveLayerMask = "Base";
                _currentPadVm.ActiveLayerMask = mask;
            }
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        private void ShiftLayer_Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.Rows == null) return;

            slotMs.Rows.RemoveAll(
                r => r != null && string.Equals(r.LayerMask, mask, StringComparison.Ordinal));

            if (string.Equals(_currentPadVm.ActiveLayerMask, mask, StringComparison.Ordinal))
            {
                _currentPadVm.ActiveLayerMask = "Base";
                _currentPadVm.ActiveLayerMask = mask;
            }
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        private async void ShiftLayer_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPadVm == null) return;
            string mask = TagToLayerMask(sender);
            if (string.IsNullOrEmpty(mask)) return;

            var slotMs = GetSlotMappingSet(_currentPadVm.PadIndex);
            if (slotMs?.ShiftActivators == null) return;
            var activator = slotMs.ShiftActivators.Find(
                a => a != null && string.Equals(a.LayerMask, mask, StringComparison.Ordinal));
            if (activator == null) return;

            string layerName = string.IsNullOrEmpty(activator.LayerName) ? activator.LayerMask : activator.LayerName;
            // Use Wpf.Ui's themed MessageBox instead of the classic Win32
            // MessageBox.Show — the Mica-styled host clashes with the
            // legacy gray system dialog and breaks the visual continuity
            // the rest of PadForge keeps.
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = Strings.Instance.Pad_Shift_DeleteConfirmTitle,
                Content = string.Format(Strings.Instance.Pad_Shift_DeleteConfirm_Format, layerName),
                PrimaryButtonText = Strings.Instance.Pad_Shift_Delete,
                CloseButtonText = Strings.Instance.Common_Cancel,
            };
            var result = await dialog.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            slotMs.ShiftActivators.Remove(activator);
            if (slotMs.Rows != null)
            {
                slotMs.Rows.RemoveAll(
                    r => r != null && string.Equals(r.LayerMask, mask, StringComparison.Ordinal));
            }

            // Snap the active tab back to Base; RebuildLayerTabs will
            // also recover if the active mask no longer matches a tab.
            _currentPadVm.ActiveLayerMask = "Base";
            _currentPadVm.RebuildLayerTabs(slotMs.ShiftActivators);
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        // Full memberwise copy. The previous hand-listed clone silently dropped
        // every Param* added after it was written (Gyro / Mouse-cursor sensitivity,
        // the steering params, and the #111 ramp params), so a layer copy lost them.
        // MappingSource.Clone() copies every field, which is what the type's own doc
        // says to use at clone sites.
        private static Engine.Data.MappingSource CloneSource(Engine.Data.MappingSource s)
            => s?.Clone();

        /// <summary>Reads the slot's MappingSet from
        /// <see cref="Common.SettingsManager.SlotMappingSets"/> by pad index.
        /// Returns null when the slot is unallocated.</summary>
        private static Engine.Data.MappingSet GetSlotMappingSet(int padIndex)
        {
            if (padIndex < 0 || padIndex >= PadForge.Common.Input.SettingsManager.SlotMappingSets.Length)
                return null;
            return PadForge.Common.Input.SettingsManager.SlotMappingSets[padIndex];
        }

        /// <summary>Returns the slot's MappingSet, creating one in place if
        /// the slot is currently null. Used by the + Shift Layer flow so a
        /// slot that has never had any mappings authored can still hold the
        /// first ShiftActivator the user adds.</summary>
        private static Engine.Data.MappingSet GetOrCreateSlotMappingSet(int padIndex)
        {
            if (padIndex < 0 || padIndex >= PadForge.Common.Input.SettingsManager.SlotMappingSets.Length)
                return null;
            return PadForge.Common.Input.SettingsManager.SlotMappingSets[padIndex]
                ??= new Engine.Data.MappingSet();
        }


        // ─────────────────────────────────────────────
        //  Motor test (click) + hover highlight
        // ─────────────────────────────────────────────

        /// <summary>#175 hover-glow for the motor test click zones. Hover
        /// dims the zone (brightness-only change), so the glow is the faint
        /// neutral ember. Shared frozen instance, assigned statically from
        /// code. Never animate a shared Effect.</summary>
        private static readonly System.Windows.Media.Effects.DropShadowEffect MotorHoverGlow = CreateMotorHoverGlow();

        private static System.Windows.Media.Effects.DropShadowEffect CreateMotorHoverGlow()
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                ShadowDepth = 0,
                BlurRadius = 10,
                Opacity = 0.30
            };
            fx.Freeze();
            return fx;
        }

        private void Motor_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.Opacity = 0.7;
                el.Effect = MotorHoverGlow;
            }
        }

        private void Motor_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement el)
            {
                el.Opacity = 1.0;
                el.Effect = null;
            }
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
        //  Constant Force grid
        // ─────────────────────────────────────────────
        // Click + drag inside the 200x200 visual area maps to signed
        // [-1..+1] X/Y on the active PadViewModel. The Border hosting
        // the grid has 4 px Padding around the visual; subtract that
        // when reading mouse coordinates so cursor-at-center reads
        // exactly (0, 0).

        private const double ConstantForcePadVisualSize = 200.0;
        private const double ConstantForcePadPadding = 4.0;

        private void ConstantForcePad_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                fe.CaptureMouse();
                ApplyConstantForceFromPointer(fe, e);
                e.Handled = true;
            }
        }

        private void ConstantForcePad_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsMouseCaptured)
            {
                ApplyConstantForceFromPointer(fe, e);
            }
        }

        private void ConstantForcePad_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.IsMouseCaptured)
            {
                fe.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void ApplyConstantForceFromPointer(FrameworkElement pad, MouseEventArgs e)
        {
            if (DataContext is not PadViewModel padVm) return;
            // Skip pointer-driven edits when the toggle is off — avoids
            // priming a force that won't apply and matches the disabled
            // visual state in XAML (Grid IsEnabled="{Binding ConstantForceEnabled}").
            if (!padVm.ConstantForceEnabled) return;

            var p = e.GetPosition(pad);
            // Map padding-inset coordinates to [-1, +1] across both axes.
            double x = (p.X - ConstantForcePadPadding) / ConstantForcePadVisualSize * 2.0 - 1.0;
            double y = (p.Y - ConstantForcePadPadding) / ConstantForcePadVisualSize * 2.0 - 1.0;
            padVm.ConstantForceX = System.Math.Clamp(x, -1.0, 1.0);
            padVm.ConstantForceY = System.Math.Clamp(y, -1.0, 1.0);
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
        /// to the fields ApplyExtendedConfigs writes through (the full
        /// ExtendedSlotConfigData set: Customize, OemNameOverride,
        /// ProductString, the identity overrides, and ForceFeedbackEnabled).
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
            // Show the user's override when set (non-zero); otherwise display the
            // active profile's identity. Editing persists the override via
            // ExtendedOverride_Changed; 0 means "use the profile's value."
            int vidOverride = vm.ExtendedConfig?.VendorId ?? 0;
            int pidOverride = vm.ExtendedConfig?.ProductId ?? 0;
            ExtendedVidBox.Text = vidOverride > 0 ? $"0x{vidOverride:X4}"
                : (profile != null ? $"0x{profile.VendorId:X4}" : string.Empty);
            ExtendedPidBox.Text = pidOverride > 0 ? $"0x{pidOverride:X4}"
                : (profile != null ? $"0x{profile.ProductId:X4}" : string.Empty);
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
        }

        private void ExtendedOverride_Changed(object sender, RoutedEventArgs e)
        {
            // Persist the user-edited override fields to the slot's ExtendedConfig.
            // ProductString feeds HMOemNameOverride when OEM Name Override is active;
            // VID/PID feed HMProfileBuilder.Vid/.Pid at VC-create time (Customize-
            // gated). A VID/PID of 0 (empty or malformed entry) means "use the
            // active profile's value."
            if (_syncingExtendedConfig) return;
            if (DataContext is not PadViewModel vm || vm.ExtendedConfig == null) return;
            if (sender == ExtendedProductStringBox)
                vm.ExtendedConfig.ProductString = ExtendedProductStringBox.Text ?? string.Empty;
            else if (sender == ExtendedVidBox)
                vm.ExtendedConfig.VendorId = ParseHexId(ExtendedVidBox.Text);
            else if (sender == ExtendedPidBox)
                vm.ExtendedConfig.ProductId = ParseHexId(ExtendedPidBox.Text);
        }

        /// <summary>Parses a "0xVVVV" / "VVVV" hex VID/PID entry to 0..0xFFFF.
        /// Returns 0 ("use the active profile's value") for empty or malformed input.</summary>
        private static int ParseHexId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string t = text.Trim();
            if (t.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                t = t.Substring(2);
            return int.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int v)
                && v > 0 && v <= 0xFFFF ? v : 0;
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

        // ─────────────────────────────────────────────
        //  Touchpad recorder dialog hooks
        // ─────────────────────────────────────────────

        private void OnRecordTouchpadGestureRequested(object sender, ViewModels.RecordTouchpadGestureArgs e)
        {
            try
            {
                var dlg = new TouchpadGestureRecorderDialog(e?.DeviceGuid ?? Guid.Empty,
                                                            e?.PadIndex ?? 0,
                                                            e?.DeviceName ?? string.Empty)
                {
                    Owner = System.Windows.Window.GetWindow(this),
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PadPage] Recorder dialog failed: {ex}");
            }
        }

        private void OnDeleteTouchpadGestureRequested(object sender, ViewModels.TouchpadCustomGestureItem item)
        {
            if (item == null || item.Source == null) return;
            InputService?.DeleteCustomTouchpadGesture(item.Source.Name);
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
                // Palette items (LightbarPaletteEntry) carry their own
                // PropertyChanged via the ObservableCollection wiring in
                // PlayStationSlotConfig; their TextBoxes bind directly
                // to entry.Hex with UpdateSourceTrigger=LostFocus, so no
                // explicit sync case is needed here.
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

        /// <summary>
        /// Same defensive measure as <see cref="ProfileCombo_PreviewKeyDown"/>,
        /// but for the Mappings-tab pickers (the Combine-mode dropdown and the
        /// cross-device source dropdowns) — and it also swallows TYPE-AHEAD
        /// keys, not just arrow / page keys.
        ///
        /// <para>Why: those dropdowns sit on rows the user is actively
        /// configuring, often with a keyboard mapped to the same slot. A
        /// closed WPF ComboBox that holds (implicit) keyboard focus still
        /// reacts to keystrokes — arrows cycle the current item, and a letter
        /// / digit selects the first item whose label starts with it
        /// (type-ahead). So pressing the keys the user mapped — Up/Down to
        /// move a stick, "A"/"B"/etc. mapped to face buttons — would silently
        /// flip the Combine mode (e.g. "Average" → "Average... wait,
        /// 'Strongest'") or change a source pick out from under them. We
        /// suppress all of that while the dropdown is CLOSED; when it's open,
        /// every key passes through so explicit navigation / type-ahead in the
        /// list still works.</para>
        ///
        /// <para>Keys deliberately let through even when closed: Enter / Space /
        /// F4 (open the dropdown) and Tab / Escape (focus). Everything that
        /// would mutate the selection — Up, Down, Left, Right, PageUp,
        /// PageDown, Home, End, and any character-producing key (A–Z, the
        /// number-row digits, and the numpad digits) — is handled here so it
        /// never reaches the ComboBox.</para>
        /// </summary>
        private void MappingComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not ComboBox cb || cb.IsDropDownOpen) return;

            // Alt+Down arrives as Key.System with the real key in SystemKey;
            // resolve it so an Alt-modified arrow is still seen as an arrow.
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;

            bool isNav =
                k is Key.Up or Key.Down or Key.Left or Key.Right
                  or Key.PageUp or Key.PageDown or Key.Home or Key.End;

            // Type-ahead: WPF ComboBox jumps to the first item whose text
            // starts with the typed character. Letters and digits cover every
            // Combine-mode label ("Strongest", "Average", …) and input label
            // ("A", "Button 0", "Left Stick X", …), which is all a user maps.
            bool isTypeAhead =
                (k >= Key.A && k <= Key.Z)
                || (k >= Key.D0 && k <= Key.D9)
                || (k >= Key.NumPad0 && k <= Key.NumPad9);

            if (isNav || isTypeAhead)
                e.Handled = true;
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
        /// Refresh the audio endpoint list backing the mic-LED FollowDeviceMute
        /// picker so unplug / replug between settings opens reflects in the UI.
        /// </summary>
        private void MicLedDevicePicker_DropDownOpened(object sender, EventArgs e)
        {
            if (_currentPadVm?.PlayStationConfig is { } cfg)
                cfg.RefreshMicLedDevices();
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

        // ─────────────────────────────────────────────
        //  Mappings DataGrid: keep row-details expanded for
        //  multi-source rows even when not selected.
        //
        //  WPF's RowDetailsVisibilityMode sets DataGridRow.DetailsVisibility
        //  as a LOCAL value on each row when selection changes, and style
        //  triggers can't override local-value dependency-property writes.
        //  So we manage DetailsVisibility from code-behind instead: hook
        //  LoadingRow to apply on first display, listen to each MappingItem's
        //  IsMultiSource for live transitions, and SelectionChanged to
        //  re-assert when WPF's internal selection logic kicks back in.
        // ─────────────────────────────────────────────

        private void MappingDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            UpdateRowDetailsVisibility(e.Row);
            if (e.Row.DataContext is MappingItem mi)
            {
                mi.PropertyChanged -= OnMappingItem_RowDetailsPropertyChanged;
                mi.PropertyChanged += OnMappingItem_RowDetailsPropertyChanged;
            }
        }

        /// <summary>
        /// WPF's <see cref="DataGrid"/> doesn't honestly auto-collapse columns
        /// to their content during its initial layout pass — the Auto sizing
        /// path lets a flexible column quietly absorb the leftover horizontal
        /// space the fixed-width columns leave behind. The user-visible
        /// workaround is to double-click the column-header gripper, which the
        /// DataGrid handles by re-applying Auto and forcing a fresh
        /// measurement.
        ///
        /// Re-applying <see cref="DataGridLength.Auto"/> programmatically at
        /// Loaded time doesn't reproduce the double-click behavior reliably
        /// (the value-equality check on the property setter can short-circuit
        /// the re-measurement). Instead, walk each flexible column, measure
        /// every realized cell's child template with infinite available
        /// width, take the max DesiredSize, add a small padding fudge to
        /// match WPF's own gripper-double-click result, and lock the column
        /// to that pixel width. That matches the user-initiated double-click
        /// outcome exactly.
        /// </summary>
        private void MappingDataGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not DataGrid grid) return;
            // Run synchronously: containers for the visible viewport
            // are realized by the time Loaded fires, and
            // AutoFitFlexibleColumns disables virtualization +
            // UpdateLayout()s to force-realize the rest. Deferring to
            // ApplicationIdle (the original approach) let WPF's natural
            // Auto sizing paint first, producing a brief wide-Options
            // flash before the measure pass tightened the column.
            AutoFitFlexibleColumns(grid);
        }

        /// <summary>For each column with <see cref="DataGridLength"/>
        /// unit type Auto / SizeToCells / SizeToHeader, measure realized
        /// cell content with unbounded width, compute the honest max
        /// content width across rows + header, and lock the column to
        /// that as <see cref="DataGridLengthUnitType.Pixel"/>.
        ///
        /// <para>Special handling: when a cell contains a
        /// <see cref="ComboBox"/>, measuring the cell only captures the
        /// currently-selected item's width — a row with no selection (or
        /// a short selection like "A") would size the column to almost
        /// nothing. The widest dropdown ITEM is what the user actually
        /// needs the column to accommodate, so each cell's measurement
        /// is augmented with the widest item across every ComboBox in
        /// the cell's visual tree (plus a small dropdown-arrow allowance).</para>
        ///
        /// <para>Row virtualization is forced off via
        /// <see cref="VirtualizingPanel.SetIsVirtualizing(DependencyObject, bool)"/>
        /// for the duration of the measurement so scrolled-off rows are
        /// realized and contribute their honest content widths to the
        /// max.</para></summary>
        private static void AutoFitFlexibleColumns(DataGrid grid)
        {
            const double CellChromePadding = 12.0; // matches WPF's gripper-double-click delta
            const double ComboBoxArrowPadding = 32.0; // dropdown-arrow + ComboBox border insets

            // Force-realize every row so cells in scrolled-off rows
            // contribute to the max. The original virtualization state is
            // restored at the end so runtime scroll performance is
            // unaffected.
            bool wasVirtualizing = VirtualizingPanel.GetIsVirtualizing(grid);
            VirtualizingPanel.SetIsVirtualizing(grid, false);
            grid.UpdateLayout();

            try
            {
                foreach (var col in grid.Columns)
                {
                    var unit = col.Width.UnitType;
                    bool flexible = unit == DataGridLengthUnitType.Auto
                                 || unit == DataGridLengthUnitType.SizeToCells
                                 || unit == DataGridLengthUnitType.SizeToHeader;
                    if (!flexible) continue;

                    double maxContent = 0.0;

                    // Header DesiredSize (when SizeToCells skip header).
                    if (unit != DataGridLengthUnitType.SizeToCells)
                    {
                        if (FindHeader(grid, col) is DataGridColumnHeader header)
                        {
                            header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            maxContent = Math.Max(maxContent, header.DesiredSize.Width);
                        }
                    }

                    // Cell content DesiredSize across every realized row (when
                    // SizeToHeader skip cells).
                    if (unit != DataGridLengthUnitType.SizeToHeader)
                    {
                        // The widest dropdown ITEM is the same for every row in a
                        // column: all Source pickers bind to the slot's single
                        // cross-device input list. So measure the dropdown once per
                        // column, not once per row. Per row it was an
                        // O(rows x ~150 items) TextBlock-allocate-and-measure loop
                        // that stalled the Mapping tab ~1s on switch; this makes it
                        // O(items). Stays <= 0 until a row with a ComboBox is found,
                        // so columns whose combos live only in later rows still size.
                        double columnComboMax = 0.0;
                        foreach (var item in grid.Items)
                        {
                            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
                                continue;
                            var cellContent = col.GetCellContent(row);
                            if (cellContent == null) continue;
                            cellContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            double cellWidth = cellContent.DesiredSize.Width;

                            // Augment with the widest dropdown item (measured once).
                            // A Source-style cell shouldn't shrink to the selected
                            // item's width when the dropdown carries longer entries.
                            if (columnComboMax <= 0.0)
                                columnComboMax = MeasureWidestComboBoxItem(cellContent);
                            if (columnComboMax > 0.0)
                                cellWidth = Math.Max(cellWidth, columnComboMax + ComboBoxArrowPadding);

                            maxContent = Math.Max(maxContent, cellWidth);
                        }
                    }

                    if (maxContent > 0)
                    {
                        col.Width = new DataGridLength(maxContent + CellChromePadding,
                            DataGridLengthUnitType.Pixel);
                    }
                }
            }
            finally
            {
                VirtualizingPanel.SetIsVirtualizing(grid, wasVirtualizing);
            }
        }

        /// <summary>Walks the visual tree rooted at <paramref name="root"/>,
        /// finds every <see cref="ComboBox"/>, and returns the widest item
        /// width across all of their ItemsSources. Each item is measured by
        /// rendering its DisplayName (or string form) into a
        /// <see cref="TextBlock"/> that inherits the ComboBox's font
        /// metrics. Returns 0 when no ComboBox is present or every dropdown
        /// is empty.</summary>
        private static double MeasureWidestComboBoxItem(DependencyObject root)
        {
            double widest = 0.0;
            ForEachDescendant<ComboBox>(root, cb =>
            {
                if (cb.ItemsSource == null && cb.Items.Count == 0) return;
                string memberPath = cb.DisplayMemberPath;
                System.Collections.IEnumerable source = cb.ItemsSource ?? cb.Items;
                foreach (var item in source)
                {
                    if (item == null) continue;
                    string text = ResolveDisplayText(item, memberPath);
                    if (string.IsNullOrEmpty(text)) continue;
                    var tb = new TextBlock
                    {
                        Text = text,
                        FontFamily = cb.FontFamily,
                        FontSize = cb.FontSize,
                        FontStretch = cb.FontStretch,
                        FontStyle = cb.FontStyle,
                        FontWeight = cb.FontWeight,
                    };
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (tb.DesiredSize.Width > widest)
                        widest = tb.DesiredSize.Width;
                }
            });
            return widest;
        }

        /// <summary>Reads <paramref name="memberPath"/> off
        /// <paramref name="item"/> via reflection (matches WPF's
        /// DisplayMemberPath behavior), or falls back to
        /// <c>item.ToString()</c> when the path is empty / missing.</summary>
        private static string ResolveDisplayText(object item, string memberPath)
        {
            if (string.IsNullOrEmpty(memberPath))
                return item.ToString();
            var prop = item.GetType().GetProperty(memberPath);
            if (prop == null) return item.ToString();
            return prop.GetValue(item)?.ToString() ?? string.Empty;
        }

        /// <summary>Depth-first visual-tree walk that invokes
        /// <paramref name="action"/> on every descendant of type
        /// <typeparamref name="T"/>.</summary>
        private static void ForEachDescendant<T>(DependencyObject root, Action<T> action)
            where T : DependencyObject
        {
            if (root is T match) action(match);
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                ForEachDescendant(VisualTreeHelper.GetChild(root, i), action);
        }

        /// <summary>Walks the DataGrid's visual tree to find the
        /// <see cref="DataGridColumnHeader"/> for a given column. Necessary
        /// because <see cref="DataGridColumn"/> doesn't expose its header
        /// element publicly.</summary>
        private static DataGridColumnHeader FindHeader(DependencyObject root, DataGridColumn col)
        {
            if (root is DataGridColumnHeader h && h.Column == col) return h;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var found = FindHeader(VisualTreeHelper.GetChild(root, i), col);
                if (found != null) return found;
            }
            return null;
        }

        private void MappingDataGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.DataContext is MappingItem mi)
                mi.PropertyChanged -= OnMappingItem_RowDetailsPropertyChanged;
        }

        private void MappingDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not DataGrid grid) return;
            // Re-assert visibility on every row whose selection state changed
            // and on every multi-source row. WPF will have just set the
            // selected row to Visible and the deselected row to Collapsed
            // via its VisibleWhenSelected logic; we override the Collapsed
            // back to Visible for multi-source rows.
            foreach (var item in grid.Items)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                    UpdateRowDetailsVisibility(row);
            }
        }

        private void OnMappingItem_RowDetailsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MappingItem.IsMultiSource)) return;
            if (sender is not MappingItem mi) return;
            if (MappingDataGrid?.ItemContainerGenerator?.ContainerFromItem(mi) is DataGridRow row)
                UpdateRowDetailsVisibility(row);
        }

        private static void UpdateRowDetailsVisibility(DataGridRow row)
        {
            if (row == null) return;
            bool keepOpen = (row.DataContext is MappingItem mi && mi.IsMultiSource) || row.IsSelected;
            row.DetailsVisibility = keepOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────
        //  Custom formula visual editor (Issue #61)
        //
        //  Each chip Button in the formula editor's WrapPanel carries
        //  its insert text in Tag. The shared Click handler walks up
        //  to the StackPanel that holds the formula TextBox (named
        //  "CustomFormulaBox") and inserts the chip's text at the
        //  current caret. Preset buttons use a separate handler that
        //  replaces the entire formula (so users can start fresh).
        //  Working with TextBox.Text directly + the bound MappingItem's
        //  CombineExpression keeps the data-binding path clean — no
        //  ComboBox-SelectedItem write-back semantics to fight with.
        // ─────────────────────────────────────────────

        private void FormulaChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string token) return;
            var box = FindFormulaTextBox(btn);
            if (box == null) return;
            int caret = box.CaretIndex;
            string current = box.Text ?? "";
            box.Text = current.Insert(Math.Min(caret, current.Length), token);
            box.CaretIndex = Math.Min(caret + token.Length, box.Text.Length);
            box.Focus();
        }

        // Drag state shared across the chip palette. Single-mouse so
        // a single point + token is enough — no per-button tracking.
        private Point _chipDragStart;
        private bool _chipDragArmed;

        private void FormulaChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string)
            {
                _chipDragStart = e.GetPosition(null);
                _chipDragArmed = true;
            }
        }

        private void FormulaChip_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_chipDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _chipDragStart.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - _chipDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is Button btn && btn.Tag is string token)
                {
                    // Reset BEFORE DoDragDrop so a quick re-click after
                    // the drag finishes doesn't carry stale state.
                    _chipDragArmed = false;
                    DragDrop.DoDragDrop(btn, token, DragDropEffects.Copy);
                }
            }
        }

        private void FormulaPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string formula) return;
            var box = FindFormulaTextBox(btn);
            if (box == null) return;
            box.Text = formula;
            box.CaretIndex = formula.Length;
            box.Focus();
        }

        private static TextBox FindFormulaTextBox(DependencyObject start)
        {
            // Walk up the visual tree until we find the FormulaEditor
            // StackPanel that hosts the named TextBox + chip palette.
            // Templated namescopes mean FindName from the Button's
            // own scope might not see siblings reliably — searching
            // the parent's descendants is robust.
            var node = VisualTreeHelper.GetParent(start);
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.Name == "FormulaEditor")
                    return FindDescendantByName<TextBox>(fe, "CustomFormulaBox");
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static T FindDescendantByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && t.Name == name) return t;
                var deeper = FindDescendantByName<T>(child, name);
                if (deeper != null) return deeper;
            }
            return null;
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
