using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        private const BindingFlags RekeyPrivate = BindingFlags.NonPublic | BindingFlags.Instance;
        private static object RekeyConnection(LinkServer server)
        {
            lock (typeof(LinkServer).GetField("_lock", RekeyPrivate).GetValue(server))
                return ((IEnumerable)typeof(LinkServer).GetField("_connections", RekeyPrivate).GetValue(server))
                    .Cast<object>().SingleOrDefault();
        }
        private static T RekeyField<T>(object connection, string name)
            => (T)connection.GetType().GetField(name).GetValue(connection);
        private static byte[] RekeyKey(LinkServer server)
        {
            var connection = RekeyConnection(server);
            return connection == null ? null : (byte[])typeof(LinkSession).GetField("_key", RekeyPrivate)
                .GetValue(RekeyField<LinkSession>(connection, "DataSession"));
        }
        private static bool RekeyKeysMatch(LinkServer a, LinkServer b)
        {
            var ka = RekeyKey(a);
            var kb = RekeyKey(b);
            return ka != null && kb != null && ka.SequenceEqual(kb);
        }

        [Fact]
        public Task TimedOutCanonicalAttemptCannotFinishRecoveryOnAnOlderConfirmedSession()
            => RequiredAttemptTimeout(sendInventory: true);

        [Fact]
        public Task ATimedOutAttemptBeforeInventoryKeepsTheConcurrentConfirmedSession()
            => RequiredAttemptTimeout(sendInventory: false);

        private async Task RequiredAttemptTimeout(bool sendInventory)
        {
            var first = PeerIdentity.Generate();
            var second = PeerIdentity.Generate();
            var identityA = first.Fingerprint.AsSpan().SequenceCompareTo(second.Fingerprint) < 0 ? first : second;
            var identityB = ReferenceEquals(identityA, first) ? second : first;
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            using var a = new LinkServer(identityA, trustA, _ => true);
            using var b = new LinkServer(identityB, trustB, _ => true);
            var connectionTrace = new ConcurrentQueue<string>();
            a.StatusChanged += status => connectionTrace.Enqueue("A " + System.Text.Json.JsonSerializer.Serialize(status));
            b.StatusChanged += status => connectionTrace.Enqueue("B " + System.Text.Json.JsonSerializer.Serialize(status));
            int bPort = StartOnFreePort(b);
            int aPort = StartOnFreePort(a, bPort);
            await using var proxy = new RekeyGateProxy(new IPEndPoint(IPAddress.Loopback, bPort));
            var empty = Array.Empty<RemotePeerDeviceInfo>();
            Assert.True(await a.ConnectAsync("127.0.0.1", proxy.Port, empty));
            Assert.True(await WaitUntil(() => RekeyKeysMatch(a, b), 5000));
            trustA.Find(identityB.PublicKey).ReconnectEnabled = false;
            trustB.Find(identityA.PublicKey).ReconnectEnabled = false;
            // The pre-reset connection is established, not a fresh collision.
            long established = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 20;
            RekeyConnection(a).GetType().GetField("EstablishedTicks").SetValue(RekeyConnection(a), established);
            RekeyConnection(b).GetType().GetField("EstablishedTicks").SetValue(RekeyConnection(b), established);
            typeof(LinkServer).GetMethod("QueueRekey", RekeyPrivate).Invoke(a, new[] { RekeyConnection(a) });
            var recovery = Assert.Single(a.RekeyTasks);
            try
            {
                // X is the required worker's second TCP connection. Hold its reveal
                // before either endpoint can bind X's admission.
                await proxy.XBeforeBind.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await b.ConnectAsync("127.0.0.1", aPort, empty));
                Assert.True(await WaitUntil(() => RekeyKeysMatch(a, b), 5000));
                var yKey = RekeyKey(a);
                b.PushDeviceList(empty);
                Assert.True(await WaitUntil(() => RekeyConnection(a) is { } current
                    && RekeyField<TaskCompletionSource>(current, "Confirmed").Task.IsCompleted, 5000));
                byte[] delayedY = RekeyField<LinkSession>(RekeyConnection(b), "DataSession")
                    .Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>());

                if (!sendInventory)
                {
                    // X stays before inventory transmission until its real deadline.
                    // The concurrent Y remains a valid recovery result in this case.
                    await recovery.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(yKey, RekeyKey(a));
                    Assert.Equal(yKey, RekeyKey(b));
                    Assert.Equal(2, proxy.Connections);
                    return;
                }

                proxy.AllowXBind.TrySetResult();
                await proxy.XInventoryForwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await proxy.XPeerInventoryHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await WaitUntil(() => RekeyKey(b) is { } key && !key.SequenceEqual(yKey), 5000),
                    "X must register at B before the client timeout is observed.");
                // The actual 3-second attempt deadline closes A's TCP stream.
                // The proxy forwards that EOF to B. B's registered control socket
                // has no further read, so EOF alone does not retire X.
                await proxy.XClientClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // This socket fixture covers loopback TCP/UDP, not the public relay.
                trustA.Find(identityB.PublicKey).RendezvousCapabilityBase64 = "";
                trustB.Find(identityA.PublicKey).RendezvousCapabilityBase64 = "";
                proxy.AllowXPeerInventory.TrySetResult();
                var bUdp = (Socket)typeof(LinkServer).GetField("_udp", RekeyPrivate).GetValue(b);
                bUdp.SendTo(delayedY, new IPEndPoint(IPAddress.Loopback, aPort));

                Assert.True(await WaitUntil(() => RekeyKey(a) is { } currentKey
                    && !currentKey.SequenceEqual(yKey) && currentKey.SequenceEqual(RekeyKey(b) ?? Array.Empty<byte>()), 12000),
                    $"Recovery incomplete. Completed={recovery.IsCompleted}, A={a.HasConnections}, B={b.HasConnections}, TCP={proxy.Connections}, AError={a.DiagLastError}, BError={b.DiagLastError}\n"
                    + string.Join("\n", connectionTrace));
                a.PushDeviceList(empty);
                Assert.True(await WaitUntil(() => RekeyConnection(b) is { } current
                    && RekeyField<IPEndPoint>(current, "PeerUdpEndpoint") != null, 5000));
                b.PushDeviceList(empty);
                await recovery.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(RekeyKeysMatch(a, b));
                Assert.Empty(b.RekeyTasks);
                Assert.False(trustA.Find(identityB.PublicKey).ReconnectEnabled);
            }
            finally
            {
                proxy.AllowXBind.TrySetResult();
                proxy.AllowXPeerInventory.TrySetResult();
                a.Stop();
                b.Stop();
                await recovery.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private sealed class RekeyGateProxy : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly Socket _udp;
            private readonly IPEndPoint _destination;
            private readonly CancellationTokenSource _stop = new();
            private readonly ConcurrentBag<TcpClient> _clients = new();
            private readonly ConcurrentBag<Task> _flows = new();
            private readonly Task _accept, _datagrams;
            private IPEndPoint _aEndpoint;
            private int _connections;
            internal int Port { get; }
            internal int Connections => Volatile.Read(ref _connections);
            internal readonly TaskCompletionSource XBeforeBind = Signal();
            internal readonly TaskCompletionSource AllowXBind = Signal();
            internal readonly TaskCompletionSource XInventoryForwarded = Signal();
            internal readonly TaskCompletionSource XPeerInventoryHeld = Signal();
            internal readonly TaskCompletionSource AllowXPeerInventory = Signal();
            internal readonly TaskCompletionSource XClientClosed = Signal();
            private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal RekeyGateProxy(IPEndPoint destination)
            {
                _destination = destination;
                Port = FreePort();
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udp.Bind(new IPEndPoint(IPAddress.Loopback, Port));
                _accept = Accept();
                _datagrams = Datagrams();
            }

            private async Task Accept()
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _clients.Add(client);
                        _flows.Add(Connect(client, Interlocked.Increment(ref _connections)));
                    }
                }
                catch (Exception) when (_stop.IsCancellationRequested) { }
            }

            private async Task Connect(TcpClient a, int number)
            {
                var b = new TcpClient(AddressFamily.InterNetwork);
                _clients.Add(b);
                try
                {
                    await b.ConnectAsync(_destination.Address, _destination.Port, _stop.Token);
                    var channelA = new TcpControlChannel(a.GetStream());
                    var channelB = new TcpControlChannel(b.GetStream());
                    await Task.WhenAll(Pump(channelA, channelB, b, number, true),
                        Pump(channelB, channelA, a, number, false));
                }
                catch (Exception) { }
            }

            private async Task Pump(TcpControlChannel from, TcpControlChannel to, TcpClient target, int number, bool fromA)
            {
                int message = 0;
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        byte[] bytes = await from.ReceiveAsync(_stop.Token);
                        message++;
                        if (number == 2 && fromA && message == 2)
                        {
                            XBeforeBind.TrySetResult();
                            await AllowXBind.Task.WaitAsync(_stop.Token);
                        }
                        if (number == 2 && !fromA && message == 3)
                        {
                            XPeerInventoryHeld.TrySetResult();
                            await AllowXPeerInventory.Task.WaitAsync(_stop.Token);
                        }
                        await to.SendAsync(bytes, _stop.Token);
                        if (number == 2 && fromA && message == 3) XInventoryForwarded.TrySetResult();
                    }
                }
                catch (Exception)
                {
                    if (number == 2 && fromA) XClientClosed.TrySetResult();
                    try { target.Client.Shutdown(SocketShutdown.Send); } catch { }
                }
            }

            private async Task Datagrams()
            {
                var bytes = new byte[65536];
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var packet = await _udp.ReceiveFromAsync(bytes.AsMemory(), SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                        var from = (IPEndPoint)packet.RemoteEndPoint;
                        IPEndPoint target;
                        if (from.Equals(_destination)) target = Volatile.Read(ref _aEndpoint);
                        else { Volatile.Write(ref _aEndpoint, from); target = _destination; }
                        if (target != null)
                            await _udp.SendToAsync(bytes.AsMemory(0, packet.ReceivedBytes), SocketFlags.None, target, _stop.Token);
                    }
                }
                catch (Exception) when (_stop.IsCancellationRequested) { }
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                _listener.Stop();
                _udp.Dispose();
                foreach (var client in _clients) client.Dispose();
                await Task.WhenAll(_accept, _datagrams);
                await Task.WhenAll(_flows);
                _stop.Dispose();
            }
        }
    }
}
