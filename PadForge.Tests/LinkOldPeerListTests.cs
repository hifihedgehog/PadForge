using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A device list too large for a Remote Link peer older than 3.6.0. That
    /// peer's UDP loop reads 4 KB and drops a larger datagram, and it parses
    /// the v1 records alone, while every list update after connect travels as
    /// one datagram. A list past 4 KB went out anyway and the peer dropped
    /// every update. It now gets the records alone, and a list past even that
    /// is not sent.
    /// </summary>
    public class LinkOldPeerListTests
    {
        private static RemotePeerDeviceInfo Device(int i, string serial = "", string name = null) => new()
        {
            PeerLocalDeviceId = "dev" + i, Slot = (byte)i, Name = name ?? ("Pad " + i),
            VendorId = 0x1234, ProductId = 0x5678, NumAxes = 6, NumButtons = 17, NumHats = 1,
            Online = true, InputDeviceType = InputDeviceType.Gamepad, SerialNumber = serial,
        };

        private static RemotePeerDeviceInfo[] Devices(int n, string serial = "", int nameLength = 0)
            => Enumerable.Range(0, n)
                .Select(i => Device(i, serial, nameLength > 0 ? new string('n', nameLength) + i : null))
                .ToArray();

        private static bool ReadsFull(byte[] list)
        {
            LinkConnection.DecodeDeviceList(list, out _, out bool full);
            return full;
        }

        private static LinkConnectionLifetime Lifetime(bool full, List<byte[]> sent)
            => new("owner", new LinkExposureSnapshot(new LinkDeviceInventory(1, Devices(1))), () => true,
                (type, _, _, payload) =>
                {
                    if (type == LinkMessageType.DeviceList) sent.Add(payload);
                    return true;
                },
                peerReadsFullLists: full);

        [Fact]
        public void TheMetadataExtension_MarksAPeerThatReadsFullLists()
        {
            var devices = Devices(3);
            byte[] full = LinkConnection.EncodeDeviceList(devices);
            byte[] basic = LinkConnection.EncodeBasicDeviceList(devices);

            // The records alone are the full list's first bytes, and the
            // extension's marker follows them there.
            Assert.Equal(basic, full.Take(basic.Length).ToArray());
            Assert.Equal(0xE2, full[basic.Length]);

            var fromFull = LinkConnection.DecodeDeviceList(full, out _, out bool fullReads);
            var fromBasic = LinkConnection.DecodeDeviceList(basic, out _, out bool basicReads);
            Assert.True(fullReads);
            Assert.False(basicReads);
            Assert.Equal(fromFull.Select(d => (d.Slot, d.PeerLocalDeviceId, d.Name, d.VendorId, d.ProductId, d.Online)),
                fromBasic.Select(d => (d.Slot, d.PeerLocalDeviceId, d.Name, d.VendorId, d.ProductId, d.Online)));

            // An empty list says the same.
            Assert.True(ReadsFull(LinkConnection.EncodeDeviceList(Array.Empty<RemotePeerDeviceInfo>())));
            Assert.False(ReadsFull(LinkConnection.EncodeBasicDeviceList(Array.Empty<RemotePeerDeviceInfo>())));
        }

        [Fact]
        public void AnOldPeer_GetsTheRecordsAlone_WhereTheFullListWouldNotFit()
        {
            var sent = new List<byte[]>();
            var lifetime = Lifetime(full: false, sent);
            // Long serials put the full list past the old peers' 3800 bytes,
            // and the records stay small.
            var devices = Devices(20, serial: new string('s', 250));
            Assert.True(LinkConnection.EncodeDeviceList(devices).Length > LinkConnection.OldPeerPayloadBudget);

            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(2, devices)));
            var payload = Assert.Single(sent);
            Assert.True(payload.Length <= LinkConnection.OldPeerPayloadBudget);
            Assert.False(ReadsFull(payload));
            Assert.Equal(20, LinkConnection.DecodeDeviceList(payload).Count);
        }

        [Fact]
        public void AListPastWhatThePeerReads_IsNotSent_AndTheBindingsStay()
        {
            var sent = new List<byte[]>();
            var lifetime = Lifetime(full: false, sent);
            var before = lifetime.CaptureInputBindings();
            // Even the records pass 3800 bytes.
            var devices = Devices(40, nameLength: 100);
            Assert.True(LinkConnection.EncodeBasicDeviceList(devices).Length > LinkConnection.OldPeerPayloadBudget);

            Assert.Equal(LinkConnectionLifetime.InventoryResult.TooLarge,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(2, devices)));
            Assert.Empty(sent);
            Assert.Same(before, lifetime.CaptureInputBindings());

            // A peer that reads full lists takes the same list whole.
            var fullSent = new List<byte[]>();
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                Lifetime(full: true, fullSent).PublishLocalInventory(new LinkDeviceInventory(2, devices)));
            Assert.True(ReadsFull(Assert.Single(fullSent)));
        }

        [Fact]
        public void AListThatIsNotSent_GivesBackTheSlotsItReserved()
        {
            // Slots are reserved per device id for the session and capped at
            // 256. An oversized list the peer never saw kept its new ids'
            // slots, so resubmitting one with new ids exhausted them, and
            // exhaustion forces a rekey.
            var sent = new List<byte[]>();
            var lifetime = Lifetime(full: false, sent);
            var reservations = typeof(LinkConnectionLifetime)
                .GetField("_local", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(reservations);
            int Reserved() => ((LinkSlotReservations)reservations.GetValue(lifetime)).ReservedCount;
            int before = Reserved();

            for (int round = 0; round < 7; round++)
            {
                var fresh = Enumerable.Range(0, 40)
                    .Select(i => Device(i, name: new string('n', 100) + i))
                    .Select(d => { d.PeerLocalDeviceId = $"r{round}d{d.Slot}"; return d; })
                    .ToArray();
                Assert.Equal(LinkConnectionLifetime.InventoryResult.TooLarge,
                    lifetime.PublishLocalInventory(new LinkDeviceInventory(2 + round, fresh)));
                Assert.Equal(before, Reserved());
            }
            Assert.Empty(sent);

            // A list that fits still goes out.
            Assert.Equal(LinkConnectionLifetime.InventoryResult.Sent,
                lifetime.PublishLocalInventory(new LinkDeviceInventory(20, Devices(3))));
            Assert.Single(sent);
        }

        [Fact]
        public void AFullListPastTheUdpLimit_IsNotSent()
        {
            var sent = new List<byte[]>();
            var devices = new[] { Device(0, name: new string('n', 70000)) };
            Assert.Equal(LinkConnectionLifetime.InventoryResult.TooLarge,
                Lifetime(full: true, sent).PublishLocalInventory(new LinkDeviceInventory(2, devices)));
            Assert.Empty(sent);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void TheServer_PassesThePeersVersion_AndARekeyTakesATooLargeList()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.Engine", "RemoteLink", "LinkServer.cs"))
                .Replace("\r\n", "\n");
            Assert.Contains("SendConnectionFrame(conn, type, slot, stamp, payload),\n                    result.PeerReadsFullLists);", src);
            // A rekey that treated it as a failure would drop the link to an
            // old peer and retry forever.
            Assert.Contains("or LinkConnectionLifetime.InventoryResult.TooLarge;", src);
        }
    }
}
