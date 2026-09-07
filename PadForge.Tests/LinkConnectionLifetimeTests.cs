using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    internal static class LinkLifetimeFixtures
    {
        internal static RemotePeerDeviceInfo Info(string id, byte slot = 0) => new()
        {
            PeerLocalDeviceId = id, Slot = slot, Name = id,
            VendorId = 0x1234, ProductId = 0x5678,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, Online = true, InputDeviceType = InputDeviceType.Gamepad
        };

        internal static LinkConnectionLifetime Lifetime(RemotePeerDeviceInfo[] devices,
            Func<LinkMessageType, byte, ulong, byte[], bool> send = null)
            => new("owner", new LinkExposureSnapshot(new LinkDeviceInventory(1, devices)),
                () => true, send ?? ((_, _, _, _) => true));

        internal static byte[] Effect(int family, bool active) => family switch
        {
            1 => OutputEffectCodec.EncodeSonyEffect(new byte[] { (byte)(active ? 1 : 0), 0 }),
            2 => OutputEffectCodec.EncodeVibration(new Vibration((ushort)(active ? 10000 : 0), 0)),
            3 => OutputEffectCodec.EncodeWheel(false, false, (short)(active ? 1000 : 0), 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 900, 0, false),
            4 => OutputEffectCodec.EncodeHapticTone(100, active ? 0.5f : 0f),
            5 => OutputEffectCodec.EncodePlayerIndex(active ? 2 : 0),
            6 => OutputEffectCodec.EncodeGuideLed(active ? 50 : 0),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    public sealed class LinkConnectionLifetimeTests
    {
        [Theory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)]
        [InlineData(4)] [InlineData(5)] [InlineData(6)]
        public void EachDeviceAndEffectFamilyHasItsOwnWatermark(int family)
        {
            var lifetime = LinkLifetimeFixtures.Lifetime(new[]
            {
                LinkLifetimeFixtures.Info("a", 3), LinkLifetimeFixtures.Info("b", 201)
            });
            var key = PeerIdentity.Generate().PublicKey;
            var sender = new LinkSession(key, true);
            var receiver = new LinkSession(key, false);
            int otherFamily = family == 6 ? 1 : family + 1;
            byte[] old = sender.Seal(LinkMessageType.Output, 3, 1, LinkLifetimeFixtures.Effect(family, true));
            byte[] other = sender.Seal(LinkMessageType.Output, 3, 2, LinkLifetimeFixtures.Effect(otherFamily, true));
            byte[] sibling = sender.Seal(LinkMessageType.Output, 201, 3, LinkLifetimeFixtures.Effect(family, true));
            byte[] zero = sender.Seal(LinkMessageType.Output, 3, 4, LinkLifetimeFixtures.Effect(family, false));
            var applied = new List<(byte Slot, int Family)>();
            bool Receive(byte[] packet)
            {
                Assert.True(receiver.Open(packet, out var type, out byte slot, out _, out uint sequence, out var payload));
                Assert.True(OutputEffectCodec.TryDecode(payload, out var effect));
                return new LinkIncomingFrame(lifetime, type, slot, sequence, payload)
                    .TryCommit((int)effect.Kind, () => applied.Add((slot, (int)effect.Kind)));
            }
            Assert.True(Receive(zero));
            Assert.False(Receive(old)); // AEAD and the replay window accepted this unseen older packet.
            Assert.True(Receive(other));
            Assert.True(Receive(sibling));
            Assert.Equal(new[] { ((byte)3, family), ((byte)3, otherFamily), ((byte)201, family) }, applied);
            var replacement = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a", 3) });
            Assert.True(new LinkIncomingFrame(replacement, LinkMessageType.Output, 3, 0, zero)
                .TryCommit(family, () => { }));
        }

        [Theory]
        [InlineData(LinkMessageType.Output)]
        [InlineData(LinkMessageType.Audio)]
        [InlineData(LinkMessageType.SourceDemand)]
        public void CapturedWorkSurvivesOtherSourcesChangingButNotItsOwnReactivation(LinkMessageType type)
        {
            var a = LinkLifetimeFixtures.Info("a", 0);
            var b = LinkLifetimeFixtures.Info("b", 1);
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { a });
            int family = type == LinkMessageType.Output ? 2 : 0;
            int commits = 0;
            var beforeAdd = new LinkIncomingFrame(lifetime, type, 0, 0, new byte[4]);
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(2, new[] { a, b })));
            Assert.True(beforeAdd.TryCommit(family, () => commits++));
            var beforeRemove = new LinkIncomingFrame(lifetime, type, 0, 1, new byte[4]);
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(3, new[] { a })));
            Assert.True(beforeRemove.TryCommit(family, () => commits++));
            var beforeOwnRemoval = new LinkIncomingFrame(lifetime, type, 0, 2, new byte[4]);
            lifetime.PublishLocalInventory(new LinkDeviceInventory(4, new[] { b }));
            lifetime.PublishLocalInventory(new LinkDeviceInventory(5, new[] { a, b }));
            Assert.Equal("a", lifetime.LocalDeviceId(0));
            Assert.False(beforeOwnRemoval.TryCommit(family, () => commits++));
            Assert.True(new LinkIncomingFrame(lifetime, type, 0, 3, new byte[4]).TryCommit(family, () => commits++));
            Assert.Equal(3, commits);
        }

        [Fact]
        public void RebindingASourceRenewsItsIdentityWithoutInvalidatingAnotherSource()
        {
            var a = LinkLifetimeFixtures.Info("a", 0);
            var b = LinkLifetimeFixtures.Info("b", 1);
            object firstA = new(), secondA = new(), sourceB = new();
            var initial = new LinkDeviceInventory(1, new[] { a, b },
                new Dictionary<string, object> { ["a"] = firstA, ["b"] = sourceB });
            var lifetime = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(initial), () => true, (_, _, _, _) => true);
            var captured = lifetime.CaptureInputBindings();
            var frameA = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 0, new byte[4]);
            var frameB = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 1, 1, new byte[4]);
            lifetime.PublishLocalInventory(new LinkDeviceInventory(2, new[] { a, b },
                new Dictionary<string, object> { ["a"] = secondA, ["b"] = sourceB }));
            Assert.NotSame(captured[0], lifetime.CaptureInputBindings()[0]);
            Assert.Same(captured[1], lifetime.CaptureInputBindings()[1]);
            Assert.False(frameA.TryCommit(2, () => { }));
            Assert.True(frameB.TryCommit(2, () => { }));
            Assert.False(lifetime.SendInput(captured, "a", new byte[1], 1));
            Assert.True(lifetime.SendInput(captured, "b", new byte[1], 2));
            Assert.True(new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 2, new byte[4]).TryCommit(2, () => { }));
        }

        [Fact]
        public void InputCapturesAndToneTicketsFollowTheirSourceActivation()
        {
            var a = LinkLifetimeFixtures.Info("a", 0);
            var b = LinkLifetimeFixtures.Info("b", 1);
            int inputs = 0, tones = 0;
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { a }, (type, _, _, _) =>
            {
                if (type == LinkMessageType.Input) inputs++;
                return true;
            });
            var captured = lifetime.CaptureInputBindings();
            var tone = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 0, OutputEffectCodec.EncodeHapticTone(100, 1));
            Assert.True(tone.TryCommit(4, () => { }));
            var ticket = tone.Ticket(4);
            lifetime.PublishLocalInventory(new LinkDeviceInventory(2, new[] { a, b }));
            Assert.NotSame(captured, lifetime.CaptureInputBindings());
            Assert.Same(captured[0], lifetime.CaptureInputBindings()[0]);
            Assert.True(lifetime.SendInput(captured, "a", new byte[1], 1));
            Assert.True(ticket.IsCurrent);
            Assert.True(ticket.TryCommit(() => tones++));
            lifetime.PublishLocalInventory(new LinkDeviceInventory(3, new[] { a }));
            Assert.True(lifetime.SendInput(captured, "a", new byte[1], 2));
            Assert.True(ticket.TryCommit(() => tones++));
            lifetime.PublishLocalInventory(new LinkDeviceInventory(4, Array.Empty<RemotePeerDeviceInfo>()));
            Assert.False(lifetime.SendInput(captured, "a", new byte[1], 3));
            Assert.False(ticket.IsCurrent);
            lifetime.PublishLocalInventory(new LinkDeviceInventory(5, new[] { a, b }));
            Assert.NotSame(captured[0], lifetime.CaptureInputBindings()[0]);
            Assert.False(lifetime.SendInput(captured, "a", new byte[1], 4));
            Assert.False(ticket.TryCommit(() => tones++));
            Assert.True(lifetime.SendInput(lifetime.CaptureInputBindings(), "a", new byte[1], 5));
            var current = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 1, OutputEffectCodec.EncodeHapticTone(100, 1));
            Assert.True(current.TryCommit(4, () => { }));
            Assert.True(current.Ticket(4).TryCommit(() => tones++));
            var replacement = LinkLifetimeFixtures.Lifetime(new[] { a });
            Assert.False(replacement.SendInput(captured, "a", new byte[1], 6));
            Assert.Equal(3, inputs);
            Assert.Equal(3, tones);
        }

        [Fact]
        public void AudioAndDemandDoNotAdvanceAnEffectsWatermark()
        {
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a") });
            foreach (var type in new[] { LinkMessageType.Audio, LinkMessageType.SourceDemand })
                Assert.True(new LinkIncomingFrame(lifetime, type, 0, 50, new byte[4]).TryCommit(0, () => { }));
            Assert.True(new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 2, new byte[4]).TryCommit(2, () => { }));
        }

        [Fact]
        public void RemovedIdentitiesKeepTheirSlotsUntilTheSessionIsRetired()
        {
            var sent = new List<byte[]>();
            var first = LinkLifetimeFixtures.Info("id0", 0);
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { first }, (type, _, _, bytes) =>
            {
                if (type == LinkMessageType.DeviceList) sent.Add(bytes);
                return true;
            });
            var original = lifetime.CaptureInputBindings();
            for (int i = 1; i < 256; i++)
            {
                Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                    lifetime.PublishLocalInventory(new LinkDeviceInventory(i + 1, new[] { LinkLifetimeFixtures.Info("id" + i) })));
                var wire = Assert.Single(LinkConnection.DecodeDeviceList(sent[^1]));
                Assert.Equal((byte)i, wire.Slot);
                Assert.Equal("id" + i, wire.PeerLocalDeviceId);
            }
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Exhausted,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(257, new[] { LinkLifetimeFixtures.Info("id256") })));
            Assert.Equal("id255", lifetime.LocalDeviceId(255));
            Assert.False(lifetime.SendInput(original, "id0", new byte[] { 1 }, 1));
            lifetime.Retire();
            var next = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("id256") });
            Assert.True(next.SendInput(next.CaptureInputBindings(), "id256", new byte[] { 1 }, 2));
        }

        [Fact]
        public void ExhaustionCountsAllNewIdsBeforeReservingAnyOfThem()
        {
            var map = new LinkSlotReservations();
            var initial = Enumerable.Range(0, 255).Select(i => LinkLifetimeFixtures.Info("id" + i, (byte)i)).ToArray();
            Assert.True(map.TryPrepare(initial, out _, out var active));
            map.Publish(active);
            Assert.False(map.TryPrepare(new[] { LinkLifetimeFixtures.Info("new1"), LinkLifetimeFixtures.Info("new2") }, out _, out _));
            Assert.Equal(255, map.ReservedCount);
            Assert.True(map.TryPrepare(new[] { LinkLifetimeFixtures.Info("new1") }, out var wire, out _));
            Assert.Equal((byte)255, Assert.Single(wire).Slot);
        }

        [Fact]
        public void OneInputSnapshotUsesEachRecipientsOwnReservedSlot()
        {
            var wireSlots = new List<byte>();
            bool Send(LinkMessageType type, byte slot, ulong timestamp, byte[] bytes)
            {
                if (type == LinkMessageType.Input) wireSlots.Add(slot);
                return true;
            }
            var first = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a") }, Send);
            var b = new[] { LinkLifetimeFixtures.Info("b") };
            first.PublishLocalInventory(new LinkDeviceInventory(2, b));
            var second = LinkLifetimeFixtures.Lifetime(b, Send);
            var targets = new LinkServer.InputTargets(new[]
            {
                (first, first.CaptureInputBindings()), (second, second.CaptureInputBindings())
            });
            using var server = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
            server.PushLocalFrame(targets, "b", CustomInputStateCodec.CreateNeutral(), new CustomInputStateCodec.Caps(false, false), 1);
            Assert.Equal(new byte[] { 1, 0 }, wireSlots);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailedInventorySendKeepsAWorkCapturedBeforeAndDuringItsAttempt(bool throws)
        {
            var a = LinkLifetimeFixtures.Info("a", 0);
            var b = LinkLifetimeFixtures.Info("b", 1);
            LinkConnectionLifetime lifetime = null;
            LinkIncomingFrame during = null;
            LinkEffectTicket tone = null;
            IReadOnlyDictionary<byte, LinkSourceBinding> inputDuring = null;
            lifetime = LinkLifetimeFixtures.Lifetime(new[] { a }, (type, _, _, _) =>
            {
                if (type != LinkMessageType.DeviceList) return true;
                during = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 1, OutputEffectCodec.EncodeVibration(new Vibration()));
                inputDuring = lifetime.CaptureInputBindings();
                var toneFrame = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 2, OutputEffectCodec.EncodeHapticTone(100, 1));
                Assert.True(toneFrame.TryCommit(4, () => { }));
                tone = toneFrame.Ticket(4);
                if (throws) throw new InvalidOperationException("inventory send");
                return false;
            });
            var inputBefore = lifetime.CaptureInputBindings();
            var before = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 0, OutputEffectCodec.EncodeVibration(new Vibration()));
            var changed = new LinkDeviceInventory(2, new[] { a, b });
            if (throws) Assert.Throws<InvalidOperationException>(() => lifetime.PublishLocalInventory(changed));
            else Assert.Equal(LinkConnectionLifetime.InventoryResult.Unavailable, lifetime.PublishLocalInventory(changed));
            Assert.Same(inputBefore, lifetime.CaptureInputBindings());
            Assert.NotSame(inputBefore, inputDuring);
            Assert.Same(inputBefore[0], inputDuring[0]);
            int stops = 0, tones = 0;
            Assert.True(before.TryCommit(2, () => stops++));
            Assert.True(during.TryCommit(2, () => stops++));
            Assert.True(tone.IsCurrent);
            Assert.True(tone.TryCommit(() => tones++));
            Assert.True(lifetime.SendInput(inputBefore, "a", new byte[1], 1));
            Assert.True(lifetime.SendInput(inputDuring, "a", new byte[1], 2));
            Assert.False(lifetime.SendInput(inputDuring, "b", new byte[1], 3));
            Assert.Equal(2, stops);
            Assert.Equal(1, tones);
        }

        [Fact]
        public void InventorySendCanRetireItsConnectionWithoutWaitingForTheSendGate()
        {
            LinkConnectionLifetime lifetime = null;
            lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a") }, (type, _, _, _) =>
            {
                if (type == LinkMessageType.DeviceList)
                    Assert.True(Task.Run(() => lifetime.Retire()).Wait(TimeSpan.FromSeconds(5)));
                return true;
            });
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Unavailable,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(2, new[] { LinkLifetimeFixtures.Info("a"), LinkLifetimeFixtures.Info("b", 1) })));
            Assert.False(lifetime.IsCurrent);
            Assert.False(lifetime.SendInput(lifetime.CaptureInputBindings(), "a", new byte[1], 1));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void FailedInventoryPublicationRestoresThePreviousInputSnapshot(bool throws)
        {
            bool reject = true;
            int inputs = 0;
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a", 19) }, (type, _, _, _) =>
            {
                if (type == LinkMessageType.Input) { inputs++; return true; }
                if (reject && throws) throw new InvalidOperationException("send failed");
                return !reject;
            });
            var original = lifetime.CaptureInputBindings();
            var changed = new LinkDeviceInventory(2, new[] { LinkLifetimeFixtures.Info("b", 19) });
            if (throws) Assert.Throws<InvalidOperationException>(() => lifetime.PublishLocalInventory(changed));
            else Assert.Equal(LinkConnectionLifetime.InventoryResult.Unavailable, lifetime.PublishLocalInventory(changed));
            Assert.Same(original, lifetime.CaptureInputBindings());
            Assert.True(lifetime.SendInput(original, "a", new byte[1], 1));
            Assert.False(lifetime.SendInput(original, "b", new byte[1], 1));
            reject = false;
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent, lifetime.PublishLocalInventory(changed));
            Assert.True(lifetime.SendInput(lifetime.CaptureInputBindings(), "b", new byte[1], 2));
            Assert.Equal(2, inputs);
        }

        [Fact]
        public async Task NewSlotInputWaitsUntilItsInventorySendCompletes()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var inputStarted = new ManualResetEventSlim();
            var order = new List<LinkMessageType>();
            var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info("a") }, (type, _, _, _) =>
            {
                if (type == LinkMessageType.DeviceList)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                }
                order.Add(type);
                return true;
            });
            var publish = Task.Run(() => lifetime.PublishLocalInventory(new LinkDeviceInventory(2,
                new[] { LinkLifetimeFixtures.Info("b") })));
            Task<bool> input = null;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var captured = lifetime.CaptureInputBindings();
                input = Task.Run(() => { inputStarted.Set(); return lifetime.SendInput(captured, "b", new byte[1], 2); });
                Assert.True(inputStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotSame(input, await Task.WhenAny(input, Task.Delay(100)));
            }
            finally { release.Set(); }
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent, await publish.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await input.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { LinkMessageType.DeviceList, LinkMessageType.Input }, order);
        }

        [Fact]
        public void SparsePeerInventoryRejectsSlotRebindingAndOlderInputForANewExposure()
        {
            var lifetime = LinkLifetimeFixtures.Lifetime(Array.Empty<RemotePeerDeviceInfo>());
            Assert.True(lifetime.AcceptPeerInventory(new[] { LinkLifetimeFixtures.Info("a", 230) }, 0, true, out _));
            int inputs = 0;
            Assert.True(lifetime.TryApplyInput(230, 0, () => inputs++));
            Assert.False(lifetime.TryApplyInput(1, 1, () => inputs++));
            Assert.True(lifetime.AcceptPeerInventory(Array.Empty<RemotePeerDeviceInfo>(), 2, false, out _));
            Assert.False(lifetime.TryApplyInput(230, 3, () => inputs++));
            Assert.True(lifetime.AcceptPeerInventory(new[] { LinkLifetimeFixtures.Info("a", 230) }, 5, false, out _));
            Assert.False(lifetime.TryApplyInput(230, 4, () => inputs++));
            Assert.True(lifetime.TryApplyInput(230, 6, () => inputs++));
            Assert.False(lifetime.AcceptPeerInventory(new[] { LinkLifetimeFixtures.Info("b", 230) }, 7, false, out bool conflict));
            Assert.True(conflict);
            Assert.False(lifetime.AcceptPeerInventory(Array.Empty<RemotePeerDeviceInfo>(), 4, false, out _));
            Assert.True(lifetime.TryApplyInput(230, 8, () => inputs++));
            Assert.Equal(3, inputs);
        }
    }
}
