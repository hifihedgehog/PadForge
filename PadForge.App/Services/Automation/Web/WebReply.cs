using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PadForge.Services.Automation.Web
{
    /// <summary>What the web helper answers one HTTP request with: a status,
    /// a body with its type, and the headers beyond the ones every answer
    /// carries.</summary>
    internal sealed record WebReply(int Status, string ContentType, string Body)
    {
        public List<KeyValuePair<string, string>> Headers { get; } = new();

        /// <summary>Where a 302 sends the browser, or null.</summary>
        public string Location { get; init; }

        public WebReply With(string name, string value)
        {
            Headers.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }

        internal const string Json = "application/json";
        internal const string Html = "text/html; charset=utf-8";

        internal static WebReply JsonOf(int status, JsonNode body) => new(status, Json, body.ToJsonString());

        /// <summary>An OAuth error answer (RFC 6749 5.2): the error code and
        /// its words, never cached.</summary>
        internal static WebReply OAuthError(int status, string error, string description)
            => JsonOf(status, new JsonObject { ["error"] = error, ["error_description"] = description })
                .With("Cache-Control", "no-store").With("Pragma", "no-cache");

        /// <summary>A redirect of the browser.</summary>
        internal static WebReply Redirect(string location) => new(302, null, null) { Location = location };

        /// <summary>A JSON-RPC error with no id (the transport specification:
        /// "The HTTP response body MAY comprise a JSON-RPC error response
        /// that has no id").</summary>
        internal static WebReply RpcError(int status, int code, string message, JsonNode id = null)
            => JsonOf(status, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });
    }
}
