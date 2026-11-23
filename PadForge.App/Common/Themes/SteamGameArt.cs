using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PadForge.SteamWorkshop;
using PadForge.SteamWorkshop.Api;
using PadForge.SteamWorkshop.Api.Dto;
using PadForge.SteamWorkshop.Cache;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// A Steam game's library art for a theme (#591): the wide picture
    /// Steam shows behind a game's library page and the game's logo.
    ///
    /// It goes through the clients Get Profiles' Community tab already
    /// uses: the store search finds a game, <see cref="SteamArtworkClient"/>
    /// fetches <c>library_hero.jpg</c> (or the store header when a game has
    /// none) and <c>logo.png</c>, and the art cache keeps both. Every
    /// fetch stands behind the same switch that tab needs: with it off the
    /// search and the fetch do nothing, and only art already in the cache
    /// is read, which asks nothing of the network.
    /// </summary>
    internal sealed class SteamGameArt
    {
        internal const string HeroFile = "library_hero.jpg", HeaderFile = "header.jpg", LogoFile = "logo.png";

        private readonly Func<bool> _lookupOn;
        private readonly SteamWorkshopCache _cache;
        private SteamStoreClient _store;
        private SteamArtworkClient _art;

        /// <param name="lookupOn">The Community tab's switch, read at each call.</param>
        internal SteamGameArt(Func<bool> lookupOn, SteamWorkshopCache cache = null)
        {
            _lookupOn = lookupOn ?? throw new ArgumentNullException(nameof(lookupOn));
            _cache = cache ?? new SteamWorkshopCache();
        }

        /// <summary>Whether PadForge may ask Steam.</summary>
        internal bool IsOn => _lookupOn();

        private void EnsureClients()
        {
            if (_store != null) return;
            var gate = new DelegateSteamWorkshopGate(_lookupOn);
            _store = new SteamStoreClient(gate);
            _art = new SteamArtworkClient(gate, _cache);
        }

        /// <summary>The games whose names match, as the store ranks them.
        /// Empty with the switch off.</summary>
        internal async Task<IReadOnlyList<(int AppId, string Name)>> SearchAsync(string query, CancellationToken ct)
        {
            if (!IsOn || string.IsNullOrWhiteSpace(query)) return Array.Empty<(int, string)>();
            EnsureClients();
            string key = "storesearch_" + query.Trim().ToLowerInvariant();
            StoreSearchResponse response = await Task.Run(() =>
                _cache.TryGetJson<StoreSearchResponse>(CacheCategory.Games, key, CacheTtls.Games, out var hit) ? hit : null, ct).ConfigureAwait(false);
            if (response == null)
            {
                response = await _store.SearchAsync(query.Trim(), ct).ConfigureAwait(false);
                if (response != null)
                    await Task.Run(() => _cache.PutJson(CacheCategory.Games, key, response), CancellationToken.None).ConfigureAwait(false);
            }
            return (response?.Items ?? new List<StoreSearchItem>())
                .Where(i => i != null && i.Id > 0 && !string.IsNullOrWhiteSpace(i.Name))
                .Select(i => (i.Id, i.Name.Trim()))
                .ToList();
        }

        /// <summary>The game's wide picture and its logo, each null when
        /// it is not to be had: from the cache when the cache holds it,
        /// else from Steam when the switch is on. Unreached is true when
        /// there is no wide picture because Steam could not be reached for
        /// it, and false when Steam answered that the game has none. Never
        /// throws for a network failure.</summary>
        internal async Task<(byte[] Hero, byte[] Logo, bool Unreached)> FetchAsync(int appId, CancellationToken ct)
        {
            if (appId <= 0) return (null, null, false);
            var (hero, unreached) = await OneAsync(appId, HeroFile, true, ct).ConfigureAwait(false);
            var (logo, _) = await OneAsync(appId, LogoFile, false, ct).ConfigureAwait(false);
            return (hero, logo, hero == null && unreached);
        }

        private async Task<(byte[] Data, bool Unreached)> OneAsync(int appId, string file, bool hero, CancellationToken ct)
        {
            bool unreached = false;
            if (IsOn)
            {
                try
                {
                    EnsureClients();
                    ArtworkResult result = hero
                        ? await _art.GetHeroAsync(appId, ct).ConfigureAwait(false)
                        : await _art.GetFileAsync(appId, file, ct).ConfigureAwait(false);
                    if (result?.Data != null) return (result.Data, false);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or InvalidOperationException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // No network, a server error, or the switch went off between the check and the call: the cache below.
                    unreached = true;
                }
            }
            // What the cache already holds, however old: it asks nothing of the
            // network. A game with no wide picture was kept as its store header.
            byte[] kept = await Task.Run(() =>
            {
                if (_cache.TryGetBytesStaleOk(CacheCategory.Art, appId + "_" + file, CacheTtls.Art, out var cached, out _)) return cached;
                if (hero && _cache.TryGetBytesStaleOk(CacheCategory.Art, appId + "_" + HeaderFile, CacheTtls.Art, out var header, out _)) return header;
                return null;
            }, ct).ConfigureAwait(false);
            return (kept, kept == null && unreached);
        }
    }
}
