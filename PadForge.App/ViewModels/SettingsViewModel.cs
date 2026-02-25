using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Settings page. Manages application-level settings
    /// including theme selection, ViGEmBus driver management, auto-start
    /// options, and settings file paths.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        public SettingsViewModel()
        {
            Title = "Settings";
        }

        // ─────────────────────────────────────────────
        //  Theme
        // ─────────────────────────────────────────────

        private int _selectedThemeIndex;

        /// <summary>
        /// Selected theme index: 0 = System, 1 = Light, 2 = Dark.
        /// </summary>
        public int SelectedThemeIndex
        {
            get => _selectedThemeIndex;
            set
            {
                if (SetProperty(ref _selectedThemeIndex, value))
                    ThemeChanged?.Invoke(this, value);
            }
        }

        /// <summary>Raised when the theme selection changes. Arg = theme index.</summary>
        public event EventHandler<int> ThemeChanged;

        // ─────────────────────────────────────────────
        //  ViGEmBus driver
        // ─────────────────────────────────────────────

        private bool _isViGEmInstalled;

        /// <summary>Whether the ViGEmBus driver is installed.</summary>
        public bool IsViGEmInstalled
        {
            get => _isViGEmInstalled;
            set
            {
                if (SetProperty(ref _isViGEmInstalled, value))
                {
                    OnPropertyChanged(nameof(ViGEmStatusText));
                    _installViGEmCommand?.NotifyCanExecuteChanged();
                    _uninstallViGEmCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>ViGEmBus status display text.</summary>
        public string ViGEmStatusText => _isViGEmInstalled ? "Installed" : "Not Installed";

        private string _vigemVersion = string.Empty;

        /// <summary>ViGEmBus driver version string.</summary>
        public string ViGEmVersion
        {
            get => _vigemVersion;
            set => SetProperty(ref _vigemVersion, value);
        }

        private RelayCommand _installViGEmCommand;

        /// <summary>Command to install the ViGEmBus driver.</summary>
        public RelayCommand InstallViGEmCommand =>
            _installViGEmCommand ??= new RelayCommand(
                () => InstallViGEmRequested?.Invoke(this, EventArgs.Empty),
                () => !_isViGEmInstalled);

        private RelayCommand _uninstallViGEmCommand;

        /// <summary>Command to uninstall the ViGEmBus driver.</summary>
        public RelayCommand UninstallViGEmCommand =>
            _uninstallViGEmCommand ??= new RelayCommand(
                () => UninstallViGEmRequested?.Invoke(this, EventArgs.Empty),
                () => _isViGEmInstalled);

        /// <summary>Raised when the user requests ViGEmBus installation.</summary>
        public event EventHandler InstallViGEmRequested;

        /// <summary>Raised when the user requests ViGEmBus uninstallation.</summary>
        public event EventHandler UninstallViGEmRequested;

        // ─────────────────────────────────────────────
        //  HidHide driver
        // ─────────────────────────────────────────────

        private bool _isHidHideInstalled;

        /// <summary>Whether the HidHide driver is installed.</summary>
        public bool IsHidHideInstalled
        {
            get => _isHidHideInstalled;
            set
            {
                if (SetProperty(ref _isHidHideInstalled, value))
                {
                    OnPropertyChanged(nameof(HidHideStatusText));
                    _installHidHideCommand?.NotifyCanExecuteChanged();
                    _uninstallHidHideCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>HidHide status display text.</summary>
        public string HidHideStatusText => _isHidHideInstalled ? "Installed" : "Not Installed";

        private string _hidHideVersion = string.Empty;

        /// <summary>HidHide driver version string.</summary>
        public string HidHideVersion
        {
            get => _hidHideVersion;
            set => SetProperty(ref _hidHideVersion, value);
        }

        private RelayCommand _installHidHideCommand;

        /// <summary>Command to install the HidHide driver.</summary>
        public RelayCommand InstallHidHideCommand =>
            _installHidHideCommand ??= new RelayCommand(
                () => InstallHidHideRequested?.Invoke(this, EventArgs.Empty),
                () => !_isHidHideInstalled);

        private RelayCommand _uninstallHidHideCommand;

        /// <summary>Command to uninstall the HidHide driver.</summary>
        public RelayCommand UninstallHidHideCommand =>
            _uninstallHidHideCommand ??= new RelayCommand(
                () => UninstallHidHideRequested?.Invoke(this, EventArgs.Empty),
                () => _isHidHideInstalled);

        /// <summary>Raised when the user requests HidHide installation.</summary>
        public event EventHandler InstallHidHideRequested;

        /// <summary>Raised when the user requests HidHide uninstallation.</summary>
        public event EventHandler UninstallHidHideRequested;

        // ─────────────────────────────────────────────
        //  Engine settings
        // ─────────────────────────────────────────────

        private bool _autoStartEngine = true;

        /// <summary>Whether to automatically start the input engine on application launch.</summary>
        public bool AutoStartEngine
        {
            get => _autoStartEngine;
            set => SetProperty(ref _autoStartEngine, value);
        }

        private bool _minimizeToTray;

        /// <summary>Whether to minimize to system tray instead of taskbar.</summary>
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetProperty(ref _minimizeToTray, value);
        }

        private bool _startMinimized;

        /// <summary>Whether to start the application minimized.</summary>
        public bool StartMinimized
        {
            get => _startMinimized;
            set => SetProperty(ref _startMinimized, value);
        }

        private bool _startAtLogin;

        /// <summary>Whether to automatically start PadForge when the user logs in.</summary>
        public bool StartAtLogin
        {
            get => _startAtLogin;
            set => SetProperty(ref _startAtLogin, value);
        }

        private bool _enablePollingOnFocusLoss = true;

        /// <summary>Whether to continue polling when the application loses focus.</summary>
        public bool EnablePollingOnFocusLoss
        {
            get => _enablePollingOnFocusLoss;
            set => SetProperty(ref _enablePollingOnFocusLoss, value);
        }

        private int _pollingRateMs = 1;

        /// <summary>
        /// Target polling interval in milliseconds. Lower = faster but more CPU.
        /// Valid range: 1–16.
        /// </summary>
        public int PollingRateMs
        {
            get => _pollingRateMs;
            set => SetProperty(ref _pollingRateMs, Math.Clamp(value, 1, 16));
        }

        // ─────────────────────────────────────────────
        //  Settings file
        // ─────────────────────────────────────────────

        private string _settingsFilePath = string.Empty;

        /// <summary>Full path to the currently loaded settings file.</summary>
        public string SettingsFilePath
        {
            get => _settingsFilePath;
            set => SetProperty(ref _settingsFilePath, value ?? string.Empty);
        }

        private bool _hasUnsavedChanges;

        /// <summary>Whether there are unsaved changes to settings.</summary>
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set => SetProperty(ref _hasUnsavedChanges, value);
        }

        private RelayCommand _saveCommand;

        /// <summary>Command to save settings to disk.</summary>
        public RelayCommand SaveCommand =>
            _saveCommand ??= new RelayCommand(
                () => SaveRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _reloadCommand;

        /// <summary>Command to reload settings from disk, discarding changes.</summary>
        public RelayCommand ReloadCommand =>
            _reloadCommand ??= new RelayCommand(
                () => ReloadRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _resetCommand;

        /// <summary>Command to reset all settings to defaults.</summary>
        public RelayCommand ResetCommand =>
            _resetCommand ??= new RelayCommand(
                () => ResetRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _openSettingsFolderCommand;

        /// <summary>Command to open the settings file folder in Explorer.</summary>
        public RelayCommand OpenSettingsFolderCommand =>
            _openSettingsFolderCommand ??= new RelayCommand(
                () => OpenSettingsFolderRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Raised when the user requests saving.</summary>
        public event EventHandler SaveRequested;

        /// <summary>Raised when the user requests reloading from disk.</summary>
        public event EventHandler ReloadRequested;

        /// <summary>Raised when the user requests a settings reset.</summary>
        public event EventHandler ResetRequested;

        /// <summary>Raised when the user wants to open the settings folder.</summary>
        public event EventHandler OpenSettingsFolderRequested;

        private string _sdlVersion = string.Empty;

        /// <summary>SDL3 library version string.</summary>
        public string SdlVersion
        {
            get => _sdlVersion;
            set => SetProperty(ref _sdlVersion, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Diagnostic info
        // ─────────────────────────────────────────────

        private string _applicationVersion = string.Empty;

        /// <summary>Application version string.</summary>
        public string ApplicationVersion
        {
            get => _applicationVersion;
            set => SetProperty(ref _applicationVersion, value ?? string.Empty);
        }

        private string _runtimeVersion = string.Empty;

        /// <summary>.NET runtime version string.</summary>
        public string RuntimeVersion
        {
            get => _runtimeVersion;
            set => SetProperty(ref _runtimeVersion, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Profiles
        // ─────────────────────────────────────────────

        private bool _enableAutoProfileSwitching;

        /// <summary>Whether auto-profile switching is enabled.</summary>
        public bool EnableAutoProfileSwitching
        {
            get => _enableAutoProfileSwitching;
            set => SetProperty(ref _enableAutoProfileSwitching, value);
        }

        /// <summary>Observable list of profile names for the UI.</summary>
        public ObservableCollection<ProfileListItem> ProfileItems { get; } = new();

        private ProfileListItem _selectedProfile;

        /// <summary>Currently selected profile in the list.</summary>
        public ProfileListItem SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value))
                {
                    _deleteProfileCommand?.NotifyCanExecuteChanged();
                    _editProfileCommand?.NotifyCanExecuteChanged();
                    _loadProfileCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        private string _activeProfileInfo = "Default";

        /// <summary>Display text for the currently active profile.</summary>
        public string ActiveProfileInfo
        {
            get => _activeProfileInfo;
            set
            {
                if (SetProperty(ref _activeProfileInfo, value ?? "Default"))
                    _revertToDefaultCommand?.NotifyCanExecuteChanged();
            }
        }

        /// <summary>Raised when the user requests reverting to the default profile.</summary>
        public event EventHandler RevertToDefaultRequested;

        /// <summary>Raised when the user requests saving current settings as a new profile.</summary>
        public event EventHandler SaveAsProfileRequested;

        /// <summary>Raised when the user requests deleting the selected profile.</summary>
        public event EventHandler DeleteProfileRequested;

        /// <summary>Raised when the user requests editing the selected profile's metadata.</summary>
        public event EventHandler EditProfileRequested;

        /// <summary>Raised when the user requests loading the selected profile into the editor.</summary>
        public event EventHandler LoadProfileRequested;

        private RelayCommand _saveAsProfileCommand;

        /// <summary>Command to save current settings as a new profile.</summary>
        public RelayCommand SaveAsProfileCommand =>
            _saveAsProfileCommand ??= new RelayCommand(
                () => SaveAsProfileRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _deleteProfileCommand;

        /// <summary>Command to delete the selected profile.</summary>
        public RelayCommand DeleteProfileCommand =>
            _deleteProfileCommand ??= new RelayCommand(
                () => DeleteProfileRequested?.Invoke(this, EventArgs.Empty),
                () => _selectedProfile != null);

        private RelayCommand _editProfileCommand;

        /// <summary>Command to edit the selected profile's name and executables.</summary>
        public RelayCommand EditProfileCommand =>
            _editProfileCommand ??= new RelayCommand(
                () => EditProfileRequested?.Invoke(this, EventArgs.Empty),
                () => _selectedProfile != null);

        private RelayCommand _loadProfileCommand;

        /// <summary>Command to load the selected profile's settings into the editor.</summary>
        public RelayCommand LoadProfileCommand =>
            _loadProfileCommand ??= new RelayCommand(
                () => LoadProfileRequested?.Invoke(this, EventArgs.Empty),
                () => _selectedProfile != null);

        private RelayCommand _revertToDefaultCommand;

        /// <summary>Command to revert to the default profile.</summary>
        public RelayCommand RevertToDefaultCommand =>
            _revertToDefaultCommand ??= new RelayCommand(
                () => RevertToDefaultRequested?.Invoke(this, EventArgs.Empty),
                () => _activeProfileInfo != "Default");

        /// <summary>Refreshes the can-execute state of profile commands.</summary>
        public void RefreshProfileCommands()
        {
            _deleteProfileCommand?.NotifyCanExecuteChanged();
            _editProfileCommand?.NotifyCanExecuteChanged();
            _loadProfileCommand?.NotifyCanExecuteChanged();
            _revertToDefaultCommand?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Display item for a profile in the Settings page list.
    /// </summary>
    public class ProfileListItem : ObservableObject
    {
        private string _id;
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        private string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string _executables;
        public string Executables
        {
            get => _executables;
            set => SetProperty(ref _executables, value);
        }
    }
}
