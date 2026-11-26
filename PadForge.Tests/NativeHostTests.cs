using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using PadForge.Engine.Common.ForceDimension;
using PadForge.Engine.Common;
using PadForge.Engine.Common.NativeHost;
using PadForge.Engine.Common.OpenXr;
using PadForge.Services.NativeHost;
using Valve.VR;
using Xunit;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Tests
{
    /// <summary>
    /// The library host (#597): third-party libraries from folders other
    /// accounts can change load in a process with the user's rights, and
    /// PadForge holds what it sends to bounds. These tests stand an
    /// in-process fake host on anonymous pipes, which carry no name, and
    /// load no vendor library.
    /// </summary>
    [Collection("NativeHostStatics")]
    public class NativeHostTests : IDisposable
    {
        private readonly List<IDisposable> _held = new();

        public void Dispose()
        {
            NativeHost.OpenSeam = null;
            FalconHostService.LoadSeam = null;
            OpenXrHostService.OpenSeam = null;
            foreach (var d in _held) d.Dispose();
        }

        private sealed class Streams : IDisposable
        {
            private readonly Stream[] _streams;
            public Streams(params Stream[] streams) => _streams = streams;
            public void Dispose()
            {
                foreach (var s in _streams) s.Dispose();
            }
        }

        /// <summary>A channel's two ends on two anonymous pipes.</summary>
        internal static (HostConnection Client, HostConnection Server) Pair(int generation = 0)
        {
            var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
            var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
            var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
            var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
            var client = new HostConnection(clientIn, toServer, new Streams(clientIn, toServer)) { Generation = generation };
            var server = new HostConnection(serverIn, toClient, new Streams(serverIn, toClient));
            return (client, server);
        }

        /// <summary>Serves <paramref name="service"/> on a thread, as the
        /// host's run does, and answers the client's end.</summary>
        private HostConnection Serve(IHostService service, List<HostConnection> servers = null, List<int> threads = null)
        {
            var (client, server) = Pair();
            servers?.Add(server);
            var thread = new Thread(() =>
            {
                lock (_held) threads?.Add(Environment.CurrentManagedThreadId);
                try { service.Serve(server); }
                catch (HostProtocolException) { }
                finally { server.Dispose(); }
            }) { IsBackground = true };
            thread.Start();
            _held.Add(client);
            return client;
        }

        // ── The protocol ──

        [Fact]
        public void AMessage_ReadsBackAsWritten_AndEveryReadIsBounded()
        {
            var w = new MessageWriter().Reset().Int(-7).UInt(7).Long(long.MinValue).ULong(ulong.MaxValue).Double(1.5).Float(-2.5f)
                .Bool(true).Byte(9).Text("Falcon\u202E").Text(null).Bytes(new byte[] { 1, 2, 3 });
            var buffer = w.Buffer.AsSpan(MessageWriter.HeaderSize, w.Length - MessageWriter.HeaderSize).ToArray();
            var r = new MessageReader();
            r.Reset(buffer, buffer.Length);
            Assert.Equal(-7, r.Int());
            Assert.Equal(7u, r.UInt());
            Assert.Equal(long.MinValue, r.Long());
            Assert.Equal(ulong.MaxValue, r.ULong());
            Assert.Equal(1.5, r.Double());
            Assert.Equal(-2.5f, r.Float());
            Assert.True(r.Bool());
            Assert.Equal(9, r.Byte());
            Assert.Equal("Falcon\u202E", r.Text(16));
            Assert.Null(r.Text(16));
            Assert.Equal(new byte[] { 1, 2, 3 }, r.Bytes(3));
            r.End();
            Assert.Throws<HostProtocolException>(() => r.Int());

            byte[] Read(Action<MessageWriter> write)
            {
                var m = new MessageWriter().Reset();
                write(m);
                return m.Buffer.AsSpan(MessageWriter.HeaderSize, m.Length - MessageWriter.HeaderSize).ToArray();
            }
            void Refused(Action<MessageWriter> write, Action<MessageReader> read)
            {
                var bytes = Read(write);
                var reader = new MessageReader();
                reader.Reset(bytes, bytes.Length);
                Assert.Throws<HostProtocolException>(() => read(reader));
            }
            Refused(m => m.Text("too long"), x => x.Text(3));
            Refused(m => m.Int(-2), x => x.Text(3));
            Refused(m => m.Int(1_000_000), x => x.Text(int.MaxValue));
            Refused(m => m.Int(5), x => x.Int(0, 4));
            Refused(m => m.Double(double.NaN), x => x.Double(-1, 1));
            Refused(m => m.Double(double.PositiveInfinity), x => x.Double(double.MinValue, double.MaxValue));
            Refused(m => m.Float(2f), x => x.Float(-1, 1));
            Refused(m => m.Byte(2), x => x.Bool());
            Refused(m => m.Int(4), x => x.Bytes(3));
            Refused(m => m.Int(1).Int(2), x => { x.Int(); x.End(); });
        }

        /// <summary>A frame past its bound ends the channel: a host cannot
        /// make PadForge allocate what it names.</summary>
        [Fact]
        public void AFramePastItsBound_EndsTheChannel()
        {
            var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
            var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
            var sink = new MemoryStream();
            var client = new HostConnection(clientIn, sink);
            _held.Add(toClient);
            _held.Add(client);
            toClient.Write(BitConverter.GetBytes(HostConnection.MaxFrame + 1));
            toClient.Flush();
            Assert.False(client.Call(w => w.Int(1), r => r.Int()));
            Assert.True(client.Broken);
            Assert.False(client.Call(w => w.Int(1), null));
        }

        [Fact]
        public void APipeName_IsTheHostsOwnForm()
        {
            string name = NativeHost.NewPipeName();
            Assert.True(NativeHost.IsPipeName(name));
            Assert.NotEqual(name, NativeHost.NewPipeName());
            Assert.False(NativeHost.IsPipeName(null));
            Assert.False(NativeHost.IsPipeName("PadForge.Automation"));
            Assert.False(NativeHost.IsPipeName(NativeHost.PipePrefix + new string('G', 32)));
            Assert.False(NativeHost.IsPipeName(name + "0"));
            Assert.True(NativeHostRun.IsRun(new[] { NativeHost.Switch, name }));
            Assert.False(NativeHostRun.IsRun(new[] { NativeHost.Switch, "x" }));
            Assert.Equal(NativeHostRun.ExitRefused, NativeHostRun.Run(new[] { NativeHost.Switch, "x" }));
            // The test host runs without rights, so a host run would not refuse
            // for that.
            Assert.False(NativeHostRun.RunsWithRights());
        }

        /// <summary>The test host starts no host: an open without a fake
        /// fails with why.</summary>
        [Fact]
        public void TheTestHost_StartsNoHost()
        {
            Assert.True(NativeHost.OffLimitsForTest);
            Assert.Null(NativeHost.Shared.Open(FalconHostService.Name, out string reason));
            Assert.Equal("the test host starts no library host", reason);
            Assert.Null(RemoteFalconLink.Load(NativeHost.Shared, null, out reason));
        }

        // ── The Falcon ──

        private sealed class FakeFalcon : IFalconLink
        {
            public double X = 0.01, Y = -0.02, Z = 0.03;
            public uint Buttons = 0b1011;
            public readonly List<(double, double, double)> Forces = new();
            public readonly HashSet<int> Threads = new();
            [ThreadStatic] private static int t_error;
            private void Saw() { lock (Threads) Threads.Add(Environment.CurrentManagedThreadId); }
            public int DeviceCount() { Saw(); return 2; }
            public int OpenIndex(int index) { Saw(); if (index > 1) { t_error = 7; return -1; } return index; }
            public int Close(int id) { Saw(); return 0; }
            public int SystemType(int id) => 60;
            public int SerialNumber(int id, out ushort serial) { serial = 4242; return 0; }
            public int GetPosition(int id, out double x, out double y, out double z) { Saw(); x = X; y = Y; z = Z; return 0; }
            public uint ButtonMask(int id) => Buttons;
            public int EnableForce(int id, bool on) => 0;
            public int SetForce(int id, double x, double y, double z) { lock (Forces) Forces.Add((x, y, z)); return 2; }
            public int SetMaxForce(int id, double newtons) => 0;
            public int SetGravityCompensation(int id, bool on) => 0;
            public int SetStandardGravity(int id, double g) => 0;
            public int GetStatus(int id, int[] status) { for (int i = 0; i < status.Length; i++) status[i] = i; return 0; }
            public double ComFrequency(int id) => 0.998;
            public int LastError() => t_error;
            public string LastErrorText() => t_error == 7 ? "no\u202Esuch\u0000device" : "ok";
        }

        private FakeFalcon UseFakeFalcon(List<int> threads = null, List<HostConnection> servers = null)
        {
            var fake = new FakeFalcon();
            FalconHostService.LoadSeam = picked => picked == @"C:\missing\dhd64.dll"
                ? (null, null, null, "missing")
                : (fake, @"C:\Program Files\Force Dimension\sdk-3.17.1\bin\dhd64.dll", "3.17.1", null);
            NativeHost.OpenSeam = service => service == FalconHostService.Name ? Serve(new FalconHostService(), servers, threads) : null;
            return fake;
        }

        [Fact]
        public void TheFalconsCalls_CrossTheHost_AsTheLibraryAnswersThem()
        {
            var fake = UseFakeFalcon();
            var link = RemoteFalconLink.Load(NativeHost.Shared, null, out string reason);
            Assert.NotNull(link);
            Assert.Equal(string.Empty, reason);
            Assert.Equal(@"C:\Program Files\Force Dimension\sdk-3.17.1\bin\dhd64.dll", link.Path);
            Assert.Equal("3.17.1", link.Version);
            Assert.Equal(2, link.DeviceCount());
            int id = link.OpenIndex(1);
            Assert.Equal(1, id);
            Assert.Equal(60, link.SystemType(id));
            Assert.Equal(0, link.SerialNumber(id, out ushort serial));
            Assert.Equal(4242, serial);
            Assert.Equal(0, link.GetPosition(id, out double x, out double y, out double z));
            Assert.Equal((0.01, -0.02, 0.03), (x, y, z));
            Assert.Equal(0b1011u, link.ButtonMask(id));
            Assert.Equal(2, link.SetForce(id, 1.5, -2.5, 3.5));
            Assert.Contains((1.5, -2.5, 3.5), fake.Forces);
            var status = new int[ForceDimensionLibrary.StatusCount];
            Assert.Equal(0, link.GetStatus(id, status));
            Assert.Equal(16, status[16]);
            Assert.Equal(0.998, link.ComFrequency(id));
            // The library's last error is the calling thread's, as in process.
            Assert.Equal(-1, link.OpenIndex(5));
            Assert.Equal(7, link.LastError());
            Assert.Equal("nosuchdevice", link.LastErrorText());
            Assert.Null(RemoteFalconLink.Load(NativeHost.Shared, @"C:\missing\dhd64.dll", out reason));
            Assert.Equal("missing", reason);
        }

        /// <summary>Each PadForge thread has a channel, and so a host thread,
        /// of its own, as each Falcon's haptic thread called the library on
        /// its own thread in process. A thread's last Close closes its
        /// channel.</summary>
        [Fact]
        public void EachThread_GetsAChannelOfItsOwn_UntilItsLastClose()
        {
            var threads = new List<int>();
            var servers = new List<HostConnection>();
            var fake = UseFakeFalcon(threads, servers);
            var link = RemoteFalconLink.Load(NativeHost.Shared, null, out _);
            var haptic = new Thread(() =>
            {
                link.GetPosition(0, out _, out _, out _);
                link.Close(0);
            });
            haptic.Start();
            haptic.Join();
            link.GetPosition(0, out _, out _, out _);
            Assert.Equal(2, servers.Count);
            Assert.Equal(2, fake.Threads.Count);
            // The haptic thread's channel ended with its Close.
            SpinWait.SpinUntil(() => servers[1].Broken, 2000);
            Assert.True(servers[1].Broken);
            Assert.False(servers[0].Broken);
        }

        /// <summary>A position past the workspace's bound, or a host that
        /// ends, reads as a failed call: the haptic thread counts it and closes
        /// the Falcon after three, as for the library's own failures.</summary>
        [Fact]
        public void AnAnswerOutOfBounds_OrAnEndedHost_IsAFailedCall()
        {
            var servers = new List<HostConnection>();
            var fake = UseFakeFalcon(servers: servers);
            var link = RemoteFalconLink.Load(NativeHost.Shared, null, out _);
            fake.X = 2.0;
            Assert.Equal(-1, link.GetPosition(0, out double x, out _, out _));
            Assert.Equal(0, x);
            fake.X = double.NaN;
            Assert.Equal(-1, link.GetPosition(0, out x, out _, out _));
            fake.X = 0.01;
            // The next call opens a new channel and loads the library again.
            Assert.Equal(0, link.GetPosition(0, out x, out _, out _));
            Assert.Equal(0.01, x);
            servers[^1].Dispose();
            Assert.Equal(-1, link.SetForce(0, 0, 0, 0));
            Assert.Equal("the library host ended", link.LastErrorText());
            // Buttons past the grip's mean nothing.
            fake.Buttons = 0xFFFFFFFF;
            Assert.Equal(0xFFFFu, link.ButtonMask(0));
        }

        /// <summary>The Falcon's library is loaded through the host, and no
        /// file of the app loads Force Dimension's library in its own
        /// process.</summary>
        [Fact]
        public void TheApp_LoadsVendorLibrariesOnlyInTheHost()
        {
            string runtime = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Falcon", "FalconRuntime.cs");
            Assert.Contains("RemoteFalconLink.Load(PadForge.Services.NativeHost.NativeHost.Shared, picked, out reason)", runtime, StringComparison.Ordinal);
            Assert.DoesNotContain("ForceDimensionLibrary.TryLoad", runtime, StringComparison.Ordinal);
            var where = new Dictionary<string, List<string>>
            {
                ["ForceDimensionLibrary.TryLoad("] = new(),
                ["OpenVR.Init("] = new(),
                ["OpenXrSession.TryCreate"] = new(),
                ["EnsureResolver()"] = new(),
            };
            foreach (string path in OffLimitsSource.Sources("PadForge.App"))
            {
                string text = File.ReadAllText(path);
                foreach (var pair in where)
                    if (text.Contains(pair.Key, StringComparison.Ordinal)) pair.Value.Add(Path.GetFileName(path));
            }
            Assert.Equal(new[] { "FalconHost.cs" }, where["ForceDimensionLibrary.TryLoad("]);
            Assert.Equal(new[] { "OpenVrHost.cs" }, where["OpenVR.Init("]);
            Assert.Empty(where["OpenXrSession.TryCreate"]);
            Assert.Equal(new[] { "OpenVrConsumerService.cs", "OpenVrHost.cs" }, where["EnsureResolver()"].OrderBy(n => n, StringComparer.Ordinal));
            string consumer = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "OpenVrConsumerService.cs");
            Assert.Contains("RemoteVrClient.Opener(PadForge.Services.NativeHost.NativeHost.Shared)", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("EnsureResolver();", FenceSource.Body(consumer, "public void Start()"), StringComparison.Ordinal);
            string tracker = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "HeadTrackerDevice.cs");
            Assert.True(tracker.IndexOf("_openXr.UseLibraryHost(PadForge.Services.NativeHost.NativeHost.Shared.Open);", StringComparison.Ordinal)
                        < tracker.IndexOf("_openXr.Start();", StringComparison.Ordinal));
            Assert.True(tracker.IndexOf("_openXr.UseLibraryHost(", StringComparison.Ordinal) > 0);
        }

        // ── The Strike 7 ──

        private sealed class FakeStrike7 : PadForge.Engine.Common.MadCatz.IStrike7Library
        {
            public Action<PadForge.Engine.Common.MadCatz.Strike7Event> Subscriber;
            public readonly List<(ushort Applet, int Bytes)> Draws = new();
            public ushort Next = 7;
            private readonly object _gate = new();
            private ushort _open;
            public string Path => @"C:\Users\sonic\Downloads\Strike7API.dll";
            public ushort Open(string name, int w, int h, byte[] icon, Action<PadForge.Engine.Common.MadCatz.Strike7Event> subscriber)
            {
                lock (_gate)
                {
                    Subscriber = subscriber;
                    return _open = Next++;
                }
            }
            // The host that ended closes its applet on its own thread, which
            // can come after the next host opened the new one. That close
            // leaves the new applet's events alone, as the library's Close
            // does for an applet other than the one it closes.
            public void Close(ushort applet)
            {
                lock (_gate)
                {
                    if (applet != _open) return;
                    Subscriber = null;
                    _open = 0;
                }
            }
            public void DrawScreen(ushort applet, ushort endpoint, byte[] bgra) { lock (Draws) Draws.Add((applet, bgra.Length)); }
        }

        private const int ScreenBytes = 480 * 320 * 4;

        /// <summary>How long a Strike 7 wait allows for the library's poll, here and in
        /// NativeHostChannelTests.</summary>
        internal const int PollWaitMs = 10_000;

        /// <summary>The applet's events cross on the screen's own pace, held
        /// to the screen's bounds, and a host that ended opens the applet
        /// again in the next one under the ID the screen holds.
        ///
        /// <para>The events come on the library's poll, every 20 ms on the
        /// thread pool. While every core is busy, as at the start of a full
        /// run, a wait of 2 s ran out before the poll came (audit 16), so each
        /// wait allows <see cref="PollWaitMs"/> and ends once its event is in.
        /// The library is closed in the finally: one left open polls on and
        /// opens its applet again through the next test's host.</para></summary>
        [Fact]
        public void TheStrike7_CrossesTheHost_AndOpensAgainAfterTheHostEnds()
        {
            var fake = new FakeStrike7();
            Strike7HostService.LoadSeam = path => (fake, null);
            var servers = new List<HostConnection>();
            NativeHost.OpenSeam = service =>
            {
                if (service != Strike7HostService.Name) return null;
                lock (servers) return Serve(new Strike7HostService(), servers);
            };
            int Servers() { lock (servers) return servers.Count; }
            HostConnection Server(int i) { lock (servers) return servers[i]; }
            PadForge.Engine.Common.MadCatz.IStrike7Library library = null;
            ushort applet = 0;
            try
            {
                string reason;
                (library, reason) = RemoteStrike7Library.Load(NativeHost.Shared, fake.Path);
                Assert.NotNull(library);
                Assert.Equal(string.Empty, reason);
                var seen = new System.Collections.Concurrent.ConcurrentQueue<PadForge.Engine.Common.MadCatz.Strike7Event>();
                applet = library.Open("PadForge", 2, 2, new byte[16], seen.Enqueue);
                Assert.Equal(7, applet);
                fake.Subscriber(new PadForge.Engine.Common.MadCatz.Strike7Event(PadForge.Engine.Common.MadCatz.Strike7EventKind.Touch, 7, 1, 100, 200, true));
                Assert.True(SpinWait.SpinUntil(() => !seen.IsEmpty, PollWaitMs));
                Assert.True(seen.TryDequeue(out var touch));
                Assert.Equal((7, 100, 200, true), (touch.AppletId, touch.X, touch.Y, touch.Pressed));
                library.DrawScreen(applet, 1, new byte[ScreenBytes]);
                Assert.Equal((7, ScreenBytes), fake.Draws.Single());

                // The host ends: a reset reaches the screen, the applet opens
                // again in the new host, and draws reach it under the old ID.
                Server(0).Dispose();
                Assert.True(SpinWait.SpinUntil(() => seen.Any(e => e.Kind == PadForge.Engine.Common.MadCatz.Strike7EventKind.Reset), PollWaitMs));
                Assert.True(SpinWait.SpinUntil(() => Servers() == 2, PollWaitMs));
                library.DrawScreen(applet, 1, new byte[ScreenBytes]);
                Assert.True(SpinWait.SpinUntil(() => fake.Draws.Count == 2, PollWaitMs));
                Assert.Equal(8, fake.Draws[1].Applet);
                fake.Subscriber(new PadForge.Engine.Common.MadCatz.Strike7Event(PadForge.Engine.Common.MadCatz.Strike7EventKind.Active, 8, 1, 0, 0, false));
                Assert.True(SpinWait.SpinUntil(() => seen.Any(e => e.Kind == PadForge.Engine.Common.MadCatz.Strike7EventKind.Active), PollWaitMs));
                Assert.Equal(7, seen.First(e => e.Kind == PadForge.Engine.Common.MadCatz.Strike7EventKind.Active).AppletId);
                // A touch the library places off the screen is held to its
                // edge in the host.
                fake.Subscriber(new PadForge.Engine.Common.MadCatz.Strike7Event(PadForge.Engine.Common.MadCatz.Strike7EventKind.Touch, 8, 1, 4000, 9000, true));
                Assert.True(SpinWait.SpinUntil(() => seen.Any(e => e.Kind == PadForge.Engine.Common.MadCatz.Strike7EventKind.Touch && e.X == 479), PollWaitMs));
                Assert.Equal(319, seen.Last(e => e.Kind == PadForge.Engine.Common.MadCatz.Strike7EventKind.Touch).Y);
                Assert.False(Server(1).Broken);
            }
            finally
            {
                if (library != null && applet != 0) library.Close(applet);
                Strike7HostService.LoadSeam = null;
            }
        }

        // ── Aura ──

        private sealed class FakeAura : PadForge.Common.Input.Peripherals.IAuraSdkNative
        {
            public readonly List<string> Calls = new();
            public IReadOnlyList<PadForge.Common.Input.Peripherals.AuraDevice> Devices { get; private set; } =
                Array.Empty<PadForge.Common.Input.Peripherals.AuraDevice>();
            public bool Open(out string detail)
            {
                Devices = new[] { new PadForge.Common.Input.Peripherals.AuraDevice(0, 0x80000, "ROG‮ Strix", 3, new ushort[] { 0x01, 0x1E }) };
                detail = "1 devices";
                return true;
            }
            public void SetLight(int device, int index, int rgb) { lock (Calls) Calls.Add($"light {device} {index} {rgb:X6}"); }
            public void SetKey(int device, ushort code, int rgb) { lock (Calls) Calls.Add($"key {device} {code:X} {rgb:X6}"); }
            public void Apply(int device) { lock (Calls) Calls.Add($"apply {device}"); }
            public void Close() { lock (Calls) Calls.Add("close"); }
        }

        /// <summary>A paint's colors wait in PadForge and cross with the
        /// apply in one call, in their order. Names are cleaned.</summary>
        [Fact]
        public void AuraColors_CrossWithTheApply_InOneCall()
        {
            var fake = new FakeAura();
            AuraHostService.NativeSeam = () => fake;
            NativeHost.OpenSeam = service => service == AuraHostService.Name ? Serve(new AuraHostService()) : null;
            try
            {
                var sdk = new RemoteAuraSdk(NativeHost.Shared);
                Assert.True(sdk.Open(out string detail));
                Assert.Equal("1 devices", detail);
                var device = Assert.Single(sdk.Devices);
                Assert.Equal("ROG Strix", device.Name);
                Assert.Equal(3, device.LightCount);
                Assert.Equal(new ushort[] { 0x01, 0x1E }, device.KeyCodes);
                sdk.SetLight(0, 2, 0xFF8000);
                sdk.SetKey(0, 0x1E, 0x00FF00);
                Assert.Empty(fake.Calls);
                sdk.Apply(0);
                Assert.Equal(new[] { "light 0 2 FF8000", "key 0 1E 00FF00", "apply 0" }, fake.Calls);
                Assert.Throws<ArgumentOutOfRangeException>(() => sdk.SetLight(0, 3, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => sdk.SetLight(1, 0, 0));
                sdk.Close();
                Assert.Equal("close", fake.Calls[^1]);
            }
            finally
            {
                AuraHostService.NativeSeam = null;
            }
        }

        // ── OpenVR ──

        /// <summary>A scripted OpenVR host: one controller and the
        /// headset, then a quit.</summary>
        private sealed class ScriptedVr : IHostService
        {
            public float Matrix0 = 1f;
            public void Serve(HostConnection channel)
            {
                while (channel.Receive(out var r))
                {
                    int op = r.Int();
                    if (op == OpenVrOps.Init) { r.End(); channel.Send(w => w.Int(0).Text(null)); }
                    else if (op == OpenVrOps.Frame)
                    {
                        r.End();
                        channel.Send(w =>
                        {
                            w.Bool(false);
                            for (int i = 0; i < OpenVrOps.Devices; i++)
                            {
                                bool connected = i == 0 || i == 3;
                                w.Bool(connected);
                                if (!connected) continue;
                                w.Float(Matrix0);
                                for (int m = 1; m < 12; m++) w.Float(m == 3 ? 0.5f : 0f);
                                for (int v = 0; v < 6; v++) w.Float(0.1f);
                                w.Int(200).Bool(true);
                                w.Int(i == 0 ? (int)ETrackedDeviceClass.HMD : (int)ETrackedDeviceClass.Controller);
                                w.Int(i == 3 ? (int)ETrackedControllerRole.LeftHand : 0);
                                if (i != 3) continue;
                                w.Bool(true).UInt(9).ULong(1UL << 33).ULong(0);
                                for (int a = 0; a < 10; a++) w.Float(a == 0 ? 0.75f : 0f);
                            }
                        });
                    }
                    else if (op == OpenVrOps.DeviceInfo)
                    {
                        int index = r.Int();
                        r.End();
                        channel.Send(w => w.Int(0).Text(index == 3 ? "Valve\u200B" : "HIDMaestro").Int(2).Int(3).Int(0).Int(0).Int(0));
                    }
                    else { r.End(); channel.Send(w => { }); }
                }
            }
        }

        [Fact]
        public void AnOpenVrFrame_IsReadInOneCall_AndHeldToItsBounds()
        {
            var vr = new ScriptedVr();
            NativeHost.OpenSeam = service => service == OpenVrHostService.Name ? Serve(vr) : null;
            var client = RemoteVrClient.Opener(NativeHost.Shared)(out var error, out string failure);
            Assert.NotNull(client);
            Assert.Equal(EVRInitError.None, error);
            Assert.Null(failure);
            Assert.False(client.PollQuit());
            var poses = new TrackedDevicePose_t[OpenVrOps.Devices];
            client.ReadPoses(poses);
            Assert.True(poses[0].bDeviceIsConnected && poses[3].bDeviceIsConnected && !poses[1].bDeviceIsConnected);
            Assert.Equal(0.5f, poses[3].mDeviceToAbsoluteTracking.m3);
            Assert.Equal(ETrackedDeviceClass.HMD, client.DeviceClass(0));
            Assert.Equal(ETrackedDeviceClass.Controller, client.DeviceClass(3));
            Assert.Equal(ETrackedControllerRole.LeftHand, client.Role(3));
            var state = new VRControllerState_t();
            Assert.True(client.ControllerState(3, ref state));
            Assert.Equal(1UL << 33, state.ulButtonPressed);
            Assert.Equal(0.75f, state.rAxis0.x);
            Assert.False(client.ControllerState(0, ref state));
            var types = new int[5];
            Assert.Equal("Valve", client.Manufacturer(3, out var propertyError, types));
            Assert.Equal(ETrackedPropertyError.TrackedProp_Success, propertyError);
            Assert.Equal(new[] { 2, 3, 0, 0, 0 }, types);
            // A pose past its bound ends the client: the consumer takes it as a
            // lost session.
            vr.Matrix0 = float.NaN;
            Assert.Throws<InvalidOperationException>(() => client.PollQuit());
            client.Dispose();
        }

        // ── OpenXR ──

        private sealed class FakeXr : IOpenXrLink
        {
            public Action<string> Log;
            public string RuntimeName => "SteamVR/OpenXR";
            public string ManifestPath => @"C:\SteamVR\steamxr_win64.json";
            public bool HasSession { get; private set; }
            public bool HasControllers => HasSession;
            public bool SharesOpenVrClient => true;
            public long InstanceLossWaitMs => 1500;
            public float Trigger = 0.5f;
            public OpenXrSessionCreate TryCreateSession(out OpenXrSourceState failure, out string read)
            {
                failure = OpenXrSourceState.Running;
                read = null;
                HasSession = true;
                Log("session made");
                return OpenXrSessionCreate.Created;
            }
            public OpenXrSessionEnd PumpEvents(out bool referenceSpaceChanged, out string read)
            {
                referenceSpaceChanged = true;
                read = "x";
                return OpenXrSessionEnd.None;
            }
            public void DestroySession() => HasSession = false;
            public void ReadHands(ref OpenXrHeadPose.Baseline left, ref OpenXrHeadPose.Baseline right,
                                  out OpenXrHandState leftState, out OpenXrHandState rightState)
            {
                left.Captured = true;
                left.X = 0.25;
                leftState = new OpenXrHandState { PoseValid = true, TX = 0.1, Trigger = Trigger, ThumbstickX = -1.2f, ControlsActive = true };
                rightState = OpenXrHandState.Empty;
            }
            public bool TryLocateHead(out XrVector3f position, out XrQuaternionf orientation)
            {
                position = new XrVector3f { x = 1, y = 2, z = 3 };
                orientation = new XrQuaternionf { w = 1 };
                return true;
            }
            public void Dispose() { }
        }

        [Fact]
        public void AnOpenXrLink_CrossesTheHost_WithItsLogAndItsBounds()
        {
            var fake = new FakeXr();
            OpenXrHostService.OpenSeam = (string manifest, bool again, Action<string> log, out OpenXrSourceState failure, out bool unavailable, out string read) =>
            {
                fake.Log = log;
                failure = OpenXrSourceState.Running;
                unavailable = false;
                read = null;
                log("instance made for " + manifest);
                return fake;
            };
            var servers = new List<HostConnection>();
            NativeHost.OpenSeam = service => service == OpenXrHeadPoseSource.LibraryHostService ? Serve(OpenXrHeadPoseSource.NewLibraryHostService(), servers) : null;
            var log = new List<string>();
            var opener = RemoteOpenXrLink.Opener(NativeHost.Shared.Open);
            var link = opener(@"C:\SteamVR\steamxr_win64.json", false, log.Add, out var state, out bool unavailableOut, out string readOut);
            Assert.NotNull(link);
            Assert.Equal(@"instance made for C:\SteamVR\steamxr_win64.json", Assert.Single(log));
            Assert.Equal("SteamVR/OpenXR", link.RuntimeName);
            Assert.True(link.SharesOpenVrClient);
            Assert.Equal(1500, link.InstanceLossWaitMs);
            Assert.Equal(OpenXrSessionCreate.Created, link.TryCreateSession(out _, out _));
            Assert.Equal("session made", log[1]);
            Assert.True(link.HasSession && link.HasControllers);
            Assert.Equal(OpenXrSessionEnd.None, link.PumpEvents(out bool changed, out string read));
            Assert.True(changed);
            Assert.Equal("x", read);
            var left = new OpenXrHeadPose.Baseline();
            var right = new OpenXrHeadPose.Baseline();
            link.ReadHands(ref left, ref right, out var leftState, out var rightState);
            Assert.True(left.Captured);
            Assert.Equal(0.25, left.X);
            Assert.True(leftState.PoseValid);
            Assert.Equal(0.5f, leftState.Trigger);
            // A stick past its range is clamped.
            Assert.Equal(-1f, leftState.ThumbstickX);
            Assert.True(rightState.IsIdle);
            Assert.True(link.TryLocateHead(out var p, out var q));
            Assert.Equal((1f, 2f, 3f, 1f), (p.x, p.y, p.z, q.w));
            // An input past its bound ends the link, and the source then reads a
            // lost instance, which it answers by asking for a new one.
            fake.Trigger = 9f;
            link.ReadHands(ref left, ref right, out leftState, out _);
            Assert.True(leftState.IsIdle);
            Assert.Equal(OpenXrSessionEnd.InstanceLost, link.PumpEvents(out _, out read));
            Assert.Equal("the library host ended", read);
            Assert.False(link.HasSession);
            link.Dispose();
        }
    }
}
