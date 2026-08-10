using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    // Minimal STUN Binding client for NAT traversal (#294). The RFC 5389
    // message layout, the 0x2112A442 magic cookie, and the XOR-MAPPED-ADDRESS
    // decode are vendored from SIPSorcery (BSD-3-Clause, retained below) rather
    // than taking the whole NuGet, per PadForge's minimal-deps preference. Only
    // the client half is needed: build a Binding request, parse the mapped
    // endpoint out of the response.
    //
    // ---------------------------------------------------------------------------
    // Portions derived from SIPSorcery (src/net/STUN): STUNHeader.cs,
    // STUNMessage.cs, STUNXORAddressAttribute.cs.
    //
    // BSD 3-Clause "New" or "Revised" License
    // Copyright (c) 2010 Aaron Clauson (aaron@sipsorcery.com)
    //
    // Redistribution and use in source and binary forms, with or without
    // modification, are permitted provided that the conditions of the BSD
    // 3-Clause License are met. See the SIPSorcery LICENSE.md.
    // ---------------------------------------------------------------------------

    /// <summary>The public endpoint a STUN server observed for our socket, plus
    /// the NAT-shape verdict from probing two servers.</summary>
    public sealed class StunResult
    {
        public IPEndPoint PublicEndpoint { get; init; }
        /// <summary>True when two servers reported DIFFERENT mapped ports for the
        /// same socket: endpoint-dependent (symmetric) NAT, where plain UDP hole
        /// punching fails. The UI pre-warns instead of failing after a timeout.</summary>
        public bool IsHardNat { get; init; }
    }

    /// <summary>
    /// Learns a socket's public endpoint via STUN Binding requests (#294 step 1).
    /// The query MUST reuse the very socket the punch will use, because the NAT
    /// mapping is per (socket, local port): a fresh socket learns a different
    /// mapping than the one LinkServer bound. So this takes a bound
    /// <see cref="Socket"/> and sends from it directly.
    /// </summary>
    public static class StunClient
    {
        public const uint MagicCookie = 0x2112A442;
        private const ushort BindingRequest = 0x0001;
        private const ushort AttrMappedAddress = 0x0001;
        private const ushort AttrXorMappedAddress = 0x0020;
        private const int HeaderSize = 20;

        /// <summary>Free public STUN servers, queried in order with fallback.
        /// Two distinct operators so the hard-NAT probe compares independent
        /// observations.</summary>
        public static readonly (string Host, int Port)[] DefaultServers =
        {
            ("stun.l.google.com", 19302),
            ("stun.cloudflare.com", 3478),
            ("stun.nextcloud.com", 3478),
        };

        /// <summary>Builds a STUN Binding request with a fresh 96-bit
        /// transaction id (returned so the caller can match the response and
        /// un-XOR the address).</summary>
        public static byte[] BuildBindingRequest(out byte[] transactionId)
        {
            transactionId = new byte[12];
            RandomNumberGenerator.Fill(transactionId);
            var msg = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(0), BindingRequest);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), 0); // no attributes
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
            transactionId.CopyTo(msg, 8);
            return msg;
        }

        /// <summary>Parses the mapped endpoint out of a STUN Binding response.
        /// Prefers XOR-MAPPED-ADDRESS (RFC 5389) and falls back to the legacy
        /// MAPPED-ADDRESS (RFC 3489). Returns null when the buffer is not a
        /// matching success response. IPv4 only, which is all the punch uses.</summary>
        public static IPEndPoint ParseBindingResponse(ReadOnlySpan<byte> buf, byte[] transactionId)
        {
            if (buf.Length < HeaderSize) return null;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(0));
            if (type != 0x0101) return null; // Binding success response
            uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(4));
            if (cookie != MagicCookie) return null;
            // Transaction id must match the request we sent from this socket.
            if (transactionId != null)
                for (int i = 0; i < 12; i++)
                    if (buf[8 + i] != transactionId[i]) return null;

            int len = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(2));
            int pos = HeaderSize;
            int end = Math.Min(buf.Length, HeaderSize + len);
            // Scan ALL attributes and genuinely prefer XOR-MAPPED-ADDRESS:
            // attribute order is not guaranteed, and a legacy MAPPED-ADDRESS
            // appearing first can carry a NAT-ALG-rewritten or private value
            // (adversarial review finding 7). MAPPED is the fallback only.
            IPEndPoint mappedFallback = null;
            while (pos + 4 <= end)
            {
                ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(pos));
                int attrLen = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(pos + 2));
                int valPos = pos + 4;
                // Bound by the DECLARED message end, never the raw buffer: a
                // response must not smuggle an attribute past its own length
                // field (finding 6).
                if (valPos + attrLen > end) break;

                if (attrType == AttrXorMappedAddress && attrLen >= 8)
                {
                    // family = value[1]; only IPv4 (0x01) is handled here.
                    if (buf[valPos + 1] == 0x01)
                    {
                        ushort xorPort = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(valPos + 2));
                        int port = xorPort ^ (ushort)(MagicCookie >> 16);
                        uint xorAddr = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(valPos + 4));
                        uint addr = xorAddr ^ MagicCookie;
                        var ipBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(ipBytes, addr);
                        return new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }
                else if (attrType == AttrMappedAddress && attrLen >= 8 && mappedFallback == null)
                {
                    if (buf[valPos + 1] == 0x01)
                    {
                        int port = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(valPos + 2));
                        var ipBytes = buf.Slice(valPos + 4, 4).ToArray();
                        mappedFallback = new IPEndPoint(new IPAddress(ipBytes), port);
                    }
                }

                // Attributes are padded to a 4-byte boundary.
                int padded = (attrLen + 3) & ~3;
                pos = valPos + padded;
            }
            return mappedFallback;
        }

        /// <summary>Sends one Binding request from <paramref name="socket"/> to
        /// a resolved STUN server and awaits the mapped endpoint, retrying a
        /// couple of times with a short timeout. Returns null on no response.
        /// The socket is left bound and usable for the punch afterward.</summary>
        public static async Task<IPEndPoint> QueryAsync(
            Socket socket, string host, int port, CancellationToken ct = default)
        {
            IPAddress[] addrs;
            try { addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false); }
            catch { return null; }
            if (addrs.Length == 0) return null;
            var server = new IPEndPoint(addrs[0], port);

            // ONE transaction id reused across retransmits, per RFC 5389 §7.2.1:
            // a fresh id per attempt rejects any response slower than one
            // attempt window, so a stable 650 ms RTT failed every retry
            // (adversarial review finding 4).
            var request = BuildBindingRequest(out var txId);
            var recvBuf = new byte[512];

            for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await socket.SendToAsync(request, SocketFlags.None, server, ct).ConfigureAwait(false);
                }
                catch { return null; }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(600));
                try
                {
                    var any = new IPEndPoint(IPAddress.Any, 0);
                    var r = await socket.ReceiveFromAsync(recvBuf, SocketFlags.None, any, attemptCts.Token)
                                        .ConfigureAwait(false);
                    // Only accept a datagram that actually came from this server
                    // and parses as our matching response; anything else (a data
                    // datagram racing in on the shared socket) is skipped.
                    if (((IPEndPoint)r.RemoteEndPoint).Address.Equals(server.Address))
                    {
                        var ep = ParseBindingResponse(recvBuf.AsSpan(0, r.ReceivedBytes), txId);
                        if (ep != null) return ep;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // attempt timeout, retry
                }
                catch { return null; }
            }
            return null;
        }

        /// <summary>Probes up to two servers from the same socket and returns the
        /// public endpoint plus the hard-NAT verdict (different mapped ports =
        /// symmetric NAT). Uses the first server's endpoint as the reported one.
        /// Returns null only when NO server answered.</summary>
        public static async Task<StunResult> DiscoverAsync(
            Socket socket, CancellationToken ct = default,
            (string Host, int Port)[] servers = null)
        {
            servers ??= DefaultServers;
            IPEndPoint first = null;
            int answered = 0;
            bool hardNat = false;

            foreach (var (host, port) in servers)
            {
                if (ct.IsCancellationRequested) break;
                var ep = await QueryAsync(socket, host, port, ct).ConfigureAwait(false);
                if (ep == null) continue;
                answered++;
                if (first == null) { first = ep; }
                else
                {
                    // Same socket, two servers: any differing mapping (port OR
                    // address) means the NAT chooses per destination, so the
                    // mapping toward a real peer is unpredictable and plain
                    // punching won't work. Multi-homed egress that varies the
                    // address is equally unpunchable, so it classifies hard
                    // too (adversarial review finding 9).
                    if (!ep.Equals(first)) hardNat = true;
                    break; // two observations is enough to classify
                }
            }

            if (first == null) return null;
            return new StunResult { PublicEndpoint = first, IsHardNat = hardNat };
        }
    }
}
