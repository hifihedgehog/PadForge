using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    /// including theme selection, HIDMaestro / HidHide driver management,
    /// auto-start options, and settings file paths.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        public SettingsViewModel()
        {
            Title = Strings.Instance.Settings_Title;

            // Keep the per-item IsActive flame flag (#175) current when the
            // profile list is rebuilt or items are added/removed.
            ProfileItems.CollectionChanged += (_, _) => OnProfileItemsChanged();
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
            OnPropertyChanged(nameof(HidHideStatusText));
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

        /// <summary>Back to the fresh-install language state: no explicit
        /// selection persisted (LanguageCode saves ""), UI following the
        /// OS display language. <see cref="SetLanguageFromCode"/> cannot
        /// express this, since it deliberately no-ops on the empty code a
        /// fresh settings document carries. Used by Reset to Defaults.</summary>
        internal void ResetLanguageToSystemDefault()
        {
            _selectedLanguage = null;
            // The culture the process started under, before the saved
            // override: the OS display language a fresh install shows.
            Strings.ChangeCulture(App.StartupUICulture ?? CultureInfo.InstalledUICulture);
            OnPropertyChanged(nameof(SelectedLanguage));
        }

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

        private string _hidMaestroVersion = GetEmbeddedHidMaestroVersion();

        /// <summary>HIDMaestro SDK version string, read from the embedded
        /// HIDMaestro.Core assembly at startup.</summary>
        public string HIDMaestroVersion
        {
            get => _hidMaestroVersion;
            set => SetProperty(ref _hidMaestroVersion, value);
        }

        private static string GetEmbeddedHidMaestroVersion()
        {
            try
            {
                var asm = typeof(HIDMaestro.HMContext).Assembly;
                var v = asm.GetName().Version;
                return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
            }
            catch { return string.Empty; }
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

        // ── Remote Link paired peers (issue #138) ──

        /// <summary>Trusted paired PCs, shown in the Settings peer manager.</summary>
        public ObservableCollection<RemoteLinkTrustedPeer> TrustedPeers { get; } = new();

        /// <summary>PadForge PCs discovered on the LAN that aren't paired yet — shown under
        /// the paired list so all peers (paired + online + new) sit in one place.</summary>
        public ObservableCollection<RemoteLinkNearbyPeer> NearbyUnpaired { get; } = new();

        public bool HasNearbyUnpaired => NearbyUnpaired.Count > 0;

        /// <summary>Raised when the user revokes one peer (by fingerprint) or all.</summary>
        public event Action<string> PeerRevokeRequested;
        public event Action PeerRevokeAllRequested;

        /// <summary>Raised when the user renames a peer (fingerprint, new name) — persisted.</summary>
        public event Action<string, string> PeerRenameRequested;

        /// <summary>Raised when the user clicks Connect on a paired-but-offline peer (host:port).</summary>
        public event Action<string> PeerConnectRequested;

        private RelayCommand _revokeAllPeersCommand;
        public RelayCommand RevokeAllPeersCommand =>
            _revokeAllPeersCommand ??= new RelayCommand(() => PeerRevokeAllRequested?.Invoke());

        /// <summary>Rebuild the trusted-peer list from the trust store. Called on
        /// pair / revoke / rename — NOT for online refresh (that updates in place via
        /// <see cref="UpdatePeerOnlineStatus"/> so an in-progress name edit isn't disrupted).</summary>
        public void RefreshTrustedPeers(System.Collections.Generic.IEnumerable<PadForge.Engine.RemoteLink.PeerTrust> peers,
            System.Collections.Generic.IReadOnlyCollection<string> connectedFingerprints = null)
        {
            TrustedPeers.Clear();
            if (peers == null) return;
            foreach (var p in peers)
            {
                bool online = connectedFingerprints != null &&
                    connectedFingerprints.Any(f => string.Equals(f, p.FingerprintHex, StringComparison.OrdinalIgnoreCase));
                TrustedPeers.Add(new RemoteLinkTrustedPeer(p.Name, p.HostName, p.FingerprintHex, p.PairedUtc, p.GamepadOnly, online,
                    fp => PeerRevokeRequested?.Invoke(fp),
                    (fp, name) => PeerRenameRequested?.Invoke(fp, name),
                    hostPort => PeerConnectRequested?.Invoke(hostPort)));
            }
        }

        /// <summary>Update the online dots in place from the live connection set, without
        /// rebuilding the list.</summary>
        public void UpdatePeerOnlineStatus(System.Collections.Generic.IReadOnlyCollection<string> connectedFingerprints)
        {
            foreach (var peer in TrustedPeers)
                peer.IsOnline = connectedFingerprints != null &&
                    connectedFingerprints.Any(f => string.Equals(f, peer.FingerprintHex, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Update each paired peer's "reachable right now" host:port from LAN
        /// discovery (in place), so a discovered-but-offline peer shows a Connect button.</summary>
        public void UpdatePeerReachability(System.Collections.Generic.IReadOnlyDictionary<string, string> fingerprintToHostPort)
        {
            foreach (var peer in TrustedPeers)
                peer.ReachableHostPort =
                    fingerprintToHostPort != null && fingerprintToHostPort.TryGetValue(peer.FingerprintHex, out var hp) ? hp : null;
        }

        /// <summary>Replace the nearby-unpaired list (discovered PCs not in the trust store).</summary>
        public void SetNearbyUnpaired(System.Collections.Generic.IEnumerable<RemoteLinkNearbyPeer> peers)
        {
            NearbyUnpaired.Clear();
            if (peers != null) foreach (var p in peers) NearbyUnpaired.Add(p);
            OnPropertyChanged(nameof(HasNearbyUnpaired));
        }

        // ── Remote Link identity protection mode (issue #138 — thumb-drive portability) ──

        /// <summary>Dropdown options, in index order: 0 Secure, 1 password-portable,
        /// 2 open-portable. Plain language — no crypto jargon.</summary>
        public System.Collections.Generic.IReadOnlyList<string> IdentityProtectionModes => new[]
        {
            PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityModeSecure,
            PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityModePortablePassword,
            PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityModePortableOpen,
        };

        private bool _suppressIdentityModeChange;
        private int _identityProtectionModeIndex;
        /// <summary>Selected protection mode (0/1/2). User changes raise
        /// <see cref="IdentityProtectionModeChangeRequested"/>; programmatic reverts use
        /// <see cref="SetIdentityProtectionModeSilently"/> so no event re-fires.</summary>
        public int IdentityProtectionModeIndex
        {
            get => _identityProtectionModeIndex;
            set
            {
                if (SetProperty(ref _identityProtectionModeIndex, value) && !_suppressIdentityModeChange)
                {
                    OnPropertyChanged(nameof(IdentityProtectionHint));
                    IdentityProtectionModeChangeRequested?.Invoke(value);
                }
            }
        }

        /// <summary>One-line guidance under the dropdown for the selected mode.</summary>
        public string IdentityProtectionHint => _identityProtectionModeIndex switch
        {
            1 => PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityHintPortablePassword,
            2 => PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityHintPortableOpen,
            _ => PadForge.Resources.Strings.Strings.Instance.RemoteLink_IdentityHintSecure,
        };

        /// <summary>Raised when the user picks a different protection mode (the new index).</summary>
        public event Action<int> IdentityProtectionModeChangeRequested;

        /// <summary>Set the selected mode without raising the change request (used to
        /// initialize from settings and to revert a cancelled switch).</summary>
        public void SetIdentityProtectionModeSilently(int index)
        {
            _suppressIdentityModeChange = true;
            IdentityProtectionModeIndex = index;
            _suppressIdentityModeChange = false;
            OnPropertyChanged(nameof(IdentityProtectionHint));
        }

        /// <summary>Raised when the user requests adding a whitelist path (opens file dialog).</summary>
        public event EventHandler AddWhitelistPathRequested;

        /// <summary>Raised when the whitelist changes (add or remove).</summary>
        public event EventHandler WhitelistChanged;

        /// <summary>Raises the WhitelistChanged event.</summary>
        internal void RaiseWhitelistChanged() => WhitelistChanged?.Invoke(this, EventArgs.Empty);

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

        private int _hmInactivityDestroyTimeoutSeconds = 60;

        /// <summary>
        /// Seconds the engine waits for any mapped device to return online
        /// before destroying an HM virtual controller and removing its slot.
        /// 0 disables (HM VCs survive arbitrary offline windows).  Default
        /// 60.  Surviving HM VCs bubble down to keep XInput indices
        /// contiguous after a destroy.
        /// </summary>
        public int HmInactivityDestroyTimeoutSeconds
        {
            get => _hmInactivityDestroyTimeoutSeconds;
            set => SetProperty(ref _hmInactivityDestroyTimeoutSeconds, Math.Clamp(value, 0, 3600));
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

        private bool _keepHidHideCloaksBetweenLaunches = false;

        /// <summary>
        /// When on, PadForge does NOT clear its HidHide-managed cloaks at
        /// shutdown — they persist between sessions so processes that scan
        /// for controllers while PadForge is closed (e.g. Steam launching
        /// after PadForge exits) still see the physicals as cloaked. The
        /// next PadForge start skips the stale-entry sweep so the
        /// persisted cloaks survive into the new session without a visible
        /// decloak window.
        ///
        /// When off (default — previous behavior), PadForge clears its
        /// cloaks on Stop / shutdown so non-PadForge sessions don't have
        /// the controllers hidden.
        ///
        /// The runtime master toggle (EnableInputHiding) is unaffected:
        /// flipping it off mid-session always decloaks immediately
        /// regardless of this setting.
        /// </summary>
        public bool KeepHidHideCloaksBetweenLaunches
        {
            get => _keepHidHideCloaksBetweenLaunches;
            set => SetProperty(ref _keepHidHideCloaksBetweenLaunches, value);
        }

        // ─────────────────────────────────────────────
        //  Community Configs (Steam Workshop, issue #9)
        // ─────────────────────────────────────────────

        private bool _enableCommunityConfigLookup;

        /// <summary>
        /// Master opt-in for the Steam Workshop feature. Off by default: no
        /// PadForge network traffic to Steam ever happens while this is
        /// false (every PadForge.SteamWorkshop client constructor throws on
        /// a cold gate as defense in depth).
        /// </summary>
        public bool EnableCommunityConfigLookup
        {
            get => _enableCommunityConfigLookup;
            set => SetProperty(ref _enableCommunityConfigLookup, value);
        }

        private bool _showLegacyWorkshopConfigs;

        /// <summary>
        /// Sub-toggle: list 2016-era Workshop entries that have no CDN
        /// file_url. They wear a Legacy badge and route through the
        /// Steam-subscribe fallback instead of a direct download.
        /// </summary>
        public bool ShowLegacyWorkshopConfigs
        {
            get => _showLegacyWorkshopConfigs;
            set => SetProperty(ref _showLegacyWorkshopConfigs, value);
        }

        private RelayCommand _clearWorkshopCacheCommand;

        /// <summary>Command to purge %LOCALAPPDATA%\PadForge\SteamWorkshopCache.</summary>
        public RelayCommand ClearWorkshopCacheCommand =>
            _clearWorkshopCacheCommand ??= new RelayCommand(
                () => ClearWorkshopCacheRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _checkWorkshopUpdatesCommand;

        /// <summary>
        /// Command surface for "check imported profiles for updates".
        /// Phase D (#9) wires update detection over SteamWorkshopSource
        /// provenance; until then the Settings button stays collapsed.
        /// </summary>
        public RelayCommand CheckWorkshopUpdatesCommand =>
            _checkWorkshopUpdatesCommand ??= new RelayCommand(
                () => CheckWorkshopUpdatesRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand _browseCommunityConfigsCommand;

        /// <summary>Command to open the Browse Community Configs dialog. Always
        /// enabled: with the opt-in off, the dialog opens on its cold-forge
        /// state and offers the enable action itself.</summary>
        public RelayCommand BrowseCommunityConfigsCommand =>
            _browseCommunityConfigsCommand ??= new RelayCommand(
                () => BrowseCommunityConfigsRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Raised when the user asks to purge the Workshop cache.</summary>
        public event EventHandler ClearWorkshopCacheRequested;

        /// <summary>Raised when the user asks to re-check imported profiles (Phase D).</summary>
        public event EventHandler CheckWorkshopUpdatesRequested;

        /// <summary>Raised when the user opens the Browse Community Configs dialog.</summary>
        public event EventHandler BrowseCommunityConfigsRequested;

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

        private string _foregroundExeName = "-";

        /// <summary>Foreground exe filename the auto-switch monitor last saw
        /// (#175 item 8). Runtime-only, fed at 1 Hz by InputService's UI
        /// timer; never persisted (not in MainWindow's MarkDirty allowlist).</summary>
        public string ForegroundExeName
        {
            get => _foregroundExeName;
            set => SetProperty(ref _foregroundExeName, value);
        }

        private bool _isForegroundMatched;

        /// <summary>True while the foreground exe matches a profile. Runtime-only.</summary>
        public bool IsForegroundMatched
        {
            get => _isForegroundMatched;
            set => SetProperty(ref _isForegroundMatched, value);
        }

        private bool _use2DControllerView;

        /// <summary>Whether to show the 2D controller view instead of 3D.</summary>
        public bool Use2DControllerView
        {
            get => _use2DControllerView;
            set => SetProperty(ref _use2DControllerView, value);
        }

        /// <summary>
        /// True after the v3 first-run cleanup wizard has been shown to the
        /// user. Persists across launches so the dialog only appears once.
        /// </summary>
        public bool LegacyDriverCleanupOffered { get; set; }

        /// <summary>True once the first-run welcome tour has been completed
        /// or skipped (replaces the pre-v4 beside-exe marker file).</summary>
        public bool FirstRunTourCompleted { get; set; }

        // ─────────────────────────────────────────────
        //  Main window position/size (profile-independent)
        // ─────────────────────────────────────────────

        public double MainWindowLeft { get; set; } = -1;
        public double MainWindowTop { get; set; } = -1;
        public double MainWindowWidth { get; set; } = 1100;
        public double MainWindowHeight { get; set; } = 720;
        public int MainWindowState { get; set; }
        public bool MainWindowFullScreen { get; set; }

        /// <summary>Observable collection of profile shortcut rows for the UI.</summary>
        public ObservableCollection<ProfileShortcutViewModel> ProfileShortcuts { get; } = new();

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
                    _exportProfileCommand?.NotifyCanExecuteChanged();
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

                // Every active-profile change flows through this setter (after
                // SettingsManager.ActiveProfileId is updated), so recompute the
                // per-item flame flags here. Runs unconditionally: a rename can
                // re-set the same display text while the active id differs.
                UpdateActiveProfileFlags();
            }
        }

        /// <summary>
        /// Recomputes each profile row's IsActive flag against
        /// SettingsManager.ActiveProfileId. Empty/null id means the built-in
        /// Default profile is active. Also repairs a missing list selection
        /// onto the active row.
        /// </summary>
        private void UpdateActiveProfileFlags()
        {
            string activeId = SettingsManager.ActiveProfileId;
            foreach (var item in ProfileItems)
            {
                item.IsActive = string.IsNullOrEmpty(activeId)
                    ? item.IsDefault
                    : item.Id == activeId;
            }

            // Selection repair (user report 2026-07-06): the selection aura
            // binds ListBoxItem.IsSelected, and at startup nothing was
            // selected even though a profile was ACTIVE, so the engaged row
            // rendered auraless until a click. Whenever no live selection
            // exists (null, or the selected row left the list), select the
            // active row. Every population and active-switch path funnels
            // through this method (OnProfileItemsChanged, the
            // ActiveProfileInfo setter), so the load, reset, and switch
            // sites all self-heal here. A live user selection is never
            // overridden.
            if (_selectedProfile == null || !ProfileItems.Contains(_selectedProfile))
            {
                var active = ProfileItems.FirstOrDefault(i => i.IsActive);
                if (active != null)
                    SelectedProfile = active;
            }
        }

        /// <summary>Rows whose PropertyChanged is currently hooked for the
        /// auto-switch no-op hint. Tracked separately because Clear() raises
        /// Reset without the removed items.</summary>
        private readonly List<ProfileListItem> _hookedProfileItems = new();

        /// <summary>
        /// Collection-change fan-out for ProfileItems: refreshes the flame
        /// flags, rehooks per-row change tracking, and recomputes the
        /// auto-switch no-op hint (#175 item 8). Full rehook per change is
        /// fine at profile-list scale, and it is the only Reset-safe shape.
        /// </summary>
        private void OnProfileItemsChanged()
        {
            UpdateActiveProfileFlags();
            foreach (var item in _hookedProfileItems)
                item.PropertyChanged -= ProfileItem_PropertyChanged;
            _hookedProfileItems.Clear();
            foreach (var item in ProfileItems)
            {
                item.PropertyChanged += ProfileItem_PropertyChanged;
                _hookedProfileItems.Add(item);
            }
            OnPropertyChanged(nameof(NoProfileHasExecutables));
        }

        /// <summary>Edit mutates rows in place (Executables raises
        /// HasExecutables), so per-row changes feed the aggregate too.</summary>
        private void ProfileItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProfileListItem.HasExecutables))
                OnPropertyChanged(nameof(NoProfileHasExecutables));
        }

        /// <summary>True while no profile carries an executable match rule
        /// (#175 item 8): with auto-switching on the monitor can never fire,
        /// so the FOREGROUND readout shows a cold hint instead of a silent
        /// no-op. Derived; notified from OnProfileItemsChanged and
        /// ProfileItem_PropertyChanged.</summary>
        public bool NoProfileHasExecutables => ProfileItems.All(i => !i.HasExecutables);

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

        /// <summary>Raised when the user requests exporting the selected
        /// profile to a shareable .pfprofile file (issue #83 follow-up).</summary>
        public event EventHandler ExportProfileRequested;

        /// <summary>Raised when the user requests importing a .pfprofile file.</summary>
        public event EventHandler ImportProfileRequested;

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

        private RelayCommand _exportProfileCommand;

        /// <summary>Command to export the selected profile as a .pfprofile.
        /// The Default entry exports a snapshot of the current settings.</summary>
        public RelayCommand ExportProfileCommand =>
            _exportProfileCommand ??= new RelayCommand(
                () => ExportProfileRequested?.Invoke(this, EventArgs.Empty),
                () => _selectedProfile != null);

        private RelayCommand _importProfileCommand;

        /// <summary>Command to import a .pfprofile.</summary>
        public RelayCommand ImportProfileCommand =>
            _importProfileCommand ??= new RelayCommand(
                () => ImportProfileRequested?.Invoke(this, EventArgs.Empty));

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

        private bool _isActive;

        /// <summary>Whether this profile is the currently active one.
        /// Drives the lit flame in the Profiles list (#175).</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
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
            set
            {
                if (SetProperty(ref _executables, value))
                {
                    OnPropertyChanged(nameof(HasExecutables));
                    OnPropertyChanged(nameof(AutoSwitchRuleSummary));
                }
            }
        }

        private string _executablePaths;

        /// <summary>Pipe-separated full executable paths as stored on the
        /// profile (the ForegroundMonitorService match source). Executables
        /// above is the display form (file names only). Feeds the card's
        /// exe icon and mono exe line (#175).</summary>
        public string ExecutablePaths
        {
            get => _executablePaths;
            set
            {
                if (SetProperty(ref _executablePaths, value))
                {
                    OnPropertyChanged(nameof(FirstExecutablePath));
                    OnPropertyChanged(nameof(FirstExecutableName));
                    OnPropertyChanged(nameof(SecondExecutableName));
                    OnPropertyChanged(nameof(ExtraExecutablesSuffix));
                }
            }
        }

        /// <summary>Full path of the first executable, or null when none.
        /// The card's icon converter File.Exists-gates this path.</summary>
        public string FirstExecutablePath
        {
            get
            {
                if (string.IsNullOrEmpty(_executablePaths)) return null;
                var parts = _executablePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length > 0 ? parts[0] : null;
            }
        }

        /// <summary>File name of the first executable for the card's mono
        /// exe line, or null when the profile has none. The tooltip carries
        /// the full list via Executables.</summary>
        public string FirstExecutableName
        {
            get
            {
                if (string.IsNullOrEmpty(_executablePaths)) return null;
                var parts = _executablePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length > 0 ? System.IO.Path.GetFileName(parts[0]) : null;
            }
        }

        /// <summary>File name of the second executable, or null. The card
        /// face shows up to two names before collapsing to the +N marker
        /// (maintainer request 2026-07-05).</summary>
        public string SecondExecutableName
        {
            get
            {
                if (string.IsNullOrEmpty(_executablePaths)) return null;
                var parts = _executablePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length > 1 ? System.IO.Path.GetFileName(parts[1]) : null;
            }
        }

        /// <summary>Locale-neutral "+N" marker for rules beyond the two
        /// names the card face shows. Its own property, rendered by a
        /// separate non-trimming element, so a long name that ellipsizes
        /// cannot swallow the multi-exe cue.</summary>
        public string ExtraExecutablesSuffix
        {
            get
            {
                if (string.IsNullOrEmpty(_executablePaths)) return null;
                var parts = _executablePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length > 2 ? "+" + (parts.Length - 2) : null;
            }
        }

        /// <summary>Whether the profile carries any executable match rule.
        /// Half of the auto-switch chip gate (#175). The other half is the
        /// global EnableAutoProfileSwitching toggle, bound in XAML.</summary>
        public bool HasExecutables => !string.IsNullOrEmpty(_executables);

        /// <summary>Exe-match rule summary for the cold auto-switch chip
        /// (#175): the display exe list under the localized prefix.</summary>
        public string AutoSwitchRuleSummary => string.IsNullOrEmpty(_executables)
            ? string.Empty
            : string.Format(Strings.Instance.Profiles_AutoSwitchRule_Format, _executables);

        private string _topologyLabel;
        public string TopologyLabel
        {
            get => _topologyLabel;
            set { if (SetProperty(ref _topologyLabel, value)) OnPropertyChanged(nameof(HasNoSlots)); }
        }

        public bool HasNoSlots => XboxCount == 0 && PlayStationCount == 0 && ExtendedCount == 0 && MidiCount == 0 && KbmCount == 0;

        private int _xboxCount;
        public int XboxCount
        {
            get => _xboxCount;
            set => SetProperty(ref _xboxCount, value);
        }

        private int _playstationCount;
        public int PlayStationCount
        {
            get => _playstationCount;
            set => SetProperty(ref _playstationCount, value);
        }

        private int _extendedCount;
        public int ExtendedCount
        {
            get => _extendedCount;
            set => SetProperty(ref _extendedCount, value);
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
