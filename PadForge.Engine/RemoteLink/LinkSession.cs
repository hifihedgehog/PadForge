using System;
using System.Buffers.Binary;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>Datagram kind on the unreliable input/feedback channel.</summary>
    public enum LinkMessageType : byte
    {
        Input = 1,     // an absolute CustomInputState frame (host-bound)
        Haptic = 2,    // a rumble/feedback update (peer-bound)
        Keepalive = 3, // liveness when no input is flowing
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

        private uint _sendCounter;

        /// <param name="sessionKey">32-byte AEAD key from the handshake key schedule.</param>
        /// <param name="isInitiator">Selects this side's nonce salt so the two directions never collide.</param>
        /// <param name="epoch">Protocol version epoch agreed at SESSION_OPEN (low nibble of the type byte).</param>
        public LinkSession(byte[] sessionKey, bool isInitiator, byte epoch = 1)
        {
            if (sessionKey == null || sessionKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException($"Session key must be {PeerCrypto.KeySize} bytes.", nameof(sessionKey));
            _key = (byte[])sessionKey.Clone();
            _sendSalt = isInitiator ? 0u : 1u;
            _recvSalt = isInitiator ? 1u : 0u;
            _epoch = (byte)(epoch & 0x0F);
        }

        /// <summary>Number of datagrams sealed so far (the next sequence). The transport
        /// rekeys before this nears 2^32 — nonce reuse under ChaCha20-Poly1305 is fatal.</summary>
        public uint SendCounter => _sendCounter;

        /// <summary>Seal one payload into a ready-to-send datagram.</summary>
        public byte[] Seal(LinkMessageType type, byte slotId, ulong timestampUs, ReadOnlySpan<byte> payload)
        {
            uint seq = _sendCounter++;
            var datagram = new byte[HeaderSize + payload.Length + PeerCrypto.TagSize];
            WriteHeader(datagram, type, slotId, seq, timestampUs);

            var nonce = PeerCrypto.BuildNonce(_sendSalt, seq);
            byte[] sealedBytes = PeerCrypto.Seal(_key, nonce, datagram.AsSpan(0, HeaderSize), payload);
            sealedBytes.CopyTo(datagram.AsSpan(HeaderSize));
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

            var nonce = PeerCrypto.BuildNonce(_recvSalt, seq);
            if (!PeerCrypto.Open(_key, nonce, datagram.Slice(0, HeaderSize), datagram.Slice(HeaderSize), out byte[] opened))
                return false; // tag failed — forged or corrupt

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
