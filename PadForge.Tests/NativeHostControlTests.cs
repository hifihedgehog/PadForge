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
    /// The library host's control channel: a quit waits for the services to
    /// close what they opened, why a service ended reaches PadForge's
    /// diagnostics, and a control call that hangs is ended by the watchdog.
    /// The host's own loop runs here on in-process anonymous pipes
    /// (<see cref="NativeHostTests.Pair"/>), with no process started.
    /// </summary>
    [Collection("NativeHostStatics")]
    public sealed class NativeHostControlTests : IDisposable
    {
        private readonly List<IDisposable> _held = new();

        public void Dispose()
        {
            AuraHostService.NativeSeam = null;
            foreach (var d in _held) d.Dispose();
        }

        /// <summary>The host's loop on a thread, as Run starts it: the
        /// control channel's PadForge end, and the host's thread.</summary>
        private (HostConnection Control, Thread Host) RunHost(Dictionary<string, HostConnection> pipes,
            IReadOnlyDictionary<string, Func<IHostService>> services)
        {
            var (control, server) = NativeHostTests.Pair();
            var host = new Thread(() => NativeHostRun.Serve(server, name =>
            {
                lock (pipes) return pipes.TryGetValue(name, out var c) ? c : null;
            }, services)) { IsBackground = true };
            host.Start();
            _held.Add(control);
            return (control, host);
        }

        /// <summary>A channel for the host to connect: the host's end goes
        /// under a new pipe name, PadForge's end comes back.</summary>
        private (string Pipe, HostConnection Client) NewChannel(Dictionary<string, HostConnection> pipes)
        {
            var (client, server) = NativeHostTests.Pair();
            string name = NativeHost.NewPipeName();
            lock (pipes) pipes[name] = server;
            _held.Add(client);
            return (name, client);
        }

        private sealed class SlowCloseAura : IAuraSdkNative
        {
            private int _closed;
            public bool Closed => Volatile.Read(ref _closed) == 1;
            public IReadOnlyList<AuraDevice> Devices { get; private set; } = Array.Empty<AuraDevice>();
            public bool Open(out string detail)
            {
                Devices = new[] { new AuraDevice(0, 0x80000, "Board", 4, Array.Empty<ushort>()) };
                detail = "1 devices";
                return true;
            }
            public void SetLight(int device, int index, int rgb) { }
            public void SetKey(int device, ushort code, int rgb) { }
            public void Apply(int device) { }
            public void Close()
            {
                // ReleaseControl takes a moment.
                Thread.Sleep(300);
                Volatile.Write(ref _closed, 1);
            }
        }

        /// <summary>A quit is answered once the services have ended and closed
        /// what they opened: Aura's lights go back to Armoury Crate before
        /// PadForge ends the host.</summary>
        [Fact]
        public void AQuit_IsAnsweredOnceTheServicesClosedWhatTheyOpened()
        {
            var fake = new SlowCloseAura();
            AuraHostService.NativeSeam = () => fake;
            var pipes = new Dictionary<string, HostConnection>();
            var (control, host) = RunHost(pipes, new Dictionary<string, Func<IHostService>> { [AuraHostService.Name] = () => new AuraHostService() });
            var (pipe, aura) = NewChannel(pipes);
            Assert.True(NativeHost.AskOpen(control, AuraHostService.Name, pipe, out bool opened, out string refused));
            Assert.True(opened, refused);
            Assert.True(aura.Call(w => w.Int(AuraOps.Open), r =>
            {
                Assert.True(r.Bool());
                r.Text(AuraOps.MaxText);
                int count = r.Int(0, AuraOps.MaxDevices);
                for (int i = 0; i < count; i++)
                {
                    r.Int(); r.UInt(); r.Text(AuraOps.MaxName); r.Int(); int keys = r.Int();
                    for (int k = 0; k < keys; k++) r.Int();
                }
            }));
            // PadForge's Stop: its ends of the channels close, then the quit.
            aura.Dispose();
            Assert.True(control.Call(w => w.Int(NativeHost.OpQuit), null));
            Assert.True(fake.Closed);
            Assert.True(host.Join(2000));
        }

        private sealed class ThrowingService : IHostService
        {
            public void Serve(HostConnection channel) => throw new InvalidTimeZoneException();
        }

        /// <summary>Why a service of the host ended goes to PadForge with the
        /// next open's answer, into PadForge's own diagnostics.</summary>
        [Fact]
        public void WhyAServiceEnded_ReachesPadForgesDiagnostics()
        {
            const string service = "throws-xu9";
            var pipes = new Dictionary<string, HostConnection>();
            var (control, _) = RunHost(pipes, new Dictionary<string, Func<IHostService>> { [service] = () => new ThrowingService() });
            string expected = "NATIVEHOST in the library host, " + service + " ended: InvalidTimeZoneException";
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var (pipe, _) = NewChannel(pipes);
                Assert.True(NativeHost.AskOpen(control, service, pipe, out bool opened, out _));
                Assert.True(opened);
                return PadForge.Engine.SdlDiagLog.Snapshot().Contains(expected, StringComparison.Ordinal);
            }, 3000));
        }

        /// <summary>A control call the host never answers is ended by the
        /// watchdog once it waited past the call limit, so the open that made
        /// it fails and the next one starts a new host.</summary>
        [Fact]
        public void AControlCallThatHangs_IsEndedByTheWatchdog()
        {
            var host = new NativeHost(null);
            var (control, server) = NativeHostTests.Pair();
            _held.Add(server);
            host.TakeControlForTest(control);
            bool answered = true;
            var caller = new Thread(() => answered = control.Call(w => w.Int(NativeHost.OpOpen), null)) { IsBackground = true };
            caller.Start();
            Assert.True(SpinWait.SpinUntil(() => control.CallStarted != 0, 2000));
            host.Watch(Stopwatch.GetTimestamp());
            Assert.False(control.Broken);
            host.Watch(Stopwatch.GetTimestamp() + Stopwatch.Frequency * NativeHost.CallLimitMs / 1000 + Stopwatch.Frequency);
            Assert.True(control.Broken);
            // The host's process ends with it, which closes its end.
            server.Dispose();
            Assert.True(caller.Join(2000));
            Assert.False(answered);
        }

        /// <summary>A Generation read takes no lock: a Falcon's haptic thread
        /// reads it on every call, and never waits on an open.</summary>
        [Fact]
        public void AGenerationRead_NeverWaitsOnTheGate()
        {
            var host = new NativeHost(null);
            object gate = typeof(NativeHost).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(host);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (gate)
                {
                    held.Set();
                    release.Wait(5000);
                }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000));
            try
            {
                int read = -1;
                var reader = new Thread(() => read = host.Generation) { IsBackground = true };
                reader.Start();
                Assert.True(reader.Join(1000));
                Assert.Equal(0, read);
            }
            finally
            {
                release.Set();
                holder.Join(2000);
            }
        }
    }
}
