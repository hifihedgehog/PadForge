using System;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>One PadForge PC discovered on the LAN (issue #138), shown in the
    /// Dashboard "Nearby PCs" list. Clicking Pair initiates the pairing — no IP typing.</summary>
    public sealed class RemoteLinkNearbyPeer
    {
        public RemoteLinkNearbyPeer(string name, string hostPort, string fingerprintHex, bool isPaired, Action<string> onPair)
        {
            Name = name;
            HostPort = hostPort;
            FingerprintHex = fingerprintHex;
            IsPaired = isPaired;
            PairCommand = new RelayCommand(() => onPair?.Invoke(hostPort));
        }

        public string Name { get; }
        public string HostPort { get; }
        public string FingerprintHex { get; }
        public bool IsPaired { get; }

        /// <summary>Name with a "(paired)" suffix when this PC is already trusted.</summary>
        public string DisplayName => IsPaired ? $"{Name} (paired)" : Name;

        public RelayCommand PairCommand { get; }
    }
}
