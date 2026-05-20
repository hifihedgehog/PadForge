using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    public class ProfileShortcutViewModel : ObservableObject
    {
        private readonly Action<ProfileShortcutViewModel> _deleteCallback;
        private readonly Action<ProfileShortcutViewModel> _saveCallback;

        public ProfileShortcutViewModel(
            GlobalMacroData data,
            Action<ProfileShortcutViewModel> deleteCallback,
            Action<ProfileShortcutViewModel> saveCallback)
        {
            Data = data ?? new GlobalMacroData();
            _deleteCallback = deleteCallback;
            _saveCallback = saveCallback;

            DeleteCommand = new RelayCommand(() => _deleteCallback?.Invoke(this));
            ClearCommand = new RelayCommand(() =>
            {
                Data.TriggerEntries = null;
                OnPropertyChanged(nameof(ButtonComboDisplay));
                _saveCallback?.Invoke(this);
            });

            _switchMode = Data.SwitchMode;

            // Persistent collections + stable-identity wrapper items.
            // The previous "return a new ObservableCollection each getter"
            // pattern replaced the ItemsSource instance on every culture
            // refresh; ComboBox responded by clearing SelectedItem and
            // writing null back through the binding setters, wiping
            // Data.TargetProfileId / TriggerDeviceGuid / SwitchMode.
            // Keeping the collection instance stable and binding selection
            // to a non-localized ID (ProfileId / DeviceGuid / Mode) means
            // culture refresh just mutates the sentinel item's DisplayName
            // in place — WPF re-renders the displayed text without touching
            // selection.
            InitSwitchModes();
            RebuildProfileChoices();
            RebuildDeviceChoices();

            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged()
        {
            // Update localized sentinel-item display names in place. Selection
            // by Mode / ProfileId / DeviceGuid is unaffected because no item
            // is added or removed.
            foreach (var item in _switchModes)
            {
                item.DisplayName = item.Mode switch
                {
                    SwitchProfileMode.Next => Strings.Instance.Profiles_ShortcutMode_Next,
                    SwitchProfileMode.Previous => Strings.Instance.Profiles_ShortcutMode_Previous,
                    SwitchProfileMode.Specific => Strings.Instance.Profiles_ShortcutMode_Specific,
                    SwitchProfileMode.ToggleWindow => Strings.Instance.Profiles_ShortcutMode_ToggleWindow,
                    _ => item.DisplayName,
                };
            }
            foreach (var p in _profileChoices)
            {
                if (string.IsNullOrEmpty(p.ProfileId))
                    p.DisplayName = Strings.Instance.Common_Default;
            }
            foreach (var d in _deviceChoices)
            {
                if (d.DeviceGuid == Guid.Empty)
                    d.DisplayName = Strings.Instance.Profiles_ShortcutDevice_Any;
            }
            OnPropertyChanged(nameof(ButtonComboDisplay));
            OnPropertyChanged(nameof(LearnButtonText));
        }

        public GlobalMacroData Data { get; }

        // ─────────────────────────────────────────────
        //  Switch mode
        // ─────────────────────────────────────────────

        private SwitchProfileMode _switchMode;
        public SwitchProfileMode SwitchMode
        {
            get => _switchMode;
            set
            {
                if (SetProperty(ref _switchMode, value))
                {
                    Data.SwitchMode = value;
                    OnPropertyChanged(nameof(IsSpecificMode));
                    _saveCallback?.Invoke(this);
                }
            }
        }

        public bool IsSpecificMode => _switchMode == SwitchProfileMode.Specific;

        private readonly ObservableCollection<SwitchProfileModeItem> _switchModes = new();
        public ObservableCollection<SwitchProfileModeItem> SwitchModes => _switchModes;

        private void InitSwitchModes()
        {
            _switchModes.Add(new SwitchProfileModeItem(SwitchProfileMode.Next, Strings.Instance.Profiles_ShortcutMode_Next));
            _switchModes.Add(new SwitchProfileModeItem(SwitchProfileMode.Previous, Strings.Instance.Profiles_ShortcutMode_Previous));
            _switchModes.Add(new SwitchProfileModeItem(SwitchProfileMode.Specific, Strings.Instance.Profiles_ShortcutMode_Specific));
            _switchModes.Add(new SwitchProfileModeItem(SwitchProfileMode.ToggleWindow, Strings.Instance.Profiles_ShortcutMode_ToggleWindow));
        }

        // ─────────────────────────────────────────────
        //  Target profile (Specific mode)
        // ─────────────────────────────────────────────

        /// <summary>Stable-ID selection target for the Profile dropdown.
        /// Empty string represents the localized "Default" sentinel; any
        /// other value is a profile's <see cref="ProfileEntry.Id"/>.</summary>
        public string TargetProfileId
        {
            get => Data.TargetProfileId ?? "";
            set
            {
                string id = string.IsNullOrEmpty(value) ? null : value;
                if (Data.TargetProfileId == id) return;
                Data.TargetProfileId = id;
                OnPropertyChanged();
                _saveCallback?.Invoke(this);
            }
        }

        private readonly ObservableCollection<ProfileChoice> _profileChoices = new();
        public ObservableCollection<ProfileChoice> ProfileChoices => _profileChoices;

        /// <summary>Rebuilds <see cref="ProfileChoices"/> from
        /// <see cref="SettingsManager.Profiles"/>. The page's DropDownOpened
        /// handler calls this so newly-saved / deleted profiles surface
        /// without a shortcut row teardown.</summary>
        public void RebuildProfileChoices()
        {
            string currentId = Data.TargetProfileId;
            _profileChoices.Clear();
            _profileChoices.Add(new ProfileChoice("", Strings.Instance.Common_Default));
            var profiles = SettingsManager.Profiles;
            if (profiles != null)
            {
                foreach (var p in profiles)
                    _profileChoices.Add(new ProfileChoice(p.Id, p.Name));
            }
            // Re-notify so the ComboBox re-matches against the rebuilt list
            // (it would otherwise hold the cleared null from Clear()).
            OnPropertyChanged(nameof(TargetProfileId));
            Data.TargetProfileId = currentId; // restore in case binding write-back overwrote it
        }

        // ─────────────────────────────────────────────
        //  Trigger device
        // ─────────────────────────────────────────────

        /// <summary>Stable-ID selection target for the Device dropdown.
        /// <see cref="Guid.Empty"/> represents the localized "Any device"
        /// sentinel.</summary>
        public Guid TriggerDeviceGuid
        {
            get => Data.TriggerDeviceGuid;
            set
            {
                if (Data.TriggerDeviceGuid == value) return;
                Data.TriggerDeviceGuid = value;
                OnPropertyChanged();
                _saveCallback?.Invoke(this);
            }
        }

        private readonly ObservableCollection<DeviceChoice> _deviceChoices = new();
        public ObservableCollection<DeviceChoice> DeviceChoices => _deviceChoices;

        /// <summary>Rebuilds <see cref="DeviceChoices"/> from
        /// <see cref="SettingsManager.UserDevices"/>. Called from the page's
        /// DropDownOpened handler so newly-connected / disconnected devices
        /// surface without a shortcut row teardown.</summary>
        public void RebuildDeviceChoices()
        {
            Guid currentGuid = Data.TriggerDeviceGuid;
            _deviceChoices.Clear();
            _deviceChoices.Add(new DeviceChoice(Guid.Empty, Strings.Instance.Profiles_ShortcutDevice_Any));
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in devices)
                    {
                        if (ud.IsOnline && !string.IsNullOrEmpty(ud.ResolvedName))
                            _deviceChoices.Add(new DeviceChoice(ud.InstanceGuid, ud.ResolvedName));
                    }
                }
            }
            OnPropertyChanged(nameof(TriggerDeviceGuid));
            Data.TriggerDeviceGuid = currentGuid;
        }

        // ─────────────────────────────────────────────
        //  Button combo
        // ─────────────────────────────────────────────

        public string ButtonComboDisplay
        {
            get
            {
                var entries = Data.TriggerEntries;
                if (_isRecording && (entries == null || entries.Length == 0))
                    return $"{Strings.Instance.Profiles_ShortcutLearning} ({_recordingCountdown}s)";
                if (entries == null || entries.Length == 0)
                    return Strings.Instance.Common_None;

                string combo = string.Join(" + ", entries.Select(e =>
                {
                    string name = e.IsAxis
                        ? ResolveAxisName(e.AxisIndex, e.DeviceInstanceGuid, e.AxisDirection)
                        : ResolveButtonName(e.ButtonIndex, e.DeviceInstanceGuid);
                    string deviceName = ResolveDeviceName(e.DeviceInstanceGuid);
                    return deviceName != null ? $"{name} ({deviceName})" : name;
                }));
                return _isRecording ? $"{combo} ({_recordingCountdown}s)" : combo;
            }
        }

        /// <summary>
        /// Resolves a raw button index to a friendly name. Uses gamepad standard
        /// names (A, B, X, Y, etc.) for indices 0-10 on gamepad-type devices.
        /// </summary>
        private static string ResolveButtonName(int index, Guid deviceGuid)
        {
            // Standard gamepad button names (SDL gamepad API order, indices 0-10).
            bool isGamepad = false;
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    var ud = devices.FirstOrDefault(d => d.InstanceGuid == deviceGuid);
                    if (ud != null)
                        isGamepad = ud.CapType == Engine.InputDeviceType.Gamepad;
                }
            }

            if (isGamepad && index >= 0 && index <= 10)
            {
                return index switch
                {
                    0 => "A",
                    1 => "B",
                    2 => "X",
                    3 => "Y",
                    4 => Strings.Instance.Btn_LeftShoulder,
                    5 => Strings.Instance.Btn_RightShoulder,
                    6 => Strings.Instance.Btn_Back,
                    7 => Strings.Instance.Btn_Start,
                    8 => Strings.Instance.Btn_LeftStickButton,
                    9 => Strings.Instance.Btn_RightStickButton,
                    10 => Strings.Instance.Btn_Guide,
                    _ => $"B{index}"
                };
            }

            // Keyboard: try to resolve virtual key name.
            if (devices != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    var ud = devices.FirstOrDefault(d => d.InstanceGuid == deviceGuid);
                    if (ud != null && ud.CapType == Engine.InputDeviceType.Keyboard)
                    {
                        if (Enum.IsDefined(typeof(VirtualKey), index))
                            return ((VirtualKey)index).ToString();
                    }
                }
            }

            return string.Format(Strings.Instance.Macro_Btn_Format, index + 1);
        }

        private static string ResolveAxisName(int index, Guid deviceGuid, AxisTriggerDirection direction)
        {
            bool isGamepad = false;
            var devices = SettingsManager.UserDevices?.Items;
            if (devices != null)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    var ud = devices.FirstOrDefault(d => d.InstanceGuid == deviceGuid);
                    if (ud != null)
                        isGamepad = ud.CapType == Engine.InputDeviceType.Gamepad;
                }
            }

            string dirSuffix = direction == AxisTriggerDirection.Positive ? "+" : "–"; // + or –

            if (isGamepad && index >= 0 && index <= 5)
            {
                string name = index switch
                {
                    0 => "LX",
                    1 => "LY",
                    2 => "LT",
                    3 => "RX",
                    4 => "RY",
                    5 => "RT",
                    _ => $"Axis {index}"
                };
                return $"{name}{dirSuffix}";
            }

            return $"Axis {index}{dirSuffix}";
        }

        private static string ResolveDeviceName(Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return null;
            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                var ud = devices.FirstOrDefault(d => d.InstanceGuid == deviceGuid);
                return ud?.ResolvedName;
            }
        }

        // ─────────────────────────────────────────────
        //  Learn mode
        // ─────────────────────────────────────────────

        private bool _isRecording;
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(LearnButtonText));
                    OnPropertyChanged(nameof(LearnButtonIcon));
                }
            }
        }

        public string LearnButtonText => _isRecording
            ? Strings.Instance.Profiles_ShortcutLearning
            : Strings.Instance.Profiles_ShortcutLearn;

        public string LearnButtonIcon => _isRecording ? "" : ""; // Stop : Record

        /// <summary>
        /// Called when Learn mode captures buttons. Sets TriggerEntries from
        /// the recorded per-button device associations.
        /// </summary>
        public void SetLearnedButtons(TriggerButtonEntry[] entries)
        {
            Data.TriggerEntries = entries;
            IsRecording = false;
            OnPropertyChanged(nameof(ButtonComboDisplay));
            OnPropertyChanged(nameof(LearnButtonText));
            _saveCallback?.Invoke(this);
        }

        public void CancelRecording()
        {
            IsRecording = false;
            OnPropertyChanged(nameof(ButtonComboDisplay));
        }

        /// <summary>Notifies the UI that the combo display changed (during live recording).</summary>
        public void NotifyComboChanged() => OnPropertyChanged(nameof(ButtonComboDisplay));

        private int _recordingCountdown;
        /// <summary>Seconds remaining in the recording countdown.</summary>
        public int RecordingCountdown
        {
            get => _recordingCountdown;
            set
            {
                if (SetProperty(ref _recordingCountdown, value))
                    OnPropertyChanged(nameof(ButtonComboDisplay));
            }
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        public RelayCommand DeleteCommand { get; }
        public RelayCommand ClearCommand { get; }
    }

    /// <summary>Mutable mode-list item — the Mode value is stable, the
    /// DisplayName updates in place on culture change so the persistent
    /// ItemsSource collection never has to be rebuilt.</summary>
    public class SwitchProfileModeItem : ObservableObject
    {
        public SwitchProfileMode Mode { get; }
        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }
        public SwitchProfileModeItem(SwitchProfileMode mode, string displayName)
        {
            Mode = mode;
            _displayName = displayName;
        }
        public override string ToString() => _displayName;
    }

    /// <summary>Mutable profile-list item — ProfileId is stable (empty
    /// string for the "Default" sentinel), DisplayName updates in place
    /// on culture change.</summary>
    public class ProfileChoice : ObservableObject
    {
        public string ProfileId { get; }
        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }
        public ProfileChoice(string profileId, string displayName)
        {
            ProfileId = profileId ?? "";
            _displayName = displayName;
        }
        public override string ToString() => _displayName;
    }

    /// <summary>Mutable device-list item — DeviceGuid is stable
    /// (<see cref="Guid.Empty"/> for the "Any device" sentinel),
    /// DisplayName updates in place on culture change.</summary>
    public class DeviceChoice : ObservableObject
    {
        public Guid DeviceGuid { get; }
        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }
        public DeviceChoice(Guid deviceGuid, string displayName)
        {
            DeviceGuid = deviceGuid;
            _displayName = displayName;
        }
        public override string ToString() => _displayName;
    }
}
