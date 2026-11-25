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
using PadForge.Services.Automation.Web;

namespace PadForge.ViewModels
{
    /// <summary>One tunnel in the Tunnel list.</summary>
    public sealed record WebTunnelChoice(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>One allowed web assistant on the Assistants on the Web card.</summary>
    public sealed class WebAllowedItem
    {
        internal WebAllowedItem(string id, string origin, string line, Action<string> revoke)
        {
            Id = id;
            Origin = origin;
            Line = line;
            RevokeCommand = new RelayCommand(() => revoke?.Invoke(id));
        }

        public string Id { get; }

        /// <summary>The host its sign-in returned to: what it is named by.</summary>
        public string Origin { get; }

        public string Line { get; }

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
            CopyCommandCommand = new RelayCommand(() => Copy(StdioCommand));
            _door.Changed += (_, _) => Post(Refresh);
            Refresh();
        }

        public RelayCommand ArmCommand { get; }

        private RelayCommand<string> _resetSettingCommand;

        /// <summary>The switch's reset: back to its default, off, which asks
        /// nothing.</summary>
        public RelayCommand<string> ResetSettingCommand => _resetSettingCommand ??= new RelayCommand<string>(name =>
        {
            if (name == nameof(IsOn)) IsOn = false;
        }, CanResetSetting);

        internal static bool CanResetSetting(string name) => name == nameof(IsOn);

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
            };
            if (!IsOn) return items;
            items.Add(new("web/tunnel", PadForge.Services.Catalog.LiveKind.Setting, s.Web_Tunnel_Label, Own) { Read = () => SelectedTunnel?.Name, Where = card });
            items.Add(new("web/address", PadForge.Services.Catalog.LiveKind.Setting, s.Web_Address_Label, Own) { Read = () => Address, Where = card });
            items.Add(new("web/accept", PadForge.Services.Catalog.LiveKind.Action, s.Web_Accept_Label, Own) { Read = () => AcceptLine, Where = card });
            items.Add(new("web/copy-command", PadForge.Services.Catalog.LiveKind.Action, s.Web_ChatGpt_Copy, Own) { Where = card });
            foreach (var row in Allowed)
                items.Add(new("web/allowed/" + row.Id, PadForge.Services.Catalog.LiveKind.Action, row.Origin + ": " + s.Web_Allowed_Revoke, Own)
                { Read = () => row.Line, Where = card });
            return items;
        }
        public RelayCommand CopyAddressCommand { get; }
        public RelayCommand AddToClaudeCommand { get; }
        public RelayCommand CopyCommandCommand { get; }

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

        /// <summary>PadForge's stdio command, for the row of the assistant
        /// whose own tunnel runs it: ChatGPT's Secure MCP Tunnel takes
        /// <c>--mcp-command</c> (OpenAI's secure MCP tunnels guide).</summary>
        public string StdioCommand => "\"" + (Environment.ProcessPath ?? "PadForge.exe") + "\" --mcp";

        /// <summary>Reads the door again, at most once a second while the
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

                // The list: None, each tunnel program found, An Address I Paste.
                var want = new List<WebTunnelChoice> { new(null, s.Web_Tunnel_None) };
                foreach (var found in _door.Found)
                    want.Add(new WebTunnelChoice(found.Key, WebTunnels.Of(found.Key)?.Name ?? found.Key));
                // A program picked before and not found now stays listed.
                if (_store.Tunnel is { } picked && picked != "paste" && want.All(w => w.Key != picked) && WebTunnels.Of(picked) is { } gone)
                    want.Add(new WebTunnelChoice(gone.Key, gone.Name));
                want.Add(new WebTunnelChoice("paste", s.Web_Tunnel_Paste));
                if (!Tunnels.SequenceEqual(want))
                {
                    Tunnels.Clear();
                    foreach (var w in want) Tunnels.Add(w);
                }
                var selected = Tunnels.FirstOrDefault(t => t.Key == _store.Tunnel) ?? Tunnels[0];
                if (!Equals(selected, _tunnel)) { _tunnel = selected; OnPropertyChanged(nameof(SelectedTunnel)); }
                if (_store.Tunnel == "paste" && _pasted == null) { _pasted = _store.Address; OnPropertyChanged(nameof(PastedAddress)); }
                OnPropertyChanged(nameof(PasteNote));
                OnPropertyChanged(nameof(HasPasteNote));

                TunnelLine = Line(s, culture);
                Address = _door.PublicAddress;
                OnPropertyChanged(nameof(HasAddress));

                bool armed = _door.Armed;
                AcceptLine = armed
                    ? string.Format(culture, s.Web_Accept_Until_Format, _door.ArmedUntil.ToLocalTime().ToString("t", culture))
                    : s.Web_Accept_Off;
                AcceptWords = armed ? s.Web_Accept_Stop : s.Web_Accept_Start;

                var items = _store.Grants.Select(g => new WebAllowedItem(g.Id, g.Origin, AllowedLine(g, s, culture), id => _door.Revoke(id))).ToList();
                if (!Allowed.Select(a => a.Id + a.Line).SequenceEqual(items.Select(a => a.Id + a.Line)))
                {
                    Allowed.Clear();
                    foreach (var item in items) Allowed.Add(item);
                    OnPropertyChanged(nameof(HasAllowed));
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>The Tunnel row's status line: the helper, then the tunnel
        /// picked, whether it keeps its address, and the others on this PC.</summary>
        private string Line(Strings s, CultureInfo culture)
        {
            if (_door.HelperState == WebHelperState.Stopped)
                return string.Format(culture, s.Web_Helper_Stopped_Format, _door.HelperWords ?? "?");
            if (_door.HelperState == WebHelperState.Starting) return s.Web_Helper_Starting;
            if (_door.HelperState == WebHelperState.WaitsForAutomation) return s.Web_Helper_AutomationOff;
            if (_store.Tunnel == "paste")
                return string.Format(culture, s.Web_Tunnel_Paste_Hint_Format, _door.Port.ToString(CultureInfo.InvariantCulture));
            var others = _door.Found.Where(f => f.Key != _store.Tunnel).Select(f => WebTunnels.Of(f.Key)).Where(k => k != null)
                .Select(k => string.Format(culture, k.KeepsAddress ? s.Web_Tunnel_Keeps_Format : s.Web_Tunnel_Changes_Format, k.Name)).ToList();
            string also = others.Count == 0 ? string.Empty : " " + string.Format(culture, s.Web_Tunnel_AlsoFound_Format, string.Join(", ", others));
            var kind = WebTunnels.Of(_store.Tunnel);
            if (kind == null) return (_door.Found.Count == 0 ? s.Web_Tunnel_NoneFound : s.Web_Tunnel_Choose) + also;
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

        private void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (ClipboardSeam != null) ClipboardSeam(text);
                else ShellLaunch.SetClipboardText(text);
                AddressNote = Strings.Instance.Web_Address_Copied;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                AddressNote = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_NotCopied_Format, ex.Message);
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
