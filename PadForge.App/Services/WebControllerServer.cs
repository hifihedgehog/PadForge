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
        private int _nextPadId;
        private readonly ConcurrentDictionary<string, int> _clientPadIds = new();
        private bool _disposed;

        private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
        private Dictionary<string, byte[]> _imageCache;
        private static readonly string _logPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "webserver.log");

        private static void Log(string msg)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
                File.AppendAllText(_logPath, line);
            }
            catch { }
        }

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

                EnsureFirewallRule(port);

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
                Log($"Server started on port {port}");

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
            _nextPadId = 0;

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

                Log(
                    $"[WebServer] Request: {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} " +
                    $"IsWebSocket={ctx.Request.IsWebSocketRequest}");

                if (ctx.Request.IsWebSocketRequest)
                {
                    // Handle WebSocket on thread pool.
                    _ = Task.Run(() => HandleWebSocketAsync(ctx));
                }
                else
                {
                    // Serve static files.
                    ServeStaticFile(ctx);
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

                ctx.Response.ContentType = GetContentType(path);
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers["Pragma"] = "no-cache";
                stream.CopyTo(ctx.Response.OutputStream);
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

                Log(
                    $"[WebServer] Accepting WebSocket from {ctx.Request.RemoteEndPoint}...");
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                ws = wsCtx.WebSocket;
                Log(
                    $"[WebServer] WebSocket accepted, state={ws.State}");

                // Create device — reuse pad number for reconnecting clients.
                var clientType = ctx.Request.QueryString["type"] ?? "xbox360";
                bool isTouchpadClient = clientType.Equals("touchpad", StringComparison.OrdinalIgnoreCase);
                var padId = _clientPadIds.GetOrAdd(clientId,
                    _ => Interlocked.Increment(ref _nextPadId));
                var name = isTouchpadClient ? $"Web Touchpad {padId}" : $"Web Controller {padId}";
                var device = new WebControllerDevice(clientId, name, isTouchpadClient);
                device.SetConnected(true);

                var cts = new CancellationTokenSource();
                var session = new ClientSession(ws, device, cts);

                // Handle rumble feedback → send to browser.
                device.RumbleRequested += (low, high) =>
                {
                    if (cts.IsCancellationRequested || ws.State != WebSocketState.Open) return;
                    _ = SendJsonAsync(ws, new { type = "rumble", left = (int)low, right = (int)high }, cts.Token);
                };

                _clients[clientId] = session;

                // Notify that a device connected.
                DeviceConnected?.Invoke(device);
                StatusChanged?.Invoke(this, string.Format(Strings.Instance.Server_RunningClients_Format, _clients.Count));

                // Send connection confirmation.
                await SendJsonAsync(ws, new { type = "connected", padId, name }, cts.Token);

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

                // Cleanup.
                device.SetConnected(false);
                _clients.TryRemove(clientId, out _);

                DeviceDisconnected?.Invoke(device);
                StatusChanged?.Invoke(this, _clients.Count > 0
                    ? string.Format(Strings.Instance.Server_RunningClients_Format, _clients.Count)
                    : string.Format(Strings.Instance.Server_RunningOn_Format, $"http://{_localIp}:{_port}"));

                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch { /* best effort */ }
                finally
                {
                    cts.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log(
                    $"[WebServer] WebSocket error: {ex.GetType().Name}: {ex.Message}");
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
                    if (root.TryGetProperty("click", out _))
                    {
                        device.UpdateTouchpadClick(true);
                    }
                    else
                    {
                        int finger = root.TryGetProperty("finger", out var fp) ? fp.GetInt32() : 0;
                        float x = root.TryGetProperty("x", out var xp) ? (float)xp.GetDouble() : 0f;
                        float y = root.TryGetProperty("y", out var yp) ? (float)yp.GetDouble() : 0f;
                        bool down = root.TryGetProperty("down", out var dp) && dp.GetBoolean();
                        device.UpdateTouchpadFinger(finger, x, y, down);
                    }
                }
            }
            catch
            {
                // Ignore malformed messages.
            }
        }

        private static async Task SendJsonAsync(WebSocket ws, object obj, CancellationToken ct)
        {
            if (ws.State != WebSocketState.Open) return;
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

            // Xbox 360
            Load(Xbox360Layout.BasePath);
            foreach (var ov in Xbox360Layout.Overlays)
                Load($"2DModels/XBOX360/{ov.ImageFile}");

            // DS4
            Load(DS4Layout.BasePath);
            foreach (var ov in DS4Layout.Overlays)
                Load($"2DModels/DS4/{ov.ImageFile}");

            Log($"Image cache loaded: {cache.Count} files");
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
        };

        private void ServeLayoutApi(HttpListenerContext ctx)
        {
            try
            {
                var type = ctx.Request.QueryString["type"] ?? "xbox360";
                var isDs4 = type.Equals("ds4", StringComparison.OrdinalIgnoreCase);

                var baseWidth = isDs4 ? DS4Layout.BaseWidth : Xbox360Layout.BaseWidth;
                var baseHeight = isDs4 ? DS4Layout.BaseHeight : Xbox360Layout.BaseHeight;
                var basePath = isDs4 ? DS4Layout.BasePath : Xbox360Layout.BasePath;
                var stickMaxTravel = isDs4 ? DS4Layout.StickMaxTravel : Xbox360Layout.StickMaxTravel;
                var overlays = isDs4 ? DS4Layout.Overlays : Xbox360Layout.Overlays;
                var folder = isDs4 ? "DS4" : "XBOX360";

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

            public ClientSession(WebSocket socket, WebControllerDevice device, CancellationTokenSource cts)
            {
                Socket = socket;
                Device = device;
                CancellationSource = cts;
            }
        }
    }
}
