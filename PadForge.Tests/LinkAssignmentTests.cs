using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    [Collection("RemoteLinkSockets")]
    public class LinkAssignmentTests
    {
        private static RemotePeerDevice Device(string id = "browser-id") => new(new RemotePeerDeviceInfo
        {
            PeerFingerprintHex = "1234", PeerLocalDeviceId = id, Name = "Browser Gamepad 1"
        });

        [Fact]
        public void CodecRoundTripsAndRejectsMalformedPackets()
        {
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id", true, 42, 15, true);
            byte[] bytes = LinkAssignmentCodec.Encode(request);
            Assert.True(LinkAssignmentCodec.TryDecode(bytes, out var decoded, out var none));
            Assert.Equal(request, decoded);
            Assert.Null(none);
            for (int length = 0; length < bytes.Length; length++)
                Assert.False(LinkAssignmentCodec.TryDecode(bytes[..length], out _, out _));
            Assert.False(LinkAssignmentCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _, out _));
            bytes[^1] = 2;
            Assert.False(LinkAssignmentCodec.TryDecode(bytes, out _, out _));
            bytes[^1] = 1;
            bytes[^2] = 16;
            Assert.False(LinkAssignmentCodec.TryDecode(bytes, out _, out _));
            var slots = Enumerable.Range(0, 16).Select(i => new LinkAssignmentSlot((byte)i, new string('界', 100), i == 15, true)).ToArray();
            var reply = new LinkAssignmentReply(request.RequestId, request.DeviceId, LinkAssignmentStatus.Ok, 9, "Game", slots);
            Assert.True(LinkAssignmentCodec.TryDecode(LinkAssignmentCodec.Encode(reply), out _, out var decodedReply));
            Assert.Equal(16, decodedReply.Slots.Length);
            Assert.True(decodedReply.Slots[15].Assigned);
            Assert.DoesNotContain('\uFFFD', decodedReply.Slots[0].Name);
            var duplicate = reply with { Slots = new[] { slots[0], slots[0] } };
            Assert.False(LinkAssignmentCodec.TryDecode(LinkAssignmentCodec.Encode(duplicate), out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => LinkAssignmentCodec.Encode(request with { DeviceId = new string('a', 129) }));
            Assert.False(LinkAssignmentCodec.TryDecode(new byte[LinkAssignmentCodec.MaxPayload + 1], out _, out _));
        }

        [Fact]
        public void PermissionDefaultsOffAndSurvivesMetadataRefreshAndXml()
        {
            byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var trust = new PeerTrustStore();
            var peer = trust.Grant(key, "PC", "today", true, false);
            Assert.False(trust.AllowsRemoteAssignments(peer.FingerprintHex));
            trust.SetRemoteAssignmentsAllowed(peer.FingerprintHex, true);
            trust.Grant(key, "Renamed", "today", true, false);
            Assert.True(trust.AllowsRemoteAssignments(peer.FingerprintHex));
            var serializer = new XmlSerializer(typeof(PeerTrust));
            using var writer = new StringWriter();
            serializer.Serialize(writer, peer);
            var copy = (PeerTrust)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.True(copy.AllowRemoteAssignments);
            var old = writer.ToString().Replace(" AllowRemoteAssignments=\"true\"", "");
            Assert.False(((PeerTrust)serializer.Deserialize(new StringReader(old))).AllowRemoteAssignments);
            trust.Revoke(key);
            Assert.False(trust.AllowsRemoteAssignments(peer.FingerprintHex));
        }

        [Fact]
        public void NewTypeDoesNotChangeOldValuesOrCollideWithControlTags()
        {
            Assert.Equal(Enumerable.Range(1, 9).Select(i => (byte)i), Enum.GetValues<LinkMessageType>().Select(t => (byte)t));
            var key = new byte[32];
            var sender = new LinkSession(key, true);
            var receiver = new LinkSession(key, false);
            var packet = sender.Seal(LinkMessageType.Assignments, 0, 0, new byte[] { 1, 2 });
            Assert.Equal(0x91, packet[0]);
            Assert.True(receiver.Open(packet, out var type, out _, out _, out var payload));
            Assert.Equal(LinkMessageType.Assignments, type);
            Assert.Equal(new byte[] { 1, 2 }, payload);
            Assert.False(receiver.Open(packet, out _, out _, out _, out _));
        }

        [Fact]
        public async Task LostReplyAndDuplicateRequestsApplyOnlyOnce()
        {
            var device = Device();
            LinkAssignmentChannel client = null;
            int applied = 0, replies = 0, attempts = 0;
            var server = new LinkAssignmentChannel("1234", () => true, () => true, _ => device,
                bytes => { replies++; if (attempts > 1) client.Receive(bytes); return true; },
                context => Task.FromResult(context.Execute(() =>
                {
                    applied++;
                    return LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Ok);
                })));
            client = new("1234", () => true, () => true, _ => null,
                bytes => { attempts++; server.Receive(bytes); server.Receive(bytes); return true; }, null);
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id", true, 0, 0, true);
            var reply = await client.SendAsync(request, timeoutMs: 1000, retryMs: 10);
            Assert.Equal(LinkAssignmentStatus.Ok, reply.Status);
            Assert.Equal(1, applied);
            Assert.True(replies >= 2);
            Assert.True(attempts >= 2);
            var conflict = await client.SendAsync(request with { Assigned = false }, timeoutMs: 1000, retryMs: 10);
            Assert.Equal(LinkAssignmentStatus.Invalid, conflict.Status);
            Assert.Equal(1, applied);
            client.Close(); server.Close();
        }

        [Theory]
        [InlineData("revoke", LinkAssignmentStatus.Denied)]
        [InlineData("replace", LinkAssignmentStatus.Unavailable)]
        [InlineData("device", LinkAssignmentStatus.Unavailable)]
        [InlineData("close", LinkAssignmentStatus.Unavailable)]
        public async Task AQueuedRequestCannotOutliveItsAuthority(string change, LinkAssignmentStatus expected)
        {
            bool allowed = true, current = true;
            var device = Device();
            var entered = new TaskCompletionSource<LinkAssignmentContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<LinkAssignmentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = new LinkAssignmentChannel("1234", () => current, () => allowed, _ => device,
                _ => true, context => { entered.SetResult(context); return finish.Task; });
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id", true, 0, 0, true);
            channel.Receive(LinkAssignmentCodec.Encode(request));
            var context = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (change == "revoke") allowed = false;
            if (change == "replace") current = false;
            if (change == "device") device = Device();
            if (change == "close") channel.Close();
            bool applied = false;
            var reply = context.Execute(() => { applied = true; return LinkAssignmentReply.For(request, LinkAssignmentStatus.Ok); });
            Assert.Equal(expected, reply.Status);
            Assert.False(applied);
            finish.SetResult(reply);
            await finish.Task;
            channel.Close();
        }

        [Fact]
        public async Task RevocationAlsoBlocksCachedRepliesAndUnknownSources()
        {
            bool allowed = true;
            var device = Device();
            LinkAssignmentChannel client = null;
            int calls = 0;
            var server = new LinkAssignmentChannel("1234", () => true, () => allowed,
                id => id == "browser-id" ? device : null, bytes => { client.Receive(bytes); return true; },
                context => Task.FromResult(context.Execute(() => { calls++; return LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Ok); })));
            client = new("1234", () => true, () => true, _ => null, bytes => { server.Receive(bytes); return true; }, null);
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id");
            Assert.Equal(LinkAssignmentStatus.Ok, (await client.SendAsync(request)).Status);
            allowed = false;
            Assert.Equal(LinkAssignmentStatus.Denied, (await client.SendAsync(request)).Status);
            allowed = true;
            Assert.Equal(LinkAssignmentStatus.Unavailable, (await client.QueryAsync("another-peers-device")).Status);
            Assert.Equal(1, calls);
            client.Close(); server.Close();
        }

        [Fact]
        public async Task TimeoutCancellationAndClosureAreBounded()
        {
            int sent = 0;
            var client = new LinkAssignmentChannel("1234", () => true, () => true, _ => null,
                _ => { sent++; return true; }, null);
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id");
            Assert.Equal(LinkAssignmentStatus.TimedOut, (await client.SendAsync(request, timeoutMs: 40, retryMs: 5)).Status);
            Assert.True(sent >= 1);
            using var cancel = new CancellationTokenSource();
            var pending = client.QueryAsync("browser-id", cancel.Token);
            cancel.Cancel();
            Assert.Equal(LinkAssignmentStatus.Canceled, (await pending).Status);
            var pendingClose = client.QueryAsync("browser-id");
            client.Close();
            Assert.Equal(LinkAssignmentStatus.Unavailable, (await pendingClose).Status);
            Assert.Equal(LinkAssignmentStatus.Unavailable, (await client.QueryAsync("browser-id")).Status);
        }

        [Fact]
        public async Task InFlightDuplicatesAndFloodStayBounded()
        {
            int calls = 0;
            var finish = new TaskCompletionSource<LinkAssignmentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = new LinkAssignmentChannel("1234", () => true, () => true, _ => Device(), _ => true,
                _ => { calls++; return finish.Task; });
            var request = new LinkAssignmentRequest(Guid.NewGuid(), "browser-id");
            for (int i = 0; i < 100; i++) channel.Receive(LinkAssignmentCodec.Encode(request));
            Assert.Equal(1, calls);
            for (int i = 0; i < 100; i++) channel.Receive(LinkAssignmentCodec.Encode(request with { RequestId = Guid.NewGuid() }));
            Assert.Equal(4, calls);
            finish.SetResult(LinkAssignmentReply.For(request, LinkAssignmentStatus.Ok));
            await finish.Task;
            channel.Close();
        }

        [Fact]
        public void BrowserIdentitySurvivesReconnectAndRemoteNamespacing()
        {
            var first = new WebControllerDevice("persistent-tab-id", "Browser Gamepad 1");
            var again = new WebControllerDevice("persistent-tab-id", "Browser Gamepad 2");
            Assert.Equal(first.InstanceGuid, again.InstanceGuid);
            var info = new RemotePeerDeviceInfo { PeerLocalDeviceId = first.InstanceGuid.ToString("N"), PeerFingerprintHex = "PC-A" };
            var before = new RemotePeerDevice(info);
            Assert.Equal(before.InstanceGuid, new RemotePeerDevice(info).InstanceGuid);
            info.PeerFingerprintHex = "PC-B";
            Assert.NotEqual(before.InstanceGuid, new RemotePeerDevice(info).InstanceGuid);
        }
    }
}
