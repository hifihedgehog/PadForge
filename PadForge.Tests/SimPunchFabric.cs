using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>
    /// In-process simulated NAT fabric that carries BOTH punch and reliable-
    /// control datagrams on ONE link per endpoint and demuxes them by tag,
    /// exactly as LinkServer's real shared-socket receive loop must. Each
    /// endpoint exposes a punch facade and a control facade that share the
    /// underlying send; inbound datagrams route to the matching sink by their
    /// first byte (punch 0xC2/0xC3, control 0xC0/0xC1). Proves the composition
    /// in PunchedConnection without real sockets.
    /// </summary>
    internal sealed class SimPunchFabric
    {
        private readonly Dictionary<IPEndPoint, SimEndpoint> _map = new();
        private readonly object _lock = new();

        public SimEndpoint Endpoint(IPEndPoint self)
        {
            var e = new SimEndpoint(this, self);
            lock (_lock) _map[self] = e;
            return e;
        }

        private void Route(IPEndPoint from, IPEndPoint to, byte[] dg)
        {
            SimEndpoint target;
            lock (_lock) _map.TryGetValue(to, out target);
            if (target == null) return; // dead address
            var copy = (byte[])dg.Clone();
            _ = Task.Run(() => target.Deliver(from, copy));
        }

        internal sealed class SimEndpoint
        {
            private readonly SimPunchFabric _fabric;
            private readonly IPEndPoint _self;
            public IPunchTransport Punch { get; }
            public IDatagramTransport Control { get; }

            public SimEndpoint(SimPunchFabric fabric, IPEndPoint self)
            {
                _fabric = fabric;
                _self = self;
                Punch = new PunchFacade(this);
                Control = new ControlFacade(this);
            }

            // Demux inbound by tag, exactly like the real receive loop.
            public void Deliver(IPEndPoint from, byte[] dg)
            {
                if (dg.Length < 1) return;
                byte tag = dg[0];
                if (tag == 0xC2 || tag == 0xC3) // punch ping/pong
                    ((PunchFacade)Punch).Inbound?.Invoke(from, dg);
                else if (tag == 0xC0 || tag == 0xC1) // control data/ack
                    ((ControlFacade)Control).Inbound?.Invoke(dg);
            }

            private sealed class PunchFacade : IPunchTransport
            {
                private readonly SimEndpoint _e;
                public Action<IPEndPoint, byte[]> OnDatagram { get => Inbound; set => Inbound = value; }
                public Action<IPEndPoint, byte[]> Inbound;
                public PunchFacade(SimEndpoint e) { _e = e; }
                public Task SendToAsync(byte[] datagram, IPEndPoint destination, CancellationToken ct)
                {
                    _e._fabric.Route(_e._self, destination, datagram);
                    return Task.CompletedTask;
                }
            }

            private sealed class ControlFacade : IDatagramTransport
            {
                private readonly SimEndpoint _e;
                public Action<byte[]> OnDatagram { get => Inbound; set => Inbound = value; }
                public Action<byte[]> Inbound;
                // The control channel learns the peer endpoint from the punch;
                // in-process we just send to whoever the endpoint is paired with,
                // which the fabric resolves as "the other end that punched us".
                public IPEndPoint PeerEndpoint;
                public ControlFacade(SimEndpoint e) { _e = e; }
                public Task SendAsync(byte[] datagram, CancellationToken ct)
                {
                    // The control channel is used only after a successful punch,
                    // so the peer endpoint is known: whichever remote last
                    // exchanged a punch with us. We resolve it from the fabric's
                    // record set by scanning for the other endpoint. For the
                    // two-party tests there is exactly one other endpoint.
                    IPEndPoint peer = _e.ResolveOtherEndpoint();
                    if (peer != null) _e._fabric.Route(_e._self, peer, datagram);
                    return Task.CompletedTask;
                }
            }

            private IPEndPoint ResolveOtherEndpoint()
            {
                lock (_fabric._lock)
                    foreach (var kv in _fabric._map)
                        if (!kv.Key.Equals(_self)) return kv.Key;
                return null;
            }
        }
    }
}
