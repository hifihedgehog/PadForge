using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Services;

namespace PadForge.Common.Themes
{
    /// <summary>Why a game's look could not be made.</summary>
    internal enum GameLookFault
    {
        None,
        /// <summary>Steam has no wide picture for the game.</summary>
        NoArt,
        /// <summary>PadForge may not ask Steam, and has no copy of the picture.</summary>
        LookupOff,
    }

    /// <summary>
    /// A Steam profile's game look (#591): PadForge wears the game's picture
    /// and four colors taken from it while that profile is the active one,
    /// and the user's own theme returns when another profile is.
    ///
    /// A look is made once, when the user turns the option on, at the import
    /// or in the profile's dialog: the game's library picture and logo come
    /// from Steam (<see cref="SteamGameArt"/>), the colors from the picture
    /// (<see cref="ThemePalette"/>), and the picture made to fit
    /// (<see cref="ThemePicture"/>) goes into <see cref="GameLookStore"/>.
    /// The profile keeps the game and the colors. After that a look asks
    /// nothing of the network: a profile becoming active reads the stored
    /// picture, and where it is gone the colors alone apply.
    ///
    /// A game look stands on the dark base, where a game's library art is
    /// drawn to be seen. Every member runs on the UI thread.
    /// </summary>
    internal static class GameLooks
    {
        /// <summary>Where a made look's picture starts in a gallery tile:
        /// the logo, then the picture, as the builder's own pictures.</summary>
        internal const double TileAt = 0.36;

        private static GameLookStore _store = new GameLookStore();
        private static Dispatcher _dispatcher;
        private static string _shownKey;
        private static ThemeDefinition _shown;

        /// <summary>Where looks' pictures are kept. A test gives its own.</summary>
        internal static GameLookStore Store
        {
            get => _store;
            set
            {
                _store = value ?? new GameLookStore();
                _shownKey = null;
                _shown = null;
            }
        }

        /// <summary>Follows the active profile from here on: its look goes
        /// on when it becomes active and comes off when another does.</summary>
        internal static void Start(Dispatcher dispatcher)
        {
            if (_dispatcher != null) SettingsManager.ActiveProfileChanged -= RefreshSoon;
            _dispatcher = dispatcher;
            if (dispatcher != null) SettingsManager.ActiveProfileChanged += RefreshSoon;
            // The profiles may have loaded before this, with one active.
            RefreshSoon();
        }

        /// <summary>Refreshes once the change that asked for it is done:
        /// the profile switch that raised it is still setting the profile up.</summary>
        internal static void RefreshSoon()
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(Refresh));
        }

        /// <summary>Puts the active profile's look on, or takes a look off
        /// when the active profile has none.</summary>
        internal static void Refresh()
        {
            string id = SettingsManager.ActiveProfileId;
            ProfileData profile = string.IsNullOrEmpty(id) ? null : SettingsManager.Profiles?.Find(p => p.Id == id);
            ProfileGameLook look = profile?.GameLook;
            if (look == null || look.AppId <= 0)
            {
                _shownKey = null;
                _shown = null;
                ThemeManager.SetGameLook(null);
                return;
            }
            // The same look is the same theme object, so a switch between two
            // profiles of one game redraws nothing.
            string key = string.Join("|", look.AppId, look.Game, look.Background, look.Accent, look.Secondary, look.Attention,
                _store.Stamp(look.AppId)?.Ticks ?? 0);
            if (key != _shownKey || _shown == null)
            {
                _shown = ThemeOf(look);
                _shownKey = key;
            }
            ThemeManager.SetGameLook(_shown);
        }

        /// <summary>Whether the look's picture is on this PC.</summary>
        internal static bool HasPicture(ProfileGameLook look) => look != null && _store.Stamp(look.AppId) != null;

        /// <summary>The theme a look draws as: its colors on the dark base,
        /// with its stored picture when there is one. A color the profile
        /// holds that is no color is left to PadForge's own.</summary>
        internal static ThemeDefinition ThemeOf(ProfileGameLook look)
        {
            if (look == null) return null;
            static Color? Shade(string text) => ThemeColor.TryParse(text, out Color parsed) ? parsed : null;
            byte[] picture = _store.Read(look.AppId);
            return new ThemeDefinition
            {
                Kind = ThemeKind.GameLook,
                Id = ThemeManager.GamePrefix + look.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = look.Game ?? string.Empty,
                Dark = true,
                Background = Shade(look.Background),
                Accent = Shade(look.Accent),
                Secondary = Shade(look.Secondary),
                Attention = Shade(look.Attention),
                PictureBytes = picture,
                PictureName = picture != null ? "picture.png" : null,
                TileAt = TileAt,
            };
        }

        /// <summary>A look that was just made and is not kept yet: the
        /// values the profile takes and the picture for the store. The
        /// picture goes into the store when the profile is saved
        /// (<see cref="Keep"/>), so a look the user backs out of leaves
        /// nothing behind.</summary>
        internal sealed class Made
        {
            internal ProfileGameLook Look;
            internal byte[] Picture;
            internal GameLookFault Fault;
        }

        /// <summary>
        /// Makes a game's look: gets its library picture and logo, takes the
        /// four colors from the picture, and makes the picture fit. With no
        /// picture to be had there is no look, and the fault says whether
        /// Steam has none or PadForge may not ask. Awaited on the UI thread,
        /// where it draws.
        /// </summary>
        internal static async Task<Made> MakeAsync(SteamGameArt art, int appId, string game, CancellationToken ct)
        {
            if (art == null || appId <= 0) return new Made { Fault = GameLookFault.NoArt };
            var (hero, logo) = await art.FetchAsync(appId, ct);
            ct.ThrowIfCancellationRequested();
            BitmapSource photo = hero != null ? ThemePicture.DecodeSource(hero) : null;
            if (photo == null) return new Made { Fault = art.IsOn ? GameLookFault.NoArt : GameLookFault.LookupOff };
            BitmapSource mark = logo != null ? BoundedBitmap.FromBytes(logo, 1280, 720, scaleUp: false) : null;

            var colors = ThemePalette.Suggest(photo, dark: true);
            BitmapSource picture = ThemePicture.Compose(photo, mark, colors.Background, dark: true, focus: 0.5);
            return new Made
            {
                Look = new ProfileGameLook
                {
                    AppId = appId,
                    Game = string.IsNullOrWhiteSpace(game) ? null : game.Trim(),
                    Background = ThemeColor.Format(colors.Background),
                    Accent = ThemeColor.Format(colors.Accent),
                    Secondary = ThemeColor.Format(colors.Secondary),
                    Attention = ThemeColor.Format(colors.Attention),
                },
                Picture = ThemePicture.EncodePng(picture),
            };
        }

        /// <summary>Keeps a made look's picture, when the profile that takes
        /// the look is saved. A picture that cannot be kept leaves a look of
        /// colors alone.</summary>
        internal static void Keep(Made made)
        {
            if (made?.Look == null || made.Picture == null) return;
            _store.Write(made.Look.AppId, made.Picture);
            _shownKey = null;
        }

        /// <summary>Lets a game's picture go when no profile wears its look
        /// any more: after a look was turned off or its profile deleted.</summary>
        internal static void Release(int appId)
        {
            if (appId <= 0) return;
            bool worn = SettingsManager.Profiles != null && SettingsManager.Profiles.Any(p => p.GameLook != null && p.GameLook.AppId == appId);
            if (!worn) _store.Remove(appId);
        }
    }
}
