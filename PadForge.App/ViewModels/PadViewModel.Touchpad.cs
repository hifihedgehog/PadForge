using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Touchpad-gestures partial: per-(active device, pad-index) settings
    /// surfaced to the Touchpad tab. Mirrors the same load/sync rhythm as
    /// the gyro tuning partial — Load* reads PadSetting.TouchpadSettings[]
    /// into VM fields under <c>_loadingTouchpadGestures</c> guard, setters
    /// push back to the same entry, and InputService.SyncViewModelToPadSetting
    /// calls SyncTouchpadGestureSettingsToActiveDevice on the live polling
    /// rhythm so the gesture engine sees changes immediately.
    /// </summary>
    public partial class PadViewModel
    {
        private bool _loadingTouchpadGestures;

        // ─── Active touchpad pivot ────────────────────

        private int _selectedTouchpadIndex;

        /// <summary>Which pad on the active device the recorder / input
        /// preview targets (0..MaxTouchpadIndex-1). Devices with one pad
        /// pin this to 0 and hide the pivot. Gesture / gating settings are
        /// per-device now (enabling one applies to every pad the device
        /// enumerates), so changing this no longer repivots the settings
        /// cards; it only redirects the recorder and the live input
        /// preview to the chosen pad.</summary>
        public int SelectedTouchpadIndex
        {
            get => _selectedTouchpadIndex;
            set
            {
                if (value < 0) value = 0;
                SetProperty(ref _selectedTouchpadIndex, value);
            }
        }

        private int _maxTouchpadIndex = 1;

        /// <summary>Number of touchpads on the active device (0 when no
        /// touchpad-capable device is selected). UI binds the pad pivot's
        /// item-count to this and hides the pivot when &lt;= 1.</summary>
        public int MaxTouchpadIndex
        {
            get => _maxTouchpadIndex;
            private set
            {
                if (SetProperty(ref _maxTouchpadIndex, Math.Max(0, value)))
                {
                    OnPropertyChanged(nameof(HasMultipleTouchpads));
                    OnPropertyChanged(nameof(TouchpadIndexOptions));
                }
            }
        }

        public bool HasMultipleTouchpads => _maxTouchpadIndex > 1;

        /// <summary>Helper for ComboBox ItemsSource — a fresh sequence
        /// 0..MaxTouchpadIndex-1.</summary>
        public IEnumerable<int> TouchpadIndexOptions =>
            Enumerable.Range(0, Math.Max(1, _maxTouchpadIndex));

        // ─── Detection card ───────────────────────────

        private bool _touchpadGesturesEnabled;
        public bool TouchpadGesturesEnabled
        {
            get => _touchpadGesturesEnabled;
            set
            {
                if (SetProperty(ref _touchpadGesturesEnabled, value))
                {
                    PushIfNotLoading();
                    OnPropertyChanged(nameof(TouchpadInBoxSectionEnabled));
                    OnPropertyChanged(nameof(TouchpadCustomSectionEnabled));
                }
            }
        }

        private string _touchpadGestureMode = "Both";

        /// <summary>"InBoxOnly", "CustomOnly", or "Both". Mirrors
        /// <see cref="TouchpadGestureSettings.Mode"/>.</summary>
        public string TouchpadGestureMode
        {
            get => _touchpadGestureMode;
            set
            {
                var s = string.IsNullOrEmpty(value) ? "Both" : value;
                if (SetProperty(ref _touchpadGestureMode, s))
                {
                    PushIfNotLoading();
                    OnPropertyChanged(nameof(TouchpadInBoxSectionEnabled));
                    OnPropertyChanged(nameof(TouchpadCustomSectionEnabled));
                }
            }
        }

        /// <summary>True while the In-Box Gestures card's toggles can
        /// actually take effect: the master switch is on and Recognize
        /// includes the in-box catalog. The card grays out and shows a
        /// pointer to the Gesture Detection card otherwise. Users kept
        /// checking category boxes with the master off and concluding
        /// the gestures were missing from the mapping list (#177/#178).</summary>
        public bool TouchpadInBoxSectionEnabled =>
            _touchpadGesturesEnabled
            && !string.Equals(_touchpadGestureMode, "CustomOnly", StringComparison.OrdinalIgnoreCase);

        /// <summary>True while the Custom Gestures card can take effect:
        /// master switch on and Recognize includes the custom catalog.</summary>
        public bool TouchpadCustomSectionEnabled =>
            _touchpadGesturesEnabled
            && !string.Equals(_touchpadGestureMode, "InBoxOnly", StringComparison.OrdinalIgnoreCase);

        private int _touchpadCooldownMs = 100;
        public int TouchpadCooldownMs
        {
            get => _touchpadCooldownMs;
            set
            {
                var v = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref _touchpadCooldownMs, v)) PushIfNotLoading();
            }
        }

        // ─── In-box gestures card ─────────────────────

        private double _touchpadSwipeDistanceThreshold = 0.15;
        public double TouchpadSwipeDistanceThreshold
        {
            get => _touchpadSwipeDistanceThreshold;
            set
            {
                var v = Math.Clamp(value, 0.01, 1.0);
                if (SetProperty(ref _touchpadSwipeDistanceThreshold, v)) PushIfNotLoading();
            }
        }

        private int _touchpadSwipeTimeWindowMs = 500;
        public int TouchpadSwipeTimeWindowMs
        {
            get => _touchpadSwipeTimeWindowMs;
            set
            {
                var v = Math.Clamp(value, 50, 5000);
                if (SetProperty(ref _touchpadSwipeTimeWindowMs, v)) PushIfNotLoading();
            }
        }

        private bool _touchpadEnableFourWaySwipes;
        public bool TouchpadEnableFourWaySwipes
        {
            get => _touchpadEnableFourWaySwipes;
            set { if (SetProperty(ref _touchpadEnableFourWaySwipes, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableEightWaySwipes;
        public bool TouchpadEnableEightWaySwipes
        {
            get => _touchpadEnableEightWaySwipes;
            set { if (SetProperty(ref _touchpadEnableEightWaySwipes, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableRadialZones;
        public bool TouchpadEnableRadialZones
        {
            get => _touchpadEnableRadialZones;
            set { if (SetProperty(ref _touchpadEnableRadialZones, value)) PushIfNotLoading(); }
        }

        private int _touchpadRadialZoneCount = 8;
        public int TouchpadRadialZoneCount
        {
            get => _touchpadRadialZoneCount;
            set
            {
                int v = value;
                if (v != 4 && v != 6 && v != 8 && v != 12) v = 8;
                if (SetProperty(ref _touchpadRadialZoneCount, v)) PushIfNotLoading();
            }
        }

        /// <summary>Canonical zone-count choices for the radial-menu UI
        /// dropdown. Static collection — same options on every pad.</summary>
        public IReadOnlyList<int> TouchpadRadialZoneCountOptions { get; } = new[] { 4, 6, 8, 12 };

        private double _touchpadRadialCenterDeadzone = 0.30;
        public double TouchpadRadialCenterDeadzone
        {
            get => _touchpadRadialCenterDeadzone;
            set
            {
                var v = Math.Clamp(value, 0.0, 0.9);
                if (SetProperty(ref _touchpadRadialCenterDeadzone, v)) PushIfNotLoading();
            }
        }

        private bool _touchpadEnableTouchSpots;
        public bool TouchpadEnableTouchSpots
        {
            get => _touchpadEnableTouchSpots;
            set { if (SetProperty(ref _touchpadEnableTouchSpots, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableTaps;
        public bool TouchpadEnableTaps
        {
            get => _touchpadEnableTaps;
            set { if (SetProperty(ref _touchpadEnableTaps, value)) PushIfNotLoading(); }
        }

        private int _touchpadTapTimeWindowMs = 350;
        public int TouchpadTapTimeWindowMs
        {
            get => _touchpadTapTimeWindowMs;
            set
            {
                var v = Math.Clamp(value, 30, 1000);
                if (SetProperty(ref _touchpadTapTimeWindowMs, v)) PushIfNotLoading();
            }
        }

        private int _touchpadMultiTapGapMs = 300;
        public int TouchpadMultiTapGapMs
        {
            get => _touchpadMultiTapGapMs;
            set
            {
                var v = Math.Clamp(value, 50, 2000);
                if (SetProperty(ref _touchpadMultiTapGapMs, v)) PushIfNotLoading();
            }
        }

        private bool _touchpadEnableLongPress;
        public bool TouchpadEnableLongPress
        {
            get => _touchpadEnableLongPress;
            set { if (SetProperty(ref _touchpadEnableLongPress, value)) PushIfNotLoading(); }
        }

        private int _touchpadLongPressTimeWindowMs = 500;
        public int TouchpadLongPressTimeWindowMs
        {
            get => _touchpadLongPressTimeWindowMs;
            set
            {
                var v = Math.Clamp(value, 100, 5000);
                if (SetProperty(ref _touchpadLongPressTimeWindowMs, v)) PushIfNotLoading();
            }
        }

        private bool _touchpadEnableTwoFingerSwipes;
        public bool TouchpadEnableTwoFingerSwipes
        {
            get => _touchpadEnableTwoFingerSwipes;
            set { if (SetProperty(ref _touchpadEnableTwoFingerSwipes, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnablePinchSpread;
        public bool TouchpadEnablePinchSpread
        {
            get => _touchpadEnablePinchSpread;
            set { if (SetProperty(ref _touchpadEnablePinchSpread, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableRotate;
        public bool TouchpadEnableRotate
        {
            get => _touchpadEnableRotate;
            set { if (SetProperty(ref _touchpadEnableRotate, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableThreeFingerGestures;
        public bool TouchpadEnableThreeFingerGestures
        {
            get => _touchpadEnableThreeFingerGestures;
            set { if (SetProperty(ref _touchpadEnableThreeFingerGestures, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableFourFingerGestures;
        public bool TouchpadEnableFourFingerGestures
        {
            get => _touchpadEnableFourFingerGestures;
            set { if (SetProperty(ref _touchpadEnableFourFingerGestures, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableFiveFingerGestures;
        public bool TouchpadEnableFiveFingerGestures
        {
            get => _touchpadEnableFiveFingerGestures;
            set { if (SetProperty(ref _touchpadEnableFiveFingerGestures, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableShapeGestures;
        public bool TouchpadEnableShapeGestures
        {
            get => _touchpadEnableShapeGestures;
            set { if (SetProperty(ref _touchpadEnableShapeGestures, value)) PushIfNotLoading(); }
        }

        private double _touchpadGestureMatchThreshold = 3.0;

        /// <summary>Shape-recognizer matching threshold. Lower = stricter
        /// matches; higher = looser. Default 3.0 matches
        /// <see cref="Engine.Touchpad.TouchpadGestureSettings.GestureMatchThreshold"/>'s
        /// default; the engine's $Q-based <see cref="Engine.Touchpad.ShapeRecognizer"/>
        /// uses the same numeric scale as the previous $P implementation
        /// so user-tuned values transfer across the migration.</summary>
        public double TouchpadGestureMatchThreshold
        {
            get => _touchpadGestureMatchThreshold;
            set
            {
                var v = Math.Clamp(value, 0.1, 10.0);
                if (SetProperty(ref _touchpadGestureMatchThreshold, v)) PushIfNotLoading();
            }
        }

        // ─── Joystick / D-pad output card ─────────────

        private bool _touchpadEnableJoystickOutput;
        public bool TouchpadEnableJoystickOutput
        {
            get => _touchpadEnableJoystickOutput;
            set { if (SetProperty(ref _touchpadEnableJoystickOutput, value)) PushIfNotLoading(); }
        }

        private double _touchpadJoystickMaxRadius = 0.30;
        public double TouchpadJoystickMaxRadius
        {
            get => _touchpadJoystickMaxRadius;
            set
            {
                var v = Math.Clamp(value, 0.05, 0.5);
                if (SetProperty(ref _touchpadJoystickMaxRadius, v)) PushIfNotLoading();
            }
        }

        private double _touchpadJoystickInnerDeadzone = 0.02;
        public double TouchpadJoystickInnerDeadzone
        {
            get => _touchpadJoystickInnerDeadzone;
            set
            {
                var v = Math.Clamp(value, 0.0, 0.10);
                if (SetProperty(ref _touchpadJoystickInnerDeadzone, v)) PushIfNotLoading();
            }
        }

        private string _touchpadJoystickDPadMode = "FourWay";

        /// <summary>"Off" / "FourWay" / "EightWay". Mirrors
        /// <see cref="TouchpadGestureSettings.JoystickDPadMode"/>.</summary>
        public string TouchpadJoystickDPadMode
        {
            get => _touchpadJoystickDPadMode;
            set
            {
                var s = string.IsNullOrEmpty(value) ? "FourWay" : value;
                if (SetProperty(ref _touchpadJoystickDPadMode, s)) PushIfNotLoading();
            }
        }

        private double _touchpadJoystickDPadActivationThreshold = 0.15;
        public double TouchpadJoystickDPadActivationThreshold
        {
            get => _touchpadJoystickDPadActivationThreshold;
            set
            {
                var v = Math.Clamp(value, 0.05, 0.5);
                if (SetProperty(ref _touchpadJoystickDPadActivationThreshold, v)) PushIfNotLoading();
            }
        }

        // ─── Mouse output card (touchpad-as-mouse tuning) ─────────────

        private double _touchpadMouseSensitivityX = 1.0;
        public double TouchpadMouseSensitivityX
        {
            get => _touchpadMouseSensitivityX;
            set
            {
                var v = Math.Clamp(value, 0.05, 10.0);
                if (SetProperty(ref _touchpadMouseSensitivityX, v)) PushIfNotLoading();
            }
        }

        private double _touchpadMouseSensitivityY = 1.0;
        public double TouchpadMouseSensitivityY
        {
            get => _touchpadMouseSensitivityY;
            set
            {
                var v = Math.Clamp(value, 0.05, 10.0);
                if (SetProperty(ref _touchpadMouseSensitivityY, v)) PushIfNotLoading();
            }
        }

        private bool _touchpadMouseInvertX;
        public bool TouchpadMouseInvertX
        {
            get => _touchpadMouseInvertX;
            set { if (SetProperty(ref _touchpadMouseInvertX, value)) PushIfNotLoading(); }
        }

        private bool _touchpadMouseInvertY;
        public bool TouchpadMouseInvertY
        {
            get => _touchpadMouseInvertY;
            set { if (SetProperty(ref _touchpadMouseInvertY, value)) PushIfNotLoading(); }
        }

        // ─── Absolute pointer card (#9 B-15) ──────────────────────────

        private double _touchpadPointerStretchX = 1.0;

        /// <summary>Mirrors <see cref="TouchpadGestureSettings.PointerStretchX"/>:
        /// margin stretch for the "Touchpad N Pointer X" absolute cursor
        /// sources. 1.0 = Steam's 1:1 pad-to-screen map; higher values
        /// reach the screen edges before the pad bezel (the Wii aim
        /// map's margin-stretch concept).</summary>
        public double TouchpadPointerStretchX
        {
            get => _touchpadPointerStretchX;
            set
            {
                var v = Math.Clamp(value, 1.0, 3.0);
                if (SetProperty(ref _touchpadPointerStretchX, v)) PushIfNotLoading();
            }
        }

        private double _touchpadPointerStretchY = 1.0;

        /// <summary>Mirrors <see cref="TouchpadGestureSettings.PointerStretchY"/>.</summary>
        public double TouchpadPointerStretchY
        {
            get => _touchpadPointerStretchY;
            set
            {
                var v = Math.Clamp(value, 1.0, 3.0);
                if (SetProperty(ref _touchpadPointerStretchY, v)) PushIfNotLoading();
            }
        }

        // ─── Swipe haptics card (discussion #219) ─────

        private bool _touchpadSwipeHapticsEnabled;

        /// <summary>Mirrors <see cref="TouchpadGestureSettings.EnableSwipeHaptics"/>:
        /// short haptic ticks as the finger travels across this pad.
        /// Default off.</summary>
        public bool TouchpadSwipeHapticsEnabled
        {
            get => _touchpadSwipeHapticsEnabled;
            set { if (SetProperty(ref _touchpadSwipeHapticsEnabled, value)) PushIfNotLoading(); }
        }

        private double _touchpadSwipeHapticsIntensity = 0.5;

        /// <summary>Mirrors <see cref="TouchpadGestureSettings.SwipeHapticsIntensity"/>
        /// (0..1). Default 0.5, the Medium step of DS4MapperTest's
        /// intensity ladder.</summary>
        public double TouchpadSwipeHapticsIntensity
        {
            get => _touchpadSwipeHapticsIntensity;
            set
            {
                var v = Math.Clamp(value, 0.0, 1.0);
                if (SetProperty(ref _touchpadSwipeHapticsIntensity, v)) PushIfNotLoading();
            }
        }

        // ─── Custom gestures card ─────────────────────

        /// <summary>Profile-scoped custom touchpad gestures filtered by
        /// the active device's class. UI binds an ItemsControl to this.
        /// Refreshed via <see cref="RefreshCustomTouchpadGestures"/>.</summary>
        public ObservableCollection<TouchpadCustomGestureItem> CustomTouchpadGestures { get; }
            = new();

        /// <summary>True when zero custom gestures are saved. Drives the
        /// "no custom gestures" placeholder text visibility.</summary>
        public bool HasNoCustomTouchpadGestures => CustomTouchpadGestures.Count == 0;

        private RelayCommand _recordTouchpadGestureCommand;

        /// <summary>Opens the recorder dialog. Raises an event so the
        /// View can show the dialog without the VM taking a UI
        /// dependency. The event payload carries the (device, pad)
        /// the user is currently editing so the dialog mirrors live
        /// finger input from the right pad.</summary>
        public RelayCommand RecordTouchpadGestureCommand =>
            _recordTouchpadGestureCommand ??= new RelayCommand(() =>
            {
                var us = GetActiveUserSettingForTouchpad(out var guid);
                var args = new RecordTouchpadGestureArgs
                {
                    DeviceGuid = guid,
                    DeviceName = us?.InstanceName ?? string.Empty,
                    PadIndex = _selectedTouchpadIndex,
                };
                RecordTouchpadGestureRequested?.Invoke(this, args);
            });

        public event EventHandler<RecordTouchpadGestureArgs> RecordTouchpadGestureRequested;

        private RelayCommand<TouchpadCustomGestureItem> _deleteTouchpadGestureCommand;

        public RelayCommand<TouchpadCustomGestureItem> DeleteTouchpadGestureCommand =>
            _deleteTouchpadGestureCommand ??= new RelayCommand<TouchpadCustomGestureItem>(item =>
            {
                if (item == null) return;
                DeleteTouchpadGestureRequested?.Invoke(this, item);
            });

        public event EventHandler<TouchpadCustomGestureItem> DeleteTouchpadGestureRequested;

        // ─── Reset commands (per-row + per-card) ──────
        //
        // Defaults below mirror TouchpadGestureSettings.Default() and
        // the per-property initializers above so a reset round-trips
        // to "the engine's out-of-the-box behavior for this pad."

        private RelayCommand _resetTouchpadGesturesEnabledCommand;
        public RelayCommand ResetTouchpadGesturesEnabledCommand =>
            _resetTouchpadGesturesEnabledCommand ??= new RelayCommand(() => TouchpadGesturesEnabled = false);

        private RelayCommand _resetTouchpadGestureModeCommand;
        public RelayCommand ResetTouchpadGestureModeCommand =>
            _resetTouchpadGestureModeCommand ??= new RelayCommand(() => TouchpadGestureMode = "Both");

        private RelayCommand _resetTouchpadCooldownMsCommand;
        public RelayCommand ResetTouchpadCooldownMsCommand =>
            _resetTouchpadCooldownMsCommand ??= new RelayCommand(() => TouchpadCooldownMs = 100);

        private RelayCommand _resetTouchpadEnableFourWaySwipesCommand;
        public RelayCommand ResetTouchpadEnableFourWaySwipesCommand =>
            _resetTouchpadEnableFourWaySwipesCommand ??= new RelayCommand(() => TouchpadEnableFourWaySwipes = false);

        private RelayCommand _resetTouchpadEnableEightWaySwipesCommand;
        public RelayCommand ResetTouchpadEnableEightWaySwipesCommand =>
            _resetTouchpadEnableEightWaySwipesCommand ??= new RelayCommand(() => TouchpadEnableEightWaySwipes = false);

        private RelayCommand _resetTouchpadSwipeDistanceThresholdCommand;
        public RelayCommand ResetTouchpadSwipeDistanceThresholdCommand =>
            _resetTouchpadSwipeDistanceThresholdCommand ??= new RelayCommand(() => TouchpadSwipeDistanceThreshold = 0.15);

        private RelayCommand _resetTouchpadSwipeTimeWindowMsCommand;
        public RelayCommand ResetTouchpadSwipeTimeWindowMsCommand =>
            _resetTouchpadSwipeTimeWindowMsCommand ??= new RelayCommand(() => TouchpadSwipeTimeWindowMs = 500);

        private RelayCommand _resetTouchpadEnableRadialZonesCommand;
        public RelayCommand ResetTouchpadEnableRadialZonesCommand =>
            _resetTouchpadEnableRadialZonesCommand ??= new RelayCommand(() => TouchpadEnableRadialZones = false);

        private RelayCommand _resetTouchpadEnableTouchSpotsCommand;
        public RelayCommand ResetTouchpadEnableTouchSpotsCommand =>
            _resetTouchpadEnableTouchSpotsCommand ??= new RelayCommand(() => TouchpadEnableTouchSpots = false);

        private RelayCommand _resetTouchpadRadialZoneCountCommand;
        public RelayCommand ResetTouchpadRadialZoneCountCommand =>
            _resetTouchpadRadialZoneCountCommand ??= new RelayCommand(() => TouchpadRadialZoneCount = 8);

        private RelayCommand _resetTouchpadRadialCenterDeadzoneCommand;
        public RelayCommand ResetTouchpadRadialCenterDeadzoneCommand =>
            _resetTouchpadRadialCenterDeadzoneCommand ??= new RelayCommand(() => TouchpadRadialCenterDeadzone = 0.30);

        private RelayCommand _resetTouchpadEnableTapsCommand;
        public RelayCommand ResetTouchpadEnableTapsCommand =>
            _resetTouchpadEnableTapsCommand ??= new RelayCommand(() => TouchpadEnableTaps = false);

        private RelayCommand _resetTouchpadTapTimeWindowMsCommand;
        public RelayCommand ResetTouchpadTapTimeWindowMsCommand =>
            _resetTouchpadTapTimeWindowMsCommand ??= new RelayCommand(() => TouchpadTapTimeWindowMs = 350);

        private RelayCommand _resetTouchpadMultiTapGapMsCommand;
        public RelayCommand ResetTouchpadMultiTapGapMsCommand =>
            _resetTouchpadMultiTapGapMsCommand ??= new RelayCommand(() => TouchpadMultiTapGapMs = 300);

        private RelayCommand _resetTouchpadEnableLongPressCommand;
        public RelayCommand ResetTouchpadEnableLongPressCommand =>
            _resetTouchpadEnableLongPressCommand ??= new RelayCommand(() => TouchpadEnableLongPress = false);

        private RelayCommand _resetTouchpadLongPressTimeWindowMsCommand;
        public RelayCommand ResetTouchpadLongPressTimeWindowMsCommand =>
            _resetTouchpadLongPressTimeWindowMsCommand ??= new RelayCommand(() => TouchpadLongPressTimeWindowMs = 500);

        private RelayCommand _resetTouchpadEnableTwoFingerSwipesCommand;
        public RelayCommand ResetTouchpadEnableTwoFingerSwipesCommand =>
            _resetTouchpadEnableTwoFingerSwipesCommand ??= new RelayCommand(() => TouchpadEnableTwoFingerSwipes = false);

        private RelayCommand _resetTouchpadEnablePinchSpreadCommand;
        public RelayCommand ResetTouchpadEnablePinchSpreadCommand =>
            _resetTouchpadEnablePinchSpreadCommand ??= new RelayCommand(() => TouchpadEnablePinchSpread = false);

        private RelayCommand _resetTouchpadEnableRotateCommand;
        public RelayCommand ResetTouchpadEnableRotateCommand =>
            _resetTouchpadEnableRotateCommand ??= new RelayCommand(() => TouchpadEnableRotate = false);

        private RelayCommand _resetTouchpadEnableThreeFingerGesturesCommand;
        public RelayCommand ResetTouchpadEnableThreeFingerGesturesCommand =>
            _resetTouchpadEnableThreeFingerGesturesCommand ??= new RelayCommand(() => TouchpadEnableThreeFingerGestures = false);

        private RelayCommand _resetTouchpadEnableFourFingerGesturesCommand;
        public RelayCommand ResetTouchpadEnableFourFingerGesturesCommand =>
            _resetTouchpadEnableFourFingerGesturesCommand ??= new RelayCommand(() => TouchpadEnableFourFingerGestures = false);

        private RelayCommand _resetTouchpadEnableFiveFingerGesturesCommand;
        public RelayCommand ResetTouchpadEnableFiveFingerGesturesCommand =>
            _resetTouchpadEnableFiveFingerGesturesCommand ??= new RelayCommand(() => TouchpadEnableFiveFingerGestures = false);

        private RelayCommand _resetTouchpadEnableShapeGesturesCommand;
        public RelayCommand ResetTouchpadEnableShapeGesturesCommand =>
            _resetTouchpadEnableShapeGesturesCommand ??= new RelayCommand(() => TouchpadEnableShapeGestures = false);

        private RelayCommand _resetTouchpadGestureMatchThresholdCommand;
        public RelayCommand ResetTouchpadGestureMatchThresholdCommand =>
            _resetTouchpadGestureMatchThresholdCommand ??= new RelayCommand(() => TouchpadGestureMatchThreshold = 3.0);

        private RelayCommand _resetTouchpadDetectionCardCommand;

        /// <summary>Reset every Detection-card field to defaults.</summary>
        public RelayCommand ResetTouchpadDetectionCardCommand =>
            _resetTouchpadDetectionCardCommand ??= new RelayCommand(() =>
            {
                TouchpadGesturesEnabled = false;
                TouchpadGestureMode = "Both";
                TouchpadCooldownMs = 100;
            });

        private RelayCommand _resetTouchpadInBoxCardCommand;

        /// <summary>Reset every In-box-gestures card field to defaults.</summary>
        public RelayCommand ResetTouchpadInBoxCardCommand =>
            _resetTouchpadInBoxCardCommand ??= new RelayCommand(() =>
            {
                TouchpadEnableTouchSpots = false;
                TouchpadEnableFourWaySwipes = false;
                TouchpadEnableEightWaySwipes = false;
                TouchpadSwipeDistanceThreshold = 0.15;
                TouchpadSwipeTimeWindowMs = 500;
                TouchpadEnableRadialZones = false;
                TouchpadRadialZoneCount = 8;
                TouchpadRadialCenterDeadzone = 0.30;
                TouchpadEnableTaps = false;
                TouchpadTapTimeWindowMs = 350;
                TouchpadMultiTapGapMs = 300;
                TouchpadEnableLongPress = false;
                TouchpadLongPressTimeWindowMs = 500;
                TouchpadEnableTwoFingerSwipes = false;
                TouchpadEnablePinchSpread = false;
                TouchpadEnableRotate = false;
                TouchpadEnableThreeFingerGestures = false;
                TouchpadEnableFourFingerGestures = false;
                TouchpadEnableFiveFingerGestures = false;
                TouchpadEnableShapeGestures = false;
                TouchpadGestureMatchThreshold = 3.0;
            });

        // ─── Joystick / D-pad card reset commands ─────

        private RelayCommand _resetTouchpadEnableJoystickOutputCommand;
        public RelayCommand ResetTouchpadEnableJoystickOutputCommand =>
            _resetTouchpadEnableJoystickOutputCommand ??= new RelayCommand(() => TouchpadEnableJoystickOutput = false);

        private RelayCommand _resetTouchpadJoystickMaxRadiusCommand;
        public RelayCommand ResetTouchpadJoystickMaxRadiusCommand =>
            _resetTouchpadJoystickMaxRadiusCommand ??= new RelayCommand(() => TouchpadJoystickMaxRadius = 0.30);

        private RelayCommand _resetTouchpadJoystickInnerDeadzoneCommand;
        public RelayCommand ResetTouchpadJoystickInnerDeadzoneCommand =>
            _resetTouchpadJoystickInnerDeadzoneCommand ??= new RelayCommand(() => TouchpadJoystickInnerDeadzone = 0.02);

        private RelayCommand _resetTouchpadJoystickDPadModeCommand;
        public RelayCommand ResetTouchpadJoystickDPadModeCommand =>
            _resetTouchpadJoystickDPadModeCommand ??= new RelayCommand(() => TouchpadJoystickDPadMode = "FourWay");

        private RelayCommand _resetTouchpadJoystickDPadActivationThresholdCommand;
        public RelayCommand ResetTouchpadJoystickDPadActivationThresholdCommand =>
            _resetTouchpadJoystickDPadActivationThresholdCommand ??= new RelayCommand(() => TouchpadJoystickDPadActivationThreshold = 0.15);

        private RelayCommand _resetTouchpadJoystickCardCommand;

        /// <summary>Reset every Joystick / D-pad card field to defaults.</summary>
        public RelayCommand ResetTouchpadJoystickCardCommand =>
            _resetTouchpadJoystickCardCommand ??= new RelayCommand(() =>
            {
                TouchpadEnableJoystickOutput = false;
                TouchpadJoystickMaxRadius = 0.30;
                TouchpadJoystickInnerDeadzone = 0.02;
                TouchpadJoystickDPadMode = "FourWay";
                TouchpadJoystickDPadActivationThreshold = 0.15;
            });

        // ─── Mouse-output card reset commands ─────────

        private RelayCommand _resetTouchpadMouseSensitivityXCommand;
        public RelayCommand ResetTouchpadMouseSensitivityXCommand =>
            _resetTouchpadMouseSensitivityXCommand ??= new RelayCommand(() => TouchpadMouseSensitivityX = 1.0);

        private RelayCommand _resetTouchpadMouseSensitivityYCommand;
        public RelayCommand ResetTouchpadMouseSensitivityYCommand =>
            _resetTouchpadMouseSensitivityYCommand ??= new RelayCommand(() => TouchpadMouseSensitivityY = 1.0);

        private RelayCommand _resetTouchpadMouseInvertXCommand;
        public RelayCommand ResetTouchpadMouseInvertXCommand =>
            _resetTouchpadMouseInvertXCommand ??= new RelayCommand(() => TouchpadMouseInvertX = false);

        private RelayCommand _resetTouchpadMouseInvertYCommand;
        public RelayCommand ResetTouchpadMouseInvertYCommand =>
            _resetTouchpadMouseInvertYCommand ??= new RelayCommand(() => TouchpadMouseInvertY = false);

        private RelayCommand _resetTouchpadMouseCardCommand;

        /// <summary>Reset every Mouse-output card field to defaults.</summary>
        public RelayCommand ResetTouchpadMouseCardCommand =>
            _resetTouchpadMouseCardCommand ??= new RelayCommand(() =>
            {
                TouchpadMouseSensitivityX = 1.0;
                TouchpadMouseSensitivityY = 1.0;
                TouchpadMouseInvertX = false;
                TouchpadMouseInvertY = false;
            });

        // ─── Absolute-pointer card reset commands (#9 B-15) ─────

        private RelayCommand _resetTouchpadPointerStretchXCommand;
        public RelayCommand ResetTouchpadPointerStretchXCommand =>
            _resetTouchpadPointerStretchXCommand ??= new RelayCommand(() => TouchpadPointerStretchX = 1.0);

        private RelayCommand _resetTouchpadPointerStretchYCommand;
        public RelayCommand ResetTouchpadPointerStretchYCommand =>
            _resetTouchpadPointerStretchYCommand ??= new RelayCommand(() => TouchpadPointerStretchY = 1.0);

        private RelayCommand _resetTouchpadPointerCardCommand;

        /// <summary>Reset every Absolute-pointer card field to defaults.</summary>
        public RelayCommand ResetTouchpadPointerCardCommand =>
            _resetTouchpadPointerCardCommand ??= new RelayCommand(() =>
            {
                TouchpadPointerStretchX = 1.0;
                TouchpadPointerStretchY = 1.0;
            });

        // ─── Swipe-haptics card reset commands ────────

        private RelayCommand _resetTouchpadSwipeHapticsEnabledCommand;
        public RelayCommand ResetTouchpadSwipeHapticsEnabledCommand =>
            _resetTouchpadSwipeHapticsEnabledCommand ??= new RelayCommand(() => TouchpadSwipeHapticsEnabled = false);

        private RelayCommand _resetTouchpadSwipeHapticsIntensityCommand;
        public RelayCommand ResetTouchpadSwipeHapticsIntensityCommand =>
            _resetTouchpadSwipeHapticsIntensityCommand ??= new RelayCommand(() => TouchpadSwipeHapticsIntensity = 0.5);

        private RelayCommand _resetTouchpadSwipeHapticsCardCommand;

        /// <summary>Reset every Swipe-haptics card field to defaults.</summary>
        public RelayCommand ResetTouchpadSwipeHapticsCardCommand =>
            _resetTouchpadSwipeHapticsCardCommand ??= new RelayCommand(() =>
            {
                TouchpadSwipeHapticsEnabled = false;
                TouchpadSwipeHapticsIntensity = 0.5;
            });

        // ─── Synthetic-pressure card reset commands (#239) ────────
        // The two fields live on the per-device DeviceSlotConfig, so the
        // resets write through the DeviceConfig anchor and follow the
        // selected device (the ResetAudioMirrorCommand pattern).

        private RelayCommand _resetTouchpadSyntheticPressureEnabledCommand;
        public RelayCommand ResetTouchpadSyntheticPressureEnabledCommand =>
            _resetTouchpadSyntheticPressureEnabledCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.TouchpadSyntheticPressure = false;
            });

        private RelayCommand _resetTouchpadSyntheticTouchPercentCommand;
        public RelayCommand ResetTouchpadSyntheticTouchPercentCommand =>
            _resetTouchpadSyntheticTouchPercentCommand ??= new RelayCommand(() =>
            {
                if (DeviceConfig != null) DeviceConfig.TouchpadSyntheticTouchPercent = 50;
            });

        private RelayCommand _resetTouchpadSyntheticPressureCardCommand;

        /// <summary>Reset every Synthetic Pressure card field to defaults.</summary>
        public RelayCommand ResetTouchpadSyntheticPressureCardCommand =>
            _resetTouchpadSyntheticPressureCardCommand ??= new RelayCommand(() =>
            {
                var cfg = DeviceConfig;
                if (cfg == null) return;
                cfg.TouchpadSyntheticPressure = false;
                cfg.TouchpadSyntheticTouchPercent = 50;
            });

        // ─── Per-pad pivot / topology helpers ─────────

        /// <summary>Update <see cref="MaxTouchpadIndex"/> from the
        /// currently selected device. Touchpad-incapable devices set it
        /// to 0 (which hides the tab via SyncTabVisibility).</summary>
        public void RecomputeTouchpadCountForActiveDevice(int padCount)
        {
            MaxTouchpadIndex = Math.Max(0, padCount);
            if (_selectedTouchpadIndex >= MaxTouchpadIndex)
                SelectedTouchpadIndex = 0;
        }

        // ─── Load / sync against PadSetting.TouchpadSettings ──────

        /// <summary>Reads the per-device gesture settings from
        /// <see cref="PadSetting.TouchpadSettings"/> into VM fields,
        /// resolving the active device's winning entry via
        /// <see cref="TouchpadGestureSettings.ResolveForDevice"/>. Called
        /// when the active device / slot / tab changes. Sets
        /// <see cref="_loadingTouchpadGestures"/> for the duration so
        /// setters don't ping-pong back to PadSetting.</summary>
        public void LoadTouchpadGestureSettingsForActiveDevice()
        {
            var us = GetActiveUserSettingForTouchpad(out var guid);
            var ps = us?.GetPadSetting();
            var s = ResolveTouchpadGestureSettings(ps, guid.ToString());
            _loadingTouchpadGestures = true;
            try
            {
                TouchpadGesturesEnabled = s.Enabled;
                TouchpadGestureMode = s.Mode ?? "Both";
                TouchpadCooldownMs = s.CooldownMs;
                TouchpadSwipeDistanceThreshold = s.SwipeDistanceThreshold;
                TouchpadSwipeTimeWindowMs = s.SwipeTimeWindowMs;
                TouchpadEnableFourWaySwipes = s.EnableFourWaySwipes;
                TouchpadEnableEightWaySwipes = s.EnableEightWaySwipes;
                TouchpadEnableRadialZones = s.EnableRadialZones;
                TouchpadRadialZoneCount = s.RadialZoneCount;
                TouchpadRadialCenterDeadzone = s.RadialCenterDeadzone;
                TouchpadEnableTouchSpots = s.EnableTouchSpots;
                TouchpadEnableTaps = s.EnableTaps;
                TouchpadTapTimeWindowMs = s.TapTimeWindowMs;
                TouchpadMultiTapGapMs = s.MultiTapGapMs;
                TouchpadEnableLongPress = s.EnableLongPress;
                TouchpadLongPressTimeWindowMs = s.LongPressTimeWindowMs;
                TouchpadEnableTwoFingerSwipes = s.EnableTwoFingerSwipes;
                TouchpadEnablePinchSpread = s.EnablePinchSpread;
                TouchpadEnableRotate = s.EnableRotate;
                TouchpadEnableThreeFingerGestures = s.EnableThreeFingerGestures;
                TouchpadEnableFourFingerGestures = s.EnableFourFingerGestures;
                TouchpadEnableFiveFingerGestures = s.EnableFiveFingerGestures;
                TouchpadEnableShapeGestures = s.EnableShapeGestures;
                TouchpadGestureMatchThreshold = s.GestureMatchThreshold;
                TouchpadEnableJoystickOutput = s.EnableJoystickOutput;
                TouchpadJoystickMaxRadius = s.JoystickMaxRadius;
                TouchpadJoystickInnerDeadzone = s.JoystickInnerDeadzone;
                TouchpadJoystickDPadMode = s.JoystickDPadMode ?? "FourWay";
                TouchpadJoystickDPadActivationThreshold = s.JoystickDPadActivationThreshold;
                TouchpadMouseSensitivityX = s.MouseSensitivityX;
                TouchpadMouseSensitivityY = s.MouseSensitivityY;
                TouchpadMouseInvertX = s.MouseInvertX;
                TouchpadMouseInvertY = s.MouseInvertY;
                TouchpadPointerStretchX = s.PointerStretchX;
                TouchpadPointerStretchY = s.PointerStretchY;
                TouchpadSwipeHapticsEnabled = s.EnableSwipeHaptics;
                TouchpadSwipeHapticsIntensity = s.SwipeHapticsIntensity;
            }
            finally { _loadingTouchpadGestures = false; }
        }

        /// <summary>Writes VM fields back to the per-(device, pad)
        /// entry. Creates the entry on first write. Public so the
        /// settings-save path can flush before XML serialization.
        ///
        /// <para>No-op while LoadTouchpadGestureSettingsForActiveDevice
        /// is running. That loader sets each VM field in sequence; each
        /// fires PropertyChanged; any external caller (e.g.
        /// MainWindow's pad.PropertyChanged hook for the Touchpad tab)
        /// that calls Sync from inside the PropertyChanged dispatch
        /// would otherwise write the VM's not-yet-loaded fields back
        /// to PadSetting and clobber the on-disk state. Symptom: every
        /// touchpad-tab toggle except <c>TouchpadGesturesEnabled</c>
        /// (which gets set first in the load and so is the only field
        /// holding the right value when the stampede fires) reverts
        /// across relaunches. PushIfNotLoading does the same check for
        /// the in-class setter path; this gate covers external callers
        /// too.</para></summary>
        public void SyncTouchpadGestureSettingsToActiveDevice()
        {
            if (_loadingTouchpadGestures) return;
            var us = GetActiveUserSettingForTouchpad(out _);
            var ps = us?.GetPadSetting();
            if (ps == null) return;

            string guidStr = us.InstanceGuid.ToString();

            var list = ps.TouchpadSettings != null
                ? new List<TouchpadSettingsEntry>(ps.TouchpadSettings)
                : new List<TouchpadSettingsEntry>();
            // Settings are per-device: match the entry by DeviceGuid ONLY
            // (the selected pad index no longer partitions them).
            TouchpadSettingsEntry entry = null;
            foreach (var e in list)
            {
                if (e == null) continue;
                if (!string.Equals(e.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase)) continue;
                entry = e; break;
            }
            if (entry == null)
            {
                entry = new TouchpadSettingsEntry
                {
                    DeviceGuid = guidStr,
                    TouchpadIndex = 0,
                    Settings = TouchpadGestureSettings.Default(),
                };
                list.Add(entry);
            }
            var s = entry.Settings ?? TouchpadGestureSettings.Default();
            s.Enabled = TouchpadGesturesEnabled;
            s.Mode = string.IsNullOrEmpty(TouchpadGestureMode) ? "Both" : TouchpadGestureMode;
            s.CooldownMs = TouchpadCooldownMs;
            s.SwipeDistanceThreshold = (float)TouchpadSwipeDistanceThreshold;
            s.SwipeTimeWindowMs = TouchpadSwipeTimeWindowMs;
            s.EnableFourWaySwipes = TouchpadEnableFourWaySwipes;
            s.EnableEightWaySwipes = TouchpadEnableEightWaySwipes;
            s.EnableRadialZones = TouchpadEnableRadialZones;
            s.RadialZoneCount = TouchpadRadialZoneCount;
            s.RadialCenterDeadzone = (float)TouchpadRadialCenterDeadzone;
            s.EnableTouchSpots = TouchpadEnableTouchSpots;
            s.EnableTaps = TouchpadEnableTaps;
            s.TapTimeWindowMs = TouchpadTapTimeWindowMs;
            s.MultiTapGapMs = TouchpadMultiTapGapMs;
            s.EnableLongPress = TouchpadEnableLongPress;
            s.LongPressTimeWindowMs = TouchpadLongPressTimeWindowMs;
            s.EnableTwoFingerSwipes = TouchpadEnableTwoFingerSwipes;
            s.EnablePinchSpread = TouchpadEnablePinchSpread;
            s.EnableRotate = TouchpadEnableRotate;
            s.EnableThreeFingerGestures = TouchpadEnableThreeFingerGestures;
            s.EnableFourFingerGestures = TouchpadEnableFourFingerGestures;
            s.EnableFiveFingerGestures = TouchpadEnableFiveFingerGestures;
            s.EnableShapeGestures = TouchpadEnableShapeGestures;
            s.GestureMatchThreshold = (float)TouchpadGestureMatchThreshold;
            s.EnableJoystickOutput = TouchpadEnableJoystickOutput;
            s.JoystickMaxRadius = (float)TouchpadJoystickMaxRadius;
            s.JoystickInnerDeadzone = (float)TouchpadJoystickInnerDeadzone;
            s.JoystickDPadMode = string.IsNullOrEmpty(TouchpadJoystickDPadMode) ? "FourWay" : TouchpadJoystickDPadMode;
            s.JoystickDPadActivationThreshold = (float)TouchpadJoystickDPadActivationThreshold;
            s.MouseSensitivityX = (float)TouchpadMouseSensitivityX;
            s.MouseSensitivityY = (float)TouchpadMouseSensitivityY;
            s.MouseInvertX = TouchpadMouseInvertX;
            s.MouseInvertY = TouchpadMouseInvertY;
            s.PointerStretchX = (float)TouchpadPointerStretchX;
            s.PointerStretchY = (float)TouchpadPointerStretchY;
            s.EnableSwipeHaptics = TouchpadSwipeHapticsEnabled;
            s.SwipeHapticsIntensity = (float)TouchpadSwipeHapticsIntensity;
            entry.Settings = s;

            // Canonicalize to exactly one entry per device, stamped index 0.
            // Stamp UNCONDITIONALLY (not only in the new-entry branch) so a
            // legacy [pad1, pad0] array can't leave the survivor at index 1,
            // then prune any other same-device sibling so no stale entry
            // shadows the per-device resolver.
            entry.TouchpadIndex = 0;
            list.RemoveAll(e => e != null && !ReferenceEquals(e, entry)
                && string.Equals(e.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase));

            ps.TouchpadSettings = list.ToArray();
        }

        /// <summary>Repopulate <see cref="CustomTouchpadGestures"/> from
        /// the supplied gesture list (typically the active profile's
        /// <c>ProfileData.TouchpadGestures</c>). Pass null to clear.
        /// Called by InputService after profile load / switch.</summary>
        public void RefreshCustomTouchpadGestures(IEnumerable<TouchpadCustomGesture> gestures)
        {
            CustomTouchpadGestures.Clear();
            if (gestures != null)
            {
                foreach (var g in gestures)
                {
                    if (g == null || string.IsNullOrWhiteSpace(g.Name)) continue;
                    CustomTouchpadGestures.Add(new TouchpadCustomGestureItem(g));
                }
            }
            OnPropertyChanged(nameof(HasNoCustomTouchpadGestures));
        }

        // ─── Internals ────────────────────────────────

        private void PushIfNotLoading()
        {
            if (_loadingTouchpadGestures) return;
            SyncTouchpadGestureSettingsToActiveDevice();
        }

        /// <summary>Pick the UserSetting whose finger paths the recorder
        /// should mirror. A slot can have several mapped devices and the
        /// user-selected one isn't necessarily touchpad-capable (a slot
        /// with All Keyboards (Merged) + DualSense leaves SelectedMappedDevice
        /// on the keyboard half the time). Walk every UserSetting on the
        /// slot, intersect with UserDevices to test HasTouchpad, and
        /// pick the first qualifying one — preferring the selected
        /// device if it qualifies, then the first online touchpad-capable
        /// device, then any touchpad-capable device. Returns null only
        /// when no device on the slot has a touchpad at all.</summary>
        private UserSetting GetActiveUserSettingForTouchpad(out Guid deviceGuid)
        {
            deviceGuid = Guid.Empty;
            var settings = SettingsManager.UserSettings;
            if (settings == null) return null;

            // Snapshot slot settings under their own lock; the device
            // lookup below takes a different SyncRoot so don't nest.
            var slotSettings = new List<UserSetting>(4);
            lock (settings.SyncRoot)
            {
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    var us = settings.Items[i];
                    if (us != null && us.MapTo == PadIndex)
                        slotSettings.Add(us);
                }
            }
            if (slotSettings.Count == 0) return null;

            Guid selectedGuid = SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;
            var devices = SettingsManager.UserDevices;

            UserSetting selectedMatch = null;
            UserSetting firstOnlineTouchpad = null;
            UserSetting firstAnyTouchpad = null;

            foreach (var us in slotSettings)
            {
                UserDevice ud = null;
                if (devices != null)
                {
                    lock (devices.SyncRoot)
                    {
                        for (int j = 0; j < devices.Items.Count; j++)
                        {
                            var d = devices.Items[j];
                            if (d != null && d.InstanceGuid == us.InstanceGuid)
                            {
                                ud = d;
                                break;
                            }
                        }
                    }
                }
                if (ud == null || !ud.HasTouchpad) continue;

                firstAnyTouchpad ??= us;
                if (ud.IsOnline) firstOnlineTouchpad ??= us;
                if (us.InstanceGuid == selectedGuid) { selectedMatch = us; break; }
            }

            var chosen = selectedMatch ?? firstOnlineTouchpad ?? firstAnyTouchpad;
            if (chosen != null) deviceGuid = chosen.InstanceGuid;
            return chosen;
        }

        // Per-device resolution keyed by the active device guid (the prior
        // TouchpadIndex-only match never checked DeviceGuid, a latent
        // two-device-one-slot bug). Funnels through the same shared resolver
        // as every runtime read seam.
        private static TouchpadGestureSettings ResolveTouchpadGestureSettings(PadSetting ps, string guidStr)
            => TouchpadGestureSettings.ResolveForDevice(ps?.TouchpadSettings, guidStr);
    }

    /// <summary>Payload carried by
    /// <see cref="PadViewModel.RecordTouchpadGestureRequested"/> so the
    /// View can open the recorder dialog with the right (device, pad)
    /// to mirror live finger input from.</summary>
    public sealed class RecordTouchpadGestureArgs : EventArgs
    {
        public Guid DeviceGuid { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public int PadIndex { get; set; }
    }

    /// <summary>UI-facing wrapper around a <see cref="TouchpadCustomGesture"/>
    /// so list items have a display-friendly summary and the original
    /// gesture reference for delete / edit hooks.</summary>
    public sealed class TouchpadCustomGestureItem
    {
        public TouchpadCustomGesture Source { get; }
        public string Name => Source?.Name ?? string.Empty;
        public int FingerCount => Source?.FingerPaths?.Count ?? 1;
        public string Summary => FingerCount == 1
            ? Strings.Instance.Pad_Touchpad_CustomGesture_OneFinger
            : string.Format(Strings.Instance.Pad_Touchpad_CustomGesture_NFingers_Format, FingerCount);

        public TouchpadCustomGestureItem(TouchpadCustomGesture source) { Source = source; }
    }
}
