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

        /// <summary>Which pad on the active device the tab is editing
        /// (0..MaxTouchpadIndex-1). Devices with one pad pin this to 0
        /// and hide the pivot. Changing the value reloads VM fields
        /// from the corresponding TouchpadSettings entry.</summary>
        public int SelectedTouchpadIndex
        {
            get => _selectedTouchpadIndex;
            set
            {
                if (value < 0) value = 0;
                if (SetProperty(ref _selectedTouchpadIndex, value))
                    LoadTouchpadGestureSettingsForActiveDevice();
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

        private bool _touchpadGesturesEnabled = true;
        public bool TouchpadGesturesEnabled
        {
            get => _touchpadGesturesEnabled;
            set { if (SetProperty(ref _touchpadGesturesEnabled, value)) PushIfNotLoading(); }
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
                if (SetProperty(ref _touchpadGestureMode, s)) PushIfNotLoading();
            }
        }

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

        private bool _touchpadEnableFourWaySwipes = true;
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

        private bool _touchpadEnableTaps = true;
        public bool TouchpadEnableTaps
        {
            get => _touchpadEnableTaps;
            set { if (SetProperty(ref _touchpadEnableTaps, value)) PushIfNotLoading(); }
        }

        private int _touchpadTapTimeWindowMs = 200;
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

        private bool _touchpadEnableLongPress = true;
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

        private bool _touchpadEnableTwoFingerSwipes = true;
        public bool TouchpadEnableTwoFingerSwipes
        {
            get => _touchpadEnableTwoFingerSwipes;
            set { if (SetProperty(ref _touchpadEnableTwoFingerSwipes, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnablePinchSpread = true;
        public bool TouchpadEnablePinchSpread
        {
            get => _touchpadEnablePinchSpread;
            set { if (SetProperty(ref _touchpadEnablePinchSpread, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableRotate = true;
        public bool TouchpadEnableRotate
        {
            get => _touchpadEnableRotate;
            set { if (SetProperty(ref _touchpadEnableRotate, value)) PushIfNotLoading(); }
        }

        private bool _touchpadEnableThreeFingerGestures = true;
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

        private double _touchpadGestureMatchThreshold = 2.5;

        /// <summary>$P recognizer matching threshold. Lower = stricter
        /// matches; higher = looser. Default 2.5 from the recipe.</summary>
        public double TouchpadGestureMatchThreshold
        {
            get => _touchpadGestureMatchThreshold;
            set
            {
                var v = Math.Clamp(value, 0.1, 10.0);
                if (SetProperty(ref _touchpadGestureMatchThreshold, v)) PushIfNotLoading();
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

        /// <summary>Opens the recorder dialog (TouchpadGestureRecorderDialog,
        /// wired in C8). Raised so the View can show the dialog without
        /// the VM taking a UI dependency.</summary>
        public RelayCommand RecordTouchpadGestureCommand =>
            _recordTouchpadGestureCommand ??= new RelayCommand(
                () => RecordTouchpadGestureRequested?.Invoke(this, EventArgs.Empty));

        public event EventHandler RecordTouchpadGestureRequested;

        private RelayCommand<TouchpadCustomGestureItem> _deleteTouchpadGestureCommand;

        public RelayCommand<TouchpadCustomGestureItem> DeleteTouchpadGestureCommand =>
            _deleteTouchpadGestureCommand ??= new RelayCommand<TouchpadCustomGestureItem>(item =>
            {
                if (item == null) return;
                DeleteTouchpadGestureRequested?.Invoke(this, item);
            });

        public event EventHandler<TouchpadCustomGestureItem> DeleteTouchpadGestureRequested;

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

        /// <summary>Reads the per-(device, pad) gesture settings from
        /// <see cref="PadSetting.TouchpadSettings"/> into VM fields.
        /// Called when the active device or selected touchpad index
        /// changes. Sets <see cref="_loadingTouchpadGestures"/> for the
        /// duration so setters don't ping-pong back to PadSetting.</summary>
        public void LoadTouchpadGestureSettingsForActiveDevice()
        {
            var ps = GetActivePadSettingForTouchpad();
            var s = ResolveTouchpadGestureSettings(ps, _selectedTouchpadIndex);
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
            }
            finally { _loadingTouchpadGestures = false; }
        }

        /// <summary>Writes VM fields back to the per-(device, pad)
        /// entry. Creates the entry on first write. Public so the
        /// settings-save path can flush before XML serialization.</summary>
        public void SyncTouchpadGestureSettingsToActiveDevice()
        {
            var us = GetActiveUserSettingForTouchpad(out _);
            var ps = us?.GetPadSetting();
            if (ps == null) return;

            string guidStr = us.InstanceGuid.ToString();
            int padIdx = _selectedTouchpadIndex;

            var list = ps.TouchpadSettings != null
                ? new List<TouchpadSettingsEntry>(ps.TouchpadSettings)
                : new List<TouchpadSettingsEntry>();
            TouchpadSettingsEntry entry = null;
            foreach (var e in list)
            {
                if (e == null) continue;
                if (e.TouchpadIndex != padIdx) continue;
                if (!string.Equals(e.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase)) continue;
                entry = e; break;
            }
            if (entry == null)
            {
                entry = new TouchpadSettingsEntry
                {
                    DeviceGuid = guidStr,
                    TouchpadIndex = padIdx,
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
            entry.Settings = s;

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

        private PadSetting GetActivePadSettingForTouchpad()
        {
            var us = GetActiveUserSettingForTouchpad(out _);
            return us?.GetPadSetting();
        }

        private UserSetting GetActiveUserSettingForTouchpad(out Guid deviceGuid)
        {
            deviceGuid = Guid.Empty;
            var sel = SelectedMappedDevice;
            if (sel == null || sel.InstanceGuid == Guid.Empty) return null;
            var settings = SettingsManager.UserSettings;
            if (settings == null) return null;
            lock (settings.SyncRoot)
            {
                for (int i = 0; i < settings.Items.Count; i++)
                {
                    var us = settings.Items[i];
                    if (us == null) continue;
                    if (us.InstanceGuid != sel.InstanceGuid) continue;
                    if (us.MapTo != PadIndex) continue;
                    deviceGuid = us.InstanceGuid;
                    return us;
                }
            }
            return null;
        }

        private static TouchpadGestureSettings ResolveTouchpadGestureSettings(PadSetting ps, int padIdx)
        {
            if (ps?.TouchpadSettings == null) return TouchpadGestureSettings.Default();
            for (int i = 0; i < ps.TouchpadSettings.Length; i++)
            {
                var e = ps.TouchpadSettings[i];
                if (e == null) continue;
                if (e.TouchpadIndex == padIdx)
                    return e.Settings ?? TouchpadGestureSettings.Default();
            }
            return TouchpadGestureSettings.Default();
        }
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
