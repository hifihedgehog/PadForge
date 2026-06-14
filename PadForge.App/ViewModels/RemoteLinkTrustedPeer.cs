using System;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>One trusted peer shown in the Settings paired-peer manager (issue #138).</summary>
    public sealed class RemoteLinkTrustedPeer
    {
        public RemoteLinkTrustedPeer(string name, string fingerprintHex, string pairedUtc, bool gamepadOnly, Action<string> onRevoke)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Paired PC" : name;
            FingerprintHex = fingerprintHex ?? "";
            PairedUtc = pairedUtc ?? "";
            GamepadOnly = gamepadOnly;
            RevokeCommand = new RelayCommand(() => onRevoke?.Invoke(FingerprintHex));
        }

        public string Name { get; }
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
