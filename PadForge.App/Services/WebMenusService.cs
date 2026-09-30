using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// The Web Menus page's content (#471). On the UI timer, ten times a
    /// second, this builds each connected Web Menus phone's snapshot: the
    /// PC's active profile and the profile list, and one page for each Touch
    /// Grid menu the phone may fire on each slot it is assigned to, with each
    /// cell's label, icon and Toggle state. The device sends a snapshot only
    /// when it changed. Pictures go by opaque token through /api/menuicon,
    /// which serves only an icon a current snapshot names.
    /// </summary>
    internal sealed class WebMenusService
    {
        private const int BuildIntervalMs = 100;

        /// <summary>The largest grid the phone draws, the overlay's own limit
        /// (MenuOverlayWindow.MaxRenderCells) and the engine's press bits.</summary>
        internal const int MaxCells = 64;

        private long _lastBuildMs;
        private readonly UserSetting[] _slotBuffer = new UserSetting[InputManager.MaxPads];

        private static volatile Dictionary<string, string> _iconTokens = new(StringComparer.Ordinal);

        /// <summary>Pictures read for the endpoint, by reference, with their
        /// type. Filled and cleared under <see cref="IconSync"/>, and a read
        /// that a registry change overtook is served once but not kept.</summary>
        private static readonly Dictionary<string, (byte[] Bytes, string Type)> _iconBytes = new(StringComparer.Ordinal);
        private static readonly object IconSync = new();
        private static int _iconVersion;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        static WebMenusService()
        {
            // A package added, renamed or removed changes what a reference reads.
            IconPackageManager.RegistryChanged += (_, __) =>
            {
                lock (IconSync)
                {
                    _iconBytes.Clear();
                    _iconVersion++;
                }
            };
        }

        /// <summary>Builds and hands over every phone's snapshot, at most ten
        /// times a second. Runs on the UI thread.</summary>
        public void Tick(InputManager im, IEnumerable<ProfileListItem> profiles)
        {
            long now = Environment.TickCount64;
            if (now - _lastBuildMs < BuildIntervalMs) return;
            _lastBuildMs = now;

            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            List<(UserDevice Ud, WebControllerDevice Web)> phones = null;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud?.Device is WebControllerDevice web && web.IsMenuSurface && ud.IsOnline)
                        (phones ??= new()).Add((ud, web));
            }

            if (phones == null)
            {
                if (_iconTokens.Count > 0) _iconTokens = new Dictionary<string, string>(StringComparer.Ordinal);
                PruneIconBytes(_iconTokens);
                return;
            }

            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            var profileList = BuildProfiles(profiles);
            var feeds = new string[phones.Count];
            for (int i = 0; i < phones.Count; i++)
                feeds[i] = BuildFeed(phones[i].Ud, im, profileList, tokens);
            // Tokens before snapshots: a snapshot on the wire must find its
            // pictures served.
            _iconTokens = tokens;
            PruneIconBytes(tokens);
            for (int i = 0; i < phones.Count; i++)
                phones[i].Web.SetMenusFeed(feeds[i]);
        }

        /// <summary>Drops cached pictures that no current snapshot names.</summary>
        private static void PruneIconBytes(Dictionary<string, string> tokens)
        {
            lock (IconSync)
            {
                if (_iconBytes.Count == 0) return;
                List<string> stale = null;
                foreach (var reference in _iconBytes.Keys)
                    if (!tokens.ContainsValue(reference)) (stale ??= new()).Add(reference);
                if (stale != null)
                    foreach (var reference in stale) _iconBytes.Remove(reference);
            }
        }

        internal string BuildFeed(UserDevice ud, InputManager im, List<FeedProfile> profiles,
            Dictionary<string, string> tokens)
        {
            var feed = new Feed
            {
                Device = ud.Device?.Name ?? "",
                Profiles = profiles,
                Profile = ActiveProfile(profiles),
            };

            // The slots this phone is on, read under the settings lock the
            // engine's own lookup takes.
            var slots = new SortedSet<int>();
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                int n = settings.FindByInstanceGuid(ud.InstanceGuid, _slotBuffer);
                for (int i = 0; i < n; i++)
                {
                    int slot = _slotBuffer[i]?.MapTo ?? -1;
                    if (slot >= 0 && slot < InputManager.MaxPads) slots.Add(slot);
                }
                Array.Clear(_slotBuffer);
            }
            if (slots.Count == 0)
            {
                feed.State = "unassigned";
                return JsonSerializer.Serialize(feed, JsonOptions);
            }
            // Named even when no page shows, so the page can say which slot
            // needs a marked menu.
            feed.Slots = new List<int>(slots);

            var sets = SettingsManager.SlotMappingSets;
            string deviceGuid = ud.InstanceGuidString;
            foreach (int slot in slots)
            {
                var set = sets != null && slot < sets.Length ? sets[slot] : null;
                var menus = set?.Menus;
                if (menus == null) continue;
                // Defensive index walk: the editor changes this list.
                for (int i = 0; i < menus.Count; i++)
                {
                    MenuDefinitionEntry def;
                    try { def = menus[i]; } catch { break; }
                    // The engine's own rule, so the page never offers a tile
                    // the engine refuses.
                    if (!InputManager.IsWebMenuFireable(def, deviceGuid)
                        || !InputManager.IsMenuLayerOpen(slot, set, def)) continue;
                    feed.Pages.Add(BuildPage(slot, def, im, tokens));
                }
            }
            feed.State = feed.Pages.Count == 0 ? "empty" : "ok";
            return JsonSerializer.Serialize(feed, JsonOptions);
        }

        private static FeedPage BuildPage(int slot, MenuDefinitionEntry def, InputManager im,
            Dictionary<string, string> tokens)
        {
            int count = Math.Clamp(def.CellCount, 0, MaxCells);
            var page = new FeedPage
            {
                Slot = slot,
                Menu = def.MenuId,
                Name = def.Name ?? "",
                Count = count,
                Labels = def.ShowLabels,
            };

            // One item per index, the last one winning, as the overlay binds
            // them (MenuOverlayWindow.BoundItems).
            var bound = new SortedDictionary<int, MenuItemDefinition>();
            var items = def.Items;
            if (items != null)
            {
                for (int k = 0; k < items.Count; k++)
                {
                    MenuItemDefinition item;
                    try { item = items[k]; } catch { break; }
                    if (item != null && item.Index >= 0 && item.Index < count) bound[item.Index] = item;
                }
            }
            foreach (var (index, item) in bound)
            {
                var cell = new FeedCell { I = index, Label = item.Label ?? "" };
                string icon = item.Icon ?? "";
                if (MenuIconResolver.IsGlyph(icon))
                    cell.Glyph = icon;
                else if (icon.Length > 0 && MenuIconResolver.Resolve(icon) != null)
                {
                    string token = Token(icon);
                    tokens[token] = icon;
                    cell.Img = "/api/menuicon?t=" + token;
                }
                cell.On = ToggleState(im, slot, item.MacroName);
                page.Cells.Add(cell);
            }
            return page;
        }

        /// <summary>Whether the cell's macro is a Toggle that is on, or null
        /// when the cell names no Toggle macro. The first macro of that name
        /// wins, the runtime's own lookup (CollectMenuDirectOutputs). The state
        /// is the macro's, whatever turned it on.</summary>
        internal static bool? ToggleState(InputManager im, int slot, string macroName)
        {
            if (string.IsNullOrEmpty(macroName) || im == null || slot < 0 || slot >= im.MacroSnapshots.Length)
                return null;
            var macros = im.MacroSnapshots[slot];
            if (macros == null) return null;
            for (int m = 0; m < macros.Length; m++)
            {
                var mac = macros[m];
                if (mac != null && string.Equals(mac.Name, macroName, StringComparison.OrdinalIgnoreCase))
                    return mac.TriggerMode == MacroTriggerMode.Toggle ? mac.ToggleTriggerLatched : null;
            }
            return null;
        }

        internal static List<FeedProfile> BuildProfiles(IEnumerable<ProfileListItem> items)
        {
            var list = new List<FeedProfile>();
            if (items != null)
                foreach (var p in items)
                    if (p != null && !string.IsNullOrEmpty(p.Id))
                        list.Add(new FeedProfile { Id = p.Id, Name = p.Name ?? "" });
            return list;
        }

        private static FeedProfile ActiveProfile(List<FeedProfile> profiles)
        {
            string id = SettingsManager.ActiveProfileId;
            if (string.IsNullOrEmpty(id)) id = ProfileListItem.DefaultProfileId;
            return profiles.Find(p => p.Id == id) ?? new FeedProfile { Id = id, Name = "" };
        }

        /// <summary>An opaque, stable token for an icon reference: the first
        /// eight bytes of its SHA-256, so the page can keep a picture it has
        /// already loaded and the reference itself never leaves the PC.</summary>
        internal static string Token(string reference)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>The picture a current snapshot names by
        /// <paramref name="token"/>, with its type read from its first bytes.
        /// False for a token no snapshot names and for bytes that are not
        /// one of the image formats icons take (PNG, JPEG, GIF, BMP).</summary>
        internal static bool TryGetIcon(string token, out byte[] bytes, out string contentType)
        {
            bytes = null;
            contentType = null;
            if (string.IsNullOrEmpty(token) || token.Length > 64) return false;
            if (!_iconTokens.TryGetValue(token, out string reference)) return false;
            int version;
            lock (IconSync)
            {
                if (_iconBytes.TryGetValue(reference, out var hit))
                {
                    bytes = hit.Bytes;
                    contentType = hit.Type;
                    return true;
                }
                version = _iconVersion;
            }
            // The file is read outside the lock. Only a picture is kept, so a
            // file that appears or turns valid later is still found, and only
            // when no registry change landed during the read.
            byte[] read = MenuIconResolver.TryReadIconBytes(reference);
            string type = SniffImageType(read);
            if (type == null) return false;
            lock (IconSync)
            {
                if (version == _iconVersion) _iconBytes[reference] = (read, type);
            }
            bytes = read;
            contentType = type;
            return true;
        }

        internal static string SniffImageType(byte[] b)
        {
            if (b == null || b.Length < 4) return null;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "image/gif";
            if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
            return null;
        }

        /// <summary>Test seam: the tokens the last build published.</summary>
        internal static void SetTokensForTest(Dictionary<string, string> tokens) => _iconTokens = tokens;

        internal sealed class Feed
        {
            public string Type { get; set; } = "menus";
            public string Device { get; set; }
            public string State { get; set; }
            public FeedProfile Profile { get; set; }
            public List<FeedProfile> Profiles { get; set; }
            public List<int> Slots { get; set; }
            public List<FeedPage> Pages { get; } = new();
        }

        internal sealed class FeedProfile
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        internal sealed class FeedPage
        {
            public int Slot { get; set; }
            public int Menu { get; set; }
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Labels { get; set; }
            public List<FeedCell> Cells { get; } = new();
        }

        internal sealed class FeedCell
        {
            public int I { get; set; }
            public string Label { get; set; }
            public string Glyph { get; set; }
            public string Img { get; set; }
            public bool? On { get; set; }
        }
    }
}
