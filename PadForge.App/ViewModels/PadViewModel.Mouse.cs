using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Engine.Data;
using PadForge.Engine.Mouse;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Mouse tab state (issue #200): per-(slot, device) mouse-gesture
    /// settings, twin of the Touchpad partial's Load/Sync/guard pattern.
    /// Persistence rides setter-time direct writes into the live PadSetting
    /// (Sync) plus MainWindow's PropertyChanged hook for MarkDirty; the
    /// 30 Hz VM flush does NOT carry these fields, same as the touchpad
    /// sub-tree.
    /// </summary>
    public partial class PadViewModel
    {
        // Guards the Sync half while the loader assigns VM properties, so
        // setters don't ping-pong half-loaded values back into PadSetting.
        // Same trap as _loadingTouchpadGestures: MainWindow's PropertyChanged
        // hook calls Sync mid-load without this.
        private bool _loadingMouseGestures;

        private bool _mouseGesturesEnabled;
        public bool MouseGesturesEnabled
        {
            get => _mouseGesturesEnabled;
            set { if (SetProperty(ref _mouseGesturesEnabled, value)) PushMouseGesturesIfNotLoading(); }
        }

        private int _mouseGestureButtons = 1 << 3; // X1 only
        /// <summary>Bitmask of the mouse buttons arming the recognizer:
        /// bit 0 Left, bit 1 Middle, bit 2 Right, bit 3 X1, bit 4 X2
        /// (SdlMouseWrapper order). One, some, or all buttons can be gesture
        /// buttons. The five bool wrappers below bind the checkboxes.</summary>
        public int MouseGestureButtons
        {
            get => _mouseGestureButtons;
            set
            {
                if (SetProperty(ref _mouseGestureButtons, value & 0x1F))
                {
                    OnPropertyChanged(nameof(MouseGestureButtonLeft));
                    OnPropertyChanged(nameof(MouseGestureButtonMiddle));
                    OnPropertyChanged(nameof(MouseGestureButtonRight));
                    OnPropertyChanged(nameof(MouseGestureButtonX1));
                    OnPropertyChanged(nameof(MouseGestureButtonX2));
                    PushMouseGesturesIfNotLoading();
                }
            }
        }

        private void SetGestureButtonBit(int bit, bool on)
            => MouseGestureButtons = on ? (MouseGestureButtons | (1 << bit))
                                        : (MouseGestureButtons & ~(1 << bit));

        public bool MouseGestureButtonLeft
        {
            get => (_mouseGestureButtons & (1 << 0)) != 0;
            set => SetGestureButtonBit(0, value);
        }
        public bool MouseGestureButtonMiddle
        {
            get => (_mouseGestureButtons & (1 << 1)) != 0;
            set => SetGestureButtonBit(1, value);
        }
        public bool MouseGestureButtonRight
        {
            get => (_mouseGestureButtons & (1 << 2)) != 0;
            set => SetGestureButtonBit(2, value);
        }
        public bool MouseGestureButtonX1
        {
            get => (_mouseGestureButtons & (1 << 3)) != 0;
            set => SetGestureButtonBit(3, value);
        }
        public bool MouseGestureButtonX2
        {
            get => (_mouseGestureButtons & (1 << 4)) != 0;
            set => SetGestureButtonBit(4, value);
        }

        private int _mouseGestureFlickThreshold = 150;
        public int MouseGestureFlickThreshold
        {
            get => _mouseGestureFlickThreshold;
            set { if (SetProperty(ref _mouseGestureFlickThreshold, Math.Clamp(value, 10, 5000))) PushMouseGesturesIfNotLoading(); }
        }

        private int _mouseGestureCooldownMs = 100;
        public int MouseGestureCooldownMs
        {
            get => _mouseGestureCooldownMs;
            set { if (SetProperty(ref _mouseGestureCooldownMs, Math.Clamp(value, 0, 5000))) PushMouseGesturesIfNotLoading(); }
        }

        private RelayCommand _resetMouseGesturesEnabledCommand;
        public RelayCommand ResetMouseGesturesEnabledCommand =>
            _resetMouseGesturesEnabledCommand ??= new RelayCommand(() => MouseGesturesEnabled = false);

        private RelayCommand _resetMouseGestureButtonCommand;
        public RelayCommand ResetMouseGestureButtonCommand =>
            _resetMouseGestureButtonCommand ??= new RelayCommand(() => MouseGestureButtons = 1 << 3);

        private RelayCommand _resetMouseGestureFlickThresholdCommand;
        public RelayCommand ResetMouseGestureFlickThresholdCommand =>
            _resetMouseGestureFlickThresholdCommand ??= new RelayCommand(() => MouseGestureFlickThreshold = 150);

        private RelayCommand _resetMouseGestureCooldownCommand;
        public RelayCommand ResetMouseGestureCooldownCommand =>
            _resetMouseGestureCooldownCommand ??= new RelayCommand(() => MouseGestureCooldownMs = 100);

        private RelayCommand _resetMouseGesturesCardCommand;
        public RelayCommand ResetMouseGesturesCardCommand =>
            _resetMouseGesturesCardCommand ??= new RelayCommand(() =>
            {
                MouseGesturesEnabled = false;
                MouseGestureButtons = 1 << 3;
                MouseGestureFlickThreshold = 150;
                MouseGestureCooldownMs = 100;
            });

        /// <summary>Loads the active mouse device's gesture settings into the
        /// VM. Missing settings load as defaults, so Reset to Defaults resets
        /// this tab by construction (the ab58f957 load-mirror contract).</summary>
        public void LoadMouseGestureSettingsForActiveDevice()
        {
            var us = GetActiveUserSettingForMouse();
            var ps = us?.GetPadSetting();
            var s = ResolveMouseGestureSettings(ps, us);
            _loadingMouseGestures = true;
            try
            {
                MouseGesturesEnabled = s.Enabled;
                MouseGestureButtons = s.GestureButtons;
                MouseGestureFlickThreshold = s.FlickThresholdCounts;
                MouseGestureCooldownMs = s.CooldownMs;
            }
            finally { _loadingMouseGestures = false; }
        }

        /// <summary>Writes the VM fields into the active mouse device's
        /// entry, creating it on first touch. No-op while loading.</summary>
        public void SyncMouseGestureSettingsToActiveDevice()
        {
            if (_loadingMouseGestures) return;
            var us = GetActiveUserSettingForMouse();
            var ps = us?.GetPadSetting();
            if (ps == null || us == null) return;

            string guidStr = us.InstanceGuid.ToString();
            var list = ps.MouseGestureSettings != null
                ? new List<MouseGestureSettingsEntry>(ps.MouseGestureSettings)
                : new List<MouseGestureSettingsEntry>();

            MouseGestureSettingsEntry entry = null;
            foreach (var e in list)
            {
                if (e != null && string.Equals(e.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase))
                { entry = e; break; }
            }
            if (entry == null)
            {
                entry = new MouseGestureSettingsEntry { DeviceGuid = guidStr };
                list.Add(entry);
            }

            entry.Settings = new MouseGestureSettings
            {
                Enabled = MouseGesturesEnabled,
                GestureButtons = MouseGestureButtons,
                FlickThresholdCounts = MouseGestureFlickThreshold,
                CooldownMs = MouseGestureCooldownMs,
            };
            ps.MouseGestureSettings = list.ToArray();
        }

        private void PushMouseGesturesIfNotLoading()
        {
            if (_loadingMouseGestures) return;
            SyncMouseGestureSettingsToActiveDevice();
        }

        /// <summary>Resolves the UserSetting whose device the Mouse tab
        /// edits: the selected mapped device when it is a mouse, else the
        /// first online mouse on the slot, else the first mouse. Same
        /// resolution shape as the touchpad twin, separate SyncRoots, never
        /// nested.</summary>
        private UserSetting GetActiveUserSettingForMouse()
        {
            var settings = PadForge.Common.Input.SettingsManager.UserSettings;
            var devices = PadForge.Common.Input.SettingsManager.UserDevices;
            if (settings == null || devices == null) return null;

            Guid selectedGuid = SelectedMappedDevice?.InstanceGuid ?? Guid.Empty;

            var candidates = new List<UserSetting>();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.MapTo != PadIndex) continue;
                    candidates.Add(us);
                }
            }

            UserSetting selectedMatch = null, firstOnline = null, firstAny = null;
            lock (devices.SyncRoot)
            {
                foreach (var us in candidates)
                {
                    UserDevice ud = null;
                    foreach (var d in devices.Items)
                    {
                        if (d != null && d.InstanceGuid == us.InstanceGuid) { ud = d; break; }
                    }
                    if (ud == null || !ud.IsMouse) continue;
                    firstAny ??= us;
                    if (ud.IsOnline && firstOnline == null) firstOnline = us;
                    if (us.InstanceGuid == selectedGuid && selectedMatch == null) selectedMatch = us;
                }
            }
            return selectedMatch ?? firstOnline ?? firstAny;
        }

        private static MouseGestureSettings ResolveMouseGestureSettings(PadSetting ps, UserSetting us)
        {
            if (ps?.MouseGestureSettings == null || us == null)
                return MouseGestureSettings.Default();
            string guidStr = us.InstanceGuid.ToString();
            foreach (var entry in ps.MouseGestureSettings)
            {
                if (entry?.Settings == null) continue;
                if (string.Equals(entry.DeviceGuid, guidStr, StringComparison.OrdinalIgnoreCase))
                    return entry.Settings;
            }
            return MouseGestureSettings.Default();
        }
    }
}
