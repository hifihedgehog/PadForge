using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class RemoteFrameApplicationTests : IDisposable
    {
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly Func<string, bool> _savedMembership = RemoteLinkOutputRouter.IsPeerConnected;
        private readonly InputService _input = new(new MainViewModel());
        private readonly LinkServer _server = new(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
        private readonly List<UserDevice> _devices = new();
        private static readonly MethodInfo Receive = typeof(InputService).GetMethod("OnRemoteFrameReceived", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo Inventory = typeof(InputService).GetMethod("BuildExposedDevices", BindingFlags.NonPublic | BindingFlags.Instance);

        public RemoteFrameApplicationTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            RemoteLinkOutputRouter.IsPeerConnected = _ => true;
            typeof(InputService).GetField("_linkServer", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_input, _server);
        }

        private (UserDevice Device, List<(ushort, ushort)> Writes) Device()
        {
            var web = new WebControllerDevice(Guid.NewGuid().ToString("N"), "Browser pad");
            var device = new UserDevice();
            device.LoadFromWebDevice(web);
            device.IsOnline = true;
            var writes = new List<(ushort, ushort)>();
            web.RumbleRequested += (left, right) => writes.Add((left, right));
            SettingsManager.UserDevices.Items.Add(device);
            _devices.Add(device);
            return (device, writes);
        }
        private LinkDeviceInventory Rebuild() => (LinkDeviceInventory)Inventory.Invoke(_input, null);
        private void Deliver(LinkIncomingFrame frame, LinkServer origin = null)
            => Receive.Invoke(_input, new object[] { origin ?? _server, frame });
        private static LinkIncomingFrame VibrationFrame(LinkConnectionLifetime lifetime, byte slot, uint seq, ushort value)
            => new(lifetime, LinkMessageType.Output, slot, seq, OutputEffectCodec.EncodeVibration(new Vibration(value, 0)));

        [Fact]
        public void WaitingReceiveDoesNotHoldTheCommitGateAndCannotApplyAfterRetirement()
        {
            var (device, writes) = Device();
            var lifetime = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(Rebuild()), () => true, (_, _, _, _) => true);
            Deliver(VibrationFrame(lifetime, 0, 1, 10000));
            Assert.Single(writes);
            Assert.True(RemoteLinkOutputRouter.PeerWroteLast(device.DevicePath));
            Assert.Empty(SettingsManager.UserSettings.Items); // The physical owner needs no local assignment.
            var stale = VibrationFrame(lifetime, 0, 2, 20000);
            using var entered = new ManualResetEventSlim();
            using var received = new ManualResetEventSlim();
            using var retired = new ManualResetEventSlim();
            Exception failure = null;
            var receiver = new Thread(() =>
            {
                try { entered.Set(); Deliver(stale); }
                catch (Exception ex) { failure = ex; }
                finally { received.Set(); }
            });
            var retirement = new Thread(() => { lifetime.Retire(); retired.Set(); });
            Monitor.Enter(device.OutputSync);
            try
            {
                receiver.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(received.Wait(100));
                retirement.Start();
                Assert.True(retired.Wait(TimeSpan.FromSeconds(5)), "Retirement waited for a receive blocked on OutputSync.");
                Assert.Single(writes);
            }
            finally
            {
                Monitor.Exit(device.OutputSync);
                receiver.Join(5000);
                if (retirement.ThreadState != ThreadState.Unstarted) retirement.Join(5000);
            }
            Assert.Null(failure);
            Assert.True(received.IsSet);
            Assert.True(device.IsOnline);
            Assert.True(RemoteLinkOutputRouter.IsPeerConnected("owner"));
            Assert.Single(writes);
            var next = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(Rebuild()), () => true, (_, _, _, _) => true);
            Deliver(VibrationFrame(next, 0, 0, 30000));
            Assert.Equal(2, writes.Count);
            using var differentServer = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
            Deliver(VibrationFrame(next, 0, 1, 40000), differentServer);
            Assert.Equal(2, writes.Count);
        }

        [Fact]
        public void AnotherDeviceAppearingCannotDiscardAStopWaitingForOutputSync()
        {
            var (device, writes) = Device();
            var lifetime = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(Rebuild()), () => true, (_, _, _, _) => true);
            Deliver(VibrationFrame(lifetime, 0, 0, 10000));
            Assert.Single(writes);
            var stop = VibrationFrame(lifetime, 0, 1, 0);
            using var entered = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            Exception failure = null;
            var receiver = new Thread(() =>
            {
                try { entered.Set(); Deliver(stop); }
                catch (Exception ex) { failure = ex; }
                finally { completed.Set(); }
            });
            Monitor.Enter(device.OutputSync);
            try
            {
                receiver.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(completed.Wait(100));
                Device();
                Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent, lifetime.PublishLocalInventory(Rebuild()));
            }
            finally
            {
                Monitor.Exit(device.OutputSync);
                receiver.Join(5000);
            }
            Assert.Null(failure);
            Assert.True(completed.IsSet);
            Assert.Equal(new[] { ((ushort)10000, (ushort)0), ((ushort)0, (ushort)0) }, writes);
            Assert.False(device.ForceFeedbackState.IsActive);

            var oldActivation = VibrationFrame(lifetime, 0, 2, 20000);
            device.IsOnline = false;
            lifetime.PublishLocalInventory(Rebuild());
            device.IsOnline = true;
            lifetime.PublishLocalInventory(Rebuild());
            Deliver(oldActivation);
            Assert.Equal(2, writes.Count);
            Deliver(VibrationFrame(lifetime, 0, 3, 20000));
            Assert.Equal(3, writes.Count);
            Assert.True(device.ForceFeedbackState.IsActive);
        }

        [Fact]
        public void AReorderedNonzeroCannotOverwriteZeroAndMalformedPacketsDoNotConsumeOrder()
        {
            var (_, writes) = Device();
            var lifetime = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(Rebuild()), () => true, (_, _, _, _) => true);
            Deliver(VibrationFrame(lifetime, 0, 1, 10000));
            Deliver(new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 7, new byte[] { 2 }));
            Deliver(VibrationFrame(lifetime, 0, 3, 0));
            Deliver(VibrationFrame(lifetime, 0, 2, 20000));
            Assert.Equal(new[] { ((ushort)10000, (ushort)0), ((ushort)0, (ushort)0) }, writes);
            Deliver(new LinkIncomingFrame(lifetime, LinkMessageType.SourceDemand, 0, 6, new byte[] { 1 }));
            Deliver(VibrationFrame(lifetime, 0, 4, 30000));
            Assert.Equal(3, writes.Count);
        }

        [Fact]
        public void ReusingTheApplicationSlotCannotRedirectOldFramesToANewPhysicalSource()
        {
            var (first, firstWrites) = Device();
            var lifetime = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(Rebuild()), () => true, (_, _, _, _) => true);
            var stale = VibrationFrame(lifetime, 0, 1, 10000);
            first.IsOnline = false;
            Rebuild();
            var (second, secondWrites) = Device();
            var inventory = Rebuild();
            Assert.Equal((byte)0, Assert.Single(inventory).Slot);
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent, lifetime.PublishLocalInventory(inventory));
            Deliver(stale);
            Deliver(VibrationFrame(lifetime, 0, 2, 10000));
            Assert.Empty(firstWrites);
            Assert.Empty(secondWrites);
            Assert.Equal(second.InstanceGuid.ToString("N"), lifetime.LocalDeviceId(1));
            Deliver(VibrationFrame(lifetime, 1, 3, 20000));
            Assert.Single(secondWrites);
        }

        [Fact]
        public void AudioAndDemandRejectRetiredConnectionsAndReachTheCurrentSource()
        {
            var (device, _) = Device();
            var inventory = Rebuild();
            var old = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(inventory), () => true, (_, _, _, _) => true);
            var audio = new LinkIncomingFrame(old, LinkMessageType.Audio, 0, 1, new byte[] { 1, 2, 3, 4 });
            var demand = new LinkIncomingFrame(old, LinkMessageType.SourceDemand, 0, 2, new byte[] { 1 });
            var audioDemand = (ConcurrentDictionary<Guid, long>)typeof(AudioPassthroughService)
                .GetField("_remoteAudioDemand", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var rings = (ConcurrentDictionary<Guid, AudioPassthroughService.RemoteAudioRing>)typeof(AudioPassthroughService)
                .GetField("_remoteRings", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var nfcDemand = (ConcurrentDictionary<Guid, long>)typeof(InputService)
                .GetField("_remoteNfcDemandMs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_input);
            // Existing demand prevents the audio worker from starting. FeedRemoteAudio still writes its real ring.
            audioDemand[device.InstanceGuid] = long.MinValue;
            try
            {
                old.Retire();
                Deliver(audio);
                Deliver(demand);
                Assert.False(rings.ContainsKey(device.InstanceGuid));
                Assert.False(nfcDemand.ContainsKey(device.InstanceGuid));
                Assert.Equal(long.MinValue, audioDemand[device.InstanceGuid]);
                var current = new LinkConnectionLifetime("owner", new LinkExposureSnapshot(inventory), () => true, (_, _, _, _) => true);
                Deliver(new LinkIncomingFrame(current, LinkMessageType.Audio, 0, 0, audio.Payload));
                Deliver(new LinkIncomingFrame(current, LinkMessageType.SourceDemand, 0, 1, demand.Payload));
                Assert.True(rings.ContainsKey(device.InstanceGuid));
                Assert.True(nfcDemand.ContainsKey(device.InstanceGuid));
                Assert.NotEqual(long.MinValue, audioDemand[device.InstanceGuid]);
            }
            finally
            {
                audioDemand.TryRemove(device.InstanceGuid, out _);
                rings.TryRemove(device.InstanceGuid, out _);
            }
        }

        [Fact]
        public void DelayedConnectNoticeCannotReplaceTheNewWrapperAndOfflineInventoryStillRegisters()
        {
            typeof(InputService).GetField("_inputManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(_input, new InputManager());
            var info = LinkLifetimeFixtures.Info(Guid.NewGuid().ToString("N"));
            info.PeerFingerprintHex = "owner";
            var scope = LinkLifetimeFixtures.Lifetime(Array.Empty<RemotePeerDeviceInfo>());
            var old = new RemotePeerDevice(info) { Connection = scope };
            old.SetConnected(false);
            Assert.False(old.IsAttached);
            Assert.False(old.IsRetired);
            Assert.True(_input.TryRegisterRemotePeer(_server, old));
            old.Dispose();
            var current = new RemotePeerDevice(info) { Connection = scope };
            Assert.True(_input.TryRegisterRemotePeer(_server, current));
            Assert.False(_input.TryRegisterRemotePeer(_server, old));
            Assert.Same(current, SettingsManager.FindDeviceByInstanceGuid(current.InstanceGuid).Device);
            Assert.True(current.IsAttached);
            RemoteLinkOutputRouter.Unregister(current.DevicePath);
        }

        public void Dispose()
        {
            foreach (var device in _devices) RemoteLinkOutputRouter.ReleaseDevice(device.DevicePath);
            _server.Dispose();
            RemoteLinkOutputRouter.IsPeerConnected = _savedMembership;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
        }
    }
}
