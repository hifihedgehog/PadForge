using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation.Web
{
    /// <summary>
    /// OpenAI's tunnel-client for the ChatGPT Secure MCP Tunnel choice (#600
    /// DESIGN-2 N4): its latest Windows release fetched from GitHub into the
    /// protected folder's Work area, as steamcmd is kept there, and used only
    /// when its zip's SHA-256 is the one the release's SHA256SUMS.txt names,
    /// as the updater checks its own downloads. PadForge does this with its
    /// rights, so the file the helper runs is the one PadForge checked; only
    /// Administrators and SYSTEM can change anything under Work.
    ///
    /// <para>The helper starts it as <c>tunnel-client run --mcp-command
    /// "&lt;PadForge.exe&gt;" --mcp --health.listen-addr 127.0.0.1:&lt;port&gt;</c>,
    /// with the runtime key and the tunnel ID in its environment alone
    /// (CONTROL_PLANE_API_KEY, CONTROL_PLANE_TUNNEL_ID, tunnel-client's
    /// docs/configuration.md), never on its command line. tunnel-client
    /// splits <c>--mcp-command</c> with its own parseCommandArgv
    /// (pkg/runtimeconfig/config.go), which takes a backslash as an escape
    /// inside double quotes too, so every backslash of PadForge's path is
    /// doubled.</para>
    /// </summary>
    internal static class TunnelClient
    {
        /// <summary>The tunnel choice's word in PadForge.xml and on the
        /// wire.</summary>
        internal const string Key = "chatgpt";

        internal const string ExeName = "tunnel-client.exe";

        internal const string LatestRelease = "https://api.github.com/repos/openai/tunnel-client/releases/latest";

        /// <summary>The pages the card's buttons open: the runtime keys,
        /// tunnel management, and ChatGPT's own plugins page, where a tunnel
        /// is added.</summary>
        internal const string KeysPage = "https://platform.openai.com/settings/organization/api-keys";
        internal const string TunnelsPage = "https://platform.openai.com/settings/organization/tunnels";
        internal const string ChatGptPage = "https://chatgpt.com/plugins";

        internal const string KeyVariable = "CONTROL_PLANE_API_KEY", TunnelVariable = "CONTROL_PLANE_TUNNEL_ID";

        /// <summary>
        /// The <c>--mcp-command</c> value: PadForge's path in double quotes
        /// with every backslash doubled, and <c>--mcp</c>. tunnel-client's
        /// parser reads <c>\\</c> as one backslash inside quotes, so the
        /// program it starts is the path itself.
        /// </summary>
        internal static string McpCommand(string padForge)
            => "\"" + (padForge ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" --mcp";

        /// <summary>tunnel-client's arguments, each as one word.</summary>
        internal static IReadOnlyList<string> Arguments(string padForge, int healthPort)
            => new[] { "run", "--mcp-command", McpCommand(padForge), "--health.listen-addr", "127.0.0.1:" + healthPort.ToString(CultureInfo.InvariantCulture) };

        /// <summary>
        /// Words as one Windows command line, each read back whole by
        /// CommandLineToArgvW and by Go's own reader of it: a word with a
        /// space, a tab or a quote goes in quotes, a quote inside is escaped,
        /// and the backslashes before a quote or the closing quote are
        /// doubled (the rule Process.ArgumentList follows, PasteArguments).
        /// </summary>
        internal static string CommandLine(IEnumerable<string> words)
        {
            var sb = new StringBuilder();
            foreach (string word in words)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (word.Length > 0 && word.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    sb.Append(word);
                    continue;
                }
                sb.Append('"');
                int slashes = 0;
                foreach (char c in word)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') sb.Append('\\', slashes * 2 + 1);
                    else sb.Append('\\', slashes);
                    slashes = 0;
                    sb.Append(c);
                }
                sb.Append('\\', slashes * 2).Append('"');
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        //  The key, kept for the signed-in user alone
        // ─────────────────────────────────────────────

        /// <summary>The runtime key encrypted for the user this process runs
        /// as (DPAPI, CurrentUser), base64. The web helper does this, since
        /// it runs as the signed-in user, never PadForge with its
        /// rights.</summary>
        internal static string Protect(string key)
            => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));

        /// <summary>The runtime key from its blob, or null when this user
        /// cannot read it.</summary>
        internal static string Unprotect(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
            catch (Exception error) when (error is CryptographicException or FormatException) { return null; }
        }

        /// <summary>Whether a tunnel ID is one PadForge hands on: letters,
        /// digits, dashes and underscores, up to 100.</summary>
        internal static bool IsTunnelId(string id) => id != null && Regex.IsMatch(id, @"\A[A-Za-z0-9_\-]{1,100}\z");

        // ─────────────────────────────────────────────
        //  The download
        // ─────────────────────────────────────────────

        /// <summary>The release's Windows zip for this PC.</summary>
        internal static string AssetName(string tag, Architecture arch)
            => "tunnel-client-" + tag + "-windows-" + (arch == Architecture.Arm64 ? "arm64" : "amd64") + ".zip";

        /// <summary>The SHA-256 a sums file names for one file, lower hex, or
        /// null. Each line is the hash, a space, and the name, the name after
        /// a star in binary mode.</summary>
        internal static string SumOf(string sums, string file)
        {
            foreach (string raw in (sums ?? string.Empty).Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"\A([0-9A-Fa-f]{64})\s+\*?(.+)\z");
                if (m.Success && string.Equals(m.Groups[2].Value.Trim(), file, StringComparison.Ordinal)) return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        /// <summary>The release's tag and the download addresses of its zip
        /// for this PC and of its sums, from GitHub's releases/latest answer.
        /// Null when it names none.</summary>
        internal static (string Tag, string Zip, string Sums)? Release(string json, Architecture arch)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tag) || !Regex.IsMatch(tag, @"\Av[0-9][0-9A-Za-z.\-]{0,30}\z")) return null;
                string want = AssetName(tag, arch), zip = null, sums = null;
                foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString();
                    string url = asset.GetProperty("browser_download_url").GetString();
                    if (url == null || !url.StartsWith("https://github.com/openai/tunnel-client/releases/download/", StringComparison.Ordinal)) continue;
                    if (name == want) zip = url;
                    else if (name == "SHA256SUMS.txt") sums = url;
                }
                return zip != null && sums != null ? (tag, zip, sums) : null;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Takes the zip only when its SHA-256 is the one the sums name, and
        /// writes its tunnel-client.exe into <paramref name="folder"/>. The
        /// exe's path, or null with why in <paramref name="why"/>.
        /// </summary>
        internal static string Install(byte[] zip, string sums, string asset, string folder, out string why)
        {
            string want = SumOf(sums, asset);
            if (want == null) { why = "the release's sums do not name " + asset; return null; }
            string have = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(have), Encoding.ASCII.GetBytes(want)))
            {
                why = "the download's SHA-256 is not the one its release names";
                return null;
            }
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')), ExeName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) { why = "the download holds no " + ExeName; return null; }
            Directory.CreateDirectory(folder);
            string exe = Path.Combine(folder, ExeName);
            using (var source = entry.Open())
            using (var target = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            why = null;
            return exe;
        }

        /// <summary>The folder tunnel-client is kept in, in the user's temp
        /// folder.</summary>
        internal static string Folder() => Path.Combine(Path.GetTempPath(), "PadForge", "TunnelClient");

        /// <summary>A copy downloaded before, the newest, or null.</summary>
        internal static string Kept()
        {
            string folder = Folder();
            if (folder == null) return null;
            try
            {
                return Directory.EnumerateDirectories(folder).Select(d => Path.Combine(d, ExeName)).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Fetches the latest release for this PC, checks it and keeps it in
        /// <see cref="Folder"/>: the exe's path, or null with why. Every
        /// request goes through the outbound gate, so the test host fetches
        /// nothing.
        /// </summary>
        internal static async Task<(string Exe, string Why)> DownloadAsync(CancellationToken cancel)
        {
            string folder = Folder();
            using var http = PadForge.Engine.Common.OutboundNetworkGate.NewHttpClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PadForge", BuildIdentity.VersionText(BuildIdentity.Version)));
            try
            {
                string json = await http.GetStringAsync(LatestRelease, cancel).ConfigureAwait(false);
                var release = Release(json, RuntimeInformation.OSArchitecture);
                if (release is not { } r) return (null, "the latest release has no Windows zip for this PC");
                string sums = await http.GetStringAsync(r.Sums, cancel).ConfigureAwait(false);
                byte[] zip = await http.GetByteArrayAsync(r.Zip, cancel).ConfigureAwait(false);
                string exe = Install(zip, sums, AssetName(r.Tag, RuntimeInformation.OSArchitecture), Path.Combine(folder, r.Tag), out string why);
                return (exe, why);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return (null, error.Message);
            }
        }
    }
}
