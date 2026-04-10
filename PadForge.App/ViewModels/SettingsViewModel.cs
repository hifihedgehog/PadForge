using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

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
            Title = Strings.Instance.Settings_Title;
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
        //  Language
        // ─────────────────────────────────────────────

        public ObservableCollection<CultureInfo> AvailableLanguages { get; } = new()
        {
            new CultureInfo("en"),
            new CultureInfo("de"),
            new CultureInfo("fr"),
            new CultureInfo("ja"),
            new CultureInfo("ko"),
            new CultureInfo("zh-Hans"),
            new CultureInfo("pt-BR"),
            new CultureInfo("es"),
            new CultureInfo("it"),
            new CultureInfo("nl"),
        };

        private CultureInfo _selectedLanguage;

        /// <summary>
        /// Currently selected UI language. Persisted as the culture name (e.g. "en", "ja").
        /// Changing this applies the new language immediately (live switching).
        /// </summary>
        public CultureInfo SelectedLanguage
        {
            get
            {
                if (_selectedLanguage != null) return _selectedLanguage;
                var current = CultureInfo.CurrentUICulture.Name;
                var match = AvailableLanguages.FirstOrDefault(c => c.Name == current)
                         ?? AvailableLanguages[0];
                return match;
            }
            set
            {
                if (SetProperty(ref _selectedLanguage, value) && value != null)
                    Strings.ChangeCulture(value);
            }
        }

        protected override void OnCultureChanged()
        {
            Title = Strings.Instance.Settings_Title;
            OnPropertyChanged(nameof(ViGEmStatusText));
            OnPropertyChanged(nameof(HidHideStatusText));
            OnPropertyChanged(nameof(VJoyStatusText));
            OnPropertyChanged(nameof(MidiServicesStatusText));

            // Refresh the default profile's display name in the list.
            var defaultItem = ProfileItems.FirstOrDefault(p => p.IsDefault);
            if (defaultItem != null)
                defaultItem.Name = Strings.Instance.Profile_Default;

            // Refresh the active profile header when the default profile is active.
            if (string.IsNullOrEmpty(SettingsManager.ActiveProfileId))
                _activeProfileInfo = Strings.Instance.Common_Default;
            OnPropertyChanged(nameof(ActiveProfileInfo));
        }

        /// <summary>Gets the persisted language code (for serialization).</summary>
        internal string LanguageCode => _selectedLanguage?.Name ?? "";

        /// <summary>Sets the language from a persisted code, applying the culture on startup.</summary>
        internal void SetLanguageFromCode(string code)
        {
            if (!string.IsNullOrEmpty(code))
            {
                var match = AvailableLanguages.FirstOrDefault(c => c.Name == code);
                if (match != null)
                {
                    _selectedLanguage = match;
                    // Apply the culture so the UI thread and resource lookups use
                    // the saved language immediately (without raising CultureChanged
                    // since the UI hasn't been built yet at load time).
                    Thread.CurrentThread.CurrentUICulture = match;
                    CultureInfo.DefaultThreadCurrentUICulture = match;
                    OnPropertyChanged(nameof(SelectedLanguage));
                }
            }
        }

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
        public string ViGEmStatusText => _isViGEmInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

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
                () => _isViGEmInstalled && !HasAnyViGEmSlots());

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
                    _addWhitelistPathCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>HidHide status display text.</summary>
        public string HidHideStatusText => _isHidHideInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

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
                () => _isHidHideInstalled && !HasAnyHidHideDevices());

        /// <summary>Raised when the user requests HidHide installation.</summary>
        public event EventHandler InstallHidHideRequested;

        /// <summary>Raised when the user requests HidHide uninstallation.</summary>
        public event EventHandler UninstallHidHideRequested;

        /// <summary>Application paths whitelisted in HidHide (user-visible paths, not DOS device paths).</summary>
        public ObservableCollection<string> HidHideWhitelistPaths { get; } = new();

        private string _selectedWhitelistPath;

        /// <summary>Currently selected whitelist path in the list.</summary>
        public string SelectedWhitelistPath
        {
            get => _selectedWhitelistPath;
            set
            {
                if (SetProperty(ref _selectedWhitelistPath, value))
                    _removeWhitelistPathCommand?.NotifyCanExecuteChanged();
            }
        }

        private RelayCommand _addWhitelistPathCommand;

        /// <summary>Command to add an application to the HidHide whitelist.</summary>
        public RelayCommand AddWhitelistPathCommand =>
            _addWhitelistPathCommand ??= new RelayCommand(
                () => AddWhitelistPathRequested?.Invoke(this, EventArgs.Empty),
                () => _isHidHideInstalled);

        private RelayCommand _removeWhitelistPathCommand;

        /// <summary>Command to remove the selected application from the HidHide whitelist.</summary>
        public RelayCommand RemoveWhitelistPathCommand =>
            _removeWhitelistPathCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedWhitelistPath != null)
                    {
                        HidHideWhitelistPaths.Remove(_selectedWhitelistPath);
                        RaiseWhitelistChanged();
                    }
                },
                () => _selectedWhitelistPath != null);

        /// <summary>Raised when the user requests adding a whitelist path (opens file dialog).</summary>
        public event EventHandler AddWhitelistPathRequested;

        /// <summary>Raised when the whitelist changes (add or remove).</summary>
        public event EventHandler WhitelistChanged;

        /// <summary>Raises the WhitelistChanged event.</summary>
        internal void RaiseWhitelistChanged() => WhitelistChanged?.Invoke(this, EventArgs.Empty);

        // ─────────────────────────────────────────────
        //  vJoy driver
        // ─────────────────────────────────────────────

        private bool _isVJoyInstalled;

        /// <summary>Whether the vJoy driver is installed and the DLL is accessible.</summary>
        public bool IsVJoyInstalled
        {
            get => _isVJoyInstalled;
            set
            {
                if (SetProperty(ref _isVJoyInstalled, value))
                {
                    OnPropertyChanged(nameof(VJoyStatusText));
                    _installVJoyCommand?.NotifyCanExecuteChanged();
                    _uninstallVJoyCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>vJoy status display text.</summary>
        public string VJoyStatusText => _isVJoyInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        private string _vjoyVersion = string.Empty;

        /// <summary>vJoy driver version string.</summary>
        public string VJoyVersion
        {
            get => _vjoyVersion;
            set => SetProperty(ref _vjoyVersion, value);
        }

        private RelayCommand _installVJoyCommand;

        /// <summary>Command to install the vJoy driver.</summary>
        public RelayCommand InstallVJoyCommand =>
            _installVJoyCommand ??= new RelayCommand(
                () => InstallVJoyRequested?.Invoke(this, EventArgs.Empty),
                () => !_isVJoyInstalled);

        private RelayCommand _uninstallVJoyCommand;

        /// <summary>Command to uninstall the vJoy driver.</summary>
        public RelayCommand UninstallVJoyCommand =>
            _uninstallVJoyCommand ??= new RelayCommand(
                () => UninstallVJoyRequested?.Invoke(this, EventArgs.Empty),
                () => _isVJoyInstalled && !HasAnyVJoySlots());

        /// <summary>Raised when the user requests vJoy installation.</summary>
        public event EventHandler InstallVJoyRequested;

        /// <summary>Raised when the user requests vJoy uninstallation.</summary>
        public event EventHandler UninstallVJoyRequested;

        // ─────────────────────────────────────────────
        //  Windows MIDI Services
        // ─────────────────────────────────────────────

        private bool _isMidiServicesInstalled;

        /// <summary>Whether Windows MIDI Services is available.</summary>
        public bool IsMidiServicesInstalled
        {
            get => _isMidiServicesInstalled;
            set
            {
                if (SetProperty(ref _isMidiServicesInstalled, value))
                {
                    OnPropertyChanged(nameof(MidiServicesStatusText));
                    _installMidiServicesCommand?.NotifyCanExecuteChanged();
                    _uninstallMidiServicesCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>MIDI Services status display text.</summary>
        public string MidiServicesStatusText => _isMidiServicesInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        private string _midiServicesVersion = string.Empty;

        /// <summary>MIDI Services version string.</summary>
        public string MidiServicesVersion
        {
            get => _midiServicesVersion;
            set => SetProperty(ref _midiServicesVersion, value);
        }

        private RelayCommand _installMidiServicesCommand;

        /// <summary>True if the OS meets the minimum version for Windows MIDI Services (Win11 24H2, build 26100).</summary>
        public static bool IsMidiOsSupported => Environment.OSVersion.Version.Build >= 26100;

        /// <summary>Command to download and install Windows MIDI Services.</summary>
        public RelayCommand InstallMidiServicesCommand =>
            _installMidiServicesCommand ??= new RelayCommand(
                () => InstallMidiServicesRequested?.Invoke(this, EventArgs.Empty),
                () => !_isMidiServicesInstalled && IsMidiOsSupported);

        private RelayCommand _uninstallMidiServicesCommand;

        /// <summary>Command to uninstall Windows MIDI Services.</summary>
        public RelayCommand UninstallMidiServicesCommand =>
            _uninstallMidiServicesCommand ??= new RelayCommand(
                () => UninstallMidiServicesRequested?.Invoke(this, EventArgs.Empty),
                () => _isMidiServicesInstalled && !HasAnyMidiSlots());

        /// <summary>Raised when the user requests MIDI Services installation.</summary>
        public event EventHandler InstallMidiServicesRequested;

        /// <summary>Raised when the user requests MIDI Services uninstallation.</summary>
        public event EventHandler UninstallMidiServicesRequested;

        // ─────────────────────────────────────────────
        //  Driver uninstall guards
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set by MainWindow to provide slot-type queries for uninstall guards.
        /// Returns true if any created slot uses ViGEm (Xbox 360 or DS4).
        /// </summary>
        internal Func<bool> HasAnyViGEmSlots { get; set; } = () => false;

        /// <summary>
        /// Set by MainWindow to provide slot-type queries for uninstall guards.
        /// Returns true if any created slot uses vJoy.
        /// </summary>
        internal Func<bool> HasAnyVJoySlots { get; set; } = () => false;

        /// <summary>
        /// Set by MainWindow to provide slot-type queries for uninstall guards.
        /// Returns true if any created slot uses MIDI.
        /// </summary>
        internal Func<bool> HasAnyMidiSlots { get; set; } = () => false;

        /// <summary>
        /// Set by MainWindow to provide device-state queries for uninstall guards.
        /// Returns true if any device has HidHide enabled.
        /// </summary>
        internal Func<bool> HasAnyHidHideDevices { get; set; } = () => false;

        /// <summary>
        /// Re-evaluates uninstall button CanExecute state.
        /// Call after slot creation/deletion/type changes.
        /// </summary>
        public void RefreshDriverGuards()
        {
            _uninstallViGEmCommand?.NotifyCanExecuteChanged();
            _uninstallVJoyCommand?.NotifyCanExecuteChanged();
            _uninstallHidHideCommand?.NotifyCanExecuteChanged();
            _uninstallMidiServicesCommand?.NotifyCanExecuteChanged();
        }

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

        private bool _enableInputHiding = true;

        /// <summary>
        /// Global master switch for device hiding (HidHide + input hooks).
        /// When false, no HidHide blacklisting or hook suppression occurs.
        /// </summary>
        public bool EnableInputHiding
        {
            get => _enableInputHiding;
            set => SetProperty(ref _enableInputHiding, value);
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

        private bool _use2DControllerView;

        /// <summary>Whether to show the 2D controller view instead of 3D.</summary>
        public bool Use2DControllerView
        {
            get => _use2DControllerView;
            set => SetProperty(ref _use2DControllerView, value);
        }

        // ─────────────────────────────────────────────
        //  Main window position/size (profile-independent)
        // ─────────────────────────────────────────────

        public double MainWindowLeft { get; set; } = -1;
        public double MainWindowTop { get; set; } = -1;
        public double MainWindowWidth { get; set; } = 1100;
        public double MainWindowHeight { get; set; } = 720;
        public int MainWindowState { get; set; }
        public bool MainWindowFullScreen { get; set; }

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

        private string _activeProfileInfo = Strings.Instance.Common_Default;

        /// <summary>Display text for the currently active profile.</summary>
        public string ActiveProfileInfo
        {
            get => _activeProfileInfo;
            set
            {
                SetProperty(ref _activeProfileInfo, value ?? Strings.Instance.Common_Default);
            }
        }

        /// <summary>Raised when the user requests reverting to the default profile.</summary>
        public event EventHandler RevertToDefaultRequested;

        /// <summary>Raised when the user requests creating a new empty profile.</summary>
        public event EventHandler NewProfileRequested;

        /// <summary>Raised when the user requests saving current settings as a new profile.</summary>
        public event EventHandler SaveAsProfileRequested;

        /// <summary>Raised when the user requests deleting the selected profile.</summary>
        public event EventHandler DeleteProfileRequested;

        /// <summary>Raised when the user requests editing the selected profile's metadata.</summary>
        public event EventHandler EditProfileRequested;

        /// <summary>Raised when the user requests loading the selected profile into the editor.</summary>
        public event EventHandler LoadProfileRequested;

        private RelayCommand _newProfileCommand;

        /// <summary>Command to create a new empty profile.</summary>
        public RelayCommand NewProfileCommand =>
            _newProfileCommand ??= new RelayCommand(
                () => NewProfileRequested?.Invoke(this, EventArgs.Empty));

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
                () => _selectedProfile != null && !_selectedProfile.IsDefault);

        private RelayCommand _editProfileCommand;

        /// <summary>Command to edit the selected profile's name and executables.</summary>
        public RelayCommand EditProfileCommand =>
            _editProfileCommand ??= new RelayCommand(
                () => EditProfileRequested?.Invoke(this, EventArgs.Empty),
                () => _selectedProfile != null && !_selectedProfile.IsDefault);

        private RelayCommand _loadProfileCommand;

        /// <summary>Command to load the selected profile's settings into the editor.</summary>
        public RelayCommand LoadProfileCommand =>
            _loadProfileCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedProfile?.IsDefault == true)
                        RevertToDefaultRequested?.Invoke(this, EventArgs.Empty);
                    else
                        LoadProfileRequested?.Invoke(this, EventArgs.Empty);
                },
                () => _selectedProfile != null);

        /// <summary>Refreshes the can-execute state of profile commands.</summary>
        public void RefreshProfileCommands()
        {
            _deleteProfileCommand?.NotifyCanExecuteChanged();
            _editProfileCommand?.NotifyCanExecuteChanged();
            _loadProfileCommand?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Display item for a profile in the Settings page list.
    /// </summary>
    public class ProfileListItem : ObservableObject
    {
        /// <summary>Sentinel ID for the built-in Default profile entry.</summary>
        public const string DefaultProfileId = "__default__";

        /// <summary>Whether this is the built-in Default profile entry.</summary>
        public bool IsDefault => Id == DefaultProfileId;

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

        private string _topologyLabel;
        public string TopologyLabel
        {
            get => _topologyLabel;
            set { if (SetProperty(ref _topologyLabel, value)) OnPropertyChanged(nameof(HasNoSlots)); }
        }

        public bool HasNoSlots => XboxCount == 0 && DS4Count == 0 && VJoyCount == 0 && MidiCount == 0 && KbmCount == 0;

        private int _xboxCount;
        public int XboxCount
        {
            get => _xboxCount;
            set => SetProperty(ref _xboxCount, value);
        }

        private int _ds4Count;
        public int DS4Count
        {
            get => _ds4Count;
            set => SetProperty(ref _ds4Count, value);
        }

        private int _vjoyCount;
        public int VJoyCount
        {
            get => _vjoyCount;
            set => SetProperty(ref _vjoyCount, value);
        }

        private int _midiCount;
        public int MidiCount
        {
            get => _midiCount;
            set => SetProperty(ref _midiCount, value);
        }

        private int _kbmCount;
        public int KbmCount
        {
            get => _kbmCount;
            set => SetProperty(ref _kbmCount, value);
        }
    }
}
