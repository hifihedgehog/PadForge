using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>
    /// In-process test doubles for the #294 traversal transports. SimTransport
    /// is a lossy, reordering point-to-point datagram pipe for exercising the
    /// UdpControlChannel ARQ; SimNat is an endpoint-addressed fabric for the
    /// hole-punch orchestration (each puncher binds one endpoint and datagrams
    /// route to the destination endpoint's handler). Deterministic under a seed.
    /// </summary>
    internal sealed class SimTransport : IDatagramTransport
    {
        private SimTransport _peer;
        private readonly Random _rng;
        private readonly double _lossRate;
        public Action<byte[]> OnDatagram { get; set; }

        private SimTransport(double lossRate, int seed)
        {
            _lossRate = lossRate;
            _rng = new Random(seed);
        }

        public static (SimTransport a, SimTransport b) Pair(double lossRate, int seed)
        {
            var a = new SimTransport(lossRate, seed);
            var b = new SimTransport(lossRate, seed + 1);
            a._peer = b;
            b._peer = a;
            return (a, b);
        }

        public Task SendAsync(byte[] datagram, CancellationToken ct)
        {
            double roll;
            lock (_rng) roll = _rng.NextDouble();
            if (roll >= _lossRate)
            {
                var copy = (byte[])datagram.Clone();
                // Deliver asynchronously with a jittered delay so ordering is
                // not guaranteed by the transport, forcing the ARQ to enforce it.
                int delay;
                lock (_rng) delay = _rng.Next(0, 8);
                _ = Task.Run(async () =>
                {
                    if (delay > 0) await Task.Delay(delay);
                    _peer.OnDatagram?.Invoke(copy);
                });
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>Endpoint-addressed datagram fabric. Each <see cref="Endpoint"/>
    /// call registers a handler keyed by an IPEndPoint; a SendToAsync to that
    /// endpoint invokes its handler with the SENDER's endpoint as source.</summary>
    internal sealed class SimNat
    {
        private readonly Dictionary<IPEndPoint, PunchEndpoint> _map = new();
        private readonly object _lock = new();

        public IPunchTransport Endpoint(IPEndPoint self)
        {
            var pe = new PunchEndpoint(this, self);
            lock (_lock) _map[self] = pe;
            return pe;
        }

        private void Route(IPEndPoint from, IPEndPoint to, byte[] dg)
        {
            PunchEndpoint target;
            lock (_lock) _map.TryGetValue(to, out target);
            if (target == null) return; // dead address: nothing answers
            var copy = (byte[])dg.Clone();
            _ = Task.Run(() => target.OnDatagram?.Invoke(from, copy));
        }

        private sealed class PunchEndpoint : IPunchTransport
        {
            private readonly SimNat _nat;
            private readonly IPEndPoint _self;
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }
            public PunchEndpoint(SimNat nat, IPEndPoint self) { _nat = nat; _self = self; }
            public Task SendToAsync(byte[] datagram, IPEndPoint destination, CancellationToken ct)
            {
                _nat.Route(_self, destination, datagram);
                return Task.CompletedTask;
            }
        }
    }
}
