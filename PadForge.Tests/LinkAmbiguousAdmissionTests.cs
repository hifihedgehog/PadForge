using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public sealed class LinkAmbiguousAdmissionTests
    {
        [Fact]
        public async Task AnUncertainPeerEpochRejectsEarlierAdmissionsAndPreservesOtherPeers()
        {
            var admissions = new LinkAdmissionCoordinator();
            var beforeFailure = admissions.Capture();
            using var pending = beforeFailure(CancellationToken.None);
            await pending.BindAsync("peer", false, true);
            using var unbound = beforeFailure(CancellationToken.None);
            using var otherPeer = beforeFailure(CancellationToken.None);
            await otherPeer.BindAsync("other", false, true);

            // A recovery worker that lost ownership cannot invalidate a session.
            Assert.False(admissions.TryInvalidatePeer("peer", () => false));
            Assert.True(pending.TryRun(() => { }));
            Assert.False(pending.Token.IsCancellationRequested);

            Assert.True(admissions.TryInvalidatePeer("peer", () => true));
            Assert.True(pending.Token.IsCancellationRequested);
            Assert.False(pending.TryPublish(() => true));
            await Assert.ThrowsAsync<LinkConnectionException>(() => unbound.BindAsync("peer", false, true));
            using var capturedEarlierButStartedLater = beforeFailure(CancellationToken.None);
            await Assert.ThrowsAsync<LinkConnectionException>(() => capturedEarlierButStartedLater.BindAsync("peer", false, true));

            Assert.False(otherPeer.Token.IsCancellationRequested);
            Assert.True(otherPeer.TryPublish(() => true));
            using var fresh = admissions.Capture()(CancellationToken.None);
            await fresh.BindAsync("peer", false, true);
            Assert.True(fresh.TryPublish(() => true));
        }
    }
}
