using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine.Common.NativeHost;
using PadForge.Services.NativeHost;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The library host's real open, stop and start, on the named pipes
    /// PadForge makes (NativeHost.MakePipe) and the host's own end of them
    /// (NativeHostRun.ClientPipe). A test's start (NativeHost.IHostStart)
    /// runs the host's loop on a thread of this process in place of a
    /// process, so nothing is started and no vendor library loads.
    /// </summary>
    [Collection("NativeHostStatics")]
    public sealed class NativeHostRealPipeTests : IDisposable
    {
        public void Dispose() => AuraHostService.NativeSeam = null;

        private static HostConnection HostEnd(string name)
        {
            var pipe = NativeHostRun.ClientPipe(name);
            pipe.Connect(NativeHost.ConnectLimitMs);
            return new HostConnection(pipe, pipe, pipe);
        }

        /// <summary>A host on a thread. It runs the host's own loop, or one
        /// that never answers an open, or one that answers and connects the
        /// channel late, each after its control connect waits
        /// <see cref="ConnectDelayMs"/>.</summary>
        private sealed class ThreadHost : NativeHost.IHostStart
        {
            public enum Kind { Serve, NeverAnswers, ConnectsLate }
            public Kind As = Kind.Serve;
            public int ConnectDelayMs;
            public IReadOnlyDictionary<string, Func<IHostService>> Services = new Dictionary<string, Func<IHostService>>();
            public int Starts, Stops;
            public readonly ManualResetEventSlim Asked = new();
            private readonly ManualResetEventSlim _killed = new();

            public bool Start(string pipeName, out string reason)
            {
                reason = null;
                Interlocked.Increment(ref Starts);
                var thread = new Thread(() =>
                {
                    Thread.Sleep(ConnectDelayMs);
                    var control = HostEnd(pipeName);
                    if (As == Kind.Serve)
                    {
                        NativeHostRun.Serve(control, HostEnd, Services);
                        return;
                    }
                    if (!control.Receive(out var request)) return;
                    request.Int();
                    request.Text(32);
                    string channel = request.Text(64);
                    Asked.Set();
                    if (As == Kind.NeverAnswers)
                    {
                        // No read waits on the pipe now, so its end closes for
                        // real when the process ends.
                        _killed.Wait();
                        control.Dispose();
                        return;
                    }
                    control.Send(w => w.Bool(true).Text(null).Int(0));
                    Thread.Sleep(1500);
                    HostEnd(channel);
                    _killed.Wait();
                    control.Dispose();
                }) { IsBackground = true };
                thread.Start();
                return true;
            }

            public void Stop()
            {
                Interlocked.Increment(ref Stops);
                _killed.Set();
            }

            public void Release() => _killed.Set();
        }

        private static object GateOf(NativeHost host)
            => typeof(NativeHost).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);

        /// <summary>Whether another thread can take the host's gate within
        /// half a second, as the watchdog and the host's Exited handler
        /// do.</summary>
        private static bool GateIsFree(NativeHost host)
        {
            object gate = GateOf(host);
            bool taken = false;
            var thread = new Thread(() =>
            {
                if (!Monitor.TryEnter(gate, 500)) return;
                taken = true;
                Monitor.Exit(gate);
            }) { IsBackground = true };
            thread.Start();
            thread.Join(2000);
            return taken;
        }

        private static Thread OpenOnAThread(NativeHost host, string service, Action<HostConnection, string> done)
        {
            var thread = new Thread(() =>
            {
                var channel = host.Open(service, out string reason);
                done(channel, reason);
            }) { IsBackground = true };
            thread.Start();
            return thread;
        }

        private sealed class Idle : IHostService
        {
            public void Serve(HostConnection channel)
            {
                while (channel.Receive(out var r)) { r.Int(); r.End(); channel.Send(w => { }); }
            }
        }

        /// <summary>While a new host's control pipe waits for its connect,
        /// nothing that takes the gate waits on it, and the open then goes
        /// through.</summary>
        [Fact]
        public void ANewHostsConnectWait_RunsOutsideTheGate()
        {
            var start = new ThreadHost { ConnectDelayMs = 1500, Services = new Dictionary<string, Func<IHostService>> { ["idle"] = () => new Idle() } };
            var host = new NativeHost(null, testStart: start);
            HostConnection opened = null;
            var open = OpenOnAThread(host, "idle", (c, _) => opened = c);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref start.Starts) == 1, 2000));
                Assert.True(GateIsFree(host));
                Assert.True(open.Join(5000));
                Assert.NotNull(opened);
                Assert.True(opened.Call(w => w.Int(0), null));
            }
            finally
            {
                opened?.Dispose();
                host.Stop();
            }
        }

        /// <summary>A control call the host never answers runs outside the
        /// gate, so the watchdog takes it and ends the host, and the open
        /// fails.</summary>
        [Fact]
        public void AnOpensControlCall_RunsOutsideTheGate_AndTheWatchdogEndsIt()
        {
            var start = new ThreadHost { As = ThreadHost.Kind.NeverAnswers };
            var host = new NativeHost(null, testStart: start);
            HostConnection opened = null;
            string why = null;
            var open = OpenOnAThread(host, "idle", (c, r) => { opened = c; why = r; });
            try
            {
                Assert.True(start.Asked.Wait(5000));
                Assert.True(GateIsFree(host));
                var watch = new Thread(() => host.Watch(Stopwatch.GetTimestamp() + Stopwatch.Frequency * (NativeHost.CallLimitMs / 1000 + 1))) { IsBackground = true };
                watch.Start();
                Assert.True(watch.Join(2000));
                Assert.True(open.Join(5000));
                Assert.Null(opened);
                Assert.Equal("the library host ended", why);
                Assert.Equal(1, start.Stops);
            }
            finally
            {
                start.Release();
            }
        }

        /// <summary>While an open waits for its channel to connect, the gate
        /// stays free.</summary>
        [Fact]
        public void AnOpensChannelConnectWait_RunsOutsideTheGate()
        {
            var start = new ThreadHost { As = ThreadHost.Kind.ConnectsLate };
            var host = new NativeHost(null, testStart: start);
            HostConnection opened = null;
            var open = OpenOnAThread(host, "idle", (c, _) => opened = c);
            try
            {
                Assert.True(start.Asked.Wait(5000));
                Thread.Sleep(100);
                Assert.True(GateIsFree(host));
                Assert.True(open.Join(5000));
                Assert.NotNull(opened);
            }
            finally
            {
                opened?.Dispose();
                start.Release();
            }
        }

        private sealed class SlowCloseAura : IAuraSdkNative
        {
            private int _closing, _closed;
            public bool Closing => Volatile.Read(ref _closing) == 1;
            public bool Closed => Volatile.Read(ref _closed) == 1;
            public IReadOnlyList<AuraDevice> Devices { get; } = Array.Empty<AuraDevice>();
            public bool Open(out string detail) { detail = string.Empty; return true; }
            public void SetLight(int device, int index, int rgb) { }
            public void SetKey(int device, ushort code, int rgb) { }
            public void Apply(int device) { }
            public void Close()
            {
                Volatile.Write(ref _closing, 1);
                Thread.Sleep(500);
                Volatile.Write(ref _closed, 1);
            }
        }

        /// <summary>Stop on the real pipes: PadForge closes its end of the
        /// Aura channel, the host's read on its end (synchronous, as the host
        /// opens it) ends, the service closes what it opened, and only then
        /// is the quit answered.</summary>
        [Fact]
        public void Stop_EndsTheHostsReadOnTheNamedPipe_AndTheServiceClosesBeforeTheQuitsAnswer()
        {
            var fake = new SlowCloseAura();
            AuraHostService.NativeSeam = () => fake;
            var start = new ThreadHost { Services = new Dictionary<string, Func<IHostService>> { [AuraHostService.Name] = () => new AuraHostService() } };
            var host = new NativeHost(null, testStart: start);
            var aura = host.Open(AuraHostService.Name, out string reason);
            Assert.True(aura != null, reason);
            host.Stop();
            Assert.True(fake.Closed);
            Assert.Equal(1, start.Stops);
        }

        /// <summary>An open that comes while a stop waits for the quit's
        /// answer waits for the stop, then starts a new host, and never asks
        /// the host that is leaving.</summary>
        [Fact]
        public void AnOpenDuringAStop_WaitsForIt_AndStartsANewHost()
        {
            var fake = new SlowCloseAura();
            AuraHostService.NativeSeam = () => fake;
            var start = new ThreadHost { Services = new Dictionary<string, Func<IHostService>> { [AuraHostService.Name] = () => new AuraHostService(), ["idle"] = () => new Idle() } };
            var host = new NativeHost(null, testStart: start);
            var aura = host.Open(AuraHostService.Name, out string reason);
            Assert.True(aura != null, reason);
            var stop = new Thread(host.Stop) { IsBackground = true };
            stop.Start();
            Assert.True(SpinWait.SpinUntil(() => fake.Closing, 3000));
            HostConnection opened = null;
            string why = null;
            var open = OpenOnAThread(host, "idle", (c, r) => { opened = c; why = r; });
            try
            {
                Assert.True(stop.Join(5000));
                Assert.True(open.Join(5000));
                Assert.True(opened != null, why);
                Assert.Equal(2, start.Starts);
            }
            finally
            {
                opened?.Dispose();
                host.Stop();
            }
        }
    }
}
