using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;
using PadForge.SteamWorkshop;
using PadForge.SteamWorkshop.Api;
using PadForge.SteamWorkshop.Api.Dto;
using PadForge.SteamWorkshop.Cache;
using PadForge.SteamWorkshop.Local;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using PadForge.ViewModels;
using EPublishedFileQueryType = SteamKit2.EPublishedFileQueryType;
using SkPublishedFileDetails = SteamKit2.Internal.PublishedFileDetails;
using SkQueryFilesResponse = SteamKit2.Internal.CPublishedFile_QueryFiles_Response;

namespace PadForge.Views
{
    /// <summary>
    /// Browse Community Configs (#9): the Steam Workshop config browser per
    /// the approved design artifact. Three states carry the flow: cold-forge
    /// opt-in (gate off), game search (portrait shelf), and the game room
    /// (hero art under the steel scrim, config list, translation manifest).
    /// All network work is async, cache-first, and cancelled on re-entry;
    /// nothing here ever runs while the opt-in gate is off.
    /// </summary>
    public partial class WorkshopBrowseDialog : Wpf.Ui.Controls.FluentWindow
    {
        private enum WsState { Cold, Search, Browse }

        private readonly SettingsViewModel _settings;

        private SteamWorkshopCache _cache;
        private SteamStoreClient _store;
        private SteamWorkshopClient _workshop;
        private SteamArtworkClient _art;
        private SteamUgcDownloader _ugc;
        private SteamCommunityClient _community;

        private readonly DispatcherTimer _searchDebounce;
        private CancellationTokenSource _searchCts;
        private CancellationTokenSource _gameCts;
        private CancellationTokenSource _manifestCts;

        private string _lastQuery = string.Empty;
        private WorkshopGameItem _selectedGame;
        private string _activeTag;
        private WorkshopConfigItem _selectedConfig;
        private SteamInputConfig _parsedConfig;
        private WorkshopTranslationOutcome _outcome;
        private int _heroSwapVersion;

        // ── Config-list paging (infinite scroll) ──

        /// <summary>QueryFiles page size for the config list. A big game's
        /// catalog runs six figures (Skyrim SE: 155k+), so the list streams
        /// page by page as the user nears the bottom instead of stopping at
        /// the first response.</summary>
        private const int ConfigsPageSize = 30;

        /// <summary>Bound on consecutive pages the ban/legacy filters ate
        /// whole within a single fill. A run this long reads as the end of
        /// the importable results; without it a mostly-legacy catalog could
        /// keep a fill fetching indefinitely.</summary>
        private const int ConfigsMaxSilentPages = 10;

        /// <summary>Next QueryFiles page to request (1-based).</summary>
        private int _nextConfigsPage = 1;
        private bool _configsExhausted;
        private bool _configsFetchBusy;
        private DateTime _configsRetryAtUtc;
        private readonly HashSet<ulong> _seenConfigIds = new();

        /// <summary>Avatar images live on avatars CDN hosts (not the appid
        /// store CDN the artwork client covers), so they get their own
        /// slim fetch, cached under the art budget.</summary>
        private static readonly Lazy<HttpClient> AvatarHttp = new Lazy<HttpClient>(() =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var version = typeof(WorkshopBrowseDialog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PadForge", version));
            return http;
        });

        /// <summary>Response cap for avatar fetches, mirroring
        /// SteamArtworkClient.MaxArtBytes.</summary>
        private const long MaxAvatarBytes = 16L * 1024 * 1024;

        /// <summary>Whole-body read budget, mirroring SteamArtworkClient:
        /// HttpClient.Timeout stops applying once the headers are in under
        /// ResponseHeadersRead (dotnet/runtime#36822), so a stalled avatar
        /// body needs its own bound.</summary>
        private static readonly TimeSpan AvatarBodyReadTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Set by MainWindow: registers the translated profile through
        /// the same path as the .pfprofile Import button and returns the
        /// deduped display name. Second arg loads it as the active profile.</summary>
        internal Func<Services.ProfileData, bool, string> ImportSink { get; set; }

        /// <summary>Non-null after a successful import; MainWindow reads these
        /// for the status line once the dialog closes.</summary>
        internal string ImportedProfileName { get; private set; }
        internal int ImportedClean { get; private set; }
        internal int ImportedPartial { get; private set; }
        internal int ImportedSkipped { get; private set; }

        public ObservableCollection<WorkshopGameItem> Games { get; } = new();
        public ObservableCollection<WorkshopConfigItem> Configs { get; } = new();
        public ObservableCollection<WorkshopTagChipItem> TagChips { get; } = new();
        public ObservableCollection<WorkshopPresetChipItem> PresetChips { get; } = new();
        public ObservableCollection<object> ManifestRows { get; } = new();

        public WorkshopBrowseDialog(SettingsViewModel settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            InitializeComponent();

            ShelfList.ItemsSource = Games;
            ConfigList.ItemsSource = Configs;
            TagChipList.ItemsSource = TagChips;
            PresetChipList.ItemsSource = PresetChips;
            ManifestRowsList.ItemsSource = ManifestRows;

            BuildScrims();

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _searchDebounce.Tick += (s, e) =>
            {
                _searchDebounce.Stop();
                _ = RunSearchAsync(SearchBox.Text);
            };

            Loaded += (s, e) => ApplyGateState();
            Closing += (s, e) =>
            {
                _searchDebounce.Stop();
                _searchCts?.Cancel();
                _gameCts?.Cancel();
                _manifestCts?.Cancel();
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // The anonymous CM session lives for the dialog session only
            // (recipe: connect on first search, disconnect on close).
            var client = _workshop;
            _workshop = null;
            if (client != null)
                _ = client.DisposeAsync();
        }

        // ─────────────────────────────────────────────
        //  States
        // ─────────────────────────────────────────────

        private void ApplyGateState()
        {
            if (_settings.EnableCommunityConfigLookup)
            {
                EnsureClients();
                LightTitleFlame();
                SetState(WsState.Search);
                SearchBox.Focus();
            }
            else
            {
                SetState(WsState.Cold);
            }
        }

        private void SetState(WsState state)
        {
            ColdPanel.Visibility = state == WsState.Cold ? Visibility.Visible : Visibility.Collapsed;
            SearchPanel.Visibility = state == WsState.Search ? Visibility.Visible : Visibility.Collapsed;
            BrowsePanel.Visibility = state == WsState.Browse ? Visibility.Visible : Visibility.Collapsed;
            if (state != WsState.Browse)
                ArtLayer.Visibility = Visibility.Collapsed;
        }

        private void EnsureClients()
        {
            if (_store != null) return;
            _cache ??= new SteamWorkshopCache();
            var gate = new DelegateSteamWorkshopGate(() => _settings.EnableCommunityConfigLookup);
            _store = new SteamStoreClient(gate);
            _workshop = new SteamWorkshopClient(gate, _cache);
            _art = new SteamArtworkClient(gate, _cache);
            _ugc = new SteamUgcDownloader(gate);
            _community = new SteamCommunityClient(gate);
        }

        private void LightTitleFlame()
        {
            TitleFlame.Fill = TryFindResource("EmberBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C));
            TitleFlame.Stroke = null;
            var glow = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x6B, 0x2C), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 };
            glow.Freeze();
            TitleFlame.Effect = glow;
        }

        /// <summary>The cold-forge enable action flips the same persisted
        /// setting the Settings card toggles (MainWindow's allowlist marks
        /// the settings document dirty on this property).</summary>
        private void EnableCommunityConfigs_Click(object sender, RoutedEventArgs e)
        {
            _settings.EnableCommunityConfigLookup = true;
            ApplyGateState();
        }

        private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();

        // ─────────────────────────────────────────────
        //  Game search (debounced, cache-first)
        // ─────────────────────────────────────────────

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Games.Count > 0)
            {
                _ = OpenGameAsync(ShelfList.SelectedItem as WorkshopGameItem ?? Games[0]);
                e.Handled = true;
            }
            else if (e.Key == Key.Down && Games.Count > 0)
            {
                ShelfList.SelectedIndex = Math.Max(ShelfList.SelectedIndex, 0);
                (ShelfList.ItemContainerGenerator.ContainerFromIndex(ShelfList.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        }

        private void ShelfList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ShelfList.SelectedItem is WorkshopGameItem g)
            {
                _ = OpenGameAsync(g);
                e.Handled = true;
            }
        }

        private void ShelfList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Only a click that landed on a tile opens it; scrollbar clicks
            // bubble through this preview handler too.
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not ListBoxItem && dep is not ListBox)
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem { DataContext: WorkshopGameItem g })
                _ = OpenGameAsync(g);
        }

        private void SearchRetry_Click(object sender, RoutedEventArgs e) => _ = RunSearchAsync(_lastQuery);

        private async Task RunSearchAsync(string rawQuery)
        {
            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            var ct = cts.Token;

            string query = (rawQuery ?? string.Empty).Trim();
            Games.Clear();
            GamesCountText.Text = string.Empty;
            SearchErrorPanel.Visibility = Visibility.Collapsed;
            SearchHintText.Visibility = Visibility.Collapsed;
            if (query.Length < 2)
            {
                SearchLoadingPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _lastQuery = query;
            SearchLoadingPanel.Visibility = Visibility.Visible;
            try
            {
                var resp = await StoreSearchCachedAsync(query, ct);
                if (ct.IsCancellationRequested) return;

                var items = (resp?.Items ?? new List<StoreSearchItem>())
                    .Where(i => i.Id > 0 && (string.IsNullOrEmpty(i.Type) || i.Type == "app" || i.Type == "game"))
                    .Take(12)
                    .ToList();
                foreach (var it in items)
                {
                    Games.Add(new WorkshopGameItem
                    {
                        AppId = it.Id,
                        Name = it.Name ?? it.Id.ToString(CultureInfo.InvariantCulture),
                        Initial = FirstLetter(it.Name),
                        ControllerSupport = it.ControllerSupport,
                    });
                }
                GamesCountText.Text = string.Format(Strings.Instance.Workshop_GamesCount_Format, Games.Count);
                if (Games.Count == 0)
                {
                    SearchHintText.Text = Strings.Instance.Workshop_NoGamesFound;
                    SearchHintText.Visibility = Visibility.Visible;
                }

                // Art and per-game config counts lazy-fill after the shelf
                // renders (design open question 3).
                foreach (var g in Games)
                    _ = FillGamePortraitAsync(g, ct);
                _ = FillConfigCountsAsync(Games.ToList(), ct);
            }
            // Filtered on OUR token: HttpClient's 15 s timeout also raises
            // TaskCanceledException (an OperationCanceledException), and that
            // one must reach the error state, not vanish.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                SearchErrorPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    SearchLoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<StoreSearchResponse> StoreSearchCachedAsync(string query, CancellationToken ct)
        {
            string key = "storesearch_" + query.ToLowerInvariant();
            var cached = await Task.Run(() =>
                _cache.TryGetJson<StoreSearchResponse>(CacheCategory.Games, key, CacheTtls.Games, out var v) ? v : null, ct);
            if (cached != null) return cached;

            var resp = await _store.SearchAsync(query, ct);
            if (resp != null)
                await Task.Run(() => _cache.PutJson(CacheCategory.Games, key, resp), CancellationToken.None);
            return resp;
        }

        private async Task FillGamePortraitAsync(WorkshopGameItem g, CancellationToken ct)
        {
            try
            {
                var art = await _art.GetPortraitAsync(g.AppId, ct);
                if (art == null || ct.IsCancellationRequested) return;
                var img = await Task.Run(() => DecodeBitmap(art.Data, 240), ct);
                if (img == null || ct.IsCancellationRequested) return;
                g.IsLetterbox = !string.Equals(art.File, "library_600x900.jpg", StringComparison.OrdinalIgnoreCase);
                g.Portrait = img;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // Art is ambience; the steel initial tile stands in.
            }
        }

        private async Task FillConfigCountsAsync(List<WorkshopGameItem> games, CancellationToken ct)
        {
            foreach (var g in games)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var resp = await _workshop.SearchAsync(g.AppId, EPublishedFileQueryType.RankedByVote, 1, 1, null, ct);
                    g.ConfigCount = (int)(resp?.total ?? 0);
                    g.CountText = string.Format(Strings.Instance.Workshop_ConfigsCount_Format, g.ConfigCount);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Counts are scent, not load-bearing. One failure means
                    // the CM session isn't reachable; stop instead of paying
                    // the timeout once per shelf tile.
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Game room (hero art + config list)
        // ─────────────────────────────────────────────

        private void BackToSearch_Click(object sender, MouseButtonEventArgs e)
        {
            // Search state survives untouched behind the game room
            // (design: back returns without losing state).
            _gameCts?.Cancel();
            _manifestCts?.Cancel();
            SetState(WsState.Search);
            SearchBox.Focus();
        }

        private async Task OpenGameAsync(WorkshopGameItem g)
        {
            if (g == null) return;
            _selectedGame = g;
            _activeTag = null;
            SetState(WsState.Browse);

            GameNameText.Text = g.Name;
            CapsuleInitial.Text = g.Initial;
            CapsuleImage.Source = null;
            UpdateGameMeta(g.ConfigCount);
            TagChips.Clear();
            ClearManifest();

            _ = SwapHeroAsync(g.AppId);
            _ = FillCapsuleAsync(g);
            await LoadConfigsAsync(g, null);
        }

        private async Task FillCapsuleAsync(WorkshopGameItem g)
        {
            try
            {
                var art = await _art.GetPortraitAsync(g.AppId, CancellationToken.None);
                if (art == null || _selectedGame != g) return;
                var img = await Task.Run(() => DecodeBitmap(art.Data, 240));
                if (img == null || _selectedGame != g) return;
                CapsuleImage.Stretch = string.Equals(art.File, "library_600x900.jpg", StringComparison.OrdinalIgnoreCase)
                    ? Stretch.UniformToFill : Stretch.Uniform;
                CapsuleImage.Source = img;
            }
            catch (Exception)
            {
                // Steel tile + initial stands in.
            }
        }

        /// <summary>Hero backdrop swap: 240 ms crossfade through steel (fade
        /// to the ground, swap, fade back; never art-to-art). Honors the
        /// Windows "show animations" setting with an instant cut.</summary>
        private async Task SwapHeroAsync(int appId)
        {
            int version = ++_heroSwapVersion;
            ImageSource src = null;
            bool blurred = false;
            try
            {
                var art = await _art.GetHeroAsync(appId, CancellationToken.None);
                if (art != null)
                {
                    blurred = !string.Equals(art.File, "library_hero.jpg", StringComparison.OrdinalIgnoreCase);
                    src = await Task.Run(() => BuildHeroBitmap(art.Data));
                }
            }
            catch (Exception)
            {
                // Fall through to the flat-steel floor of the chain.
            }
            if (version != _heroSwapVersion || BrowsePanel.Visibility != Visibility.Visible) return;

            void Apply()
            {
                HeroImage.Source = src;
                // Fallback chain: library_hero → header blurred ×8 → flat steel.
                HeroImage.Effect = src != null && blurred ? new BlurEffect { Radius = 8 } : null;
                ArtLayer.Visibility = src != null ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!SystemParameters.ClientAreaAnimation)
            {
                HeroImage.BeginAnimation(OpacityProperty, null);
                HeroImage.Opacity = 1;
                Apply();
                return;
            }

            if (ArtLayer.Visibility != Visibility.Visible || HeroImage.Source == null)
            {
                Apply();
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
                HeroImage.BeginAnimation(OpacityProperty, fadeIn);
                return;
            }

            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fadeOut.Completed += (s, e) =>
            {
                if (version != _heroSwapVersion) return;
                Apply();
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
                HeroImage.BeginAnimation(OpacityProperty, fadeIn);
            };
            HeroImage.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void UpdateGameMeta(int? configTotal)
        {
            var cold = TryFindResource("ColdBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x58, 0xB6, 0xE4));
            GameMetaText.Inlines.Clear();
            GameMetaText.Inlines.Add(new Run("appid "));
            GameMetaText.Inlines.Add(new Run(_selectedGame.AppId.ToString(CultureInfo.InvariantCulture)) { Foreground = cold });
            if (configTotal.HasValue)
            {
                GameMetaText.Inlines.Add(new Run(" · "));
                GameMetaText.Inlines.Add(new Run(configTotal.Value.ToString(CultureInfo.InvariantCulture)) { Foreground = cold });
                GameMetaText.Inlines.Add(new Run(" " + Strings.Instance.Workshop_CommunityConfigsSuffix));
            }
            string support = _selectedGame.ControllerSupport;
            if (support == "full")
                GameMetaText.Inlines.Add(new Run(" · " + Strings.Instance.Workshop_ControllerSupportFull));
            else if (support == "partial")
                GameMetaText.Inlines.Add(new Run(" · " + Strings.Instance.Workshop_ControllerSupportPartial));
        }

        private void ConfigsRetry_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedGame != null)
                _ = LoadConfigsAsync(_selectedGame, _activeTag);
        }

        private async Task LoadConfigsAsync(WorkshopGameItem g, string requiredTag)
        {
            _gameCts?.Cancel();
            // Cancel the manifest/translate task too, exactly as BackToSearch
            // does. Configs.Clear() below deselects the current row, but a
            // download already in flight for it keeps its own token alive,
            // finishes, and restores _outcome plus the Save footer for a
            // config the active tag no longer lists. Save then imported it.
            _manifestCts?.Cancel();
            var cts = new CancellationTokenSource();
            _gameCts = cts;
            var ct = cts.Token;

            Configs.Clear();
            _seenConfigIds.Clear();
            _nextConfigsPage = 1;
            _configsExhausted = false;
            _configsRetryAtUtc = DateTime.MinValue;
            // Owned for the whole initial fill so a scroll event raised by
            // rows landing mid-load cannot start a second, overlapping page
            // fetch. A stale generation's finally skips the reset (its token
            // is cancelled), so this flag always reflects the live one.
            _configsFetchBusy = true;
            ConfigsEmptyPanel.Visibility = Visibility.Collapsed;
            ConfigsErrorPanel.Visibility = Visibility.Collapsed;
            ConfigsMorePanel.Visibility = Visibility.Collapsed;
            ConfigsLoadingPanel.Visibility = Visibility.Visible;
            ConfigsFoundText.Text = string.Empty;
            try
            {
                var resp = await FetchConfigsPageAsync(g, requiredTag, _nextConfigsPage, ct);
                if (ct.IsCancellationRequested) return;

                var details = resp?.publishedfiledetails ?? new List<SkPublishedFileDetails>();
                var rows = AppendConfigRows(details);
                AdvanceConfigsPaging(details);

                int total = (int)(resp?.total ?? 0);
                ConfigsFoundText.Text = string.Format(Strings.Instance.Workshop_Found_Format, total);
                g.ConfigCount = total;
                UpdateGameMeta(total);

                // Chips come from the unfiltered result's live tags and stay
                // put while a tag filter narrows the list.
                if (requiredTag == null)
                    BuildTagChips(details);

                // The legacy filter can eat most of page 1. Top up to a full
                // page of visible rows so the list is scrollable (scrolling
                // is what drives further paging) before calling the room
                // empty.
                if (Configs.Count < ConfigsPageSize && !_configsExhausted)
                    rows.AddRange(await FetchConfigRowsAsync(g, requiredTag, ConfigsPageSize - Configs.Count, ct));
                if (ct.IsCancellationRequested) return;

                if (Configs.Count == 0)
                {
                    ConfigsEmptyBody.Text = string.Format(Strings.Instance.Workshop_EmptyBody_Format, g.Name);
                    ConfigsEmptyPanel.Visibility = Visibility.Visible;
                }

                _ = FillPersonasAsync(rows, ct);
            }
            // See RunSearchAsync: only OUR cancellation is silent.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (SteamWorkshopException ex) when (!ct.IsCancellationRequested)
            {
                ConfigsErrorBody.Text = ex.Message;
                ConfigsErrorPanel.Visibility = Visibility.Visible;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                ConfigsErrorBody.Text = Strings.Instance.Workshop_ErrorBody;
                ConfigsErrorPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    _configsFetchBusy = false;
                    ConfigsLoadingPanel.Visibility = Visibility.Collapsed;
                }
            }
        }

        /// <summary>One QueryFiles page for the game room: rating order, the
        /// requested tag filter, the shared page size.</summary>
        private Task<SkQueryFilesResponse> FetchConfigsPageAsync(
            WorkshopGameItem g, string requiredTag, int page, CancellationToken ct)
        {
            var tags = requiredTag == null ? null : new[] { requiredTag };
            return _workshop.SearchAsync(g.AppId, EPublishedFileQueryType.RankedByVote, page, ConfigsPageSize, tags, ct);
        }

        /// <summary>Appends one page's visible rows: ban and legacy filters,
        /// plus cross-page dedup (rank order can shift between page fetches,
        /// and a shifted item must not land twice). Returns what was added.</summary>
        private List<WorkshopConfigItem> AppendConfigRows(List<SkPublishedFileDetails> details)
        {
            bool showLegacy = _settings.ShowLegacyWorkshopConfigs;
            var rows = new List<WorkshopConfigItem>();
            foreach (var d in details)
            {
                if (d.banned) continue;
                if (!showLegacy && string.IsNullOrEmpty(d.file_url)) continue;
                if (!_seenConfigIds.Add(d.publishedfileid)) continue;
                rows.Add(BuildConfigItem(d));
            }
            foreach (var row in rows)
                Configs.Add(row);
            return rows;
        }

        /// <summary>A short page is Steam's end-of-results signal.</summary>
        private void AdvanceConfigsPaging(List<SkPublishedFileDetails> details)
        {
            _nextConfigsPage++;
            if (details.Count < ConfigsPageSize)
                _configsExhausted = true;
        }

        /// <summary>Pages forward until at least <paramref name="minRows"/>
        /// visible rows land, the results run out, or the silent-page bound
        /// trips. Filtered-out pages keep the loop going (never the caller's
        /// problem): without that, a legacy-heavy stretch would strand the
        /// scroll at a bottom that never grows. Returns every row appended.</summary>
        private async Task<List<WorkshopConfigItem>> FetchConfigRowsAsync(
            WorkshopGameItem g, string requiredTag, int minRows, CancellationToken ct)
        {
            var added = new List<WorkshopConfigItem>();
            int silentPages = 0;
            while (!_configsExhausted && added.Count < minRows && !ct.IsCancellationRequested)
            {
                var resp = await FetchConfigsPageAsync(g, requiredTag, _nextConfigsPage, ct);
                if (ct.IsCancellationRequested) break;
                var details = resp?.publishedfiledetails ?? new List<SkPublishedFileDetails>();
                int before = added.Count;
                added.AddRange(AppendConfigRows(details));
                AdvanceConfigsPaging(details);
                silentPages = added.Count > before ? 0 : silentPages + 1;
                if (silentPages >= ConfigsMaxSilentPages)
                    _configsExhausted = true;
            }
            return added;
        }

        /// <summary>Infinite scroll: within one viewport of the bottom, the
        /// next page streams in (the QueryFiles API pages; page 1 alone
        /// showed 30 of Skyrim's 155k configs). The distance math reads the
        /// same in both scroll units (items under the ListBox's logical
        /// scrolling, pixels otherwise). The Configs guard keeps the
        /// loading/empty/error states inert.</summary>
        private void ConfigList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Configs.Count == 0 || _configsExhausted || _configsFetchBusy) return;
            if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
            if (e.ExtentHeight - e.VerticalOffset - e.ViewportHeight > e.ViewportHeight) return;
            if (DateTime.UtcNow < _configsRetryAtUtc) return;
            _ = LoadMoreConfigsAsync();
        }

        /// <summary>Scroll-driven page fetch: single-flight, cancelled by a
        /// tag or game switch through _gameCts (the stale task then leaves
        /// the fresh generation's flags and panels alone).</summary>
        private async Task LoadMoreConfigsAsync()
        {
            if (_configsFetchBusy || _configsExhausted) return;
            var g = _selectedGame;
            var cts = _gameCts;
            if (g == null || cts == null || cts.IsCancellationRequested) return;
            var ct = cts.Token;

            _configsFetchBusy = true;
            ConfigsMorePanel.Visibility = Visibility.Visible;
            try
            {
                var rows = await FetchConfigRowsAsync(g, _activeTag, 1, ct);
                if (!ct.IsCancellationRequested)
                    _ = FillPersonasAsync(rows, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A mid-scroll failure keeps everything already on screen and
                // pauses paging briefly; the next gesture near the bottom
                // retries from the same page instead of blanking the list.
                _configsRetryAtUtc = DateTime.UtcNow.AddSeconds(5);
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    _configsFetchBusy = false;
                    ConfigsMorePanel.Visibility = Visibility.Collapsed;
                }
            }
        }

        private WorkshopConfigItem BuildConfigItem(SkPublishedFileDetails d)
        {
            uint up = d.vote_data?.votes_up ?? 0;
            uint down = d.vote_data?.votes_down ?? 0;
            bool hasVotes = up + down > 0;
            double ratio = hasVotes ? (double)up / (up + down) : 0;
            ulong subs = Math.Max(d.subscriptions, d.lifetime_subscriptions);

            var tags = (d.tags ?? new List<SkPublishedFileDetails.Tag>())
                .Where(t => t.tag != null && t.tag.StartsWith("controller_", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .Select(t => new WorkshopTagChipItem
                {
                    Tag = t.tag,
                    Label = (string.IsNullOrEmpty(t.display_name) ? PrettifyTag(t.tag) : t.display_name).ToUpperInvariant(),
                    IsCold = string.Equals(t.tag, "controller_neptune", StringComparison.OrdinalIgnoreCase),
                })
                .ToList();

            return new WorkshopConfigItem
            {
                FileId = d.publishedfileid,
                Title = string.IsNullOrWhiteSpace(d.title) ? d.publishedfileid.ToString(CultureInfo.InvariantCulture) : d.title,
                CreatorId = d.creator,
                FileUrl = d.file_url,
                FileSize = (long)d.file_size,
                TimeUpdated = d.time_updated,
                IsLegacy = string.IsNullOrEmpty(d.file_url),
                HasVotes = hasVotes,
                VoteBarWidth = Math.Round(ratio * 64.0),
                VotePercentText = hasVotes ? ((int)Math.Round(ratio * 100)).ToString(CultureInfo.InvariantCulture) + "%" : string.Empty,
                SubsText = string.Format(Strings.Instance.Workshop_Subs_Format, CompactCount(subs)),
                ByLine = string.Format(Strings.Instance.Workshop_ByLine_Format, "…", RelativeTime(d.time_updated)),
                Tags = tags,
            };
        }

        private void BuildTagChips(List<SkPublishedFileDetails> details)
        {
            TagChips.Clear();
            TagChips.Add(new WorkshopTagChipItem { Tag = null, Label = Strings.Instance.Workshop_TagAll, IsActive = true });
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in details)
            {
                if (d.tags == null) continue;
                foreach (var t in d.tags)
                {
                    if (t.tag == null || !t.tag.StartsWith("controller_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(t.tag)) continue;
                    TagChips.Add(new WorkshopTagChipItem
                    {
                        Tag = t.tag,
                        Label = string.IsNullOrEmpty(t.display_name) ? PrettifyTag(t.tag) : t.display_name,
                    });
                }
            }
        }

        private void TagChip_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WorkshopTagChipItem chip) return;
            if (string.Equals(chip.Tag, _activeTag, StringComparison.Ordinal)) return;
            _activeTag = chip.Tag;
            foreach (var c in TagChips)
                c.IsActive = string.Equals(c.Tag, _activeTag, StringComparison.Ordinal);
            if (_selectedGame != null)
                _ = LoadConfigsAsync(_selectedGame, _activeTag);
        }

        private async Task FillPersonasAsync(List<WorkshopConfigItem> rows, CancellationToken ct)
        {
            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) return;
                if (row.CreatorId == 0) continue;
                try
                {
                    var persona = await GetPersonaCachedAsync(row.CreatorId, ct);
                    if (ct.IsCancellationRequested) return;
                    if (persona?.PersonaName != null)
                    {
                        row.ByLine = string.Format(Strings.Instance.Workshop_ByLine_Format,
                            persona.PersonaName, RelativeTime(row.TimeUpdated));
                        row.AvatarInitials = Initials(persona.PersonaName);
                    }
                    if (!string.IsNullOrEmpty(persona?.AvatarMediumUrl))
                    {
                        var bytes = await GetAvatarCachedAsync(row.CreatorId, persona.AvatarMediumUrl, ct);
                        if (bytes != null && !ct.IsCancellationRequested)
                        {
                            var img = await Task.Run(() => DecodeBitmap(bytes, 68), ct);
                            if (img != null) row.Avatar = img;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Initials tile stands in (a per-creator timeout included);
                    // move on to the next creator.
                }
            }
        }

        private async Task<SteamPersona> GetPersonaCachedAsync(ulong steamId, CancellationToken ct)
        {
            string key = steamId.ToString(CultureInfo.InvariantCulture);
            var cached = await Task.Run(() =>
                _cache.TryGetJson<SteamPersona>(CacheCategory.Personas, key, CacheTtls.Personas, out var v) ? v : null, ct);
            if (cached != null) return cached;

            var persona = await _community.GetPersonaAsync(steamId, ct);
            if (persona != null)
                await Task.Run(() => _cache.PutJson(CacheCategory.Personas, key, persona), CancellationToken.None);
            return persona;
        }

        private async Task<byte[]> GetAvatarCachedAsync(ulong steamId, string url, CancellationToken ct)
        {
            string key = "avatar_" + steamId.ToString(CultureInfo.InvariantCulture);
            var cached = await Task.Run(() =>
                _cache.TryGetBytes(CacheCategory.Art, key, null, out var v) ? v : null, ct);
            if (cached != null) return cached;

            var bytes = await FetchAvatarAsync(url, ct);
            if (bytes != null)
                await Task.Run(() => _cache.PutBytes(CacheCategory.Art, key, bytes), CancellationToken.None);
            return bytes;
        }

        /// <summary>SteamArtworkClient.GetRawAsync's hardening applied to the
        /// avatar host: 16 MB response cap and an image-signature sniff. Null
        /// for anything oversized or non-image (the initials tile stands in).</summary>
        private static async Task<byte[]> FetchAvatarAsync(string url, CancellationToken ct)
        {
            using var response = await AvatarHttp.Value
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var declared = response.Content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxAvatarBytes)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AvatarBodyReadTimeout);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxAvatarBytes)
                    return null; // oversized: treat as unusable
            }

            var bytes = buffer.ToArray();
            return LooksLikeAvatarImage(bytes) ? bytes : null;
        }

        /// <summary>Mirrors SteamArtworkClient.LooksLikeImage.</summary>
        private static bool LooksLikeAvatarImage(byte[] b)
        {
            if (b.Length < 4) return false;

            // JPEG
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            // PNG
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            // GIF
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
            // WEBP (RIFF....WEBP)
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;

            return false;
        }

        // ─────────────────────────────────────────────
        //  Manifest (the dossier)
        // ─────────────────────────────────────────────

        private void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigList.SelectedItem is WorkshopConfigItem item)
                _ = SelectConfigAsync(item);
            else
                ClearManifest();
        }

        private void ManifestRetry_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedConfig != null)
                _ = SelectConfigAsync(_selectedConfig);
        }

        private void OpenInSteam_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedConfig == null) return;
            var url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" +
                      _selectedConfig.FileId.ToString(CultureInfo.InvariantCulture);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Browser launch is best-effort; the URL stays reachable from Steam.
            }
        }

        private void ClearManifest()
        {
            _selectedConfig = null;
            _parsedConfig = null;
            _outcome = null;
            ManifestHeaderRight.Text = string.Empty;
            ManifestRows.Clear();
            PresetChips.Clear();
            SetManifestPanel(ManifestIdlePanel);
        }

        /// <summary>Shows exactly one manifest fill; the footer rides only
        /// with the result.</summary>
        private void SetManifestPanel(FrameworkElement active)
        {
            ManifestIdlePanel.Visibility = active == ManifestIdlePanel ? Visibility.Visible : Visibility.Collapsed;
            ManifestLoadingPanel.Visibility = active == ManifestLoadingPanel ? Visibility.Visible : Visibility.Collapsed;
            ManifestLegacyPanel.Visibility = active == ManifestLegacyPanel ? Visibility.Visible : Visibility.Collapsed;
            ManifestErrorPanel.Visibility = active == ManifestErrorPanel ? Visibility.Visible : Visibility.Collapsed;
            ManifestResultPanel.Visibility = active == ManifestResultPanel ? Visibility.Visible : Visibility.Collapsed;
            ManifestFooter.Visibility = active == ManifestResultPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task SelectConfigAsync(WorkshopConfigItem item)
        {
            _manifestCts?.Cancel();
            var cts = new CancellationTokenSource();
            _manifestCts = cts;
            var ct = cts.Token;

            _selectedConfig = item;
            _parsedConfig = null;
            _outcome = null;
            ManifestHeaderRight.Text = item.Title;
            ManifestRows.Clear();
            PresetChips.Clear();

            SetManifestPanel(ManifestLoadingPanel);

            if (item.IsLegacy)
            {
                // No file_url to download. A local Steam subscription copy is the
                // only readable source, and the subscribe panel the honest miss.
                if (!await TryShowLocalConfigAsync(item, ct) && !ct.IsCancellationRequested)
                    SetManifestPanel(ManifestLegacyPanel);
                return;
            }

            try
            {
                string vdfKey = item.FileId.ToString(CultureInfo.InvariantCulture) + "_" +
                                item.TimeUpdated.ToString(CultureInfo.InvariantCulture);
                string vdf = await Task.Run(() =>
                    _cache.TryGetString(CacheCategory.Vdf, vdfKey, null, out var v) ? v : null, ct);
                if (vdf == null)
                {
                    vdf = await _ugc.DownloadVdfAsync(item.FileUrl, item.FileSize, ct);
                    var text = vdf;
                    await Task.Run(() => _cache.PutString(CacheCategory.Vdf, vdfKey, text), CancellationToken.None);
                }
                if (ct.IsCancellationRequested) return;

                var parsed = await Task.Run(() => SteamInputConfig.FromVdf(VdfParser.Parse(vdf)), ct);
                if (ct.IsCancellationRequested) return;
                _parsedConfig = parsed;

                BuildPresetChips(parsed);
                RunTranslationAndShow(item, parsed);
            }
            // See RunSearchAsync: only OUR cancellation is silent. A network
            // timeout falls through and maps to the design's error sentence.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A dead file_url (unreachable CDN, or Steam serving an error page
                // in place of the config) may still have a subscribed local copy.
                if (IsDeadUrlShaped(ex) && await TryShowLocalConfigAsync(item, ct))
                    return;
                if (!ct.IsCancellationRequested)
                    ShowManifestError(MapErrorMessage(ex));
            }
        }

        // ─────────────────────────────────────────────
        //  Legacy configs (local Steam folder fallback)
        // ─────────────────────────────────────────────

        /// <summary>Failures where the CDN copy is unreachable or gone, as opposed
        /// to real config content that failed to parse. SteamWorkshopException out
        /// of the downloader covers the served-an-error-page and wrong-size cases.</summary>
        private static bool IsDeadUrlShaped(Exception ex) =>
            ex is HttpRequestException or TaskCanceledException or SteamWorkshopException;

        /// <summary>
        /// #9 Phase D fallback: a Steam subscription materializes controller configs
        /// under steamapps/workshop/content/241100/{fileid} in every Steam library
        /// (as controller_configuration.vdf or {ugchandle}_legacy.bin, both text VDF),
        /// so a config the CDN cannot serve may still be readable from disk. Never
        /// throws. True when this method now owns the manifest panels (a fill, an
        /// honest parse error, or a cancelled attempt); false when no local copy
        /// exists and the caller should show its own miss state.
        /// </summary>
        private async Task<bool> TryShowLocalConfigAsync(WorkshopConfigItem item, CancellationToken ct)
        {
            string vdf;
            try
            {
                vdf = await Task.Run(() => LocalWorkshopConfigStore.ReadConfigText(item.FileId), ct);
            }
            catch (OperationCanceledException)
            {
                return true; // a newer selection owns the panels now
            }
            catch (Exception)
            {
                return false; // unreadable disk state counts as no local copy
            }
            if (ct.IsCancellationRequested) return true;
            if (vdf == null) return false;

            try
            {
                var parsed = await Task.Run(() => SteamInputConfig.FromVdf(VdfParser.Parse(vdf)), ct);
                if (ct.IsCancellationRequested) return true;
                _parsedConfig = parsed;

                BuildPresetChips(parsed);
                RunTranslationAndShow(item, parsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A local copy that exists but will not translate reports its own
                // honest error (version 2 rejection, personalization blob, ...).
                ShowManifestError(MapErrorMessage(ex));
            }
            return true;
        }

        private void BuildPresetChips(SteamInputConfig parsed)
        {
            PresetChips.Clear();
            foreach (var preset in parsed.Presets ?? (IReadOnlyList<SteamInputPreset>)Array.Empty<SteamInputPreset>())
            {
                PresetChips.Add(new WorkshopPresetChipItem
                {
                    Id = preset.Id,
                    // Same fallback shape the translator uses in its report
                    // paths, so chips and manifest groups agree.
                    Label = string.IsNullOrWhiteSpace(preset.Name)
                        ? "Preset " + preset.Id.ToString(CultureInfo.InvariantCulture)
                        : preset.Name,
                    IsIncluded = true,
                });
            }
        }

        private void PresetChip_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not WorkshopPresetChipItem chip) return;
            chip.IsIncluded = !chip.IsIncluded;
            if (_selectedConfig != null && _parsedConfig != null)
                RunTranslationAndShow(_selectedConfig, _parsedConfig);
        }

        private void RunTranslationAndShow(WorkshopConfigItem item, SteamInputConfig parsed)
        {
            try
            {
                var outcome = RunTranslator(item, parsed);
                _outcome = outcome;

                StatCleanNum.Text = outcome.Clean.ToString(CultureInfo.InvariantCulture);
                StatPartialNum.Text = outcome.Partial.ToString(CultureInfo.InvariantCulture);
                StatSkippedNum.Text = outcome.Skipped.ToString(CultureInfo.InvariantCulture);
                ManifestHeaderRight.Text = item.Title + " · " +
                    string.Format(Strings.Instance.Workshop_BindingsRead_Format, outcome.BindingsRead);

                ManifestRows.Clear();
                foreach (var row in outcome.Rows)
                    ManifestRows.Add(row);

                SetManifestPanel(ManifestResultPanel);
            }
            catch (Exception ex)
            {
                ShowManifestError(ex.Message);
            }
        }

        private void ShowManifestError(string message)
        {
            ManifestErrorBody.Text = message;
            SetManifestPanel(ManifestErrorPanel);
        }

        /// <summary>Recipe error-matrix wording: connectivity resolves to the
        /// design's one calm sentence; content rejections speak for
        /// themselves.</summary>
        private static string MapErrorMessage(Exception ex) => ex switch
        {
            SteamWorkshopException swx => swx.Message,
            VdfSyntaxException vdx => vdx.Message,
            SteamInputConfigException six => six.Message,
            HttpRequestException => Strings.Instance.Workshop_ErrorBody,
            TaskCanceledException => Strings.Instance.Workshop_ErrorBody,
            _ => ex.Message,
        };

        // ─────────────────────────────────────────────
        //  Import (Forge into Profile)
        // ─────────────────────────────────────────────

        private void SaveProfile_Click(object sender, RoutedEventArgs e) => CompleteImport(applyAfter: false);

        private void SaveAndApply_Click(object sender, RoutedEventArgs e) => CompleteImport(applyAfter: true);

        private void CompleteImport(bool applyAfter)
        {
            if (_outcome?.Profile == null || ImportSink == null) return;
            // The materializer stamped ImportedAt when the manifest was
            // built (config selection). Re-stamp at the actual import so
            // time spent reading the dossier doesn't age the record.
            if (_outcome.Profile.WorkshopSource != null)
                _outcome.Profile.WorkshopSource.ImportedAt = DateTime.UtcNow;
            ImportedProfileName = ImportSink(_outcome.Profile, applyAfter);
            ImportedClean = _outcome.Clean;
            ImportedPartial = _outcome.Partial;
            ImportedSkipped = _outcome.Skipped;
            Close();
        }

        // ─────────────────────────────────────────────
        //  Translator seam (#9 Phase B sibling)
        // ─────────────────────────────────────────────

        /// <summary>
        /// The one method that touches PadForge.SteamWorkshop.Translation.
        /// Runs the ConfigTranslator over the parsed config with the preset
        /// chips' selection and adapts the report into the dossier shape.
        /// </summary>
        private WorkshopTranslationOutcome RunTranslator(WorkshopConfigItem item, SteamInputConfig parsed)
        {
            // All chips on = null = all presets, so a config whose preset list
            // parsing missed something still translates whole.
            HashSet<int> included = null;
            if (PresetChips.Count > 0 && PresetChips.Any(p => !p.IsIncluded))
                included = PresetChips.Where(p => p.IsIncluded).Select(p => p.Id).ToHashSet();

            var options = new TranslationOptions
            {
                FileId = (long)item.FileId,
                IncludedPresetIds = included,
            };
            var translated = new ConfigTranslator().Translate(parsed, options);
            var report = translated.Report ?? new TranslationReport();

            // Workshop provenance (#9 Phase D): the identity of what was
            // imported, from the details this dialog already holds. The
            // materializer fills in the import-time facts.
            var provenance = new Services.SteamWorkshopSource
            {
                PublishedFileId = item.FileId,
                AppId = _selectedGame?.AppId ?? 0,
                GameName = _selectedGame?.Name,
                Title = item.Title,
                TimeUpdated = item.TimeUpdated,
            };

            return new WorkshopTranslationOutcome
            {
                Profile = Services.WorkshopProfileMaterializer.Materialize(translated, provenance),
                Clean = report.CleanCount,
                Partial = report.PartialCount,
                // The dossier has three stat blocks; a config that errored a
                // binding didn't carry it across, so errors count as skips.
                Skipped = report.SkippedCount + report.ErrorCount,
                BindingsRead = report.Entries.Count,
                Rows = BuildManifestRows(report),
            };
        }

        /// <summary>Adapts the report's entries into grouped dossier rows.
        /// Entries arrive in the translator's deterministic emit order, so
        /// groups are consecutive path clusters (preset / physical slot).</summary>
        private static List<object> BuildManifestRows(TranslationReport report)
        {
            var rows = new List<object>();
            var presets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in report.Entries)
            {
                int slash = entry.SourcePath.IndexOf('/');
                presets.Add(slash < 0 ? entry.SourcePath : entry.SourcePath.Substring(0, slash));
            }
            bool multiPreset = presets.Count > 1;

            string lastGroup = null;
            foreach (var entry in report.Entries)
            {
                string group = GroupLabel(entry.SourcePath, multiPreset);
                if (group != lastGroup)
                {
                    rows.Add(new WorkshopManifestGroupItem { Name = group });
                    lastGroup = group;
                }

                var (source, target) = SourceAndTarget(entry);
                rows.Add(new WorkshopManifestRowItem
                {
                    Source = source,
                    Target = target,
                    Reason = ReasonText(entry),
                    DotBrush = entry.Status switch
                    {
                        TranslationStatus.Clean => WorkshopManifestRowItem.CleanBrush,
                        TranslationStatus.Partial => WorkshopManifestRowItem.PartialBrush,
                        _ => WorkshopManifestRowItem.SkippedBrush,
                    },
                    DotGlow = entry.Status switch
                    {
                        TranslationStatus.Clean => WorkshopManifestRowItem.CleanGlow,
                        TranslationStatus.Partial => WorkshopManifestRowItem.PartialGlow,
                        _ => null,
                    },
                });
            }
            return rows;
        }

        /// <summary>Group header from the entry path
        /// (<c>Preset/slot_token/group N (mode)/input/Activator</c>): the
        /// physical cluster, preset-prefixed when several presets are in
        /// play. Aggregate entries (preset-only paths) group under the
        /// preset name, or the neutral fallback.</summary>
        private static string GroupLabel(string sourcePath, bool multiPreset)
        {
            var segments = (sourcePath ?? string.Empty).Split('/');
            if (segments.Length < 2)
            {
                return string.IsNullOrWhiteSpace(segments[0])
                    ? Strings.Instance.Workshop_GroupOther
                    : segments[0];
            }
            string cluster = PrettifySlotToken(segments[1]);
            return multiPreset ? segments[0] + " · " + cluster : cluster;
        }

        private static string PrettifySlotToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return Strings.Instance.Workshop_GroupOther;
            var body = token.Replace('_', ' ').Trim();
            return char.ToUpperInvariant(body[0]) + body.Substring(1);
        }

        /// <summary>Source and target columns. Emitted rows carry
        /// <c>"{target} &lt;- {descriptor}"</c>: the PadForge-side source
        /// descriptor is the cyan column and the target is what it drives.
        /// Non-emitted entries show the raw Steam binding against an em
        /// dash, exactly the design's skipped-row read.</summary>
        private static (string Source, string Target) SourceAndTarget(TranslationEntry entry)
        {
            string emitted = entry.Emitted ?? string.Empty;
            int arrow = emitted.IndexOf(" <- ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                string target = emitted.Substring(0, arrow);
                string source = emitted.Substring(arrow + 4);
                string activator = ActivatorAnnotation(entry.SourcePath);
                if (activator != null) source += " · " + activator;
                return (source, target);
            }

            string fallback = !string.IsNullOrWhiteSpace(entry.Binding)
                ? entry.Binding
                : PathTail(entry.SourcePath);
            // U+2014 is the design's skipped-row target glyph (a data
            // token in the manifest column, not punctuation).
            return (fallback, emitted.Length > 0 ? emitted : "\u2014");
        }

        /// <summary>The activator variant, lowercased ("long-press"), when it
        /// isn't a plain full press.</summary>
        private static string ActivatorAnnotation(string sourcePath)
        {
            var segments = (sourcePath ?? string.Empty).Split('/');
            if (segments.Length < 5) return null;
            string activator = segments[4];
            if (activator.Length == 0 ||
                string.Equals(activator, "Full_Press", StringComparison.OrdinalIgnoreCase))
                return null;
            return activator.Replace('_', '-').ToLowerInvariant();
        }

        private static string PathTail(string sourcePath)
        {
            var segments = (sourcePath ?? string.Empty).Split('/');
            // input (+ activator when notable), else the deepest segment.
            if (segments.Length >= 4)
            {
                string tail = segments[3];
                string activator = ActivatorAnnotation(sourcePath);
                return activator == null ? tail : tail + " · " + activator;
            }
            return segments[segments.Length - 1];
        }

        /// <summary>The inline why. Clean mechanical rows stay bare (the
        /// design's clean rows carry no reason); the automap-passthrough
        /// aggregate keeps its note, and everything not clean explains
        /// itself via its Workshop_Tr_* resource.</summary>
        private static string ReasonText(TranslationEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ReasonKey)) return null;
            if (entry.Status == TranslationStatus.Clean &&
                entry.ReasonKey != TranslationReasons.DefaultAutomapPassthrough)
                return null;

            string raw = Strings.Get(entry.ReasonKey);
            if (entry.ReasonArgs == null || entry.ReasonArgs.Count == 0) return raw;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, raw, entry.ReasonArgs.ToArray());
            }
            catch (FormatException)
            {
                return raw;
            }
        }

        // ─────────────────────────────────────────────
        //  Art plumbing
        // ─────────────────────────────────────────────

        /// <summary>Scrim gradients over the hero art, built from the live
        /// theme ground so the art fades into the page on either ground:
        /// vertical 18% → 66% at 46% → opaque at 78%, plus 72% side
        /// vignettes (design art-system table).</summary>
        private void BuildScrims()
        {
            var ground = (TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush)?.Color
                         ?? Color.FromRgb(0x0B, 0x0E, 0x14);

            Color At(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), ground.R, ground.G, ground.B);

            var vertical = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            vertical.GradientStops.Add(new GradientStop(At(0.18), 0));
            vertical.GradientStops.Add(new GradientStop(At(0.66), 0.46));
            vertical.GradientStops.Add(new GradientStop(At(1.0), 0.78));
            vertical.GradientStops.Add(new GradientStop(At(1.0), 1));
            vertical.Freeze();
            ScrimVertical.Fill = vertical;

            var horizontal = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
            };
            horizontal.GradientStops.Add(new GradientStop(At(0.72), 0));
            horizontal.GradientStops.Add(new GradientStop(At(0), 0.3));
            horizontal.GradientStops.Add(new GradientStop(At(0), 0.7));
            horizontal.GradientStops.Add(new GradientStop(At(0.72), 1));
            horizontal.Freeze();
            ScrimHorizontal.Fill = horizontal;
        }

        private static BitmapImage DecodeBitmap(byte[] data, int decodeWidth)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.StreamSource = new MemoryStream(data);
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Hero treatment baked into the pixels (design: saturate
        /// .82, brightness .8): WPF has no partial-saturation effect, so the
        /// bitmap is adjusted once on a worker and frozen.</summary>
        private static BitmapSource BuildHeroBitmap(byte[] data)
        {
            var decoded = DecodeBitmap(data, 1600);
            if (decoded == null) return null;
            try
            {
                var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth;
                int h = converted.PixelHeight;
                int stride = w * 4;
                var px = new byte[h * stride];
                converted.CopyPixels(px, stride, 0);

                const double saturation = 0.82;
                const double brightness = 0.8;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i];
                    double g = px[i + 1];
                    double r = px[i + 2];
                    double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                    px[i] = ClampByte((luma + (b - luma) * saturation) * brightness);
                    px[i + 1] = ClampByte((luma + (g - luma) * saturation) * brightness);
                    px[i + 2] = ClampByte((luma + (r - luma) * saturation) * brightness);
                }

                var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                result.Freeze();
                return result;
            }
            catch (Exception)
            {
                return decoded;
            }
        }

        private static byte ClampByte(double v) => (byte)Math.Clamp(v, 0, 255);

        // ─────────────────────────────────────────────
        //  Formatting helpers
        // ─────────────────────────────────────────────

        private static string FirstLetter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            return name.Trim().Substring(0, 1).ToUpperInvariant();
        }

        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var trimmed = name.Trim();
            return trimmed.Substring(0, Math.Min(2, trimmed.Length)).ToUpperInvariant();
        }

        private static string PrettifyTag(string tag)
        {
            var body = tag.StartsWith("controller_", StringComparison.OrdinalIgnoreCase)
                ? tag.Substring("controller_".Length)
                : tag;
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(body.Replace('_', ' '));
        }

        private static string CompactCount(ulong value)
        {
            if (value >= 1_000_000) return (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (value >= 1_000) return (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string RelativeTime(uint unixSeconds)
        {
            if (unixSeconds == 0) return Strings.Instance.Workshop_TimeToday;
            var then = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var days = (DateTimeOffset.UtcNow - then).TotalDays;
            if (days < 1) return Strings.Instance.Workshop_TimeToday;
            if (days < 60) return string.Format(Strings.Instance.Workshop_TimeDays_Format, (int)days);
            if (days < 365) return string.Format(Strings.Instance.Workshop_TimeMonths_Format, (int)(days / 30));
            return string.Format(Strings.Instance.Workshop_TimeYears_Format, (int)(days / 365));
        }
    }

    // ─────────────────────────────────────────────
    //  List items
    // ─────────────────────────────────────────────

    /// <summary>A game tile on the search shelf. Art and config count
    /// lazy-fill after the shelf renders.</summary>
    public sealed class WorkshopGameItem : ObservableObject
    {
        public int AppId { get; init; }
        public string Name { get; init; }
        public string Initial { get; init; }
        public string ControllerSupport { get; init; }
        public int? ConfigCount { get; set; }

        private ImageSource _portrait;
        public ImageSource Portrait
        {
            get => _portrait;
            set => SetProperty(ref _portrait, value);
        }

        private bool _isLetterbox;
        /// <summary>True when header art stands in for the missing portrait
        /// (letterboxed on the steel tile per the fallback chain).</summary>
        public bool IsLetterbox
        {
            get => _isLetterbox;
            set => SetProperty(ref _isLetterbox, value);
        }

        private string _countText;
        public string CountText
        {
            get => _countText;
            set => SetProperty(ref _countText, value);
        }
    }

    /// <summary>A config card: signals, not a spreadsheet.</summary>
    public sealed class WorkshopConfigItem : ObservableObject
    {
        public ulong FileId { get; init; }
        public string Title { get; init; }
        public ulong CreatorId { get; init; }
        public string FileUrl { get; init; }
        public long FileSize { get; init; }
        public uint TimeUpdated { get; init; }
        public bool IsLegacy { get; init; }
        public bool HasVotes { get; init; }
        public double VoteBarWidth { get; init; }
        public string VotePercentText { get; init; }
        public string SubsText { get; init; }
        public List<WorkshopTagChipItem> Tags { get; init; }

        private string _byLine;
        public string ByLine
        {
            get => _byLine;
            set => SetProperty(ref _byLine, value);
        }

        private string _avatarInitials;
        public string AvatarInitials
        {
            get => _avatarInitials;
            set => SetProperty(ref _avatarInitials, value);
        }

        private ImageSource _avatar;
        public ImageSource Avatar
        {
            get => _avatar;
            set => SetProperty(ref _avatar, value);
        }
    }

    /// <summary>One chip, two duties: the filter row over the config list
    /// (IsActive) and the controller-type tokens on a card (IsCold flags
    /// the Steam Deck chip).</summary>
    public sealed class WorkshopTagChipItem : ObservableObject
    {
        public string Tag { get; init; }
        public string Label { get; init; }
        public bool IsCold { get; init; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }
    }

    /// <summary>A preset chip in the dossier footer (these become shift
    /// layers on import).</summary>
    public sealed class WorkshopPresetChipItem : ObservableObject
    {
        public int Id { get; init; }
        public string Label { get; init; }

        private bool _isIncluded;
        public bool IsIncluded
        {
            get => _isIncluded;
            set => SetProperty(ref _isIncluded, value);
        }
    }

    /// <summary>Manifest group header row ("Button diamond", "Gyro", …).</summary>
    public sealed class WorkshopManifestGroupItem
    {
        public string Name { get; init; }
    }

    /// <summary>One telemetry row of the dossier: status dot, source →
    /// target, and the reason inline for anything not clean.</summary>
    public sealed class WorkshopManifestRowItem
    {
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Effect FrozenGlow(byte r, byte g, byte b)
        {
            var glow = new DropShadowEffect
            {
                Color = Color.FromRgb(r, g, b),
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.5,
            };
            glow.Freeze();
            return glow;
        }

        public static readonly Brush CleanBrush = Frozen(0x57, 0xC7, 0x84);
        public static readonly Brush PartialBrush = Frozen(0xE3, 0xB3, 0x41);
        public static readonly Brush SkippedBrush = Frozen(0x8B, 0x95, 0xA3);
        public static readonly Effect CleanGlow = FrozenGlow(0x57, 0xC7, 0x84);
        public static readonly Effect PartialGlow = FrozenGlow(0xE3, 0xB3, 0x41);

        public string Source { get; init; }
        public string Target { get; init; }
        public string Reason { get; init; }
        public Brush DotBrush { get; init; } = CleanBrush;
        public Effect DotGlow { get; init; }
    }

    /// <summary>The translator's answer, adapted for the dossier: the
    /// profile to register plus the grouped rows and stat counts.</summary>
    internal sealed class WorkshopTranslationOutcome
    {
        public Services.ProfileData Profile { get; init; }
        public int Clean { get; init; }
        public int Partial { get; init; }
        public int Skipped { get; init; }
        public int BindingsRead { get; init; }
        public List<object> Rows { get; init; } = new();
    }
}
