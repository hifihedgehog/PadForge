using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class RemoteRouteLifetimeTests
    {
        [Fact]
        public void ReplacementRoutesReassertAllFamiliesAndOldRoutesCannotChooseNewKeys()
        {
            var output = RemoteLinkOutputRouter.SendScopedOutput;
            var audio = RemoteLinkOutputRouter.SendScopedAudio;
            var demand = RemoteLinkOutputRouter.SendScopedDemand;
            string id = Guid.NewGuid().ToString("N");
            var info = LinkLifetimeFixtures.Info(id, 12);
            info.PeerFingerprintHex = "owner";
            var oldPeer = new RemotePeerDevice(info);
            var newInfo = LinkLifetimeFixtures.Info(id, 12);
            newInfo.PeerFingerprintHex = "owner";
            var newPeer = new RemotePeerDevice(newInfo);
            var packets = new List<(LinkConnectionLifetime Connection, LinkMessageType Type)>();
            LinkConnectionLifetime Create()
            {
                var connection = LinkLifetimeFixtures.Lifetime(Array.Empty<RemotePeerDeviceInfo>());
                Assert.True(connection.AcceptPeerInventory(new[] { info }, 0, true, out _));
                return connection;
            }
            var old = Create();
            var current = Create();
            bool Send(LinkConnectionLifetime connection, byte slot, string deviceId, byte[] payload, LinkMessageType type)
            {
                bool accepted = connection.Send(type, slot, payload, deviceId: deviceId);
                if (accepted) packets.Add((connection, type));
                return accepted;
            }
            RemoteLinkOutputRouter.SendScopedOutput = (connection, slot, deviceId, payload) => Send(connection, slot, deviceId, payload, LinkMessageType.Output);
            RemoteLinkOutputRouter.SendScopedAudio = (connection, slot, deviceId, payload) => Send(connection, slot, deviceId, payload, LinkMessageType.Audio);
            RemoteLinkOutputRouter.SendScopedDemand = (connection, slot, deviceId, payload) => Send(connection, slot, deviceId, payload, LinkMessageType.SourceDemand);
            string path = oldPeer.DevicePath;
            void ShipFamilies()
            {
                Assert.True(RemoteLinkOutputRouter.ShipSonyEffect(path, new byte[] { 1, 2 }));
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(10000, 0)));
                Assert.True(RemoteLinkOutputRouter.ShipWheel(path, false, false, 1000, 0,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 100, 900, 0, false));
                Assert.True(RemoteLinkOutputRouter.ShipHapticTone(path, 100, 0.5f));
                Assert.True(RemoteLinkOutputRouter.ShipPlayerIndex(path, 2));
                Assert.True(RemoteLinkOutputRouter.ShipGuideLed(path, 50));
            }
            try
            {
                RemoteLinkOutputRouter.Register(path, "owner", 12, old, oldPeer);
                ShipFamilies();
                Assert.Equal(6, packets.Count);
                var map = (System.Collections.Concurrent.ConcurrentDictionary<string, RemoteLinkOutputRouter.Target>)typeof(RemoteLinkOutputRouter)
                    .GetField("_byPath", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var captured = map[path];
                old.Retire();
                RemoteLinkOutputRouter.Register(path, "owner", 12, current, newPeer);
                RemoteLinkOutputRouter.Unregister(path, oldPeer);
                packets.Clear();
                Assert.False(RemoteLinkOutputRouter.StopVibration(path));
                Assert.Empty(packets);
                var dispatch = typeof(RemoteLinkOutputRouter).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var type in new[] { LinkMessageType.Output, LinkMessageType.Audio, LinkMessageType.SourceDemand })
                    Assert.False((bool)dispatch.Invoke(null, new object[] { captured, type, new byte[] { 1 } }));
                Assert.Empty(packets);
                ShipFamilies();
                Assert.Equal(6, packets.Count);
                Assert.True(RemoteLinkOutputRouter.ShipAudio(path, new byte[] { 1, 2, 3, 4 }));
                RemoteLinkOutputRouter.ShipNfcDemand(path);
                Assert.Equal(8, packets.Count);
                Assert.All(packets, packet => Assert.Same(current, packet.Connection));
                Assert.False(current.Send(LinkMessageType.Output, 12, new byte[] { 1 }, deviceId: "someone else"));
            }
            finally
            {
                RemoteLinkOutputRouter.Unregister(path);
                RemoteLinkOutputRouter.SendScopedOutput = output;
                RemoteLinkOutputRouter.SendScopedAudio = audio;
                RemoteLinkOutputRouter.SendScopedDemand = demand;
            }
        }
    }
}
