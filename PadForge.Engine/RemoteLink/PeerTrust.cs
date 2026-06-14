using System;
using System.Xml.Serialization;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// One trusted peer in the persisted trust list (issue #138). Identity is the
    /// peer's static Ed25519 public key (base64); a stored trust entry can never
    /// impersonate the peer without that peer's private key, so it is not secret.
    /// Serializes as PadForge.xml attributes, the same shape as SoundPackageData.
    /// </summary>
    public sealed class PeerTrust
    {
        /// <summary>Peer static Ed25519 public key, base64. The pinned identity.</summary>
        [XmlAttribute] public string PublicKeyBase64 { get; set; } = "";

        /// <summary>Human-readable name shown in the peer manager.</summary>
        [XmlAttribute] public string Name { get; set; } = "";

        /// <summary>When the one-time pairing grant happened (ISO-8601 UTC string).</summary>
        [XmlAttribute] public string PairedUtc { get; set; } = "";

        /// <summary>Auto-select: re-apply this prior explicit grant on reconnect with no
        /// prompt. Never skips the SAS gate for a NEW key and never admits an unknown
        /// peer — it only removes list-pick friction for an already-trusted key.</summary>
        [XmlAttribute] public bool ReconnectEnabled { get; set; } = true;

        /// <summary>When set, this peer may only feed gamepad-type slots and can never
        /// be a trigger source for a keyboard/mouse/scroll macro action.</summary>
        [XmlAttribute] public bool GamepadOnly { get; set; }

        /// <summary>Decoded public key bytes, or null if the stored value is malformed.</summary>
        [XmlIgnore]
        public byte[] PublicKey
        {
            get
            {
                try { return string.IsNullOrEmpty(PublicKeyBase64) ? null : Convert.FromBase64String(PublicKeyBase64); }
                catch { return null; }
            }
        }

        /// <summary>SHA-256 fingerprint hex, or "" if the key is missing/malformed.</summary>
        [XmlIgnore]
        public string FingerprintHex
        {
            get
            {
                var key = PublicKey;
                return key != null && key.Length == PeerCrypto.KeySize
                    ? Convert.ToHexString(PeerCrypto.Fingerprint(key)) : "";
            }
        }

        public static PeerTrust FromPublicKey(byte[] publicKey, string name, string pairedUtc, bool reconnect, bool gamepadOnly)
            => new()
            {
                PublicKeyBase64 = Convert.ToBase64String(publicKey),
                Name = name ?? "",
                PairedUtc = pairedUtc ?? "",
                ReconnectEnabled = reconnect,
                GamepadOnly = gamepadOnly,
            };
    }
}
