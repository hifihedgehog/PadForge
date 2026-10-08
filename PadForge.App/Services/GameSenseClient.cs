using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PadForge.Services
{
    /// <summary>
    /// The GameSense server inside SteelSeries GG (#494, asked in discussion
    /// #488): rumble on the tactile mice (Rival 500, 700 and 710) and a color
    /// per general device type (mouse, keyboard, headset), all in one
    /// PADFORGE game. Every rule here is from SteelSeries/gamesense-sdk,
    /// cloned beside the other references:
    ///
    /// <para>sending-game-events.md: the server address is the "address" key
    /// of %PROGRAMDATA%/SteelSeries/SteelSeries Engine 3/coreProps.json, and
    /// no file means the engine is not running. A handler runs when an event
    /// arrives with a new value, unless the event registers value_optional,
    /// which runs it on every update. A game deactivates after 15 seconds
    /// without events, and game_heartbeat resets that timer without touching
    /// the devices.</para>
    ///
    /// <para>json-handlers-tactile.md and standard-zones.md: device type
    /// "tactile" on zone "one", mode "vibrate", a pattern chosen by value
    /// range, and a rate that repeats it a set number of times a second until
    /// the value changes. A custom pulse runs the motor for its length-ms.
    /// The doc warns that vibrations take time and can queue up, so each
    /// range's pulse ends before its next repeat starts.</para>
    ///
    /// <para>json-handlers-color.md: a "context-color" handler takes its
    /// color from the event's frame under its context-frame-key, so one
    /// event per device type carries any color with no precomputed ranges.
    /// The zones are standard-zones.md's for each general type, plus
    /// rgb-per-key-zones' "all" for a per-key keyboard.</para>
    ///
    /// <para>writing-handlers-in-json.md: bind_game_event registers an event
    /// with its handlers, remove_game_event drops it with its bindings, and
    /// stop_game returns the devices to GG's own behavior. A color event is
    /// bound only while its device type is claimed and removed when it stops
    /// being claimed, so a type PadForge does not light keeps GG's.</para>
    ///
    /// <para>The calls are synchronous with a short timeout, because they run
    /// on the GameSense worker. A local engine answers in milliseconds.</para>
    /// </summary>
    internal sealed class GameSenseClient : IDisposable
    {
        internal const string Game = "PADFORGE";
        internal const string Event = "RUMBLE";

        /// <summary>Well inside the 15 second deactivation timer.</summary>
        internal const int HeartbeatMs = 10000;

        internal const int DefaultHttpTimeoutMs = 500;

        internal static string DefaultCorePropsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

        /// <summary>The name GG shows for the game.</summary>
        internal const string MetadataBody =
            "{\"game\":\"PADFORGE\",\"game_display_name\":\"PadForge\"}";

        /// <summary>The RUMBLE event and its tactile handler. Value 0 plays
        /// nothing and repeats nothing. Each level's pulse ends before the
        /// next repeat: 30 ms of every 200 ms, 55 of every 143 and 80 of
        /// every 100.</summary>
        internal const string BindBody =
            "{\"game\":\"PADFORGE\",\"event\":\"RUMBLE\",\"min_value\":0,\"max_value\":100,"
            + "\"handlers\":[{\"device-type\":\"tactile\",\"zone\":\"one\",\"mode\":\"vibrate\","
            + "\"pattern\":["
            + "{\"low\":0,\"high\":0,\"pattern\":[]},"
            + "{\"low\":1,\"high\":33,\"pattern\":[{\"type\":\"custom\",\"length-ms\":30}]},"
            + "{\"low\":34,\"high\":66,\"pattern\":[{\"type\":\"custom\",\"length-ms\":55}]},"
            + "{\"low\":67,\"high\":100,\"pattern\":[{\"type\":\"custom\",\"length-ms\":80}]}"
            + "],"
            + "\"rate\":{\"frequency\":["
            + "{\"low\":1,\"high\":33,\"frequency\":5},"
            + "{\"low\":34,\"high\":66,\"frequency\":7},"
            + "{\"low\":67,\"high\":100,\"frequency\":10}"
            + "]}}]}";

        internal const string GameBody = "{\"game\":\"PADFORGE\"}";

        /// <summary>The frame key every color handler reads.</summary>
        internal const string ColorKey = "color";

        /// <summary>The zones each general device type is lit on
        /// (standard-zones.md:86-110 and 208-238).</summary>
        internal static (string DeviceType, string Zone)[] ColorZones(string type) => type switch
        {
            "mouse" => new[] { ("mouse", "wheel"), ("mouse", "logo"), ("mouse", "base") },
            "keyboard" => new[]
            {
                ("rgb-per-key-zones", "all"),
                ("keyboard", "main-keyboard"), ("keyboard", "function-keys"), ("keyboard", "keypad"),
                ("keyboard", "number-keys"), ("keyboard", "macro-keys"),
            },
            "headset" => new[] { ("headset", "earcups") },
            _ => Array.Empty<(string, string)>(),
        };

        /// <summary>The event a device type's color travels on. Event names
        /// allow only A to Z, 0 to 9, "-" and "_" (sending-game-events.md).</summary>
        internal static string ColorEvent(string type) => "COLOR_" + type.ToUpperInvariant();

        /// <summary>A device type's color event with a context-color handler
        /// on each of its zones, run on every update.</summary>
        internal static string ColorBindBody(string type)
        {
            var sb = new StringBuilder();
            sb.Append("{\"game\":\"PADFORGE\",\"event\":\"").Append(ColorEvent(type))
              .Append("\",\"min_value\":0,\"max_value\":100,\"value_optional\":true,\"handlers\":[");
            var zones = ColorZones(type);
            for (int i = 0; i < zones.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"device-type\":\"").Append(zones[i].DeviceType)
                  .Append("\",\"zone\":\"").Append(zones[i].Zone)
                  .Append("\",\"mode\":\"context-color\",\"context-frame-key\":\"").Append(ColorKey).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static string ColorEventBody(string type, int rgb)
            => "{\"game\":\"PADFORGE\",\"event\":\"" + ColorEvent(type) + "\",\"data\":{\"value\":100,\"frame\":{\""
               + ColorKey + "\":{\"red\":" + ((rgb >> 16) & 0xFF).ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ",\"green\":" + ((rgb >> 8) & 0xFF).ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ",\"blue\":" + (rgb & 0xFF).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}";

        internal static string RemoveEventBody(string type)
            => "{\"game\":\"PADFORGE\",\"event\":\"" + ColorEvent(type) + "\"}";

        internal static string EventBody(int value)
            => "{\"game\":\"PADFORGE\",\"event\":\"RUMBLE\",\"data\":{\"value\":"
               + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        /// <summary>The value posted for each rumble level, one inside each
        /// of the handler's ranges.</summary>
        internal static int LevelValue(int level) => level switch
        {
            <= 0 => 0,
            1 => 17,
            2 => 50,
            _ => 84,
        };

        private readonly string _corePropsPath;
        private readonly HttpClient _http;
        private readonly Dictionary<string, int> _colors = new();
        private string _base;
        private int _level;
        private long _lastPost;

        public GameSenseClient(string corePropsPath = null, int httpTimeoutMs = DefaultHttpTimeoutMs)
        {
            _corePropsPath = corePropsPath ?? DefaultCorePropsPath;
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs) };
        }

        public bool Connected => _base != null;

        /// <summary>The device types whose color GG holds now.</summary>
        internal IReadOnlyCollection<string> ColorTypes => _colors.Keys;

        /// <summary>Reads the engine's address and binds the rumble event.
        /// False when GG is not running or refuses.</summary>
        public bool TryConnect(long now)
        {
            string address = ReadAddress(_corePropsPath);
            if (address == null) return false;
            string url = "http://" + address;
            if (!Post(url, "/game_metadata", MetadataBody) || !Post(url, "/bind_game_event", BindBody))
                return false;
            // GG keeps a color event bound until it is removed, and a session
            // that dropped or a process that died left its events behind. They
            // go now, so a type nobody claims stays GG's. An event that is not
            // registered answers an error, which changes nothing.
            foreach (var type in PadForge.Common.Input.Peripherals.PeripheralLinker.GameSenseColorTypes)
                Post(url, "/remove_game_event", RemoveEventBody(type));
            _base = url;
            _level = 0;
            _colors.Clear();
            _lastPost = now;
            return true;
        }

        /// <summary>Posts a rumble level change. False when the engine
        /// stopped answering, which disconnects.</summary>
        public bool Render(int level, long now)
        {
            if (_base == null) return false;
            if (level != _level)
            {
                if (!Post(_base, "/game_event", EventBody(LevelValue(level)))) return Drop();
                _level = level;
                _lastPost = now;
            }
            return Heartbeat(now);
        }

        /// <summary>Each claimed device type's color: a type newly claimed is
        /// bound and painted, a changed color is posted, and a type no longer
        /// claimed is removed so GG lights it again. False when the engine
        /// stopped answering, which disconnects.</summary>
        public bool SetColors(IReadOnlyList<KeyValuePair<string, int>> wanted, long now)
        {
            if (_base == null) return false;
            List<string> gone = null;
            foreach (var type in _colors.Keys)
            {
                bool still = false;
                foreach (var pair in wanted)
                    if (pair.Key == type) { still = true; break; }
                if (!still) (gone ??= new List<string>()).Add(type);
            }
            if (gone != null)
            {
                foreach (var type in gone)
                {
                    if (!Post(_base, "/remove_game_event", RemoveEventBody(type))) return Drop();
                    _colors.Remove(type);
                    _lastPost = now;
                }
            }
            foreach (var pair in wanted)
            {
                bool bound = _colors.TryGetValue(pair.Key, out int sent);
                if (bound && sent == pair.Value) continue;
                if (!bound && !Post(_base, "/bind_game_event", ColorBindBody(pair.Key))) return Drop();
                if (!Post(_base, "/game_event", ColorEventBody(pair.Key, pair.Value))) return Drop();
                _colors[pair.Key] = pair.Value;
                _lastPost = now;
            }
            return Heartbeat(now);
        }

        /// <summary>Keeps the game active while rumble or a color holds.</summary>
        private bool Heartbeat(long now)
        {
            if ((_level > 0 || _colors.Count > 0) && now - _lastPost >= HeartbeatMs)
            {
                if (!Post(_base, "/game_heartbeat", GameBody)) return Drop();
                _lastPost = now;
            }
            return true;
        }

        private bool Drop()
        {
            _base = null;
            _level = 0;
            _colors.Clear();
            return false;
        }

        /// <summary>Silences the motor, drops the color events and hands the
        /// devices back to GG.</summary>
        public void Close()
        {
            if (_base == null) return;
            if (_level != 0) Post(_base, "/game_event", EventBody(0));
            foreach (var type in _colors.Keys) Post(_base, "/remove_game_event", RemoveEventBody(type));
            Post(_base, "/stop_game", GameBody);
            _base = null;
            _level = 0;
            _colors.Clear();
        }

        private bool Post(string baseUrl, string path, string body)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                using var response = _http.Send(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        internal static string ReadAddress(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return TryReadAddress(File.ReadAllText(path), out string address) ? address : null;
            }
            catch
            {
                // Locked or half-written while the engine starts: the next
                // look reads it.
                return null;
            }
        }

        /// <summary>The "address" key, "host:port".</summary>
        internal static bool TryReadAddress(string json, out string address)
        {
            address = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("address", out var prop)
                    || prop.ValueKind != JsonValueKind.String)
                    return false;
                string value = prop.GetString()?.Trim();
                if (string.IsNullOrEmpty(value) || value.IndexOf(':') <= 0) return false;
                address = value;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { Close(); } catch { }
            _http.Dispose();
        }
    }
}
