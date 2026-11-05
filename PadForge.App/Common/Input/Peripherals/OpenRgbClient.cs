using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One packet the server sent: its header's device and packet
    /// IDs, and its data.</summary>
    internal sealed record OpenRgbPacket(uint Device, uint Id, byte[] Data);

    /// <summary>What a wait for a packet came to.</summary>
    internal enum OpenRgbReceive
    {
        Packet,
        /// <summary>Nothing came inside the wait.</summary>
        Timeout,
        /// <summary>The connection is gone, or the server sent something
        /// that is not a packet.</summary>
        Closed,
    }

    /// <summary>A connection to an OpenRGB SDK server that carries whole
    /// packets. The worker owns it, and the crash path may send on it
    /// beside the worker, so a send is whole or not at all.</summary>
    internal interface IOpenRgbLink : IDisposable
    {
        /// <summary>Sends one whole packet. False when it did not go
        /// out.</summary>
        bool Send(byte[] packet);

        /// <summary>The next packet, waiting up to
        /// <paramref name="timeoutMs"/> for it.</summary>
        OpenRgbReceive Receive(int timeoutMs, out OpenRgbPacket packet);
    }

    /// <summary>
    /// The TCP connection to the OpenRGB SDK server on this PC (#557), at the
    /// address <see cref="OpenRgbServerPort"/> finds, which is on this PC and
    /// by default loopback, the address the server binds unless told
    /// otherwise (NetworkProtocol.h:30-33, OPENRGB_SDK_HOST).
    ///
    /// <para>PadForge speaks the protocol with a client of its own and does
    /// not take OpenRGB.NET, the client RGB.NET's OpenRGB provider uses
    /// (RGB.NET.Devices.OpenRGB.csproj:64, version 3.1.1). That client
    /// speaks versions 0 to 4 and throws on any other
    /// (OpenRGB.NET Models/ProtocolVersion.cs:79-87), so it would not
    /// read the zone flags a version 5 server sends. It waits for every
    /// reply with no time limit (OpenRgbConnection.cs:120-125), where
    /// PadForge's worker must not hang on a server that stopped answering,
    /// and it would add a package and a license to the three places
    /// PadForge credits its sources. PadForge needs six packets, which
    /// <see cref="OpenRgbProtocol"/> builds byte for byte from the server's
    /// own code.</para>
    /// </summary>
    internal sealed class OpenRgbTcpLink : IOpenRgbLink
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app: no connection is made to the server.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>The bound of one send, so a server that stopped reading
        /// cannot hold the worker or the crash path.</summary>
        internal const int SendTimeoutMs = 250;

        private readonly TcpClient _client;
        private readonly Socket _socket;
        private readonly object _sendGate = new();
        private readonly List<byte> _buffer = new();
        private readonly byte[] _chunk = new byte[16384];
        private int _closed;

        private OpenRgbTcpLink(TcpClient client)
        {
            _client = client;
            _socket = client.Client;
        }

        /// <summary>Connects to the server at <paramref name="address"/>,
        /// waiting up to <paramref name="timeoutMs"/>. Null when nothing
        /// answers.</summary>
        internal static IOpenRgbLink Connect(OpenRgbServerAddress address, int timeoutMs)
        {
            if (OffLimitsForTest) return null;
            var client = new TcpClient(address.Host.AddressFamily);
            try
            {
                // Packets go out as they are written, the setting
                // OpenRGB.NET makes for the same server
                // (OpenRgbConnection.cs:31-32).
                client.NoDelay = true;
                client.SendTimeout = SendTimeoutMs;
                var connect = client.ConnectAsync(address.Host, address.Port);
                // A connection given up on still ends in a fault once the
                // client is disposed, and that fault is read here.
                _ = connect.ContinueWith(t => _ = t.Exception,
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                    | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
                if (!connect.Wait(timeoutMs) || !client.Connected)
                {
                    client.Dispose();
                    return null;
                }
                return new OpenRgbTcpLink(client);
            }
            catch
            {
                try { client.Dispose(); } catch { }
                return null;
            }
        }

        public bool Send(byte[] packet)
        {
            if (packet == null || Volatile.Read(ref _closed) != 0) return false;
            lock (_sendGate)
            {
                try
                {
                    int sent = 0;
                    while (sent < packet.Length)
                    {
                        int n = _socket.Send(packet, sent, packet.Length - sent, SocketFlags.None);
                        if (n <= 0) return false;
                        sent += n;
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public OpenRgbReceive Receive(int timeoutMs, out OpenRgbPacket packet)
        {
            packet = null;
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (true)
            {
                if (Volatile.Read(ref _closed) != 0) return OpenRgbReceive.Closed;
                var taken = Take(out packet);
                if (taken != OpenRgbReceive.Timeout) return taken;
                long left = deadline - Environment.TickCount64;
                if (left < 0) left = 0;
                try
                {
                    if (!_socket.Poll((int)Math.Min(left * 1000, int.MaxValue), SelectMode.SelectRead))
                        return OpenRgbReceive.Timeout;
                    int n = _socket.Receive(_chunk, 0, _chunk.Length, SocketFlags.None);
                    if (n <= 0) return OpenRgbReceive.Closed;
                    for (int i = 0; i < n; i++) _buffer.Add(_chunk[i]);
                }
                catch
                {
                    return OpenRgbReceive.Closed;
                }
            }
        }

        /// <summary>A whole packet from the bytes received so far, Timeout
        /// when they do not hold one yet, and Closed for bytes that are not
        /// a packet or one longer than the server ever sends.</summary>
        private OpenRgbReceive Take(out OpenRgbPacket packet)
        {
            packet = null;
            if (_buffer.Count < OpenRgbProtocol.HeaderLength) return OpenRgbReceive.Timeout;
            var header = new byte[OpenRgbProtocol.HeaderLength];
            _buffer.CopyTo(0, header, 0, header.Length);
            if (!OpenRgbProtocol.TryReadHeader(header, out uint device, out uint id, out uint size)
                || size > OpenRgbProtocol.MaxPacket)
                return OpenRgbReceive.Closed;
            if (_buffer.Count < OpenRgbProtocol.HeaderLength + size) return OpenRgbReceive.Timeout;
            var data = new byte[size];
            _buffer.CopyTo(OpenRgbProtocol.HeaderLength, data, 0, (int)size);
            _buffer.RemoveRange(0, OpenRgbProtocol.HeaderLength + (int)size);
            packet = new OpenRgbPacket(device, id, data);
            return OpenRgbReceive.Packet;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _client.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// One conversation with an OpenRGB SDK server (#557), in the order
    /// OpenRGB's own client opens one: the version request, a wait of up
    /// to a second for the reply, the lower of the two versions, then the
    /// client's name (NetworkClient.cpp:1759-1855). A server that sends no
    /// reply speaks version 0 (lines 1786-1803, and
    /// Documentation/OpenRGBSDK.md:109).
    ///
    /// <para>Below version 6 the server sends nothing unasked but the
    /// device list changed notice, after which the indexes the client holds
    /// may name other controllers, and the client reads the list again
    /// (Documentation/OpenRGBSDK.md:115-117). Every wait here notes that
    /// notice (<see cref="ListChanged"/>) and reads on.</para>
    /// </summary>
    internal sealed class OpenRgbSession : IDisposable
    {
        /// <summary>The wait for the version reply, OpenRGB's own client's
        /// 200 waits of 5 ms (NetworkClient.cpp:1781-1803).</summary>
        internal const int VersionWaitMs = 1000;

        /// <summary>The wait for any other reply. A server on the same PC
        /// answers within milliseconds.</summary>
        internal const int ReplyWaitMs = 1000;

        /// <summary>The name the server lists PadForge under.</summary>
        internal const string ClientName = "PadForge";

        private readonly IOpenRgbLink _link;
        private readonly int _replyWaitMs;
        private int _disposed;

        private OpenRgbSession(IOpenRgbLink link, uint version, int replyWaitMs)
        {
            _link = link;
            Version = version;
            _replyWaitMs = replyWaitMs;
        }

        /// <summary>The version both sides speak.</summary>
        public uint Version { get; }

        /// <summary>Set when the server said its device list changed. The
        /// reader clears it when it has read the list again.</summary>
        public bool ListChanged { get; set; }

        /// <summary>The connection, for the crash path's sends.</summary>
        internal IOpenRgbLink Link => _link;

        /// <summary>Opens a conversation on <paramref name="link"/>. Null
        /// when the link failed; the link is then disposed.</summary>
        public static OpenRgbSession Begin(IOpenRgbLink link, int versionWaitMs = VersionWaitMs,
            int replyWaitMs = ReplyWaitMs)
        {
            if (link == null) return null;
            bool listChanged = false;
            uint version = 0;
            if (!link.Send(OpenRgbProtocol.ProtocolVersionRequest(OpenRgbProtocol.ClientVersion)))
            {
                link.Dispose();
                return null;
            }
            var reply = Await(link, OpenRgbProtocol.RequestProtocolVersion, 0, versionWaitMs, ref listChanged,
                out var packet);
            if (reply == OpenRgbReceive.Closed)
            {
                link.Dispose();
                return null;
            }
            if (reply == OpenRgbReceive.Packet)
            {
                long server = OpenRgbProtocol.ReadVersion(packet.Data);
                if (server >= 0) version = (uint)Math.Min(server, OpenRgbProtocol.ClientVersion);
            }
            if (!link.Send(OpenRgbProtocol.ClientName(ClientName)))
            {
                link.Dispose();
                return null;
            }
            return new OpenRgbSession(link, version, replyWaitMs) { ListChanged = listChanged };
        }

        /// <summary>Every controller the server holds, in its order, which
        /// is the index each is addressed by. Null when the server stopped
        /// answering or a reply could not be read. A device list changed
        /// notice that comes during the read is kept in
        /// <see cref="ListChanged"/> for the next one.</summary>
        public List<OpenRgbController> ReadControllers()
        {
            // A notice that comes while the list is read means the list
            // read may be stale, so it stands for the next read.
            ListChanged = false;
            bool changed = false;
            if (!_link.Send(OpenRgbProtocol.ControllerCountRequest())) return null;
            if (Await(_link, OpenRgbProtocol.RequestControllerCount, null, _replyWaitMs, ref changed, out var packet)
                != OpenRgbReceive.Packet)
                return null;
            int count = OpenRgbProtocol.ReadCount(packet.Data);
            if (count < 0) return null;
            ListChanged = changed;
            var controllers = new List<OpenRgbController>(Math.Min(count, 256));
            for (uint i = 0; i < (uint)count; i++)
            {
                var controller = ReadController(i);
                if (controller == null) return null;
                controllers.Add(controller);
            }
            return controllers;
        }

        /// <summary>One controller by its index. Null when the server
        /// stopped answering or the reply could not be read.</summary>
        public OpenRgbController ReadController(uint index)
        {
            bool changed = ListChanged;
            if (!_link.Send(OpenRgbProtocol.ControllerDataRequest(index, Version))) return null;
            var reply = Await(_link, OpenRgbProtocol.RequestControllerData, index, _replyWaitMs, ref changed, out var packet);
            ListChanged = changed;
            return reply == OpenRgbReceive.Packet ? OpenRgbProtocol.ReadController(packet.Data, Version) : null;
        }

        public bool UpdateMode(uint index, int modeIndex, OpenRgbMode mode)
            => _link.Send(OpenRgbProtocol.UpdateModeRequest(index, modeIndex, mode, Version));

        public bool UpdateLeds(uint index, IReadOnlyList<uint> colors)
            => _link.Send(OpenRgbProtocol.UpdateLedsRequest(index, colors));

        /// <summary>Reads what the server sent unasked, without waiting.
        /// False when the connection is gone.</summary>
        public bool Drain()
        {
            while (true)
            {
                var got = _link.Receive(0, out var packet);
                if (got == OpenRgbReceive.Closed) return false;
                if (got == OpenRgbReceive.Timeout) return true;
                if (packet.Id == OpenRgbProtocol.DeviceListUpdated) ListChanged = true;
            }
        }

        /// <summary>Waits for the reply with packet ID <paramref name="id"/>,
        /// for the device <paramref name="device"/> where one is named,
        /// noting a device list changed notice on the way and passing over
        /// any other packet.</summary>
        private static OpenRgbReceive Await(IOpenRgbLink link, uint id, uint? device, int timeoutMs,
            ref bool listChanged, out OpenRgbPacket packet)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                int left = (int)Math.Max(0, deadline - Environment.TickCount64);
                var got = link.Receive(left, out packet);
                if (got != OpenRgbReceive.Packet) return got;
                if (packet.Id == OpenRgbProtocol.DeviceListUpdated)
                {
                    listChanged = true;
                    continue;
                }
                if (packet.Id == id && (device == null || packet.Device == device.Value)) return got;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _link.Dispose(); } catch { }
        }
    }
}
