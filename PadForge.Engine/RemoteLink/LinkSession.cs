using System;
using System.Buffers.Binary;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>Datagram kind on the unreliable input/feedback channel.</summary>
    public enum LinkMessageType : byte
    {
        Input = 1,     // an absolute CustomInputState frame (host-bound)
        Haptic = 2,    // legacy scalar rumble update (superseded by Output)
        Keepalive = 3, // liveness when no input is flowing
        Output = 4,    // a tagged output-effect frame (Sony effect / Vibration / Wheel),
                       // consumer -> owner, applied to the owner's physical device (#138)
        Audio = 5,     // a speaker PCM block, consumer -> owner, rendered to the pad speaker
        DeviceList = 6,// owner -> consumer: the owner's CURRENT exposed device set (stable
                       // slot + online per device), re-sent on change and periodically so a
                       // device hot-plugged after connect appears/disappears live (#138)
        SourceDemand = 7,// consumer -> owner: "a live mapping on my side is polling this
                       // device's demand-gated source", so the owner powers the hardware
                       // that source needs. Demand latches are machine-local by design
                       // (SourceCoercion stamps them where the mapping evaluates), so a
                       // consumer's NFC binding could never arm the owner's reader
                       // without this lane and silently never fired (#241, audit
                       // 2026-07-24). Payload: [0] = demand kind (1 = NFC reader).
    }

    /// <summary>
    /// Per-session datagram sealing for the Remote Link input/feedback channel
    /// (issue #138). Wraps each codec payload in a small authenticated header and
    /// an AEAD seal under the session key established by the handshake.
    ///
    /// Wire layout (header is authenticated-but-not-encrypted AAD; payload is sealed):
    ///   [0]      (type &lt;&lt; 4) | epoch
    ///   [1]      slot/device id within the session
    ///   [2..5]   u32 sequence (LE) — RFC 1982 serial, also the AEAD nonce counter
    ///   [6..13]  u64 send timestamp (LE, microseconds, QPC-derived)
    ///   [14..]   ChaCha20-Poly1305(payload) + 16-byte tag
    ///
    /// The sequence doubles as the per-direction nonce counter, so it never repeats
    /// within a session under one key. Nonce space is disjoint per direction (a
    /// distinct salt per side), so the two peers can share one key without colliding.
    /// Replay protection: the window advances only after the tag verifies.
    /// </summary>
    public sealed class LinkSession
    {
        public const int HeaderSize = 14;

        private readonly byte[] _key;
        private readonly uint _sendSalt;
        private readonly uint _recvSalt;
        private readonly byte _epoch;
        private readonly AntiReplayWindow _replay = new();

        private long _sendCounter; // sealed-count; the wire sequence is (count - 1)

        /// <param name="sessionKey">32-byte AEAD key from the handshake key schedule.</param>
        /// <param name="isInitiator">Selects this side's nonce salt so the two directions never collide.</param>
        /// <param name="epoch">Protocol version epoch agreed at SESSION_OPEN (low nibble of the type byte).</param>
        public LinkSession(byte[] sessionKey, bool isInitiator, byte epoch = 1)
        {
            if (sessionKey == null || sessionKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException($"Session key must be {PeerCrypto.KeySize} bytes.", nameof(sessionKey));
            _key = (byte[])sessionKey.Clone();
            _keyParam = new Org.BouncyCastle.Crypto.Parameters.KeyParameter(_key);
            _sendSalt = isInitiator ? 0u : 1u;
            _recvSalt = isInitiator ? 1u : 0u;
            _epoch = (byte)(epoch & 0x0F);
        }

        // Per-session AEAD cipher reuse (cite-verified against bc-csharp at
        // tag release-2.6.2, the exact referenced package version: Init
        // fully resets all per-message state including the one-time
        // Poly1305 key, recovers from a failed-tag DoFinal, and BC's own
        // TLS record layer runs one long-lived cipher per direction with
        // Init per record). Replaces a fresh cipher graph + KeyParameter +
        // AeadParameters + two ToArray copies per datagram at up to 125 Hz
        // per device per peer, both directions. Seal has CONCURRENT callers
        // (stream tick, keepalive timer, output relay, audio, device-list
        // push) so the seal cipher works under its lock; Open's single
        // UDP-loop caller gets the same guard for the uncontended price.
        // PeerCrypto.Seal/Open stay as the one-shot reference helpers.
        private readonly Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305 _sealCipher = new();
        private readonly Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305 _openCipher = new();
        private readonly Org.BouncyCastle.Crypto.Parameters.KeyParameter _keyParam;
        private readonly byte[] _sealNonce = new byte[PeerCrypto.NonceSize];
        private readonly byte[] _openNonce = new byte[PeerCrypto.NonceSize];
        private readonly object _sealLock = new();
        private readonly object _openLock = new();

        /// <summary>Number of datagrams sealed so far. Seal hard-stops before the
        /// sequence wraps 2^32 (nonce reuse under ChaCha20-Poly1305 is fatal), forcing
        /// a fresh handshake rather than silently reusing a nonce.</summary>
        public uint SendCounter => (uint)System.Threading.Interlocked.Read(ref _sendCounter);

        /// <summary>Seal one payload into a ready-to-send datagram. Thread-safe: the
        /// sequence is allocated atomically so concurrent senders never share a nonce.</summary>
        public byte[] Seal(LinkMessageType type, byte slotId, ulong timestampUs, ReadOnlySpan<byte> payload)
        {
            long n = System.Threading.Interlocked.Increment(ref _sendCounter);
            if (n > uint.MaxValue)
                throw new LinkConnectionException("Send counter exhausted — rekey required to avoid nonce reuse.");
            uint seq = (uint)(n - 1);
            var datagram = new byte[HeaderSize + payload.Length + PeerCrypto.TagSize];
            WriteHeader(datagram, type, slotId, seq, timestampUs);

            lock (_sealLock)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_sealNonce.AsSpan(0), _sendSalt);
                BinaryPrimitives.WriteUInt64LittleEndian(_sealNonce.AsSpan(4), seq);
                _sealCipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(_keyParam, _sealNonce));
                _sealCipher.ProcessAadBytes(datagram.AsSpan(0, HeaderSize));
                int w = _sealCipher.ProcessBytes(payload, datagram.AsSpan(HeaderSize));
                _sealCipher.DoFinal(datagram.AsSpan(HeaderSize + w));
            }
            return datagram;
        }

        /// <summary>
        /// Open a received datagram. Returns false on a short frame, an epoch
        /// mismatch, a failed tag, or a replayed/stale sequence — fail closed,
        /// nothing trusted. The window advances only on a verified, fresh datagram.
        /// </summary>
        public bool Open(ReadOnlySpan<byte> datagram, out LinkMessageType type, out byte slotId, out ulong timestampUs, out byte[] payload)
        {
            type = default; slotId = 0; timestampUs = 0; payload = Array.Empty<byte>();
            if (datagram.Length < HeaderSize + PeerCrypto.TagSize) return false;

            byte typeEpoch = datagram[0];
            byte epoch = (byte)(typeEpoch & 0x0F);
            if (epoch != _epoch) return false;
            var msgType = (LinkMessageType)(byte)(typeEpoch >> 4);

            slotId = datagram[1];
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(2, 4));
            ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(6, 8));

            var ct = datagram.Slice(HeaderSize);
            byte[] opened;
            lock (_openLock)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_openNonce.AsSpan(0), _recvSalt);
                BinaryPrimitives.WriteUInt64LittleEndian(_openNonce.AsSpan(4), seq);
                try
                {
                    _openCipher.Init(false, new Org.BouncyCastle.Crypto.Parameters.ParametersWithIV(_keyParam, _openNonce));
                    _openCipher.ProcessAadBytes(datagram.Slice(0, HeaderSize));
                    // DecInit GetOutputSize is exact (ct minus tag), so the
                    // buffer never needs a trailing slice-copy.
                    opened = new byte[_openCipher.GetOutputSize(ct.Length)];
                    int w = _openCipher.ProcessBytes(ct, opened);
                    w += _openCipher.DoFinal(opened.AsSpan(w));
                    if (w != opened.Length) opened = opened[..w];
                }
                catch
                {
                    // AEAD tag mismatch or malformed input: forged or
                    // corrupt. The next Init fully rebuilds cipher state.
                    return false;
                }
            }

            // Verify-then-window: a forged sequence can never advance replay state.
            if (!_replay.CheckAndUpdate(seq)) return false; // duplicate or older than the window

            type = msgType;
            timestampUs = ts;
            payload = opened;
            return true;
        }

        private void WriteHeader(Span<byte> dst, LinkMessageType type, byte slotId, uint seq, ulong timestampUs)
        {
            dst[0] = (byte)(((byte)type << 4) | _epoch);
            dst[1] = slotId;
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(2, 4), seq);
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(6, 8), timestampUs);
        }
    }
}
