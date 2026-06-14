using System;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>One PadForge PC discovered on the LAN (issue #138), shown in the
    /// Dashboard "Nearby PCs" list. Clicking Pair initiates the pairing — no IP typing.</summary>
    public sealed class RemoteLinkNearbyPeer
    {
        public RemoteLinkNearbyPeer(string name, string hostPort, string fingerprintHex, bool isPaired, bool isConnected, Action<string> onPair)
        {
            Name = name;
            HostPort = hostPort;
            FingerprintHex = fingerprintHex;
            IsPaired = isPaired;
            IsConnected = isConnected;
            PairCommand = new RelayCommand(() => onPair?.Invoke(hostPort));
        }

        public string Name { get; }
        public string HostPort { get; }
        public string FingerprintHex { get; }
        public bool IsPaired { get; }
        public bool IsConnected { get; }

        /// <summary>Name with a state suffix.</summary>
        public string DisplayName =>
            IsConnected ? $"{Name} (connected)" : IsPaired ? $"{Name} (paired)" : Name;

        /// <summary>Button text by state: Connected (disabled) / Connect (paired) / Pair (new).</summary>
        public string ButtonLabel =>
            IsConnected ? PadForge.Resources.Strings.Strings.Instance.RemoteLink_Connected
            : IsPaired ? PadForge.Resources.Strings.Strings.Instance.RemoteLink_Connect
            : PadForge.Resources.Strings.Strings.Instance.Dashboard_RemoteLinkPairButton;

        /// <summary>The action button is disabled while a live session already exists.</summary>
        public bool CanPair => !IsConnected;

        public RelayCommand PairCommand { get; }
    }
}
