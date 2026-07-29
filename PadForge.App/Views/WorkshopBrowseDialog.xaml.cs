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

        /// <summary>See <see cref="WorkshopConfigListPager"/>.</summary>
        private const int ConfigsMaxSilentPages = 10;

        /// <summary>The pure paging decisions (page cursor, cross-page
        /// dedupe, end-of-results, silent-page bound) live in the pager;
        /// this class keeps the fetch loop and the panels.</summary>
        private readonly WorkshopConfigListPager _configsPager = new(ConfigsPageSize, ConfigsMaxSilentPages);

        /// <summary>Steam's total for the active query (QueryFiles reports
        /// it on every page), shown as "Showing N of M" while the list
        /// streams. Zero when Steam reported none.</summary>
        private int _configsTotal;
        private bool _configsFetchBusy;
        private DateTime _configsRetryAtUtc;

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
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so no point in the window was non-client and it
            // could not be moved at all. Same remedy MainWindow uses on its
            // branding bar.
            //
            // The guard is not optional. Standard controls (Button, TextBox,
            // ListBoxItem) mark this bubbling event handled and so never
            // reach here, but THIS dialog drives several bare Borders as
            // buttons (the back chip, the tag chips, the preset chips) and a
            // Border marks nothing. DragMove hands the mouse to the OS move
            // loop, which swallows the release, so the MouseLeftButtonUp
            // those Borders listen for never arrives and every one of them
            // goes dead. That is exactly what shipped: "Search Games" stopped
            // responding the moment this window became draggable.
            MouseLeftButtonDown += (_, e) =>
            {
                if (PressLandedOnSomethingClickable(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); } catch { }
            };


            ShelfList.ItemsSource = Games;
            ConfigList.ItemsSource = Configs;
            TagChipList.ItemsSource = TagChips;
            PresetChipList.ItemsSource = PresetChips;
            ManifestRowsList.ItemsSource = ManifestRows;
            UpdateSortChips();

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

        /// <summary><para>True when a press landed on anything the user can
        /// click, so the window drag must stand down and let the control have
        /// its release.</para>
        /// <para>Two tests, and both are needed. The standard controls are
        /// named outright. Everything else is caught by the hand cursor,
        /// which is this dialog's own convention for "this Border is a
        /// button" and, more to the point, is what the USER is shown before
        /// they click: if the pointer says the thing is pressable, pressing
        /// it must not drag the window instead. All three of this dialog's
        /// Border-buttons set it, two of them through their shared chip
        /// styles, so a new chip inherits the exemption for free.</para>
        /// </summary>
        private static bool PressLandedOnSomethingClickable(DependencyObject hit)
        {
            for (var d = hit; d != null;)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase
                    or System.Windows.Controls.Primitives.TextBoxBase
                    or System.Windows.Controls.Primitives.Selector
                    or System.Windows.Controls.Primitives.ScrollBar
                    or System.Windows.Controls.Primitives.Thumb
                    or ScrollViewer or ComboBox or Slider or MenuItem)
                    return true;
                if (d is FrameworkElement fe && fe.Cursor == Cursors.Hand)
                    return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

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
            // Opening a DIFFERENT game drops the query. Re-entering for the
            // same game is how the sort and the search themselves refetch, so
            // those must not clear what they just set. The tag filter resets
            // either way: its chips are rebuilt from the new result set.
            if (!ReferenceEquals(g, _selectedGame)) ResetConfigQuery();
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

        /// <summary>Hero backdrop swap. With art already on screen this is a
        /// 240 ms crossfade through steel: fade to the ground, swap, fade
        /// back, never art-to-art. With nothing showing, which is every swap
        /// entered from the search or cold state because leaving browse
        /// collapses the art layer, it is a straight 120 ms fade in. Honors
        /// the Windows "show animations" setting with an instant cut.</summary>
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
            _configsPager.Reset();
            _configsTotal = 0;
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
                var resp = await FetchConfigsPageAsync(g, requiredTag, _configsPager.NextPage, ct);
                if (ct.IsCancellationRequested) return;

                var details = resp?.publishedfiledetails ?? new List<SkPublishedFileDetails>();
                var rows = AppendConfigRows(details);

                int total = (int)(resp?.total ?? 0);
                _configsTotal = total;
                UpdateConfigsFoundText();
                // g.ConfigCount is a GAME-level stat that outlives this
                // dialog and feeds the browse list. Steam's `total` is the
                // count for THIS query, so writing it while a tag filter is
                // active turned a game's config count into a filter's count
                // and kept it. Only an unfiltered query may set it, and the
                // header meta line follows the same rule.
                if (requiredTag == null)
                {
                    g.ConfigCount = total;
                    UpdateGameMeta(total);
                }
                else
                {
                    UpdateGameMeta(g.ConfigCount);
                }

                // Chips come from the unfiltered result's live tags and stay
                // put while a tag filter narrows the list.
                if (requiredTag == null)
                    BuildTagChips(details);

                // The legacy filter can eat most of page 1. Top up to a full
                // page of visible rows so the list is scrollable (scrolling
                // is what drives further paging) before calling the room
                // empty.
                if (Configs.Count < ConfigsPageSize && !_configsPager.Exhausted)
                    rows.AddRange(await FetchConfigRowsAsync(g, requiredTag, ConfigsPageSize - Configs.Count, ct));
                if (ct.IsCancellationRequested) return;
                UpdateConfigsFoundText();

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

        /// <summary>One QueryFiles page for the game room: the chosen sort
        /// order, the requested tag filter, the shared page size.</summary>
        private Task<SkQueryFilesResponse> FetchConfigsPageAsync(
            WorkshopGameItem g, string requiredTag, int page, CancellationToken ct)
        {
            var tags = requiredTag == null ? null : new[] { requiredTag };
            return _workshop.SearchAsync(g.AppId, SortOrders[_sortIndex].Query, page, ConfigsPageSize,
                tags, ct, _configQuery);
        }

        // ─────────────────────────────────────────────
        //  Search within the game
        // ─────────────────────────────────────────────

        /// <summary>The live query, sent to Steam as the QueryFiles
        /// search_text filter. Null when the box is empty.</summary>
        private string _configQuery;

        private DispatcherTimer _configSearchDebounce;

        /// <summary>Debounced so a refetch fires once the typing settles
        /// rather than once per keystroke. Steam is a network round trip and
        /// this dialog already throttles itself against it.</summary>
        private void ConfigSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            ConfigSearchPlaceholder.Visibility = string.IsNullOrEmpty(ConfigSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;

            _configSearchDebounce ??= CreateConfigSearchDebounce();
            _configSearchDebounce.Stop();
            _configSearchDebounce.Start();
        }

        private DispatcherTimer CreateConfigSearchDebounce()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            t.Tick += (_, __) =>
            {
                t.Stop();
                ApplyConfigQuery();
            };
            return t;
        }

        /// <summary>Enter searches immediately; Escape clears back to the
        /// full list.</summary>
        private void ConfigSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _configSearchDebounce?.Stop();
                ApplyConfigQuery();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ConfigSearchBox.Text.Length > 0)
            {
                ConfigSearchBox.Clear();
                _configSearchDebounce?.Stop();
                ApplyConfigQuery();
                e.Handled = true;
            }
        }

        /// <summary>Refetches the game room under the current query. A
        /// refetch rather than a local filter: the point is to reach the
        /// configs that were never paged in, which is the whole problem on a
        /// game carrying thousands of them.</summary>
        /// <summary>Clears the query and the box without triggering a
        /// refetch: the caller is already about to fetch.</summary>
        private void ResetConfigQuery()
        {
            _configSearchDebounce?.Stop();
            _configQuery = null;
            if (ConfigSearchBox != null && ConfigSearchBox.Text.Length > 0)
                ConfigSearchBox.Clear();
        }

        private void ApplyConfigQuery()
        {
            string next = ConfigSearchBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(next)) next = null;
            if (string.Equals(next, _configQuery, StringComparison.Ordinal)) return;
            _configQuery = next;
            if (_selectedGame != null) _ = OpenGameAsync(_selectedGame);
        }

        // ─────────────────────────────────────────────
        //  Sort
        // ─────────────────────────────────────────────

        /// <summary><para>The orders worth offering, each a Steam query type
        /// so the RANKING IS STEAM'S over the whole result set rather than a
        /// re-shuffle of whatever page happens to be loaded.</para>
        /// <para>Rating stays first and stays the default: it is Steam's
        /// confidence-weighted vote score, which is why a 100% with two votes
        /// sits below an 86% with seven.</para></summary>
        private static readonly (string Key, EPublishedFileQueryType Query)[] SortOrders =
        {
            ("Workshop_Sort_Rating", EPublishedFileQueryType.RankedByVote),
            ("Workshop_Sort_Trend", EPublishedFileQueryType.RankedByTrend),
            ("Workshop_Sort_Newest", EPublishedFileQueryType.RankedByPublicationDate),
            ("Workshop_Sort_Subscribers", EPublishedFileQueryType.RankedByTotalUniqueSubscriptions),
            ("Workshop_Sort_Votes", EPublishedFileQueryType.RankedByVotesUp),
        };

        private int _sortIndex;
        private bool _sortAscending;

        /// <summary>Cycles the sort order and refetches. Refetch rather than
        /// re-sort, because the order decides WHICH configs come back, not
        /// just how the loaded ones line up.</summary>
        private void SortChip_Click(object sender, MouseButtonEventArgs e)
        {
            _sortIndex = (_sortIndex + 1) % SortOrders.Length;
            UpdateSortChips();
            if (_selectedGame != null) _ = OpenGameAsync(_selectedGame);
        }

        /// <summary><para>Flips the direction. Steam ranks descending and
        /// offers no ascending form, so this reverses the rows on this side.
        /// </para>
        /// <para>Which means it reverses what has been LOADED, not the whole
        /// result set: a game with 1,171 configs and 59 pulled in shows the
        /// weakest of those 59 first, not the weakest of the 1,171. Paging
        /// keeps working, and each new page is folded in and the whole list
        /// re-reversed, so the order stays consistent as it grows.</para>
        /// </summary>
        private void SortDir_Click(object sender, MouseButtonEventArgs e)
        {
            _sortAscending = !_sortAscending;
            UpdateSortChips();
            ApplySortDirection();
        }

        /// <summary>Reverses the loaded rows in place when ascending is on.
        /// Kept as a separate step from the fetch so newly paged-in rows can
        /// be folded into the same order without a round trip.</summary>
        private void ApplySortDirection()
        {
            if (Configs.Count < 2) return;
            var ordered = Configs.ToList();
            ordered.Reverse();
            Configs.Clear();
            foreach (var c in ordered) Configs.Add(c);
        }

        private void UpdateSortChips()
        {
            var si = Strings.Instance;
            SortChipText.Text = Strings.Get(SortOrders[_sortIndex].Key);
            SortDirText.Text = _sortAscending
                ? "↑ " + si.Workshop_Sort_Ascending
                : "↓ " + si.Workshop_Sort_Descending;
        }

        /// <summary>Appends one page's visible rows (the pager filters and
        /// dedupes, and advances its cursor). Returns what was added.</summary>
        private List<WorkshopConfigItem> AppendConfigRows(List<SkPublishedFileDetails> details)
        {
            var rows = new List<WorkshopConfigItem>();
            foreach (var d in _configsPager.Accept(details, _settings.ShowLegacyWorkshopConfigs))
                rows.Add(BuildConfigItem(d));
            // Steam hands every page back in descending rank. Ascending puts
            // each new page at the FRONT and reversed, which keeps one
            // consistent order as the list pages in rather than appending a
            // second descending run underneath the first.
            if (_sortAscending)
            {
                for (int i = 0; i < rows.Count; i++)
                    Configs.Insert(i, rows[rows.Count - 1 - i]);
            }
            else
            {
                foreach (var row in rows)
                    Configs.Add(row);
            }
            return rows;
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
            _configsPager.BeginFill();
            while (!_configsPager.Exhausted && added.Count < minRows && !ct.IsCancellationRequested)
            {
                var resp = await FetchConfigsPageAsync(g, requiredTag, _configsPager.NextPage, ct);
                if (ct.IsCancellationRequested) break;
                var details = resp?.publishedfiledetails ?? new List<SkPublishedFileDetails>();
                added.AddRange(AppendConfigRows(details));
            }
            return added;
        }

        /// <summary>"Showing N of M" while the list streams (M is Steam's
        /// total for the query, N the rows on screen after the ban/legacy
        /// filters). Falls back to the plain found-count when Steam
        /// reported no total.</summary>
        private void UpdateConfigsFoundText()
        {
            ConfigsFoundText.Text = _configsTotal > 0
                ? string.Format(Strings.Instance.Workshop_ShowingOf_Format, Configs.Count, _configsTotal)
                : string.Format(Strings.Instance.Workshop_Found_Format, Configs.Count);
        }

        /// <summary>Infinite scroll: within one viewport of the bottom, the
        /// next page streams in (the QueryFiles API pages; page 1 alone
        /// showed 30 of Skyrim's 155k configs). The distance math reads the
        /// same in both scroll units (items under the ListBox's logical
        /// scrolling, pixels otherwise). The Configs guard keeps the
        /// loading/empty/error states inert.</summary>
        private void ConfigList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Configs.Count == 0 || _configsPager.Exhausted || _configsFetchBusy) return;
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
            if (_configsFetchBusy || _configsPager.Exhausted) return;
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
                {
                    UpdateConfigsFoundText();
                    _ = FillPersonasAsync(rows, ct);
                }
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
            // Steam retired the subscribe step for controller configs during
            // 2023. Measured over the response cache (979 items, 908 unique):
            // every config created 2015-2022 carries a subscriber count, 2023
            // is the transition year at 18 of 93, and NOT ONE of the 556
            // created 2024 or later has a single subscriber. A modern layout
            // is applied straight from Steam's in-game config picker, and
            // nothing subscribes. So the count is real history where it
            // exists and a permanent zero on anything current, which is why
            // it only renders when it has something to say. Same rule the
            // vote bar beside it already follows.
            ulong subs = Math.Max(d.subscriptions, d.lifetime_subscriptions);
            bool hasSubs = subs > 0;

            var tags = (d.tags ?? new List<SkPublishedFileDetails.Tag>())
                .Where(t => t.tag != null && t.tag.StartsWith("controller_", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .Select(t => new WorkshopTagChipItem
                {
                    Tag = t.tag,
                    Label = ControllerTagLabel(t.tag, t.display_name).ToUpperInvariant(),
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
                // The vote COUNT, beside the percentage, because the list is
                // ordered by neither. Steam ranks RankedByVote on its own
                // confidence-weighted score, so evidence beats a thin
                // ratio: 6 up / 1 down (86%) outranks 2 up / 0 down (100%),
                // and a config with no votes at all sorts below one with a
                // single downvote. Showing the percentage alone made that
                // order look arbitrary, since the number on screen was not
                // the number being sorted on. The count is the missing
                // half, and with it the ranking reads correctly.
                VotesText = hasVotes
                    ? string.Format(Strings.Instance.Workshop_Votes_Format, up + down)
                    : string.Empty,
                HasSubs = hasSubs,
                SubsText = hasSubs
                    ? string.Format(Strings.Instance.Workshop_Subs_Format, CompactCount(subs))
                    : string.Empty,
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
                        Label = ControllerTagLabel(t.tag, t.display_name),
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
            // Cancel any in-flight manifest load first, the way BackToSearch
            // and LoadConfigsAsync both do. Without it the pending load kept
            // running and repopulated the panel this method just cleared.
            _manifestCts?.Cancel();
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
                    Label = PresetLabel(parsed, preset),
                    IsIncluded = true,
                });
            }
        }

        /// <summary><para>The name the config's author gave this preset.</para>
        /// <para>A preset's <c>name</c> is a SET TOKEN, not a label. Steam
        /// writes <c>"name" "Preset_1000001"</c> on the preset and carries
        /// the author's actual words on the matching entry of the
        /// <c>action_layers</c> block (<c>"title" "Deactivate trackpad"</c>),
        /// which the parser already collects into
        /// <see cref="SteamInputConfig.ActionSetTitles"/> keyed by that
        /// token. Rendering the token was how the chips came out reading
        /// "Preset_1000001" and "Preset_1000002" for a config whose two
        /// layers are named "Deactivate trackpad" and "Menu save".</para>
        /// <para>The title may itself be a <c>#token</c> into the config's
        /// localization block, so it resolves through there the same way the
        /// translator's own PresetDisplayName does. Chips and manifest groups
        /// have to agree, and now they read from the same two sources in the
        /// same order.</para></summary>
        private static string PresetLabel(SteamInputConfig parsed, SteamInputPreset preset)
        {
            string token = (preset.Name ?? string.Empty).Trim();
            string fallback = token.Length == 0
                ? "Preset " + preset.Id.ToString(CultureInfo.InvariantCulture)
                : token;
            if (token.Length == 0
                || parsed?.ActionSetTitles == null
                || !parsed.ActionSetTitles.TryGetValue(token, out var title)
                || string.IsNullOrWhiteSpace(title))
                return fallback;

            title = title.Trim();
            if (!title.StartsWith("#", StringComparison.Ordinal)) return title;

            // A #token indexes the config's own localization table. Prefer
            // the running UI language, then English, then any language that
            // carries it, before giving up and showing the token.
            foreach (var lang in PreferredConfigLanguages())
            {
                if (parsed.Localization != null
                    && parsed.Localization.TryGetValue(lang, out var strings)
                    && strings != null
                    && strings.TryGetValue(title.Substring(1), out var localized)
                    && !string.IsNullOrWhiteSpace(localized))
                    return localized.Trim();
            }
            foreach (var strings in parsed.Localization?.Values
                     ?? Enumerable.Empty<IReadOnlyDictionary<string, string>>())
            {
                if (strings != null
                    && strings.TryGetValue(title.Substring(1), out var any)
                    && !string.IsNullOrWhiteSpace(any))
                    return any.Trim();
            }
            return fallback;
        }

        /// <summary>Config-localization language keys to try, best first.
        /// Steam names them in English ("english", "german"), not as culture
        /// codes, so the running culture maps through its English name.</summary>
        private static IEnumerable<string> PreferredConfigLanguages()
        {
            var culture = CultureInfo.CurrentUICulture;
            string steamName = culture.TwoLetterISOLanguageName switch
            {
                "de" => "german",   "es" => "spanish",  "fr" => "french",
                "it" => "italian",  "ja" => "japanese", "ko" => "koreana",
                "nl" => "dutch",    "pt" => "brazilian", "zh" => "schinese",
                _ => "english",
            };
            yield return steamName;
            if (steamName != "english") yield return "english";
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

                // Light the pad. The manifest's Target column and the 2DModels
                // layout's TargetName share one namespace ("ButtonA",
                // "LeftTrigger", "DPadUp"), so the bound set needs no
                // translation. Axis targets carry an X/Y suffix the art does
                // not, so they fold onto the stick ring they belong to.
                ControllerPreview.Render(
                    item.Tags?.FirstOrDefault()?.Tag,
                    outcome.Rows.OfType<WorkshopManifestRowItem>()
                                .Select(r => new WorkshopControllerPreview.Callout(
                                    r.ArtAnchor, r.Target)));

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

            // Rows arrive in the translator's deterministic emit order, which
            // walks one preset at a time, so a layer's rows are contiguous and
            // a band can be opened each time the leading segment changes.
            var perLayer = report.Entries
                .GroupBy(e => LayerLabel(e.SourcePath))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            string lastGroup = null;
            string lastLayer = null;
            foreach (var entry in report.Entries)
            {
                if (multiPreset)
                {
                    string layer = LayerLabel(entry.SourcePath);
                    if (layer != lastLayer)
                    {
                        rows.Add(new WorkshopManifestLayerItem
                        {
                            Name = layer,
                            CountText = string.Format(Strings.Instance.Workshop_LayerBindings_Format,
                                perLayer.TryGetValue(layer, out int n) ? n : 0),
                        });
                        lastLayer = layer;
                        // A control header repeats under each layer it appears
                        // in, so the same pad reads as its own section there.
                        lastGroup = null;
                    }
                }

                // The layer band already carries the layer, so the control
                // header no longer repeats it on every line.
                string group = GroupLabel(entry.SourcePath, multiPreset && lastLayer == null);
                if (group != lastGroup)
                {
                    rows.Add(new WorkshopManifestGroupItem { Name = group });
                    lastGroup = group;
                }

                var (source, target, resolved) = SourceAndTarget(entry);
                // SourceAndTarget DECORATES the source: the translator's
                // half-axis/invert parenthetical plus a " . activator"
                // annotation. Both the art anchor and the friendly-name table
                // key on the BARE identifier, so they must be handed the
                // undecorated stem or they match nothing. The decoration is
                // re-attached to the display string only.
                var (bare, decoration) = SplitSourceDecoration(source);
                // FriendlySource runs ONLY over a resolved descriptor. Its
                // SpaceIdentifier tail splits on every lower-to-upper
                // boundary, which is right for "LeftStickX" and destructive
                // for prose already in display form ("D-Pad Up" comes back
                // as "D- Pad Up"). A skipped row arrives pre-humanized from
                // FriendlyBinding, so it must not be run through twice.
                rows.Add(new WorkshopManifestRowItem
                {
                    Source = (resolved ? FriendlySource(bare) : bare) + decoration,
                    Target = resolved ? FriendlySource(target) : target,
                    ArtAnchor = resolved ? ArtAnchorFor(bare) : null,
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
        /// <summary>The action layer a row belongs to: the leading segment of
        /// the entry path, which the translator fills with the preset's
        /// DISPLAY name (its action-set title, resolved through the config's
        /// localization). Aggregate entries whose whole path is the preset
        /// name land here too.</summary>
        private static string LayerLabel(string sourcePath)
        {
            var s = sourcePath ?? string.Empty;
            int slash = s.IndexOf('/');
            var head = (slash < 0 ? s : s.Substring(0, slash)).Trim();
            return head.Length == 0 ? Strings.Instance.Workshop_GroupOther : head;
        }

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

        /// <summary>Group header for a Steam slot token. Un-underscoring the
        /// token is not the same as naming the slot: it yielded "Joystick"
        /// for the thing PadForge calls the Left Stick, and "Button diamond"
        /// for the face buttons. The shared table names it the way the rest
        /// of the app does, and only a token that table does not know falls
        /// back to the mechanical spelling.</summary>
        private static string PrettifySlotToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return Strings.Instance.Workshop_GroupOther;
            string named = PhysicalSlotResolver.SlotDisplayName(token);
            if (!string.Equals(named, token, StringComparison.Ordinal)) return named;
            var body = token.Replace('_', ' ').Trim();
            return char.ToUpperInvariant(body[0]) + body.Substring(1);
        }

        /// <summary>Source and target columns. Emitted rows carry
        /// <c>"{target} &lt;- {descriptor}"</c>: the PadForge-side source
        /// descriptor is the cyan column and the target is what it drives.
        /// Non-emitted entries show the binding Steam wanted against an em
        /// dash, exactly the design's skipped-row read.</summary>
        /// <para><c>Resolved</c> says which of the two branches ran, because
        /// the caller must treat them differently. A resolved descriptor is
        /// an ENGINE IDENTIFIER ("LeftStickX") and still needs the friendly-
        /// name pass; the skip branch returns finished display prose and
        /// must not be put through that pass a second time.</para>
        private static (string Source, string Target, bool Resolved) SourceAndTarget(
            TranslationEntry entry)
        {
            string emitted = entry.Emitted ?? string.Empty;
            int arrow = emitted.IndexOf(" <- ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                string target = HumanizeKbmTarget(emitted.Substring(0, arrow));
                string source = emitted.Substring(arrow + 4);
                string activator = ActivatorAnnotation(entry.SourcePath);
                if (activator != null) source += " · " + activator;
                return (source, target, true);
            }

            // Reached only by a SKIP. Every successful translation carries
            // its resolved descriptor after the arrow, rows and macros
            // alike (ConfigTranslator.MacroEmit), so this branch never
            // renders something PadForge actually mapped. The binding is
            // spelled as words on the way out: Steam's wire grammar is not
            // a vocabulary to show anyone using this app.
            string fallback = !string.IsNullOrWhiteSpace(entry.Binding)
                ? FriendlyBinding(entry.Binding)
                : FriendlySource(PathTail(entry.SourcePath));
            // U+2014 is the design's skipped-row target glyph (a data
            // token in the manifest column, not punctuation).
            return (fallback, emitted.Length > 0 ? HumanizeKbmTarget(emitted) : "\u2014", false);
        }

        /// <summary>Renders a keyboard/mouse mapping target as the key a
        /// person would recognize. The translator's target vocabulary is
        /// the engine's storage grammar ("KbmKey5A" is a HEX virtual-key
        /// code, "KbmMBtn1" a mouse-button index), which is correct on
        /// disk and unreadable in a preview: nobody knows 0x5A is Z.
        /// Routes through the same localized
        /// <see cref="ViewModels.MacroAction.VirtualKeyDisplayName"/> table
        /// the macro editor's key picker uses, so the preview and the
        /// editor name a key identically in every language. Non-KBM
        /// targets (gamepad buttons, axes) pass through untouched, being
        /// already human-readable.</summary>
        internal static string HumanizeKbmTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return target;

            if (target.StartsWith("KbmKey", StringComparison.Ordinal)
                && int.TryParse(target.AsSpan(6), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int vk)
                && Enum.IsDefined(typeof(PadForge.Common.VirtualKey), vk))
                return ViewModels.MacroAction.VirtualKeyDisplayName((PadForge.Common.VirtualKey)vk);

            // Mouse buttons are ZERO-based across the whole vocabulary
            // (audit 2026-07-25, C25): SteamInputVkTable emits LEFT as
            // KbmMBtn0, and MappingTranslation / the KBM output map / the
            // editor's own picker all agree. The first cut of this helper
            // read them as 1-based, so every button rendered as its
            // predecessor and left-click fell through unnamed, which is
            // worse than the raw string it replaced.
            var si = Strings.Instance;
            if (target.StartsWith("KbmMBtn", StringComparison.Ordinal)
                && int.TryParse(target.AsSpan(7), out int btn))
            {
                // The editor's own KBM row labels, so the preview and the
                // mapping grid name the same target identically.
                return btn switch
                {
                    0 => si.Mouse_LeftClick,
                    1 => si.Mouse_RightClick,
                    2 => si.Mouse_MiddleClick,
                    3 => si.Mouse_Button4,
                    4 => si.Mouse_Button5,
                    _ => target,
                };
            }

            return target switch
            {
                // Same rule for the analog lanes: these are the strings the
                // KBM mapping rows use for these exact target names.
                "KbmMouseX" => si.Mouse_X,
                "KbmMouseY" => si.Mouse_Y,
                "KbmScroll" => si.Mouse_Scroll,
                "KbmScrollH" => si.Mouse_ScrollH,
                _ => target,
            };
        }

        /// <summary>The <c>xinput_button</c> tokens of Steam's binding
        /// grammar. Corpus-grounded: these are every value the fixtures
        /// carry for that type.</summary>
        private static readonly Dictionary<string, string> XInputButtonNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y",
                ["DPAD_UP"] = "D-Pad Up", ["DPAD_DOWN"] = "D-Pad Down",
                ["DPAD_LEFT"] = "D-Pad Left", ["DPAD_RIGHT"] = "D-Pad Right",
                ["SHOULDER_LEFT"] = "Left Bumper", ["SHOULDER_RIGHT"] = "Right Bumper",
                ["TRIGGER_LEFT"] = "Left Trigger", ["TRIGGER_RIGHT"] = "Right Trigger",
                ["JOYSTICK_LEFT"] = "Left Stick Click", ["JOYSTICK_RIGHT"] = "Right Stick Click",
                ["LSTICK_DOWN"] = "Left Stick Down",
                ["START"] = "Menu", ["SELECT"] = "View",
            };

        /// <summary>The <c>controller_action</c> verbs spelled the way a
        /// person reads them. Steam's own grammar is SCREAMING_SNAKE and
        /// belongs in the config file, never on screen. Unlocalized English
        /// on purpose, matching <see cref="InputNames"/>: same display-
        /// vocabulary layer, and every row carrying one of these has a
        /// localized Reason beside it doing the explaining.</summary>

        /// <summary><para>Renders a raw Steam <c>binding</c> value as words.
        /// Only a genuinely SKIPPED row reaches this: everything PadForge
        /// translated carries its own resolved descriptor (see
        /// <c>ConfigTranslator.MacroEmit</c>). A skip still has to say what
        /// Steam wanted, and saying it as
        /// <c>"controller_action set_led 242 25 0 100 255 1"</c> hands the
        /// user a wire format they have no reason to know.</para>
        /// <para>The binding's own grammar is
        /// <c>&lt;type&gt; &lt;param...&gt;, &lt;label&gt;, &lt;icon&gt;</c>,
        /// and the label is the config author's own name for the binding
        /// ("key_press 1, Weapon 1"). Nothing this method can synthesize
        /// beats the words the author chose, so the label wins outright when
        /// there is one. Steam's own localization tokens ("#ChangeClass")
        /// are not words, so they do not count as one.</para></summary>
        internal static string FriendlyBinding(string rawBinding)
        {
            if (string.IsNullOrWhiteSpace(rawBinding)) return rawBinding;

            SteamInputBinding b;
            try { b = SteamInputBinding.Parse(rawBinding); }
            catch (ArgumentException) { return rawBinding; }

            string label = (b.ActionName ?? string.Empty).Trim();
            if (label.Length > 0 && label[0] != '#') return label;

            var parts = (b.Param ?? string.Empty)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string first = parts.Length > 0 ? parts[0] : string.Empty;

            switch ((b.Type ?? string.Empty).ToLowerInvariant())
            {
                case "key_press":
                    // Through the same VK table the macro editor's key
                    // picker uses, so one key reads identically everywhere.
                    return SteamInputVkTable.TryResolve(first, out byte vk, out _)
                        ? HumanizeKbmTarget(SteamInputVkTable.KbmKeyTarget(vk))
                        : TitleFromToken(first);

                case "mouse_button":
                    return SteamInputVkTable.TryResolveMouseButtonIndex(first, out int mb)
                        ? HumanizeKbmTarget("KbmMBtn" + mb.ToString(CultureInfo.InvariantCulture))
                        : TitleFromToken(first);

                case "xinput_button":
                    return XInputButtonNames.TryGetValue(first, out var xb)
                        ? xb : TitleFromToken(first);

                case "controller_action":
                    return PadForge.SteamWorkshop.Translation.SteamVocabulary
                        .CommandLabel(first) ?? TitleFromToken(first);

                case "game_action":
                    // "game_action FPSControls attack": the action itself,
                    // not the action-set token that scopes it.
                    return TitleFromToken(parts.Length > 1 ? parts[parts.Length - 1] : first);

                // mouse_wheel SCROLL_UP, and mode_shift's hosting slot.
                default:
                    return first.Length > 0 ? TitleFromToken(first) : TitleFromToken(b.Type);
            }
        }

        /// <summary>"LEFT_SHIFT" -> "Left Shift", "left_trackpad" -> "Left
        /// Trackpad". Steam's token separator is the underscore, so the
        /// words are already there and only need the casing.</summary>
        internal static string TitleFromToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            var words = token.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder(token.Length + 2);
            foreach (var w in words)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(w[0]));
                // Lower the tail only when the token was SCREAMING_SNAKE;
                // a mixed-case word ("DPad") keeps the author's shape.
                sb.Append(w.Length > 1
                    ? (w.ToUpperInvariant() == w ? w.Substring(1).ToLowerInvariant() : w.Substring(1))
                    : string.Empty);
            }
            return sb.ToString();
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
            // The localized sentence is prose; the arguments dropped into it
            // are not. For the keys listed in TokenArgReasons those slots
            // carry Steam's own grammar, and a reason that reads "controller
            // action TOGGLE_MAGNIFIER not supported" hands the user a token
            // out of a config file. Spell it.
            object[] args = TokenArgReasons.Contains(entry.ReasonKey)
                ? entry.ReasonArgs.Select(a => (object)SpellTokenList(a)).ToArray()
                : entry.ReasonArgs.ToArray();
            try
            {
                return string.Format(CultureInfo.CurrentCulture, raw, args);
            }
            catch (FormatException)
            {
                return raw;
            }
        }

        /// <summary>The reason keys whose format arguments are Steam's own
        /// grammar (a verb, a slot, an input, a setting key) rather than
        /// author text, a PadForge descriptor, or a number. Membership is
        /// explicit, not pattern-matched, because the three excluded kinds
        /// must survive verbatim: an author's layer name is THEIR writing
        /// and underscores in it are theirs to keep, a descriptor belongs to
        /// <see cref="FriendlySource"/>'s table instead, and a number has
        /// nothing to spell.</summary>
        private static readonly HashSet<string> TokenArgReasons = new(StringComparer.Ordinal)
        {
            TranslationReasons.SteamSystemAction,
            TranslationReasons.UnsupportedControllerAction,
            TranslationReasons.LayerReleaseEdgeApproximated,
            TranslationReasons.UnknownBindingType,
            TranslationReasons.UnknownKey,
            TranslationReasons.UnsupportedKey,
            TranslationReasons.LongPressKeyTap,
            TranslationReasons.UnknownMouseButton,
            TranslationReasons.UnknownXInputButton,
            TranslationReasons.UnknownPhysicalInput,
            TranslationReasons.MobileTouchSurfaceOnly,
            TranslationReasons.UnknownGroupMode,
            TranslationReasons.UnknownActivatorType,
            TranslationReasons.MissingModeShiftGroup,
            TranslationReasons.RowCapExceeded,
            TranslationReasons.MenuSurfaceNotSupported,
            TranslationReasons.ResponseCurveNotSupported,
            TranslationReasons.DeadZoneRadialResidual,
            TranslationReasons.RotationNonlinearWithheld,
            TranslationReasons.MouseRegionTuningDropped,
            TranslationReasons.MouseModeTuningDropped,
            TranslationReasons.AxisInversionNotApplied,
            TranslationReasons.GyroButtonMaskDropped,
        };

        /// <summary>Spells one Steam token, or a comma-joined run of them
        /// ("curve_exponent, custom_curve_exponent"), as words. Anything
        /// already carrying a space is prose rather than a token, and a
        /// number is left exactly as the translator computed it.</summary>
        internal static string SpellTokenList(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return arg;
            var parts = arg.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0 || t.IndexOf(' ') >= 0
                    || double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    continue;
                parts[i] = SpellSteamToken(t);
            }
            return string.Join(", ", parts.Select(s => s.Trim()));
        }

        /// <summary>One token, through the named tables first so a verb
        /// reads as what it does ("Set light color") rather than as its
        /// spelled-out identifier ("Set Led").</summary>
        internal static string SpellSteamToken(string token)
            => PadForge.SteamWorkshop.Translation.SteamVocabulary.CommandLabel(token)
             ?? (XInputButtonNames.TryGetValue(token, out var xb) ? xb
             : TitleFromToken(token));

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

        /// <summary>Length in CHAR units of the text element starting at
        /// index 0, so a name beginning with an astral-plane character (an
        /// emoji, common in Steam personas) is not split into a lone
        /// surrogate that renders as the replacement glyph.</summary>
        private static int FirstElementLength(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]) ? 2 : 1;
        }

        private static string FirstLetter(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var trimmed = name.Trim();
            return trimmed.Substring(0, FirstElementLength(trimmed)).ToUpperInvariant();
        }

        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var trimmed = name.Trim();
            int take = FirstElementLength(trimmed);
            if (take < trimmed.Length)
                take += FirstElementLength(trimmed.Substring(take));
            return trimmed.Substring(0, Math.Min(take, trimmed.Length)).ToUpperInvariant();
        }

        /// <summary>Retail names for Steam's controller tag namespace.
        ///
        /// <para>Steam hands back the RAW TAG as a controller tag's
        /// display_name, so the browse chips rendered
        /// "controller_ps5_edge" and "controller_steamcontroller_gordon"
        /// verbatim. Prettifying the tag alone is not enough either: it
        /// yields "Ps5 Edge" and "Steamcontroller Gordon", because the tag
        /// bodies are Valve's internal codenames rather than product
        /// names.</para>
        ///
        /// <para>Codenames resolved rather than guessed, since these are
        /// user-visible: <c>neptune</c> is the Steam Deck, <c>triton</c> is
        /// the Steam Controller (2026) (28DE:1304, the same pad this app
        /// already drives as SC2026), and <c>gordon</c> is the Steam
        /// Controller (2015). Both Steam Controller generations are named
        /// by YEAR rather than by ordinal, which is the branding and also
        /// what PadForge calls them everywhere else.</para></summary>
        private static readonly Dictionary<string, string> ControllerTagNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["controller_xbox360"] = "Xbox 360",
                ["controller_xboxone"] = "Xbox One",
                ["controller_xboxelite"] = "Xbox Elite",
                ["controller_ps4"] = "DualShock 4",
                ["controller_ps5"] = "DualSense",
                ["controller_ps5_edge"] = "DualSense Edge",
                ["controller_switch_pro"] = "Switch Pro",
                ["controller_neptune"] = "Steam Deck",
                ["controller_triton"] = "Steam Controller (2026)",
                ["controller_steamcontroller_gordon"] = "Steam Controller (2015)",
                ["controller_steamcontroller"] = "Steam Controller",
                ["controller_generic"] = "Generic",
                ["controller_mobile_touch"] = "Mobile Touch",
            };

        /// <summary>The label for one controller tag. Prefers the mapped
        /// retail name, then a display_name only when Steam actually gave
        /// one (it usually echoes the tag), then the prettified tag so a
        /// controller released after this build still reads sanely.</summary>
        internal static string ControllerTagLabel(string tag, string displayName)
        {
            if (tag != null && ControllerTagNames.TryGetValue(tag, out var retail))
                return retail;
            if (!string.IsNullOrEmpty(displayName)
                && !string.Equals(displayName, tag, StringComparison.OrdinalIgnoreCase))
                return displayName;
            return PrettifyTag(tag);
        }

        /// <summary>Real button names for the manifest and the callouts.
        ///
        /// <para>Both columns used to render the engine's own identifiers
        /// ("Gamepad Paddle2", "LeftThumbAxisX", "ButtonA"). That is
        /// programmer vocabulary on a screen users read to decide whether
        /// to install someone's config. Names here are what is printed on
        /// the hardware or what a player would say out loud.</para></summary>
        private static readonly Dictionary<string, string> InputNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ButtonA"] = "A", ["ButtonB"] = "B", ["ButtonX"] = "X", ["ButtonY"] = "Y",
                ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y",
                ["LeftTrigger"] = "Left Trigger", ["RightTrigger"] = "Right Trigger",
                ["LeftShoulder"] = "Left Bumper", ["RightShoulder"] = "Right Bumper",
                ["LeftBumper"] = "Left Bumper", ["RightBumper"] = "Right Bumper",
                ["LeftThumbAxisX"] = "Left Stick", ["LeftThumbAxisY"] = "Left Stick",
                ["RightThumbAxisX"] = "Right Stick", ["RightThumbAxisY"] = "Right Stick",
                ["LeftStickX"] = "Left Stick", ["LeftStickY"] = "Left Stick",
                ["RightStickX"] = "Right Stick", ["RightStickY"] = "Right Stick",
                ["LeftThumbButton"] = "Left Stick Click",
                ["RightThumbButton"] = "Right Stick Click",
                ["LeftThumbRing"] = "Left Stick", ["RightThumbRing"] = "Right Stick",
                ["DPadUp"] = "D-Pad Up", ["DPadDown"] = "D-Pad Down",
                ["DPadLeft"] = "D-Pad Left", ["DPadRight"] = "D-Pad Right",
                ["ButtonStart"] = "Menu", ["ButtonBack"] = "View",
                ["ButtonGuide"] = "Guide", ["Start"] = "Menu", ["Back"] = "View",
                ["Paddle1"] = "Paddle 1", ["Paddle2"] = "Paddle 2",
                ["Paddle3"] = "Paddle 3", ["Paddle4"] = "Paddle 4",
                ["Touchpad"] = "Touchpad", ["TouchpadClick"] = "Touchpad Click",
            };

        /// <summary>Names one input for a human. Falls back to spacing the
        /// identifier so an input we have not named still reads as words
        /// rather than as run-together Pascal case.</summary>
        internal static string FriendlySource(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var s = raw.StartsWith("Gamepad ", StringComparison.OrdinalIgnoreCase)
                ? raw.Substring("Gamepad ".Length)
                : raw;
            s = s.Trim();
            if (InputNames.TryGetValue(s, out var nice)) return nice;
            if (InputNames.TryGetValue(s.Replace(" ", ""), out nice)) return nice;
            return SpaceIdentifier(s);
        }

        /// <summary>"LeftStickX" -> "Left Stick X", "Paddle2" -> "Paddle 2".
        /// Consecutive capitals stay together so "DPad" does not shatter.</summary>
        internal static string SpaceIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool boundary = i > 0
                    && (char.IsDigit(c) != char.IsDigit(s[i - 1])
                        || (char.IsUpper(c) && !char.IsUpper(s[i - 1])));
                if (boundary && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Folds a translator SOURCE onto the art element that
        /// draws it.
        ///
        /// <para>The preview renders the controller the config was authored
        /// on (the body is picked from the config's own controller tag), so
        /// a binding belongs on the control the author physically pressed,
        /// labelled with what it produces. Anchoring on the target instead
        /// drew the virtual pad's geometry on the source device's body: a
        /// Steam Deck touchpad bound to the d-pad lit the DECK'S d-pad and
        /// called it "Touchpad 0", which is backwards on every layout.</para></summary>
        /// <summary>Splits a manifest source into the bare engine identifier
        /// and its human decoration.
        ///
        /// <para>The manifest shows a source as the identifier plus, where
        /// they apply, the translator's half-axis/invert parenthetical and an
        /// activator annotation after a middle dot. Those belong in the label
        /// and nowhere else: the art anchor and the friendly-name table both
        /// key on the bare stem, so handing either the decorated string makes
        /// every non-default activator match nothing and fall through to the
        /// raw identifier.</para></summary>
        internal static (string Bare, string Decoration) SplitSourceDecoration(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return (source, string.Empty);
            var bare = source;
            var decoration = string.Empty;

            int dot = bare.IndexOf(" · ", StringComparison.Ordinal);
            if (dot >= 0)
            {
                decoration = bare.Substring(dot);
                bare = bare.Substring(0, dot);
            }

            int paren = bare.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && bare.EndsWith(")", StringComparison.Ordinal))
            {
                decoration = bare.Substring(paren) + decoration;
                bare = bare.Substring(0, paren);
            }

            return (bare.TrimEnd(), decoration);
        }

        internal static string ArtAnchorFor(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var s = source.Trim();

            // Touchpad sources name a PAD and a gesture on it
            // ("Touchpad 0 DPadUp", "Touchpad 1 Finger 0 X"), not a
            // button. Pad 0 is the left surface, pad 1 the right.
            if (s.StartsWith("Touchpad ", StringComparison.OrdinalIgnoreCase))
                return s.Length > 9 && s[9] == '1' ? "RightTouchpadClick" : "LeftTouchpadClick";

            // The mapping grid stores most physical inputs under the
            // family prefix; art TargetNames are bare.
            if (s.StartsWith("Gamepad ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Gamepad ".Length);

            // Every stick family anchors on the ring: the axes, the ring
            // descriptor, and the axis pair the manifest reports per axis.
            if (s.StartsWith("LeftStick", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("LeftThumbAxis", StringComparison.OrdinalIgnoreCase))
                return "LeftThumbRing";
            if (s.StartsWith("RightStick", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("RightThumbAxis", StringComparison.OrdinalIgnoreCase))
                return "RightThumbRing";

            return s;
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
        public string VotesText { get; init; }
        public bool HasSubs { get; init; }
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

    /// <summary>An action-layer band above the control headers that belong to
    /// it. Only emitted for a config with more than one layer.</summary>
    public sealed class WorkshopManifestLayerItem
    {
        public string Name { get; init; }
        public string CountText { get; init; }
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

        /// <summary>The RAW target, kept for the controller preview's art
        /// join. Target itself is humanized for display, and the 2DModels
        /// layout keys on the engine identifier, so the two must not be the
        /// same string.</summary>
        /// <summary>The art element this row is DRAWN ON: the physical
        /// control the config binds, not the virtual output it produces.
        /// The preview shows the pad the config was authored for, so the
        /// anchor is the source and the label is what it does.</summary>
        public string ArtAnchor { get; init; }
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
