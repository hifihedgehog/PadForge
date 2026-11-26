using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Common.MadCatz;
using PadForge.Engine.Common.NativeHost;
using PadForge.Services.NativeHost;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The library host's channels past their first call: what a service
    /// sends stays inside what PadForge reads, a service that ends cleans up
    /// after itself, and a host that cannot be reached at once is reached
    /// again. Every channel is an in-process pair of anonymous pipes
    /// (<see cref="NativeHostTests.Pair"/>), and no vendor library loads.
    /// </summary>
    [Collection("NativeHostStatics")]
    public sealed class NativeHostChannelTests : IDisposable
    {
        private readonly List<IDisposable> _held = new();

        public void Dispose()
        {
            NativeHost.OpenSeam = null;
            AuraHostService.NativeSeam = null;
            foreach (var d in _held) d.Dispose();
        }

        private HostConnection Serve(IHostService service, List<HostConnection> servers = null)
        {
            var (client, server) = NativeHostTests.Pair();
            servers?.Add(server);
            var thread = new Thread(() =>
            {
                try { service.Serve(server); }
                catch (HostProtocolException) { }
                finally { server.Dispose(); }
            }) { IsBackground = true };
            thread.Start();
            _held.Add(client);
            return client;
        }

        // ── Aura ──

        private sealed class ManyLightsAura : IAuraSdkNative
        {
            public int Lights = 5000;
            public IReadOnlyList<AuraDevice> Devices { get; private set; } = Array.Empty<AuraDevice>();
            public bool Open(out string detail)
            {
                Devices = new[] { new AuraDevice(0, 0x80000, "Wall", Lights, Array.Empty<ushort>()) };
                detail = "1 devices";
                return true;
            }
            public void SetLight(int device, int index, int rgb) { }
            public void SetKey(int device, ushort code, int rgb) { }
            public void Apply(int device) { }
            public void Close() { }
        }

        /// <summary>A device with more lights than PadForge reads is sent
        /// with the lights PadForge reads, as its keys are, and a count below
        /// zero as none: the open goes through.</summary>
        [Theory]
        [InlineData(5000, AuraOps.MaxLights)]
        [InlineData(-3, 0)]
        public void AnAuraDevicesLights_AreSentWithinTheBound(int lights, int expected)
        {
            var fake = new ManyLightsAura { Lights = lights };
            AuraHostService.NativeSeam = () => fake;
            NativeHost.OpenSeam = service => service == AuraHostService.Name ? Serve(new AuraHostService()) : null;
            var sdk = new RemoteAuraSdk(NativeHost.Shared);
            Assert.True(sdk.Open(out string detail));
            Assert.Equal("1 devices", detail);
            Assert.Equal(expected, Assert.Single(sdk.Devices).LightCount);
            sdk.Close();
        }

        // ── The Strike 7 ──

        private sealed class CountingStrike7 : IStrike7Library
        {
            private readonly object _gate = new();
            private readonly HashSet<ushort> _open = new();
            public Action<Strike7Event> Subscriber;
            public readonly List<ushort> Draws = new();
            private ushort _next = 7;
            // The host keeps a library it loaded for its path: each test
            // names its own.
            public string Path { get; } = @"C:\Strike7\" + Guid.NewGuid().ToString("N") + @"\Strike7API.dll";
            public int Opened { get { lock (_gate) return _open.Count; } }
            public int OpenedEver { get { lock (_gate) return _next - 7; } }
            public ushort Open(string name, int w, int h, byte[] icon, Action<Strike7Event> subscriber)
            {
                lock (_gate)
                {
                    Subscriber = subscriber;
                    _open.Add(_next);
                    return _next++;
                }
            }
            public void Close(ushort applet) { lock (_gate) _open.Remove(applet); }
            public void DrawScreen(ushort applet, ushort endpoint, byte[] bgra) { lock (_gate) Draws.Add(applet); }
            public int DrawCount { get { lock (_gate) return Draws.Count; } }
        }

        private const int ScreenBytes = Strike7Library.ScreenWidth * Strike7Library.ScreenHeight * 4;

        /// <summary>A channel that ends while the host lives closes the
        /// applets it opened, so the applet PadForge opens again in the next
        /// channel is the only one.</summary>
        [Fact]
        public void AStrike7ChannelThatEnds_ClosesItsApplets()
        {
            var fake = new CountingStrike7();
            Strike7HostService.LoadSeam = _ => (fake, null);
            var clients = new List<HostConnection>();
            NativeHost.OpenSeam = service =>
            {
                if (service != Strike7HostService.Name) return null;
                var client = Serve(new Strike7HostService());
                lock (clients) clients.Add(client);
                return client;
            };
            IStrike7Library library = null;
            ushort applet = 0;
            try
            {
                (library, _) = RemoteStrike7Library.Load(NativeHost.Shared, fake.Path);
                var seen = new System.Collections.Concurrent.ConcurrentQueue<Strike7Event>();
                applet = library.Open("PadForge", 2, 2, new byte[16], seen.Enqueue);
                Assert.Equal(1, fake.Opened);
                // PadForge's side drops the channel, as a frame out of bounds
                // does: the applet opens again in a new one.
                lock (clients) clients[0].Dispose();
                Assert.True(SpinWait.SpinUntil(() => fake.OpenedEver == 2, NativeHostTests.PollWaitMs));
                Assert.True(SpinWait.SpinUntil(() => fake.Opened == 1, NativeHostTests.PollWaitMs));
                Assert.Equal(1, fake.Opened);
            }
            finally
            {
                if (library != null && applet != 0) library.Close(applet);
                Strike7HostService.LoadSeam = null;
            }
        }

        /// <summary>A host that cannot be reached when the channel breaks, as
        /// one inside its restart's pause, is reached on a later tick: the
        /// applet opens again with no call from the screen, and the screen
        /// hears one reset.</summary>
        [Fact]
        public void AStrike7HostRefusedAtTheBreak_IsReachedAgainLater()
        {
            var fake = new CountingStrike7();
            Strike7HostService.LoadSeam = _ => (fake, null);
            var servers = new List<HostConnection>();
            int refuse = 0;
            NativeHost.OpenSeam = service =>
            {
                if (service != Strike7HostService.Name) return null;
                if (Interlocked.Exchange(ref refuse, 0) == 1) return null;
                return Serve(new Strike7HostService(), servers);
            };
            IStrike7Library library = null;
            ushort applet = 0;
            try
            {
                (library, _) = RemoteStrike7Library.Load(NativeHost.Shared, fake.Path);
                var seen = new System.Collections.Concurrent.ConcurrentQueue<Strike7Event>();
                applet = library.Open("PadForge", 2, 2, new byte[16], seen.Enqueue);
                Volatile.Write(ref refuse, 1);
                servers[0].Dispose();
                Assert.True(SpinWait.SpinUntil(() => seen.Any(e => e.Kind == Strike7EventKind.Reset), NativeHostTests.PollWaitMs));
                Assert.True(SpinWait.SpinUntil(() => fake.OpenedEver == 2, NativeHostTests.PollWaitMs));
                library.DrawScreen(applet, 1, new byte[ScreenBytes]);
                Assert.True(SpinWait.SpinUntil(() => fake.DrawCount == 1, NativeHostTests.PollWaitMs));
                Assert.Equal(8, fake.Draws[0]);
                Assert.Single(seen, e => e.Kind == Strike7EventKind.Reset);
            }
            finally
            {
                if (library != null && applet != 0) library.Close(applet);
                Strike7HostService.LoadSeam = null;
            }
        }

        // ── OpenVR ──

        /// <summary>OpenVR's session is the host process's own, one at a time.
        /// A second channel's start ends the session that runs and opens its
        /// own, and the first channel's end then leaves it up.</summary>
        [Fact]
        public void AnOpenVrChannelThatEnds_LeavesAnotherChannelsSessionUp()
        {
            object gate = new();
            bool open = false;
            int overOpen = 0, ends = 0;
            OpenVrHostService.InitSeam = () =>
            {
                lock (gate)
                {
                    // VR_InitInternal2 over a session that runs replaces it
                    // without its cleanup.
                    if (open) overOpen++;
                    open = true;
                }
                return Valve.VR.EVRInitError.None;
            };
            OpenVrHostService.ShutdownSeam = () => { lock (gate) { open = false; ends++; } };
            try
            {
                var threads = new List<Thread>();
                HostConnection Start()
                {
                    var (client, server) = NativeHostTests.Pair();
                    var thread = new Thread(() =>
                    {
                        try { new OpenVrHostService().Serve(server); }
                        catch (HostProtocolException) { }
                        finally { server.Dispose(); }
                    }) { IsBackground = true };
                    thread.Start();
                    threads.Add(thread);
                    _held.Add(client);
                    return client;
                }
                bool Init(HostConnection c) => c.Call(w => w.Int(OpenVrOps.Init), r => { r.Int(); r.Text(OpenVrOps.MaxText); });

                var first = Start();
                Assert.True(Init(first));
                var second = Start();
                Assert.True(Init(second));
                first.Dispose();
                Assert.True(threads[0].Join(3000));
                lock (gate)
                {
                    Assert.True(open);
                    Assert.Equal(0, overOpen);
                }
                second.Dispose();
                Assert.True(threads[1].Join(3000));
                lock (gate)
                {
                    Assert.False(open);
                    Assert.Equal(2, ends);
                }
            }
            finally
            {
                OpenVrHostService.InitSeam = null;
                OpenVrHostService.ShutdownSeam = null;
            }
        }
    }
}
