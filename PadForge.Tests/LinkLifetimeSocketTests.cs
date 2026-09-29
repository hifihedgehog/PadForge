using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        private delegate void DatagramRoute(ReadOnlySpan<byte> datagram, IPEndPoint endpoint);
        private static void DeliverDatagram(LinkServer server, byte[] datagram)
            => typeof(LinkServer).GetMethod("RouteDatagram", BindingFlags.NonPublic | BindingFlags.Instance)
                .CreateDelegate<DatagramRoute>(server)(datagram, null);
        private static LinkSession DataSession(LinkServer server)
        {
            var connections = (IEnumerable)typeof(LinkServer).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
            var connection = Assert.Single(connections.Cast<object>());
            return (LinkSession)connection.GetType().GetField("DataSession").GetValue(connection);
        }
        private static LinkDeviceInventory SingleInventory(long revision, string id)
            => new(revision, new[] { LinkLifetimeFixtures.Info(id) });

        /// <summary>ConnectAsync answers false for a refused socket, a failed
        /// handshake and a refused registration alike, and says which only
        /// through StatusChanged. A test that asserts on it records that
        /// stream, so a failure states its own cause. One of these failed in
        /// a full run on 2026-09-20 with nothing but "Expected: True".
        /// Subscribe before Start, so a bind failure is recorded too.</summary>
        private static ConcurrentQueue<string> TraceStatus(params (string Name, LinkServer Server)[] servers)
        {
            var trace = new ConcurrentQueue<string>();
            foreach (var (name, server) in servers)
                server.StatusChanged += status => trace.Enqueue(name + " " + System.Text.Json.JsonSerializer.Serialize(status));
            return trace;
        }
        private static string Why(LinkServer dialer, ConcurrentQueue<string> trace)
            => "ConnectAsync returned false. DiagLastError=" + (dialer.DiagLastError ?? "(null)")
               + " | status: " + string.Join(" | ", trace);

        [Fact]
        public async Task SparseInventoryKeepsDelayedInputAndEveryReverseChannelOnTheirOriginalSource()
        {
            using var consumer = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            using var owner = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            var devices = new ConcurrentDictionary<string, RemotePeerDevice>();
            consumer.DeviceConnected += device => devices[device.Info.PeerLocalDeviceId] = device;
            var received = new ConcurrentQueue<LinkIncomingFrame>();
            owner.FrameReceived += received.Enqueue;
            var current = SingleInventory(1, "a");
            owner.ExposeProvider = () => Volatile.Read(ref current);
            var status = TraceStatus(("consumer", consumer), ("owner", owner));
            int port = StartOnFreePort(consumer);
            StartOnFreePort(owner, port);
            Assert.True(await owner.ConnectAsync("127.0.0.1", port, current), Why(owner, status));
            Assert.True(await WaitUntil(() => devices.ContainsKey("a"), 5000));
            var original = devices["a"];
            var ownerSession = DataSession(owner);
            var consumerSession = DataSession(consumer);
            var stateA = CustomInputStateCodec.CreateNeutral();
            stateA.Buttons[0] = true;
            byte[] inputA = CustomInputStateCodec.Encode(stateA, new CustomInputStateCodec.Caps(false, false));
            DeliverDatagram(consumer, ownerSession.Seal(LinkMessageType.Input, 0, 1, inputA));
            Assert.True(original.GetCurrentState().Buttons[0]);
            // Slot 1 has not been advertised. Its input cannot update the slot-0 device.
            DeliverDatagram(consumer, ownerSession.Seal(LinkMessageType.Input, 1, 2, new byte[] { 0 }));
            Assert.True(original.GetCurrentState().Buttons[0]);
            var staleInput = ownerSession.Seal(LinkMessageType.Input, 0, 3, inputA);
            foreach (var type in new[] { LinkMessageType.Output, LinkMessageType.Audio, LinkMessageType.SourceDemand })
                DeliverDatagram(owner, consumerSession.Seal(type, 0, 1, new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(3, received.Count);
            var staleReverse = received.ToArray();
            received.Clear();

            current = new LinkDeviceInventory(2, Array.Empty<RemotePeerDeviceInfo>());
            owner.PushDeviceList(current);
            Assert.True(await WaitUntil(() => !original.IsAttached, 5000));
            current = SingleInventory(3, "b");
            owner.PushDeviceList(current);
            Assert.True(await WaitUntil(() => devices.ContainsKey("b"), 5000));
            var next = devices["b"];
            Assert.Equal((byte)1, next.LinkSlot);
            DeliverDatagram(consumer, staleInput);
            Assert.False(next.GetCurrentState()?.Buttons[0] == true);
            int applied = 0;
            foreach (var frame in staleReverse) Assert.False(frame.TryCommit(0, () => applied++));
            foreach (var type in new[] { LinkMessageType.Output, LinkMessageType.Audio, LinkMessageType.SourceDemand })
            {
                DeliverDatagram(owner, consumerSession.Seal(type, 0, 2, new byte[] { 1, 2, 3, 4 }));
                DeliverDatagram(owner, consumerSession.Seal(type, 1, 3, new byte[] { 1, 2, 3, 4 }));
            }
            while (received.TryDequeue(out var frame))
            {
                if (frame.Slot == 0) Assert.False(frame.TryCommit(0, () => applied++));
                else
                {
                    Assert.Equal("b", frame.DeviceId);
                    Assert.True(frame.TryCommit(0, () => applied++));
                }
            }
            Assert.Equal(3, applied);
            var stateB = CustomInputStateCodec.CreateNeutral();
            stateB.Buttons[1] = true;
            DeliverDatagram(consumer, ownerSession.Seal(LinkMessageType.Input, 1, 4,
                CustomInputStateCodec.Encode(stateB, new CustomInputStateCodec.Caps(false, false))));
            Assert.True(next.GetCurrentState().Buttons[1]);
            Assert.False(next.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public async Task ADeviceWhoseTypeChangesRegistersAgain()
        {
            // The consumer refreshed the owner's type in place, but its row
            // kept the type it registered with, so a Bliss-Box port the owner's
            // switch reopened kept the other shape's rest rule.
            using var consumer = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            using var owner = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => true);
            RemotePeerDevice device = null;
            int registrations = 0;
            consumer.DeviceConnected += d =>
            {
                Volatile.Write(ref device, d);
                Interlocked.Increment(ref registrations);
            };
            var current = SingleInventory(1, "a");
            owner.ExposeProvider = () => Volatile.Read(ref current);
            var status = TraceStatus(("consumer", consumer), ("owner", owner));
            int port = StartOnFreePort(consumer);
            StartOnFreePort(owner, port);
            Assert.True(await owner.ConnectAsync("127.0.0.1", port, current), Why(owner, status));
            Assert.True(await WaitUntil(() => Volatile.Read(ref registrations) == 1, 5000));
            // The same type again registers nothing, and a new one registers.
            current = SingleInventory(2, "a");
            owner.PushDeviceList(current);
            var joystick = LinkLifetimeFixtures.Info("a");
            joystick.InputDeviceType = InputDeviceType.Joystick;
            current = new LinkDeviceInventory(3, new[] { joystick });
            owner.PushDeviceList(current);
            Assert.True(await WaitUntil(() => Volatile.Read(ref registrations) >= 2
                && Volatile.Read(ref device).Info.InputDeviceType == InputDeviceType.Joystick, 5000));
            Assert.Equal(2, Volatile.Read(ref registrations));
        }

        [Fact]
        public async Task ExhaustionRekeysAndReplaysCurrentInventoryWithAutoReconnectDisabled()
        {
            var ownerIdentity = PeerIdentity.Generate();
            var consumerIdentity = PeerIdentity.Generate();
            var ownerTrust = new PeerTrustStore();
            var consumerTrust = new PeerTrustStore();
            int approvals = 0;
            using var consumer = new LinkServer(consumerIdentity, consumerTrust, _ => { Interlocked.Increment(ref approvals); return true; });
            using var owner = new LinkServer(ownerIdentity, ownerTrust, _ => { Interlocked.Increment(ref approvals); return true; });
            RemotePeerDevice received = null;
            consumer.DeviceConnected += device => Volatile.Write(ref received, device);
            var current = SingleInventory(1, "id0");
            owner.ExposeProvider = () => Volatile.Read(ref current);
            var status = TraceStatus(("consumer", consumer), ("owner", owner));
            int port = StartOnFreePort(consumer);
            StartOnFreePort(owner, port);
            Assert.True(await owner.ConnectAsync("127.0.0.1", port, current), Why(owner, status));
            Assert.True(await WaitUntil(() => received != null, 5000));
            ownerTrust.Find(consumerIdentity.PublicKey).ReconnectEnabled = false;
            consumerTrust.Find(ownerIdentity.PublicKey).ReconnectEnabled = false;
            var oldRoute = received.Connection;
            var oldTargets = owner.CaptureInputTargets();
            var oldSession = DataSession(owner);
            byte[] stale = oldSession.Seal(LinkMessageType.Input, 0, 1,
                CustomInputStateCodec.Encode(CustomInputStateCodec.CreateNeutral(), new CustomInputStateCodec.Caps(false, false)));
            for (int i = 1; i <= 256; i++)
            {
                Volatile.Write(ref current, SingleInventory(i + 1, "id" + i));
                owner.PushDeviceList(current);
            }
            Assert.True(await WaitUntil(() => received?.Info.PeerLocalDeviceId == "id256"
                && received.Connection.IsCurrent && owner.HasConnections
                && !owner.IsPeerConnecting(consumerIdentity.FingerprintHex), 45000), owner.DiagLastError);
            Assert.False(oldRoute.IsCurrent);
            Assert.NotSame(oldSession, DataSession(owner));
            Assert.Equal(2, approvals);
            Assert.False(oldRoute.Send(LinkMessageType.Output, 0, new byte[] { 1 }, deviceId: "id0"));
            DeliverDatagram(consumer, stale);
            Assert.False(received.GetCurrentState()?.Buttons[2] == true);
            var live = CustomInputStateCodec.CreateNeutral();
            live.Buttons[2] = true;
            owner.PushLocalFrame(oldTargets, "id0", live, new CustomInputStateCodec.Caps(false, false), 2);
            owner.PushLocalFrame(owner.CaptureInputTargets(), "id256", live, new CustomInputStateCodec.Caps(false, false), 3);
            Assert.True(await WaitUntil(() => received.GetCurrentState()?.Buttons[2] == true, 5000));
            Volatile.Write(ref current, SingleInventory(258, "id257"));
            owner.PushDeviceList(current);
            Assert.True(await WaitUntil(() => received.Info.PeerLocalDeviceId == "id257" && received.Connection.IsCurrent, 5000));
        }

        [Fact]
        public async Task RevocationCancelsARequiredRekeyInsteadOfLettingItsRetryOutliveThePeer()
        {
            var identity = PeerIdentity.Generate();
            var trust = new PeerTrustStore();
            using var consumer = new LinkServer(identity, new PeerTrustStore(), _ => true);
            using var owner = new LinkServer(PeerIdentity.Generate(), trust, _ => true);
            var current = SingleInventory(1, "id0");
            owner.ExposeProvider = () => current;
            var status = TraceStatus(("consumer", consumer), ("owner", owner));
            int port = StartOnFreePort(consumer);
            StartOnFreePort(owner, port);
            Assert.True(await owner.ConnectAsync("127.0.0.1", port, current), Why(owner, status));
            trust.Find(identity.PublicKey).RendezvousCapabilityBase64 = "";
            consumer.Stop();
            for (int i = 1; i <= 256; i++)
            {
                current = SingleInventory(i + 1, "id" + i);
                owner.PushDeviceList(current);
            }
            Assert.True(owner.IsPeerConnecting(identity.FingerprintHex));
            var pending = Assert.Single(owner.RekeyTasks);
            owner.RevokePeer(identity.FingerprintHex);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(owner.IsPeerConnecting(identity.FingerprintHex));
            Assert.False(owner.HasConnections);
        }
    }
}
