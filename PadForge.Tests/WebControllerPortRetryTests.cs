using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>
    /// The web controller's port another program held: the main address is
    /// probed every few seconds and gets a full start only when the port is
    /// free, since that start runs netsh first. The plain address of a
    /// running server is added again on its listener.
    /// </summary>
    public sealed class WebControllerPortRetryTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        [Fact]
        public void AHeldPortIsProbed_AFreedPortStarts_AndTheSwitchOffEndsTheRetries()
        {
            RunSta(dispatcher =>
            {
                var vm = new MainViewModel();
                using var service = new InputService(vm);
                bool held = true;
                int starts = 0, probes = 0;
                var listeners = new List<HeldListener>();
                service.WebServerFactory = () =>
                {
                    starts++;
                    return new WebControllerServer(_ => null, () =>
                    {
                        var listener = new HeldListener(() => held, plainHeld: () => false);
                        lock (listeners) listeners.Add(listener);
                        return listener;
                    });
                };
                service.WebPortProbe = _ => { probes++; return !held; };
                Set(service, "_inputManager", RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
                try
                {
                    vm.Dashboard.WebControllerPort = 18090;
                    vm.Dashboard.EnableWebController = true;
                    Call(service, "StartWebServerIfEnabled");
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer") == null);
                    Assert.True(Get<bool>(service, "_webPortInUse"));
                    Assert.Equal(1, starts);

                    // Inside the interval nothing is probed.
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.Equal(0, probes);

                    // Still held: probed, and no full start.
                    Rewind(service, "_webFailedMs");
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.Equal(1, probes);
                    Assert.Equal(1, starts);
                    Assert.Null(Get<WebControllerServer>(service, "_webServer"));

                    // A language change keeps the reason.
                    Call(service, "RefreshServerStatusStrings");
                    Assert.Equal(string.Format(Strings.Instance.Server_PortInUse_Format, 18090), vm.Dashboard.WebControllerStatus);

                    // Freed: the next probe passes and the full start serves.
                    held = false;
                    Rewind(service, "_webFailedMs");
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.Equal(2, probes);
                    Assert.Equal(2, starts);
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer")?.IsRunning == true);
                    PumpUntil(dispatcher, () => vm.Dashboard.IsWebControllerRunning);
                    Assert.False(Get<bool>(service, "_webPortInUse"));

                    // Held again after a stop and start, then the switch goes off.
                    held = true;
                    Call(service, "StopWebServer");
                    Call(service, "StartWebServerIfEnabled");
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer") == null);
                    Assert.True(Get<bool>(service, "_webPortInUse"));
                    vm.Dashboard.EnableWebController = false;
                    Call(service, "StopWebServer");
                    Rewind(service, "_webFailedMs");
                    int before = probes;
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.Equal(before, probes);
                    Assert.Equal(3, starts);

                    // The dashboard tick runs it.
                    string input = System.IO.File.ReadAllText(SourcePath("PadForge.App", "Services", "InputService.cs")).Replace("\r\n", "\n");
                    Assert.Contains("            RetryDsuServerOnPortInUse();\n            RetryWebServerOnPortInUse();\n", input);
                }
                finally
                {
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                }
            });
        }

        [Fact]
        public void ARunningServersHeldPlainPort_IsAddedAgain_ThroughTheTick()
        {
            RunSta(dispatcher =>
            {
                var vm = new MainViewModel();
                using var service = new InputService(vm);
                bool plainHeld = true;
                var firewall = new List<WebControllerServer.PlainHttpOptions>();
                service.WebServerFactory = () => new WebControllerServer(_ => null,
                    () => new HeldListener(() => false, () => plainHeld), plainFirewall: firewall.Add);
                service.WebPortProbe = _ => throw new InvalidOperationException("the main address serves");
                Set(service, "_inputManager", RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
                try
                {
                    vm.Dashboard.WebControllerAccessCode = WebControllerAccess.Generate();
                    vm.Dashboard.WebControllerPort = 18090;
                    vm.Dashboard.EnableWebControllerPlainHttp = true;
                    vm.Dashboard.WebControllerPlainHttpPort = 18091;
                    vm.Dashboard.EnableWebController = true;
                    Call(service, "StartWebServerIfEnabled");
                    PumpUntil(dispatcher, () => Get<WebControllerServer>(service, "_webServer")?.IsRunning == true);
                    var server = Get<WebControllerServer>(service, "_webServer");
                    Assert.True(server.PlainPortInUse);
                    Assert.False(server.IsPlainServing);

                    // Held: refused again, the main address still serves.
                    Rewind(service, "_webPlainRetryMs");
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.True(server.PlainPortInUse);
                    Assert.True(server.IsRunning);

                    plainHeld = false;
                    Rewind(service, "_webPlainRetryMs");
                    Call(service, "RetryWebServerOnPortInUse");
                    Assert.True(server.IsPlainServing);
                    Assert.False(server.PlainPortInUse);
                    Assert.Contains(firewall, p => p != null && p.Port == 18091);
                    PumpUntil(dispatcher, () => vm.Dashboard.WebControllerPlainStatus == server.PlainStatus);
                }
                finally
                {
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                }
            });
        }

        /// <summary>On a real http.sys listener on loopback: a plain port
        /// added again after its holder left serves, and asks for the code
        /// from the first request, as one served from the start does.</summary>
        [Fact]
        public async Task APlainAddressAddedAgain_StillAsksForTheCode()
        {
            var rng = new Random();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int main = 20000 + rng.Next(20000);
                int plain = main + 1;
                var holder = new HttpListener();
                holder.Prefixes.Add($"http://127.0.0.1:{plain}/");
                try { holder.Start(); }
                catch (HttpListenerException) { holder.Close(); continue; }

                using var server = new WebControllerServer(_ => null, () => new LoopbackListener());
                string code = WebControllerAccess.Generate();
                server.SetPlainAccessCode(code);
                bool started = server.Start(main, new WebControllerServer.PlainHttpOptions(plain, false));
                if (!started) { holder.Close(); continue; }
                Assert.True(server.PlainPortInUse);
                Assert.False(server.RetryPlainAddress());

                holder.Close();
                Assert.True(server.RetryPlainAddress());
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using (var refused = await http.GetAsync($"http://127.0.0.1:{plain}/"))
                    Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                using (var admitted = await http.GetAsync($"http://127.0.0.1:{plain}/?code={code}"))
                    Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
                using (var mainPage = await http.GetAsync($"http://127.0.0.1:{main}/"))
                    Assert.Equal(HttpStatusCode.OK, mainPage.StatusCode);
                return;
            }
            throw new InvalidOperationException("no free loopback port pair");
        }

        [Fact]
        public void TheProbe_StartsAndClosesOneListenerOnTheMainPrefix()
        {
            var free = new HeldListener(() => false, () => false);
            Assert.True(WebControllerServer.ProbePort(18090, () => free));
            Assert.Equal(new[] { "http://+:18090/" }, free.Prefixes);
            Assert.True(free.Closed);

            var held = new HeldListener(() => true, () => false);
            Assert.False(WebControllerServer.ProbePort(18090, () => held));
            Assert.True(held.Closed);
        }

        /// <summary>A server whose start failed leaves its reason on the
        /// card, and switching it off then shows Stopped, with no server to
        /// stop: the game mod server, the motion server and the web
        /// controller.</summary>
        [Fact]
        public void AServerSwitchedOffAfterAFailedStart_ShowsStopped()
        {
            RunSta(dispatcher =>
            {
                var vm = new MainViewModel();
                using var service = new InputService(vm);
                using var holder = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)holder.Client.LocalEndPoint).Port;
                service.WebServerFactory = () => new WebControllerServer(_ => null,
                    () => new HeldListener(() => true, plainHeld: () => false));
                service.WebPortProbe = _ => false;
                Set(service, "_inputManager", RuntimeHelpers.GetUninitializedObject(typeof(InputManager)));
                try
                {
                    var s = Strings.Instance;
                    vm.Dashboard.GameModDsxStandIn = false;
                    vm.Dashboard.GameModServerPort = port;
                    vm.Dashboard.EnableGameModServer = true;
                    vm.Dashboard.DsuMotionServerPort = port;
                    vm.Dashboard.EnableDsuMotionServer = true;
                    vm.Dashboard.WebControllerPort = 18090;
                    vm.Dashboard.EnableWebController = true;
                    Call(service, "StartGameModServerIfEnabled");
                    Call(service, "StartDsuServerIfEnabled");
                    Call(service, "StartWebServerIfEnabled");
                    PumpUntil(dispatcher, () =>
                        vm.Dashboard.GameModServerStatus == string.Format(s.GameMods_PortInUse_Format, port)
                        && vm.Dashboard.DsuServerStatus == string.Format(s.Server_PortInUse_Format, port)
                        && vm.Dashboard.WebControllerStatus == string.Format(s.Server_PortInUse_Format, 18090)
                        && Get<WebControllerServer>(service, "_webServer") == null);

                    // The switches go off.
                    vm.Dashboard.EnableGameModServer = false;
                    vm.Dashboard.EnableDsuMotionServer = false;
                    vm.Dashboard.EnableWebController = false;
                    Call(service, "StopGameModServer");
                    Call(service, "StopDsuServer");
                    Call(service, "StopWebServer");
                    PumpUntil(dispatcher, () =>
                        vm.Dashboard.GameModServerStatus == s.Common_Stopped
                        && vm.Dashboard.DsuServerStatus == s.Common_Stopped
                        && vm.Dashboard.WebControllerStatus == s.Common_Stopped);
                    Assert.False(vm.Dashboard.IsGameModServerRunning);
                    Assert.False(vm.Dashboard.IsDsuServerRunning);
                    Assert.False(vm.Dashboard.IsWebControllerRunning);
                }
                finally
                {
                    Call(service, "StopGameModServer");
                    Call(service, "StopDsuServer");
                    Call(service, "StopWebServer");
                    Set(service, "_inputManager", null);
                }
            });
        }

        // ── Helpers ──

        private static void Set(InputService service, string field, object value)
            => (typeof(InputService).GetField(field, Fields) ?? throw new MissingFieldException(field)).SetValue(service, value);

        private static T Get<T>(InputService service, string field)
            => (T)(typeof(InputService).GetField(field, Fields) ?? throw new MissingFieldException(field)).GetValue(service);

        private static void Rewind(InputService service, string field)
            => Set(service, field, Environment.TickCount64 - InputService.PortInUseRetryMs);

        private static void Call(InputService service, string name)
            => (typeof(InputService).GetMethod(name, Fields) ?? throw new MissingMethodException(name)).Invoke(service, null);

        private static void PumpUntil(Dispatcher dispatcher, Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, "timed out");
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }
        }

        private static void RunSta(Action<Dispatcher> body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                try { body(dispatcher); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dispatcher test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static string SourcePath(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.App"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var all = new List<string> { dir.FullName };
            all.AddRange(parts);
            return System.IO.Path.Combine(all.ToArray());
        }

        /// <summary>A listener whose main prefix or plain prefix another
        /// program holds while the given answer says so, refused as http.sys
        /// refuses a held port (error 183).</summary>
        private sealed class HeldListener : IWebControllerListener
        {
            private readonly Func<bool> _held;
            private readonly Func<bool> _plainHeld;
            private readonly ManualResetEventSlim _stopped = new();
            public readonly List<string> Prefixes = new();
            public bool Closed;
            public bool IsListening { get; private set; }

            public HeldListener(Func<bool> held, Func<bool> plainHeld)
            {
                _held = held;
                _plainHeld = plainHeld;
            }

            public void Start(string prefix)
            {
                Prefixes.Add(prefix);
                if (_held()) throw new HttpListenerException(183);
                IsListening = true;
            }

            public void AddPrefix(string prefix)
            {
                if (_plainHeld()) throw new HttpListenerException(183);
                Prefixes.Add(prefix);
            }

            public HttpListenerContext GetContext()
            {
                _stopped.Wait(TimeSpan.FromSeconds(10));
                throw new HttpListenerException();
            }

            public void Stop() { IsListening = false; _stopped.Set(); }
            public void Close() { Closed = true; Stop(); }
        }

        /// <summary>The production listener's behavior on 127.0.0.1 in place
        /// of the strong wildcard.</summary>
        private sealed class LoopbackListener : IWebControllerListener
        {
            private readonly HttpListener _listener = new();
            private static string Loopback(string prefix) => prefix.Replace("://+:", "://127.0.0.1:");
            public bool IsListening => _listener.IsListening;
            public void Start(string prefix) { _listener.Prefixes.Add(Loopback(prefix)); _listener.Start(); }
            public void AddPrefix(string prefix) => _listener.Prefixes.Add(Loopback(prefix));
            public HttpListenerContext GetContext() => _listener.GetContext();
            public void Stop() => _listener.Stop();
            public void Close() => _listener.Close();
        }
    }
}
