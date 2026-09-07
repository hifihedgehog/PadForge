using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public sealed class RemoteToneLifetimeTests
    {
        private static (UserDevice Device, LinkConnectionLifetime Lifetime) Source()
        {
            var device = new UserDevice();
            device.LoadFromWebDevice(new WebControllerDevice(Guid.NewGuid().ToString("N"), "Tone source"));
            device.IsOnline = true;
            return (device, LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) }));
        }
        private static RemoteToneRequest Request(UserDevice device, LinkConnectionLifetime lifetime, uint sequence, float amplitude)
        {
            var frame = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, sequence,
                OutputEffectCodec.EncodeHapticTone(100, amplitude));
            Assert.True(frame.TryCommit(4, () => { }));
            return new RemoteToneRequest(device, 100, amplitude, frame.Ticket(4));
        }

        [Fact]
        public void AZeroReplacesPendingNonzeroBeforeTheWorkerRuns()
        {
            var (device, lifetime) = Source();
            var jobs = new Queue<Action>();
            var published = new List<float>();
            var queue = new DeferredRemoteToneQueue((_, latest) =>
            {
                var request = latest();
                request.TryPublish(() => published.Add(request.Amplitude));
            }, jobs.Enqueue);
            queue.Submit(Request(device, lifetime, 0, 1), true);
            queue.Submit(Request(device, lifetime, 1, 0), false);
            Assert.Single(jobs);
            jobs.Dequeue()();
            Assert.Empty(published);
            queue.Submit(Request(device, lifetime, 2, 0.5f), true);
            Assert.Single(jobs);
            jobs.Dequeue()();
            Assert.Equal(new[] { 0.5f }, published);
        }

        [Fact]
        public void NativePreparationPublishesTheLatestRequestIncludingZero()
        {
            var (device, lifetime) = Source();
            var jobs = new Queue<Action>();
            var published = new List<float>();
            DeferredRemoteToneQueue queue = null;
            var first = Request(device, lifetime, 0, 1);
            queue = new DeferredRemoteToneQueue((_, latest) =>
            {
                var zero = Request(device, lifetime, 1, 0);
                queue.Submit(zero, false);
                Assert.False(first.TryPublish(() => published.Add(1)));
                var current = latest();
                Assert.True(current.TryPublish(() => published.Add(current.Amplitude)));
            }, jobs.Enqueue);
            queue.Submit(first, true);
            jobs.Dequeue()();
            Assert.Equal(new[] { 0f }, published);
        }

        [Fact]
        public void AStillLiveConnectionCannotPublishOverAnotherPeersLaterQueuedRequest()
        {
            var (device, first) = Source();
            var old = Request(device, first, 0, 1);
            var second = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
            var latest = Request(device, second, 0, 0);
            Assert.True(old.IsCurrent); // Its own connection and effect sequence still pass.
            int writes = 0;
            Assert.False(old.TryPublish(() => latest, () => writes++));
            Assert.Equal(0, writes);
            Assert.True(latest.TryPublish(() => latest, () => writes++));
            Assert.Equal(1, writes);
        }

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
        public void DeferredPublicationRejectsRetiredRemovedReplacedOrSupersededSources(int invalidation)
        {
            var (device, lifetime) = Source();
            var request = Request(device, lifetime, 1, 1);
            Assert.True(request.IsCurrent);
            switch (invalidation)
            {
                case 0: lifetime.Retire(); break;
                case 1:
                    lifetime.PublishLocalInventory(new LinkDeviceInventory(2, Array.Empty<RemotePeerDeviceInfo>()));
                    break;
                case 2:
                    device.LoadFromWebDevice(new WebControllerDevice(Guid.NewGuid().ToString("N"), "Replacement"));
                    break;
                case 3: Request(device, lifetime, 2, 0); break;
            }
            int writes = 0;
            Assert.False(request.TryPublish(() => writes++));
            Assert.Equal(0, writes);
            var replacement = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
            var current = Request(device, replacement, 0, 1);
            // A different effect family does not supersede this tone.
            Assert.True(new LinkIncomingFrame(replacement, LinkMessageType.Output, 0, 1,
                OutputEffectCodec.EncodeGuideLed(20)).TryCommit(6, () => { }));
            Assert.True(current.TryPublish(() => writes++));
            Assert.Equal(1, writes);
        }
    }
}
