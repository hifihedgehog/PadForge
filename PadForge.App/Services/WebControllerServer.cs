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
        private readonly ConcurrentDictionary<string, int> _typePadCounters = new();
        private readonly ConcurrentDictionary<string, int> _clientPadIds = new();
        private bool _disposed;

        private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
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
        public string Url => _localIp != null ? $"http://{_localIp}:{_port}" : null;

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

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{port}/");
                _listener.Start();
                _running = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    Name = "PadForge.WebServer",
                    IsBackground = true
                };
                _acceptThread.Start();

                var url = $"http://{_localIp}:{port}";
                StatusChanged?.Invoke(this, string.Format(Strings.Instance.Server_RunningOn_Format, url));
                return true;
            }
            catch (HttpListenerException ex)
            {
                _listener = null;
                var msg = ex.ErrorCode == 5
                    ? string.Format(Strings.Instance.Server_AccessDenied_Format, port)
                    : string.Format(Strings.Instance.Server_PortInUse_Format, port);
                StatusChanged?.Invoke(this, msg);
                return false;
            }
            catch (Exception)
            {
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
            _clientPadIds.Clear();
            _typePadCounters.Clear();

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

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
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
                if (isTouchpadClient)
                    name = $"Web Touchpad {padId}";
                else if (typeKey == "ds4")
                    name = $"DualShock 4 Web Controller {padId}";
                else
                    name = $"Xbox 360 Web Controller {padId}";
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
                    device.HasTouchpad = true; // DS4 gamepad with touchpad zone
                device.SetConnected(true);

                var cts = new CancellationTokenSource();
                var session = new ClientSession(ws, device, cts);

                // Handle rumble feedback → send to browser.
                device.RumbleRequested += (low, high) =>
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) return;
                    _ = SendJsonAsync(ws, new { type = "rumble", left = (int)low, right = (int)high }, cts.Token, session.SendGate);
                };

                _clients[compositeKey] = session;

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

                    // Receive loop.
                    var buffer = new byte[1024];
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

                        if (result.MessageType == WebSocketMessageType.Text)
                            ProcessMessage(device, buffer, result.Count);
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
                        : string.Format(Strings.Instance.Server_RunningOn_Format, $"http://{_localIp}:{_port}"));

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

            LoadAll("XBOX360",    Xbox360Layout.BasePath,     Xbox360Layout.Overlays);
            LoadAll("DS4",        DS4Layout.BasePath,         DS4Layout.Overlays);
            LoadAll("DualSense",  DualSenseLayout.BasePath,   DualSenseLayout.Overlays);
            LoadAll("XBOXONE",    XboxOneSLayout.BasePath,    XboxOneSLayout.Overlays);
            LoadAll("XBOXSERIES", XboxSeriesXLayout.BasePath, XboxSeriesXLayout.Overlays);

            return cache;
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
        };

        private void ServeLayoutApi(HttpListenerContext ctx)
        {
            try
            {
                // Type aliases match the 2D-model folder names so URL queries
                // can request "?type=DualSense" or "?type=XBOXSERIES" directly.
                // Legacy "ds4" / "xbox360" still work for older clients.
                var type = ctx.Request.QueryString["type"] ?? "xbox360";
                int baseWidth, baseHeight;
                string basePath, folder;
                double stickMaxTravel;
                OverlayElement[] overlays;
                if (type.Equals("ds4", StringComparison.OrdinalIgnoreCase) || type.Equals("DS4", StringComparison.OrdinalIgnoreCase))
                {
                    baseWidth = DS4Layout.BaseWidth; baseHeight = DS4Layout.BaseHeight;
                    basePath = DS4Layout.BasePath; overlays = DS4Layout.Overlays;
                    stickMaxTravel = DS4Layout.StickMaxTravel; folder = "DS4";
                }
                else if (type.Equals("DualSense", StringComparison.OrdinalIgnoreCase))
                {
                    baseWidth = DualSenseLayout.BaseWidth; baseHeight = DualSenseLayout.BaseHeight;
                    basePath = DualSenseLayout.BasePath; overlays = DualSenseLayout.Overlays;
                    stickMaxTravel = DualSenseLayout.StickMaxTravel; folder = "DualSense";
                }
                else if (type.Equals("XBOXONE", StringComparison.OrdinalIgnoreCase))
                {
                    baseWidth = XboxOneSLayout.BaseWidth; baseHeight = XboxOneSLayout.BaseHeight;
                    basePath = XboxOneSLayout.BasePath; overlays = XboxOneSLayout.Overlays;
                    stickMaxTravel = XboxOneSLayout.StickMaxTravel; folder = "XBOXONE";
                }
                else if (type.Equals("XBOXSERIES", StringComparison.OrdinalIgnoreCase))
                {
                    baseWidth = XboxSeriesXLayout.BaseWidth; baseHeight = XboxSeriesXLayout.BaseHeight;
                    basePath = XboxSeriesXLayout.BasePath; overlays = XboxSeriesXLayout.Overlays;
                    stickMaxTravel = XboxSeriesXLayout.StickMaxTravel; folder = "XBOXSERIES";
                }
                else
                {
                    baseWidth = Xbox360Layout.BaseWidth; baseHeight = Xbox360Layout.BaseHeight;
                    basePath = Xbox360Layout.BasePath; overlays = Xbox360Layout.Overlays;
                    stickMaxTravel = Xbox360Layout.StickMaxTravel; folder = "XBOX360";
                }

                var sb = new StringBuilder(4096);
                sb.Append("{\"baseWidth\":").Append(baseWidth)
                  .Append(",\"baseHeight\":").Append(baseHeight)
                  .Append(",\"basePath\":\"").Append(basePath).Append('"')
                  .Append(",\"stickMaxTravel\":").Append(stickMaxTravel)
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

                    sb.Append("{\"image\":\"2DModels/").Append(folder).Append('/').Append(ov.ImageFile).Append('"')
                      .Append(",\"target\":\"").Append(ov.TargetName).Append('"')
                      .Append(",\"type\":\"").Append(elementType).Append('"')
                      .Append(",\"x\":").Append(ov.X)
                      .Append(",\"y\":").Append(ov.Y)
                      .Append(",\"w\":").Append(ov.Width)
                      .Append(",\"h\":").Append(ov.Height);

                    if (_targetInputMap.TryGetValue(ov.TargetName, out var input))
                    {
                        sb.Append(",\"inputKind\":\"").Append(input.kind).Append('"')
                          .Append(",\"inputCode\":").Append(input.code);
                    }

                    sb.Append('}');
                }

                sb.Append("]}");

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

        private static void EnsureFirewallRule(int port)
        {
            try
            {
                // Check if rule already exists.
                var check = RunNetsh($"advfirewall firewall show rule name=\"{FirewallRuleName}\"");
                if (check.Contains(port.ToString()))
                    return;

                // Add inbound TCP rule for the web server port.
                RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port}");
            }
            catch { /* best effort — app may not be elevated */ }
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
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5_000))
                try { proc.Kill(); } catch { }
            return output;
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
