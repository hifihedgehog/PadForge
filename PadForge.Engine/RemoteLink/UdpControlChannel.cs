using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>The unreliable datagram path the ARQ runs over. A real
    /// implementation sends on the punched UDP socket to the peer endpoint and
    /// feeds inbound control datagrams to the channel's handler; the tests use
    /// an in-process pair with loss and reorder. Keeping this abstract is what
    /// makes the reliable channel deterministically testable without sockets.</summary>
    public interface IDatagramTransport
    {
        Task SendAsync(byte[] datagram, CancellationToken ct);
        /// <summary>Set by the channel so the owner's receive loop can hand it
        /// inbound control datagrams.</summary>
        Action<byte[]> OnDatagram { get; set; }
    }

    /// <summary>
    /// <see cref="ILinkControlChannel"/> over an unreliable datagram transport
    /// (#294 step 2): a small stop-and-wait-per-message ARQ that gives the
    /// handshake the reliable, ordered, framed duplex channel it already has
    /// over TCP. The existing in-memory channel contract (LinkConnectionTests'
    /// MemChannel) defines the semantics this must satisfy, so the same
    /// LinkConnection handshake runs unmodified over the punched path.
    ///
    /// Wire (all datagrams carry a one-byte tag so they demux from LinkSession
    /// data datagrams on the shared socket; LinkSession's first byte is
    /// (type &lt;&lt; 4)|epoch with type 1..7, never 0xC0/0xC1):
    ///   DATA: 0xC0 | u32 channelId (BE) | u32 seq (BE) | payload
    ///   ACK:  0xC1 | u32 channelId (BE) | u32 seq (BE)
    ///
    /// The channel id (derived from the punch nonce, unique per connection
    /// attempt) scopes sequences to THIS channel instance, so a delayed
    /// datagram from a previous connection on the same endpoint pair can never
    /// ACK or occupy a fresh channel's sequence space.
    ///
    /// Each direction has an independent monotonic sequence. The sender holds a
    /// message until its ACK arrives, retransmitting on a timer; the receiver
    /// ACKs every DATA and delivers each seq exactly once, in order. The whole
    /// inbound path runs under one lock so the channel is correct even when the
    /// transport delivers callbacks concurrently. Small and rare traffic (a
    /// handshake plus device lists), so stop-and-wait's one in-flight message
    /// per direction is ample and keeps the logic auditable.
    ///
    /// FAILURE CONTRACT: a send that is cancelled or faults mid-flight leaves
    /// the peer's expected sequence unknowable, so the channel POISONS itself
    /// (all later operations throw). That matches how the handshake uses it: a
    /// cancelled handshake abandons the connection attempt outright, exactly as
    /// a TCP connection is abandoned after a cancelled write.
    /// </summary>
    public sealed class UdpControlChannel : ILinkControlChannel, IDisposable
    {
        public const byte TagData = 0xC0;
        public const byte TagAck = 0xC1;
        private const int HeaderLen = 9; // tag + channelId + seq
        /// <summary>Inbound messages queued but not yet read. Beyond this the
        /// receiver stops ACKing (the peer retransmits), bounding memory
        /// against a flooding peer.</summary>
        private const int MaxQueuedMessages = 64;

        private readonly IDatagramTransport _transport;
        private readonly TimeSpan _retransmit;
        private readonly int _maxMessage;
        private readonly uint _channelId;

        // Send side (one in-flight message at a time, per direction).
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private uint _sendSeq;
        private TaskCompletionSource<bool> _ackTcs;
        private uint _awaitingAck;

        // Receive side: in-order delivery queue + dedup of the next expected
        // seq. Bounded so a peer cannot grow memory without us reading.
        private readonly Channel<byte[]> _delivered = Channel.CreateBounded<byte[]>(MaxQueuedMessages);
        private uint _expectedSeq;
        private readonly object _recvLock = new();
        private volatile bool _dead;

        /// <param name="channelId">Per-connection id scoping the sequence space
        /// (derive from the punch nonce so both sides agree and stale traffic
        /// from an earlier attempt is rejected).</param>
        public UdpControlChannel(IDatagramTransport transport, TimeSpan? retransmit = null,
            int maxMessage = 60_000, uint channelId = 0)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _retransmit = retransmit ?? TimeSpan.FromMilliseconds(300);
            // Cap under the IPv4 UDP payload limit (65,507) with header room,
            // so an accepted message can always actually be sent.
            _maxMessage = Math.Min(maxMessage, 65_000);
            _channelId = channelId;
            _transport.OnDatagram = OnDatagram;
        }

        /// <summary>A stable channel id from a shared punch nonce (first four
        /// bytes, big-endian), so both peers compute the same value.</summary>
        public static uint ChannelIdFromNonce(byte[] nonce)
            => nonce != null && nonce.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(nonce) : 0u;

        public async Task SendAsync(byte[] message, CancellationToken ct)
        {
            if (_dead) throw new LinkConnectionException("Control channel is closed.");
            if (message.Length > _maxMessage) throw new LinkConnectionException("Control message too large.");
            // One outstanding message per direction: the next SendAsync waits
            // for the current one's ACK, which is what makes the receiver's
            // single-expected-seq dedup correct.
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            bool acked = false;
            try
            {
                if (_dead) throw new LinkConnectionException("Control channel is closed.");
                uint seq = _sendSeq;
                var dg = new byte[HeaderLen + message.Length];
                dg[0] = TagData;
                BinaryPrimitives.WriteUInt32BigEndian(dg.AsSpan(1), _channelId);
                BinaryPrimitives.WriteUInt32BigEndian(dg.AsSpan(5), seq);
                message.CopyTo(dg, HeaderLen);

                var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_recvLock) { _ackTcs = ackTcs; _awaitingAck = seq; }

                while (!ct.IsCancellationRequested && !_dead)
                {
                    await _transport.SendAsync(dg, ct).ConfigureAwait(false);
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timer.CancelAfter(_retransmit);
                    try
                    {
                        acked = await ackTcs.Task.WaitAsync(timer.Token).ConfigureAwait(false);
                        if (acked) { _sendSeq = seq + 1; return; }
                        // The TCS only ever completes true or cancelled; a
                        // false result cannot occur, but fall through safely.
                        break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_dead)
                    {
                        // retransmit timeout: loop and resend the same seq
                    }
                    catch (OperationCanceledException)
                    {
                        break; // caller cancel or dispose: poisoned below
                    }
                }
                if (_dead) throw new LinkConnectionException("Control channel is closed.");
                ct.ThrowIfCancellationRequested();
                throw new LinkConnectionException("Control channel send failed.");
            }
            finally
            {
                lock (_recvLock) { _ackTcs = null; }
                if (!acked)
                {
                    // A message may or may not have reached the peer; the
                    // shared sequence state is unknowable. Poison the channel
                    // rather than silently desynchronize (adversarial review
                    // finding 12: a burned sequence turned every later send
                    // into an ACKed-but-dropped silent loss).
                    Poison();
                }
                _sendGate.Release();
            }
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            try { return await _delivered.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (ChannelClosedException) { throw new LinkConnectionException("Control channel is closed."); }
        }

        /// <summary>Feeds one inbound control datagram from the owner's receive
        /// loop. Ignores anything without a control tag or with another
        /// channel's id. The whole path is serialized under one lock so
        /// concurrent transport callbacks cannot lose or reorder messages
        /// (adversarial review findings 10/11).</summary>
        public void OnDatagram(byte[] dg)
        {
            if (_dead || dg == null || dg.Length < HeaderLen) return;
            byte tag = dg[0];
            if (tag != TagData && tag != TagAck) return;
            uint chan = BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(1));
            if (chan != _channelId) return; // stale traffic from an earlier attempt
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(5));

            if (tag == TagAck)
            {
                TaskCompletionSource<bool> tcs = null;
                lock (_recvLock)
                    if (_ackTcs != null && seq == _awaitingAck) tcs = _ackTcs;
                tcs?.TrySetResult(true);
                return;
            }

            // Oversized DATA is a protocol violation; drop without ACK.
            if (dg.Length - HeaderLen > _maxMessage) return;

            bool ack = false;
            lock (_recvLock)
            {
                if (seq == _expectedSeq)
                {
                    // Queue first, advance only on success: a full queue means
                    // no ACK, so the peer retransmits later instead of the
                    // message vanishing (finding 17's bound).
                    var payload = dg.AsSpan(HeaderLen).ToArray();
                    if (_delivered.Writer.TryWrite(payload))
                    {
                        _expectedSeq++;
                        ack = true;
                    }
                }
                else if (SeqBefore(seq, _expectedSeq))
                {
                    // Already delivered: re-ACK so the retransmitting sender
                    // stops. Never re-deliver.
                    ack = true;
                }
                // seq beyond expected cannot happen under stop-and-wait (the
                // peer holds its next message until this one's ACK arrives);
                // drop without ACK.
            }
            if (ack)
            {
                var ackDg = new byte[HeaderLen];
                ackDg[0] = TagAck;
                BinaryPrimitives.WriteUInt32BigEndian(ackDg.AsSpan(1), _channelId);
                BinaryPrimitives.WriteUInt32BigEndian(ackDg.AsSpan(5), seq);
                _ = _transport.SendAsync(ackDg, CancellationToken.None);
            }
        }

        // RFC 1982-style: is a strictly before b in u32 serial space.
        private static bool SeqBefore(uint a, uint b) => (int)(a - b) < 0;

        /// <summary>True when a datagram's first byte is a control tag (so a
        /// shared receive loop can split control from LinkSession data).</summary>
        public static bool IsControlDatagram(ReadOnlySpan<byte> dg)
            => dg.Length >= 1 && (dg[0] == TagData || dg[0] == TagAck);

        private void Poison()
        {
            _dead = true;
            _delivered.Writer.TryComplete();
            TaskCompletionSource<bool> tcs;
            lock (_recvLock) tcs = _ackTcs;
            tcs?.TrySetCanceled();
        }

        public void Dispose() => Poison();
    }
}
