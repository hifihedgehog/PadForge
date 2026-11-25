using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// A web assistant the user allowed (#600 item 11), as the web helper
    /// and the app both hold it. It is named by the address its sign-in
    /// returns to (<see cref="Origin"/>), never by the name it gives
    /// (<see cref="Name"/>, shown as its own claim). Its tokens are kept as
    /// SHA-256 hashes only, and its token works only for the address it was
    /// issued for (<see cref="Resource"/>, RFC 8707).
    /// </summary>
    internal sealed record WebGrant
    {
        /// <summary>PadForge's own ID for it, random, for the pipe and the
        /// page.</summary>
        public string Id { get; init; }

        /// <summary>The host its sign-in returns to, such as claude.ai: what
        /// it is named by.</summary>
        public string Origin { get; init; }

        /// <summary>The name it gave in its registration or its metadata
        /// document, a claim.</summary>
        public string Name { get; init; }

        /// <summary>Its OAuth client ID: the address of its metadata
        /// document, or the ID its registration got.</summary>
        public string ClientId { get; init; }

        /// <summary>The address its sign-in returned to.</summary>
        public string RedirectUri { get; init; }

        /// <summary>The address its token works for: the MCP endpoint it
        /// signed in to.</summary>
        public string Resource { get; init; }

        public string AccessHash { get; init; }
        public DateTimeOffset AccessUntil { get; init; }
        public string RefreshHash { get; init; }
        public DateTimeOffset RefreshUntil { get; init; }

        public DateTimeOffset Allowed { get; init; }
        public DateTimeOffset? LastUsed { get; init; }
    }

    /// <summary>The secrets of the web door and their hashes.</summary>
    internal static class WebSecrets
    {
        /// <summary>A fresh secret: 32 random bytes, base64url.</summary>
        internal static string New(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

        /// <summary>The hash a token is kept as: SHA-256 of its text, lower
        /// hex. The token itself is never written anywhere.</summary>
        internal static string Hash(string token)
            => string.IsNullOrEmpty(token) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        /// <summary>Whether a presented token is the one a hash was kept
        /// for, compared in fixed time.</summary>
        internal static bool Matches(string token, string hash)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(hash) || hash.Length != 64) return false;
            byte[] want;
            try { want = Convert.FromHexString(hash); }
            catch (FormatException) { return false; }
            byte[] have = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return CryptographicOperations.FixedTimeEquals(have, want);
        }

        /// <summary>The PKCE S256 challenge of a verifier (RFC 7636 4.2):
        /// base64url of the SHA-256 of its ASCII.</summary>
        internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        /// <summary>A six-digit code for one sign-in, from the system's
        /// random source.</summary>
        internal static string SixDigits() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        /// <summary>A six-digit code as the card and the sign-in page show
        /// it: two groups of three.</summary>
        internal static string Spaced(string code)
            => code != null && code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code;

        internal static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>A PKCE verifier as RFC 7636 4.1 gives it: 43 to 128 of
        /// the unreserved characters.</summary>
        internal static bool IsVerifier(string text)
        {
            if (text == null || text.Length < 43 || text.Length > 128) return false;
            foreach (char c in text)
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~')) return false;
            return true;
        }

        /// <summary>An S256 challenge: 43 base64url characters.</summary>
        internal static bool IsChallenge(string text)
        {
            if (text == null || text.Length != 43) return false;
            foreach (char c in text)
                if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
            return true;
        }
    }

    /// <summary>Addresses of the web door.</summary>
    internal static class WebAddress
    {
        /// <summary>The MCP endpoint's path.</summary>
        internal const string McpPath = "/mcp";

        /// <summary>An address a user pasted or a tunnel reported, as the
        /// door's base: https, a host, no path, query or fragment, no
        /// trailing slash. Null when it is not one.</summary>
        internal static string Base(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)) return null;
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
            string path = uri.AbsolutePath.TrimEnd('/');
            // A pasted address may end in /mcp, the address an assistant takes.
            if (path.Length > 0 && !string.Equals(path, McpPath, StringComparison.OrdinalIgnoreCase)) return null;
            return "https://" + uri.IdnHost.ToLowerInvariant() + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The loopback base the helper listens on.</summary>
        internal static string Loopback(int port) => "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

        /// <summary>Two resource addresses compared as RFC 8707 resource
        /// indicators: scheme and host without regard to case, the rest
        /// exactly, a trailing slash ignored.</summary>
        internal static bool SameResource(string a, string b)
        {
            if (a == null || b == null) return false;
            if (!Uri.TryCreate(a, UriKind.Absolute, out var x) || !Uri.TryCreate(b, UriKind.Absolute, out var y)) return false;
            if (!string.IsNullOrEmpty(x.Fragment) || !string.IsNullOrEmpty(y.Fragment)) return false;
            return string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.IdnHost, y.IdnHost, StringComparison.OrdinalIgnoreCase)
                && x.Port == y.Port
                && string.Equals(x.AbsolutePath.TrimEnd('/'), y.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
                && string.Equals(x.Query, y.Query, StringComparison.Ordinal);
        }

        /// <summary>Whether a host is this PC by its own text.</summary>
        internal static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim('[', ']');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
        }

        /// <summary>A redirect address a client may register or use: https,
        /// or http to this PC (RFC 8252 7.3), absolute, without a fragment or
        /// user info. The security considerations of the authorization
        /// specification: "All redirect URIs MUST be either localhost or use
        /// HTTPS."</summary>
        internal static bool IsRedirect(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 2048) return false;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Scheme == Uri.UriSchemeHttps) return !string.IsNullOrEmpty(uri.Host);
            return uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host);
        }

        /// <summary>The host a redirect returns to, as the user is shown it.</summary>
        internal static string OriginOf(string redirect)
            => Uri.TryCreate(redirect, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null;

        /// <summary>A host without its port, for the card's "through".</summary>
        internal static string HostOf(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null;

        /// <summary>A query or form body as name and value pairs, the first
        /// of a repeated name kept and a repeated name noted, as RFC 6749
        /// 3.1 refuses a parameter sent twice.</summary>
        internal static Dictionary<string, string> Form(string text, out bool repeated)
        {
            repeated = false;
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return form;
            if (text[0] == '?') text = text.Substring(1);
            foreach (string pair in text.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name = Decode(eq < 0 ? pair : pair.Substring(0, eq));
                string value = eq < 0 ? string.Empty : Decode(pair.Substring(eq + 1));
                if (!form.TryAdd(name, value)) repeated = true;
            }
            return form;
        }

        private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

        /// <summary>An address with query parameters added in order.</summary>
        internal static string WithQuery(string url, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var sb = new StringBuilder(url);
            bool first = !url.Contains('?');
            foreach (var p in parameters)
            {
                if (p.Value == null) continue;
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(Uri.EscapeDataString(p.Key)).Append('=').Append(Uri.EscapeDataString(p.Value));
            }
            return sb.ToString();
        }
    }
}
