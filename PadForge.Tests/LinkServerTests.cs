using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class LinkServerTests
    {
        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        /// <summary>Probe-then-bind is a TOCTOU race: the OS can hand the
        /// probed port to another socket in the gap (seen on the bench with
        /// the app running alongside the suite). Retry on a fresh ephemeral
        /// port until the bind lands.</summary>
        private static int StartOnFreePort(LinkServer s, int avoid = -1)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int p = FreePort();
                if (p == avoid) continue;
                s.Start(p);
                if (s.IsRunning) return p;
                s.Stop();
            }
            throw new InvalidOperationException("no ephemeral port would bind after 8 attempts");
        }

        private static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (cond()) return true;
                await Task.Delay(20);
            }
            return cond();
        }

        private static RemotePeerDeviceInfo PadInfo() => new()
        {
            PeerLocalDeviceId = "pad0", Name = "A Pad",
            VendorId = 0x054C, ProductId = 0x0CE6,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
        };

        [Fact]
        public async Task LocalhostLoopback_PairsAndStreamsInputOverRealSockets()
        {
            Func<PendingPairing, PairingApproval> approve = _ => true;
            using var host = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve);     // consumer
            using var holder = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve);   // device holder

            RemotePeerDevice received = null;
            host.DeviceConnected += d => received = d;

            int pA = StartOnFreePort(host);
            StartOnFreePort(holder, avoid: pA);

            bool connected = await holder.ConnectAsync("127.0.0.1", pA, new[] { PadInfo() });
            Assert.True(connected);

            Assert.True(await WaitUntil(() => received != null, 5000), "host never saw the remote device");
            Assert.Equal("A Pad", received.Name);
            Assert.StartsWith("peer://", received.DevicePath);

            // The holder streams its device's input over real UDP (a few frames —
            // a heartbeat would cover a drop; here loopback is lossless).
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[2] = true;
            state.Axis[0] = 4000;
            var caps = new CustomInputStateCodec.Caps(false, false);
            for (int i = 0; i < 5; i++)
            {
                holder.PushLocalFrame(0, state, caps, (ulong)(i + 1));
                await Task.Delay(10);
            }

            Assert.True(await WaitUntil(() => received.GetCurrentState()?.Buttons[2] == true, 5000),
                $"input never arrived. holder sent={holder.DiagDatagramsSent} holderErr={holder.DiagLastError} | host recv={host.DiagDatagramsReceived} opened={host.DiagDatagramsOpened} hostErr={host.DiagLastError}");
            var s = received.GetCurrentState();
            Assert.True(s.Buttons[2]);
            Assert.Equal(4000, s.Axis[0]);
        }

        [Fact]
        public async Task UnknownPeerRejected_NoDeviceReachesPipeline()
        {
            // Host rejects the pairing; holder approves. No device must appear on the host.
            using var host = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve: _ => false);
            using var holder = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), approve: _ => true);

            bool deviceAppeared = false;
            host.DeviceConnected += _ => deviceAppeared = true;

            int pA = StartOnFreePort(host);
            StartOnFreePort(holder, avoid: pA);

            await holder.ConnectAsync("127.0.0.1", pA, new[] { PadInfo() });
            await Task.Delay(300); // give any (erroneous) registration a chance to fire

            Assert.False(deviceAppeared);
        }

        [Fact]
        public void StartStop_IsClean()
        {
            var s = new LinkServer(PeerIdentity.Generate(), new PeerTrustStore(), _ => false);
            StartOnFreePort(s);
            Assert.True(s.IsRunning);
            s.Stop();
            Assert.False(s.IsRunning);
            s.Dispose();
        }
    }
}
