using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
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
            RaiseForegroundText();
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

        /// <summary>The languages the picker lists, each under its own name
        /// with its English name beside it. The list is LanguageCatalog's:
        /// one row for each translation in the tree (#505).</summary>
        public IReadOnlyList<LanguageInfo> AvailableLanguages => LanguageCatalog.All;

        private CultureInfo _selectedLanguage;

        /// <summary>
        /// Currently selected UI language. Persisted as the culture name (e.g. "en-US", "ja").
        /// Changing this applies the new language immediately (live switching).
        /// </summary>
        public CultureInfo SelectedLanguage
        {
            get
            {
                if (_selectedLanguage != null) return _selectedLanguage;
                // The catalog walks the parent chain before giving up. An
                // exact-name match alone sent de-DE, zh-Hans-CN and every
                // other specific culture to English, even when the neutral
                // parent (de, zh-Hans) is right there in the list. A German
                // user with a German Windows saw the picker say English.
                return (LanguageCatalog.Resolve(CultureInfo.CurrentUICulture) ?? LanguageCatalog.English).Culture;
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
            OnPropertyChanged(nameof(HIDMaestroStatusText));
            OnPropertyChanged(nameof(BthPS3StatusText));
            OnPropertyChanged(nameof(MidiServicesStatusText));
            OnPropertyChanged(nameof(MidiServicesDetailText));
            OnPropertyChanged(nameof(MidiInstallTooltip));
            OnPropertyChanged(nameof(SteamVrStatusText));
            OnPropertyChanged(nameof(SteamVrSentence));
            // Both build their text from Strings.Instance, so they are exactly
            // as culture-dependent as the two above and were the pair this
            // handler missed. The Remote Link protection picker and its hint
            // kept the previous language until something else re-read them.
            OnPropertyChanged(nameof(IdentityProtectionModes));
            OnPropertyChanged(nameof(IdentityProtectionHint));
            // A paired PC's state line and its fingerprint's gamepad-only
            // word are built from the string table on every read.
            foreach (var peer in TrustedPeers) peer.RefreshText();
            // Built from Strings.Instance on every read (#457).
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(AboutSubLine));

            // Refresh the default profile's display name in the list.
            var defaultItem = ProfileItems.FirstOrDefault(p => p.IsDefault);
            if (defaultItem != null)
                defaultItem.Name = Strings.Instance.Profile_Default;

            // And every card's game line and type chips, which the line above
            // does not cover: it renames one item, while those words are
            // culture-dependent on all of them.
            foreach (var item in ProfileItems)
                item?.RaiseCultureDependentText();
            RaiseForegroundText();

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
            // override: the OS display language a fresh install shows, read
            // through its row of the language table as startup reads it.
            Strings.ChangeCulture(LanguageCatalog.InterfaceCulture(App.StartupUICulture ?? CultureInfo.InstalledUICulture));
            OnPropertyChanged(nameof(SelectedLanguage));
        }

        /// <summary>Sets the language from a persisted code, applying the culture on startup.</summary>
        internal void SetLanguageFromCode(string code)
        {
            if (!string.IsNullOrEmpty(code))
            {
                CultureInfo match = LanguageCatalog.Find(code)?.Culture;
                if (match != null)
                {
                    _selectedLanguage = match;
                    // Apply the culture so the UI thread and resource lookups use
                    // the saved language immediately (without raising CultureChanged
                    // since the UI hasn't been built yet at load time).
                    // Through the shared culture-change path, not two raw
                    // assignments. The note above assumed this only ran before
                    // the interface existed, but a reload runs it on a live
                    // window: the thread culture moved and nothing told the
                    // bindings or the culture handlers, so a reload to a
                    // different saved language left the visible text in the old
                    // one. At startup nothing is subscribed yet, so the extra
                    // notification costs nothing.
                    if (!Equals(CultureInfo.CurrentUICulture, match))
                        PadForge.Resources.Strings.Strings.ChangeCulture(match);
                    OnPropertyChanged(nameof(SelectedLanguage));
                }
            }
        }

        // ─────────────────────────────────────────────
        //  The page's groups and its search (#574)
        // ─────────────────────────────────────────────

        private SettingsGroup _selectedSettingsGroup = SettingsGroup.General;

        /// <summary>The group the Settings page shows while its search box is
        /// empty. For this run only: the page opens on General.</summary>
        public SettingsGroup SelectedSettingsGroup
        {
            get => _selectedSettingsGroup;
            set => SetProperty(ref _selectedSettingsGroup, value);
        }

        private string _settingsSearchText = string.Empty;

        /// <summary>What the Settings page's search box holds. While it holds a
        /// word the page shows the matching rows of every group. Not saved, and
        /// emptied when the page is left.</summary>
        public string SettingsSearchText
        {
            get => _settingsSearchText;
            set => SetProperty(ref _settingsSearchText, value ?? string.Empty);
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

        /// <summary>HidHide status display text. HidHide installs on x64 and
        /// on ARM64 Windows. On any other machine there is nothing to install,
        /// so "Not Installed" beside a dead Install button would read as
        /// something the user could fix.</summary>
        public string HidHideStatusText =>
            _isHidHideInstalled ? Strings.Instance.Common_Installed
            : !PadForge.Engine.PlatformSupport.HidHideAvailable ? Strings.Instance.Common_NotAvailableOnArm64
            : Strings.Instance.Common_NotInstalled;

        private string _hidHideVersion = string.Empty;

        /// <summary>HidHide driver version string.</summary>
        public string HidHideVersion
        {
            get => _hidHideVersion;
            set => SetProperty(ref _hidHideVersion, value);
        }

        private string _hidMaestroVersion = GetEmbeddedHidMaestroVersion();

        /// <summary>HIDMaestro SDK version string, read from the embedded
        /// HIDMaestro.Core assembly at startup. Bare, with no "v" before
        /// it, as every version the Settings page shows.</summary>
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
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
            }
            catch { return string.Empty; }
        }

        private bool _isHIDMaestroInstalled;

        /// <summary>Whether HIDMaestro's driver is in the driver store
        /// (Services.HIDMaestroRemoval.IsDriverInstalled). PadForge installs it
        /// with its first virtual controller, and Uninstall removes it.</summary>
        public bool IsHIDMaestroInstalled
        {
            get => _isHIDMaestroInstalled;
            set
            {
                if (SetProperty(ref _isHIDMaestroInstalled, value))
                    OnPropertyChanged(nameof(HIDMaestroStatusText));
            }
        }

        /// <summary>HIDMaestro status display text.</summary>
        public string HIDMaestroStatusText =>
            _isHIDMaestroInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        private RelayCommand _uninstallHIDMaestroCommand;

        /// <summary>Opens the removal of HIDMaestro from the PC (#518). Always
        /// available: with the driver gone, the dialog still lists whatever
        /// else HIDMaestro left, or says nothing is left.</summary>
        public RelayCommand UninstallHIDMaestroCommand =>
            _uninstallHIDMaestroCommand ??= new RelayCommand(
                () => UninstallHIDMaestroRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Raised when the user asks to remove HIDMaestro.</summary>
        public event EventHandler UninstallHIDMaestroRequested;

        /// <summary>The BthPS3 version PadForge carries and installs, from
        /// the DriverVer of its embedded BthPS3.inf.</summary>
        public string BthPS3Version { get; } = GetBundledBthPs3Version();

        private static string GetBundledBthPs3Version()
        {
            try
            {
                var v = Services.Ds3DriverInstaller.BundledBthPs3Version();
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : string.Empty;
            }
            catch { return string.Empty; }
        }

        private bool _isBthPS3Installed;

        /// <summary>Whether either BthPS3 driver is installed as a service
        /// (Services.Ds3DriverInstaller.IsAnyBthPs3DriverInstalled). PadForge installs
        /// BthPS3 the first time a PlayStation 3, Move or Navigation
        /// controller is paired, and Uninstall removes it.</summary>
        public bool IsBthPS3Installed
        {
            get => _isBthPS3Installed;
            set
            {
                if (SetProperty(ref _isBthPS3Installed, value))
                    OnPropertyChanged(nameof(BthPS3StatusText));
            }
        }

        /// <summary>BthPS3 status display text.</summary>
        public string BthPS3StatusText =>
            _isBthPS3Installed ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        private RelayCommand _uninstallBthPS3Command;

        /// <summary>Opens the removal of BthPS3 from the PC. Always available:
        /// with the drivers gone, the dialog still lists the WinUSB package
        /// and certificate PadForge left, or says nothing is left.</summary>
        public RelayCommand UninstallBthPS3Command =>
            _uninstallBthPS3Command ??= new RelayCommand(
                () => UninstallBthPS3Requested?.Invoke(this, EventArgs.Empty));

        /// <summary>Raised when the user asks to remove BthPS3.</summary>
        public event EventHandler UninstallBthPS3Requested;

        private RelayCommand _installHidHideCommand;

        /// <summary>Command to install the HidHide driver.</summary>
        public RelayCommand InstallHidHideCommand =>
            _installHidHideCommand ??= new RelayCommand(
                () => InstallHidHideRequested?.Invoke(this, EventArgs.Empty),
                () => !_isHidHideInstalled && PadForge.Engine.PlatformSupport.HidHideAvailable);

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

        /// <summary>Controllers on COM ports the user added in the pairing
        /// dialog (hifihedgehog/SDL#33). SDL opens each named port.</summary>
        public ObservableCollection<SerialControllerEntry> SerialControllers { get; } = new();

        /// <summary>Raised on each add or remove in the dialog, never by the
        /// load, so reading the file cannot mark it dirty.</summary>
        public event EventHandler SerialControllersChanged;

        internal void RaiseSerialControllersChanged() => SerialControllersChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>IDs of the devices the user moved to PadForge's WinUSB
        /// driver from the Devices page (hifihedgehog/SDL#33 Part 15).</summary>
        public ObservableCollection<string> WinUsbOptIns { get; } = new();

        /// <summary>The keyboards the user took from Windows for PadForge
        /// alone from the Devices page (#549), each with the row it had as a
        /// Raw Input keyboard.</summary>
        public ObservableCollection<PadForge.Common.Input.WinUsbKeyboards.KeyboardTakeoverEntry> KeyboardTakeovers { get; } = new();

        /// <summary>Pads in iCade mode the user marked on the Devices page
        /// (hifihedgehog/SDL#33 Part 16), as "0xVVVV/0xPPPP" pairs for
        /// SDL_JOYSTICK_ICADE_DEVICES.</summary>
        public ObservableCollection<string> ICadePads { get; } = new();

        /// <summary>DJI RC and RC 2 remotes the user added by address in the
        /// pairing dialog (hifihedgehog/SDL#33 Part 6), as "a.b.c.d:port" for
        /// SDL_JOYSTICK_DJI_REMOTE_TCP_HOSTS.</summary>
        public ObservableCollection<string> DjiRemoteHosts { get; } = new();

        /// <summary>The Namco USIO's layout, "taiko" or "tekken", chosen from
        /// the board's rows on the Devices page (hifihedgehog/SDL#33
        /// Part 14).</summary>
        public string UsioLayout { get; set; } = PadForge.Common.Input.InputManager.UsioTaiko;

        /// <summary>Raised on each add or remove in the dialog, never by the
        /// load, so reading the file cannot mark it dirty.</summary>
        public event EventHandler DjiRemoteHostsChanged;

        internal void RaiseDjiRemoteHostsChanged() => DjiRemoteHostsChanged?.Invoke(this, EventArgs.Empty);

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
        public event Action<string, bool> PeerAssignmentPermissionChanged;
        public event Action<string> PeerAssignmentsRequested;

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
                    hostPort => PeerConnectRequested?.Invoke(hostPort),
                    p.AllowRemoteAssignments,
                    (fp, allowed) => PeerAssignmentPermissionChanged?.Invoke(fp, allowed),
                    fp => PeerAssignmentsRequested?.Invoke(fp)));
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
                // A negative index is never a user pick. It is what Selector
                // writes back when its ItemsSource is replaced, and
                // IdentityProtectionModes hands out a NEW array on every read,
                // so raising it on a language change blanked the box and
                // pushed -1 into the source. InputService then turned that
                // into mode 0, which matches no current mode, so it did not
                // early-return and called EnsureIdentityUnlocked: changing the
                // app language popped the Remote Link password dialog.
                if (value < 0) return;
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
        /// initialize from settings and to revert a canceled switch).</summary>
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

        // Microsoft deleted the Windows MIDI Services runtime installers on
        // 2026-10-01. The card says which API PadForge drives, offers
        // PadForge's build of the runtime where it would give MIDI slots
        // their own ports (MidiApiSelection.CanOfferRuntimeInstall), and
        // offers to uninstall an installed runtime, Microsoft's or
        // PadForge's.

        private Common.Input.MidiApiKind _activeMidiApi;

        /// <summary>The API PadForge drives on this PC
        /// (<see cref="Common.Input.MidiApiSelection"/>).</summary>
        internal Common.Input.MidiApiKind ActiveMidiApi
        {
            get => _activeMidiApi;
            set
            {
                if (_activeMidiApi == value) return;
                _activeMidiApi = value;
                OnPropertyChanged(nameof(IsMidiAvailable));
                OnMidiCardChanged();
            }
        }

        private bool _canInstallMidiRuntime;

        /// <summary>Whether the card offers to install the App SDK
        /// runtime.</summary>
        public bool CanInstallMidiRuntime
        {
            get => _canInstallMidiRuntime;
            set
            {
                if (SetProperty(ref _canInstallMidiRuntime, value))
                    _installMidiRuntimeCommand?.NotifyCanExecuteChanged();
            }
        }

        private RelayCommand _installMidiRuntimeCommand;

        /// <summary>Installs PadForge's build of the App SDK runtime.
        /// Unavailable while a runtime uninstaller PadForge started still
        /// runs, as Uninstall is: Windows Installer runs one installation at
        /// a time and refuses a second with 1618, and the card offers
        /// Install as soon as the registry stops listing the runtime, which
        /// can be before its uninstaller exits.</summary>
        public RelayCommand InstallMidiRuntimeCommand =>
            _installMidiRuntimeCommand ??= new RelayCommand(
                () => InstallMidiRuntimeRequested?.Invoke(this, EventArgs.Empty),
                () => _canInstallMidiRuntime && !_midiUninstallerRunning);

        /// <summary>The Install button's tooltip: what it installs, or while
        /// an uninstaller still runs, that Install waits for it. Under the
        /// legacy API MIDI keeps running through the uninstall, so the card's
        /// own line for a running uninstaller, which says MIDI starts again,
        /// does not fit here.</summary>
        public string MidiInstallTooltip => _midiUninstallerRunning
            ? Strings.Instance.Settings_MidiInstallAfterUninstaller
            : Strings.Instance.Settings_MidiInstallTooltip;

        /// <summary>Raised when the user requests installing the runtime.</summary>
        public event EventHandler InstallMidiRuntimeRequested;

        private bool _isMidiRuntimeInstalled;

        /// <summary>Whether the App SDK runtime is installed, Microsoft's or
        /// PadForge's. It can be, beside the in-box API, after the November
        /// update.</summary>
        public bool IsMidiRuntimeInstalled
        {
            get => _isMidiRuntimeInstalled;
            set
            {
                if (SetProperty(ref _isMidiRuntimeInstalled, value))
                    OnMidiCardChanged();
            }
        }

        private string _midiRuntimeVersion = string.Empty;

        /// <summary>The runtime's version, from its DLL.</summary>
        public string MidiRuntimeVersion
        {
            get => _midiRuntimeVersion;
            set
            {
                if (SetProperty(ref _midiRuntimeVersion, value ?? string.Empty))
                    OnPropertyChanged(nameof(MidiServicesDetailText));
            }
        }

        private bool _midiApiNotStarted;

        /// <summary>True when the probe found no API it could start, the
        /// legacy one included, or timed out on a stuck service
        /// (<see cref="Common.Input.MidiApiSelection.ForCard"/>).</summary>
        public bool MidiApiNotStarted
        {
            get => _midiApiNotStarted;
            set
            {
                if (SetProperty(ref _midiApiNotStarted, value))
                    OnMidiCardChanged();
            }
        }

        private Common.Input.MidiNotRunningReason _midiNotRunningReason;

        /// <summary>Why MIDI is not running, for the detail line while
        /// <see cref="MidiApiNotStarted"/> holds: a service that did not
        /// respond, which PadForge restarts, a classic MIDI driver that did
        /// not respond, which no service restart reaches, or the runtime's
        /// uninstaller still running.</summary>
        internal Common.Input.MidiNotRunningReason MidiNotRunningReason
        {
            get => _midiNotRunningReason;
            set
            {
                if (_midiNotRunningReason == value) return;
                _midiNotRunningReason = value;
                OnMidiCardChanged();
            }
        }

        private int _midiInBoxFailure;

        /// <summary>The HRESULT of an in-box API that Windows registers but
        /// that failed to activate, or 0. The legacy API runs in its place,
        /// and the detail line names the failure.</summary>
        internal int MidiInBoxFailure
        {
            get => _midiInBoxFailure;
            set
            {
                if (_midiInBoxFailure == value) return;
                _midiInBoxFailure = value;
                OnMidiCardChanged();
            }
        }

        /// <summary>Whether any MIDI API can run MIDI slots and MIDI input.</summary>
        public bool IsMidiAvailable => _activeMidiApi != Common.Input.MidiApiKind.None;

        /// <summary>The card's status line: which API, or that none
        /// started.</summary>
        public string MidiServicesStatusText => _activeMidiApi switch
        {
            Common.Input.MidiApiKind.InBox => Strings.Instance.Settings_MidiStatusInBox,
            Common.Input.MidiApiKind.AppSdk => Strings.Instance.Settings_MidiStatusRuntime,
            Common.Input.MidiApiKind.Legacy => Strings.Instance.Settings_MidiStatusLegacy,
            _ => _midiApiNotStarted
                ? Strings.Instance.Settings_MidiStatusNotRunning
                : Strings.Instance.Settings_MidiStatusUnavailable,
        };

        /// <summary>The line under the status: the runtime's version while
        /// PadForge uses it, a note that a leftover runtime is no longer
        /// needed once Windows has the API, how the legacy API sends and
        /// listens (naming the in-box API's failure when that is why the
        /// legacy API runs), or why nothing runs.</summary>
        public string MidiServicesDetailText => _activeMidiApi switch
        {
            Common.Input.MidiApiKind.InBox => _isMidiRuntimeInstalled
                ? string.Format(Strings.Instance.Settings_MidiOlderRuntime_Format, _midiRuntimeVersion)
                : string.Empty,
            Common.Input.MidiApiKind.AppSdk => _midiRuntimeVersion,
            Common.Input.MidiApiKind.Legacy => _midiInBoxFailure != 0
                ? string.Format(Strings.Instance.Settings_MidiInBoxFailed_Format, _midiInBoxFailure.ToString("X8"))
                : Strings.Instance.Settings_MidiLegacy,
            _ => !_midiApiNotStarted ? string.Empty
                : _midiNotRunningReason switch
                {
                    Common.Input.MidiNotRunningReason.Uninstalling => Strings.Instance.Settings_MidiUninstallRunning,
                    Common.Input.MidiNotRunningReason.DriverDidNotRespond => Strings.Instance.Settings_MidiNotRunningDriver,
                    _ => Strings.Instance.Settings_MidiNotRunning,
                },
        };

        /// <summary>True when the detail line is the older runtime's
        /// version, which the card sets in the telemetry face.</summary>
        public bool MidiDetailIsVersion => _activeMidiApi == Common.Input.MidiApiKind.AppSdk;

        private void OnMidiCardChanged()
        {
            OnPropertyChanged(nameof(MidiServicesStatusText));
            OnPropertyChanged(nameof(MidiServicesDetailText));
            OnPropertyChanged(nameof(MidiDetailIsVersion));
            _uninstallMidiServicesCommand?.NotifyCanExecuteChanged();
        }

        private bool _midiUninstallerRunning;

        /// <summary>True while a runtime uninstaller PadForge started has not
        /// exited (<see cref="Common.Input.MidiRuntimeSwitch.UninstallerRunning"/>).
        /// One that outlives its five-minute wait leaves the card usable
        /// again, and Uninstall and Install stay unavailable until it exits:
        /// Windows Installer runs one installation at a time, so a second
        /// one only fails.</summary>
        internal bool MidiUninstallerRunning
        {
            get => _midiUninstallerRunning;
            set
            {
                if (_midiUninstallerRunning == value) return;
                _midiUninstallerRunning = value;
                _uninstallMidiServicesCommand?.NotifyCanExecuteChanged();
                _installMidiRuntimeCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(MidiInstallTooltip));
            }
        }

        private RelayCommand _uninstallMidiServicesCommand;

        /// <summary>Uninstalls the runtime. A MIDI slot running on it keeps
        /// it installed. When Windows has the API, or the legacy API runs
        /// the slots, the runtime can go while they run. Unavailable while
        /// an uninstaller already runs.</summary>
        public RelayCommand UninstallMidiServicesCommand =>
            _uninstallMidiServicesCommand ??= new RelayCommand(
                () => UninstallMidiServicesRequested?.Invoke(this, EventArgs.Empty),
                () => _isMidiRuntimeInstalled
                      && !_midiUninstallerRunning
                      && !(_activeMidiApi == Common.Input.MidiApiKind.AppSdk && HasAnyMidiSlots()));

        /// <summary>Raised when the user requests uninstalling the runtime.</summary>
        public event EventHandler UninstallMidiServicesRequested;

        // ─────────────────────────────────────────────
        //  SteamVR (VR controllers, issue #49)
        // ─────────────────────────────────────────────

        private bool _isSteamVrInstalled;

        /// <summary>Whether a SteamVR install (either shape: Steam client
        /// or the Steam-free steamcmd one) is present.</summary>
        public bool IsSteamVrInstalled
        {
            get => _isSteamVrInstalled;
            set
            {
                if (SetProperty(ref _isSteamVrInstalled, value))
                {
                    OnPropertyChanged(nameof(SteamVrStatusText));
                    OnPropertyChanged(nameof(SteamVrSentence));
                    OnPropertyChanged(nameof(ShowSteamVrUninstall));
                    _installSteamVrCommand?.NotifyCanExecuteChanged();
                    _uninstallSteamVrCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>SteamVR status display text, tiered (#287 audit): the
        /// deepest true state wins, so the row says more than "installed"
        /// once the consumer or a VR slot is live. This is the only SteamVR
        /// status row now that the Dashboard's driver strip is gone. Reads
        /// the live statics. MainWindow's periodic refresh raises the change
        /// notification.</summary>
        public string SteamVrStatusText => SteamVrStateNow() switch
        {
            SteamVrState.NotInstalled => Strings.Instance.Common_NotInstalled,
            SteamVrState.ControllersLive => Strings.Instance.Vr_Status_ControllersLive,
            SteamVrState.DriverConnected => Strings.Instance.Vr_Status_DriverConnected,
            SteamVrState.Running => Strings.Instance.Vr_Status_Running,
            _ => Strings.Instance.Common_Installed,
        };

        /// <summary>The same state as one line in sentence case, as a VR
        /// slot's header says it after the slot's name (#574): "SteamVR
        /// installed", "SteamVR running, controllers live".</summary>
        public string SteamVrSentence => SteamVrStateNow() switch
        {
            SteamVrState.NotInstalled => Strings.Instance.Pad_SteamVr_NotInstalled,
            SteamVrState.ControllersLive => Strings.Instance.Pad_SteamVr_ControllersLive,
            SteamVrState.DriverConnected => Strings.Instance.Pad_SteamVr_DriverConnected,
            SteamVrState.Running => Strings.Instance.Pad_SteamVr_Running,
            _ => Strings.Instance.Pad_SteamVr_Installed,
        };

        /// <summary>SteamVR's states, from the least to the deepest.</summary>
        private enum SteamVrState { NotInstalled, Installed, Running, DriverConnected, ControllersLive }

        /// <summary>The deepest of SteamVR's states that is true now. Reads
        /// the live statics.</summary>
        private SteamVrState SteamVrStateNow()
        {
            if (!_isSteamVrInstalled) return SteamVrState.NotInstalled;
            var (drv, live) = PadForge.Common.Input.HMaestroVRController.GlobalDriverStatus();
            if (live) return SteamVrState.ControllersLive;
            if (drv) return SteamVrState.DriverConnected;
            if (PadForge.Common.Input.OpenVrConsumerService.ServerConnected) return SteamVrState.Running;
            return SteamVrState.Installed;
        }

        /// <summary>Raises the SteamVR row's change notification. MainWindow's
        /// periodic status refresh calls it.</summary>
        public void RefreshSteamVrStatus()
        {
            OnPropertyChanged(nameof(SteamVrStatusText));
            OnPropertyChanged(nameof(SteamVrSentence));
            RefreshSteamVrDriverConflict();
        }

        private string _steamVrDriverConflictText = string.Empty;

        /// <summary>The warning the SteamVR row shows when another OpenVR
        /// driver is registered under HIDMaestro's driver name, naming its
        /// folders. Empty when there is none. OpenVR requires driver names to
        /// be unique, so SteamVR may load that driver in place of
        /// HIDMaestro's and the VR slot's controllers never appear
        /// (HMVR.FindDriverNameConflicts, HIDMaestro 1.12.0).</summary>
        public string SteamVrDriverConflictText
        {
            get => _steamVrDriverConflictText;
            private set
            {
                if (SetProperty(ref _steamVrDriverConflictText, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasSteamVrDriverConflict));
            }
        }

        public bool HasSteamVrDriverConflict => _steamVrDriverConflictText.Length > 0;

        private long _steamVrConflictCheckedTick;
        private bool _steamVrConflictChecked;
        private const long SteamVrConflictTtlMs = 10_000;

        /// <summary>Test seam for the conflict probe, the SDK's own call in
        /// production. It reads the user's openvrpaths.vrpath and SteamVR's
        /// drivers folder and changes nothing.</summary>
        internal static Func<IReadOnlyList<string>> SteamVrConflictProbe = () => HIDMaestro.HMVR.FindDriverNameConflicts();

        /// <summary>Re-reads the conflict list at most every ten seconds:
        /// the probe reads files, and the periodic refresh runs far more often
        /// than a user installs an OpenVR driver.</summary>
        private void RefreshSteamVrDriverConflict()
        {
            if (!_isSteamVrInstalled)
            {
                SteamVrDriverConflictText = string.Empty;
                return;
            }
            long now = Environment.TickCount64;
            if (_steamVrConflictChecked && now - _steamVrConflictCheckedTick < SteamVrConflictTtlMs) return;
            _steamVrConflictChecked = true;
            _steamVrConflictCheckedTick = now;
            IReadOnlyList<string> folders;
            try { folders = SteamVrConflictProbe(); }
            catch { folders = null; }
            SteamVrDriverConflictText = SteamVrDriverConflictLine(folders);
        }

        /// <summary>The warning for a conflict list: empty for none, else the
        /// format with the folders joined one per line.</summary>
        internal static string SteamVrDriverConflictLine(IReadOnlyList<string> folders)
            => folders == null || folders.Count == 0
                ? string.Empty
                : string.Format(Strings.Instance.Vr_DriverNameConflict_Format,
                    string.Join(Environment.NewLine, folders));

        private string _steamVrInstallDir = PadForge.Common.DriverInstaller.SteamVrInstallDir;

        /// <summary>Where the Steam-free install will land. Not persisted by
        /// PadForge: after a successful install the HIDMaestro path hint IS
        /// the durable record, so this only needs to live until then.</summary>
        public string SteamVrInstallDir
        {
            get => _steamVrInstallDir;
            set => SetProperty(ref _steamVrInstallDir, value);
        }

        private bool _isSteamVrOwned;

        /// <summary>Whether the present SteamVR install is the Steam-free one
        /// PADFORGE created (HIDMaestro hint set, and not Steam's own
        /// install). Gates the Uninstall button: a Steam-client install is
        /// never PadForge's to remove.</summary>
        public bool IsSteamVrOwned
        {
            get => _isSteamVrOwned;
            set
            {
                if (SetProperty(ref _isSteamVrOwned, value))
                {
                    OnPropertyChanged(nameof(ShowSteamVrUninstall));
                    _uninstallSteamVrCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>Uninstall renders only for an installed AND owned copy.</summary>
        public bool ShowSteamVrUninstall => _isSteamVrInstalled && _isSteamVrOwned;

        private RelayCommand _installSteamVrCommand;

        /// <summary>Command to install SteamVR Steam-free via steamcmd.</summary>
        public RelayCommand InstallSteamVrCommand =>
            _installSteamVrCommand ??= new RelayCommand(
                () => InstallSteamVrRequested?.Invoke(this, EventArgs.Empty),
                () => !_isSteamVrInstalled);

        private RelayCommand _uninstallSteamVrCommand;

        /// <summary>Command to remove the PadForge-owned Steam-free install.</summary>
        public RelayCommand UninstallSteamVrCommand =>
            _uninstallSteamVrCommand ??= new RelayCommand(
                () => UninstallSteamVrRequested?.Invoke(this, EventArgs.Empty),
                () => _isSteamVrInstalled && _isSteamVrOwned && !HasAnyVrSlots());

        /// <summary>Raised when the user requests the Steam-free SteamVR install.</summary>
        public event EventHandler InstallSteamVrRequested;

        /// <summary>Raised when the user requests removal of the owned install.</summary>
        public event EventHandler UninstallSteamVrRequested;

        // ─────────────────────────────────────────────
        //  Driver uninstall guards
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set by MainWindow to provide slot-type queries for uninstall guards.
        /// Returns true if any created slot uses MIDI.
        /// </summary>
        internal Func<bool> HasAnyMidiSlots { get; set; } = () => false;

        /// <summary>
        /// Set by MainWindow to provide slot-type queries for uninstall guards.
        /// Returns true if any created slot is a VR slot.
        ///
        /// <para>SteamVR had no such guard at all: HidHide refused to uninstall
        /// under a hidden device and MIDI Services under a MIDI slot, but the
        /// runtime every VR slot depends on could be removed with those slots
        /// sitting right there in the rail.</para>
        /// </summary>
        internal Func<bool> HasAnyVrSlots { get; set; } = () => false;

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
            _uninstallSteamVrCommand?.NotifyCanExecuteChanged();
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

        private bool _closeToTray;

        private RelayCommand _resetCloseToTrayCommand;
        public RelayCommand ResetCloseToTrayCommand =>
            _resetCloseToTrayCommand ??= new RelayCommand(() => ResetSetting(nameof(CloseToTray)));

        /// <summary>Whether closing the window keeps PadForge running in the system tray.</summary>
        public bool CloseToTray
        {
            get => _closeToTray;
            set => SetProperty(ref _closeToTray, value);
        }
        private bool _alwaysShowTrayIcon;
        /// <summary>Keep the tray icon up while the window is open (#439), so
        /// Exit in its menu never needs the window closed first.</summary>
        public bool AlwaysShowTrayIcon
        {
            get => _alwaysShowTrayIcon;
            set => SetProperty(ref _alwaysShowTrayIcon, value);
        }

        // ── Updates (#457) ──

        private bool _checkForUpdatesAutomatically = true;
        /// <summary>Check GitHub at launch and every 12 hours.</summary>
        public bool CheckForUpdatesAutomatically
        {
            get => _checkForUpdatesAutomatically;
            set => SetProperty(ref _checkForUpdatesAutomatically, value);
        }

        private bool _installUpdatesAutomatically;
        /// <summary>Download what the automatic check finds and install it
        /// at the next launch.</summary>
        public bool InstallUpdatesAutomatically
        {
            get => _installUpdatesAutomatically;
            set => SetProperty(ref _installUpdatesAutomatically, value);
        }

        private bool _includePreReleaseUpdates;
        /// <summary>Offer the rolling dev build instead of releases.</summary>
        public bool IncludePreReleaseUpdates
        {
            get => _includePreReleaseUpdates;
            set => SetProperty(ref _includePreReleaseUpdates, value);
        }

        private Func<string> _updateStatus;
        /// <summary>The card's status line, empty until a check has run. It is
        /// built each time it is read, from the language in use then, so a
        /// language change reaches it (OnCultureChanged raises it).</summary>
        public string UpdateStatusText => _updateStatus?.Invoke() ?? string.Empty;

        /// <summary>Sets what the status line says, and whether it says this
        /// build is the newest. The function reads Strings.Instance itself
        /// and captures only fixed values (a version, a percentage, an error
        /// message). Null clears the line.</summary>
        internal void SetUpdateStatus(Func<string> text, bool upToDate = false)
        {
            _updateStatus = text;
            IsUpToDate = upToDate;
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(AboutSubLine));
        }

        private bool _isUpToDate;
        /// <summary>The last check found this build the newest (#574).</summary>
        public bool IsUpToDate
        {
            get => _isUpToDate;
            private set => SetProperty(ref _isUpToDate, value);
        }

        private string _versionNumber = string.Empty;
        /// <summary>The version alone, "6.0.0", which the About page shows
        /// beside PadForge's name (#574). ApplicationVersion is the full
        /// build line.</summary>
        public string VersionNumber
        {
            get => _versionNumber;
            set => SetProperty(ref _versionNumber, value ?? string.Empty);
        }

        /// <summary>The About page's line under PadForge's name (#574): the
        /// subtitle, then "Up to date." once a check found the newest build,
        /// or the Updates card's own sentence while a check runs, has failed
        /// or has found an update. Before any check, the subtitle alone.</summary>
        public string AboutSubLine
        {
            get
            {
                var s = Strings.Instance;
                string state = IsUpToDate ? s.About_UpToDate : UpdateStatusText;
                return string.IsNullOrEmpty(state)
                    ? s.About_Subtitle
                    : string.Format(CultureInfo.CurrentCulture, s.About_SubtitleState_Format, s.About_Subtitle, state);
            }
        }

        private bool _isUpdateAvailable;
        /// <summary>A newer build was found. Shows Install and Restart and
        /// Release Notes.</summary>
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set
            {
                if (SetProperty(ref _isUpdateAvailable, value))
                    _installUpdateCommand?.NotifyCanExecuteChanged();
            }
        }

        private bool _isUpdateBusy;
        /// <summary>A check or a download is running.</summary>
        public bool IsUpdateBusy
        {
            get => _isUpdateBusy;
            set
            {
                if (SetProperty(ref _isUpdateBusy, value))
                {
                    _checkForUpdatesNowCommand?.NotifyCanExecuteChanged();
                    _installUpdateCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        private bool _isUpdateDownloading;
        public bool IsUpdateDownloading
        {
            get => _isUpdateDownloading;
            set => SetProperty(ref _isUpdateDownloading, value);
        }

        private int _updateProgress;
        /// <summary>Download progress, 0 to 100.</summary>
        public int UpdateProgress
        {
            get => _updateProgress;
            set => SetProperty(ref _updateProgress, value);
        }

        private RelayCommand _checkForUpdatesNowCommand;
        public RelayCommand CheckForUpdatesNowCommand =>
            _checkForUpdatesNowCommand ??= new RelayCommand(
                () => CheckForUpdatesNowRequested?.Invoke(this, EventArgs.Empty),
                () => !_isUpdateBusy);

        private RelayCommand _installUpdateCommand;
        public RelayCommand InstallUpdateCommand =>
            _installUpdateCommand ??= new RelayCommand(
                () => InstallUpdateRequested?.Invoke(this, EventArgs.Empty),
                () => _isUpdateAvailable && !_isUpdateBusy);

        private RelayCommand _openReleaseNotesCommand;
        public RelayCommand OpenReleaseNotesCommand =>
            _openReleaseNotesCommand ??= new RelayCommand(
                () => OpenReleaseNotesRequested?.Invoke(this, EventArgs.Empty));

        public event EventHandler CheckForUpdatesNowRequested;
        public event EventHandler InstallUpdateRequested;
        public event EventHandler OpenReleaseNotesRequested;

        private bool _batteryNotifyEnabled = true;

        /// <summary>Low-battery notification master toggle (#293), default on.</summary>
        public bool BatteryNotifyEnabled
        {
            get => _batteryNotifyEnabled;
            set => SetProperty(ref _batteryNotifyEnabled, value);
        }

        private int _batteryNotifyThreshold = 15;

        /// <summary>Percent at or below which the low-battery notification
        /// fires (edge-triggered). Clamped 5-50, default 15.</summary>
        public int BatteryNotifyThreshold
        {
            get => _batteryNotifyThreshold;
            set => SetProperty(ref _batteryNotifyThreshold, System.Math.Clamp(value, 5, 50));
        }

        private bool _batteryNotifyVibrate;

        /// <summary>Optionally buzz the device itself on the low-battery edge,
        /// using the identify pulse train. Default off.</summary>
        public bool BatteryNotifyVibrate
        {
            get => _batteryNotifyVibrate;
            set => SetProperty(ref _batteryNotifyVibrate, value);
        }

        private int _batteryNotifyRepeatMinutes;

        /// <summary>Shows the low-battery notice again every this many
        /// minutes while the pad stays at or below the threshold and is not
        /// charging. 0, the default, never repeats. Clamped 0-60.</summary>
        public int BatteryNotifyRepeatMinutes
        {
            get => _batteryNotifyRepeatMinutes;
            set => SetProperty(ref _batteryNotifyRepeatMinutes, System.Math.Clamp(value, 0, 60));
        }

        private bool _batteryNotifyFull;

        /// <summary>Notify once when a pad's charge rises to 100%, which on a
        /// pad that reports no charging state (an Xbox pad) is the sign a
        /// charge finished. Opt-in, default off.</summary>
        public bool BatteryNotifyFull
        {
            get => _batteryNotifyFull;
            set => SetProperty(ref _batteryNotifyFull, value);
        }

        private bool _assignOfferNewDevice = true;

        /// <summary>Assignment Prompts: offer a device PadForge has never
        /// seen before to the virtual controller whose page is open when it
        /// connects. Default on.</summary>
        public bool AssignOfferNewDevice
        {
            get => _assignOfferNewDevice;
            set => SetProperty(ref _assignOfferNewDevice, value);
        }

        private bool _assignOfferEmptySlot = true;

        /// <summary>Assignment Prompts: offer any connecting device, new or
        /// already known, to the open virtual controller when that slot has
        /// no devices assigned. Default on.</summary>
        public bool AssignOfferEmptySlot
        {
            get => _assignOfferEmptySlot;
            set => SetProperty(ref _assignOfferEmptySlot, value);
        }

        private bool _assignOfferHome = true;

        /// <summary>Assignment Prompts: Home offers a device PadForge has
        /// just met a virtual controller of its own, once, while no virtual
        /// controller uses it (#574). Default on, like the two above.</summary>
        public bool AssignOfferHome
        {
            get => _assignOfferHome;
            set => SetProperty(ref _assignOfferHome, value);
        }

        private bool _handheldButtonsEnabled;

        /// <summary>Handheld PC Buttons (issue #343): adds the per-machine
        /// Hidden Buttons row and the System Motion row. Off by default;
        /// the engine sweep reads the registry flag this setter mirrors.</summary>
        public bool HandheldButtonsEnabled
        {
            get => _handheldButtonsEnabled;
            set
            {
                if (SetProperty(ref _handheldButtonsEnabled, value))
                    PadForge.Common.Input.HandheldButtonRegistry.FeatureEnabled = value;
            }
        }

        private bool _startMinimized;

        /// <summary>Whether to start the application minimized.</summary>
        public bool StartMinimized
        {
            get => _startMinimized;
            set => SetProperty(ref _startMinimized, value);
        }

        private bool _diagnosticsLoggingEnabled;

        /// <summary>Diagnostics logging (#303): mirrors the engine's
        /// in-memory event ring to diagnostics.log beside the exe,
        /// continuously. Persisted, so it survives restarts and works for
        /// auto-started sessions where a launch flag cannot. Default off:
        /// a healthy install writes no log.</summary>
        public bool DiagnosticsLoggingEnabled
        {
            get => _diagnosticsLoggingEnabled;
            set => SetProperty(ref _diagnosticsLoggingEnabled, value);
        }

        /// <summary>Where the diagnostics log and snapshots land, shown on
        /// the Diagnostics card.</summary>
        public string DiagnosticsFolderPath => Common.DiagnosticsLogControl.Folder;

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


        private string _pollingOverrideNote;

        /// <summary>Why the global polling knob is not currently in charge
        /// (#365 follow-up): while the active profile carries a polling
        /// override, this names the profile and the rate it imposes, and
        /// the Settings page shows it under the knob. Null or empty while
        /// the global value rules, which collapses the note. Written by
        /// InputService.ApplyEffectivePollingRate, the same single owner
        /// that writes the live rate, so the note can never disagree with
        /// what the loop is actually doing.</summary>
        public string PollingOverrideNote
        {
            get => _pollingOverrideNote;
            set => SetProperty(ref _pollingOverrideNote, value);
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

        private bool _flydigiEnhancedProtocol = true;

        /// <summary>SDL's Flydigi HIDAPI driver switch (#395). See
        /// InputManager.ApplyFlydigiEnhancedProtocol.</summary>
        public bool FlydigiEnhancedProtocol
        {
            get => _flydigiEnhancedProtocol;
            set => SetProperty(ref _flydigiEnhancedProtocol, value);
        }

        private bool _gKeysEnabled;

        /// <summary>Logitech G-keys through the G-key SDK (issue #454), the
        /// Flydigi switch's neighbor: a vendor input path this engine can
        /// read, off until asked for because turning it on loads a
        /// third-party library and opens a session with the Logitech
        /// software.</summary>
        public bool GKeysEnabled
        {
            get => _gKeysEnabled;
            set
            {
                if (SetProperty(ref _gKeysEnabled, value))
                    PadForge.Common.Input.LogitechGKeysRuntime.Enabled = value;
            }
        }

        private string _gKeysStatus = string.Empty;

        /// <summary>Which of the seven states the machine is in, pushed by
        /// InputService. Empty while the feature is off, which collapses the
        /// line.</summary>
        public string GKeysStatus
        {
            get => _gKeysStatus;
            set => SetProperty(ref _gKeysStatus, value);
        }

        private bool _gKeysStatusIsWarning;

        /// <summary>The G-keys line says something is wrong, which draws it in
        /// the warning color (#574). Running with keys arriving does not.</summary>
        public bool GKeysStatusIsWarning
        {
            get => _gKeysStatusIsWarning;
            set => SetProperty(ref _gKeysStatusIsWarning, value);
        }

        private bool _analogKeyboardsEnabled;

        /// <summary>Analog keyboards read over their vendor HID interface
        /// (issue #468), the G-keys switch's neighbor and shape: a vendor
        /// input path this engine can read, off until asked for because the
        /// polled families share their configurator's channel.</summary>
        public bool AnalogKeyboardsEnabled
        {
            get => _analogKeyboardsEnabled;
            set
            {
                if (SetProperty(ref _analogKeyboardsEnabled, value))
                    PadForge.Common.Input.AnalogKeyboardRuntime.Enabled = value;
            }
        }

        private string _analogKeyboardsStatus = string.Empty;

        /// <summary>Which keyboards are being read, pushed by InputService.
        /// Empty while the feature is off, which collapses the line.</summary>
        public string AnalogKeyboardsStatus
        {
            get => _analogKeyboardsStatus;
            set => SetProperty(ref _analogKeyboardsStatus, value);
        }

        private bool _blissBoxEnabled;

        /// <summary>Bliss-Box adapters driven through their API (issue #469),
        /// the analog keyboards switch's neighbor and shape: off until asked
        /// for, because it talks to the adapter over the channel the Bliss-Box
        /// API Tool and DeviceBuddy use.</summary>
        public bool BlissBoxEnabled
        {
            get => _blissBoxEnabled;
            set
            {
                if (SetProperty(ref _blissBoxEnabled, value))
                    PadForge.Common.Input.BlissBoxRuntime.Enabled = value;
            }
        }

        private string _blissBoxStatus = string.Empty;

        /// <summary>Which ports are being read and what is in them, pushed by
        /// InputService. Empty while the feature is off, which collapses the
        /// line.</summary>
        public string BlissBoxStatus
        {
            get => _blissBoxStatus;
            set => SetProperty(ref _blissBoxStatus, value);
        }

        /// <summary>Each Bliss-Box port's own choices (#469): its Dreamcast
        /// screen, its native arrow poll. Kept by the port's device and
        /// changed on the UI thread only.</summary>
        public List<PadForge.Services.BlissBoxPortData> BlissBoxPorts { get; set; } = new();

        /// <summary>The Default profile's Dreamcast screen picture (#469),
        /// base64 in image order, or null.</summary>
        public string DefaultProfileDreamcastPicture { get; set; }

        private bool _keyboardScreensEnabled;

        /// <summary>The keyboard screens (issue #519): the Logitech GamePanel
        /// screens, Arx Control and the S.T.R.I.K.E. 7, the Bliss-Box switch's
        /// neighbor and shape. Off until asked for, because it loads other
        /// vendors' libraries and can bind WinUSB to a G19's screen
        /// interface.</summary>
        public bool KeyboardScreensEnabled
        {
            get => _keyboardScreensEnabled;
            set
            {
                if (SetProperty(ref _keyboardScreensEnabled, value))
                    PadForge.Common.Input.KeyboardScreensRuntime.Enabled = value;
            }
        }

        private string _keyboardScreensStatus = string.Empty;

        /// <summary>The feature's lines, pushed by InputService: Arx Control's
        /// library while no Arx screen is present, the S.T.R.I.K.E. 7's while
        /// no S.T.R.I.K.E. 7 is, and no screen found. Each screen's own line
        /// is on its row. Empty while the feature is off or the engine is
        /// stopped, which collapses the lines.</summary>
        public string KeyboardScreensStatus
        {
            get => _keyboardScreensStatus;
            set => SetProperty(ref _keyboardScreensStatus, value);
        }

        private bool _keyboardScreensStatusIsWarning;

        /// <summary>The keyboard screens' lines include that no screen was
        /// found, which draws them in the warning color (#574). A library's
        /// line beside a screen that is present is information.</summary>
        public bool KeyboardScreensStatusIsWarning
        {
            get => _keyboardScreensStatusIsWarning;
            set => SetProperty(ref _keyboardScreensStatusIsWarning, value);
        }

        /// <summary>Each keyboard screen's choices (#519), kept by the
        /// screen's row. Changed on the UI thread only.</summary>
        public List<PadForge.Services.KeyboardScreenData> KeyboardScreens { get; set; } = new();

        /// <summary>The Default profile's keyboard screen picture (#519), a
        /// stored path, or null.</summary>
        public string DefaultProfileKeyboardScreenPicture { get; set; }

        /// <summary>A Strike7API.dll the user picked (#519), a stored path, or
        /// null for the copy PadForge downloads.</summary>
        public string Strike7LibraryPath { get; set; }

        private RelayCommand _downloadStrike7LibraryCommand;
        private RelayCommand _pickStrike7LibraryCommand;
        private RelayCommand _installVc2008RuntimeCommand;

        /// <summary>Downloads Mad Catz's SDK and keeps its x64 library (#519).</summary>
        public RelayCommand DownloadStrike7LibraryCommand =>
            _downloadStrike7LibraryCommand ??= new RelayCommand(
                () => DownloadStrike7LibraryRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Points PadForge at a Strike7API.dll the user has (#519).</summary>
        public RelayCommand PickStrike7LibraryCommand =>
            _pickStrike7LibraryCommand ??= new RelayCommand(
                () => PickStrike7LibraryRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Installs Microsoft's Visual C++ 2008 runtime, which the
        /// S.T.R.I.K.E. 7 library links (#519).</summary>
        public RelayCommand InstallVc2008RuntimeCommand =>
            _installVc2008RuntimeCommand ??= new RelayCommand(
                () => InstallVc2008RuntimeRequested?.Invoke(this, EventArgs.Empty));

        public event EventHandler DownloadStrike7LibraryRequested;
        public event EventHandler PickStrike7LibraryRequested;
        public event EventHandler InstallVc2008RuntimeRequested;

        // ─────────────────────────────────────────────
        //  Novint Falcon (issue #521)
        // ─────────────────────────────────────────────

        private bool _novintFalconEnabled;

        /// <summary>Drive Novint Falcon (#521), the keyboard screens'
        /// neighbor and shape. Off until asked for: the Falcon moves on its
        /// own, and an open Falcon is locked against every other program,
        /// FalconJoyForce included.</summary>
        public bool NovintFalconEnabled
        {
            get => _novintFalconEnabled;
            set
            {
                if (SetProperty(ref _novintFalconEnabled, value))
                    PadForge.Common.Input.Falcon.FalconRuntime.Enabled = value;
            }
        }

        private string _falconStatus = string.Empty;

        /// <summary>The library's line, then why no Falcon opened while none
        /// is open, pushed by InputService. Each open Falcon's line is on its
        /// Devices panel. Empty while the switch is off.</summary>
        public string FalconStatus
        {
            get => _falconStatus;
            set => SetProperty(ref _falconStatus, value);
        }

        private bool _falconStatusIsWarning;

        /// <summary>The Falcon's lines say something is wrong, which draws
        /// them in the warning color (#574): a library that did not load, a
        /// picked copy that was not used, or no Falcon open.</summary>
        public bool FalconStatusIsWarning
        {
            get => _falconStatusIsWarning;
            set => SetProperty(ref _falconStatusIsWarning, value);
        }

        private bool _falconCanInstallDriver;

        /// <summary>A Falcon with no driver is plugged in and Force
        /// Dimension's package is at hand, so the Install button shows.</summary>
        public bool FalconCanInstallDriver
        {
            get => _falconCanInstallDriver;
            set => SetProperty(ref _falconCanInstallDriver, value);
        }

        private string _falconLibraryPath;

        /// <summary>A dhd64.dll the user picked (#521), a stored path, or null
        /// for the installed copies.</summary>
        public string FalconLibraryPath
        {
            get => _falconLibraryPath;
            set
            {
                _falconLibraryPath = value;
                PadForge.Common.Input.Falcon.FalconRuntime.PickedLibrary =
                    string.IsNullOrWhiteSpace(value) ? null : PadForge.Common.IconPackageManager.ResolvePath(value);
            }
        }

        private RelayCommand _pickFalconLibraryCommand;
        private RelayCommand _installFalconDriverCommand;
        private RelayCommand _openFalconDownloadCommand;

        /// <summary>Points PadForge at a dhd64.dll the user has (#521).</summary>
        public RelayCommand PickFalconLibraryCommand =>
            _pickFalconLibraryCommand ??= new RelayCommand(
                () => PickFalconLibraryRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Gives a Falcon with no driver Force Dimension's (#521).</summary>
        public RelayCommand InstallFalconDriverCommand =>
            _installFalconDriverCommand ??= new RelayCommand(
                () => InstallFalconDriverRequested?.Invoke(this, EventArgs.Empty));

        /// <summary>Opens Force Dimension's SDK download page (#521).</summary>
        public RelayCommand OpenFalconDownloadCommand =>
            _openFalconDownloadCommand ??= new RelayCommand(
                () => OpenFalconDownloadRequested?.Invoke(this, EventArgs.Empty));

        public event EventHandler PickFalconLibraryRequested;
        public event EventHandler InstallFalconDriverRequested;
        public event EventHandler OpenFalconDownloadRequested;

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
        /// Command for "check imported profiles for updates" (#9 Phase D):
        /// MainWindow walks the SteamWorkshopSource-stamped profiles and
        /// compares each stored time_updated against a fresh Steam read.
        /// </summary>
        public RelayCommand CheckWorkshopUpdatesCommand =>
            _checkWorkshopUpdatesCommand ??= new RelayCommand(
                () => CheckWorkshopUpdatesRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand<string> _getProfilesCommand;

        /// <summary>Opens Get Profiles (#574) on a tab: "Starters" (also for
        /// anything it does not know), "Community" or "File". The page's button
        /// opens Starters and its drop tile Import a File. Always enabled: the
        /// starters ship in the box, and with the community opt-in off the
        /// Community tab opens on its cold state and offers the enable action
        /// itself.</summary>
        public RelayCommand<string> GetProfilesCommand =>
            _getProfilesCommand ??= new RelayCommand<string>(tab =>
                GetProfilesRequested?.Invoke(this,
                    Enum.TryParse(tab, out GetProfilesTab t) && Enum.IsDefined(t) ? t : GetProfilesTab.Starters));

        /// <summary>Raised when Get Profiles is asked for, with its tab.</summary>
        public event EventHandler<GetProfilesTab> GetProfilesRequested;

        /// <summary>Raised when the user asks to purge the Workshop cache.</summary>
        public event EventHandler ClearWorkshopCacheRequested;

        /// <summary>Raised when the user asks to re-check imported profiles (Phase D).</summary>
        public event EventHandler CheckWorkshopUpdatesRequested;

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

        /// <summary>Whether auto-profile switching is enabled. Each card's
        /// game line says Loads for while it is on and Set for while it is
        /// off (#574).</summary>
        public bool EnableAutoProfileSwitching
        {
            get => _enableAutoProfileSwitching;
            set
            {
                if (!SetProperty(ref _enableAutoProfileSwitching, value)) return;
                foreach (var item in ProfileItems)
                    item.SwitchesByGame = value;
            }
        }

        private bool _enableExternalControl;

        /// <summary>Whether the external-control named pipe (#366) is served,
        /// so a launcher or script can activate and deactivate profiles from
        /// outside PadForge. InputService watches this to start and stop the
        /// pipe live.</summary>
        public bool EnableExternalControl
        {
            get => _enableExternalControl;
            set => SetProperty(ref _enableExternalControl, value);
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
            set { if (SetProperty(ref _isForegroundMatched, value)) RaiseForegroundText(); }
        }

        private string _foregroundProfileName = string.Empty;

        /// <summary>The name of the profile the program in front loads, empty
        /// when none (#574). Runtime only, fed at 1 Hz with
        /// ForegroundExeName.</summary>
        public string ForegroundProfileName
        {
            get => _foregroundProfileName;
            set { if (SetProperty(ref _foregroundProfileName, value ?? string.Empty)) RaiseForegroundText(); }
        }

        /// <summary>The In front now line around the program's name: what the
        /// language puts before it and after it. A match comes first, then no
        /// profile with a game, then neither.</summary>
        public string ForegroundLead { get; private set; } = string.Empty;

        /// <summary>What the In front now line puts after the program's name.</summary>
        public string ForegroundTail { get; private set; } = string.Empty;

        private void RaiseForegroundText()
        {
            var s = Strings.Instance;
            string format = IsForegroundMatched ? s.Profiles_InFront_Match_Format
                : NoProfileHasExecutables ? s.Profiles_InFront_NoGames_Format
                : s.Profiles_InFront_None_Format;
            (ForegroundLead, ForegroundTail) = SplitAroundPlace(format, ForegroundProfileName);
            OnPropertyChanged(nameof(ForegroundLead));
            OnPropertyChanged(nameof(ForegroundTail));
        }

        /// <summary>A sentence in two parts around its {0}, the program's or
        /// the games' place, with {1} filled by <paramref name="name"/> in
        /// both, so the page sets the {0} value apart in a run of its own and
        /// a language may put {1} before {0}. The split comes first, so a name
        /// that holds "{0}" cannot move it. A format with no {0} is all
        /// lead.</summary>
        internal static (string Lead, string Tail) SplitAroundPlace(string format, string name)
        {
            format ??= string.Empty;
            name ??= string.Empty;
            int at = format.IndexOf("{0}", StringComparison.Ordinal);
            string lead = at < 0 ? format : format.Substring(0, at);
            string tail = at < 0 ? string.Empty : format.Substring(at + 3);
            return (lead.Replace("{1}", name, StringComparison.Ordinal), tail.Replace("{1}", name, StringComparison.Ordinal));
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

        /// <summary>The profile the last card command acted on (#574). A card's
        /// command sets it before raising its event, and MainWindow's handlers
        /// read it.</summary>
        public ProfileListItem SelectedProfile
        {
            get => _selectedProfile;
            set => SetProperty(ref _selectedProfile, value);
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
        /// Default profile is active. The card commands whose answer turns on
        /// which profile is active ask again.
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
            _loadProfileCommand?.NotifyCanExecuteChanged();
            _saveAsProfileCommand?.NotifyCanExecuteChanged();
            _saveSetupHereCommand?.NotifyCanExecuteChanged();
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
                item.SwitchesByGame = EnableAutoProfileSwitching;
                item.PropertyChanged += ProfileItem_PropertyChanged;
                _hookedProfileItems.Add(item);
            }
            OnPropertyChanged(nameof(NoProfileHasExecutables));
            RaiseForegroundText();
        }

        /// <summary>Edit mutates rows in place (Executables raises
        /// HasExecutables), so per-row changes feed the aggregate too.</summary>
        private void ProfileItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProfileListItem.HasExecutables)) return;
            OnPropertyChanged(nameof(NoProfileHasExecutables));
            RaiseForegroundText();
        }

        /// <summary>True while no profile carries an executable match rule
        /// (#175 item 8): with auto-switching on the monitor can never fire,
        /// so the In front now line says no profile has a game set. Derived,
        /// and notified from OnProfileItemsChanged and
        /// ProfileItem_PropertyChanged.</summary>
        public bool NoProfileHasExecutables => ProfileItems.All(i => !i.HasExecutables);

        /// <summary>Raised when the user requests reverting to the default profile.</summary>
        public event EventHandler RevertToDefaultRequested;

        /// <summary>Raised when the user requests creating a new empty profile.</summary>
        public event EventHandler NewProfileRequested;

        /// <summary>Raised when the user requests saving current settings as a new profile.</summary>
        public event EventHandler SaveAsProfileRequested;

        /// <summary>Raised when the user asks for the current setup to replace
        /// what a stored profile holds (#574, Save Current Setup Here).</summary>
        public event EventHandler SaveSetupHereRequested;

        /// <summary>Raised when the user requests deleting the selected profile.</summary>
        public event EventHandler DeleteProfileRequested;

        /// <summary>Raised when the user requests editing the selected profile's metadata.</summary>
        public event EventHandler EditProfileRequested;

        /// <summary>Raised when the user requests loading the selected profile into the editor.</summary>
        public event EventHandler LoadProfileRequested;

        /// <summary>Raised when the user requests exporting the selected
        /// profile to a shareable .pfprofile file (issue #83 follow-up).</summary>
        public event EventHandler ExportProfileRequested;

        /// <summary>A card's command acts on its own profile (#574). The
        /// profile becomes SelectedProfile, which MainWindow's handlers read,
        /// before the event.</summary>
        private bool Acts(ProfileListItem p)
        {
            if (p == null) return false;
            SelectedProfile = p;
            return true;
        }

        private RelayCommand _newProfileCommand;

        /// <summary>Command to create a new empty profile.</summary>
        public RelayCommand NewProfileCommand =>
            _newProfileCommand ??= new RelayCommand(
                () => NewProfileRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand<ProfileListItem> _loadProfileCommand;

        /// <summary>Loads a card's profile. Default loads through the revert
        /// path, as it always has. The active profile has nothing to load.</summary>
        public RelayCommand<ProfileListItem> LoadProfileCommand =>
            _loadProfileCommand ??= new RelayCommand<ProfileListItem>(p =>
            {
                if (!Acts(p)) return;
                if (p.IsDefault) RevertToDefaultRequested?.Invoke(this, EventArgs.Empty);
                else LoadProfileRequested?.Invoke(this, EventArgs.Empty);
            }, p => p != null && !p.IsActive);

        private RelayCommand<ProfileListItem> _saveAsProfileCommand;

        /// <summary>Saves the live setup as a new profile, from the active
        /// card's menu: the active profile is what the live setup holds.</summary>
        public RelayCommand<ProfileListItem> SaveAsProfileCommand =>
            _saveAsProfileCommand ??= new RelayCommand<ProfileListItem>(
                p => { if (Acts(p)) SaveAsProfileRequested?.Invoke(this, EventArgs.Empty); },
                p => p?.IsActive == true);

        private RelayCommand<ProfileListItem> _saveSetupHereCommand;

        /// <summary>Puts the live setup into another stored profile. Default is
        /// never overwritten, and the active profile is the live setup already.</summary>
        public RelayCommand<ProfileListItem> SaveSetupHereCommand =>
            _saveSetupHereCommand ??= new RelayCommand<ProfileListItem>(
                p => { if (Acts(p)) SaveSetupHereRequested?.Invoke(this, EventArgs.Empty); },
                p => p != null && !p.IsDefault && !p.IsActive);

        private RelayCommand<ProfileListItem> _editProfileCommand;

        /// <summary>Edits a card's name, games and polling override. Default
        /// has none of its own to edit.</summary>
        public RelayCommand<ProfileListItem> EditProfileCommand =>
            _editProfileCommand ??= new RelayCommand<ProfileListItem>(
                p => { if (Acts(p)) EditProfileRequested?.Invoke(this, EventArgs.Empty); },
                p => p != null && !p.IsDefault);

        private RelayCommand<ProfileListItem> _exportProfileCommand;

        /// <summary>Exports a card's profile as a .pfprofile. The Default
        /// card exports its own stored state.</summary>
        public RelayCommand<ProfileListItem> ExportProfileCommand =>
            _exportProfileCommand ??= new RelayCommand<ProfileListItem>(
                p => { if (Acts(p)) ExportProfileRequested?.Invoke(this, EventArgs.Empty); },
                p => p != null);

        private RelayCommand<ProfileListItem> _deleteProfileCommand;

        /// <summary>Deletes a card's profile. Default is never deleted.</summary>
        public RelayCommand<ProfileListItem> DeleteProfileCommand =>
            _deleteProfileCommand ??= new RelayCommand<ProfileListItem>(
                p => { if (Acts(p)) DeleteProfileRequested?.Invoke(this, EventArgs.Empty); },
                p => p != null && !p.IsDefault);

        private RelayCommand _resetShortcutsCommand;

        /// <summary>Reset All on the Shortcuts card (#574): each shortcut's
        /// action, profile and device go back to their defaults through the
        /// row's own resets, which save when a value changes. The rows and
        /// their recorded buttons stay.</summary>
        public RelayCommand ResetShortcutsCommand =>
            _resetShortcutsCommand ??= new RelayCommand(() =>
            {
                foreach (var s in ProfileShortcuts.ToList())
                {
                    s.ResetSettingCommand.Execute(nameof(ProfileShortcutViewModel.SwitchMode));
                    s.ResetSettingCommand.Execute(nameof(ProfileShortcutViewModel.TargetProfileId));
                    s.ResetSettingCommand.Execute(nameof(ProfileShortcutViewModel.TriggerDeviceGuid));
                }
            });
    }

    /// <summary>The Settings page's groups (#574), in their order.</summary>
    public enum SettingsGroup { General, Input, Hardware, Drivers, Data }

    /// <summary>The tabs of Get Profiles (#574), in their order.</summary>
    public enum GetProfilesTab { Starters, Community, File }

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
            set
            {
                if (!SetProperty(ref _id, value)) return;
                OnPropertyChanged(nameof(IsDefault));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(ShowsSaveHere));
            }
        }

        private bool _isActive;

        /// <summary>Whether this profile is the currently active one: its
        /// card shows Active with the flame and an ember edge (#574).</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (!SetProperty(ref _isActive, value)) return;
                OnPropertyChanged(nameof(ShowsLoad));
                OnPropertyChanged(nameof(ShowsSaveAs));
                OnPropertyChanged(nameof(ShowsSaveHere));
            }
        }

        /// <summary>The card's menu offers Edit and Delete: Default has no
        /// name or games of its own and is never deleted.</summary>
        public bool CanEdit => !IsDefault;

        /// <summary>The card shows Load where the active card shows Active.</summary>
        public bool ShowsLoad => !IsActive;

        /// <summary>The active card's menu offers Save As.</summary>
        public bool ShowsSaveAs => IsActive;

        /// <summary>Save Current Setup Here, on every card but Default's and
        /// the active one's.</summary>
        public bool ShowsSaveHere => !IsActive && !IsDefault;

        private string _name;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>The entry's screen-reader name, the profile's name:
        /// ListBoxItem and ItemAutomationPeer read an item that is not text
        /// through its ToString().</summary>
        public override string ToString() => Name ?? string.Empty;

        private string _executables;

        /// <summary>The profile's games as file names, "a.exe, b.exe": the
        /// card's game line's tooltip.</summary>
        public string Executables
        {
            get => _executables;
            set
            {
                if (SetProperty(ref _executables, value))
                    OnPropertyChanged(nameof(HasExecutables));
            }
        }

        private string _executablePaths;

        /// <summary>Pipe-separated full executable paths as stored on the
        /// profile (the ForegroundMonitorService match source). Executables
        /// above is the display form (file names only). Feeds the card's
        /// game line (#574).</summary>
        public string ExecutablePaths
        {
            get => _executablePaths;
            set
            {
                if (SetProperty(ref _executablePaths, value))
                    RaiseGameLine();
            }
        }

        /// <summary>Whether the profile carries any executable match rule:
        /// its card says which games load it, else No game set.</summary>
        public bool HasExecutables => !string.IsNullOrEmpty(_executables);

        private bool _switchesByGame;

        /// <summary>Set by the owner from Switch Profiles by Game. The card says
        /// Loads for while it is on and Set for while it is off, since the
        /// game then loads nothing by itself.</summary>
        public bool SwitchesByGame
        {
            get => _switchesByGame;
            set { if (SetProperty(ref _switchesByGame, value)) RaiseGameLine(); }
        }

        /// <summary>The first two games' file names, "a.exe, b.exe".</summary>
        public string GameNames { get; private set; } = string.Empty;

        /// <summary>"+N" for the games past two, empty otherwise. Drawn in a cell
        /// of its own so a long name never cuts it off (maintainer request
        /// 2026-07-05).</summary>
        public string GameExtra { get; private set; } = string.Empty;

        /// <summary>What Loads for or Set for puts before the names.</summary>
        public string GameLead { get; private set; } = string.Empty;

        /// <summary>What Loads for or Set for puts after the names.</summary>
        public string GameTail { get; private set; } = string.Empty;

        /// <summary>One chip per controller type, in the order of the profile's
        /// slots, each with how many slots are of that type.</summary>
        public IReadOnlyList<ProfileTypeChip> TypeChips { get; private set; } = Array.Empty<ProfileTypeChip>();

        /// <summary>The profile has no controllers: its card shows the No
        /// Controllers chip. An item never given a topology has none, as
        /// Reset to Defaults adds Default.</summary>
        public bool HasNoSlots => TypeChips.Count == 0;

        private bool[] _slotCreated;
        private int[] _slotTypes;

        /// <summary>The profile's slots, which the type chips count.</summary>
        internal void SetTopology(bool[] slotCreated, int[] slotControllerTypes)
        {
            _slotCreated = (bool[])slotCreated?.Clone();
            _slotTypes = (int[])slotControllerTypes?.Clone();
            TypeChips = ProfileTypeChip.Build(_slotCreated, _slotTypes);
            OnPropertyChanged(nameof(TypeChips));
            OnPropertyChanged(nameof(HasNoSlots));
        }

        /// <summary>The game line from the profile's games: two file names, the
        /// count of the rest, and the words Loads for or Set for put around
        /// them, split at the names' place.</summary>
        private void RaiseGameLine()
        {
            var names = string.IsNullOrEmpty(_executablePaths)
                ? Array.Empty<string>()
                : _executablePaths.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(System.IO.Path.GetFileName).ToArray();
            GameNames = string.Join(", ", names.Take(2));
            GameExtra = names.Length > 2
                ? "+" + (names.Length - 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            string format = SwitchesByGame ? Strings.Instance.Profiles_Card_LoadsFor_Format : Strings.Instance.Profiles_Card_SetFor_Format;
            (GameLead, GameTail) = SettingsViewModel.SplitAroundPlace(format, null);
            OnPropertyChanged(nameof(GameNames));
            OnPropertyChanged(nameof(GameExtra));
            OnPropertyChanged(nameof(GameLead));
            OnPropertyChanged(nameof(GameTail));
        }

        /// <summary>Re-raises this item's culture-dependent text: the game
        /// line's words and the chips' type names. A language switch changes
        /// them with nothing else to announce it. The owning ViewModel calls
        /// this for every item from OnCultureChanged.</summary>
        internal void RaiseCultureDependentText()
        {
            TypeChips = ProfileTypeChip.Build(_slotCreated, _slotTypes);
            OnPropertyChanged(nameof(TypeChips));
            OnPropertyChanged(nameof(HasNoSlots));
            RaiseGameLine();
        }
    }

    /// <summary>A controller type on a profile's card, with how many of the
    /// profile's slots are of it (#574). The icon is the type's own symbol
    /// from SlotTypeIconMap, as Home's cards and the sidebar's pills draw
    /// it.</summary>
    public sealed class ProfileTypeChip
    {
        internal ProfileTypeChip(Engine.VirtualControllerType type, int count)
        {
            Type = type;
            Count = count;
            TypeName = NameOf(type);
            SpokenName = string.Format(Strings.Instance.Profiles_TypeCount_Format, TypeName, count);
        }

        public Engine.VirtualControllerType Type { get; }

        public int Count { get; }

        /// <summary>The type's vector symbol, or null for the types drawn with a glyph.</summary>
        public System.Windows.Media.Geometry TypeGeometry => SlotTypeIconMap.GeometryFor(Type);

        /// <summary>The type's glyph, or empty for the types drawn with a vector symbol.</summary>
        public string TypeGlyph => SlotTypeIconMap.GlyphFor(Type);

        /// <summary>The type's name, the chip's tooltip.</summary>
        public string TypeName { get; }

        /// <summary>What a screen reader reads for the chip: the type and its count.</summary>
        public string SpokenName { get; }

        /// <summary>The chips of a topology: created slots only, each type at its
        /// first slot. A created slot with no type entry, or a value outside the
        /// enum, counts as Xbox, as the old counter did. No slots, or no array,
        /// gives no chips.</summary>
        internal static IReadOnlyList<ProfileTypeChip> Build(bool[] slotCreated, int[] slotControllerTypes)
        {
            if (slotCreated == null) return Array.Empty<ProfileTypeChip>();
            var order = new List<Engine.VirtualControllerType>();
            var counts = new Dictionary<Engine.VirtualControllerType, int>();
            for (int i = 0; i < slotCreated.Length; i++)
            {
                if (!slotCreated[i]) continue;
                int raw = slotControllerTypes != null && i < slotControllerTypes.Length ? slotControllerTypes[i] : 0;
                var type = Enum.IsDefined(typeof(Engine.VirtualControllerType), raw)
                    ? (Engine.VirtualControllerType)raw
                    : Engine.VirtualControllerType.Xbox;
                if (counts.TryGetValue(type, out int n)) counts[type] = n + 1;
                else
                {
                    counts[type] = 1;
                    order.Add(type);
                }
            }
            return order.Select(t => new ProfileTypeChip(t, counts[t])).ToArray();
        }

        private static string NameOf(Engine.VirtualControllerType type) => type switch
        {
            Engine.VirtualControllerType.PlayStation => Strings.Instance.ControllerType_PlayStation,
            Engine.VirtualControllerType.Extended => Strings.Instance.ControllerType_Extended,
            Engine.VirtualControllerType.Midi => Strings.Instance.ControllerType_MIDI,
            Engine.VirtualControllerType.KeyboardMouse => Strings.Instance.ControllerType_KeyboardMouse,
            Engine.VirtualControllerType.Nintendo => Strings.Instance.ControllerType_Nintendo,
            Engine.VirtualControllerType.Vr => Strings.Instance.ControllerType_VR,
            _ => Strings.Instance.ControllerType_Xbox,
        };
    }
}
