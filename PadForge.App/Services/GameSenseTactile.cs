using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PadForge.Services
{
    /// <summary>
    /// SteelSeries tactile mice (Rival 500, 700 and 710) through the GameSense
    /// server inside SteelSeries GG (#494, asked in discussion #488). Every
    /// rule here is from SteelSeries/gamesense-sdk, cloned beside the other
    /// references:
    ///
    /// <para>sending-game-events.md: the server address is the "address" key
    /// of %PROGRAMDATA%/SteelSeries/SteelSeries Engine 3/coreProps.json, and
    /// no file means the engine is not running. A handler runs when an event
    /// arrives with a new value (register_game_event's value_optional is what
    /// would make a repeated value count, and it defaults to false). A game
    /// deactivates after 15 seconds without events, and game_heartbeat resets
    /// that timer without touching the devices.</para>
    ///
    /// <para>json-handlers-tactile.md and standard-zones.md: device type
    /// "tactile" on zone "one", mode "vibrate", a pattern chosen by value
    /// range, and a rate that repeats it a set number of times a second until
    /// the value changes. A custom pulse runs the motor for its length-ms.
    /// The doc warns that vibrations take time and can queue up, so each
    /// range's pulse ends before its next repeat starts.</para>
    ///
    /// <para>writing-handlers-in-json.md: bind_game_event registers the event
    /// with its handler, and stop_game returns the devices to GG's own
    /// behavior.</para>
    ///
    /// <para>The calls are synchronous with a short timeout, because they run
    /// on the haptics worker beside the Logitech pulses. A local engine
    /// answers in milliseconds.</para>
    /// </summary>
    internal sealed class GameSenseTactile : IDisposable
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
        private string _base;
        private int _level;
        private long _lastPost;

        public GameSenseTactile(string corePropsPath = null, int httpTimeoutMs = DefaultHttpTimeoutMs)
        {
            _corePropsPath = corePropsPath ?? DefaultCorePropsPath;
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(httpTimeoutMs) };
        }

        public bool Connected => _base != null;

        /// <summary>Reads the engine's address and binds the event. False when
        /// GG is not running or refuses.</summary>
        public bool TryConnect(long now)
        {
            string address = ReadAddress(_corePropsPath);
            if (address == null) return false;
            string url = "http://" + address;
            if (!Post(url, "/game_metadata", MetadataBody) || !Post(url, "/bind_game_event", BindBody))
                return false;
            _base = url;
            _level = 0;
            _lastPost = now;
            return true;
        }

        /// <summary>Posts a level change, or a heartbeat while rumble holds.
        /// False when the engine stopped answering, which disconnects.</summary>
        public bool Render(int level, long now)
        {
            if (_base == null) return false;
            string path, body;
            if (level != _level)
            {
                path = "/game_event";
                body = EventBody(LevelValue(level));
            }
            else if (level > 0 && now - _lastPost >= HeartbeatMs)
            {
                path = "/game_heartbeat";
                body = GameBody;
            }
            else
            {
                return true;
            }

            if (!Post(_base, path, body))
            {
                _base = null;
                _level = 0;
                return false;
            }
            _level = level;
            _lastPost = now;
            return true;
        }

        /// <summary>Silences the motor and hands the devices back to GG.</summary>
        public void Close()
        {
            if (_base == null) return;
            if (_level != 0) Post(_base, "/game_event", EventBody(0));
            Post(_base, "/stop_game", GameBody);
            _base = null;
            _level = 0;
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
