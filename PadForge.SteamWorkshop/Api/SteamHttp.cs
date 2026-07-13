using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Shared HTTP plumbing for the anonymous Steam REST clients: one long-lived
    /// <see cref="HttpClient"/> (avoids socket exhaustion) with a 15 s timeout and a
    /// <c>PadForge/{version}</c> User-Agent, plus the JSON options the DTOs deserialize with.
    /// </summary>
    internal static class SteamHttp
    {
        private static readonly Lazy<HttpClient> LazyClient = new Lazy<HttpClient>(CreateClient);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static HttpClient Client => LazyClient.Value;

        private static HttpClient CreateClient()
        {
            var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            var version = typeof(SteamHttp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PadForge", version));
            return http;
        }
    }
}
