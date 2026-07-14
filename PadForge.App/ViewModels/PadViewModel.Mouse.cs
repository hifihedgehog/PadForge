using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Engine.Data;
using PadForge.Engine.Mouse;
using PadForge.Resources.Strings;

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
        /// (SdlMouseWrapper order), bit 5 Custom (discussion #216, armed by
        /// the recorded cross-device input instead of a mouse button). One,
        /// some, or all buttons can be gesture buttons. The six bool
        /// wrappers below bind the checkboxes.</summary>
        public int MouseGestureButtons
        {
            get => _mouseGestureButtons;
            set
            {
                if (SetProperty(ref _mouseGestureButtons, value & 0x3F))
                {
                    OnPropertyChanged(nameof(MouseGestureButtonLeft));
                    OnPropertyChanged(nameof(MouseGestureButtonMiddle));
                    OnPropertyChanged(nameof(MouseGestureButtonRight));
                    OnPropertyChanged(nameof(MouseGestureButtonX1));
                    OnPropertyChanged(nameof(MouseGestureButtonX2));
                    OnPropertyChanged(nameof(MouseGestureButtonCustom));
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
        public bool MouseGestureButtonCustom
        {
            get => (_mouseGestureButtons & (1 << 5)) != 0;
            set => SetGestureButtonBit(5, value);
        }

        // ─── Custom activation input (discussion #216): the recorded
        //     cross-device descriptor + owning device GUID that arm the
        //     Custom gesture session while held. Same pair shape, picker
        //     projection, and record flow as the Gyro tab's Aim Engage
        //     cluster (PadViewModel.GyroAimEngageButton et al.), scoped
        //     per (slot, mouse device) with the rest of this card. ───

        private string _mouseGestureCustomEngageButton = "";
        public string MouseGestureCustomEngageButton
        {
            get => _mouseGestureCustomEngageButton;
            set
            {
                if (SetProperty(ref _mouseGestureCustomEngageButton, value ?? ""))
                {
                    OnPropertyChanged(nameof(MouseGestureCustomEngageSelectedInput));
                    PushMouseGesturesIfNotLoading();
                }
            }
        }

        private string _mouseGestureCustomEngageDeviceGuid = "";
        public string MouseGestureCustomEngageDeviceGuid
        {
            get => _mouseGestureCustomEngageDeviceGuid;
            set
            {
                if (SetProperty(ref _mouseGestureCustomEngageDeviceGuid, value ?? ""))
                {
                    OnPropertyChanged(nameof(MouseGestureCustomEngageSelectedInput));
                    PushMouseGesturesIfNotLoading();
                }
            }
        }

        /// <summary>Tells the view to re-resolve
        /// <see cref="MouseGestureCustomEngageSelectedInput"/> after
        /// <see cref="SlotAvailableInputs"/> is rebuilt, mirroring
        /// <see cref="OnGyroAimEngageSelectedInputRefresh"/>.</summary>
        public void OnMouseGestureCustomEngageSelectedInputRefresh()
            => OnPropertyChanged(nameof(MouseGestureCustomEngageSelectedInput));

        /// <summary>InputChoice projection over the custom-activation
        /// pair, twin of <see cref="GyroAimEngageSelectedInput"/>: the
        /// getter resolves the matching entry in
        /// <see cref="SlotAvailableInputs"/>; the setter writes both
        /// backing strings atomically and ignores the ComboBox's transient
        /// null write-back (the Reset button is the only clear path).</summary>
        public InputChoice MouseGestureCustomEngageSelectedInput
        {
            get
            {
                if (string.IsNullOrEmpty(_mouseGestureCustomEngageButton)) return null;
                foreach (var c in SlotAvailableInputs)
                {
                    if (c == null) continue;
                    if (string.Equals(c.Descriptor, _mouseGestureCustomEngageButton, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", _mouseGestureCustomEngageDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                        return c;
                }
                return null;
            }
            set
            {
                if (value == null) return;
                MouseGestureCustomEngageButton = value.Descriptor ?? "";
                MouseGestureCustomEngageDeviceGuid = value.DeviceGuid ?? "";
            }
        }

        /// <summary>Whether the custom-activation recorder is listening
        /// for the next physical input. Drives the record button's icon +
        /// tooltip swap, mirroring <see cref="GyroAimEngageRecording"/>.</summary>
        private bool _mouseGestureCustomEngageRecording;
        public bool MouseGestureCustomEngageRecording
        {
            get => _mouseGestureCustomEngageRecording;
            set
            {
                if (SetProperty(ref _mouseGestureCustomEngageRecording, value))
                {
                    OnPropertyChanged(nameof(MouseGestureCustomEngageRecordButtonIcon));
                    OnPropertyChanged(nameof(MouseGestureCustomEngageRecordButtonText));
                }
            }
        }
        // Stop glyph while recording, Record glyph while idle. Same
        // single-line literal convention as GyroAimEngageRecordButtonIcon.
        public string MouseGestureCustomEngageRecordButtonIcon => _mouseGestureCustomEngageRecording ? "" : "";
        public string MouseGestureCustomEngageRecordButtonText => _mouseGestureCustomEngageRecording
            ? Strings.Instance.Common_Recording
            : Strings.Instance.Common_Record;

        /// <summary>Fires when the user clicks Record next to the custom
        /// activation picker. MainWindow starts a freeform recorder session
        /// (or cancels a running one), the Aim Engage toggle pattern.</summary>
        public event EventHandler MouseGestureCustomEngageRecordRequested;
        public void FireMouseGestureCustomEngageRecord()
            => MouseGestureCustomEngageRecordRequested?.Invoke(this, EventArgs.Empty);

        private RelayCommand _mouseGestureCustomEngageRecordCommand;
        public RelayCommand MouseGestureCustomEngageRecordCommand =>
            _mouseGestureCustomEngageRecordCommand ??= new RelayCommand(FireMouseGestureCustomEngageRecord);

        private RelayCommand _resetMouseGestureCustomEngageCommand;
        public RelayCommand ResetMouseGestureCustomEngageCommand =>
            _resetMouseGestureCustomEngageCommand ??= new RelayCommand(() =>
            {
                MouseGestureCustomEngageButton = "";
                MouseGestureCustomEngageDeviceGuid = "";
            });

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
                MouseGestureCustomEngageButton = "";
                MouseGestureCustomEngageDeviceGuid = "";
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
                MouseGestureCustomEngageButton = s.CustomEngageButton ?? "";
                MouseGestureCustomEngageDeviceGuid = s.CustomEngageDeviceGuid ?? "";
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
                CustomEngageButton = MouseGestureCustomEngageButton ?? "",
                CustomEngageDeviceGuid = MouseGestureCustomEngageDeviceGuid ?? "",
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
