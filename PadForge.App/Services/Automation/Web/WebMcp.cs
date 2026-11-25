using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// The MCP endpoint of the web helper (#600 item 11): Streamable HTTP, a
    /// POST per message, answered in plain JSON.
    ///
    /// <para>Each request is served statelessly, as the SDK's own HTTP
    /// handler serves a stateless one (csharp-sdk v2.4.0
    /// ModelContextProtocol.AspNetCore StreamableHttpHandler.StartNewSessionAsync,
    /// serveStatelessly): a <see cref="StreamableHttpServerTransport"/> with
    /// <c>Stateless</c>, a server made for it, the message handed to it, and
    /// the server ended with the request. That transport writes every reply
    /// as an event stream (StreamableHttpPostTransport through its
    /// SseEventWriter), which a quick tunnel cannot carry ("Quick Tunnels do
    /// not support Server-Sent Events"), so the reply is gathered in memory,
    /// the response to the request taken from it, and sent as JSON alone.
    /// Notifications on the way (progress) are left out. The
    /// specification lets the server choose JSON for any request.</para>
    ///
    /// <para>The checks come in the order that handler makes them: the
    /// Accept header, the body, the protocol version header, the version
    /// in the body's metadata, the method and name headers, the required
    /// client capabilities, and the methods a per-request revision removed.
    /// GET and DELETE are refused with 405, which both the 2025-11-25
    /// transport (no standalone stream offered) and the 2026-07-28 one (no
    /// GET endpoint, no sessions) allow, and no session ID is made, so every
    /// revision is served without sessions. <c>subscriptions/listen</c>,
    /// whose answer never ends, is not offered, and the lists' change notices
    /// are off.</para>
    ///
    /// <para>Behind each web assistant stands one copy of the assistant
    /// connection (<see cref="AssistantMirror"/>), with its own pipe session
    /// to PadForge named by its grant, so PadForge lists it by the address
    /// its sign-in returned to, under the user's level, with its own Activity
    /// and Undo.</para>
    /// </summary>
    internal sealed class WebMcp : IAsyncDisposable
    {
        /// <summary>The revisions the endpoint serves, as the SDK it is
        /// built on supports them (src/Common/McpProtocolVersions.cs
        /// SupportedProtocolVersions at v2.4.0).</summary>
        internal static readonly string[] Versions = { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28" };

        /// <summary>The first revision that carries its metadata in each
        /// request.</summary>
        internal const string PerRequestVersion = "2026-07-28";

        /// <summary>How long one request may take: the question card's two
        /// minutes and a little more.</summary>
        internal static readonly TimeSpan LongestCall = TimeSpan.FromSeconds(150);

        /// <summary>How long a web assistant's connection to PadForge stays
        /// open after its last request.</summary>
        internal static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(10);

        /// <summary>Makes the copy that stands behind a grant. A test gives
        /// its own.</summary>
        private readonly Func<WebGrant, AssistantMirror> _makeMirror;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Behind> _behind = new(StringComparer.Ordinal);

        private sealed class Behind
        {
            public AssistantMirror Mirror;
            public CancellationTokenSource Stop;
            public Task Upstream;
            public DateTimeOffset LastUsed;
        }

        public WebMcp(Func<WebGrant, AssistantMirror> makeMirror, Func<DateTimeOffset> now = null)
        {
            _makeMirror = makeMirror ?? throw new ArgumentNullException(nameof(makeMirror));
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>The grants with a connection to PadForge now, for tests.</summary>
        internal IReadOnlyList<string> Connected
        {
            get { lock (_sync) return _behind.Keys.ToList(); }
        }

        /// <summary>One POST to the endpoint by a signed-in web
        /// assistant.</summary>
        internal async Task<WebReply> PostAsync(WebRequest request, WebGrant grant, CancellationToken cancel)
        {
            // The client MUST accept both (transport: "Sending Messages").
            string accept = request.Header("Accept") ?? string.Empty;
            if (!Accepts(accept, "application/json") || !Accepts(accept, "text/event-stream"))
                return WebReply.RpcError(406, -32000, "Not Acceptable: the client must accept both application/json and text/event-stream.");

            JsonRpcMessage message;
            try { message = JsonSerializer.Deserialize<JsonRpcMessage>(request.Body ?? string.Empty, McpJsonUtilities.DefaultOptions); }
            catch (JsonException error) { return WebReply.RpcError(400, (int)McpErrorCode.InvalidRequest, "The body is not a JSON-RPC message: " + error.Message); }
            if (message is null) return WebReply.RpcError(400, (int)McpErrorCode.InvalidRequest, "The body is not a JSON-RPC message.");
            if (message is not (JsonRpcRequest or JsonRpcNotification))
                return WebReply.RpcError(400, (int)McpErrorCode.InvalidRequest, "A client sends requests and notifications only.");
            var id = message is JsonRpcRequest asked ? JsonSerializer.SerializeToNode(asked.Id, McpJsonUtilities.DefaultOptions) : null;

            string version = request.Header("MCP-Protocol-Version");
            if (!string.IsNullOrEmpty(version) && !Versions.Contains(version))
                return Unsupported(version, id);
            bool modern = IsPerRequest(version);
            if (modern)
            {
                var parameters = (message as JsonRpcRequest)?.Params ?? (message as JsonRpcNotification)?.Params;
                var meta = (parameters as JsonObject)?["_meta"] as JsonObject;
                string inBody = meta?["io.modelcontextprotocol/protocolVersion"] is JsonValue v && v.TryGetValue(out string text) ? text : null;
                if (message is JsonRpcRequest && inBody == null)
                    return WebReply.RpcError(400, (int)McpErrorCode.InvalidParams, "Requests at " + version + " carry _meta/io.modelcontextprotocol/protocolVersion.", id);
                if (inBody != null && inBody != version)
                    return WebReply.RpcError(400, (int)McpErrorCode.HeaderMismatch, "The MCP-Protocol-Version header does not match the body's.", id);
                string mismatch = HeaderMismatch(request, message);
                if (mismatch != null) return WebReply.RpcError(400, (int)McpErrorCode.HeaderMismatch, mismatch, id);
                if (message is JsonRpcRequest && meta?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                    return WebReply.RpcError(400, (int)McpErrorCode.InvalidParams, "Requests at " + version + " carry _meta/io.modelcontextprotocol/clientCapabilities as an object.", id);
                if (message is JsonRpcRequest { Method: "initialize" or "ping" or "logging/setLevel" or "resources/subscribe" or "resources/unsubscribe" } removed)
                    return WebReply.RpcError(404, (int)McpErrorCode.MethodNotFound, "Method '" + removed.Method + "' is not available at " + version + ".", id);
            }
            if (message is JsonRpcRequest { Method: RequestMethods.SubscriptionsListen })
                return WebReply.RpcError(404, (int)McpErrorCode.MethodNotFound, "PadForge's web door answers in plain JSON and keeps no stream open, so it offers no subscriptions/listen.", id);

            // A notification is taken and answered with 202 and no body. With
            // no session there is nothing for it to change.
            if (message is not JsonRpcRequest call) return new WebReply(202, null, null);

            var behind = Ensure(grant);
            message.Context = new JsonRpcMessageContext { ProtocolVersion = string.IsNullOrEmpty(version) ? null : version };
            await using var transport = new StreamableHttpServerTransport { Stateless = true };
            var options = behind.Mirror.Options();
            options.ScopeRequests = false;
            await using var server = McpServer.Create(transport, options);
            using var running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            running.CancelAfter(LongestCall);
            var run = server.RunAsync(running.Token);
            using var buffer = new MemoryStream();
            int status = 200;
            try
            {
                await transport.HandlePostRequestAsync(message, buffer, first =>
                {
                    // The 2026-07-28 revision puts some errors on the status
                    // line, as the SDK's handler does (onResponseStarting).
                    if (modern && first is JsonRpcError { Error: { } error })
                        status = (McpErrorCode)error.Code switch
                        {
                            McpErrorCode.MethodNotFound => 404,
                            McpErrorCode.MissingRequiredClientCapability or McpErrorCode.UnsupportedProtocolVersion or McpErrorCode.HeaderMismatch => 400,
                            _ => status,
                        };
                    return default;
                }, running.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return WebReply.RpcError(504, (int)McpErrorCode.InternalError, "PadForge did not answer in time.", id);
            }
            finally
            {
                running.Cancel();
                try { await run.ConfigureAwait(false); } catch { }
            }
            string answer = FinalOf(buffer.ToArray(), id);
            if (answer == null) return WebReply.RpcError(500, (int)McpErrorCode.InternalError, "PadForge gave no answer.", id);
            return new WebReply(status, WebReply.Json, answer).With("Cache-Control", "no-store");
        }

        /// <summary>The response to the request among the events of a
        /// buffered reply, as its JSON text, or null.</summary>
        internal static string FinalOf(byte[] events, JsonNode id)
        {
            string found = null;
            using var stream = new MemoryStream(events);
            foreach (var item in SseParser.Create(stream).Enumerate())
            {
                JsonObject node;
                try { node = JsonNode.Parse(item.Data) as JsonObject; }
                catch (JsonException) { continue; }
                if (node == null || !(node.ContainsKey("result") || node.ContainsKey("error"))) continue;
                if (id != null && !JsonNode.DeepEquals(node["id"], id)) continue;
                found = item.Data;
            }
            return found;
        }

        private static string HeaderMismatch(WebRequest request, JsonRpcMessage message)
        {
            string method = (message as JsonRpcRequest)?.Method ?? (message as JsonRpcNotification)?.Method;
            string header = request.Header("Mcp-Method")?.Trim();
            if (header == null) return "Missing required Mcp-Method header.";
            if (header != method) return "Header mismatch: Mcp-Method header value '" + header + "' does not match body value '" + method + "'.";
            string field = method switch
            {
                RequestMethods.ToolsCall or RequestMethods.PromptsGet => "name",
                RequestMethods.ResourcesRead => "uri",
                _ => null,
            };
            if (field == null) return null;
            string name = request.Header("Mcp-Name")?.Trim();
            if (name == null) return "Missing required Mcp-Name header.";
            foreach (char c in name)
                if (!(c == '\t' || (c >= ' ' && c <= '~'))) return "Header mismatch: Mcp-Name header contains invalid characters.";
            string decoded = McpHeaderEncoder.DecodeValue(name);
            if (decoded == null) return "Header mismatch: Mcp-Name header contains invalid Base64 encoding.";
            var parameters = (message as JsonRpcRequest)?.Params as JsonObject;
            string inBody = parameters?[field] is JsonValue v && v.TryGetValue(out string text) ? text : null;
            return decoded == inBody ? null : "Header mismatch: Mcp-Name header value does not match the body's " + field + ".";
        }

        private static WebReply Unsupported(string version, JsonNode id)
            => WebReply.JsonOf(400, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = (int)McpErrorCode.UnsupportedProtocolVersion,
                    ["message"] = "Bad Request: The MCP-Protocol-Version header value '" + version + "' is not supported.",
                    ["data"] = new JsonObject
                    {
                        ["supported"] = new JsonArray(Versions.Select(x => (JsonNode)x).ToArray()),
                        ["requested"] = version,
                    },
                },
            });

        internal static bool IsPerRequest(string version)
            => !string.IsNullOrEmpty(version) && string.CompareOrdinal(version, PerRequestVersion) >= 0;

        private static bool Accepts(string accept, string type)
        {
            foreach (string part in accept.Split(','))
            {
                string media = part.Split(';')[0].Trim();
                if (string.Equals(media, type, StringComparison.OrdinalIgnoreCase) || media == "*/*"
                    || (media.EndsWith("/*", StringComparison.Ordinal) && type.StartsWith(media.Substring(0, media.Length - 1), StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────
        //  The copy behind each web assistant
        // ─────────────────────────────────────────────

        private Behind Ensure(WebGrant grant)
        {
            lock (_sync)
            {
                if (!_behind.TryGetValue(grant.Id, out var behind))
                {
                    behind = new Behind { Mirror = _makeMirror(grant), Stop = new CancellationTokenSource() };
                    var stop = behind.Stop.Token;
                    var mirror = behind.Mirror;
                    behind.Upstream = Task.Run(() => mirror.RunUpstreamAsync(stop));
                    _behind[grant.Id] = behind;
                }
                behind.LastUsed = _now();
                return behind;
            }
        }

        /// <summary>Ends the connection of every grant that is no longer
        /// allowed, and of every one unused for <see cref="IdleClose"/>.</summary>
        internal void Sweep(IEnumerable<string> allowed)
        {
            var keep = new HashSet<string>(allowed ?? Array.Empty<string>(), StringComparer.Ordinal);
            List<Behind> gone = new();
            lock (_sync)
            {
                var now = _now();
                foreach (var pair in _behind.ToList())
                    if (!keep.Contains(pair.Key) || now - pair.Value.LastUsed > IdleClose)
                    {
                        gone.Add(pair.Value);
                        _behind.Remove(pair.Key);
                    }
            }
            foreach (var behind in gone) _ = EndAsync(behind);
        }

        private static async Task EndAsync(Behind behind)
        {
            try { behind.Stop.Cancel(); } catch (ObjectDisposedException) { }
            try { await behind.Upstream.ConfigureAwait(false); } catch { }
            try { await behind.Mirror.DisposeAsync().ConfigureAwait(false); } catch { }
            behind.Stop.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            List<Behind> all;
            lock (_sync)
            {
                all = _behind.Values.ToList();
                _behind.Clear();
            }
            foreach (var behind in all) await EndAsync(behind).ConfigureAwait(false);
        }
    }

    /// <summary>One HTTP request as the web helper reads it.</summary>
    internal sealed record WebRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string Header(string name) => Headers != null && Headers.TryGetValue(name, out var value) ? value : null;

        internal static IReadOnlyDictionary<string, string> HeadersOf(params (string Name, string Value)[] headers)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in headers) map[name] = value;
            return map;
        }
    }
}
