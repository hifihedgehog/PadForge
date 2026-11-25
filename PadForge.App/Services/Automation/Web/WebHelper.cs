using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// What <c>PadForge.exe --web</c> runs (#600 item 11): the web helper,
    /// which PadForge starts through the plugin launcher while Reach PadForge
    /// From the Web is on, without rights, an engine or a window. It loads
    /// none of WPF.
    ///
    /// <para>It opens <c>PadForge.Automation</c> (owner checked, as every
    /// client does) and says <c>hello kind=web-helper</c> with the one-time key
    /// PadForge gave it in its variables. PadForge takes that key only from
    /// the process it started, so a helper anything else starts is refused
    /// and ends with code 1. From there the pipe carries JSON lines both ways
    /// (<see cref="WebLink"/>): the armed window, the allowed web assistants
    /// and the tunnel the user picked come down, and sign-ins, new token
    /// hashes, the port and the tunnel's address go up.</para>
    ///
    /// <para>It listens on <c>http://127.0.0.1:port/</c> only. A loopback
    /// listener needs no URL reservation from a process without rights, and
    /// its prefix takes a request whatever Host it names (both probed on this
    /// PC on 2026-11-24), so a tunnel's Host reaches it and the front door
    /// checks it. It ends when PadForge closes its pipe.</para>
    /// </summary>
    internal static class WebHelper
    {
        /// <summary>The variable PadForge hands the helper its one-time key
        /// in. Read once and taken out of the helper's own environment, so
        /// nothing it starts inherits it.</summary>
        internal const string KeyVariable = "PADFORGE_WEB_KEY";

        /// <summary>The pipe's name, for a test's own.</summary>
        internal const string PipeVariable = "PADFORGE_PIPE";

        internal const int Done = 0, Refused = 1, NotRunning = 4;

        /// <summary>The whole run.</summary>
        internal static int Run()
        {
            string key = Environment.GetEnvironmentVariable(KeyVariable);
            Environment.SetEnvironmentVariable(KeyVariable, null);
            string pipe = Environment.GetEnvironmentVariable(PipeVariable);
            try
            {
                return RunAsync(key, string.IsNullOrEmpty(pipe) ? AutomationPipe.PipeName : pipe, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("PadForge's web helper stopped: " + error.Message); } catch { }
                return Refused;
            }
        }

        /// <summary>Runs the helper against a pipe until PadForge closes it or
        /// <paramref name="stopping"/>. A test runs it with its own pipe.</summary>
        internal static async Task<int> RunAsync(string key, string pipeName, CancellationToken stopping,
            Func<string, CancellationToken, Task<string>> fetchDocument = null, Action<int> listening = null, Action<WebRequest, WebReply> answered = null)
        {
            if (string.IsNullOrEmpty(key))
            {
                Console.Error.WriteLine("PadForge starts its web helper itself, while Reach PadForge From the Web is on.");
                return Refused;
            }
            using var pipe = PipeOwner.Open(pipeName);
            if (pipe == null) return NotRunning;
            try { await pipe.ConnectAsync(AssistantMirror.ConnectMs, stopping).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or IOException) { return NotRunning; }
            if (!PipeOwner.IsServer(pipe.SafePipeHandle, pipeName)) return Refused;

            byte[] hello = Encoding.UTF8.GetBytes("hello kind=web-helper token=" + key + "\n");
            await pipe.WriteAsync(hello, stopping).ConfigureAwait(false);
            await pipe.FlushAsync(stopping).ConfigureAwait(false);
            string answer = await WebLink.ReadLineAsync(pipe, stopping).ConfigureAwait(false);
            if (answer == null || !answer.StartsWith("ok ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("PadForge did not take this web helper: it takes only the one it started.");
                return Refused;
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            await using var link = new WebLink(pipe);
            var reading = link.RunAsync(stop.Token);
            // The first state says which port PadForge kept and the language.
            await Task.WhenAny(link.FirstState, reading).ConfigureAwait(false);
            if (!link.FirstState.IsCompleted) return Done;

            using var listener = Listen(link.WantedPort, out int port);
            if (listener == null) return Refused;
            link.Say(new JsonObject { ["t"] = "port", ["port"] = port });
            listening?.Invoke(port);
            link.Say(new JsonObject
            {
                ["t"] = "found",
                ["tunnels"] = new JsonArray(WebTunnels.Find().Select(f => (JsonNode)new JsonObject { ["key"] = f.Key, ["path"] = f.Path }).ToArray()),
            });

            var auth = new WebAuth(link, fetchDocument);
            await using var mcp = new WebMcp(grant => new AssistantMirror(pipeName, padForgeRuns: () => true, host: "web",
                fileSettings: () => AutomationFileSettings.None)
            {
                HelloLine = () => "hello kind=web token=" + key + " grant=" + grant.Id,
                LocalFiles = false,
                ListChanges = false,
                StartPadForge = () => false,
            });
            var front = new WebFront(port, auth, mcp, link, () => link.PublicBase);
            link.Changed += () => mcp.Sweep(link.Grants.Select(g => g.Id));
            link.Port = port;

            var serving = ServeAsync(listener, front, stop.Token, answered);
            using var sweep = new Timer(_ => mcp.Sweep(link.Grants.Select(g => g.Id)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            try
            {
                await reading.ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                link.StopTunnel();
                try { listener.Stop(); } catch { }
                try { await serving.ConfigureAwait(false); } catch { }
            }
            return Done;
        }

        /// <summary>A listener on 127.0.0.1 at the port PadForge kept, else
        /// at a port the system reports free.</summary>
        internal static HttpListener Listen(int wanted, out int port)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                port = attempt == 0 && wanted is > 1024 and < 65536 ? wanted : FreePort();
                if (port <= 0) continue;
                string prefix = WebAddress.Loopback(port) + "/";
                // The test host binds nothing beyond loopback.
                PadForge.Engine.Common.NetworkListenerGate.EnterPrefix(prefix);
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    return listener;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
            }
            port = 0;
            return null;
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                probe.Start();
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                return 0;
            }
            finally
            {
                probe.Stop();
            }
        }

        /// <summary>Takes requests until <paramref name="stopping"/>, each on
        /// its own task.</summary>
        internal static async Task ServeAsync(HttpListener listener, WebFront front, CancellationToken stopping, Action<WebRequest, WebReply> answered = null)
        {
            using var stopsListening = stopping.Register(() => { try { listener.Stop(); } catch { } });
            while (!stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => AnswerAsync(context, front, stopping, answered));
            }
        }

        private static async Task AnswerAsync(HttpListenerContext context, WebFront front, CancellationToken stopping, Action<WebRequest, WebReply> answered)
        {
            var response = context.Response;
            try
            {
                var request = context.Request;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in request.Headers.AllKeys)
                    if (name != null) headers[name] = request.Headers[name];
                // The Host as the client sent it, which HTTP.sys keeps.
                if (!headers.ContainsKey("Host") && request.UserHostName != null) headers["Host"] = request.UserHostName;
                string path = request.Url?.AbsolutePath ?? "/";
                int limit = path == WebAddress.McpPath ? WebFront.LongestBody : WebFront.LongestFormBody;
                string body = null;
                if (request.HasEntityBody)
                {
                    body = await ReadBodyAsync(request.InputStream, limit, stopping).ConfigureAwait(false);
                    if (body == null)
                    {
                        await WriteAsync(response, WebReply.RpcError(413, -32000, "The body is too large."), stopping).ConfigureAwait(false);
                        return;
                    }
                }
                var asked = new WebRequest(request.HttpMethod, path, request.Url?.Query, headers, body);
                var reply = await front.HandleAsync(asked, stopping).ConfigureAwait(false);
                // A test's own record of what was asked and answered.
                try { answered?.Invoke(asked, reply); } catch { }
                await WriteAsync(response, reply, stopping).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try { response.StatusCode = 500; response.Close(); } catch { }
            }
        }

        private static async Task<string> ReadBodyAsync(Stream stream, int limit, CancellationToken stopping)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            while (true)
            {
                int n = await stream.ReadAsync(chunk, stopping).ConfigureAwait(false);
                if (n == 0) break;
                buffer.Write(chunk, 0, n);
                if (buffer.Length > limit) return null;
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static async Task WriteAsync(HttpListenerResponse response, WebReply reply, CancellationToken stopping)
        {
            response.StatusCode = reply.Status;
            response.KeepAlive = true;
            foreach (var header in reply.Headers) response.Headers[header.Key] = header.Value;
            if (reply.Location != null) response.RedirectLocation = reply.Location;
            if (reply.Body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(reply.Body);
                response.ContentType = reply.ContentType;
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, stopping).ConfigureAwait(false);
            }
            else response.ContentLength64 = 0;
            response.Close();
        }
    }

    /// <summary>
    /// The web helper's side of its pipe to PadForge (#600 item 11), JSON
    /// lines both ways, and the app's side as the helper's OAuth server sees
    /// it (<see cref="IWebAppSide"/>).
    /// </summary>
    internal sealed class WebLink : IWebAppSide, IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly ConcurrentDictionary<string, WebGrant> _grants = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _asking = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _tunnelSync = new object();
        private System.Diagnostics.Process _tunnel;
        private string _tunnelKey;
        private volatile string _tunnelAddress;
        private volatile string _pasted;
        private long _armedUntil;

        public WebLink(Stream stream) => _stream = stream;

        public event Action Changed;

        internal Task FirstState => _first.Task;
        internal int WantedPort { get; private set; }
        /// <summary>The loopback port the helper listens on. A tunnel waits
        /// for it.</summary>
        internal int Port
        {
            get => _port;
            set
            {
                _port = value;
                ApplyTunnel();
            }
        }

        private int _port;
        private string _wantKey, _wantPath;
        private int _wantRun;
        private string _startedFor;

        /// <summary>The public base address now: the tunnel's, else the one
        /// the user pasted.</summary>
        internal string PublicBase => WebAddress.Base(_tunnelAddress) ?? WebAddress.Base(_pasted);

        public DateTimeOffset ArmedUntil => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _armedUntil));

        public IReadOnlyCollection<WebGrant> Grants => _grants.Values.ToList();

        public async Task<string> AskAsync(WebSignIn signIn, CancellationToken cancel)
        {
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _asking[signIn.Sid] = answer;
            using var giveUp = cancel.Register(() => answer.TrySetResult(null));
            Say(new JsonObject
            {
                ["t"] = "ask",
                ["sid"] = signIn.Sid,
                ["origin"] = signIn.Origin,
                ["name"] = signIn.Name,
                ["code"] = signIn.Code,
                ["through"] = signIn.Through,
                ["loopback"] = signIn.Loopback,
                ["clientId"] = signIn.ClientId,
                ["redirect"] = signIn.RedirectUri,
                ["resource"] = signIn.Resource,
            });
            try { return await answer.Task.ConfigureAwait(false); }
            finally { _asking.TryRemove(signIn.Sid, out _); }
        }

        public void Tokens(WebGrant grant)
        {
            _grants[grant.Id] = grant;
            Say(new JsonObject { ["t"] = "tokens", ["grant"] = WebWire.ToJson(grant) });
        }

        public void Used(string grantId) => Say(new JsonObject { ["t"] = "used", ["id"] = grantId });

        /// <summary>Sends one line to PadForge. A pipe that broke ends the
        /// run through the reader.</summary>
        internal void Say(JsonObject message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            _ = Task.Run(async () =>
            {
                await _write.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(bytes).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                finally { _write.Release(); }
            });
        }

        /// <summary>Reads PadForge's lines until the pipe closes.</summary>
        internal async Task RunAsync(CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                string line;
                try { line = await ReadLineAsync(_stream, stopping, timeoutMs: 0).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { return; }
                if (line == null) return;
                JsonObject message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (message == null) continue;
                Take(message);
            }
        }

        private void Take(JsonObject message)
        {
            switch (message["t"]?.GetValue<string>())
            {
                case "state":
                    Interlocked.Exchange(ref _armedUntil, message["armedUntil"]?.GetValue<long>() ?? 0);
                    var grants = message["grants"] as JsonArray ?? new JsonArray();
                    var keep = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var node in grants)
                    {
                        var grant = WebWire.FromJson(node as JsonObject);
                        if (grant?.Id == null) continue;
                        keep.Add(grant.Id);
                        // A token issued here since the app last wrote is
                        // the newer one.
                        _grants.AddOrUpdate(grant.Id, grant, (_, mine) => mine.AccessHash != grant.AccessHash && mine.AccessUntil > grant.AccessUntil ? mine : grant);
                    }
                    foreach (string id in _grants.Keys.Where(id => !keep.Contains(id)).ToList()) _grants.TryRemove(id, out _);
                    _pasted = message["pasted"]?.GetValue<string>();
                    if (message["port"]?.GetValue<int>() is int port) WantedPort = port;
                    if (message["language"]?.GetValue<string>() is { Length: > 0 } language)
                    {
                        try { PadForge.Resources.Strings.Strings.ChangeCulture(CultureInfo.GetCultureInfo(language)); }
                        catch (CultureNotFoundException) { }
                    }
                    _first.TrySetResult(true);
                    // PadForge hears which armed window the helper now holds.
                    Say(new JsonObject { ["t"] = "seen", ["armedUntil"] = Interlocked.Read(ref _armedUntil) });
                    lock (_tunnelSync)
                    {
                        _wantKey = message["tunnel"]?.GetValue<string>();
                        _wantPath = message["tunnelPath"]?.GetValue<string>();
                        _wantRun = message["tunnelRun"]?.GetValue<int>() ?? 0;
                    }
                    ApplyTunnel();
                    try { Changed?.Invoke(); } catch { }
                    break;
                case "answer":
                    string sid = message["sid"]?.GetValue<string>();
                    if (sid != null && _asking.TryGetValue(sid, out var waiting))
                        waiting.TrySetResult(message["grant"]?.GetValue<string>());
                    break;
            }
        }

        /// <summary>Starts the tunnel PadForge says, or stops the one that
        /// runs when it says none or another. The same choice starts once:
        /// again only when the user picks it again (its run number).</summary>
        private void ApplyTunnel()
        {
            lock (_tunnelSync)
            {
                if (_port == 0) return;
                string want = _wantKey == null ? null : _wantKey + "|" + _wantPath + "|" + _wantRun.ToString(CultureInfo.InvariantCulture);
                if (want == _startedFor) return;
                StopTunnelLocked();
                _startedFor = want;
                string key = _wantKey, path = _wantPath;
                var kind = WebTunnels.Of(key);
                if (kind == null) return;
                _tunnelKey = key;
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                {
                    Report(key, "missing", null, null);
                    return;
                }
                Report(key, "starting", null, null);
                string last = null;
                System.Diagnostics.Process started = null;
                started = WebTunnels.Start(kind, path, _port,
                    address =>
                    {
                        lock (_tunnelSync) if (_tunnelKey == key) _tunnelAddress = address;
                        Report(key, "running", address, null);
                        try { Changed?.Invoke(); } catch { }
                    },
                    line => last = line,
                    code =>
                    {
                        lock (_tunnelSync)
                        {
                            if (_tunnelKey != key || !ReferenceEquals(_tunnel, started)) return;
                            _tunnel = null;
                            _tunnelAddress = null;
                        }
                        Report(key, "stopped", null, Last(last));
                        try { Changed?.Invoke(); } catch { }
                    });
                _tunnel = started;
                if (_tunnel == null) Report(key, "failed", null, null);
            }
        }

        /// <summary>The program's last line as the page shows it: cleaned
        /// and cut.</summary>
        private static string Last(string line)
            => string.IsNullOrWhiteSpace(line) ? null : ProgramDeviceDeclaration.Clean(line, 200);

        private void Report(string key, string state, string address, string words)
            => Say(new JsonObject { ["t"] = "tunnel", ["key"] = key, ["state"] = state, ["address"] = address, ["words"] = words });

        internal void StopTunnel()
        {
            lock (_tunnelSync) StopTunnelLocked();
        }

        private void StopTunnelLocked()
        {
            var tunnel = _tunnel;
            _tunnel = null;
            _tunnelAddress = null;
            _tunnelKey = null;
            WebTunnels.Stop(tunnel);
        }

        /// <summary>One line of a stream, read a byte at a time so nothing
        /// after it is taken. Null at its end.</summary>
        internal static async Task<string> ReadLineAsync(Stream stream, CancellationToken stopping, int timeoutMs = 5000)
        {
            var line = new List<byte>(256);
            var one = new byte[1];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            if (timeoutMs > 0) timeout.CancelAfter(timeoutMs);
            while (line.Count < 1024 * 1024)
            {
                int n;
                try { n = await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return null; }
                if (n == 0) return null;
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.Add(one[0]);
            }
            return null;
        }

        public ValueTask DisposeAsync()
        {
            StopTunnel();
            foreach (var waiting in _asking.Values) waiting.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A grant as the pipe and PadForge.xml carry it.</summary>
    internal static class WebWire
    {
        internal static JsonObject ToJson(WebGrant g) => new()
        {
            ["id"] = g.Id,
            ["origin"] = g.Origin,
            ["name"] = g.Name,
            ["clientId"] = g.ClientId,
            ["redirect"] = g.RedirectUri,
            ["resource"] = g.Resource,
            ["access"] = g.AccessHash,
            ["accessUntil"] = g.AccessUntil.ToUnixTimeMilliseconds(),
            ["refresh"] = g.RefreshHash,
            ["refreshUntil"] = g.RefreshUntil.ToUnixTimeMilliseconds(),
        };

        internal static WebGrant FromJson(JsonObject o)
        {
            if (o == null) return null;
            string S(string name) => o[name] is JsonValue v && v.TryGetValue(out string s) ? s : null;
            long L(string name) => o[name] is JsonValue v && v.TryGetValue(out long n) ? n : 0;
            return new WebGrant
            {
                Id = S("id"),
                Origin = S("origin"),
                Name = S("name"),
                ClientId = S("clientId"),
                RedirectUri = S("redirect"),
                Resource = S("resource"),
                AccessHash = S("access"),
                AccessUntil = DateTimeOffset.FromUnixTimeMilliseconds(L("accessUntil")),
                RefreshHash = S("refresh"),
                RefreshUntil = DateTimeOffset.FromUnixTimeMilliseconds(L("refreshUntil")),
            };
        }
    }
}
