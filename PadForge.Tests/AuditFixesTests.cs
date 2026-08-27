using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the behavioral fixes from the 2026-08-13 audit. Each test fails
    /// against the code as it stood before its fix, which is the only property
    /// that makes a regression test worth writing.
    /// </summary>
    public class AuditFixesTests
    {
        // ── Bencode bounds (the DHT parses this straight off the internet) ──

        [Fact]
        public void Bencode_LengthNearIntMax_IsRejected_NotOverflowed()
        {
            // "2147483647:" then nothing. start + len overflows to negative and
            // passed the old bounds check, which then handed Array.Copy a
            // length the buffer could not satisfy.
            var data = System.Text.Encoding.ASCII.GetBytes("2147483647:abc");
            Assert.Throws<FormatException>(() => Bencode.Decode(data));
        }

        [Fact]
        public void Bencode_HonestString_StillRoundTrips()
        {
            var data = System.Text.Encoding.ASCII.GetBytes("4:spam");
            Assert.Equal("spam", System.Text.Encoding.ASCII.GetString((byte[])Bencode.Decode(data)));
        }

        // ── Spray de-duplication keeps distinct IPv6 candidates ──

        [Fact]
        public void SprayTargets_TwoV6AddressesSharingTheLow32Bits_BothSurvive()
        {
            // MapToIPv4 keeps only the last four bytes, so these two collided
            // and the second was dropped from the spray.
            var a = new IPEndPoint(IPAddress.Parse("2001:db8::1:0:0:1"), 41000);
            var b = new IPEndPoint(IPAddress.Parse("2001:db8:1::2:0:0:1"), 41000);
            var targets = PortPredictor.BuildSprayTargets(null, null, new[] { a, b });
            Assert.Contains(a, targets);
            Assert.Contains(b, targets);
        }

        [Fact]
        public void SprayTargets_TrueDuplicate_IsStillDropped()
        {
            var a = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 5000);
            var b = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 5000);
            Assert.Single(PortPredictor.BuildSprayTargets(null, null, new[] { a, b }));
        }

        // ── The punch stops spraying the moment a path wins ──

        private sealed class CountingPunchTransport : IPunchTransport
        {
            public int Sent;
            public IPEndPoint Answer;         // the endpoint that "replies"
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }

            public Task SendToAsync(byte[] datagram, IPEndPoint destination, CancellationToken ct)
            {
                Interlocked.Increment(ref Sent);
                // The first candidate answers with a pong; every later send in
                // the SAME sweep is the waste this fix exists to stop.
                if (Answer != null && destination.Equals(Answer))
                {
                    var pong = (byte[])datagram.Clone();
                    pong[0] = HolePuncher.TagPong;
                    OnDatagram?.Invoke(Answer, pong);
                }
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Punch_StopsTheSweep_WhenAPathWinsMidway()
        {
            var nonce = new byte[HolePuncher.NonceLen];
            for (int i = 0; i < nonce.Length; i++) nonce[i] = (byte)(i + 1);

            var candidates = new List<IPEndPoint>();
            for (int p = 0; p < 200; p++)
                candidates.Add(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 30000 + p));

            var transport = new CountingPunchTransport { Answer = candidates[0] };
            var puncher = new HolePuncher(transport, nonce, TimeSpan.FromMilliseconds(50));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var won = await puncher.PunchAsync(candidates, cts.Token);

            Assert.Equal(candidates[0], won);
            // Before the fix the sweep ran all 200 candidates (and kept going).
            Assert.True(transport.Sent < 20,
                $"the spray kept firing after the win: {transport.Sent} probes sent");
        }

        // ── A KRPC reply only counts from the node we asked ──

        private sealed class WrongSourceTransport : IKrpcTransport
        {
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }
            private static readonly IPEndPoint Impostor = new(IPAddress.Parse("198.51.100.66"), 6881);

            public Task SendAsync(byte[] datagram, IPEndPoint node, CancellationToken ct)
            {
                // Answer every query from the WRONG endpoint, echoing the
                // transaction id the query carried.
                var txn = Bencode.GetBytes(Bencode.Decode(datagram), "t");
                if (txn != null) OnDatagram?.Invoke(Impostor, BuildReply(txn));
                return Task.CompletedTask;
            }

            /// <summary>A minimal well-formed KRPC response: d1:rd2:id20:...e1:t2:..1:y1:re</summary>
            internal static byte[] BuildReply(byte[] txn)
            {
                var ms = new System.IO.MemoryStream();
                void Ascii(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
                Ascii("d1:rd2:id20:");
                ms.Write(new byte[20], 0, 20);
                Ascii("e1:t" + txn.Length + ":");
                ms.Write(txn, 0, txn.Length);
                Ascii("1:y1:re");
                return ms.ToArray();
            }
        }

        [Fact]
        public async Task Krpc_AReplyFromAnotherEndpoint_DoesNotSatisfyTheRpc()
        {
            var transport = new WrongSourceTransport();
            using var store = new DhtPresenceStore(
                transport, new[] { new IPEndPoint(IPAddress.Parse("203.0.113.1"), 6881) });

            // Reach RpcAsync directly: the impostor's reply carries the right
            // transaction id, and before the fix that alone completed the call.
            var rpc = typeof(DhtPresenceStore).GetMethod("RpcAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(rpc);
            var txn = new byte[] { 0x00, 0x01 };
            var datagram = Krpc.FindNode(new byte[20], new byte[20], txn);
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var task = (Task)rpc.Invoke(store, new object[]
            {
                datagram, txn, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 6881), ct.Token
            });
            await task;
            object result = task.GetType().GetProperty("Result").GetValue(task);
            Assert.Null(result);
        }

        // ── The web pad: input bounds, motion staleness, capability notify ──

        private static WebControllerDevice NewPad() =>
            new WebControllerDevice("audit-" + Guid.NewGuid().ToString("N"), "Audit Pad");

        [Fact]
        public void WebPad_AxisValuesClampToTheDeclaredRange()
        {
            var pad = NewPad();
            pad.UpdateAxis(0, 1_000_000);
            pad.UpdateAxis(1, -5);
            var s = pad.GetCurrentState();
            Assert.Equal(65535, s.Axis[0]);
            Assert.Equal(0, s.Axis[1]);
        }

        [Fact]
        public void WebPad_AnImpossibleHatAngleCenters()
        {
            var pad = NewPad();
            pad.UpdatePov(9000);
            Assert.Equal(9000, pad.GetCurrentState().Povs[0]);
            pad.UpdatePov(70000);
            Assert.Equal(-1, pad.GetCurrentState().Povs[0]);
        }

        [Fact]
        public void WebPad_TouchpadFlip_RaisesCapabilitiesChanged_Once()
        {
            var pad = NewPad();
            int raised = 0;
            pad.CapabilitiesChanged += () => raised++;
            pad.HasTouchpad = true;
            pad.HasTouchpad = true;   // idempotent
            Assert.Equal(1, raised);
            Assert.True(pad.HasTouchpad);
        }

        [Fact]
        public void WebPad_GyroZeroesWhenTheStreamStops_AccelKeepsGravity()
        {
            var pad = NewPad();
            pad.EnableMotionCaps();
            pad.UpdateMotion(1.5f, -2f, 0.5f, 0f, 9.81f, 0f);

            var fresh = pad.GetCurrentState();
            Assert.Equal(1.5f, fresh.Gyro[0]);

            // Past the staleness window the rates go to zero, because a latched
            // rate reads as a controller spinning forever. Gravity stays.
            var last = typeof(WebControllerDevice).GetField("_lastMotionTicks",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(last);
            last.SetValue(pad, Environment.TickCount64 - 5000);

            var stale = pad.GetCurrentState();
            Assert.Equal(0f, stale.Gyro[0]);
            Assert.Equal(0f, stale.Gyro[1]);
            Assert.Equal(0f, stale.Gyro[2]);
            Assert.Equal(9.81f, stale.Accel[1]);
        }
    }
}
