using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop.Api.Dto;

namespace PadForge.SteamWorkshop.Api
{
    /// <summary>
    /// Anonymous HTTPS to <c>steamcommunity.com</c> for a creator's public persona name and
    /// avatar (the <c>?xml=1</c> profile document). The constructor throws if the opt-in gate
    /// is off.
    /// </summary>
    public sealed class SteamCommunityClient
    {
        public SteamCommunityClient(ISteamWorkshopGate gate)
        {
            SteamWorkshopGuard.EnsureEnabled(gate);
        }

        /// <summary>Fetches the public profile for a 64-bit Steam id.</summary>
        public async Task<SteamPersona> GetPersonaAsync(ulong steamId, CancellationToken ct = default)
        {
            var url = $"https://steamcommunity.com/profiles/{steamId}?xml=1";
            using var response = await SteamHttp.Client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return SteamPersona.FromProfileXml(steamId, xml);
        }
    }
}
