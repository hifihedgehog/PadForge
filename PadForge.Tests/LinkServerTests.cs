using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>Runs with suite parallelism disabled (round six): these
    /// three drive REAL loopback sockets with wall-clock deadlines, and
    /// under the full suite's parallel load they intermittently failed
    /// as a trio (twice on 2026-07-25, green in isolation six of six)
    /// while every other run was clean. Deadline blowout and
    /// ephemeral-port churn are both load-shaped; taking the class out
    /// of the parallel pool removes the load from the window instead of
    /// guessing at a longer deadline.</summary>
    [CollectionDefinition("RemoteLinkSockets", DisableParallelization = true)]
    public sealed class RemoteLinkSocketsCollection { }

    [Collection("RemoteLinkSockets")]
    public class LinkServerTests
    {
        /// <summary>An ephemeral port number free on BOTH protocols, verified
        /// with the server's exact binds (LinkServer.Start:
        /// TcpListener(IPAddress.Any, port) then UDP bind on Any:port).
        ///
        /// <para>Round nine captured half the mechanism: the probe tested TCP
        /// only, so candidates were unverified on UDP. Round twelve captured
        /// the rest: candidates came from a TCP port-0 bind, and Windows
        /// assigns ephemeral TCP ports SEQUENTIALLY, while this machine
        /// reserves a contiguous 900-port UDP exclusion block inside the
        /// dynamic range (netsh excludedportrange: 63832-64732). Whenever the
        /// rotor sat inside the block, forty CONSECUTIVE candidates were all
        /// UDP-dead and the trio failed as a burst, healing minutes later
        /// when the rotor walked out. Random candidates make probe failures
        /// independent, so an exclusion band can only ever eat single
        /// probes, never the loop. The residual TOCTOU gap between the
        /// probe's release and the caller's real bind is what the caller's
        /// retry loop is for.</para></summary>
        private static int FreePort()
        {
            for (int probe = 0; probe < 40; probe++)
            {
                int p = Random.Shared.Next(49152, 65536);
                TcpListener tcp = null;
                Socket udp = null;
                try
                {
                    tcp = new TcpListener(IPAddress.Any, p);
                    tcp.Start();
                    udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    udp.Bind(new IPEndPoint(IPAddress.Any, p));
                    return p;
                }
                catch (SocketException) { }
                finally
                {
                    try { udp?.Close(); } catch { }
                    try { tcp?.Stop(); } catch { }
                }
            }
            throw new InvalidOperationException(
                "no ephemeral port was free on both TCP and UDP after 40 probes");
        }

        /// <summary>Probe-then-bind keeps a residual TOCTOU gap: the OS can
        /// hand the probed port to another socket between the release and
        /// LinkServer's own bind. Retry on a fresh doubly-probed port
        /// until the bind lands. Twenty-five attempts because the observed
        /// failures came in bursts under heavy machine load, where a
        /// handful of consecutive losses is ordinary.</summary>
        private static int StartOnFreePort(LinkServer s, int avoid = -1)
        {
            for (int attempt = 0; attempt < 25; attempt++)
            {
                int p = FreePort();
                if (p == avoid) continue;
                s.Start(p);
                if (s.IsRunning) return p;
                s.Stop();
            }
            throw new InvalidOperationException("no ephemeral port would bind after 25 attempts");
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
