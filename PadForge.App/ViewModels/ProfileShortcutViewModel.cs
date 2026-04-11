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

        public static ObservableCollection<SwitchProfileModeItem> SwitchModes { get; } = new()
        {
            new(SwitchProfileMode.Next, Strings.Instance.Profiles_ShortcutMode_Next),
            new(SwitchProfileMode.Previous, Strings.Instance.Profiles_ShortcutMode_Previous),
            new(SwitchProfileMode.Specific, Strings.Instance.Profiles_ShortcutMode_Specific),
        };

        // ─────────────────────────────────────────────
        //  Target profile (Specific mode)
        // ─────────────────────────────────────────────

        public string TargetProfileName
        {
            get
            {
                if (Data.TargetProfileId == null) return Strings.Instance.Common_Default;
                var profile = SettingsManager.Profiles?.Find(p => p.Id == Data.TargetProfileId);
                return profile?.Name ?? Data.TargetProfileId;
            }
            set
            {
                if (value == Strings.Instance.Common_Default)
                    Data.TargetProfileId = null;
                else
                {
                    var profile = SettingsManager.Profiles?.Find(p => p.Name == value);
                    Data.TargetProfileId = profile?.Id;
                }
                OnPropertyChanged();
                _saveCallback?.Invoke(this);
            }
        }

        public ObservableCollection<string> ProfileNames
        {
            get
            {
                var names = new ObservableCollection<string> { Strings.Instance.Common_Default };
                var profiles = SettingsManager.Profiles;
                if (profiles != null)
                    foreach (var p in profiles)
                        names.Add(p.Name);
                return names;
            }
        }

        // ─────────────────────────────────────────────
        //  Trigger device
        // ─────────────────────────────────────────────

        public string SelectedDeviceName
        {
            get
            {
                if (Data.TriggerDeviceGuid == Guid.Empty)
                    return Strings.Instance.Profiles_ShortcutDevice_Any;
                var devices = SettingsManager.UserDevices?.Items;
                if (devices != null)
                {
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        var ud = devices.FirstOrDefault(d => d.InstanceGuid == Data.TriggerDeviceGuid);
                        if (ud != null) return ud.ResolvedName;
                    }
                }
                return Data.TriggerDeviceGuid.ToString("N").Substring(0, 8) + "...";
            }
            set
            {
                if (value == Strings.Instance.Profiles_ShortcutDevice_Any)
                    Data.TriggerDeviceGuid = Guid.Empty;
                else
                {
                    var devices = SettingsManager.UserDevices?.Items;
                    if (devices != null)
                    {
                        lock (SettingsManager.UserDevices.SyncRoot)
                        {
                            var ud = devices.FirstOrDefault(d => d.ResolvedName == value);
                            if (ud != null) Data.TriggerDeviceGuid = ud.InstanceGuid;
                        }
                    }
                }
                OnPropertyChanged();
                _saveCallback?.Invoke(this);
            }
        }

        public ObservableCollection<string> DeviceOptions
        {
            get
            {
                var options = new ObservableCollection<string> { Strings.Instance.Profiles_ShortcutDevice_Any };
                var devices = SettingsManager.UserDevices?.Items;
                if (devices != null)
                {
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        foreach (var ud in devices)
                        {
                            if (ud.IsOnline && !string.IsNullOrEmpty(ud.ResolvedName))
                                options.Add(ud.ResolvedName);
                        }
                    }
                }
                return options;
            }
        }

        // ─────────────────────────────────────────────
        //  Button combo
        // ─────────────────────────────────────────────

        public string ButtonComboDisplay
        {
            get
            {
                var entries = Data.TriggerEntries;
                if (entries == null || entries.Length == 0)
                    return Strings.Instance.Common_None;

                return string.Join(" + ", entries.Select(e =>
                {
                    string buttonName = ResolveButtonName(e.ButtonIndex, e.DeviceInstanceGuid);
                    string deviceName = ResolveDeviceName(e.DeviceInstanceGuid);
                    return deviceName != null ? $"{buttonName} ({deviceName})" : buttonName;
                }));
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

        private bool _isLearning;
        public bool IsLearning
        {
            get => _isLearning;
            set
            {
                if (SetProperty(ref _isLearning, value))
                    OnPropertyChanged(nameof(LearnButtonText));
            }
        }

        public string LearnButtonText => _isLearning
            ? Strings.Instance.Profiles_ShortcutLearning
            : Strings.Instance.Profiles_ShortcutLearn;

        /// <summary>
        /// Called when Learn mode captures buttons. Sets TriggerEntries from
        /// the recorded per-button device associations.
        /// </summary>
        public void SetLearnedButtons(TriggerButtonEntry[] entries)
        {
            Data.TriggerEntries = entries;
            IsLearning = false;
            OnPropertyChanged(nameof(ButtonComboDisplay));
            OnPropertyChanged(nameof(LearnButtonText));
            _saveCallback?.Invoke(this);
        }

        public void CancelLearn()
        {
            IsLearning = false;
            OnPropertyChanged(nameof(LearnButtonText));
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        public RelayCommand DeleteCommand { get; }
        public RelayCommand ClearCommand { get; }
    }

    public record SwitchProfileModeItem(SwitchProfileMode Mode, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
