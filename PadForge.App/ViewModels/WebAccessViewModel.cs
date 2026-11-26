using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;

namespace PadForge.ViewModels
{
    /// <summary>One tunnel in the Tunnel list.</summary>
    public sealed record WebTunnelChoice(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>One of this PC's addresses on the network door's Address
    /// row (#600 item 53), with its own Copy.</summary>
    public sealed class NetworkAddressItem
    {
        internal NetworkAddressItem(string address, bool first, Action<string> copy)
        {
            Address = address;
            IsFirst = first;
            CopyCommand = new RelayCommand(() => copy?.Invoke(address));
        }

        public string Address { get; }

        /// <summary>Whether the row's label stands beside it: the first
        /// address alone carries it.</summary>
        public bool IsFirst { get; }

        public string CopyId => "Net_CopyAddress_" + Address;

        public RelayCommand CopyCommand { get; }
    }

    /// <summary>One key on the Assistants on This Network row (#600 item
    /// 53), with its reach (#600 DESIGN-2 N3), Pick Devices… while it is
    /// held to its own devices, and Revoke. The row is kept while its key
    /// is, and shows the store's change (<see cref="Show"/>), so a reach box
    /// the user works in stays where it is.</summary>
    public sealed class NetworkKeyItem : ObservableObject
    {
        private ProgramReach _reach;
        private readonly Action<string, ProgramReach> _setReach;
        private string _name, _line;
        private bool _first;
        private ReachWords.Language _language;

        internal NetworkKeyItem(string id, string name, string line, bool first, Action<string> revoke,
            ProgramReach reach = null, Action<string, ProgramReach> setReach = null, Func<string, string, ProgramReach, ProgramReach> pick = null)
        {
            Id = id;
            _setReach = setReach;
            Show(name, line, first, reach);
            RevokeCommand = new RelayCommand(() => revoke?.Invoke(id));
            PickCommand = new RelayCommand(() =>
            {
                var picked = pick?.Invoke(WebDoor.NetKey(id), Name, _reach ?? ProgramReach.OwnDevices);
                if (picked != null) _setReach?.Invoke(id, picked);
            });
        }

        /// <summary>Shows the key as the store holds it now.</summary>
        internal void Show(string name, string line, bool first, ProgramReach reach)
        {
            bool wasLimited = _reach != null;
            _reach = reach;
            SetProperty(ref _name, name, nameof(Name));
            SetProperty(ref _line, reach == null ? line : line + " " + ReachWords.Line(reach), nameof(Line));
            SetProperty(ref _first, first, nameof(IsFirst));
            bool newWords = _language.Follow();
            if (newWords) OnPropertyChanged(nameof(ReachChoices));
            if (newWords || wasLimited != (reach != null))
            {
                OnPropertyChanged(nameof(SelectedReach));
                OnPropertyChanged(nameof(IsLimited));
            }
        }

        public IReadOnlyList<string> ReachChoices => ReachWords.Choices;

        /// <summary>0 for All of PadForge, 1 for Only Its Own Devices and
        /// These.</summary>
        public int SelectedReach
        {
            get => _reach == null ? 0 : 1;
            set
            {
                if (value is not (0 or 1) || value == SelectedReach) return;
                _setReach?.Invoke(Id, value == 0 ? null : _reach ?? ProgramReach.OwnDevices);
            }
        }

        public bool IsLimited => _reach != null;

        public RelayCommand PickCommand { get; }

        public string ReachId => "Net_Reach_" + Id;

        public string PickId => "Net_Pick_" + Id;

        public string Id { get; }

        /// <summary>The name the user gave its key.</summary>
        public string Name => _name;

        public string Line => _line;

        /// <summary>Whether the row's label stands beside it.</summary>
        public bool IsFirst => _first;

        public string RevokeId => "Net_Revoke_" + Id;

        public RelayCommand RevokeCommand { get; }
    }

    /// <summary>One allowed web assistant on the Assistants on the Web card.
    /// The row is kept while its grant is, and shows the store's change
    /// (<see cref="Show"/>), so a reach box the user works in stays where
    /// it is.</summary>
    public sealed class WebAllowedItem : ObservableObject
    {
        private ProgramReach _reach;
        private readonly Action<string, ProgramReach> _setReach;
        private string _origin, _line;
        private ReachWords.Language _language;

        internal WebAllowedItem(string id, string origin, string line, Action<string> revoke,
            ProgramReach reach = null, Action<string, ProgramReach> setReach = null, Func<string, string, ProgramReach, ProgramReach> pick = null)
        {
            Id = id;
            _setReach = setReach;
            Show(origin, line, reach);
            RevokeCommand = new RelayCommand(() => revoke?.Invoke(id));
            PickCommand = new RelayCommand(() =>
            {
                var picked = pick?.Invoke(WebDoor.WebKey(id), Origin, _reach ?? ProgramReach.OwnDevices);
                if (picked != null) _setReach?.Invoke(id, picked);
            });
        }

        /// <summary>Shows the grant as the store holds it now. Its reach
        /// (#600 DESIGN-2 N3) is said as a network key's row says it.</summary>
        internal void Show(string origin, string line, ProgramReach reach)
        {
            bool wasLimited = _reach != null;
            _reach = reach;
            SetProperty(ref _origin, origin, nameof(Origin));
            SetProperty(ref _line, reach == null ? line : line + " " + ReachWords.Line(reach), nameof(Line));
            bool newWords = _language.Follow();
            if (newWords) OnPropertyChanged(nameof(ReachChoices));
            if (newWords || wasLimited != (reach != null))
            {
                OnPropertyChanged(nameof(SelectedReach));
                OnPropertyChanged(nameof(IsLimited));
            }
        }

        public IReadOnlyList<string> ReachChoices => ReachWords.Choices;

        /// <summary>0 for All of PadForge, 1 for Only Its Own Devices and
        /// These. Picking the second keeps the devices picked before.</summary>
        public int SelectedReach
        {
            get => _reach == null ? 0 : 1;
            set
            {
                if (value is not (0 or 1) || value == SelectedReach) return;
                _setReach?.Invoke(Id, value == 0 ? null : _reach ?? ProgramReach.OwnDevices);
            }
        }

        /// <summary>Whether Pick Devices… shows.</summary>
        public bool IsLimited => _reach != null;

        public RelayCommand PickCommand { get; }

        public string ReachId => "Web_Reach_" + Id;

        public string PickId => "Web_Pick_" + Id;

        public string Id { get; }

        /// <summary>The host its sign-in returned to: what it is named by.</summary>
        public string Origin => _origin;

        public string Line => _line;

        public string RevokeId => "Web_Revoke_" + Id;

        public RelayCommand RevokeCommand { get; }
    }

    /// <summary>
    /// Reach PadForge From the Web and the Assistants on the Web card (#600
    /// item 11), over the app's web door. Every control here is the user's
    /// alone: it sits on the Automation page.
    /// </summary>
    public sealed class WebAccessViewModel : ObservableObject
    {
        /// <summary>Claude's documented link that opens its add dialog with
        /// the address filled in (claude.com/docs/connectors/building
        /// directory-vs-custom: "The user reviews the values and confirms
        /// before anything is added").</summary>
        internal const string ClaudeAddLink = "https://claude.ai/customize/connectors?modal=add-custom-connector&connectorName=PadForge&connectorUrl=";

        private readonly WebDoor _door;
        private readonly WebAccessStore _store;
        private readonly Func<DateTimeOffset> _now;
        private bool _refreshing;

        /// <summary>Asks the user before the switch turns on. The window
        /// shows its confirmation. A test gives its own answer.</summary>
        internal Func<bool> AskToTurnOn { get; set; }

        /// <summary>Asks the user before the network switch turns on (#600
        /// item 53). A test gives its own answer.</summary>
        internal Func<bool> AskToTurnNetworkOn { get; set; }

        /// <summary>Asks for a new key's name and level, up to the user's
        /// level, or null when the user canceled. The window shows its
        /// dialog.</summary>
        internal Func<AutomationLevel, (string Name, AutomationLevel Level)?> AskNewKey { get; set; }

        /// <summary>Shows a new key once, with its name, its text and the
        /// address to give the assistant. The window shows its dialog.</summary>
        internal Action<string, string, string> ShowKey { get; set; }

        /// <summary>Asks for another port, given the one kept, or null when
        /// the user canceled.</summary>
        internal Func<int, int?> AskPort { get; set; }

        /// <summary>Pick Devices… on a key's row and an allowed web
        /// assistant's (#600 DESIGN-2 N3): the consent key its connections
        /// carry, its name and its reach in, the reach picked out.</summary>
        internal Func<string, string, ProgramReach, ProgramReach> PickReachDevices { get; set; }

        /// <summary>The user's level now, which caps a new key's.</summary>
        internal Func<AutomationLevel> UserLevel { get; set; } = () => AutomationLevel.ChangeSettings;

        /// <summary>Puts text on the clipboard. A test gives its own.</summary>
        internal Action<string> ClipboardSeam { get; set; }

        /// <summary>Opens a link with the user's rights. A test gives its own.</summary>
        internal Func<string, bool> OpenLinkSeam { get; set; }

        /// <summary>Runs an action on the interface's thread.</summary>
        internal Action<Action> Post { get; set; } = a => a();

        internal WebAccessViewModel(WebDoor door, WebAccessStore store, Func<DateTimeOffset> now = null)
        {
            _door = door;
            _store = store;
            _now = now ?? (() => DateTimeOffset.Now);
            ArmCommand = new RelayCommand(() => { if (_door.Armed) _door.Disarm(); else _door.Arm(); });
            CopyAddressCommand = new RelayCommand(() => Copy(Address));
            AddToClaudeCommand = new RelayCommand(OpenClaude);
            MakeKeyCommand = new RelayCommand(() => Open(TunnelClient.KeysPage));
            MakeTunnelCommand = new RelayCommand(() => Open(TunnelClient.TunnelsPage));
            OpenChatGptCommand = new RelayCommand(() => Open(TunnelClient.ChatGptPage));
            ChangePortCommand = new RelayCommand(ChangePort);
            AddKeyCommand = new RelayCommand(AddKey);
            _door.Changed += (_, _) => Post(Refresh);
            // A file read in, as at a Reload on Settings, gives the paste box
            // the address it holds.
            _store.Loaded += (_, _) => { _pastedStale = true; Post(Refresh); };
            Refresh();
        }

        public RelayCommand ArmCommand { get; }

        private RelayCommand<string> _resetSettingCommand;

        /// <summary>The switch's reset: back to its default, off, which asks
        /// nothing.</summary>
        public RelayCommand<string> ResetSettingCommand => _resetSettingCommand ??= new RelayCommand<string>(name =>
        {
            if (name == nameof(IsOn)) IsOn = false;
            if (name == nameof(IsNetworkOn)) IsNetworkOn = false;
        }, CanResetSetting);

        internal static bool CanResetSetting(string name) => name is nameof(IsOn) or nameof(IsNetworkOn);

        /// <summary>The switch, the card's controls and each allowed web
        /// assistant as catalog entries (#600 items 1 and 2), each the user's
        /// alone: Search and a screen reader find them, and no program sets or
        /// presses one.</summary>
        internal IReadOnlyList<PadForge.Services.Catalog.LiveItem> CatalogItems()
        {
            var s = Strings.Instance;
            string where = s.Extras_Automation_Title + ", " + s.Extras_Automation_Card_Assistants;
            string card = s.Extras_Automation_Title + ", " + s.Web_Header;
            const PadForge.Common.ReachClass Own = PadForge.Common.ReachClass.UsersOwn;
            var items = new List<PadForge.Services.Catalog.LiveItem>
            {
                new("web/reach", PadForge.Services.Catalog.LiveKind.Setting, s.Web_Reach_Label, Own, PadForge.Common.ReachReason.Network)
                { Read = () => IsOn ? s.Common_On : s.Common_Off, Where = where },
                // Reach PadForge From This Network (#600 item 53), its port,
                // Add a Network Assistant… and each key's Revoke: the user's
                // alone, so no program makes itself a key.
                new("net/reach", PadForge.Services.Catalog.LiveKind.Setting, s.Net_Reach_Label, Own, PadForge.Common.ReachReason.Network)
                { Read = () => IsNetworkOn ? s.Common_On : s.Common_Off, Where = where },
            };
            if (IsNetworkOn)
            {
                items.Add(new("net/port", PadForge.Services.Catalog.LiveKind.Action, s.Net_Port_Label + ": " + s.Net_Port_Change, Own)
                { Read = () => NetworkPortText, Where = where });
                items.Add(new("net/add", PadForge.Services.Catalog.LiveKind.Action, s.Net_Add, Own) { Where = where });
                foreach (var key in NetworkKeys)
                    items.Add(new("net/key/" + key.Id, PadForge.Services.Catalog.LiveKind.Action, key.Name + ": " + s.Web_Allowed_Revoke, Own)
                    { Read = () => key.Line, Where = where });
            }
            if (!IsOn) return items;
            items.Add(new("web/tunnel", PadForge.Services.Catalog.LiveKind.Setting, s.Web_Tunnel_Label, Own) { Read = () => SelectedTunnel?.Name, Where = card });
            items.Add(new("web/address", PadForge.Services.Catalog.LiveKind.Setting, s.Web_Address_Label, Own) { Read = () => Address, Where = card });
            items.Add(new("web/accept", PadForge.Services.Catalog.LiveKind.Action, s.Web_Accept_Label, Own) { Read = () => AcceptLine, Where = card });
            if (IsChatGpt)
            {
                // The runtime key is never read, by a program or anyone.
                items.Add(new("web/runtime-key", PadForge.Services.Catalog.LiveKind.Setting, s.Web_ChatGpt_KeyLabel, Own) { Read = () => null, Where = card });
                items.Add(new("web/tunnel-id", PadForge.Services.Catalog.LiveKind.Setting, s.Web_ChatGpt_IdLabel, Own) { Read = () => TunnelIdText, Where = card });
                items.Add(new("web/open-chatgpt", PadForge.Services.Catalog.LiveKind.Action, s.Web_ChatGpt_Open, Own) { Where = card });
            }
            foreach (var row in Allowed)
                items.Add(new("web/allowed/" + row.Id, PadForge.Services.Catalog.LiveKind.Action, row.Origin + ": " + s.Web_Allowed_Revoke, Own)
                { Read = () => row.Line, Where = card });
            return items;
        }
        public RelayCommand CopyAddressCommand { get; }
        public RelayCommand AddToClaudeCommand { get; }
        public RelayCommand MakeKeyCommand { get; }
        public RelayCommand MakeTunnelCommand { get; }
        public RelayCommand OpenChatGptCommand { get; }

        // ── The ChatGPT Secure MCP Tunnel (#600 DESIGN-2 N4) ──

        /// <summary>Whether the ChatGPT tunnel is picked: Runtime Key and
        /// Tunnel ID take the Address row's place.</summary>
        public bool IsChatGpt => _store.Tunnel == TunnelClient.Key;

        /// <summary>Whether the Address, Accept and Allowed rows show.</summary>
        public bool ShowsAddress => !IsChatGpt;

        /// <summary>Whether a runtime key is kept, so its box shows it
        /// masked.</summary>
        public bool HasRuntimeKey => _store.RuntimeKeyBlob != null;

        /// <summary>Whether the door accepts a new web assistant now.</summary>
        public bool IsArmed => _door.Armed;

        /// <summary>Why the key pasted was not kept, or the line under the
        /// box.</summary>
        public string RuntimeKeyLine => _door.RuntimeKeyWords ?? Strings.Instance.Web_ChatGpt_KeyHint;

        /// <summary>The Runtime Key box was left with a new key, or emptied.
        /// The key goes to the helper once and is never kept here.</summary>
        internal void SetRuntimeKey(string key)
        {
            _door.SetRuntimeKey(key);
            Refresh();
        }

        /// <summary>The Tunnel ID last said to the box. Refresh says it again
        /// only when the store's differs: the box sends its text on
        /// LostFocus, and each change notice puts the store's text over what
        /// the user is typing.</summary>
        private string _tunnelIdShown;

        public string TunnelIdText
        {
            get => _store.TunnelId;
            set
            {
                if (_refreshing || value == _store.TunnelId) return;
                _door.SetTunnelId(value);
                _tunnelIdShown = _store.TunnelId;
                OnPropertyChanged();
            }
        }

        /// <summary>The card's last line.</summary>
        public string Footnote => IsChatGpt ? Strings.Instance.Web_ChatGpt_Footnote : Strings.Instance.Web_Footnote;

        private void Open(string link)
        {
            if (OpenLinkSeam != null) { OpenLinkSeam(link); return; }
            ShellLaunch.AsTheUser(new ProcessStartInfo(link) { UseShellExecute = true }, out _);
        }
        public RelayCommand ChangePortCommand { get; }
        public RelayCommand AddKeyCommand { get; }

        // ── Reach PadForge From This Network (#600 item 53) ──

        public bool IsNetworkOn
        {
            get => _store.NetworkOn;
            set
            {
                if (value == _store.NetworkOn) return;
                if (value)
                {
                    // Turning it on asks: it listens on this PC's network.
                    if (!(AskToTurnNetworkOn?.Invoke() ?? false))
                    {
                        OnPropertyChanged();
                        return;
                    }
                    _door.TurnNetworkOn();
                }
                else _door.TurnNetworkOff();
                Refresh();
            }
        }

        /// <summary>The address an assistant is given, one per private
        /// address of this PC.</summary>
        public ObservableCollection<NetworkAddressItem> NetworkAddresses { get; } = new();

        public bool HasNetworkAddress => NetworkAddresses.Count > 0;

        /// <summary>Whether the Address row says this PC has no private
        /// address.</summary>
        public bool ShowsNoAddress => !HasNetworkAddress && _store.NetworkPort != 0;

        private string _networkPortText;
        public string NetworkPortText { get => _networkPortText; private set => SetProperty(ref _networkPortText, value); }

        private string _networkLine;
        /// <summary>The line under the Port row: the note on what crosses the
        /// network, or why the door is not open.</summary>
        public string NetworkLine { get => _networkLine; private set => SetProperty(ref _networkLine, value); }

        private string _networkNote;
        /// <summary>What a Copy on the network rows did.</summary>
        public string NetworkNote { get => _networkNote; private set { if (SetProperty(ref _networkNote, value)) OnPropertyChanged(nameof(HasNetworkNote)); } }
        public bool HasNetworkNote => !string.IsNullOrEmpty(_networkNote);

        public ObservableCollection<NetworkKeyItem> NetworkKeys { get; } = new();

        public bool HasNetworkKeys => NetworkKeys.Count > 0;

        /// <summary>The key a window shows now, so a scrub knows it: never
        /// written anywhere (#600 item 53).</summary>
        internal static volatile string ShownKey;

        /// <summary>The address to put in a new key's lines: this PC's first
        /// private address, else its computer name.</summary>
        internal string KeyAddress()
        {
            string host = NetworkDoor.PrivateAddresses().FirstOrDefault() ?? Environment.MachineName;
            return NetworkDoor.Address(host, _store.NetworkPort);
        }

        private void AddKey()
        {
            if (!_store.NetworkOn) return;
            var asked = AskNewKey?.Invoke(UserLevel());
            if (asked == null) return;
            string key = _door.AddNetworkKey(asked.Value.Name, asked.Value.Level);
            Refresh();
            if (key == null) return;
            try
            {
                ShownKey = key;
                ShowKey?.Invoke(asked.Value.Name, key, KeyAddress());
            }
            finally
            {
                ShownKey = null;
            }
        }

        private void ChangePort()
        {
            var port = AskPort?.Invoke(_store.NetworkPort);
            if (port is int p && _door.ChangeNetworkPort(p)) Refresh();
        }

        private void CopyNetwork(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (ClipboardSeam != null) ClipboardSeam(text);
                else ShellLaunch.SetClipboardText(text);
                NetworkNote = Strings.Instance.Web_Address_Copied;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                NetworkNote = Strings.Instance.Extras_Automation_NotCopied;
            }
        }

        /// <summary>The line under the Port row, from the helper's state and
        /// the listener's.</summary>
        private string NetworkLineNow(Strings s, CultureInfo culture)
        {
            string port = _store.NetworkPort.ToString(CultureInfo.InvariantCulture);
            if (_door.HelperState == WebHelperState.WaitsForAutomation) return s.Net_Helper_AutomationOff;
            if (_door.HelperState == WebHelperState.Stopped)
                return string.Format(culture, s.Net_Helper_Retrying_Format, _door.HelperWords ?? "?");
            var state = _door.NetworkState;
            if (state != null && state.Port == _store.NetworkPort)
            {
                switch (state.State)
                {
                    case "taken": return string.Format(culture, s.Net_Port_Taken_Format, port);
                    case "denied": return string.Format(culture, s.Net_Port_NoReservation_Format, port);
                    case "failed": return string.Format(culture, s.Net_Port_Failed_Format, port);
                }
            }
            return s.Net_Note_Rule;
        }

        /// <summary>A key's line: its level, when it was made and when it was
        /// last used.</summary>
        private string KeyLine(NetworkKey key, Strings s, CultureInfo culture)
        {
            string level = WebDoor.LevelName(key.Level);
            var made = key.Made.ToLocalTime();
            string madeWords = KeyWhen(made, culture, s);
            if (key.LastUsed is not { } used) return string.Format(culture, s.Net_Key_LineNew_Format, level, madeWords);
            var usedLocal = used.ToLocalTime();
            string usedWords = usedLocal.Date == made.Date && made.Date == _now().ToLocalTime().Date
                ? string.Format(culture, s.Net_When_Time_Format, usedLocal.ToString("t", culture))
                : KeyWhen(usedLocal, culture, s);
            return string.Format(culture, s.Net_Key_Line_Format, level, madeWords, usedWords);
        }

        private string KeyWhen(DateTimeOffset local, CultureInfo culture, Strings s)
            => local.Date == _now().ToLocalTime().Date
                ? string.Format(culture, s.Net_When_Today_Format, local.ToString("t", culture))
                : string.Format(culture, s.Net_When_Day_Format, local.ToString("d", culture), local.ToString("t", culture));

        private void RefreshNetwork(Strings s, CultureInfo culture)
        {
            OnPropertyChanged(nameof(IsNetworkOn));
            // Before the first pick ends the port is 0: no address is
            // listed, and the row does not say the PC has none.
            var addresses = _store.NetworkPort == 0 ? new List<string>()
                : NetworkDoor.PrivateAddresses().Select(a => NetworkDoor.Address(a, _store.NetworkPort)).ToList();
            if (!NetworkAddresses.Select(a => a.Address).SequenceEqual(addresses))
            {
                NetworkAddresses.Clear();
                for (int i = 0; i < addresses.Count; i++) NetworkAddresses.Add(new NetworkAddressItem(addresses[i], i == 0, CopyNetwork));
                OnPropertyChanged(nameof(HasNetworkAddress));
            }
            OnPropertyChanged(nameof(ShowsNoAddress));
            NetworkPortText = _store.NetworkPort == 0 ? string.Empty : _store.NetworkPort.ToString(CultureInfo.CurrentCulture);
            NetworkLine = NetworkLineNow(s, culture);
            // A key's row is kept while the key is: a reach picked in the
            // row changes the store, whose Changed reads it again here.
            bool had = NetworkKeys.Count > 0;
            RowSync.Sync(NetworkKeys, _store.Keys.ToList(), row => row.Id, k => k.Id,
                (k, i) => new NetworkKeyItem(k.Id, k.Name, KeyLine(k, s, culture), i == 0, id => _door.RevokeNetworkKey(id),
                    k.Reach, (id, reach) => _door.SetKeyReach(id, reach), (key, name, reach) => PickReachDevices?.Invoke(key, name, reach)),
                (row, k, i) => row.Show(k.Name, KeyLine(k, s, culture), i == 0, k.Reach));
            if (had != NetworkKeys.Count > 0) OnPropertyChanged(nameof(HasNetworkKeys));
        }

        // ── The switch ──

        public bool IsOn
        {
            get => _store.On;
            set
            {
                if (value == _store.On) return;
                if (value)
                {
                    // Turning it on asks: an extra that listens on the network.
                    if (!(AskToTurnOn?.Invoke() ?? false))
                    {
                        OnPropertyChanged();
                        return;
                    }
                    _door.TurnOn();
                }
                else _door.TurnOff();
                Refresh();
            }
        }

        // ── Tunnel ──

        public ObservableCollection<WebTunnelChoice> Tunnels { get; } = new();

        private WebTunnelChoice _tunnel;
        public WebTunnelChoice SelectedTunnel
        {
            get => _tunnel;
            set
            {
                if (value == null || Equals(value, _tunnel)) return;
                _tunnel = value;
                OnPropertyChanged();
                if (_refreshing) return;
                _door.PickTunnel(value.Key, value.Key == "paste" ? _pasted : null);
                Refresh();
            }
        }

        public bool IsPaste => _store.Tunnel == "paste";

        private string _pasted;
        private volatile bool _pastedStale;
        public string PastedAddress
        {
            get => _pasted;
            set
            {
                if (!SetProperty(ref _pasted, value)) return;
                if (_refreshing) return;
                if (WebAddress.Base(value) != null || string.IsNullOrWhiteSpace(value)) _door.PickTunnel("paste", value);
                OnPropertyChanged(nameof(PasteNote));
                OnPropertyChanged(nameof(HasPasteNote));
            }
        }

        public string PasteNote => IsPaste && !string.IsNullOrWhiteSpace(_pasted) && WebAddress.Base(_pasted) == null ? Strings.Instance.Web_Tunnel_Paste_Wrong : null;
        public bool HasPasteNote => PasteNote != null;

        private string _tunnelLine;
        public string TunnelLine { get => _tunnelLine; private set => SetProperty(ref _tunnelLine, value); }

        // ── Address ──

        private string _address;
        public string Address { get => _address; private set => SetProperty(ref _address, value); }

        public bool HasAddress => !string.IsNullOrEmpty(_address);

        private string _note;
        public string AddressNote { get => _note; private set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasAddressNote)); } }
        public bool HasAddressNote => !string.IsNullOrEmpty(_note);

        // ── Accept a New Web Assistant ──

        private string _acceptLine, _acceptWords;
        public string AcceptLine { get => _acceptLine; private set => SetProperty(ref _acceptLine, value); }
        public string AcceptWords { get => _acceptWords; private set => SetProperty(ref _acceptWords, value); }

        // ── Allowed on the Web ──

        public ObservableCollection<WebAllowedItem> Allowed { get; } = new();
        public bool HasAllowed => Allowed.Count > 0;


        /// <summary>Reads the door again: on the door's Changed, when the card
        /// comes into view, and once a second while the card shows and the
        /// armed window runs.</summary>
        internal void Refresh()
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            _refreshing = true;
            try
            {
                OnPropertyChanged(nameof(IsOn));
                OnPropertyChanged(nameof(IsPaste));
                OnPropertyChanged(nameof(IsChatGpt));
                OnPropertyChanged(nameof(ShowsAddress));
                OnPropertyChanged(nameof(HasRuntimeKey));
                OnPropertyChanged(nameof(IsArmed));
                OnPropertyChanged(nameof(RuntimeKeyLine));
                if (_store.TunnelId != _tunnelIdShown)
                {
                    _tunnelIdShown = _store.TunnelId;
                    OnPropertyChanged(nameof(TunnelIdText));
                }
                OnPropertyChanged(nameof(Footnote));
                RefreshNetwork(s, culture);

                // The list: None, each tunnel program found, the ChatGPT
                // tunnel, which PadForge fetches, An Address I Paste.
                var want = new List<WebTunnelChoice> { new(null, s.Web_Tunnel_None) };
                foreach (var found in _door.Found)
                    want.Add(new WebTunnelChoice(found.Key, WebTunnels.Of(found.Key)?.Name ?? found.Key));
                want.Add(new WebTunnelChoice(TunnelClient.Key, WebTunnels.Of(TunnelClient.Key).Name));
                // A program picked before and not found now stays listed.
                if (_store.Tunnel is { } picked && picked != "paste" && picked != TunnelClient.Key && want.All(w => w.Key != picked) && WebTunnels.Of(picked) is { } gone)
                    want.Add(new WebTunnelChoice(gone.Key, gone.Name));
                want.Add(new WebTunnelChoice("paste", s.Web_Tunnel_Paste));
                bool rebuilt = !Tunnels.SequenceEqual(want);
                if (rebuilt)
                {
                    Tunnels.Clear();
                    foreach (var w in want) Tunnels.Add(w);
                }
                // A rebuilt list leaves the box with no pick, the one it had
                // being gone from it, so it is told its pick again even when
                // the choice compares equal.
                var selected = Tunnels.FirstOrDefault(t => t.Key == _store.Tunnel) ?? Tunnels[0];
                if (rebuilt || !Equals(selected, _tunnel)) { _tunnel = selected; OnPropertyChanged(nameof(SelectedTunnel)); }
                // The box holds the address the store keeps until the user
                // types, and again after the file is read in.
                if (_pasted == null || _pastedStale)
                {
                    _pastedStale = false;
                    if (_pasted != _store.Address) { _pasted = _store.Address; OnPropertyChanged(nameof(PastedAddress)); }
                }
                OnPropertyChanged(nameof(PasteNote));
                OnPropertyChanged(nameof(HasPasteNote));

                TunnelLine = Line(s, culture);
                Address = _door.PublicAddress;
                OnPropertyChanged(nameof(HasAddress));
                // A Copy's note is about the address it copied.
                if (_noteFor != null && _noteFor != Address)
                {
                    _noteFor = null;
                    AddressNote = null;
                }

                bool armed = _door.Armed;
                AcceptLine = armed
                    ? string.Format(culture, s.Web_Accept_Until_Format, _door.ArmedUntil.ToLocalTime().ToString("t", culture))
                    : s.Web_Accept_Off;
                AcceptWords = armed ? s.Web_Accept_Stop : s.Web_Accept_Start;

                // A grant's row is kept while the grant is, as a key's is.
                bool had = Allowed.Count > 0;
                RowSync.Sync(Allowed, _store.Grants.ToList(), row => row.Id, g => g.Id,
                    (g, _) => new WebAllowedItem(g.Id, g.Origin, AllowedLine(g, s, culture), id => _door.Revoke(id),
                        g.Reach, (id, reach) => _door.SetGrantReach(id, reach), (key, name, reach) => PickReachDevices?.Invoke(key, name, reach)),
                    (row, g, _) => row.Show(g.Origin, AllowedLine(g, s, culture), g.Reach));
                if (had != Allowed.Count > 0) OnPropertyChanged(nameof(HasAllowed));
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>Asks the helper to look for tunnel programs again: the
        /// card calls it when it shows and when the Tunnel list opens.</summary>
        internal void LookForTunnels() => _door.LookForTunnels();

        /// <summary>The Tunnel row's status line: the helper, then the tunnel
        /// picked, whether it keeps its address, and the others on this PC.</summary>
        private string Line(Strings s, CultureInfo culture)
        {
            if (_door.HelperState == WebHelperState.Stopped)
                return string.Format(culture, s.Web_Helper_Retrying_Format, _door.HelperWords ?? "?");
            if (_door.HelperState == WebHelperState.Starting) return s.Web_Helper_Starting;
            if (_door.HelperState == WebHelperState.WaitsForAutomation) return s.Web_Helper_AutomationOff;
            if (_store.Tunnel == "paste")
                return string.Format(culture, s.Web_Tunnel_Paste_Hint_Format, _door.Port.ToString(CultureInfo.InvariantCulture));
            if (_store.Tunnel == TunnelClient.Key) return ChatGptLine(s, culture);
            var others = _door.Found.Where(f => f.Key != _store.Tunnel).Select(f => WebTunnels.Of(f.Key)).Where(k => k != null)
                .Select(k => string.Format(culture, k.KeepsAddress ? s.Web_Tunnel_Keeps_Format : s.Web_Tunnel_Changes_Format, k.Name)).ToList();
            string also = others.Count == 0 ? string.Empty : " " + string.Format(culture, s.Web_Tunnel_AlsoFound_Format, string.Join(", ", others));
            var kind = WebTunnels.Of(_store.Tunnel);
            if (kind == null) return (_door.Found.Count == 0 ? s.Web_Tunnel_NoProgramFound : s.Web_Tunnel_Choose) + also;
            var status = _door.Tunnel?.Key == kind.Key ? _door.Tunnel : null;
            string state = status?.State switch
            {
                "running" => kind.KeepsAddress ? s.Web_Tunnel_Running_Keeps : s.Web_Tunnel_Running_Changes,
                "stopped" => status.Words == null ? s.Web_Tunnel_Stopped : string.Format(culture, s.Web_Tunnel_Stopped_Format, status.Words),
                "failed" => s.Web_Tunnel_Failed,
                "missing" => s.Web_Tunnel_Missing,
                _ => s.Web_Tunnel_Starting,
            };
            return state + also;
        }

        /// <summary>The Tunnel row's line while the ChatGPT tunnel is picked
        /// (#600 DESIGN-2 N4): the fetch, what it still needs, and where
        /// tunnel-client stands.</summary>
        private string ChatGptLine(Strings s, CultureInfo culture)
        {
            string download = _door.DownloadState;
            if (download == "downloading") return s.Web_ChatGpt_Downloading;
            if (download != null) return string.Format(culture, s.Web_ChatGpt_DownloadFailed_Format, download);
            var status = _door.Tunnel?.Key == TunnelClient.Key ? _door.Tunnel : null;
            return status?.State switch
            {
                "ready" => s.Web_ChatGpt_ReadyReported,
                "running" => s.Web_ChatGpt_NotReadyYet,
                "needs" => s.Web_ChatGpt_Needs,
                "stopped" => status.Words == null ? s.Web_Tunnel_Stopped : string.Format(culture, s.Web_Tunnel_Stopped_Format, status.Words),
                "failed" => s.Web_Tunnel_Failed,
                "missing" => s.Web_ChatGpt_Missing,
                _ => _store.RuntimeKeyBlob == null || _store.TunnelId == null ? s.Web_ChatGpt_Needs : s.Web_Tunnel_Starting,
            };
        }

        private string AllowedLine(WebGrant g, Strings s, CultureInfo culture)
        {
            string line = string.Format(culture, s.Web_Allowed_ReturnsTo_Format, g.Origin);
            if (!string.IsNullOrEmpty(g.Name)) line += " " + string.Format(culture, s.Web_Allowed_CallsItself_Format, g.Name);
            string allowed = When(g.Allowed, culture);
            line += " " + (g.LastUsed is { } used
                ? string.Format(culture, s.Web_Allowed_When_Format, allowed, When(used, culture))
                : string.Format(culture, s.Web_Allowed_WhenNew_Format, allowed));
            return line;
        }

        private string When(DateTimeOffset when, CultureInfo culture)
        {
            var local = when.ToLocalTime();
            return local.Date == _now().ToLocalTime().Date ? local.ToString("t", culture) : local.ToString("g", culture);
        }

        /// <summary>The address the Copy note is about.</summary>
        private string _noteFor;

        private void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _noteFor = text;
            try
            {
                if (ClipboardSeam != null) ClipboardSeam(text);
                else ShellLaunch.SetClipboardText(text);
                AddressNote = Strings.Instance.Web_Address_Copied;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                AddressNote = Strings.Instance.Extras_Automation_NotCopied;
            }
        }

        /// <summary>The link that opens Claude's add dialog with the address
        /// filled in, or null without an address.</summary>
        internal string ClaudeLink => HasAddress ? ClaudeAddLink + Uri.EscapeDataString(Address) : null;

        private void OpenClaude()
        {
            string link = ClaudeLink;
            if (link == null) return;
            if (OpenLinkSeam != null) { OpenLinkSeam(link); return; }
            ShellLaunch.AsTheUser(new ProcessStartInfo(link) { UseShellExecute = true }, out _);
        }
    }
}
