using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Api.Dto;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Anonymous HTTPS POST to
    /// <c>ISteamRemoteStorage/GetPublishedFileDetails/v1/</c> for per-file Workshop metadata
    /// (title, description, creator, file_url, votes, subscriptions, time_updated, tags).
    /// The constructor throws if the opt-in gate is off.
    /// </summary>
    public sealed class SteamRemoteStorageClient
    {
        private const string Endpoint =
            "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

        public SteamRemoteStorageClient(ISteamWorkshopGate gate)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
        }

        /// <summary>Fetches details for a single published file, or null if Steam returns none.</summary>
        public async Task<PublishedFileDetails> GetDetailsAsync(long fileId, CancellationToken ct = default)
        {
            var all = await GetDetailsAsync(new[] { fileId }, ct).ConfigureAwait(false);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>Fetches details for several published files in one request.</summary>
        public async Task<IReadOnlyList<PublishedFileDetails>> GetDetailsAsync(
            IReadOnlyList<long> fileIds, CancellationToken ct = default)
        {
            if (fileIds == null) throw new ArgumentNullException(nameof(fileIds));

            var form = new List<KeyValuePair<string, string>>(fileIds.Count + 1)
            {
                new KeyValuePair<string, string>("itemcount", fileIds.Count.ToString(CultureInfo.InvariantCulture)),
            };
            for (var i = 0; i < fileIds.Count; i++)
            {
                form.Add(new KeyValuePair<string, string>(
                    $"publishedfileids[{i}]",
                    fileIds[i].ToString(CultureInfo.InvariantCulture)));
            }

            using var content = new FormUrlEncodedContent(form);
            using var response = await SteamHttp.Client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<GetPublishedFileDetailsResponse>(
                stream, SteamHttp.JsonOptions, ct).ConfigureAwait(false);

            return parsed?.Response?.PublishedFileDetails ?? new List<PublishedFileDetails>();
        }
    }
}
