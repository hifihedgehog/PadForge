using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>How an inbound peer should be admitted, based on its pinned trust state.</summary>
    public enum TrustDecision
    {
        /// <summary>Unknown key — must run the full SAS pairing with an explicit human grant.
        /// Discovery and a known network never substitute for this first grant.</summary>
        FirstContact,

        /// <summary>Known key with auto-select on — reconnect with no prompt, authenticated
        /// against the pinned key (the handshake still proves possession + forward secrecy).</summary>
        KnownAutoSelect,

        /// <summary>Known key with auto-select off — recognized, but the user re-selects it
        /// from the list before input is accepted. Still no fresh SAS (key is pinned).</summary>
        KnownManual,
    }

    /// <summary>
    /// In-memory authority over the persisted trust list (issue #138). Holds the
    /// admission policy so the rest of the link never has to: an unknown key always
    /// means FirstContact (no auto-trust, ever), and auto-select only re-applies a
    /// prior explicit grant for an already-pinned key. The persistence layer feeds
    /// it the loaded list and writes back on every mutation.
    /// </summary>
    public sealed class PeerTrustStore
    {
        private readonly object _lock = new object();
        private readonly List<PeerTrust> _peers;

        public PeerTrustStore(IEnumerable<PeerTrust> initial = null)
        {
            _peers = initial?.Where(p => p?.PublicKey != null).ToList() ?? new List<PeerTrust>();
        }

        /// <summary>Snapshot of the trusted peers (copy — safe to enumerate).</summary>
        public IReadOnlyList<PeerTrust> Peers
        {
            get { lock (_lock) return _peers.ToList(); }
        }

        /// <summary>Find the trust entry for a static public key, or null.</summary>
        public PeerTrust Find(byte[] publicKey)
        {
            if (publicKey == null) return null;
            lock (_lock)
                return _peers.FirstOrDefault(p => KeyEquals(p.PublicKey, publicKey));
        }

        public bool IsTrusted(byte[] publicKey) => Find(publicKey) != null;

        /// <summary>The label to suffix onto a peer's shared device names: the custom name the
        /// user set, else the discovery-learned host name, else null when neither is known yet.
        /// Matched by fingerprint, case-insensitive. Lets a remote peer's devices read e.g.
        /// "DualSense Wireless Controller (John's PC)" in the device list.</summary>
        public string ResolvePeerLabel(string peerFingerprintHex)
        {
            if (string.IsNullOrWhiteSpace(peerFingerprintHex)) return null;
            var peer = Peers.FirstOrDefault(p =>
                string.Equals(p.FingerprintHex, peerFingerprintHex, StringComparison.OrdinalIgnoreCase));
            if (peer == null) return null;
            if (!string.IsNullOrWhiteSpace(peer.Name)) return peer.Name;
            return string.IsNullOrWhiteSpace(peer.HostName) ? null : peer.HostName;
        }

        /// <summary>
        /// The admission decision for a connecting peer's static key. An unknown key
        /// is always FirstContact regardless of any auto-select or network hint.
        /// </summary>
        public TrustDecision Decide(byte[] publicKey)
        {
            var entry = Find(publicKey);
            if (entry == null) return TrustDecision.FirstContact;
            return entry.ReconnectEnabled ? TrustDecision.KnownAutoSelect : TrustDecision.KnownManual;
        }

        /// <summary>
        /// Pin a peer after a successful first-time pairing (or update an existing
        /// entry's metadata). Returns the stored entry. Caller persists afterward.
        /// </summary>
        public PeerTrust Grant(byte[] publicKey, string name, string pairedUtc, bool reconnect, bool gamepadOnly)
        {
            if (publicKey == null || publicKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException("Peer public key must be a 32-byte Ed25519 key.", nameof(publicKey));
            lock (_lock)
            {
                var existing = _peers.FirstOrDefault(p => KeyEquals(p.PublicKey, publicKey));
                if (existing != null)
                {
                    existing.Name = name ?? existing.Name;
                    existing.ReconnectEnabled = reconnect;
                    existing.GamepadOnly = gamepadOnly;
                    return existing;
                }
                var entry = PeerTrust.FromPublicKey(publicKey, name, pairedUtc, reconnect, gamepadOnly);
                _peers.Add(entry);
                return entry;
            }
        }

        /// <summary>Remove a peer's trust. Returns true if it was present. The caller
        /// also cancels any live session and zeroizes session keys — a revoked peer's
        /// next handshake then fails closed and it must pair again.</summary>
        public bool Revoke(byte[] publicKey)
        {
            lock (_lock)
            {
                int removed = _peers.RemoveAll(p => KeyEquals(p.PublicKey, publicKey));
                return removed > 0;
            }
        }

        public void RevokeAll()
        {
            lock (_lock) _peers.Clear();
        }

        /// <summary>Replace all entries in place (a reload), keeping THIS store instance
        /// so a running LinkServer holding a reference still sees the current trust set.</summary>
        public void ReplaceAll(IEnumerable<PeerTrust> peers)
        {
            lock (_lock)
            {
                _peers.Clear();
                if (peers != null)
                    foreach (var p in peers)
                        if (p?.PublicKey != null) _peers.Add(p);
            }
        }

        /// <summary>Whether a known peer is restricted to gamepad-only output. Unknown
        /// keys default to restricted (fail safe), though an unknown key never reaches
        /// admission in the first place.</summary>
        public bool IsGamepadOnly(byte[] publicKey)
        {
            var entry = Find(publicKey);
            return entry?.GamepadOnly ?? true;
        }

        private static bool KeyEquals(byte[] a, byte[] b)
            => a != null && b != null && a.Length == b.Length && PeerCrypto.FixedTimeEquals(a, b);
    }
}
