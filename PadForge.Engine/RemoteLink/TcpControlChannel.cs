using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// <see cref="ILinkControlChannel"/> over a TCP stream: length-prefixed
    /// (u32 big-endian) message framing with a hard cap, so a hostile peer can't
    /// announce a giant frame to exhaust memory. The control channel is rare,
    /// reliable, and ordered, which is exactly what the handshake needs.
    /// </summary>
    public sealed class TcpControlChannel : ILinkControlChannel
    {
        private const int MaxMessage = 64 * 1024; // handshake + device list are tiny
        private readonly Stream _stream;
        // Separate send / receive length buffers. One shared field was
        // written by SendAsync's prefix and read by ReceiveAsync's prefix,
        // so any overlap between a send and a pending receive corrupted the
        // framing of both. The handshake happens to alternate today, but a
        // duplex channel must not depend on that, and four bytes is not a
        // price worth the hazard (round 34).
        private readonly byte[] _sendLenBuf = new byte[4];
        private readonly byte[] _recvLenBuf = new byte[4];

        public TcpControlChannel(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        public async Task SendAsync(byte[] message, CancellationToken ct)
        {
            if (message.Length > MaxMessage) throw new LinkConnectionException("Control message too large.");
            BinaryPrimitives.WriteUInt32BigEndian(_sendLenBuf, (uint)message.Length);
            await _stream.WriteAsync(_sendLenBuf, ct);
            await _stream.WriteAsync(message, ct);
            await _stream.FlushAsync(ct);
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            await ReadExactlyAsync(_recvLenBuf, 4, ct);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(_recvLenBuf);
            if (len > MaxMessage) throw new LinkConnectionException("Control message too large.");
            var buf = new byte[len];
            await ReadExactlyAsync(buf, (int)len, ct);
            return buf;
        }

        private async Task ReadExactlyAsync(byte[] buf, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await _stream.ReadAsync(buf.AsMemory(read, count - read), ct);
                if (n == 0) throw new LinkConnectionException("Control channel closed.");
                read += n;
            }
        }
    }
}
