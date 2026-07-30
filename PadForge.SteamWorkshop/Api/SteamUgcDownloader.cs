using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Downloads a Steam Input VDF blob from its CDN <c>file_url</c>. Enforces a 10 MB cap,
    /// validates the byte count against the expected size when provided, and rejects HTML
    /// error pages that Steam sometimes serves in place of the file. The constructor throws
    /// if the opt-in gate is off.
    /// </summary>
    public sealed class SteamUgcDownloader
    {
        /// <summary>Hard cap on a downloaded config (matches the VDF parser's input cap).</summary>
        public const long MaxVdfBytes = 10L * 1024 * 1024;

        public SteamUgcDownloader(ISteamWorkshopGate gate)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
        }

        /// <summary>
        /// Downloads and returns the VDF text. <paramref name="expectedSize"/> (from the
        /// published-file metadata) is validated when greater than zero; pass 0 to skip.
        /// </summary>
        public async Task<string> DownloadVdfAsync(string fileUrl, long expectedSize, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(fileUrl))
                throw new ArgumentException("file_url is empty (legacy non-downloadable config).", nameof(fileUrl));

            using var response = await SteamHttp.Client
                .GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxVdfBytes)
                throw new SteamWorkshopException($"Config too large ({declared.Value} bytes); rejected.");

            var bytes = await ReadCappedAsync(response.Content, MaxVdfBytes, ct).ConfigureAwait(false);

            if (expectedSize > 0 && bytes.Length != expectedSize)
                throw new SteamWorkshopException(
                    $"Downloaded config size {bytes.Length} does not match the expected {expectedSize} bytes.");

            var text = DecodeUtf8(bytes);
            if (LooksLikeHtml(text))
                throw new SteamWorkshopException("Steam returned an error page, not a VDF.");

            return text;
        }

        /// <summary>Whole-body read budget. HttpClient.Timeout stops applying
        /// once the headers are in under ResponseHeadersRead
        /// (dotnet/runtime#36822), so a stalled CDN body needs its own bound.</summary>
        private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(30);

        private static async Task<byte[]> ReadCappedAsync(HttpContent content, long cap, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(BodyReadTimeout);
            try
            {
                await using var stream = await content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > cap)
                        throw new SteamWorkshopException($"Config exceeds the {cap}-byte limit; rejected.");
                }
                return buffer.ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SteamWorkshopException("The config download stalled and timed out.");
            }
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            // Strip a UTF-8 BOM if present so downstream detection sees the first real char.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool LooksLikeHtml(string text)
        {
            var i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '<') return false;

            var head = text.Substring(i, Math.Min(64, text.Length - i));
            return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                   || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                   || head.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
                   || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                   || head.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
        }
    }
}
