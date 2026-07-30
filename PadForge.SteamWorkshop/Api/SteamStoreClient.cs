using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Api.Dto;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Anonymous HTTPS to <c>store.steampowered.com</c> for game search (name to app id).
    /// No API key. The constructor throws if the opt-in gate is off.
    /// </summary>
    public sealed class SteamStoreClient
    {
        public SteamStoreClient(ISteamWorkshopGate gate)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
        }

        /// <summary>Searches the store for games matching <paramref name="query"/>.</summary>
        public async Task<StoreSearchResponse> SearchAsync(string query, CancellationToken ct = default)
        {
            var url = "https://store.steampowered.com/api/storesearch/?term=" +
                      Uri.EscapeDataString(query ?? string.Empty) + "&l=english&cc=US";
            return await GetJsonAsync<StoreSearchResponse>(url, ct).ConfigureAwait(false)
                   ?? new StoreSearchResponse();
        }

        private static async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
        {
            using var response = await SteamHttp.Client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, SteamHttp.JsonOptions, ct).ConfigureAwait(false);
        }
    }
}
