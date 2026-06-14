using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>Outcome of a completed handshake — the per-session key plus the
    /// material the pairing UI and trust store need.</summary>
    public sealed class HandshakeResult
    {
        /// <summary>32-byte AEAD session key for <see cref="LinkSession"/>.</summary>
        public byte[] SessionKey { get; init; }
        /// <summary>6-digit short authentication string for out-of-band human comparison.</summary>
        public string Sas { get; init; }
        /// <summary>The peer's static Ed25519 public key, to pin in the trust list.</summary>
        public byte[] PeerStaticPublicKey { get; init; }
        /// <summary>SHA-256 fingerprint of the peer's static key.</summary>
        public byte[] PeerFingerprint { get; init; }
        /// <summary>Opaque capability bytes the peer advertised (bound into the signed transcript).</summary>
        public byte[] PeerCapabilities { get; init; }
        /// <summary>True for the side that initiated — selects the LinkSession nonce direction.</summary>
        public bool IsInitiator { get; init; }
    }

    /// <summary>
    /// Pairing/reconnect handshake for the Remote Link control channel (issue #138),
    /// run over the reliable ordered TCP path. Authenticated key exchange with:
    ///  - a fresh X25519 ephemeral per side for forward secrecy (the ee DH),
    ///  - commit-before-reveal on the initiator's ephemeral so neither side can
    ///    grind the displayed SAS to a collision before both contributions are fixed,
    ///  - Ed25519 signatures over the full transcript hash for mutual static-key
    ///    authentication (so trust-list presence alone never authenticates), and
    ///  - capabilities + version folded into that signed transcript, so a downgrade
    ///    is detected at the signature check, not negotiated in the clear.
    /// The 6-digit SAS is compared out-of-band on first pairing; on reconnect the
    /// caller already pinned the peer key and skips the human step.
    ///
    /// Message order:  I-&gt;R COMMIT, R-&gt;I REVEAL_R, I-&gt;R REVEAL_I, R-&gt;I CONFIRM.
    /// Each Step* returns the next outbound message bytes, or throws
    /// <see cref="HandshakeException"/> on any malformed or unauthenticated input
    /// (fail closed — no device is created on a failed handshake).
    /// </summary>
    public sealed class LinkHandshake
    {
        private const int NonceLen = 16;
        private static readonly byte[] SessionInfo = System.Text.Encoding.ASCII.GetBytes("padforge-link v1 session");
        private static readonly byte[] SasInfo = System.Text.Encoding.ASCII.GetBytes("padforge-link v1 sas");
        private static readonly byte[] CommitInfo = System.Text.Encoding.ASCII.GetBytes("padforge-link v1 commit");

        private readonly PeerIdentity _identity;
        private readonly byte[] _capabilities;
        private readonly bool _isInitiator;

        private PeerCrypto.KeyPair _ephemeral;
        private byte[] _nonce;
        private byte[] _commitMsg;     // raw COMMIT bytes (transcript part 1)
        private byte[] _revealRMsg;    // raw REVEAL_R bytes (transcript part 2)
        private byte[] _revealIMsg;    // raw REVEAL_I core bytes, no signature (transcript part 3)

        // Peer-revealed material.
        private byte[] _peerEphemeralPub;
        private byte[] _peerNonce;
        private byte[] _peerStaticPub;
        private byte[] _peerCaps;
        private byte[] _expectedCommit; // responder: commit it must match on REVEAL_I

        private State _state;
        private enum State { Init, SentCommit, SentRevealR, Done }

        public LinkHandshake(PeerIdentity identity, byte[] capabilities, bool isInitiator)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _capabilities = capabilities ?? Array.Empty<byte>();
            _isInitiator = isInitiator;
            _ephemeral = PeerCrypto.GenerateX25519KeyPair();
            _nonce = PeerCrypto.RandomBytes(NonceLen);
        }

        public HandshakeResult Result { get; private set; }

        // ── Initiator ───────────────────────────────────────────────────────

        /// <summary>I → R: commit to our ephemeral + nonce + caps (hash only).</summary>
        public byte[] StartCommit()
        {
            Require(_isInitiator && _state == State.Init, "StartCommit out of order.");
            byte[] commit = Hash(CommitInfo, _ephemeral.PublicKey, _nonce, _capabilities);
            _commitMsg = Tlv.Encode(commit);
            _state = State.SentCommit;
            return _commitMsg;
        }

        /// <summary>I receives REVEAL_R, then I → R: reveal our values + sign the transcript.</summary>
        public byte[] OnResponderReveal(ReadOnlySpan<byte> msg2)
        {
            Require(_isInitiator && _state == State.SentCommit, "OnResponderReveal out of order.");
            _revealRMsg = msg2.ToArray();
            var f = Decode(msg2, 4, "REVEAL_R");
            _peerEphemeralPub = Key(f[0], "peer ephemeral");
            _peerNonce = f[1];
            _peerCaps = f[2];
            _peerStaticPub = Key(f[3], "peer static");

            _revealIMsg = Tlv.Encode(_ephemeral.PublicKey, _nonce, _capabilities, _identity.PublicKey);
            byte[] transcriptHash = TranscriptHash();
            byte[] sig = _identity.Sign(transcriptHash);

            Finish(transcriptHash);
            _state = State.Done;
            return Tlv.Encode(_revealIMsg, sig); // REVEAL_I = (core, signature)
        }

        /// <summary>I receives CONFIRM (R's signature) and verifies it.</summary>
        public void OnResponderConfirm(ReadOnlySpan<byte> msg4)
        {
            Require(_isInitiator && _state == State.Done && Result == null, "OnResponderConfirm out of order.");
            var f = Decode(msg4, 1, "CONFIRM");
            byte[] sigR = f[0];
            if (!PeerCrypto.Ed25519Verify(_peerStaticPub, TranscriptHash(), sigR))
                throw new HandshakeException("Responder signature failed — possible MITM.");
            PublishResult();
        }

        // ── Responder ───────────────────────────────────────────────────────

        /// <summary>R receives COMMIT, then R → I: reveal our ephemeral + caps + static key.</summary>
        public byte[] OnInitiatorCommit(ReadOnlySpan<byte> msg1)
        {
            Require(!_isInitiator && _state == State.Init, "OnInitiatorCommit out of order.");
            _commitMsg = msg1.ToArray();
            var f = Decode(msg1, 1, "COMMIT");
            _expectedCommit = f[0];
            if (_expectedCommit.Length != 32) throw new HandshakeException("Bad commit length.");

            _revealRMsg = Tlv.Encode(_ephemeral.PublicKey, _nonce, _capabilities, _identity.PublicKey);
            _state = State.SentRevealR;
            return _revealRMsg;
        }

        /// <summary>R receives REVEAL_I, verifies the commit + signature, then R → I: CONFIRM.</summary>
        public byte[] OnInitiatorReveal(ReadOnlySpan<byte> msg3)
        {
            Require(!_isInitiator && _state == State.SentRevealR, "OnInitiatorReveal out of order.");
            var outer = Decode(msg3, 2, "REVEAL_I");
            _revealIMsg = outer[0];
            byte[] sigI = outer[1];

            var f = Decode(_revealIMsg, 4, "REVEAL_I core");
            _peerEphemeralPub = Key(f[0], "peer ephemeral");
            _peerNonce = f[1];
            _peerCaps = f[2];
            _peerStaticPub = Key(f[3], "peer static");

            // Commit-before-reveal: the revealed values must match the MSG1 commitment.
            byte[] recomputed = Hash(CommitInfo, _peerEphemeralPub, _peerNonce, _peerCaps);
            if (!PeerCrypto.FixedTimeEquals(recomputed, _expectedCommit))
                throw new HandshakeException("Commit mismatch — initiator changed its contribution.");

            byte[] transcriptHash = TranscriptHash();
            if (!PeerCrypto.Ed25519Verify(_peerStaticPub, transcriptHash, sigI))
                throw new HandshakeException("Initiator signature failed — possible MITM.");

            Finish(transcriptHash);
            byte[] sigR = _identity.Sign(transcriptHash);
            PublishResult();
            _state = State.Done;
            return Tlv.Encode(sigR); // CONFIRM
        }

        // ── Shared finish ───────────────────────────────────────────────────

        private byte[] _transcriptHash;
        private byte[] _sessionKey;
        private string _sas;

        private byte[] TranscriptHash()
        {
            // Identical on both sides: COMMIT || REVEAL_R || REVEAL_I-core.
            return Hash(_commitMsg, _revealRMsg, _revealIMsg);
        }

        private void Finish(byte[] transcriptHash)
        {
            _transcriptHash = transcriptHash;
            byte[] shared = PeerCrypto.X25519Agree(_ephemeral.PrivateKey, _peerEphemeralPub);
            _sessionKey = PeerCrypto.DeriveKey(shared, salt: transcriptHash, ConcatInfo(SessionInfo, transcriptHash));
            PeerCrypto.Zeroize(shared);

            byte[] sasHash = Hash(SasInfo, transcriptHash);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(sasHash.AsSpan(0, 4)) % 1_000_000u;
            _sas = v.ToString("D6");
        }

        private void PublishResult()
        {
            Result = new HandshakeResult
            {
                SessionKey = _sessionKey,
                Sas = _sas,
                PeerStaticPublicKey = _peerStaticPub,
                PeerFingerprint = PeerCrypto.Fingerprint(_peerStaticPub),
                PeerCapabilities = _peerCaps,
                IsInitiator = _isInitiator,
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static byte[] ConcatInfo(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static byte[] Hash(params byte[][] parts)
        {
            using var sha = SHA256.Create();
            foreach (var p in parts)
                if (p.Length > 0) sha.TransformBlock(p, 0, p.Length, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash;
        }

        private static byte[] Key(byte[] b, string what)
        {
            if (b == null || b.Length != PeerCrypto.KeySize)
                throw new HandshakeException($"Bad {what} key length.");
            return b;
        }

        private static byte[][] Decode(ReadOnlySpan<byte> data, int count, string what)
        {
            if (!Tlv.TryDecode(data, count, out var fields))
                throw new HandshakeException($"Malformed {what} message.");
            return fields;
        }

        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new HandshakeException(msg);
        }
    }

    /// <summary>Thrown on any malformed, out-of-order, or unauthenticated handshake step.</summary>
    public sealed class HandshakeException : Exception
    {
        public HandshakeException(string message) : base(message) { }
    }

    /// <summary>Length-prefixed field framing (u16 BE length + bytes) for the
    /// control-channel messages. Tolerant decode: returns false rather than
    /// throwing on truncation or a wrong field count, so a hostile control
    /// message fails the handshake closed.</summary>
    internal static class Tlv
    {
        public static byte[] Encode(params byte[][] fields)
        {
            int total = 0;
            foreach (var f in fields) total += 2 + f.Length;
            var buf = new byte[total];
            int o = 0;
            foreach (var f in fields)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)f.Length); o += 2;
                f.CopyTo(buf.AsSpan(o)); o += f.Length;
            }
            return buf;
        }

        public static bool TryDecode(ReadOnlySpan<byte> data, int expectedCount, out byte[][] fields)
        {
            fields = null;
            var list = new byte[expectedCount][];
            int o = 0;
            for (int i = 0; i < expectedCount; i++)
            {
                if (o + 2 > data.Length) return false;
                int len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(o, 2)); o += 2;
                if (o + len > data.Length) return false;
                list[i] = data.Slice(o, len).ToArray(); o += len;
            }
            if (o != data.Length) return false; // trailing garbage
            fields = list;
            return true;
        }
    }
}
