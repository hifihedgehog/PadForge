using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        /// <summary>Currently-subscribed MappedDeviceInfo for the selected
        /// device, tracked so the lightbar preview's Battery mode can follow
        /// the slow-lane BatteryText refresh (#167). Re-pointed on
        /// DataContext change and on SelectedMappedDevice change.</summary>
        private PadViewModel.MappedDeviceInfo _currentSelectedDeviceInfo;

        // Lightbar preview scene (#175): BuildLightbarPreview composes the
        // Gamepad-Asset-Pack 2D art into LightbarPreviewHost and stores the
        // animation targets here; SyncLightbarPreview is their only other
        // writer. The lit group carries the bloom effect and takes the
        // opacity animations (Breathing / Strobe); every strip Rectangle
        // shares one Fill brush so a single color animation drives all
        // elements (Rainbow lockstep). The nullable family flag tracks the
        // last-built scene so slot / tab churn never reloads bitmaps.
        private Canvas _lightbarLitGroup;
        private System.Windows.Shapes.Rectangle[] _lightbarRects;
        private Brush _lightbarFill;
        private System.Windows.Media.Effects.DropShadowEffect _lightbarBloom;
        private bool? _lightbarBuiltFamilyIsDs4;

        // Load-once bitmap cache for the preview (static: families are
        // fixed, PadPage instances come and go with navigation).
        private static System.Windows.Media.Imaging.BitmapImage _lightbarDs5Base;
        private static System.Windows.Media.Imaging.BitmapImage _lightbarDs5Mask;
        private static System.Windows.Media.Imaging.BitmapImage _lightbarDs4Base;
        private static System.Windows.Media.Imaging.BitmapImage _lightbarDs4FrontMask;
        private static System.Windows.Media.Imaging.BitmapImage _lightbarDs4RearMask;

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
            SyncLightbarPreview();
            SyncAudioHexBoxes();
            // Loaded can fire again without a paired Unloaded when the
            // element re-enters the tree — unsubscribe first so handlers
            // never stack.
            PadForge.Common.SoundPackageManager.RegistryChanged -= OnSoundPackageRegistryChanged;
            PadForge.Common.SoundPackageManager.RegistryChanged += OnSoundPackageRegistryChanged;
            RefreshSoundPackages();
            SyncBassShakerMeterTimer();
        }

        private void PadPage_Unloaded(object sender, RoutedEventArgs e)
        {
            PadForge.Common.SoundPackageManager.RegistryChanged -= OnSoundPackageRegistryChanged;
            _bassShakerMeterTimer?.Stop();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged -= OnPadVmPropertyChanged;
                _currentPadVm.ActiveDeviceConfigPropertyChanged -= OnDeviceConfigChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged -= OnMappedDevicesChanged;
                _currentPadVm.RecordTouchpadGestureRequested -= OnRecordTouchpadGestureRequested;
                _currentPadVm.DeleteTouchpadGestureRequested -= OnDeleteTouchpadGestureRequested;
            }

            _currentPadVm = DataContext as PadViewModel;
            if (_currentPadVm != null)
            {
                _currentPadVm.PropertyChanged += OnPadVmPropertyChanged;
                // The DeviceSlotConfig anchor is per selected device
                // (BindDeviceConfigForDevice swaps it on
                // SelectedMappedDevice change), so subscribe through the view
                // model's ActiveDeviceConfigPropertyChanged forwarder
                // rather than the inner config instance: the forwarder
                // follows the anchor across device swaps (same pattern as
                // InputService).
                _currentPadVm.ActiveDeviceConfigPropertyChanged += OnDeviceConfigChanged;
                if (_currentPadVm.MappedDevices != null)
                    _currentPadVm.MappedDevices.CollectionChanged += OnMappedDevicesChanged;
                _currentPadVm.RecordTouchpadGestureRequested += OnRecordTouchpadGestureRequested;
                _currentPadVm.DeleteTouchpadGestureRequested += OnDeleteTouchpadGestureRequested;
            }
            ResubscribeSelectedDeviceInfo();

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

            ApplyViewMode();
            SyncTabStripSelection();
            SyncExtendedConfigBar();
            SyncMidiConfigBar();
            SyncLightbarHexBox();
            SyncLightbarPreview();
            SyncAudioHexBoxes();
            // Slot switch: the meter timer follows the NEW slot's selected
            // tab (a different slot may sit on a different tab).
            SyncBassShakerMeterTimer();

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

        private bool IsVr()
        {
            return DataContext is PadViewModel vm && vm.OutputType == Engine.VirtualControllerType.Vr;
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
            else if (IsVr())
            {
                // VR (v1): no preview surface yet, hide every preview and
                // the 2D/3D toggle. The mapping grid below is the whole
                // editing surface.
                ControllerModel3D.Visibility = Visibility.Collapsed;
                ControllerModel2D.Visibility = Visibility.Collapsed;
                ControllerSchematic.Visibility = Visibility.Collapsed;
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
            bool isVrSlot = IsVr();
            // KBM shows Sticks (Mouse X/Y + Scroll) but hides Triggers; MIDI
            // hides both Sticks and Triggers because its mapping surface is
            // CC + note, not stick/trigger. VR hides both too: the Vr lane
            // reads none of the stick/trigger tuning keys those tabs edit.
            TabSticks.Visibility = (isMidi || isVrSlot) ? Visibility.Collapsed : Visibility.Visible;
            // Raw-surface slots whose profile declares no analog triggers
            // (the Switch Pro's ZL/ZR are digital buttons) have nothing
            // for the Triggers tab to show; hide it like the other
            // no-surface gates instead of presenting an empty tab.
            bool rawNoTriggers = DataContext is PadViewModel tvm
                && tvm.OutputType is Engine.VirtualControllerType.Extended
                    or Engine.VirtualControllerType.Nintendo
                && (tvm.ExtendedConfig?.TriggerCount ?? 0) == 0;
            TabTriggers.Visibility = (isMidi || isKbm || isVrSlot || rawNoTriggers)
                ? Visibility.Collapsed : Visibility.Visible;

            // Flick Stick tuning card (#225): keyboard/mouse slots only.
            // The flick output is relative mouse movement, so the
            // "Flick Stick ..." sources it tunes (on Mouse X) only
            // evaluate there.
            if (FlickStickCard != null)
                FlickStickCard.Visibility = isKbm ? Visibility.Visible : Visibility.Collapsed;

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
            bool lightbarIsDs4 = false;
            bool hasIndicatorLeds = false;
            // Guide Button LED (#209): XInput-pathed pads (the \\.\XboxGIP
            // GIP LED lane, USB only, though Bluetooth pads share the
            // synthetic path and simply no-op), the 2015 Steam
            // Controller (SDL home-LED hint), and the Switch home LED
            // population (#226: Pro Controller, right Joy-Con, the
            // combined pair, the charging grip; per-device
            // SDL_SetJoystickLED). Puts the Lighting tab up for those
            // devices with only the Guide LED card visible.
            bool hasGuideLed = false;
            bool hasForceFeedback = false;
            bool hasGyro = false;
            bool hasMouse = false;
            bool hasIrPointer = false; // #146 Wii Remote IR camera -> Pointer tab
            bool hasImpulseTriggers = false;
            bool hasS2Mag = false;
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
                        hasMouse = ud.IsMouse;
                        hasIrPointer = ud.HasIrCamera;
                        hasImpulseTriggers = ud.HasRumbleTriggers;
                        // Gate on the SAME capability the engine consumes.
                        // HasJoyCon2Mouse is naxes >= 8; the compass lane
                        // (UpdateCompassEstimate, the calibration sweep,
                        // and CompassYawCorrectionProvider) all require
                        // HasSwitch2Magnetometer, which needs the wider
                        // axis set an older SDL fork DLL does not report.
                        // Gating the card on the looser flag offered the
                        // whole feature (including a figure-8 calibration
                        // that silently kept nothing) on hardware where it
                        // could never run.
                        hasS2Mag = ud.Device is PadForge.Engine.SdlDeviceWrapper s2w
                                && s2w.HasSwitch2Magnetometer;
                        hasTouchpad = ud.HasTouchpad;
                        hasGuideLed =
                            PadForge.Common.Input.XboxGipGuideLedWriter.IsXboxGipPathed(ud)
                         || PadForge.Common.Input.SteamHomeLedSetter.IsSteamController2015(ud.VendorId, ud.ProdId)
                         || PadForge.Common.Input.SwitchHomeLedSetter.IsSwitchHomeLedDevice(ud.VendorId, ud.ProdId);
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
                            // Published snapshot, never Device.GetCurrentState:
                            // the poll thread is the sole reader of the pooled
                            // wrapper buffers, and a UI-thread read would be a
                            // second writer into them.
                            var st = ud.InputState;
                            numTouchpads = st?.Touchpads?.Length
                                ?? (ud.CapTouchpadCount > 0 ? ud.CapTouchpadCount : 1);
                            if (numTouchpads <= 0) numTouchpads = 1;
                        }

                        if (ud.VendorId == 0x054C)
                        {
                            bool isDualSense = ud.ProdId == 0x0CE6;
                            bool isDualSenseEdge = ud.ProdId == 0x0DF2;
                            bool isDs4 = ud.ProdId == 0x05C4 || ud.ProdId == 0x09CC || ud.ProdId == 0x0BA0;
                            hasAdaptiveTriggers = isDualSense || isDualSenseEdge;
                            hasLightbar = isDualSense || isDualSenseEdge || isDs4;
                            lightbarIsDs4 = isDs4;
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
                TabLighting.Visibility = (hasLightbar || hasGuideLed) ? Visibility.Visible : Visibility.Collapsed;
            // Lightbar-specific content hides when the tab is up for a
            // guide-LED-only device (Xbox / 2015 Steam Controller / Switch
            // home-LED devices, #226).
            if (LightbarModeCard != null)
                LightbarModeCard.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;
            if (LightingLightbarSubtitle != null)
                LightingLightbarSubtitle.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;
            if (LightingPlayerIdleHint != null)
                LightingPlayerIdleHint.Visibility = hasLightbar ? Visibility.Visible : Visibility.Collapsed;
            if (GuideLedCard != null)
                GuideLedCard.Visibility = hasGuideLed ? Visibility.Visible : Visibility.Collapsed;
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
            if (TabMouse != null)
                TabMouse.Visibility = hasMouse ? Visibility.Visible : Visibility.Collapsed;
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
            // Trigger fold (#271 item 2): only meaningful on a device with
            // body rumble but no trigger motors. Those are the devices
            // whose sink drops the game's LT/RT channels. Xbox One+ pads
            // render the triggers natively, so the row hides there.
            if (FfbTriggerFoldChk != null)
                FfbTriggerFoldChk.Visibility = (hasRumble && !hasImpulseTriggers)
                    ? Visibility.Visible : Visibility.Collapsed;
            // Compass-anchored yaw (#271 item 5): the magnetometer ships on
            // the Switch 2 Joy-Cons, the same identity set as the optical
            // mouse, so the mouse capability doubles as the gate.
            if (CompassYawCard != null)
                CompassYawCard.Visibility = hasS2Mag ? Visibility.Visible : Visibility.Collapsed;

            // Family-correct preview (#175): same PID split as the
            // capability gates above. Rebuild the art scene only when the
            // Sony family actually changed (this sync re-runs on every slot
            // / tab / device churn and must not reload bitmaps), then let
            // the animation engine re-seed the fresh targets.
            if (hasLightbar)
            {
                if (_lightbarBuiltFamilyIsDs4 != lightbarIsDs4)
                    BuildLightbarPreview(lightbarIsDs4);
                SyncLightbarPreview();
            }

            // Sync the per-pad pivot to the active device. PadViewModel
            // recomputes MaxTouchpadIndex / SelectedTouchpadIndex and
            // triggers a settings reload for the new (device, pad).
            // The zero-reset is the else of THIS branch, not the mouse one
            // below: keyed on hasMouse it fired for every non-mouse device,
            // wiping the count set two lines earlier, so a DualSense or Steam
            // Controller reported zero touchpads and the multi-pad selector
            // never appeared.
            if (DataContext is PadViewModel vmTouch && hasTouchpad)
            {
                vmTouch.RecomputeTouchpadCountForActiveDevice(numTouchpads);
                vmTouch.LoadTouchpadGestureSettingsForActiveDevice();
            }
            else if (DataContext is PadViewModel vmNoTouch)
            {
                vmNoTouch.RecomputeTouchpadCountForActiveDevice(0);
            }

            // Mouse tab (#200): reload the per-(slot, device) gesture
            // settings whenever the active device changes.
            if (DataContext is PadViewModel vmMouse && hasMouse)
                vmMouse.LoadMouseGestureSettingsForActiveDevice();

            if (MotorBarsGrid != null)
                MotorBarsGrid.Visibility = Visibility.Visible;

            // SelectedConfigTab tag values: 0 Controller, 1 Macros, 2 Mappings,
            // 3 Sticks, 4 Triggers, 5 Force Feedback, 6 Adaptive Triggers,
            // 7 Lighting, 8 Gyro, 9 Impulse Triggers, 10 Touchpad, 11 Wheel,
            // 12 Audio, 13 Pointer, 14 Mouse, 15 Menus, 16 Bass Shakers.
            // Macros, Mappings, and
            // Force Feedback are visible for every VC type. MIDI hides
            // Sticks and Triggers; K+M hides Triggers only. Adaptive
            // Triggers, Lighting, Gyro, and Impulse Triggers are gated on
            // the selected device's capabilities above. Bass Shakers is a
            // SLOT-TYPE gate (Xbox / PlayStation only, #236). Kick the user
            // back to the Controller tab if they're sitting on a now-hidden
            // one.
            if (DataContext is PadViewModel vm)
            {
                if (isMidi && (vm.SelectedConfigTab == 3 || vm.SelectedConfigTab == 4))
                    vm.SelectedConfigTab = 0;
                else if (isKbm && vm.SelectedConfigTab == 4)
                    vm.SelectedConfigTab = 0;
                // Triggers (4) also hides for a raw device with no trigger
                // axes, and Force Feedback (5) hides whenever the device has
                // none. Both were absent from this chain, so a user sitting
                // on either when the device changed was left on a collapsed
                // tab with no selected button (round 34).
                else if (vm.SelectedConfigTab == 4 && rawNoTriggers)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 5 && !hasForceFeedback)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 6 && !hasAdaptiveTriggers)
                    vm.SelectedConfigTab = 0;
                // Must match TabLighting's visibility predicate exactly
                // (hasLightbar || hasGuideLed). Testing only hasLightbar
                // bounced guide-LED-only devices (Xbox pads, the 2015 Steam
                // Controller, Switch) straight off a tab that was visible and
                // populated for them.
                else if (vm.SelectedConfigTab == 7 && !(hasLightbar || hasGuideLed))
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 8 && !hasGyro)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 9 && !hasImpulseTriggers)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 10 && !hasTouchpad)
                    vm.SelectedConfigTab = 0;
                // Same shape as the Lighting tab above: TabWheel shows on
                // (hasWheel || hasGenericWheel), so a generic SDL force-feedback
                // wheel could not stay on its own tab to reach the auto-centre
                // slider.
                else if (vm.SelectedConfigTab == 11 && !(hasWheel || hasGenericWheel))
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 12 && !hasAudio) // 12 = Audio
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 13 && !hasIrPointer) // 13 = Pointer
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == 14 && !hasMouse) // 14 = Mouse (#200)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == PadViewModel.BassShakersTabIndex
                         && !vm.RumbleAudioTabVisible) // 16 = Bass Shakers (#236)
                    vm.SelectedConfigTab = 0;
                else if (vm.SelectedConfigTab == PadViewModel.OutputTabIndex
                         && !vm.OutputTabVisible) // 17 = Output (#270 follow-up)
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
                ControllerModel2D.AnnotationChipNavigateRequested -= OnAnnotationChipNavigate;
                ControllerModel2D.AnnotationChipNavigateRequested += OnAnnotationChipNavigate;
                ControllerModel2D.AnnotationsToggled -= OnAnnotationsToggled;
                ControllerModel2D.AnnotationsToggled += OnAnnotationsToggled;
                ControllerModel2D.AnnotationsEnabled = vm.AnnotationOverlayEnabled;
            }
            else
            {
                ControllerModel3D.ControllerElementRecordRequested -= OnModelRecordRequested;
                ControllerModel3D.ControllerElementRecordRequested += OnModelRecordRequested;
                ControllerModel3D.Bind(vm);
                ControllerModel3D.AnnotationChipNavigateRequested -= OnAnnotationChipNavigate;
                ControllerModel3D.AnnotationChipNavigateRequested += OnAnnotationChipNavigate;
                ControllerModel3D.AnnotationsToggled -= OnAnnotationsToggled;
                ControllerModel3D.AnnotationsToggled += OnAnnotationsToggled;
                ControllerModel3D.AnnotationsEnabled = vm.AnnotationOverlayEnabled;
            }
        }

        private void OnModelRecordRequested(object sender, string targetName)
        {
            // Nintendo slots ride the raw surface: the preview art speaks
            // the Xbox-style element grammar ("ButtonA", "LeftThumbAxisXNeg")
            // but the mapping grid's rows are RawBtn/RawAxis/RawPov, so the
            // record handler's row lookup would miss every click. Translate
            // at the funnel; an element with no raw counterpart is ignored.
            if (DataContext is PadViewModel vm
                && vm.OutputType == Engine.VirtualControllerType.Nintendo)
            {
                targetName = Models2D.NintendoPreviewMap.ToRaw(targetName, vm.ProfileId);
                if (targetName == null) return;
            }
            ControllerElementRecordRequested?.Invoke(this, targetName);
        }

        /// <summary>Write the annotation-overlay toggle back to the VM so the
        /// state survives preview swaps within the session (#175 roadmap 1).
        /// Session-only: PadViewModel.AnnotationOverlayEnabled is never
        /// persisted to PadSetting.</summary>
        private void OnAnnotationsToggled(object sender, bool enabled)
        {
            if (DataContext is PadViewModel vm)
                vm.AnnotationOverlayEnabled = enabled;
        }

        /// <summary>Annotation chip click: jump to the Mappings tab and
        /// select + scroll to the owning row. The Loaded-priority dispatch is
        /// required because the header-less TabControl template only realizes
        /// the selected tab's content, so the DataGrid needs a layout pass
        /// after SelectedConfigTab changes before ScrollIntoView works.</summary>
        private void OnAnnotationChipNavigate(object sender, string targetSettingName)
        {
            if (DataContext is not PadViewModel vm) return;
            MappingItem row = null;
            foreach (var m in vm.Mappings)
            {
                if (string.Equals(m.TargetSettingName, targetSettingName, StringComparison.Ordinal))
                {
                    row = m;
                    break;
                }
            }
            if (row == null) return;

            vm.SelectedConfigTab = 2; // 2 = Mappings, tag map above
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                MappingDataGrid.SelectedItem = row;
                MappingDataGrid.UpdateLayout();
                MappingDataGrid.ScrollIntoView(row);
            });
        }

        // ─────────────────────────────────────────────
        //  Pipeline heat chips (#175 item 10)
        // ─────────────────────────────────────────────

        /// <summary>Per-kind cycle cursor so repeat clicks on a chip step
        /// through every row owning that pipeline instead of pinning the
        /// first. Each entry remembers the row set it was walking; when
        /// the owning set's identity changes (rows edited, remapped, or
        /// rebuilt) the cursor restarts at the first owner instead of
        /// landing mid-list. Session-only UI state.</summary>
        private readonly Dictionary<string, (List<MappingItem> Rows, int Cursor)> _pipelineChipCycle = new();

        /// <summary>Identity check for the cycle cursor: same rows, same
        /// order, by reference.</summary>
        private static bool SameChipRowSet(List<MappingItem> a, List<MappingItem> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>Chip click: scroll the mapping grid to the pipeline's
        /// owning row. Ownership uses the same PadViewModel predicates
        /// that light the chips, so the click target always matches the
        /// visual. SHIFT has no single owning row; it cycles the layer
        /// tab strip the way ShiftLayerTab_Click does (authoring
        /// selection only, nothing persisted).</summary>
        private void PipelineChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not PadViewModel vm) return;
            if (sender is not FrameworkElement fe || fe.Tag is not string kind) return;

            if (kind == "Shift")
            {
                if (vm.LayerTabs.Count < 2) return;
                int cur = 0;
                for (int i = 0; i < vm.LayerTabs.Count; i++)
                {
                    if (string.Equals(vm.LayerTabs[i].LayerMask, vm.ActiveLayerMask, StringComparison.Ordinal))
                    { cur = i; break; }
                }
                vm.ActiveLayerMask = vm.LayerTabs[(cur + 1) % vm.LayerTabs.Count].LayerMask;
                return;
            }

            var rows = new List<MappingItem>();
            foreach (var m in vm.Mappings)
            {
                bool owns = kind switch
                {
                    "Curve" => vm.IsCurvePipelineRow(m),
                    "Gyro" => PadViewModel.IsGyroPipelineRow(m),
                    "Invert" => PadViewModel.IsInvertPipelineRow(m),
                    "DeadZone" => PadViewModel.IsDeadZonePipelineRow(m),
                    _ => false,
                };
                if (owns) rows.Add(m);
            }
            if (rows.Count == 0) return;

            int next = _pipelineChipCycle.TryGetValue(kind, out var prev)
                && SameChipRowSet(prev.Rows, rows)
                ? (prev.Cursor + 1) % rows.Count : 0;
            _pipelineChipCycle[kind] = (rows, next);
            var row = rows[next];

            // Same select + layout + scroll sequence the annotation chips
            // use (OnAnnotationChipNavigate). The chip row lives on the
            // Mappings tab, so no tab switch is needed first.
            MappingDataGrid.SelectedItem = row;
            MappingDataGrid.UpdateLayout();
            MappingDataGrid.ScrollIntoView(row);
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

            // Two-tier grammar (#175 artifact): tier 1 (slot: Preview/Macros/
            // Mappings, tags 0-2, plus Menus at tag 15, #9 B-17, and Bass
            // Shakers at tag 16, #236) and tier 2 (device tabs, tags 3-14).
            // Exactly one tab is checked across
            // BOTH tiers (#175 item 18): a checked tier-1 pivot over an
            // active device tab lied about what's on screen. The idle tier
            // drops to hover affordance (both tab styles keep their
            // IsMouseOver triggers when unchecked). Navigation rides Click
            // (not Checked), so re-clicking a still-checked tab still
            // switches back.
            bool slotTier = selected <= 2 || selected == 15 || selected == 16 || selected == 17;
            foreach (var rb in FindVisualChildren<RadioButton>(this))
            {
                if (!TryGetTagIndex(rb, out int idx)) continue;
                if (rb.GroupName == "PadTab")
                    rb.IsChecked = slotTier && idx == selected;
                else if (rb.GroupName == "PadTabDevice")
                    rb.IsChecked = !slotTier && idx == selected;
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
                // Both tiers. The device-tier tabs sit in their own radio group
                // ("PadTabDevice") so WPF never auto-unchecks across tiers, and
                // filtering on the base group alone made Ctrl+Tab skip every
                // one of them: the strip showed tabs the keyboard could not
                // reach.
                if ((rb.GroupName == "PadTab" || rb.GroupName == "PadTabDevice")
                    && rb.IsVisible && rb.IsEnabled && TryGetTagIndex(rb, out int idx))
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

        /// <summary>Same hot-plug refresh for the Bass Shakers output
        /// picker (#236).</summary>
        private void RumbleAudioEndpoint_DropDownOpened(object sender, EventArgs e)
            => _currentPadVm?.RefreshRumbleAudioEndpoints();

        // ─────────────────────────────────────────────
        //  Bass Shakers meters (#236)
        // ─────────────────────────────────────────────

        /// <summary>Drives the four voice-activity meters and the endpoint
        /// status line while the Bass Shakers tab is on screen. Runs ONLY
        /// while the page is loaded AND tab 16 is selected; every other
        /// state stops the timer so a background page costs nothing.</summary>
        private System.Windows.Threading.DispatcherTimer _bassShakerMeterTimer;

        private void SyncBassShakerMeterTimer()
        {
            bool wanted = IsLoaded && _currentPadVm != null
                && _currentPadVm.SelectedConfigTab == PadViewModel.BassShakersTabIndex;
            if (!wanted)
            {
                _bassShakerMeterTimer?.Stop();
                return;
            }
            if (_bassShakerMeterTimer == null)
            {
                _bassShakerMeterTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                _bassShakerMeterTimer.Tick += (s, e) =>
                {
                    // Iconic gate: the tab flag does not flip on minimize.
                    if (PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
                    UpdateBassShakerMeters();
                };
            }
            _bassShakerMeterTimer.Start();
            // Immediate paint so the status line never shows a 100 ms blank.
            UpdateBassShakerMeters();
        }

        private void UpdateBassShakerMeters()
        {
            var vm = _currentPadVm;
            if (vm == null)
            {
                _bassShakerMeterTimer?.Stop();
                return;
            }

            // Published game-feedback pack only. Test tones live outside the
            // packs by design (provenance rule in RumbleAudioService), so the
            // meters show what the game sends, not the Test buttons.
            long pack = PadForge.Common.Input.RumbleAudioService.ReadPack(vm.PadIndex);
            var voices = vm.RumbleAudioVoices;
            for (int i = 0; i < voices.Count && i < 4; i++)
                voices[i].MeterLevel = PadForge.Engine.Common.LfeOutputState.Voice(pack, i) / 655.35;

            if (BassShakerStatusText == null) return;
            string status = PadForge.Common.Input.RumbleAudioService.GetSlotStatus(vm.PadIndex);
            if (string.IsNullOrEmpty(status))
            {
                BassShakerStatusText.Text = Strings.Instance.Pad_RumbleAudio_Status_Inactive;
                BassShakerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            }
            else if (status == "!missing")
            {
                // Fail-closed marker: the configured endpoint is gone. The
                // selection is preserved, nothing renders until it returns.
                BassShakerStatusText.Text = Strings.Instance.Pad_RumbleAudio_Status_Missing;
                BassShakerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            }
            else
            {
                BassShakerStatusText.Text = string.Format(
                    Strings.Instance.Pad_RumbleAudio_Status_Active_Format, status);
                BassShakerStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            }
        }

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

        /// <summary>Pick the program/file the Run Program action launches. The
        /// button's DataContext is the MacroAction. Any file is allowed; the user
        /// owns the choice of what to run.</summary>
        private void RunProgramBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PadForge.ViewModels.MacroAction action)
                return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.MacroAction_RunProgram_Path,
                Filter = "Programs (*.exe;*.bat;*.cmd;*.com)|*.exe;*.bat;*.cmd;*.com|All files|*.*",
                CheckFileExists = true,
            };
            try
            {
                if (!string.IsNullOrEmpty(action.ProgramPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(action.ProgramPath);
            }
            catch { }
            if (dlg.ShowDialog() != true) return;
            action.ProgramPath = dlg.FileName;
        }

        /// <summary>Pick the working folder the Run Program action starts in.</summary>
        private void RunProgramBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PadForge.ViewModels.MacroAction action)
                return;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = PadForge.Resources.Strings.Strings.Instance.MacroAction_RunProgram_WorkingDir,
            };
            try
            {
                if (!string.IsNullOrEmpty(action.ProgramWorkingDir))
                    dlg.InitialDirectory = action.ProgramWorkingDir;
                else if (!string.IsNullOrEmpty(action.ProgramPath))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(action.ProgramPath);
            }
            catch { }
            if (dlg.ShowDialog() != true) return;
            action.ProgramWorkingDir = dlg.FolderName;
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
            // Build a new list and swap the reference, never Add in place.
            // The poll thread enumerates slotMs.ShiftActivators every frame
            // without our lock (ApplyMappingSetToGamepad and
            // ResolveActiveLayerMask), so an in-place Add could throw
            // "collection was modified" inside its foreach and lose the whole
            // mapping pass for that frame. This is the discipline
            // ApplyShiftLayerSnapshot already documents and follows; the Add
            // and Remove paths here were the two that bypassed it.
            slotMs.ShiftActivators = new System.Collections.Generic.List<Engine.Data.ShiftActivator>(
                slotMs.ShiftActivators ?? (System.Collections.Generic.IEnumerable<Engine.Data.ShiftActivator>)
                    System.Array.Empty<Engine.Data.ShiftActivator>())
                { dlg.Result };
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

        /// <summary>Macro trigger dropdown (#177): arrow keys on the
        /// CLOSED combo open the dropdown instead of stepping the
        /// selection. WPF's closed-combo arrow behavior changes
        /// SelectedItem per keystroke, and with a commit-on-selection
        /// palette each stray step would append an unintended trigger
        /// entry that ANDs into the combo and blocks the macro. With
        /// this suppression, every SelectionChanged that carries an item
        /// is a deliberate pick: a mouse click selects while the
        /// dropdown is open, and Enter commits the highlighted item
        /// right after closing it (WPF selects after the close), both
        /// landing in <see cref="MacroTriggerPick_SelectionChanged"/>.</summary>
        private void MacroTriggerPick_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox combo) return;
            if (combo.IsDropDownOpen) return;
            // Home / End on a closed combo commit SelectFirst / SelectLast
            // the same way closed arrows step the selection, so all four
            // open the dropdown instead (typeahead is disabled in XAML
            // via IsTextSearchEnabled for the same reason).
            if (e.Key is System.Windows.Input.Key.Down or System.Windows.Input.Key.Up
                or System.Windows.Input.Key.Home or System.Windows.Input.Key.End)
            {
                combo.IsDropDownOpen = true;
                e.Handled = true;
            }
        }

        /// <summary>Appends the picked input to the macro's multi-device
        /// trigger combo, then clears the selection so the ComboBox acts
        /// as a command palette (the reset re-enters this handler with no
        /// selection, which the guard swallows). Unconvertible
        /// descriptors never reach this handler (the list is pre-filtered
        /// through MacroItem.TryBuildTriggerEntry), but the conversion is
        /// re-run defensively. Duplicate entries are dropped. Persistence
        /// rides SetTriggerInputEntries' existing TriggerInputs change
        /// notification, the same autosave path the trigger recorder
        /// uses.</summary>
        private void MacroTriggerPick_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ComboBox combo) return;
            if (combo.SelectedItem is not PadForge.ViewModels.InputChoice choice) return;
            combo.SelectedIndex = -1;

            if (combo.DataContext is not PadForge.ViewModels.MacroItem macro) return;
            if (!PadForge.ViewModels.MacroItem.TryBuildTriggerEntry(choice, out var entry)) return;

            var entries = new System.Collections.Generic.List<PadForge.ViewModels.MacroItem.TriggerInputEntry>(
                macro.GetTriggerInputEntries());
            foreach (var ex in entries)
                if (ex != null && ex.DeviceGuid == entry.DeviceGuid
                    && string.Equals(ex.Spec, entry.Spec, StringComparison.Ordinal))
                    return;
            entries.Add(entry);
            macro.SetTriggerInputEntries(entries);
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
                // Pass the FULL list, exactly like AddShiftLayer_Click.
                // The dialog segregates buttons vs axes itself, and any
                // family filtered here (Touchpad gestures, MIDI, gyro,
                // mouse, IR, balance...) becomes un-Configurable: the
                // saved activator's input can't be re-selected and Save
                // blocks on the input-required validation.
                foreach (var c in first.AvailableInputs)
                {
                    if (c == null) continue;
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
            string oldMode = existing.Mode;   // round five, X12 inverse
            string oldCycle = existing.CycleLayers;
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
            existing.AutoCancelMs = dlg.Result.AutoCancelMs;
            existing.Color = dlg.Result.Color;
            existing.Icon = dlg.Result.Icon;
            existing.PostponeMapping = dlg.Result.PostponeMapping;
            existing.FireOnRelease = dlg.Result.FireOnRelease;

            if (!string.Equals(oldMask, existing.LayerMask, StringComparison.Ordinal))
            {
                // A mask change renames the LOGICAL layer, and mask
                // equality is the layer identity across slots (audit
                // 2026-07-25 round four, R10/R19/R28). Split-config imports
                // clone the same activator and cycle ring onto both member
                // slots, so a current-slot-only rewrite left the twin
                // split-brained: its ring stepped onto the dead mask while
                // the globally-retagged macros waited on the new one. The
                // rename therefore follows the mask EVERYWHERE: activators,
                // cycle rings, rows, menus, and macros on every slot. For
                // two independently hand-authored same-named layers this
                // co-renames both, which is visible and non-lossy; the
                // half-global alternative silently broke imports.
                RenameMaskEverywhere(oldMask, existing.LayerMask, existing);

                // Drop engagement so nothing stays parked on a mask that no
                // longer exists (R12). Scoped to the slots the rename
                // actually touched (round five, X12): the all-slots reset
                // wiped every OTHER pad's live engagement too, which for
                // Toggle re-fired an edge and for Cycle lost the ring
                // position, on a pad whose owner did nothing.
                ClearShiftRuntimeForTouchedSlots(oldMask, existing.LayerMask);

                // Sibling tab strips and pickers mirror their own slot
                // activators; rebuild them all so the rename shows
                // everywhere it landed.
                RebuildAllPadLayerTabs(oldMask, existing.LayerMask);
            }

            else if (!string.Equals(oldMode, existing.Mode, StringComparison.Ordinal))
            {
                // A MODE change with an unchanged mask strands this slot's
                // runtime just as badly (round five, X12 inverse): Latch and
                // Cycle park a mask string that only their own mode's tick
                // rewrites, so Latch -> Hold left the slot stuck engaged.
                PadForge.Common.Input.InputManager.ClearShiftRuntime(_currentPadVm.PadIndex);
            }

            else if (!string.Equals(oldCycle, existing.CycleLayers, StringComparison.Ordinal))
            {
                // Same mask, same mode, different ring. The live cursor
                // indexes the OLD list, so shortening the ring leaves it
                // pointing past the end and the next press evaluates a stop
                // that no longer exists. ShiftCycleStepper clamps so this
                // cannot throw, but a cursor rebased by a clamp lands the
                // user somewhere they did not choose. Reset it instead.
                PadForge.Common.Input.InputManager.ClearShiftRuntime(_currentPadVm.PadIndex);
            }

            _currentPadVm.RebuildLayerTabs(slotMs.ShiftActivators);
            // Macros retag LAST (round six, R2): every pad's picker
            // choices now hold the new mask, so the SelectedValue each
            // retag pushes resolves instead of blanking the picker.
            if (!string.Equals(oldMask, existing.LayerMask, StringComparison.Ordinal))
                RetagMacrosEverywhere(AllPadViewModels(), oldMask, existing.LayerMask);
            _currentPadVm.ActiveLayerMask = existing.LayerMask;
            _currentPadVm.ConfigItemDirtyCallback?.Invoke();
        }

        /// <summary>Clears the shift runtime only for slots a mask rename
        /// touched, plus the edited slot (round five, X12).</summary>
        private void ClearShiftRuntimeForTouchedSlots(string oldMask, string newMask)
        {
            if (_currentPadVm != null)
                PadForge.Common.Input.InputManager.ClearShiftRuntime(_currentPadVm.PadIndex);
            var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
            if (sets == null) return;
            for (int i = 0; i < sets.Length; i++)
            {
                var set = sets[i];
                if (set?.ShiftActivators == null) continue;
                if (_currentPadVm != null && i == _currentPadVm.PadIndex) continue;
                bool touched = false;
                foreach (var a in set.ShiftActivators)
                {
                    if (a == null) continue;
                    if (string.Equals(a.LayerMask, newMask, StringComparison.Ordinal)
                        || PadForge.Common.Input.InputManager.PipeListContains(a.CycleLayers, newMask))
                    { touched = true; break; }
                }
                if (touched) PadForge.Common.Input.InputManager.ClearShiftRuntime(i);
            }
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
            // The Base tab's Rename edits the base APPEARANCE (name,
            // color, icon) through the same dialog Configure reroutes to;
            // without this the menu item was a silent no-op on any config
            // with no legacy "Base" activator (round eight, R14).
            if (string.Equals(TagToLayerMask(sender), "Base", StringComparison.Ordinal))
            {
                ShiftLayer_Configure_Click(sender, e);
                return;
            }
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
                    TrimDeadzone = r.TrimDeadzone,
                    TrimRate = r.TrimRate,
                    TrimResetOnRelease = r.TrimResetOnRelease,
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
            // One reference swap, not an in-place edit. The poll thread reads
            // Rows every tick without taking a lock, so a RemoveAll followed by
            // a run of Adds exposed it to a list that was missing the old rows
            // and did not yet have the new ones.
            var pasted = new System.Collections.Generic.List<Engine.Data.MappingRow>(slotMs.Rows.Count);
            foreach (var keep in slotMs.Rows)
            {
                if (keep != null && string.Equals(keep.LayerMask, mask, StringComparison.Ordinal))
                    continue;
                pasted.Add(keep);
            }

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
                    TrimDeadzone = r.TrimDeadzone,
                    TrimRate = r.TrimRate,
                    TrimResetOnRelease = r.TrimResetOnRelease,
                    Sources = new System.Collections.Generic.List<Engine.Data.MappingSource>(),
                };
                if (r.Sources != null)
                    foreach (var s in r.Sources)
                        if (s != null) rc.Sources.Add(CloneSource(s));
                pasted.Add(rc);
            }

            // The swap itself. Everything above built the replacement off to
            // the side; this is the single point where the poll thread's view
            // changes, and it changes from one complete list to another.
            slotMs.Rows = pasted;

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

            // Reference swap, same reason as the paste handler above.
            slotMs.Rows = slotMs.Rows.FindAll(
                r => !(r != null && string.Equals(r.LayerMask, mask, StringComparison.Ordinal)));
            // #254 A-3: Clear empties the layer's ROWS only, by design.
            // Macros keep their mask: the layer still exists, so they
            // remain live and re-authoring rows around them is the
            // expected flow.

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
            // The Base-mask delete HEALS (removes only the bogus legacy
            // activator, never rows or macros), so it gets its own honest
            // confirm text; the standard string promises row deletion
            // that deliberately does not happen there (round eight, R14).
            string confirmText = string.Equals(mask, "Base", StringComparison.Ordinal)
                ? string.Format(Strings.Instance.Pad_Shift_DeleteConfirmBase_Format, layerName)
                : string.Format(Strings.Instance.Pad_Shift_DeleteConfirm_Format, layerName);
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = Strings.Instance.Pad_Shift_DeleteConfirmTitle,
                Content = confirmText,
                PrimaryButtonText = Strings.Instance.Pad_Shift_Delete,
                CloseButtonText = Strings.Instance.Common_Cancel,
            };
            // Capture the pad VM BEFORE the await (audit 2026-07-25 round
            // four, R20): the confirm is not application-modal, so the user
            // can switch slots while it is up, and _currentPadVm would then
            // point at the NEW slot while slotMs still belongs to the old
            // one. Everything after the await targets this capture.
            var padVmAtOpen = _currentPadVm;

            var result = await dialog.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            // Re-validate after the await (R20): a Configure or a second
            // delete may have removed or renamed the activator while the
            // confirm was up. The activator REFERENCE is the identity; a
            // stale mask capture must not sweep rows or macros.
            // The captured set can itself be stale: a profile switch while
            // the confirm was up replaces SlotMappingSets entries with
            // clones, so membership in the captured list proves nothing
            // about the live configuration (round five, X16).
            var liveSet = GetSlotMappingSet(padVmAtOpen.PadIndex);
            if (!ReferenceEquals(liveSet, slotMs)) return;
            if (!slotMs.ShiftActivators.Contains(activator)) return;
            mask = activator.LayerMask;

            ExecuteLayerDelete(slotMs, activator, mask, AllPadViewModels());

            // The engine's shift runtime may still be ENGAGED on the deleted
            // mask (round four, R12: Latch/Toggle/Cycle park the mask string
            // until their own activator's tick rewrites it, and that
            // activator is gone). Removing an activator also shifts every
            // later activator's INDEX down, and the runtime is index-parallel,
            // so this slot's state must be dropped either way. Slot-scoped
            // (round five, X12): the all-slots reset also wiped unrelated
            // pads' live engagement.
            PadForge.Common.Input.InputManager.ClearShiftRuntime(padVmAtOpen.PadIndex);

            // Snap the active tab back to Base; RebuildLayerTabs will
            // also recover if the active mask no longer matches a tab.
            padVmAtOpen.ActiveLayerMask = "Base";
            padVmAtOpen.RebuildLayerTabs(slotMs.ShiftActivators);
            padVmAtOpen.ConfigItemDirtyCallback?.Invoke();
        }

        /// <summary>The data half of a layer delete, after the user
        /// confirmed and the handler re-validated. Internal static so the
        /// legacy-"Base" healing below is testable without driving the
        /// dialog (the round-six lesson: pinning only a predicate leaves
        /// the call site unguarded).
        ///
        /// LEGACY-"BASE" HEALING (round seven, R7): a pre-round-six layer
        /// whose persisted MASK is literally "Base" collides with the
        /// base-set identity. Its rows are indistinguishable from base
        /// rows (MappingRow.LayerMask defaults to "Base"), so the normal
        /// sweep below would delete EVERY base mapping on the slot, and
        /// macros scoped "Base" carry the #254 base-set contract, not
        /// this layer. Deleting such an activator therefore removes ONLY
        /// the bogus activator, healing the data; rows, macros, menus,
        /// and rings stay untouched.</summary>
        internal static void ExecuteLayerDelete(
            PadForge.Engine.Data.MappingSet slotMs,
            PadForge.Engine.Data.ShiftActivator activator,
            string mask,
            System.Collections.Generic.IEnumerable<PadViewModel> padVms)
        {
            // Swap, don't Remove in place: same poll-thread enumeration
            // hazard as the Add path above.
            if (slotMs.ShiftActivators != null)
            {
                var trimmed = new System.Collections.Generic.List<PadForge.Engine.Data.ShiftActivator>(
                    slotMs.ShiftActivators);
                trimmed.Remove(activator);
                slotMs.ShiftActivators = trimmed;
            }
            if (string.Equals(mask, "Base", StringComparison.Ordinal))
                return;

            if (slotMs.Rows != null)
            {
                // Reference swap, same reason as the two handlers above.
                slotMs.Rows = slotMs.Rows.FindAll(
                    r => !(r != null && string.Equals(r.LayerMask, mask, StringComparison.Ordinal)));
            }

            // Scrub the deleted mask from THIS slot's cycle rings FIRST
            // (round five, X9). Running it after the declare scan let a
            // same-slot ring satisfy the scan and spare the macros, and then
            // the scrub removed that very stop: the macros kept a mask
            // nothing declared and went permanently dark, which is the exact
            // failure the scan exists to prevent.
            foreach (var a in slotMs.ShiftActivators)
            {
                if (a == null || string.IsNullOrEmpty(a.CycleLayers)) continue;
                var stops = a.CycleLayers.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var kept = new System.Collections.Generic.List<string>(stops.Length);
                foreach (var stop in stops)
                    if (!string.Equals(stop, mask, StringComparison.Ordinal)) kept.Add(stop);
                if (kept.Count != stops.Length) a.CycleLayers = string.Join("|", kept);
            }

            // Does a RELATED slot still declare this mask? Only a slot from
            // the same import counts (round five, X10): keeping the macros
            // alive because an UNRELATED pad happens to own a same-named
            // hand-authored layer handed that pad's controller remote
            // control over these macros through the gate's fallback, which
            // is the coupling this audit lineage removed from the Base
            // branch. Import masks share a "Layer_{fileId}_" domain; a
            // hand-authored mask matches no domain and never counts.
            bool maskStillDeclared = RelatedSlotStillDeclares(
                PadForge.Common.Input.SettingsManager.SlotMappingSets, slotMs, mask);

            if (!maskStillDeclared)
            {
                // #254 A-3: deleting a layer keeps its macros (rows die with
                // the layer because they ARE its content; macros are
                // standalone authoring). They are DISABLED FIRST and only
                // then untagged (round four, R18): the engine reads these
                // shared instances live on the poll thread, and the old
                // order opened a window where the macro was enabled and
                // ungated between the two writes, firing once globally
                // during the delete. Disabled preserves the authoring;
                // "" clears the dead mask.
                foreach (var padVm in padVms)
                {
                    foreach (var mac in padVm.Macros)
                    {
                        if (mac == null || !string.Equals(mac.LayerMask, mask, StringComparison.Ordinal))
                            continue;
                        mac.IsEnabled = false;
                        mac.LayerMask = "";
                    }
                }
                // Layer-scoped menus on the deleted mask stay tagged: they
                // have no disable field, an untag would silently broaden
                // them to always-available, and a same-named layer re-add
                // revives them (hand-authored masks are name-derived).
            }
        }

        // Full memberwise copy. The previous hand-listed clone silently dropped
        // every Param* added after it was written (Gyro / Mouse-cursor sensitivity,
        // the steering params, and the #111 ramp params), so a layer copy lost them.
        // MappingSource.Clone() copies every field, which is what the type's own doc
        // says to use at clone sites.
        private static Engine.Data.MappingSource CloneSource(Engine.Data.MappingSource s)
            => s?.Clone();

        /// <summary>True when a slot RELATED to <paramref name="ownSet"/>
        /// still declares <paramref name="mask"/> after a delete, so the
        /// layer's macros must be left alone (round five, X10). Related
        /// means the same import domain: keeping macros alive because an
        /// unrelated pad owns a same-named hand-authored layer handed that
        /// pad's controller remote control over them through the gate's
        /// fallback. Internal so the policy is testable without driving the
        /// delete dialog.</summary>
        internal static bool RelatedSlotStillDeclares(
            PadForge.Engine.Data.MappingSet[] allSets,
            PadForge.Engine.Data.MappingSet ownSet,
            string mask)
        {
            if (allSets == null || string.IsNullOrEmpty(mask)) return false;
            foreach (var set in allSets)
            {
                if (set?.ShiftActivators == null) continue;
                foreach (var a in set.ShiftActivators)
                {
                    if (a == null) continue;
                    bool declares = string.Equals(a.LayerMask, mask, StringComparison.Ordinal)
                        || PadForge.Common.Input.InputManager.PipeListContains(a.CycleLayers, mask);
                    if (!declares) continue;
                    // The own slot's own remaining declarations count, and so
                    // does any slot from the same import. Nothing else.
                    if (ReferenceEquals(set, ownSet)) return true;
                    if (PadForge.Common.Input.InputManager.SlotSharesImportDomain(ownSet, mask))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Renames a layer mask across the persisted
        /// configuration: every slot activators list, cycle rings,
        /// mapping rows, and layer-scoped menus (audit 2026-07-25 round
        /// four, R10/R19/R28). Mask equality is layer identity across
        /// slots; see the Configure caller for the policy note.
        /// MACROS ARE DELIBERATELY NOT HERE (round six, R2): their masks
        /// feed the editor's Layer picker through a SelectedValue
        /// binding, and WPF resolves a changed value only against the
        /// choices that exist at write time. Retagging them before the
        /// tab rebuilds pushed the new mask into pickers that did not
        /// hold it yet, and an item added later never re-resolves a null
        /// selection, so every rename blanked the picker over intact
        /// data. <see cref="RetagMacrosEverywhere"/> runs AFTER all tab
        /// rebuilds instead.</summary>
        private void RenameMaskEverywhere(string oldMask, string newMask, PadForge.Engine.Data.ShiftActivator renamed)
        {
            var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
            if (sets != null)
            {
                foreach (var set in sets)
                {
                    if (set == null) continue;
                    if (set.ShiftActivators != null)
                    {
                        foreach (var a in set.ShiftActivators)
                        {
                            if (a == null) continue;
                            if (!ReferenceEquals(a, renamed)
                                && string.Equals(a.LayerMask, oldMask, StringComparison.Ordinal))
                            {
                                // Mask only. LayerName is documented on
                                // ShiftActivator as independently editable
                                // ("LayerMask=Shift1, LayerName=Pit Stop"),
                                // so copying this activator's name over a
                                // sibling slot's was unrecoverable data loss
                                // (round five, X7).
                                a.LayerMask = newMask;
                            }
                            if (string.IsNullOrEmpty(a.CycleLayers)) continue;
                            var stops = a.CycleLayers.Split('|', StringSplitOptions.RemoveEmptyEntries);
                            bool touched = false;
                            for (int si = 0; si < stops.Length; si++)
                            {
                                if (!string.Equals(stops[si], oldMask, StringComparison.Ordinal)) continue;
                                stops[si] = newMask;
                                touched = true;
                            }
                            if (touched) a.CycleLayers = string.Join("|", stops);
                        }
                    }
                    if (set.Rows != null)
                    {
                        foreach (var r in set.Rows)
                            if (r != null && string.Equals(r.LayerMask, oldMask, StringComparison.Ordinal))
                                r.LayerMask = newMask;
                    }
                    if (set.Menus != null)
                    {
                        // Menus are the retag family fourth member (R28):
                        // they persist a LayerMask and the runtime requires
                        // exact engagement equality, so a menu left on the
                        // old mask would never open again.
                        foreach (var mn in set.Menus)
                            if (mn != null && string.Equals(mn.LayerMask, oldMask, StringComparison.Ordinal))
                                mn.LayerMask = newMask;
                    }
                }
            }
        }

        /// <summary>The macro half of a mask rename (round four R10,
        /// repositioned round six R2). Must run only after EVERY pad's
        /// tabs (and therefore its MacroLayerChoices) have been rebuilt
        /// with the new mask: the picker's SelectedValue binding resolves
        /// the retagged value at write time, so the matching choice has
        /// to exist first. Internal static so the walk is testable
        /// without constructing the page.</summary>
        internal static void RetagMacrosEverywhere(
            System.Collections.Generic.IEnumerable<PadForge.ViewModels.PadViewModel> padVms,
            string oldMask, string newMask)
        {
            if (padVms == null) return;
            foreach (var padVm in padVms)
            {
                if (padVm?.Macros == null) continue;
                foreach (var mac in padVm.Macros)
                {
                    if (mac != null && string.Equals(mac.LayerMask, oldMask, StringComparison.Ordinal))
                        mac.LayerMask = newMask;
                }
            }
        }

        /// <summary>Rebuilds every OTHER pad layer tabs from its own slot
        /// activators after a cross-slot mask edit (round four).</summary>
        private void RebuildAllPadLayerTabs(string oldMask = null, string newMask = null)
        {
            var sets = PadForge.Common.Input.SettingsManager.SlotMappingSets;
            foreach (var padVm in AllPadViewModels())
            {
                if (ReferenceEquals(padVm, _currentPadVm)) continue;
                // A pad AUTHORING the renamed layer must follow the rename
                // (round five, X8). Its activator was just rewritten, so the
                // stale ActiveLayerMask would match no tab and RebuildLayerTabs
                // would snap it to Base and reload its grid from Base rows,
                // silently moving another pad's authoring target.
                if (oldMask != null
                    && string.Equals(padVm.ActiveLayerMask, oldMask, StringComparison.Ordinal))
                    padVm.ActiveLayerMask = newMask;
                var ms = sets != null && padVm.PadIndex >= 0 && padVm.PadIndex < sets.Length
                    ? sets[padVm.PadIndex] : null;
                padVm.RebuildLayerTabs(ms?.ShiftActivators);
            }
        }

        /// <summary>Every pad view-model, for the layer-retag walks (audit
        /// 2026-07-25, C7). Macros on OTHER slots can legitimately carry
        /// this slot's mask through the gate's split-config fallback, and
        /// they are exactly the ones a current-slot-only walk orphans.
        /// Falls back to the current pad alone when the main window is not
        /// reachable (design-time, detached host).</summary>
        private System.Collections.Generic.IEnumerable<PadViewModel> AllPadViewModels()
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm
                && mainVm.Pads != null)
            {
                foreach (var p in mainVm.Pads)
                    if (p?.Macros != null) yield return p;
                yield break;
            }
            if (_currentPadVm?.Macros != null) yield return _currentPadVm;
        }

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

        private void ClearAllMappings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm) return;
            // Destructive-verb guard (#175 phase 2 item 1d): Clear All wipes
            // every mapping row on the tab, so it asks through the shared
            // ConfirmDialog before ClearMappingsCommand runs.
            bool confirmed = ConfirmDialog.Show(
                Window.GetWindow(this),
                Strings.Instance.Pad_ClearMappings,
                Strings.Instance.Pad_ClearMappingsConfirm,
                Strings.Instance.Pad_ClearAll);
            if (confirmed)
                vm.ClearMappingsCommand.Execute(null);
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
            {
                SyncTabStripSelection();
                // Bass Shakers meters (#236) run only while tab 16 shows.
                SyncBassShakerMeterTimer();
            }
            else if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                SyncExtendedConfigBar();
                SyncMidiConfigBar();
                ApplyViewMode();
            }
            else if (e.PropertyName == nameof(PadViewModel.SelectedMappedDevice))
            {
                // Tabs reflect the selected physical device; refresh on
                // dropdown change. SetProperty raises this BEFORE the setter
                // swaps the DeviceConfig anchor, so the lighting
                // re-syncs ride the DeviceConfig case below instead;
                // only the device-info subscription is re-pointed here (the
                // backing field is already the new device).
                SyncTabVisibility();
                ResubscribeSelectedDeviceInfo();
            }
            else if (e.PropertyName == nameof(PadViewModel.DeviceConfig))
            {
                // The Lighting tab's config anchor swapped to another
                // per-device entry (BindDeviceConfigForDevice on device
                // change). Config events keep flowing through the
                // ActiveDeviceConfigPropertyChanged forwarder; re-seed
                // the value-synced controls against the new instance.
                SyncLightbarHexBox();
                SyncLightbarPreview();
                SyncAudioHexBoxes();
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

                RawStickCountBox.Text = sticks.ToString();
                ExtendedTriggerCountBox.Text = triggers.ToString();
                RawPovCountBox.Text = (profile.HasHat ? 1 : 0).ToString();
                RawButtonCountBox.Text = profile.ButtonCount.ToString();
            }
            else
            {
                // No profile resolved (e.g. catalog not loaded yet) — fall
                // back to the persisted ExtendedConfig so the UI has something
                // to show rather than blank fields.
                RawStickCountBox.Text = vm.ExtendedConfig.ThumbstickCount.ToString();
                ExtendedTriggerCountBox.Text = vm.ExtendedConfig.TriggerCount.ToString();
                RawPovCountBox.Text = vm.ExtendedConfig.PovCount.ToString();
                RawButtonCountBox.Text = vm.ExtendedConfig.ButtonCount.ToString();
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

        private void OnDeviceConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            // Keep the HEX textboxes live-synced with the RGB sliders.
            // Skip the refresh while the user is mid-edit in the textbox
            // itself — *_Apply is what's writing the properties at that
            // moment, and overwriting Text would fight the caret position.
            switch (e.PropertyName)
            {
                case nameof(ViewModels.DeviceSlotConfig.LightbarRed):
                case nameof(ViewModels.DeviceSlotConfig.LightbarGreen):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBlue):
                    if (LightbarHexBox != null && !LightbarHexBox.IsKeyboardFocusWithin)
                        SyncLightbarHexBox();
                    SyncLightbarPreview();
                    break;
                // Preview retune set: mode, the shared period (Breathing /
                // Rainbow / ColorCycle / AudioPulseRainbow / Strobe cadence),
                // Rainbow brightness, and the Battery endpoint colors. Slider
                // drags land here so the running animation retimes live.
                case nameof(ViewModels.DeviceSlotConfig.LightbarMode):
                case nameof(ViewModels.DeviceSlotConfig.LightbarPeriodMs):
                case nameof(ViewModels.DeviceSlotConfig.LightbarRainbowBrightness):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryLowR):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryLowG):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryLowB):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryHighR):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryHighG):
                case nameof(ViewModels.DeviceSlotConfig.LightbarBatteryHighB):
                    SyncLightbarPreview();
                    break;
                case nameof(ViewModels.DeviceSlotConfig.AudioLowR):
                case nameof(ViewModels.DeviceSlotConfig.AudioLowG):
                case nameof(ViewModels.DeviceSlotConfig.AudioLowB):
                    if (AudioLowHexBox != null && !AudioLowHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioLowHexBox, "Low");
                    break;
                case nameof(ViewModels.DeviceSlotConfig.AudioMidR):
                case nameof(ViewModels.DeviceSlotConfig.AudioMidG):
                case nameof(ViewModels.DeviceSlotConfig.AudioMidB):
                    if (AudioMidHexBox != null && !AudioMidHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioMidHexBox, "Mid");
                    break;
                case nameof(ViewModels.DeviceSlotConfig.AudioHighR):
                case nameof(ViewModels.DeviceSlotConfig.AudioHighG):
                case nameof(ViewModels.DeviceSlotConfig.AudioHighB):
                    if (AudioHighHexBox != null && !AudioHighHexBox.IsKeyboardFocusWithin)
                        SyncOneAudioHex(AudioHighHexBox, "High");
                    break;
                // Palette items (LightbarPaletteEntry) carry their own
                // PropertyChanged via the ObservableCollection wiring in
                // DeviceSlotConfig; their TextBoxes bind directly
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
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;
            var (r, g, b) = ReadAudioRgb(vm.DeviceConfig, tag);
            box.Text = $"{r:X2}{g:X2}{b:X2}";
        }

        private static (byte r, byte g, byte b) ReadAudioRgb(
            ViewModels.DeviceSlotConfig cfg, string tag) => tag switch
        {
            "Low"  => (cfg.AudioLowR,  cfg.AudioLowG,  cfg.AudioLowB),
            "Mid"  => (cfg.AudioMidR,  cfg.AudioMidG,  cfg.AudioMidB),
            "High" => (cfg.AudioHighR, cfg.AudioHighG, cfg.AudioHighB),
            _ => (0, 0, 0),
        };

        private static void WriteAudioRgb(
            ViewModels.DeviceSlotConfig cfg, string tag, byte r, byte g, byte b)
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
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;
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
                WriteAudioRgb(vm.DeviceConfig, tag, r, g, b);
            }

            SyncOneAudioHex(box, tag);
        }

        /// <summary>Populates the HEX textbox from the current
        /// DeviceConfig RGB. Called from DataContextChanged so
        /// switching slots loads the right value, and from
        /// PadPage_Loaded for the initial paint.</summary>
        private void SyncLightbarHexBox()
        {
            if (LightbarHexBox == null) return;
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;
            LightbarHexBox.Text = $"{vm.DeviceConfig.LightbarRed:X2}{vm.DeviceConfig.LightbarGreen:X2}{vm.DeviceConfig.LightbarBlue:X2}";
        }

        /// <summary>Re-points the BatteryText subscription at the
        /// currently selected mapped device so the lightbar preview's
        /// Battery mode tracks the slow-lane percent refresh (#167).
        /// Called from OnDataContextChanged and on every
        /// SelectedMappedDevice change.</summary>
        private void ResubscribeSelectedDeviceInfo()
        {
            if (_currentSelectedDeviceInfo != null)
                _currentSelectedDeviceInfo.PropertyChanged -= OnSelectedDeviceInfoChanged;
            _currentSelectedDeviceInfo = _currentPadVm?.SelectedMappedDevice;
            if (_currentSelectedDeviceInfo != null)
                _currentSelectedDeviceInfo.PropertyChanged += OnSelectedDeviceInfoChanged;
        }

        private void OnSelectedDeviceInfoChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only Battery mode consumes device telemetry; the mode gate
            // keeps a battery tick from restarting other modes' animation
            // clocks.
            if (e.PropertyName == nameof(PadViewModel.MappedDeviceInfo.BatteryText)
                && _currentPadVm?.DeviceConfig?.LightbarMode == ViewModels.LightbarMode.Battery)
                SyncLightbarPreview();
        }

        /// <summary>Builds the lightbar preview scene for one Sony family
        /// out of the project's real 2D controller art (#175): the
        /// Gamepad-Asset-Pack base composite at native resolution inside
        /// the LightbarPreviewHost Viewbox, plus one Rectangle per lightbar
        /// element whose OpacityMask is the pack's own strip PNG, so the
        /// lit shape is the art's shape (the DS4 front strip's alpha fades
        /// at both ends exactly like the baked art). Strip rectangles use
        /// positions measured by alpha-masked template matching against
        /// the pack's baked composites: DualSense ring 411,189 647x293
        /// (mask is native-size), DS4 front 510,228 446x5 and rear
        /// 495,111 474x28 (both stretched: the baked art is larger than
        /// the strip PNGs, drawing them at native size would undershoot
        /// the lit area). All rectangles share one Fill brush and sit in
        /// one subgroup carrying the bloom DropShadowEffect, so
        /// SyncLightbarPreview drives color on the brush and intensity on
        /// the group exactly as it drove the old hand-drawn arc. Bitmaps
        /// load once per family into static fields (EmbeddedBitmaps,
        /// the g.resources idiom shared with ControllerModel2DView).</summary>
        private void BuildLightbarPreview(bool isDs4)
        {
            if (LightbarPreviewHost == null) return;

            if (isDs4)
            {
                _lightbarDs4Base ??= EmbeddedBitmaps.Load(Models2D.DS4Layout.BasePath);
                _lightbarDs4FrontMask ??= EmbeddedBitmaps.Load("2DModels/DS4/DS4_Lightbar_Front.png");
                _lightbarDs4RearMask ??= EmbeddedBitmaps.Load("2DModels/DS4/DS4_Lightbar_Rear.png");
            }
            else
            {
                _lightbarDs5Base ??= EmbeddedBitmaps.Load(Models2D.DualSenseLayout.BasePath);
                _lightbarDs5Mask ??= EmbeddedBitmaps.Load("2DModels/DualSense/DualSense_Lightbar.png");
            }

            double baseW = isDs4 ? Models2D.DS4Layout.BaseWidth : Models2D.DualSenseLayout.BaseWidth;
            double baseH = isDs4 ? Models2D.DS4Layout.BaseHeight : Models2D.DualSenseLayout.BaseHeight;

            var scene = new Canvas { Width = baseW, Height = baseH };
            var baseImg = new Image
            {
                Source = isDs4 ? _lightbarDs4Base : _lightbarDs5Base,
                Width = baseW,
                Height = baseH,
                Stretch = Stretch.Fill,
            };
            Canvas.SetLeft(baseImg, 0);
            Canvas.SetTop(baseImg, 0);
            scene.Children.Add(baseImg);

            // Seed fill: the cold Off blue, frozen. SyncLightbarPreview
            // replaces it on the first pass right after this build.
            var seed = new SolidColorBrush(Color.FromRgb(0x58, 0xB6, 0xE4));
            seed.Freeze();
            _lightbarFill = seed;

            // Bloom radius is in native-canvas units under the Viewbox, so
            // it shrinks with the art (the 1467-wide canvas shows at 260).
            // The old arc used 14 at 180 wide shown 1:1; a straight ratio
            // match would be ~114 here, an oversized blur kernel for a
            // strip that no longer needs the halo to carry its shape. 48
            // (~3.5x the old value, roughly 8 on-screen pixels) keeps a
            // visible glow at a sane kernel size.
            _lightbarBloom = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 48,
                Opacity = 0.55,
                Color = Color.FromRgb(0x58, 0xB6, 0xE4),
            };
            _lightbarLitGroup = new Canvas { Effect = _lightbarBloom };

            _lightbarRects = isDs4
                ? new[]
                {
                    MakeLitRect(_lightbarDs4FrontMask, 510, 228, 446, 5),
                    MakeLitRect(_lightbarDs4RearMask, 495, 111, 474, 28),
                }
                : new[] { MakeLitRect(_lightbarDs5Mask, 411, 189, 647, 293) };
            foreach (var rect in _lightbarRects)
                _lightbarLitGroup.Children.Add(rect);
            scene.Children.Add(_lightbarLitGroup);

            LightbarPreviewHost.Children.Clear();
            LightbarPreviewHost.Children.Add(scene);
            _lightbarBuiltFamilyIsDs4 = isDs4;
        }

        private System.Windows.Shapes.Rectangle MakeLitRect(
            System.Windows.Media.Imaging.BitmapImage mask, double x, double y, double w, double h)
        {
            // A missing mask bitmap leaves the brush imageless (the strip
            // simply stays dark), the same soft degradation the 2D overlay
            // rig uses for absent resources.
            var maskBrush = new ImageBrush(mask) { Stretch = Stretch.Fill };
            maskBrush.Freeze();
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = _lightbarFill,
                OpacityMask = maskBrush,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            return rect;
        }

        /// <summary>Drives the lightbar preview strips so they mirror the
        /// physical bar's behavior over time (#175, user report
        /// 2026-07-05) instead of representing modes spatially. Every
        /// sync first clears the animations the previous mode started,
        /// then rebuilds from the active config: Breathing breathes the
        /// lit group's opacity, Rainbow / ColorCycle / AudioPulseRainbow
        /// loop a hue wheel through the shared fill and bloom, Strobe
        /// blinks, Battery holds the synthesizer's low-to-high lerp at
        /// the selected device's live percent, Off drops the strips to
        /// cold blue at low opacity (game writes win). Timings consume
        /// LightbarPeriodMs exactly as Ds5EffectSynthesizer does (one
        /// full cycle per period). All motion is code-driven
        /// BeginAnimation on the element / the DropShadowEffect (the
        /// MainWindow mini-card heat-ring pattern); nothing animates
        /// from style triggers. Reduced motion
        /// (SystemParameters.ClientAreaAnimation false) holds static
        /// presentations: Breathing at 0.6 opacity, Rainbow at the
        /// configured color, Strobe solid. Audio modes keep the static
        /// cold-to-color gradient because the live audio peak lives on
        /// UserEffectsDispatcher's polling thread and is not reachable
        /// from the view layer.</summary>
        private void SyncLightbarPreview()
        {
            // Macro lightbar actions mutate the per-device config from the
            // polling thread (InputManager Step 4b slot fan-out), and the
            // ActiveDeviceConfigPropertyChanged forwarder raises on the
            // calling thread. DP access and BeginAnimation require the UI
            // thread, so bounce over instead of throwing.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SyncLightbarPreview));
                return;
            }
            // Not built yet: the selected device has no lightbar (tab is
            // hidden) or SyncTabVisibility has not run for this family.
            if (_lightbarLitGroup == null || _lightbarBloom == null || _lightbarRects == null) return;
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;
            var cfg = vm.DeviceConfig;
            var baseColor = Color.FromRgb(cfg.LightbarRed, cfg.LightbarGreen, cfg.LightbarBlue);

            // Wipe prior animations first so every mode starts from local
            // values: lit-group opacity (Breathing / Strobe), bloom color
            // (rainbow lockstep), and the outgoing fill's color loop.
            // Frozen brushes can't carry clocks, so only a live brush is
            // cleared.
            _lightbarLitGroup.BeginAnimation(UIElement.OpacityProperty, null);
            _lightbarBloom.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, null);
            if (_lightbarFill is SolidColorBrush prevFill && !prevFill.IsFrozen)
                prevFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _lightbarLitGroup.Opacity = 1.0;

            bool motion = SystemParameters.ClientAreaAnimation;
            // Same floor as Ds5EffectSynthesizer.ComputeLightbarColor: one
            // full Breathing / hue-rotation / Strobe cycle per period.
            int periodMs = Math.Max(cfg.LightbarPeriodMs, 250);
            // Phase-lock every loop to a wall-clock grid (the mini-card heat
            // ring pattern in MainWindow): RGB / period slider drags re-run
            // this sync on every tick, and without the negative BeginTime
            // each restart would snap the loop back to cycle start, pinning
            // Breathing at its trough and Strobe on solid for the whole
            // drag. The engine keys its phase off its own timer, so only
            // the rate matches the physical bar, not the absolute phase.
            var phaseLock = TimeSpan.FromMilliseconds(
                -(DateTime.UtcNow.TimeOfDay.TotalMilliseconds % periodMs));

            Brush fill;
            bool fillAnimated = false;
            Color bloomColor = baseColor;
            switch (cfg.LightbarMode)
            {
                case ViewModels.LightbarMode.PlayerNumber:
                {
                    // #191 default: the idle floor. Preview the Sony
                    // player color the hardware will actually show, under
                    // the GAME WRITES WIN token (a game's write takes over
                    // and persists for the session). For a device feeding
                    // several virtual controllers that is the identity
                    // winner's color (smallest displayed number), not this
                    // slot's own; fall back to the slot number when no
                    // device is selected. The firmware table runs dim on
                    // purpose (0x40 peak channel), so normalize to full
                    // brightness here so the hue reads on screen the way
                    // the physical bar's glow does.
                    Guid selGuid = vm.SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
                    int player = SettingsManager.SlotOrders.GetIdentityPlayerNumber(selGuid);
                    if (player <= 0)
                        player = SettingsManager.SlotOrders.GetGlobalSlotNumber(vm.PadIndex);
                    var (pr, pg, pb) = PlayerIdentityDefaults.ColorFor(player);
                    int peak = Math.Max(pr, Math.Max(pg, pb));
                    Color pc = peak > 0
                        ? Color.FromRgb(
                            (byte)(pr * 0xFF / peak),
                            (byte)(pg * 0xFF / peak),
                            (byte)(pb * 0xFF / peak))
                        : Color.FromRgb(0x58, 0xB6, 0xE4);
                    fill = new SolidColorBrush(pc);
                    bloomColor = pc;
                    break;
                }

                case ViewModels.LightbarMode.Off:
                    // Deliberate hard-off (stealth): PadForge authors
                    // black every dispatch, so the strips go dark. A
                    // whisper of steel at low opacity keeps the strip
                    // geometry findable against the shell art; the bloom
                    // dims with the group because Opacity applies to the
                    // element's composite, effect output included.
                    fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x33));
                    bloomColor = Color.FromRgb(0x2A, 0x2E, 0x33);
                    _lightbarLitGroup.Opacity = 0.25;
                    break;

                case ViewModels.LightbarMode.Breathing:
                    fill = new SolidColorBrush(baseColor);
                    if (motion)
                    {
                        // Engine: triangle envelope 0 -> 1 -> 0 across one
                        // period. AutoReverse doubles the effective length,
                        // so one leg is half the period. Floor 0.15 keeps
                        // the strips findable at the trough.
                        var breathe = new DoubleAnimation(0.15, 1.0,
                            new Duration(TimeSpan.FromMilliseconds(periodMs / 2.0)))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                            BeginTime = phaseLock, // AutoReverse pair = periodMs
                        };
                        _lightbarLitGroup.BeginAnimation(UIElement.OpacityProperty, breathe);
                    }
                    else
                    {
                        _lightbarLitGroup.Opacity = 0.6;
                    }
                    break;

                case ViewModels.LightbarMode.Rainbow:
                case ViewModels.LightbarMode.ColorCycle:
                case ViewModels.LightbarMode.AudioPulseRainbow:
                    if (motion)
                    {
                        // One full hue rotation per period, the same
                        // R->Y->G->C->B->M wheel the synthesizer walks via
                        // HsvToRgb(phase * 360). Rainbow additionally scales
                        // by the brightness slider (the engine's only Rainbow
                        // dimmer). ColorCycle previews the wheel rather than
                        // the user palette (single-color fill at the
                        // matching traversal period). AudioPulseRainbow shows
                        // the hue rotation without the audio-peak modulation
                        // (the peak is not reachable here).
                        double v = cfg.LightbarMode == ViewModels.LightbarMode.Rainbow
                            ? Math.Clamp(cfg.LightbarRainbowBrightness / 100.0, 0.0, 1.0)
                            : 1.0;
                        Color Hue(byte r, byte g, byte b) => Color.FromRgb(
                            (byte)Math.Round(r * v),
                            (byte)Math.Round(g * v),
                            (byte)Math.Round(b * v));
                        Color[] wheel =
                        {
                            Hue(0xFF, 0x00, 0x00), Hue(0xFF, 0xFF, 0x00),
                            Hue(0x00, 0xFF, 0x00), Hue(0x00, 0xFF, 0xFF),
                            Hue(0x00, 0x00, 0xFF), Hue(0xFF, 0x00, 0xFF),
                            Hue(0xFF, 0x00, 0x00),
                        };
                        var loop = new ColorAnimationUsingKeyFrames
                        {
                            Duration = new Duration(TimeSpan.FromMilliseconds(periodMs)),
                            RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = phaseLock,
                        };
                        for (int i = 0; i < wheel.Length; i++)
                            loop.KeyFrames.Add(new LinearColorKeyFrame(
                                wheel[i], KeyTime.FromPercent(i / 6.0)));
                        // Animated brush: must NOT be frozen (a frozen brush
                        // rejects BeginAnimation). One shared brush fills
                        // every strip, so one clock drives them all.
                        var animated = new SolidColorBrush(wheel[0]);
                        animated.BeginAnimation(SolidColorBrush.ColorProperty, loop);
                        // Bloom follows in lockstep: code-driven
                        // BeginAnimation on the effect, the approved shape
                        // (mini-card heat ring). Both clocks are created in
                        // the same pass so they tick together.
                        _lightbarBloom.BeginAnimation(
                            System.Windows.Media.Effects.DropShadowEffect.ColorProperty, loop);
                        fill = animated;
                        fillAnimated = true;
                        bloomColor = wheel[0]; // local value under the loop
                    }
                    else
                    {
                        // Reduced motion: hold the configured color.
                        fill = new SolidColorBrush(baseColor);
                    }
                    break;

                case ViewModels.LightbarMode.Strobe:
                    // Engine: square wave at LightbarPeriodMs cadence, first
                    // half of the period on, second half off. Reduced motion
                    // holds the on phase (solid).
                    fill = new SolidColorBrush(baseColor);
                    if (motion)
                    {
                        var blink = new DoubleAnimationUsingKeyFrames
                        {
                            Duration = new Duration(TimeSpan.FromMilliseconds(periodMs)),
                            RepeatBehavior = RepeatBehavior.Forever,
                            BeginTime = phaseLock,
                        };
                        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
                        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromPercent(0.5)));
                        _lightbarLitGroup.BeginAnimation(UIElement.OpacityProperty, blink);
                    }
                    break;

                case ViewModels.LightbarMode.Battery:
                {
                    // Mirror the synthesizer's Battery lerp: configured Low ->
                    // High colors at the selected device's live percent
                    // (BatteryText, #167 slow lane, "78%" shape). Unknown or
                    // absent battery mirrors SlotBatteryPercentProvider's
                    // default of 100 (full) so the bar shows the high-charge
                    // color rather than empty red.
                    double t = 1.0;
                    string txt = vm.SelectedMappedDevice?.BatteryText;
                    if (!string.IsNullOrEmpty(txt))
                    {
                        int cut = txt.IndexOf('%');
                        if (cut > 0 && int.TryParse(txt.Substring(0, cut),
                                System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out int pct))
                            t = Math.Clamp(pct, 0, 100) / 100.0;
                    }
                    var mix = Color.FromRgb(
                        (byte)Math.Round(cfg.LightbarBatteryLowR + (cfg.LightbarBatteryHighR - cfg.LightbarBatteryLowR) * t),
                        (byte)Math.Round(cfg.LightbarBatteryLowG + (cfg.LightbarBatteryHighG - cfg.LightbarBatteryLowG) * t),
                        (byte)Math.Round(cfg.LightbarBatteryLowB + (cfg.LightbarBatteryHighB - cfg.LightbarBatteryLowB) * t));
                    fill = new SolidColorBrush(mix);
                    bloomColor = mix;
                    break;
                }

                case ViewModels.LightbarMode.AudioPulse:
                case ViewModels.LightbarMode.AudioPulseRandom:
                case ViewModels.LightbarMode.AudioThresholds:
                case ViewModels.LightbarMode.AudioGradient:
                case ViewModels.LightbarMode.AudioCrossFade:
                    // Live audio is not reachable in the view layer (the peak
                    // lives on UserEffectsDispatcher's polling thread), so
                    // audio modes keep the static cold-to-color gradient.
                    fill = new LinearGradientBrush(
                        Color.FromRgb(0x58, 0xB6, 0xE4), baseColor, 0);
                    break;

                default:
                    // Static, the legacy InputReactive family, and anything
                    // new: solid configured color, no animation.
                    fill = new SolidColorBrush(baseColor);
                    break;
            }

            // Single-writer discipline: this method owns Fill. Every strip
            // shares the one brush (solid or the audio gradient alike).
            if (!fillAnimated && fill.CanFreeze) fill.Freeze();
            foreach (var rect in _lightbarRects)
                rect.Fill = fill;
            _lightbarFill = fill;
            _lightbarBloom.Color = bloomColor;
        }

        /// <summary>Parses a HEX color string (with or without leading #)
        /// and writes the components back into DeviceConfig. The
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
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;

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
                vm.DeviceConfig.LightbarRed = r;
                vm.DeviceConfig.LightbarGreen = g;
                vm.DeviceConfig.LightbarBlue = b;
            }

            // Always reformat the textbox to canonical RRGGBB. Catches
            // both successful parse (echo back normalized form) and
            // failed parse (revert to current truth).
            LightbarHexBox.Text = $"{vm.DeviceConfig.LightbarRed:X2}{vm.DeviceConfig.LightbarGreen:X2}{vm.DeviceConfig.LightbarBlue:X2}";
        }

        /// <summary>Preset swatch row on the Lighting tab (#175
        /// competitor item 6). Tag carries RRGGBB. Write-through hits
        /// the same LightbarRed/Green/Blue trio the sliders and picker
        /// bind, so the lightbar preview, SV picker, and per-channel
        /// rows all follow on their own. The hex box is code-behind
        /// synced (not value-bound), so re-sync it explicitly, same
        /// contract as LightbarHexBox_Apply.</summary>
        private void LightbarSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string hex) return;
            if (DataContext is not PadViewModel vm || vm.DeviceConfig == null) return;

            if (hex.Length == 6
                && byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte b))
            {
                vm.DeviceConfig.LightbarRed = r;
                vm.DeviceConfig.LightbarGreen = g;
                vm.DeviceConfig.LightbarBlue = b;
                SyncLightbarHexBox();
            }
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
                // VID / PID are override fields too, and the handler's own
                // comment promises every one of them is snapped back. Left
                // set, a reset produced a "default" config still wearing the
                // previous device's identity, which is exactly what
                // ExtendedSlotConfig's own reset helper zeroes (round 34).
                vm.ExtendedConfig.VendorId = 0;
                vm.ExtendedConfig.ProductId = 0;
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

            if (int.TryParse(RawStickCountBox.Text, out int sticks))
                vm.ExtendedConfig.ThumbstickCount = sticks;
            if (int.TryParse(ExtendedTriggerCountBox.Text, out int triggers))
                vm.ExtendedConfig.TriggerCount = triggers;
            if (int.TryParse(RawPovCountBox.Text, out int povs))
                vm.ExtendedConfig.PovCount = povs;
            if (int.TryParse(RawButtonCountBox.Text, out int buttons))
                vm.ExtendedConfig.ButtonCount = buttons;

            // Reflect clamped values back into text boxes
            RawStickCountBox.Text = vm.ExtendedConfig.ThumbstickCount.ToString();
            ExtendedTriggerCountBox.Text = vm.ExtendedConfig.TriggerCount.ToString();
            RawPovCountBox.Text = vm.ExtendedConfig.PovCount.ToString();
            RawButtonCountBox.Text = vm.ExtendedConfig.ButtonCount.ToString();
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
                // The import installed rows authored for the imported
                // profile's wire; stamp it so the setter adopts instead of
                // translating them as if they were the previous profile's.
                Common.Input.SettingsManager.StampNintendoWire(vm.PadIndex, dialog.ImportedProfileId);
                vm.ProfileId = dialog.ImportedProfileId;
            }
        }

        /// <summary>
        /// "Clone Device 1:1" sizes the Extended layout to the selected device
        /// and writes identity mappings (physical input i → same-indexed Extended
        /// output) so the slot passes the device straight through to a DirectInput
        /// consumer (issue #196). Reshapes and rewrites this device's rows on the
        /// slot; the confirmation states the counts and any inputs left unmapped
        /// past the Extended caps.
        /// </summary>
        private async void ExtendedCloneBtn_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not PadViewModel vm) return;
            if (vm.OutputType != Engine.VirtualControllerType.Extended) return;

            // Any assigned device qualifies, online or not: the clone reads the
            // stored input inventory (persisted DeviceObjects, or capability
            // counts as fallback), never live input, so an unplugged device
            // clones with full fidelity. Unlike Map All, nothing here waits for
            // the user to press anything.
            var sel = vm.SelectedMappedDevice;
            if (sel == null || sel.InstanceGuid == Guid.Empty)
            {
                await new Wpf.Ui.Controls.MessageBox
                {
                    Title = Strings.Instance.Pad_ExtendedClone_Title,
                    Content = Strings.Instance.Pad_ExtendedClone_NoDevice,
                    CloseButtonText = Strings.Instance.Common_Close,
                }.ShowDialogAsync();
                return;
            }

            Engine.Data.UserDevice ud;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                ud = SettingsManager.UserDevices.Items.Find(d => d.InstanceGuid == sel.InstanceGuid);
            }

            var clone = Engine.Data.PassthroughCloneGenerator.Generate(ud);
            if (clone.Rows.Count == 0)
            {
                await new Wpf.Ui.Controls.MessageBox
                {
                    Title = Strings.Instance.Pad_ExtendedClone_Title,
                    Content = Strings.Instance.Pad_ExtendedClone_NoInputs,
                    CloseButtonText = Strings.Instance.Common_Close,
                }.ShowDialogAsync();
                return;
            }

            string deviceName = !string.IsNullOrWhiteSpace(sel.Name)
                ? sel.Name
                : (!string.IsNullOrWhiteSpace(ud.DisplayName) ? ud.DisplayName : ud.ProductName);

            string content = string.Format(Strings.Instance.Pad_ExtendedClone_Confirm_Format,
                deviceName, clone.LayoutAxes, clone.Buttons, clone.Povs);
            if (clone.HasOverflow)
                content += "\n\n" + string.Format(Strings.Instance.Pad_ExtendedClone_Overflow_Format,
                    clone.AxesMapped, clone.AxesAvailable,
                    clone.ButtonsMapped, clone.ButtonsAvailable,
                    clone.PovsMapped, clone.PovsAvailable);

            var confirm = new Wpf.Ui.Controls.MessageBox
            {
                Title = Strings.Instance.Pad_ExtendedClone_Title,
                Content = content,
                PrimaryButtonText = Strings.Instance.Pad_ExtendedClone_Apply,
                CloseButtonText = Strings.Instance.Common_Cancel,
            };
            if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
                return;

            ApplyPassthroughClone(vm, sel.InstanceGuid, deviceName, clone);
        }

        /// <summary>
        /// Applies a generated <see cref="Engine.Data.PassthroughCloneGenerator.CloneResult"/>
        /// to the slot: sets the Extended layout (sticks-only, all axes bipolar)
        /// and replaces the cloned device's contribution on every Base-layer row
        /// with a clean identity mapping. Other devices' contributions survive:
        /// their extra sources stay, and one of their primaries displaced by an
        /// identity row is demoted to an extra source on the same row, so
        /// multi-device combining stays additive. Persists the way a recorded
        /// mapping does (in-memory MappingSet made authoritative immediately,
        /// plus a dirty mark for the debounced full save).
        /// </summary>
        private void ApplyPassthroughClone(PadViewModel vm, Guid deviceGuid, string deviceLabel,
            Engine.Data.PassthroughCloneGenerator.CloneResult clone)
        {
            var cfg = vm.ExtendedConfig;
            if (cfg == null) return;

            // A passthrough must drive unshifted play, so the identity rows are
            // Base-layer rows. PushUiExtraSourcesIntoSlotMappingSets saves into
            // whichever layer is being authored, and RefreshMappingsCore hydrates
            // the grid from that same layer, so snap the authoring layer to Base
            // first (the setter re-hydrates the grid synchronously via
            // LayerActivated when it actually changes).
            vm.ActiveLayerMask = "Base";

            // Shape. Customize on so the layout override actually applies; drop
            // triggers to 0 FIRST so the stick setter isn't clamped by a leftover
            // trigger count (same ordering the config's own ResetToDefaults uses).
            cfg.Customize = true;
            cfg.TriggerCount = 0;
            cfg.ThumbstickCount = clone.Sticks;
            cfg.TriggerCount = clone.Triggers;
            cfg.PovCount = clone.Povs;
            cfg.ButtonCount = clone.Buttons;

            // Reflect the applied shape back into the config bar the same way
            // ApplyExtendedCustomValues does. Without this the count boxes keep
            // pre-clone text, and the first LostFocus on any of them would write
            // the stale numbers back and silently revert the clone's layout.
            // _syncingExtendedConfig keeps the Customize checkbox's Toggled
            // handler from re-firing on the programmatic IsChecked write.
            _syncingExtendedConfig = true;
            try
            {
                ExtendedCustomizeChk.IsChecked = true;
                RawStickCountBox.Text = cfg.ThumbstickCount.ToString();
                ExtendedTriggerCountBox.Text = cfg.TriggerCount.ToString();
                RawPovCountBox.Text = cfg.PovCount.ToString();
                RawButtonCountBox.Text = cfg.ButtonCount.ToString();
            }
            finally { _syncingExtendedConfig = false; }

            var byTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in clone.Rows)
                byTarget[row.Target] = row.Descriptor;

            // NOTE: the count setters above fired RebuildMappings, and
            // InputService.OnMappingsRebuilt synchronously re-hydrated the fresh
            // MappingItems from the slot's PRE-CLONE MappingSet (primary, extra
            // sources, combine mode, per-row deadzone, everything). The rows are
            // NOT empty here, and when the shape didn't change no rebuild fired
            // at all. Each row therefore gets an explicit reset below before the
            // identity mapping lands on it.
            string guidStr = deviceGuid.ToString().ToLowerInvariant();
            foreach (var mi in vm.Mappings)
            {
                if (mi == null || string.IsNullOrEmpty(mi.TargetSettingName)) continue;
                bool covered = byTarget.TryGetValue(mi.TargetSettingName, out string desc);

                // The cloned device's old extra-source contributions are
                // superseded by the identity mapping everywhere in the layout.
                // Other devices' extras stay.
                for (int i = mi.ExtraSources.Count - 1; i >= 0; i--)
                {
                    var ex = mi.ExtraSources[i];
                    if (ex != null && string.Equals(ex.DeviceGuid ?? "", guidStr, StringComparison.OrdinalIgnoreCase))
                        mi.ExtraSources.RemoveAt(i);
                }

                bool primaryIsCloneDevice = string.Equals(
                    mi.PrimarySourceDeviceGuid ?? "", guidStr, StringComparison.OrdinalIgnoreCase);

                if (!covered)
                {
                    // A layout slot the device doesn't fill (the tail axis of an
                    // odd-axis device): clear the cloned device's stale primary
                    // so the slot stays a faithful mirror. A row another device
                    // owns here is left alone.
                    if (primaryIsCloneDevice && !string.IsNullOrEmpty(mi.SourceDescriptor))
                        mi.ClearCommand.Execute(null);
                    continue;
                }

                // Covered target. Capture a primary another device owned here so
                // it can ride on as an extra source after the identity row takes
                // the primary slot (additive multi-device semantics). Same
                // construction as PromoteNegDescriptorToExtraSource, without the
                // Neg-pair invert flip: a demoted primary keeps its own flags.
                string oldDesc = mi.SourceDescriptor;
                string oldGuid = mi.PrimarySourceDeviceGuid ?? "";
                string oldLabel = mi.PrimarySourceDeviceLabel ?? "";
                int oldDeadZone = mi.MappingDeadZone;
                bool demote = !primaryIsCloneDevice && !string.IsNullOrEmpty(oldDesc)
                    && !string.IsNullOrEmpty(oldGuid);

                // Full primary reset: descriptor, Neg pair, Invert/Half/
                // Bidirectional, per-row deadzone, device stamp, and a
                // non-Direct primary kind (whose persist path would otherwise
                // ignore the cloned descriptor entirely).
                mi.ClearCommand.Execute(null);
                mi.GyroSensitivity = 1.0;
                mi.MouseCursorSensitivity = 1.0;
                mi.IrPointerSensitivity = 1.0;
                mi.Sensitivity = 1.0;

                if (demote)
                {
                    bool inv = false, half = false;
                    string clean = oldDesc;
                    if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                    { inv = true; half = true; clean = clean.Substring(2); }
                    else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1])
                             && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(clean))
                    { inv = true; clean = clean.Substring(1); }
                    else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                    { half = true; clean = clean.Substring(1); }

                    bool duplicate = false;
                    foreach (var existing in mi.ExtraSources)
                    {
                        if (existing == null) continue;
                        if (string.Equals(existing.Descriptor ?? "", clean, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(existing.DeviceGuid ?? "", oldGuid, StringComparison.OrdinalIgnoreCase))
                        { duplicate = true; break; }
                    }
                    if (!duplicate)
                    {
                        mi.ExtraSources.Add(new MappingSourceItem
                        {
                            Kind = "Direct",
                            DeviceGuid = oldGuid,
                            DeviceLabel = oldLabel,
                            Descriptor = clean,
                            Invert = inv,
                            HalfAxis = half,
                            DeadZone = oldDeadZone,
                        });
                    }
                }

                // With no extras left the row is a plain identity mapping; a
                // stale combine mode or custom expression from the previous
                // occupant would misdescribe it.
                if (mi.ExtraSources.Count == 0)
                {
                    mi.CombineMode = "";
                    mi.CombineExpression = "";
                }

                mi.PrimarySourceDeviceGuid = guidStr;
                mi.PrimarySourceDeviceLabel = deviceLabel;
                mi.LoadDescriptor(desc);
            }

            // Persist. Commit the grid into the in-memory per-VC MappingSet so
            // the engine sees the clone immediately, then re-hydrate the grid
            // from that now-authoritative MappingSet: RefreshMappingsCore
            // resolves each row's friendly display text and sets
            // MappingsViewLoaded, so the debounced SaveViewModelToPadSetting
            // pushes the rows into the per-device PadSetting. MarkDirty queues
            // the file write. Mirrors MainWindow's RecordingCompleted path.
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.SettingsService?.PushUiExtraSourcesIntoSlotMappingSets();
            PadForge.Services.InputService.RefreshMappingsToViewModel(vm);
            mw?.SettingsService?.MarkDirty();
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

        // The SelectedItem side is OneWay-bound to a name computed by
        // CurveLut.MatchPreset over the NORMALIZED stored curve, so a
        // binding-driven re-selection can arrive while the stored value is a
        // non-canonical spelling of the same preset (legacy single-number
        // values, CurveEditor's F3 output). Writing the canonical spelling
        // back in that case dirties the config without any user action, so
        // each handler skips the write when the stored curve already
        // normalizes to the picked preset.
        //
        // Each handler also requires DataContext and Tag to agree on the row:
        // during container recycling the two rebind in separate passes, and
        // between them a binding-driven SelectionChanged pairs the new row's
        // preset name with the old row's item.
        private void StickPresetX_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is StickConfigItem item)
            {
                if (!ReferenceEquals(cb.DataContext, item)) return;
                var serialized = FindPresetSerialized(name);
                if (serialized != null && CurveLut.Normalize(item.SensitivityCurveX) != CurveLut.Normalize(serialized))
                    item.SensitivityCurveX = serialized;
            }
        }

        private void StickPresetY_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is StickConfigItem item)
            {
                if (!ReferenceEquals(cb.DataContext, item)) return;
                var serialized = FindPresetSerialized(name);
                if (serialized != null && CurveLut.Normalize(item.SensitivityCurveY) != CurveLut.Normalize(serialized))
                    item.SensitivityCurveY = serialized;
            }
        }

        private void TriggerPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string name && cb.Tag is TriggerConfigItem item)
            {
                if (!ReferenceEquals(cb.DataContext, item)) return;
                var serialized = FindPresetSerialized(name);
                if (serialized != null && CurveLut.Normalize(item.SensitivityCurve) != CurveLut.Normalize(serialized))
                    item.SensitivityCurve = serialized;
            }
        }

        /// <summary>
        /// Radio-like guard for the adaptive-trigger mode card grids
        /// (#175 competitor item 5). A single-select ListBox still lets
        /// Ctrl+click deselect the current item, which would leave no
        /// card lit while the bound mode keeps its old value (null
        /// can't write into the enum property). Restore the cleared
        /// selection so the cards behave like radio buttons: one mode
        /// is always on.
        /// </summary>
        private void AtModeCards_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ListBox lb && lb.SelectedItem == null && e.RemovedItems.Count > 0)
                lb.SelectedItem = e.RemovedItems[0];
        }

        // ─────────────────────────────────────────────
        //  AppVolume process dropdown
        // ─────────────────────────────────────────────

        private void AppVolumeProcessDropDown_Opened(object sender, EventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is MacroAction action)
            {
                // The refresh Clear()s AudioProcessNames, which drops the
                // Selector's current item and pushes an empty Text through the
                // TwoWay ProcessName binding before the re-Add. Capture and
                // restore so opening the picker to look doesn't blank the
                // saved process (same trap as MicLedDevicePicker below).
                var keep = action.ProcessName;
                action.RefreshAudioProcessesCommand.Execute(null);
                if (!string.IsNullOrEmpty(keep) && action.ProcessName != keep)
                    action.ProcessName = keep;
            }
        }

        /// <summary>
        /// Refresh the audio endpoint list backing the mic-LED FollowDeviceMute
        /// picker so unplug / replug between settings opens reflects in the UI.
        /// </summary>
        private void MicLedDevicePicker_DropDownOpened(object sender, EventArgs e)
        {
            if (_currentPadVm?.DeviceConfig is { } cfg)
            {
                // RefreshMicLedDevices replaces the ItemsSource, which triggers a WPF
                // Selector Reset: MicLedDeviceItem has reference equality, so the
                // previously-selected item isn't found in the new list and the Selector
                // pushes SelectedValue=null back through the TwoWay binding, ERASING the
                // saved MicLedFollowDeviceId (the DS5 mic LED then stops following the
                // endpoint). Capture and restore it across the refresh so a user who
                // just opens the picker to look doesn't lose their saved device.
                var keep = cfg.MicLedFollowDeviceId;
                cfg.RefreshMicLedDevices();
                if (!string.IsNullOrEmpty(keep) && cfg.MicLedFollowDeviceId != keep)
                    cfg.MicLedFollowDeviceId = keep;
            }
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

            // Both collections are mutated by the polling thread's device /
            // settings passes, so this UI-thread walk takes their SyncRoots
            // exactly as the same file already does at lines ~377 and ~3351.
            // Unlocked, a concurrent Add could throw out of the enumeration
            // or let Find read a torn list (round 34). Lock order is
            // UserDevices BEFORE UserSettings, the codebase-wide rule.
            lock (SettingsManager.UserDevices.SyncRoot)
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var setting in SettingsManager.UserSettings.Items)
                {
                    if (setting.MapTo != slotIndex)
                        continue;
                    var ud = SettingsManager.UserDevices.Items
                        .Find(d => d.InstanceGuid == setting.InstanceGuid);
                    if (ud != null && !devices.Contains(ud))
                        devices.Add(ud);
                }
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

            // Same SyncRoot discipline as the sibling picker above.
            PadForge.Engine.Data.UserDevice ud;
            lock (SettingsManager.UserDevices.SyncRoot)
                ud = SettingsManager.UserDevices.Items
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
        //  Mappings DataGrid: one open editor at a time.
        //
        //  Multi-source rows used to force their details strip open
        //  permanently via code-behind DetailsVisibility writes (#175
        //  phase two item 10 killed that: two devices on a slot turned
        //  four outputs into a scroll wall). Details now follow the
        //  grid's stock VisibleWhenSelected mode, and an unselected
        //  fan-in row collapses to the compact one-liner through the
        //  row template's HasExtraSources / IsRowSelected trigger.
        //  SelectionChanged mirrors selection onto each MappingItem so
        //  that trigger can bind plain DataContext state.
        // ─────────────────────────────────────────────

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
            //
            // #175 telemetry board: compact rows (trivial + collapsed
            // fan-in) collapse their cells presenter, which leaves their
            // cells ungenerated and invisible to the width pass.
            // Temporarily expand every compact row so each column's
            // honest content width is measured, then restore the compact
            // state. All inside this one dispatcher callback, so no
            // frame renders the expanded state.
            var expandedForMeasure = new List<MappingItem>();
            foreach (var item in grid.Items)
            {
                if (item is MappingItem mi && (mi.IsTrivialDirect || mi.HasExtraSources) && !mi.IsExpandedOverride)
                {
                    mi.IsExpandedOverride = true;
                    expandedForMeasure.Add(mi);
                }
            }
            try
            {
                AutoFitFlexibleColumns(grid);
            }
            finally
            {
                foreach (var mi in expandedForMeasure)
                    mi.IsExpandedOverride = false;
            }
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

        /// <summary>#175 telemetry board / phase two item 10: first click
        /// on a compact row opens it back into the full editing row. The
        /// row's cells are collapsed while compact, so the click never
        /// reaches a DataGridCell and WPF's own selection logic won't run.
        /// Selecting here makes the opened row behave exactly like a
        /// clicked full row (details strip opens via the
        /// VisibleWhenSelected path). Trivial rows additionally need the
        /// expansion override; fan-in rows expand on selection alone.
        /// Focus moves to the row so Enter/Space can toggle it afterwards.
        /// Clicks on already-expanded rows fall through to normal cell
        /// handling.</summary>
        private void MappingRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridRow row || row.DataContext is not MappingItem mi) return;
            if (row.IsSelected || MappingDataGrid == null) return;
            if (mi.IsTrivialDirect && !mi.IsExpandedOverride)
            {
                mi.IsExpandedOverride = true;
                MappingDataGrid.SelectedItem = mi;
                row.Focus();
            }
            else if (mi.HasExtraSources)
            {
                MappingDataGrid.SelectedItem = mi;
                row.Focus();
            }
        }

        /// <summary>Enter/Space on a focused row toggles it open and
        /// closed (#175 phase two item 10). Gated on the row itself being
        /// the original source so the same keys keep their stock meaning
        /// inside the editor's TextBoxes / ComboBoxes / CheckBoxes.</summary>
        private void MappingRow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            if (e.OriginalSource is not DataGridRow) return;
            if (sender is not DataGridRow row || row.DataContext is not MappingItem mi) return;
            if (MappingDataGrid == null) return;
            if (row.IsSelected)
            {
                MappingDataGrid.SelectedItem = null;
            }
            else
            {
                if (mi.IsTrivialDirect && !mi.IsExpandedOverride)
                    mi.IsExpandedOverride = true;
                MappingDataGrid.SelectedItem = mi;
            }
            e.Handled = true;
        }

        private void MappingDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not DataGrid grid) return;
            // #175 telemetry board: a trivial row expanded by click drops
            // back to its compact line once deselected. Phase two item 10:
            // IsRowSelected mirrors selection for the fan-in compact-swap
            // trigger, so deselecting a multi-source row collapses it.
            foreach (var removed in e.RemovedItems)
            {
                if (removed is MappingItem rm)
                {
                    rm.IsRowSelected = false;
                    if (rm.IsExpandedOverride)
                        rm.IsExpandedOverride = false;
                }
            }
            foreach (var added in e.AddedItems)
            {
                if (added is MappingItem am)
                    am.IsRowSelected = true;
            }
            // Opening a row grows it by the details strip, which can push
            // the editor below the fold. Same layout-then-scroll sequence
            // as OnAnnotationChipNavigate, deferred so the strip has been
            // measured before the viewport math runs.
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is MappingItem sel)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    if (ReferenceEquals(grid.SelectedItem, sel))
                        grid.ScrollIntoView(sel);
                });
            }
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

        // Action-sequence reorder: drag a step chip within the list to move it,
        // dropping onto another step. Mirrors the chip-palette drag idiom above;
        // the commit is a single Actions.Move so selection and bindings survive.
        private Point _actionDragStart;
        private MacroAction _actionDragItem;

        private void ActionsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _actionDragStart = e.GetPosition(null);
            _actionDragItem = ActionFromVisual(e.OriginalSource as DependencyObject);
        }

        private void ActionsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_actionDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _actionDragStart.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - _actionDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var item = _actionDragItem;
                _actionDragItem = null; // reset before the modal DoDragDrop
                DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
            }
        }

        private void ActionsList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(MacroAction)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void ActionsList_Drop(object sender, DragEventArgs e)
        {
            if (sender is not ListBox lb) return;
            if (e.Data.GetData(typeof(MacroAction)) is not MacroAction dragged) return;
            if (lb.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<MacroAction> actions) return;
            int oldIndex = actions.IndexOf(dragged);
            if (oldIndex < 0) return;
            var target = ActionFromVisual(lb.InputHitTest(e.GetPosition(lb)) as DependencyObject);
            int newIndex = target != null ? actions.IndexOf(target) : actions.Count - 1;
            if (newIndex < 0 || newIndex == oldIndex) return;
            actions.Move(oldIndex, newIndex);
            lb.SelectedItem = dragged;
        }

        private static MacroAction ActionFromVisual(DependencyObject d)
        {
            while (d != null && d is not ListBoxItem)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return (d as ListBoxItem)?.DataContext as MacroAction;
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

    /// <summary>Two-way 0..1 fraction (setting units) to 0..100 percent
    /// (display) for the Touchpad tab's DZ-idiom rows (#175 item 15).
    /// ConvertBack takes a double from sliders and a string from the
    /// percent edit boxes. Unparseable text leaves the source untouched.</summary>
    public sealed class FractionToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? d * 100.0 : Binding.DoNothing;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double d) return d / 100.0;
            return value is string s
                   && double.TryParse(s, NumberStyles.Float, culture, out double p)
                ? p / 100.0
                : Binding.DoNothing;
        }
    }
}
