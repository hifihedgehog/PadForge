using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the second audit round: the findings the first round recorded
    /// instead of fixing. Mutation-verified, same discipline as
    /// <see cref="AuditFixesTests"/>.
    /// </summary>
    public class AuditRoundTwoTests
    {
        // ── STUN parsing ──

        private static byte[] BindingResponseV4(byte[] txId, string ip, ushort port, int declaredLenDelta = 0)
        {
            // header(20) + XOR-MAPPED-ADDRESS(4 + 8)
            var msg = new byte[20 + 12];
            msg[0] = 0x01; msg[1] = 0x01;                       // Binding success
            msg[2] = 0; msg[3] = (byte)(12 + declaredLenDelta); // attribute bytes
            msg[4] = 0x21; msg[5] = 0x12; msg[6] = 0xA4; msg[7] = 0x42;
            txId.CopyTo(msg, 8);
            msg[20] = 0x00; msg[21] = 0x20;                     // XOR-MAPPED-ADDRESS
            msg[22] = 0x00; msg[23] = 0x08;                     // length 8
            msg[24] = 0x00; msg[25] = 0x01;                     // reserved, IPv4
            ushort xport = (ushort)(port ^ (ushort)(StunClient.MagicCookie >> 16));
            msg[26] = (byte)(xport >> 8); msg[27] = (byte)xport;
            var raw = IPAddress.Parse(ip).GetAddressBytes();
            uint addr = (uint)((raw[0] << 24) | (raw[1] << 16) | (raw[2] << 8) | raw[3]);
            uint xaddr = addr ^ StunClient.MagicCookie;
            msg[28] = (byte)(xaddr >> 24); msg[29] = (byte)(xaddr >> 16);
            msg[30] = (byte)(xaddr >> 8); msg[31] = (byte)xaddr;
            return msg;
        }

        [Fact]
        public void Stun_AResponseClaimingMoreBodyThanItCarries_IsRejected()
        {
            StunClient.BuildBindingRequest(out var txId);
            var msg = BindingResponseV4(txId, "203.0.113.9", 41234, declaredLenDelta: 40);
            Assert.Null(StunClient.ParseBindingResponse(msg, txId));
        }

        [Fact]
        public void Stun_AnHonestResponse_StillParses()
        {
            StunClient.BuildBindingRequest(out var txId);
            var msg = BindingResponseV4(txId, "203.0.113.9", 41234);
            var ep = StunClient.ParseBindingResponse(msg, txId);
            Assert.NotNull(ep);
            Assert.Equal(IPAddress.Parse("203.0.113.9"), ep.Address);
            Assert.Equal(41234, ep.Port);
        }

        [Fact]
        public void Stun_AnIpv6MappedAddress_Decodes()
        {
            StunClient.BuildBindingRequest(out var txId);
            var expected = IPAddress.Parse("2001:db8::dead:beef");
            const ushort port = 51820;

            var msg = new byte[20 + 4 + 20];
            msg[0] = 0x01; msg[1] = 0x01;
            msg[2] = 0; msg[3] = 24;
            msg[4] = 0x21; msg[5] = 0x12; msg[6] = 0xA4; msg[7] = 0x42;
            txId.CopyTo(msg, 8);
            msg[20] = 0x00; msg[21] = 0x20;
            msg[22] = 0x00; msg[23] = 0x14;      // length 20
            msg[24] = 0x00; msg[25] = 0x02;      // reserved, IPv6
            ushort xport = (ushort)(port ^ (ushort)(StunClient.MagicCookie >> 16));
            msg[26] = (byte)(xport >> 8); msg[27] = (byte)xport;
            // X-Address = address XOR (cookie || transaction id)
            var raw = expected.GetAddressBytes();
            var mask = new byte[16];
            mask[0] = 0x21; mask[1] = 0x12; mask[2] = 0xA4; mask[3] = 0x42;
            Array.Copy(txId, 0, mask, 4, 12);
            for (int i = 0; i < 16; i++) msg[28 + i] = (byte)(raw[i] ^ mask[i]);

            var ep = StunClient.ParseBindingResponse(msg, txId);
            Assert.NotNull(ep);
            Assert.Equal(expected, ep.Address);
            Assert.Equal(port, ep.Port);
        }

        [Fact]
        public void Stun_AResponseWithSomeoneElsesTransactionId_IsRejected()
        {
            StunClient.BuildBindingRequest(out var mine);
            StunClient.BuildBindingRequest(out var theirs);
            var msg = BindingResponseV4(theirs, "203.0.113.9", 41234);
            Assert.Null(StunClient.ParseBindingResponse(msg, mine));
        }

        // ── Synthetic instance ids ──

        [Fact]
        public void SyntheticIds_AreStable_AndOutOfSdlsRange()
        {
            uint a = SyntheticInstanceId.From("web://client-42");
            uint b = SyntheticInstanceId.From("web://client-42");
            Assert.Equal(a, b);                                   // stable, unlike GetHashCode
            Assert.True(a >= SyntheticInstanceId.ReservedBase,    // never collides with an SDL id
                $"id {a} landed in SDL's range");
            Assert.True(SyntheticInstanceId.IsSynthetic(a));
            Assert.False(SyntheticInstanceId.IsSynthetic(3u));    // a real SDL joystick id
        }

        [Fact]
        public void SyntheticIds_DifferentIdentitiesDiffer()
        {
            Assert.NotEqual(SyntheticInstanceId.From("midi://a"), SyntheticInstanceId.From("midi://b"));
        }

        [Fact]
        public void WebPad_UsesTheReservedIdBand()
        {
            var pad = new WebControllerDevice("audit-band", "Audit Pad");
            Assert.True(SyntheticInstanceId.IsSynthetic(pad.SdlInstanceId));
        }

        // ── A built pad advertises only what it carries ──

        [Fact]
        public void CustomPad_AdvertisesOnlyItsOwnWidgets()
        {
            var pad = new WebControllerDevice("audit-custom", "Built Pad");
            // One stick (axes 0,1) and two buttons. No triggers, no D-pad.
            pad.SetCustomSurface(new[] { 0, 1 }, new[] { 0, 1 }, hasPov: false);

            Assert.Equal(new[] { 0, 1 }, pad.SupportedButtonIndices);

            var objects = pad.GetDeviceObjects();
            Assert.Equal(4, objects.Length);   // two axes + two buttons, no hat
            foreach (var o in objects)
                Assert.NotEqual(ObjectGuid.PovController, o.ObjectTypeGuid);
        }

        [Fact]
        public void StockPad_KeepsTheFullGamepadSurface()
        {
            var pad = new WebControllerDevice("audit-stock", "Stock Pad");
            Assert.Equal(11, pad.SupportedButtonIndices.Length);
            Assert.Equal(6 + 11 + 1, pad.GetDeviceObjects().Length);
        }

        // ── Rumble is claimed only when the browser can play it ──

        [Fact]
        public void WebPad_RumbleClaimFollowsTheClient()
        {
            var pad = new WebControllerDevice("audit-rumble", "Audit Pad");
            Assert.True(pad.HasRumble);        // assumed until the client says otherwise
            int raised = 0;
            pad.CapabilitiesChanged += () => raised++;
            pad.HasRumble = false;             // an iOS client with no Vibration API
            Assert.False(pad.HasRumble);
            Assert.Equal(1, raised);
        }

        // ── Presence sequence survives a restart ──

        [Fact]
        public void PresenceSlot_StartsAboveAnyPreviousRun()
        {
            var slot = new PresenceService.Slot();
            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Seeded from the clock, not from zero: a fresh process must publish
            // at a sequence higher than the record its last run left in the DHT.
            Assert.True(slot.Sequence > 1_700_000_000,
                $"sequence {slot.Sequence} would be rejected by nodes holding an older record");
            Assert.True(Math.Abs(slot.Sequence - unixNow) < 5);
        }

        // ── The punch spray is bounded, and says so ──

        private sealed class BlackholeTransport : IPunchTransport
        {
            public int Sent;
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }
            public Task SendToAsync(byte[] datagram, IPEndPoint destination, CancellationToken ct)
            {
                Interlocked.Increment(ref Sent);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Punch_StopsAtItsProbeBudget_WhenNothingAnswers()
        {
            var nonce = new byte[HolePuncher.NonceLen];
            var candidates = new List<IPEndPoint>();
            for (int p = 0; p < 2000; p++)
                candidates.Add(new IPEndPoint(IPAddress.Parse("203.0.113.20"), 20000 + p));

            var transport = new BlackholeTransport();
            var puncher = new HolePuncher(transport, nonce, TimeSpan.FromMilliseconds(1));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var won = await puncher.PunchAsync(candidates, cts.Token);

            Assert.Null(won);
            Assert.True(transport.Sent <= HolePuncher.MaxProbeBudget + candidates.Count,
                $"spray ran past its budget: {transport.Sent} probes");
            Assert.True(transport.Sent >= 1000, "the spray did not actually run");
        }
    }
}
