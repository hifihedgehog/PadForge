using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>One trusted peer in the Settings paired-peer manager (issue #138). The
    /// name is editable (committed on focus loss → persisted to the trust store), and the
    /// online dot reflects a live session, refreshed in place so editing isn't disrupted.</summary>
    public sealed class RemoteLinkTrustedPeer : ObservableObject
    {
        private readonly Action<string, string> _onRename;

        public RemoteLinkTrustedPeer(string name, string fingerprintHex, string pairedUtc, bool gamepadOnly, bool isOnline,
            Action<string> onRevoke, Action<string, string> onRename, Action<string> onConnect)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "Paired PC" : name;
            FingerprintHex = fingerprintHex ?? "";
            PairedUtc = pairedUtc ?? "";
            GamepadOnly = gamepadOnly;
            _isOnline = isOnline;
            _onRename = onRename;
            RevokeCommand = new RelayCommand(() => onRevoke?.Invoke(FingerprintHex));
            ConnectCommand = new RelayCommand(() => { if (!string.IsNullOrEmpty(_reachableHostPort)) onConnect?.Invoke(_reachableHostPort); });
        }

        private string _reachableHostPort;
        /// <summary>Where this peer is reachable right now (host:port from LAN discovery),
        /// or null when it isn't discovered. Drives the Connect button.</summary>
        public string ReachableHostPort
        {
            get => _reachableHostPort;
            set { if (SetProperty(ref _reachableHostPort, value)) OnPropertyChanged(nameof(CanConnect)); }
        }

        /// <summary>Show a Connect button when the peer is on the LAN but not connected.</summary>
        public bool CanConnect => !string.IsNullOrEmpty(_reachableHostPort) && !_isOnline;

        /// <summary>Reconnect to this already-trusted peer (no SAS prompt — the handshake
        /// auto-accepts a known key).</summary>
        public RelayCommand ConnectCommand { get; }

        private string _name;
        /// <summary>Friendly name. Setting it (e.g. on TextBox focus-loss) persists the
        /// rename to the trust store via the callback.</summary>
        public string Name
        {
            get => _name;
            set
            {
                string v = string.IsNullOrWhiteSpace(value) ? "Paired PC" : value.Trim();
                if (SetProperty(ref _name, v)) _onRename?.Invoke(FingerprintHex, v);
            }
        }

        private bool _isOnline;
        public bool IsOnline
        {
            get => _isOnline;
            set { if (SetProperty(ref _isOnline, value)) { OnPropertyChanged(nameof(OnlineText)); OnPropertyChanged(nameof(CanConnect)); } }
        }

        public string OnlineText => IsOnline ? "Online" : "Offline";

        public string FingerprintHex { get; }
        public string PairedUtc { get; }
        public bool GamepadOnly { get; }

        /// <summary>Short, grouped fingerprint for display.</summary>
        public string FingerprintDisplay
        {
            get
            {
                string head = FingerprintHex.Length > 16 ? FingerprintHex.Substring(0, 16) : FingerprintHex;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < head.Length; i++)
                {
                    if (i > 0 && i % 4 == 0) sb.Append(' ');
                    sb.Append(head[i]);
                }
                string fp = sb.ToString();
                return GamepadOnly ? $"{fp}  ·  gamepad only" : fp;
            }
        }

        public RelayCommand RevokeCommand { get; }
    }
}
