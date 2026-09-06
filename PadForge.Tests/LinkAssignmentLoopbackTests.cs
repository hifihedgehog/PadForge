using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        [Fact]
        public async Task AssignmentsUseThePairedSessionAndInputKeepsFlowing()
        {
            var sourceIdentity = PeerIdentity.Generate();
            var targetTrust = new PeerTrustStore();
            using var target = new LinkServer(PeerIdentity.Generate(), targetTrust, _ => true);
            using var source = new LinkServer(sourceIdentity, new PeerTrustStore(), _ => true);
            RemotePeerDevice received = null;
            target.DeviceConnected += d => received = d;
            int applications = 0;
            target.AssignmentHandler = context => Task.FromResult(context.Execute(() =>
            {
                if (context.Request.IsSet) applications++;
                return new LinkAssignmentReply(context.Request.RequestId, context.Request.DeviceId,
                    LinkAssignmentStatus.Ok, 1, "Game", new[] { new LinkAssignmentSlot(0, "Xbox 1", applications > 0, true) });
            }));
            int port = StartOnFreePort(target);
            StartOnFreePort(source, port);
            Assert.True(await source.ConnectAsync("127.0.0.1", port, new[] { PadInfo() }));
            Assert.True(await WaitUntil(() => received != null, 5000));
            var channel = source.GetAssignmentChannel(source.ConnectedFingerprints()[0]);
            Assert.NotNull(channel);
            Assert.Equal(LinkAssignmentStatus.Denied, (await channel.QueryAsync("pad0")).Status);
            targetTrust.SetRemoteAssignmentsAllowed(sourceIdentity.FingerprintHex, true);
            var snapshot = await channel.QueryAsync("pad0");
            Assert.Equal(LinkAssignmentStatus.Ok, snapshot.Status);
            Assert.Single(snapshot.Slots);
            Assert.Equal(LinkAssignmentStatus.Ok, (await channel.SetAsync("pad0", snapshot.Revision, 0, true)).Status);
            Assert.Equal(1, applications);
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[2] = true;
            source.PushLocalFrame(0, state, new CustomInputStateCodec.Caps(false, false), 1);
            Assert.True(await WaitUntil(() => received.GetCurrentState()?.Buttons[2] == true, 5000));
            targetTrust.SetRemoteAssignmentsAllowed(sourceIdentity.FingerprintHex, false);
            Assert.Equal(LinkAssignmentStatus.Denied, (await channel.SetAsync("pad0", 1, 0, false)).Status);
            Assert.Equal(1, applications);
            source.RevokePeer(source.ConnectedFingerprints()[0]);
            Assert.Equal(LinkAssignmentStatus.Unavailable, (await channel.QueryAsync("pad0")).Status);
        }
    }
}
