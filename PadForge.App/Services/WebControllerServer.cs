using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// Embedded HTTP + WebSocket server that serves a gamepad UI to browsers.
    /// Each connected client becomes a <see cref="WebControllerDevice"/> in the
    /// input pipeline. Follows the <see cref="DsuMotionServer"/> lifecycle pattern.
    /// </summary>
    public sealed class WebControllerServer : IDisposable
    {
        private const int MaxClients = 16;
        private const int DefaultPort = 8080;
        private const string WebAssetPrefix = "PadForge.WebAssets.";

        private HttpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private string _localIp;
        private bool _https;
        private readonly ConcurrentDictionary<string, int> _typePadCounters = new();
        private readonly ConcurrentDictionary<string, int> _clientPadIds = new();
        private bool _disposed;

        private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
        /// <summary>Serializes session registration: the client cap and the
        /// retire-the-previous-session step must decide together.</summary>
        private readonly object _registrationLock = new object();
        private Dictionary<string, byte[]> _imageCache;

        /// <summary>Raised when server status changes (for UI display).</summary>
        public event EventHandler<string> StatusChanged;

        /// <summary>Raised when a browser client connects and a device is created.</summary>
        public event Action<WebControllerDevice> DeviceConnected;

        /// <summary>Raised when a browser client disconnects.</summary>
        public event Action<WebControllerDevice> DeviceDisconnected;

        /// <summary>Number of currently connected clients.</summary>
        public int ClientCount => _clients.Count;

        /// <summary>The URL the server is listening on.</summary>
        public string Url => _localIp != null ? $"{(_https ? "https" : "http")}://{_localIp}:{_port}" : null;

        /// <summary>True when serving HTTPS (a secure context, required for the
        /// phone motion sensors, #296 phase 0).</summary>
        public bool IsHttps => _https;

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        public bool Start(int port = DefaultPort)
        {
            if (_running) return true;

            _port = port;
            _localIp = GetLocalIpAddress();

            try
            {
                // Pre-cache 2D model PNGs for web serving (must load on UI thread).
                if (_imageCache == null)
                {
                    if (Application.Current?.Dispatcher != null)
                        Application.Current.Dispatcher.Invoke(() => _imageCache = LoadImageCache());
                    else
                        _imageCache = LoadImageCache();
                }

                // Fire-and-forget: the rule only affects external inbound
                // reachability, not the local _listener.Start() bind below, and
                // Start() runs on the UI thread (the web-controller toggle),
                // where RunNetsh's two possible netsh spawns block up to 5s
                // each. EnsureFirewallRule is static, touches no instance
                // state, and is already best-effort (swallows its own
                // failures), so a thread-pool hop changes nothing but the
                // blocked thread.
                System.Threading.Tasks.Task.Run(() => EnsureFirewallRule(port));

                // Secure lane (#296 phase 0). DeviceMotionEvent only fires in
                // a secure context, so bind a self-signed cert and serve
                // https:// when the binding succeeds. Any failure (not
                // elevated, netsh unavailable) falls back to plain http, and
                // everything except the phone sensors still works.
                _https = WebControllerTls.EnsureHttpsBinding(port) != null;

                _listener = new HttpListener();
                _listener.Prefixes.Add($"{(_https ? "https" : "http")}://+:{port}/");
                try { _listener.Start(); }
                catch when (_https)
                {
                    // The https prefix can fail even after a good sslcert bind
                    // (namespace ACL, a stale binding). Retry as plain http so
                    // the controller still serves.
                    PadForge.Engine.SdlDiagLog.WriteLine("WEBTLS https listen failed, falling back to http");
                    try { _listener.Close(); } catch { }
                    _https = false;
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://+:{port}/");
                    _listener.Start();
                }
                _running = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    Name = "PadForge.WebServer",
                    IsBackground = true
                };
                _acceptThread.Start();

                StatusChanged?.Invoke(this, string.Format(Strings.Instance.Server_RunningOn_Format, Url));
                return true;
            }
            catch (HttpListenerException ex)
            {
                // Close before dropping the reference: a listener that bound
                // its prefix and failed later still holds the http.sys
                // registration, and nulling the field leaked it until process
                // exit, so the retry hit "port in use" against ourselves.
                try { _listener?.Close(); } catch { }
                _listener = null;
                var msg = ex.ErrorCode == 5
                    ? string.Format(Strings.Instance.Server_AccessDenied_Format, port)
                    : string.Format(Strings.Instance.Server_PortInUse_Format, port);
                StatusChanged?.Invoke(this, msg);
                return false;
            }
            catch (Exception)
            {
                try { _listener?.Close(); } catch { }
                _listener = null;
                StatusChanged?.Invoke(this, Strings.Instance.Server_FailedToStart);
                return false;
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            try { _listener?.Stop(); _listener?.Close(); }
            catch { /* best effort */ }

            // Snapshot the live devices BEFORE anything clears the registry.
            // Each receive loop's teardown is gated on a conditional remove
            // from _clients (so a browser reconnecting under the same id
            // cannot evict the new session), and Stop cleared the dictionary
            // out from under those loops. The remove then failed, the gate
            // stayed shut, and the web pad was never marked offline: it sat in
            // the device list as a phantom online controller for the rest of
            // the session.
            var stopping = new System.Collections.Generic.List<WebControllerDevice>();
            foreach (var kvp in _clients)
                if (kvp.Value?.Device != null) stopping.Add(kvp.Value.Device);

            // Close all client WebSockets.
            foreach (var kvp in _clients)
            {
                try { kvp.Value.CancellationSource.Cancel(); }
                catch { /* best effort */ }
            }

            _acceptThread?.Join(3000);
            _acceptThread = null;
            _listener = null;
            _clients.Clear();

            // Fire the teardown ourselves, AFTER the clear. Doing it here
            // rather than before is what keeps it exactly-once: with _clients
            // already empty no receive loop can win its conditional remove, so
            // this is the sole path that can raise DeviceDisconnected.
            foreach (var dev in stopping)
            {
                try
                {
                    dev.SetConnected(false);
                    DeviceDisconnected?.Invoke(dev);
                }
                catch { /* best effort */ }
            }
            _clientPadIds.Clear();
            _typePadCounters.Clear();

            // Give the http.sys certificate binding back. Nothing called
            // RemoveBinding, so turning the web controller off (or moving it to
            // another port, which stops and restarts) left the old port bound
            // to our certificate for the life of the machine, including after
            // uninstall. Off the UI thread: it spawns netsh.
            if (_https)
            {
                int boundPort = _port;
                _https = false;
                Task.Run(() => { try { WebControllerTls.RemoveBinding(boundPort); } catch { } });
            }

            StatusChanged?.Invoke(this, Strings.Instance.Common_Stopped);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }

        // ─────────────────────────────────────────────
        //  Accept loop
        // ─────────────────────────────────────────────

        private void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running) break;
                    // A dead listener throws immediately and forever, and the
                    // bare continue burned a core spinning on it. Stop when the
                    // listener is gone; pause briefly on a transient failure.
                    var l = _listener;
                    if (l == null || !l.IsListening) break;
                    Thread.Sleep(50);
                    continue;
                }

                if (ctx.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketAsync(ctx));
                }
                else
                {
                    // Serve static files on thread pool so the accept loop
                    // can immediately process the next request (e.g. WebSocket
                    // upgrade that arrives while a page is still being served).
                    var captured = ctx;
                    _ = Task.Run(() => ServeStaticFile(captured));
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Static file serving
        // ─────────────────────────────────────────────

        private void ServeStaticFile(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "/";

                if (path == "/") path = "/index.html";

                // Layout API endpoint.
                if (path == "/api/custom-layouts")
                {
                    ServeCustomLayoutsApi(ctx);
                    return;
                }

                if (path == "/api/layout")
                {
                    ServeLayoutApi(ctx);
                    return;
                }

                // Serve 2D model PNGs from image cache (/img/2DModels/...).
                if (path.StartsWith("/img/") && _imageCache != null)
                {
                    var imgPath = path.Substring(5); // strip "/img/"
                    if (_imageCache.TryGetValue(imgPath, out var imgBytes))
                    {
                        ctx.Response.ContentType = "image/png";
                        ctx.Response.ContentLength64 = imgBytes.Length;
                        ctx.Response.StatusCode = 200;
                        ctx.Response.OutputStream.Write(imgBytes, 0, imgBytes.Length);
                        ctx.Response.Close();
                        return;
                    }
                }

                // Map URL path to embedded resource name.
                // "/js/foo.js" → "PadForge.WebAssets.js.foo.js"
                var resourceName = WebAssetPrefix + path.TrimStart('/').Replace('/', '.');

                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);

                if (stream == null)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                // Read into buffer so we can set Content-Length (avoids chunked
                // encoding which can stall keep-alive on some mobile browsers).
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var body = ms.ToArray();

                ctx.Response.ContentType = GetContentType(path);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers["Pragma"] = "no-cache";
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>The custom-layouts API (#296 phase 4). GET lists the
        /// stored layouts; POST upserts one (returns its id); DELETE ?id=
        /// removes one. All storage rides WebCustomLayoutStore, which
        /// whitelists the schema, so the browser's payload is never stored
        /// verbatim.</summary>
        private void ServeCustomLayoutsApi(HttpListenerContext ctx)
        {
            try
            {
                switch (ctx.Request.HttpMethod)
                {
                    case "GET":
                    {
                        var bytes = Encoding.UTF8.GetBytes(WebCustomLayoutStore.Json);
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        break;
                    }
                    case "POST":
                    {
                        // Cap the READ, not the read result. ReadToEnd first and
                        // check the length after means a client on the LAN can
                        // make the app allocate as much as it cares to send.
                        const int MaxBody = 64 * 1024;
                        if (ctx.Request.ContentLength64 > MaxBody) { ctx.Response.StatusCode = 413; break; }
                        var buf = new char[MaxBody + 1];
                        int got = 0;
                        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        {
                            int n;
                            while (got < buf.Length && (n = reader.Read(buf, got, buf.Length - got)) > 0)
                                got += n;
                        }
                        if (got > MaxBody) { ctx.Response.StatusCode = 413; break; }
                        string body = new string(buf, 0, got);
                        var id = WebCustomLayoutStore.Upsert(body);
                        if (id == null) { ctx.Response.StatusCode = 400; break; }
                        var ok = Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}");
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = ok.Length;
                        ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                        break;
                    }
                    case "DELETE":
                    {
                        var id = ctx.Request.QueryString["id"];
                        ctx.Response.StatusCode = WebCustomLayoutStore.Delete(id) ? 200 : 404;
                        break;
                    }
                    default:
                        ctx.Response.StatusCode = 405;
                        break;
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        /// <summary>Renders a URL to a QR image for the Dashboard card (#296),
        /// so the phone scans instead of typing. Returns a frozen ImageSource,
        /// or null if the URL will not encode.</summary>
        public static System.Windows.Media.ImageSource RenderQr(string url)
        {
            try
            {
                var mods = QrCode.Encode(url);
                if (mods == null) return null;
                int size = mods.GetLength(0);
                const int scale = 6, quiet = 3;
                int dim = (size + quiet * 2) * scale;

                var pixels = new byte[dim * dim]; // 8bpp grayscale, 0=black, 255=white
                for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        if (mods[x, y])
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int px = (x + quiet) * scale + dx;
                                    int py = (y + quiet) * scale + dy;
                                    pixels[py * dim + px] = 0;
                                }

                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                    dim, dim, 96, 96, System.Windows.Media.PixelFormats.Gray8, null, pixels, dim);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static string GetContentType(string path)
        {
            if (path.EndsWith(".html")) return "text/html; charset=utf-8";
            if (path.EndsWith(".css")) return "text/css; charset=utf-8";
            if (path.EndsWith(".js")) return "application/javascript; charset=utf-8";
            if (path.EndsWith(".json")) return "application/json; charset=utf-8";
            if (path.EndsWith(".svg")) return "image/svg+xml";
            if (path.EndsWith(".png")) return "image/png";
            if (path.EndsWith(".ico")) return "image/x-icon";
            return "application/octet-stream";
        }

        // ─────────────────────────────────────────────
        //  WebSocket handling
        // ─────────────────────────────────────────────

        private async Task HandleWebSocketAsync(HttpListenerContext ctx)
        {
            WebSocket ws = null;
            try
            {
                // Extract client ID from query string.
                var clientId = ctx.Request.QueryString["id"] ?? Guid.NewGuid().ToString("N");

                if (_clients.Count >= MaxClients)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                    return;
                }

                var clientType = ctx.Request.QueryString["type"] ?? "xbox360";
                var layoutParam = ctx.Request.QueryString["layout"] ?? "xbox360";

                // Keepalive pings, so a phone that left the network (screen
                // off, Wi-Fi dropped, walked out of range) is detected instead
                // of parking a receive that never returns: without traffic the
                // TCP connection has nothing to fail on, and the session held
                // its device online and one of the MaxClients slots forever.
                var wsCtx = await ctx.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(30));
                ws = wsCtx.WebSocket;

                // Create device — reuse pad number for reconnecting clients.
                bool isTouchpadClient = clientType.Equals("touchpad", StringComparison.OrdinalIgnoreCase);
                bool hasTouchpad = isTouchpadClient ||
                    ctx.Request.QueryString["touchpad"] == "1";
                // Per-type pad numbering: each type (xbox360/ds4/touchpad) starts at 1.
                var typeKey = isTouchpadClient ? "touchpad" : layoutParam.ToLowerInvariant();
                var compositeKey = typeKey + ":" + clientId;
                var padId = _clientPadIds.GetOrAdd(compositeKey,
                    _ => _typePadCounters.AddOrUpdate(typeKey, 1, (_, v) => v + 1));
                string name;
                string customLayoutJson = null;
                if (isTouchpadClient)
                    name = $"Web Touchpad {padId}";
                else if (typeKey.StartsWith("custom:", StringComparison.Ordinal))
                {
                    // A builder pad (#296 phase 4): its own typeKey per layout
                    // id, so the ProductGuid hash gives it a distinct product
                    // identity like every stock layout.
                    customLayoutJson = WebCustomLayoutStore.Find(typeKey.Substring(7));
                    string customName = "Custom Pad";
                    if (customLayoutJson != null)
                    {
                        try
                        {
                            using var cdoc = JsonDocument.Parse(customLayoutJson);
                            if (cdoc.RootElement.TryGetProperty("name", out var np))
                                customName = np.GetString();
                        }
                        catch { }
                    }
                    name = $"{customName} {padId}";
                }
                else
                    name = $"{ResolveLayout(typeKey).NameStem} Web Controller {padId}";
                // Pass typeKey explicitly so each layout (xbox360 / ds4 /
                // touchpad) carries a distinct ProductGuid. Without this,
                // FindOrCreateUserDevice's BT-reconnect fallback would
                // migrate one layout's offline UserDevice row onto a
                // freshly-connecting client of a different layout (the
                // fallback gates on ProductGuid + offline status only),
                // which is the source of issue: switching layouts in the
                // browser silently overwrote the previous layout's row in
                // the Devices list.
                var device = new WebControllerDevice(compositeKey, name, isTouchpadClient, typeKey);
                if (hasTouchpad && !isTouchpadClient)
                    device.HasTouchpad = true; // gamepad layout with a touchpad zone
                if (!isTouchpadClient && customLayoutJson != null)
                {
                    // A custom pad's shape comes from its widgets: extended
                    // button slots from button codes past the standard 11, a
                    // touchpad surface when a touch area exists.
                    try
                    {
                        var ext = new List<int>();
                        // The pad's REAL surface, widget by widget. Without
                        // this the picker offered a full gamepad for a pad
                        // built from two buttons and a stick.
                        var axes = new List<int>();
                        var buttons = new List<int>();
                        bool hasPov = false;
                        using var cdoc = JsonDocument.Parse(customLayoutJson);
                        foreach (var w in cdoc.RootElement.GetProperty("widgets").EnumerateArray())
                        {
                            string kind = w.GetProperty("kind").GetString();
                            int code = w.TryGetProperty("code", out var cp0) ? cp0.GetInt32() : 0;
                            if (kind == "touch") device.HasTouchpad = true;
                            else if (kind == "dpad") hasPov = true;
                            else if (kind == "stick")
                            {
                                // A stick widget drives its base axis and the next.
                                if (!axes.Contains(code)) axes.Add(code);
                                if (!axes.Contains(code + 1)) axes.Add(code + 1);
                            }
                            else if (kind == "slider")
                            {
                                if (!axes.Contains(code)) axes.Add(code);
                            }
                            else if (kind == "button")
                            {
                                if (!buttons.Contains(code)) buttons.Add(code);
                                if (code > 10 && code != 16 && code < 22 && !ext.Contains(code)) ext.Add(code);
                            }
                        }
                        if (ext.Count > 0) device.SetExtendedButtons(ext.ToArray());
                        device.SetCustomSurface(axes.ToArray(), buttons.ToArray(), hasPov);
                    }
                    catch { }
                }
                else if (!isTouchpadClient)
                {
                    // Declare the extended slots (paddles / Misc) this layout's
                    // surface offers, so they surface in the picker with
                    // canonical names instead of arriving as unmapped codes.
                    var extDef = ResolveLayout(typeKey);
                    var ext = new List<int>();
                    foreach (var ov in extDef.Overlays)
                        if (ResolveInput(typeKey, ov.TargetName, out var im)
                            && im.kind == "button" && im.code > 10 && im.code != 16
                            && !ext.Contains(im.code))
                            ext.Add(im.code);
                    if (ext.Count > 0) device.SetExtendedButtons(ext.ToArray());
                }
                device.SetConnected(true);

                var cts = new CancellationTokenSource();
                var session = new ClientSession(ws, device, cts);

                // Handle rumble feedback → send to browser.
                device.RumbleRequested += (low, high) =>
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) return;
                    _ = SendJsonAsync(ws, new { type = "rumble", left = (int)low, right = (int)high }, cts.Token, session.SendGate);
                };
                // Lightbar + player identity (#296): the phone renders the
                // slot's LED color and player pips. Driven by the same
                // identity pass that lights physical pads.
                device.LedChanged += (r, g, b) =>
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) return;
                    _ = SendJsonAsync(ws, new { type = "led", r = (int)r, g = (int)g, b = (int)b }, cts.Token, session.SendGate);
                };
                device.PlayerIndexChanged += idx =>
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) return;
                    _ = SendJsonAsync(ws, new { type = "player", index = idx }, cts.Token, session.SendGate);
                };

                // Registration is one critical section: the MaxClients check
                // above is a cheap pre-filter that races (several browsers can
                // pass it before any of them registers), and a reconnect under
                // an existing id has to retire the OLD session before the new
                // device comes up. The old session's own teardown cannot do it:
                // its removal is conditional on still being the registered
                // session, so once the new one is in the dictionary the old
                // loop's remove fails and its device stayed online forever as a
                // phantom.
                lock (_registrationLock)
                {
                    _clients.TryGetValue(compositeKey, out var prior);
                    if (prior == null && _clients.Count >= MaxClients)
                    {
                        try { ws.Abort(); } catch { }
                        try { ws.Dispose(); } catch { }
                        cts.Dispose();
                        return;
                    }
                    if (prior != null)
                    {
                        // Claim the teardown here so the old loop's conditional
                        // remove cannot also fire it.
                        ((ICollection<KeyValuePair<string, ClientSession>>)_clients)
                            .Remove(new KeyValuePair<string, ClientSession>(compositeKey, prior));
                        try { prior.CancellationSource.Cancel(); } catch { }
                        try
                        {
                            prior.Device.SetConnected(false);
                            DeviceDisconnected?.Invoke(prior.Device);
                        }
                        catch { /* best effort */ }
                    }
                    _clients[compositeKey] = session;
                }

                // Once the session is registered, everything runs under try/finally
                // so a throw from an event handler (DeviceConnected/StatusChanged),
                // the confirm send, or the receive path can't orphan the session in
                // _clients: that would leak ws/cts and permanently consume one of
                // MaxClients slots. The finally is the sole teardown path.
                try
                {
                    // Notify that a device connected.
                    DeviceConnected?.Invoke(device);
                    StatusChanged?.Invoke(this, string.Format(Strings.Instance.Server_RunningClients_Format, _clients.Count));

                    // Send connection confirmation.
                    await SendJsonAsync(ws, new { type = "connected", padId, name }, cts.Token, session.SendGate);
                    // A reconnecting client missed any LED/player push that
                    // happened while it was away; replay the current state.
                    device.ResendIdentity();

                    // Receive loop. A text message can arrive in several frames,
                    // and handing a fragment to the JSON parser dropped the
                    // whole message: assemble until EndOfMessage, with a cap so
                    // a client cannot grow the buffer without bound.
                    const int MaxMessageBytes = 16 * 1024;
                    var buffer = new byte[1024];
                    byte[] assembled = null;
                    int assembledLen = 0;
                    bool overflowed = false;
                    while (ws.State == WebSocketState.Open && _running && !cts.Token.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await ws.ReceiveAsync(
                                new ArraySegment<byte>(buffer), cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (WebSocketException)
                        {
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        if (result.MessageType != WebSocketMessageType.Text)
                            continue;

                        if (result.EndOfMessage && assembledLen == 0 && !overflowed)
                        {
                            // The common case: one frame, one message.
                            ProcessMessage(device, buffer, result.Count);
                            continue;
                        }

                        if (!overflowed)
                        {
                            if (assembledLen + result.Count > MaxMessageBytes)
                            {
                                overflowed = true;
                            }
                            else
                            {
                                if (assembled == null) assembled = new byte[MaxMessageBytes];
                                Buffer.BlockCopy(buffer, 0, assembled, assembledLen, result.Count);
                                assembledLen += result.Count;
                            }
                        }

                        if (result.EndOfMessage)
                        {
                            if (!overflowed && assembledLen > 0)
                                ProcessMessage(device, assembled, assembledLen);
                            assembledLen = 0;
                            overflowed = false;
                        }
                    }
                }
                finally
                {
                    // Cleanup. Conditional: a browser that reconnected with the
                    // same client id already replaced this session in _clients
                    // (the indexer overwrite above), and an unconditional remove
                    // here would evict the NEW session and offline the device the
                    // new connection just brought up (audit F9). Only tear down
                    // when this session is still the registered one.
                    bool stillRegistered = ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, ClientSession>>)_clients)
                        .Remove(new System.Collections.Generic.KeyValuePair<string, ClientSession>(compositeKey, session));
                    if (stillRegistered)
                    {
                        device.SetConnected(false);
                        DeviceDisconnected?.Invoke(device);
                    }
                    StatusChanged?.Invoke(this, _clients.Count > 0
                        ? string.Format(Strings.Instance.Server_RunningClients_Format, _clients.Count)
                        : string.Format(Strings.Instance.Server_RunningOn_Format, Url ?? $"http://{_localIp}:{_port}"));

                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                    catch { /* best effort */ }
                    cts.Dispose();
                    // Dispose the WebSocket after CloseAsync (the documented close
                    // lifecycle). A late fire-and-forget rumble send would hit a
                    // disposed socket, but SendJsonAsync is best-effort guarded and
                    // checks ws.State, so the throw is swallowed.
                    try { ws?.Dispose(); } catch { }
                    // Don't dispose SendGate: a rumble send may still hold it,
                    // and Release() on a disposed SemaphoreSlim throws into the
                    // fire-and-forget send. The gate never touches
                    // AvailableWaitHandle, so it holds no unmanaged handle and
                    // the GC reclaims it with nothing to leak.
                }
            }
            catch (Exception ex)
            {
                _ = ex;
                // Connection failed before WebSocket established.
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void ProcessMessage(WebControllerDevice device, byte[] data, int length)
        {
            try
            {
                using var doc = JsonDocument.Parse(data.AsMemory(0, length));
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();

                if (type == "input")
                {
                    var kind = root.GetProperty("kind").GetString();
                    var code = root.GetProperty("code").GetInt32();
                    var value = root.GetProperty("value").GetInt32();

                    if (kind == "button")
                        device.UpdateButton(code, value != 0);
                    else if (kind == "axis")
                        device.UpdateAxis(code, value);
                    else if (kind == "pov")
                        device.UpdatePov(value);
                }
                else if (type == "motion")
                {
                    // Phone motion (#296 phase 1): gyro rad/s + accel m/s²,
                    // already rotated into the SDL frame client-side. Caps flip
                    // on first arrival, mirroring the touchpad pattern, so the
                    // Devices page and the gyro pipeline discover the source
                    // the moment it streams.
                    if (!device.HasGyro) device.EnableMotionCaps();
                    float gx = root.TryGetProperty("gx", out var gxp) ? (float)gxp.GetDouble() : 0f;
                    float gy = root.TryGetProperty("gy", out var gyp) ? (float)gyp.GetDouble() : 0f;
                    float gz = root.TryGetProperty("gz", out var gzp) ? (float)gzp.GetDouble() : 0f;
                    float ax = root.TryGetProperty("ax", out var axp) ? (float)axp.GetDouble() : 0f;
                    float ay = root.TryGetProperty("ay", out var ayp) ? (float)ayp.GetDouble() : 0f;
                    float az = root.TryGetProperty("az", out var azp) ? (float)azp.GetDouble() : 0f;
                    device.UpdateMotion(gx, gy, gz, ax, ay, az);
                }
                else if (type == "caps")
                {
                    // What this browser can actually do. Sent once right after
                    // the socket opens. Only ever narrows a claim we would
                    // otherwise make on the client's behalf.
                    if (root.TryGetProperty("vibrate", out var vp)
                        && vp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        device.HasRumble = vp.GetBoolean();
                }
                else if (type == "touchpad")
                {
                    // DS4 controller page sends touchpad-finger messages from
                    // a gamepad connection — enable touchpad capability on
                    // first touch. Touchpad CLICK is no longer a special-case
                    // payload: controller_client.js emits a standard
                    // {type:"input", kind:"button", code:16, ...} message
                    // (handled by the "input" branch above) so this branch
                    // only carries finger position now.
                    device.HasTouchpad = true;

                    int finger = root.TryGetProperty("finger", out var fp) ? fp.GetInt32() : 0;
                    float x = root.TryGetProperty("x", out var xp) ? (float)xp.GetDouble() : 0f;
                    float y = root.TryGetProperty("y", out var yp) ? (float)yp.GetDouble() : 0f;
                    bool down = root.TryGetProperty("down", out var dp) && dp.GetBoolean();
                    device.UpdateTouchpadFinger(finger, x, y, down);
                }
            }
            catch
            {
                // Ignore malformed messages.
            }
        }

        private static async Task SendJsonAsync(WebSocket ws, object obj, CancellationToken ct, SemaphoreSlim gate = null)
        {
            if (ws.State != WebSocketState.Open) return;
            // Serialize concurrent sends on one socket — a managed WebSocket
            // throws if a second SendAsync starts before the first completes.
            if (gate != null)
            {
                try { await gate.WaitAsync(ct); }
                catch { return; }
            }
            try
            {
                var json = JsonSerializer.Serialize(obj);
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    ct);
            }
            catch { /* best effort */ }
            finally { gate?.Release(); }
        }

        // ─────────────────────────────────────────────
        //  2D model image cache + layout API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Where a layout's lightbar sits on its own art, and whether it
        /// carries player indicator LEDs.
        ///
        /// <para>The rule is the Lighting tab's, not a new one: a lightbar
        /// belongs to the DualSense family and the DualShock 4, and the
        /// indicator LEDs to the DualSense family alone (PadPage's
        /// hasLightbar / hasIndicatorLeds, "DS4 has neither"). Everything
        /// else shows no lighting at all, which is why this table has no
        /// entry for the Xbox, Switch or Steam layouts.</para>
        ///
        /// <para>Geometry is the same the Lighting tab's preview draws, in
        /// the same base-coordinate space the overlays use, with the same
        /// mask art. The Edge entry is the DualSense's shifted by 175,
        /// which is the exact offset all 24 of its shared overlays carry:
        /// its canvas is 350 wider and the pad sits centered in it.</para>
        /// </summary>
        private sealed class LightingDef
        {
            public (string Image, double X, double Y, double W, double H)[] Lightbar;
            public bool IndicatorLeds;
            public double PipCenterX, PipY;
        }

        private static readonly Dictionary<string, LightingDef> LightingDefs =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["ds4"] = new LightingDef
            {
                Lightbar = new[]
                {
                    ("2DModels/DS4/DS4_Lightbar_Front.png", 510d, 228d, 446d, 5d),
                    ("2DModels/DS4/DS4_Lightbar_Rear.png", 495d, 111d, 474d, 28d),
                },
                IndicatorLeds = false,
            },
            ["dualsense"] = new LightingDef
            {
                Lightbar = new[] { ("2DModels/DualSense/DualSense_Lightbar.png", 411d, 189d, 647d, 293d) },
                IndicatorLeds = true, PipCenterX = 736.5, PipY = 505,
            },
            ["dualsenseedge"] = new LightingDef
            {
                Lightbar = new[] { ("2DModels/DUALSENSEEDGE/DualSense_Lightbar.png", 586d, 189d, 647d, 293d) },
                IndicatorLeds = true, PipCenterX = 911.5, PipY = 505,
            },
        };

        private static Dictionary<string, byte[]> LoadImageCache()
        {
            var cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            void Load(string resourcePath)
            {
                if (cache.ContainsKey(resourcePath)) return;
                try
                {
                    var sri = Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative));
                    if (sri == null) return;
                    using var ms = new MemoryStream();
                    sri.Stream.CopyTo(ms);
                    cache[resourcePath] = ms.ToArray();
                }
                catch { }
            }

            void LoadAll(string folder, string basePath, OverlayElement[] overlays)
            {
                Load(basePath);
                foreach (var ov in overlays)
                    if (!string.IsNullOrEmpty(ov.ImageFile))
                        Load($"2DModels/{folder}/{ov.ImageFile}");
            }

            // Lightbar masks are not overlays, so LoadAll never sees them.
            foreach (var lit in LightingDefs.Values)
                foreach (var bar in lit.Lightbar)
                    Load(bar.Image);

            foreach (var def in LayoutDefs)
            {
                LoadAll(def.Folder, def.BasePath, def.Overlays);
                // Colorway variants: the finish-suffixed base art, plus any
                // finish-suffixed overlay art (Xbox Series ships per-colorway
                // stick and trigger PNGs). Missing files just don't cache,
                // and the API only redirects to variants that did.
                foreach (var finish in def.Finishes)
                {
                    Load(WithFinish(def.BasePath, finish));
                    foreach (var ov in def.Overlays)
                        if (!string.IsNullOrEmpty(ov.ImageFile))
                            Load(WithFinish($"2DModels/{def.Folder}/{ov.ImageFile}", finish));
                }
            }

            return cache;
        }

        /// <summary>One row per servable layout: the 2D model class values,
        /// the art folder, the device display-name stem, and the colorway
        /// finishes whose base art ships. Adding a layout is one row; the
        /// ProductGuid distinctness rule is upheld automatically because the
        /// typeKey feeds WebControllerDevice's per-layout product hash.</summary>
        private sealed class LayoutDef
        {
            public string TypeKey;
            public string Folder;
            public string NameStem;
            public int BaseWidth, BaseHeight;
            public string BasePath;
            public double StickMaxTravel;
            public OverlayElement[] Overlays;
            public string[] Finishes;
        }

        private static readonly LayoutDef[] LayoutDefs =
        {
            new() { TypeKey = "xbox360", Folder = "XBOX360", NameStem = "Xbox 360",
                    BaseWidth = Xbox360Layout.BaseWidth, BaseHeight = Xbox360Layout.BaseHeight,
                    BasePath = Xbox360Layout.BasePath, StickMaxTravel = Xbox360Layout.StickMaxTravel,
                    Overlays = Xbox360Layout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "ds4", Folder = "DS4", NameStem = "DualShock 4",
                    BaseWidth = DS4Layout.BaseWidth, BaseHeight = DS4Layout.BaseHeight,
                    BasePath = DS4Layout.BasePath, StickMaxTravel = DS4Layout.StickMaxTravel,
                    Overlays = DS4Layout.Overlays,
                    Finishes = new[] { "GlacierWhite", "Gold", "MagmaRed", "MidnightBlue" } },
            new() { TypeKey = "dualsense", Folder = "DualSense", NameStem = "DualSense",
                    BaseWidth = DualSenseLayout.BaseWidth, BaseHeight = DualSenseLayout.BaseHeight,
                    BasePath = DualSenseLayout.BasePath, StickMaxTravel = DualSenseLayout.StickMaxTravel,
                    Overlays = DualSenseLayout.Overlays,
                    Finishes = new[] { "CosmicRed", "GalacticPurple", "Midnight", "NovaPink", "StarlightBlue" } },
            new() { TypeKey = "xboxone", Folder = "XBOXONE", NameStem = "Xbox One",
                    BaseWidth = XboxOneSLayout.BaseWidth, BaseHeight = XboxOneSLayout.BaseHeight,
                    BasePath = XboxOneSLayout.BasePath, StickMaxTravel = XboxOneSLayout.StickMaxTravel,
                    Overlays = XboxOneSLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "xboxseries", Folder = "XBOXSERIES", NameStem = "Xbox Series",
                    BaseWidth = XboxSeriesXLayout.BaseWidth, BaseHeight = XboxSeriesXLayout.BaseHeight,
                    BasePath = XboxSeriesXLayout.BasePath, StickMaxTravel = XboxSeriesXLayout.StickMaxTravel,
                    Overlays = XboxSeriesXLayout.Overlays,
                    Finishes = new[] { "Carbon", "DeepPink", "ElectricVolt", "PulseRed", "ShockBlue" } },
            new() { TypeKey = "switchpro", Folder = "SWITCHPRO", NameStem = "Switch Pro",
                    BaseWidth = SwitchProLayout.BaseWidth, BaseHeight = SwitchProLayout.BaseHeight,
                    BasePath = SwitchProLayout.BasePath, StickMaxTravel = SwitchProLayout.StickMaxTravel,
                    Overlays = SwitchProLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "switch2pro", Folder = "SWITCH2PRO", NameStem = "Switch 2 Pro",
                    BaseWidth = Switch2ProLayout.BaseWidth, BaseHeight = Switch2ProLayout.BaseHeight,
                    BasePath = Switch2ProLayout.BasePath, StickMaxTravel = Switch2ProLayout.StickMaxTravel,
                    Overlays = Switch2ProLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "dualsenseedge", Folder = "DUALSENSEEDGE", NameStem = "DualSense Edge",
                    BaseWidth = DualSenseEdgeLayout.BaseWidth, BaseHeight = DualSenseEdgeLayout.BaseHeight,
                    BasePath = DualSenseEdgeLayout.BasePath, StickMaxTravel = DualSenseEdgeLayout.StickMaxTravel,
                    Overlays = DualSenseEdgeLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "steamdeck", Folder = "STEAMDECK", NameStem = "Steam Deck",
                    BaseWidth = SteamDeckLayout.BaseWidth, BaseHeight = SteamDeckLayout.BaseHeight,
                    BasePath = SteamDeckLayout.BasePath, StickMaxTravel = SteamDeckLayout.StickMaxTravel,
                    Overlays = SteamDeckLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "steamcontroller", Folder = "STEAMCONTROLLER", NameStem = "Steam Controller",
                    BaseWidth = SteamControllerLayout.BaseWidth, BaseHeight = SteamControllerLayout.BaseHeight,
                    BasePath = SteamControllerLayout.BasePath, StickMaxTravel = SteamControllerLayout.StickMaxTravel,
                    Overlays = SteamControllerLayout.Overlays, Finishes = Array.Empty<string>() },
            new() { TypeKey = "steamcontroller2", Folder = "STEAMCONTROLLER2", NameStem = "Steam Controller (2026)",
                    BaseWidth = SteamController2Layout.BaseWidth, BaseHeight = SteamController2Layout.BaseHeight,
                    BasePath = SteamController2Layout.BasePath, StickMaxTravel = SteamController2Layout.StickMaxTravel,
                    Overlays = SteamController2Layout.Overlays, Finishes = Array.Empty<string>() },
        };

        /// <summary>Resolves a layout request key (query value, any case,
        /// legacy aliases included) to its registry row, defaulting to
        /// xbox360 exactly as the old if-chain did.</summary>
        private static LayoutDef ResolveLayout(string type)
        {
            string k = (type ?? "xbox360").Trim().ToLowerInvariant();
            foreach (var d in LayoutDefs)
                if (d.TypeKey == k) return d;
            return LayoutDefs[0];
        }

        private static readonly Dictionary<string, (string kind, int code)> _targetInputMap = new()
        {
            ["ButtonA"] = ("button", 0),
            ["ButtonB"] = ("button", 1),
            ["ButtonX"] = ("button", 2),
            ["ButtonY"] = ("button", 3),
            ["LeftShoulder"] = ("button", 4),
            ["RightShoulder"] = ("button", 5),
            ["ButtonBack"] = ("button", 6),
            ["ButtonStart"] = ("button", 7),
            ["LeftThumbButton"] = ("button", 8),
            ["RightThumbButton"] = ("button", 9),
            ["ButtonGuide"] = ("button", 10),
            ["DPadUp"] = ("dpad", 0),
            ["DPadDown"] = ("dpad", 18000),
            ["DPadLeft"] = ("dpad", 27000),
            ["DPadRight"] = ("dpad", 9000),
            ["LeftTrigger"] = ("axis", 2),
            ["RightTrigger"] = ("axis", 5),
            ["LeftThumbRing"] = ("stick", 0),   // axes 0,1
            ["RightThumbRing"] = ("stick", 3),  // axes 3,4
            ["TouchpadClick"] = ("button", 16), // SDL_GAMEPAD_BUTTON_TOUCHPAD slot
            // Extended standardized slots (SDL_gamepad.h): Misc1=11 is the
            // Share/Mute/Capture/QAM family, paddles are 12-15 in R1/L1/R2/L2
            // order, Misc2+=17.. carry per-device extras.
            ["ButtonShare"] = ("button", 11),
            ["ButtonMute"] = ("button", 11),
            ["ButtonQuickAccess"] = ("button", 11),
            ["ButtonC"] = ("button", 17),
            ["RightPaddle"] = ("button", 12),
            ["LeftPaddle"] = ("button", 13),
            ["Paddle1"] = ("button", 12), // Deck grips, SDL order R1,L1,R2,L2
            ["Paddle2"] = ("button", 13),
            ["Paddle3"] = ("button", 14),
            ["Paddle4"] = ("button", 15),
            ["LeftFunction"] = ("button", 17),  // DualSense Edge Fn pair
            ["RightFunction"] = ("button", 18),
            ["LeftGrip"] = ("button", 13),
            ["RightGrip"] = ("button", 12),
            ["LeftTouchpadClick"] = ("button", 16),
            ["RightTouchpadClick"] = ("button", 16),
        };

        /// <summary>Per-layout input overrides, for targets that mean different
        /// things on different devices. The 2015 Steam Controller has NO
        /// physical right thumbstick: its RIGHT TRACKPAD acts as the right
        /// stick, and SDL routes the right-pad click to
        /// SDL_GAMEPAD_BUTTON_RIGHT_STICK (slot 9), not the touchpad-click
        /// slot. Without this the layout mapped BOTH trackpad clicks to slot
        /// 16, so the two pads were the same input and read as "the touchpad
        /// twice" (owner report 2026-08-12). The Steam Deck keeps the default:
        /// it has a real right stick at slot 9 already, so its right-pad click
        /// stays on the touchpad-click slot.</summary>
        private static readonly Dictionary<string, Dictionary<string, (string kind, int code)>> _layoutInputOverrides = new()
        {
            ["steamcontroller"] = new()
            {
                // SDL_hidapi_steam.c: the left pad is "normally mapped to
                // D-Pad" and the right pad drives RIGHTX/RIGHTY with its
                // click on RIGHT_STICK. The surfaces carry inputKind so the
                // client binds them as a D-pad zone and a right-stick zone
                // instead of generic touch surfaces.
                ["LeftTouchpad"] = ("pov", 0),           // left pad = D-pad surface
                ["RightTouchpad"] = ("stick", 3),        // right pad = right stick surface
                // On glass there is no touch-vs-click distinction, so the pad
                // CLICK zones would sit on top of the surfaces and steal every
                // touch. They yield: kind "none" tells the client to build no
                // zone for them. The physical click semantics (dpad press /
                // right-stick click) are carried by the surfaces themselves.
                ["LeftTouchpadClick"] = ("none", 0),
                ["RightTouchpadClick"] = ("none", 0),
                // Its click is ALSO the right stick button, one bit doing
                // two jobs. Same reasoning: the surface carries it.
                ["RightThumbButton"] = ("none", 0),
                ["LeftThumbButton"] = ("button", 8),     // the single physical stick is the LEFT stick
            },
        };

        /// <summary>Resolves an overlay target to its (kind, code), honoring the
        /// per-layout overrides before the shared default map.</summary>
        private static bool ResolveInput(string typeKey, string target, out (string kind, int code) input)
        {
            if (typeKey != null
                && _layoutInputOverrides.TryGetValue(typeKey, out var ov)
                && ov.TryGetValue(target, out input))
                return true;
            return _targetInputMap.TryGetValue(target, out input);
        }

        /// <summary>A double as JSON: invariant, never the current culture.</summary>
        private static string Num(double v) =>
            v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"path/Stem.png" + "Carbon" -> "path/Stem_Carbon.png".</summary>
        private static string WithFinish(string path, string finish)
        {
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path : path.Substring(0, dot) + "_" + finish + path.Substring(dot);
        }

        private void ServeLayoutApi(HttpListenerContext ctx)
        {
            try
            {
                // Type aliases match the 2D-model folder names so URL queries
                // can request "?type=DualSense" or "?type=XBOXSERIES" directly.
                // Legacy "ds4" / "xbox360" still work for older clients.
                var type = ctx.Request.QueryString["type"] ?? "xbox360";
                var def = ResolveLayout(type);
                int baseWidth = def.BaseWidth, baseHeight = def.BaseHeight;
                string basePath = def.BasePath, folder = def.Folder;
                double stickMaxTravel = def.StickMaxTravel;
                OverlayElement[] overlays = def.Overlays;

                // Colorway: swap in the finish-suffixed base art when it
                // shipped. Overlay art swaps per-element below the same way.
                var finish = ctx.Request.QueryString["finish"];
                bool hasFinish = !string.IsNullOrEmpty(finish)
                    && Array.FindIndex(def.Finishes, f => f.Equals(finish, StringComparison.OrdinalIgnoreCase)) >= 0;
                if (hasFinish)
                {
                    finish = def.Finishes[Array.FindIndex(def.Finishes, f => f.Equals(finish, StringComparison.OrdinalIgnoreCase))];
                    var variant = WithFinish(def.BasePath, finish);
                    if (_imageCache != null && _imageCache.ContainsKey(variant)) basePath = variant;
                }

                // Every number in this document goes out invariant. A
                // StringBuilder.Append(double) formats in the CURRENT culture,
                // and PadForge pins no culture, so on any comma-decimal Windows
                // (de, fr, es, pt, ru, most of Europe and South America) every
                // coordinate emitted "0,42" and the browser's JSON.parse threw
                // on the whole layout: the web controller was a blank page for
                // those users and worked perfectly for everyone testing it.
                var sb = new StringBuilder(4096);
                sb.Append("{\"baseWidth\":").Append(baseWidth)
                  .Append(",\"baseHeight\":").Append(baseHeight)
                  .Append(",\"basePath\":\"").Append(basePath).Append('"')
                  .Append(",\"stickMaxTravel\":").Append(Num(stickMaxTravel))
                  .Append(",\"overlays\":[");

                for (int i = 0; i < overlays.Length; i++)
                {
                    var ov = overlays[i];
                    if (i > 0) sb.Append(',');

                    var elementType = ov.ElementType switch
                    {
                        OverlayElementType.Button => "button",
                        OverlayElementType.Trigger => "trigger",
                        OverlayElementType.TriggerBase => "triggerBase",
                        OverlayElementType.StickRing => "stickRing",
                        OverlayElementType.StickClick => "stickClick",
                        OverlayElementType.Touchpad => "touchpad",
                        _ => "button"
                    };

                    string ovPath = $"2DModels/{folder}/{ov.ImageFile}";
                    if (hasFinish)
                    {
                        var ovVariant = WithFinish(ovPath, finish);
                        if (_imageCache != null && _imageCache.ContainsKey(ovVariant)) ovPath = ovVariant;
                    }
                    sb.Append("{\"image\":\"").Append(ovPath).Append('"')
                      .Append(",\"target\":\"").Append(ov.TargetName).Append('"')
                      .Append(",\"type\":\"").Append(elementType).Append('"')
                      .Append(",\"x\":").Append(Num(ov.X))
                      .Append(",\"y\":").Append(Num(ov.Y))
                      .Append(",\"w\":").Append(Num(ov.Width))
                      .Append(",\"h\":").Append(Num(ov.Height));

                    if (ResolveInput(def.TypeKey, ov.TargetName, out var input))
                    {
                        sb.Append(",\"inputKind\":\"").Append(input.kind).Append('"')
                          .Append(",\"inputCode\":").Append(input.code);
                    }

                    sb.Append('}');
                }

                sb.Append("],\"finishes\":[");
                for (int i = 0; i < def.Finishes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(def.Finishes[i]).Append('"');
                }
                sb.Append(']');

                // Lighting, for the layouts whose hardware actually has it.
                // Absent for every other layout, which is what stops the
                // client drawing a bar on a pad that owns none.
                if (LightingDefs.TryGetValue(def.TypeKey, out var lit))
                {
                    sb.Append(",\"lightbar\":[");
                    for (int i = 0; i < lit.Lightbar.Length; i++)
                    {
                        var bar = lit.Lightbar[i];
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"image\":\"").Append(bar.Image).Append('"')
                          .Append(",\"x\":").Append(Num(bar.X))
                          .Append(",\"y\":").Append(Num(bar.Y))
                          .Append(",\"w\":").Append(Num(bar.W))
                          .Append(",\"h\":").Append(Num(bar.H)).Append('}');
                    }
                    sb.Append(']');
                    if (lit.IndicatorLeds)
                    {
                        sb.Append(",\"indicatorLeds\":{\"cx\":").Append(Num(lit.PipCenterX))
                          .Append(",\"y\":").Append(Num(lit.PipY)).Append('}');
                    }
                }
                sb.Append('}');

                var json = sb.ToString();
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        // ─────────────────────────────────────────────
        //  Network utility
        // ─────────────────────────────────────────────

        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 80);
                return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
            }
            catch
            {
                return "localhost";
            }
        }

        // ─────────────────────────────────────────────
        //  Firewall
        // ─────────────────────────────────────────────

        private const string FirewallRuleName = "PadForge Web Controller";

        private static void EnsureFirewallRule(int port) => EnsureInboundFirewallRule(FirewallRuleName, "TCP", port);

        /// <summary>Adds an inbound allow rule for one port unless a rule of
        /// that name already names the port. Best effort, blocking (netsh
        /// spawns): callers hop to the thread pool. Shared with the head
        /// tracker's UDP listener (#355).</summary>
        internal static void EnsureInboundFirewallRule(string ruleName, string protocol, int port)
        {
            try
            {
                // Delete by name, then add. Reading netsh's own output to
                // decide whether the rule already exists cannot be done
                // portably: the dump is rendered from the firewall's MUI
                // resources, so "LocalPort" is translated on a localized
                // Windows and any label match silently never fires. The
                // earlier substring test over the raw dump avoided the
                // language problem and had a worse one, matching "42" inside
                // "4242".
                //
                // Delete-then-add needs no parsing at all, is idempotent, and
                // clears the pile-up the port-change path had been building:
                // nothing ever removed the rule for a port the user moved
                // away from, so the rules accumulated one per port under the
                // same name. A delete with no matching rule is an error netsh
                // reports and this swallows.
                RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol={protocol} localport={port}");
            }
            catch { /* best effort, the app may not be elevated */ }
        }

        private static string RunNetsh(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;

            // Start the read BEFORE waiting, and do not block on it. ReadToEnd
            // returns only when the pipe closes, so a netsh that wedged without
            // exiting hung here forever and the five-second timeout on the next
            // line was unreachable. Kicking the read off asynchronously lets
            // WaitForExit own the deadline, and the Kill closes the pipe, which
            // is what completes the read.
            var read = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(5_000))
            {
                try { proc.Kill(); } catch { }
            }

            try { return read.Wait(2_000) ? read.Result : string.Empty; }
            catch { return string.Empty; }
        }

        // ─────────────────────────────────────────────
        //  Client session record
        // ─────────────────────────────────────────────

        private sealed class ClientSession
        {
            public WebSocket Socket { get; }
            public WebControllerDevice Device { get; }
            public CancellationTokenSource CancellationSource { get; }
            /// <summary>Serializes ws.SendAsync — a managed WebSocket forbids a
            /// second outstanding send, so rapid rumble (SetRumble + StopRumble)
            /// must not overlap.</summary>
            public SemaphoreSlim SendGate { get; } = new SemaphoreSlim(1, 1);

            public ClientSession(WebSocket socket, WebControllerDevice device, CancellationTokenSource cts)
            {
                Socket = socket;
                Device = device;
                CancellationSource = cts;
            }
        }
    }
}
