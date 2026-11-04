using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lights of each GameSense color type and the JSON that colors them
    /// one by one (#557). Every rule is from SteelSeries/gamesense-sdk, doc/api:
    ///
    /// <para>json-handlers-color.md:96-107: a handler on a per-key keyboard
    /// can name its keys by "custom-zone-keys", "an array of the HID codes of
    /// the keys in the zone". Lines 223-260: a "context-color" handler takes
    /// its color from the key of the event's frame its "context-frame-key"
    /// names. So one event carries the whole picture: a handler per light,
    /// each reading its own frame key, and every post a frame with each
    /// light's color.</para>
    ///
    /// <para>The keyboard's keys are the codes SteelSeries Apex firmware
    /// takes, the HID usages of OpenRGB's keys[] (<see cref="SteelSeriesApexLayouts.Keys"/>),
    /// with 0xF0, the Fn key, as RGB.NET names it to GameSense
    /// ("fn" 240, RGB.NET.Devices.SteelSeries/API/SteelSeriesSDK.cs:50-53).
    /// 0xFB, play and pause, is no HID code a reference sends GameSense, and
    /// stays out. The logo is rgb-per-key-zones' "logo" zone
    /// (standard-zones.md:216). The five zones of the rgb-5-zone keyboards
    /// (the Apex 150 and 300, standard-zones.md:33) ride along, since no
    /// per-key board is of that type. The general "keyboard" zones are left
    /// out of this form: the Apex M800 is of that type too
    /// (standard-zones.md:7-8), and json-handlers-color.md:109 warns that
    /// zones that overlap on one device confuse it.</para>
    ///
    /// <para>The mouse's lights are the general type's three zones, wheel,
    /// logo and base (standard-zones.md:82-86). The rgb-N-zone types are not
    /// used for it: rgb-2-zone covers Rival mice, Arctis headsets and QCK
    /// Prism mousepads at once (standard-zones.md:29), so a mouse's colors
    /// there would land on a headset too. The headset has one zone,
    /// earcups, and stays one color.</para>
    ///
    /// <para>No other zone-count type is closer for the keyboards that are
    /// neither per-key nor five-zone, or for the mice with numbered zones,
    /// since every such type a handler names reaches every device of that
    /// count, whatever its kind. rgb-3-zone covers the MSI 3 Zone keyboard
    /// and the Sensei Wireless mouse at once (standard-zones.md:31), and
    /// RGB.NET puts the Rival 3 and the Aerox mice on it too (RGB.NET
    /// 3c5af72 RGB.NET.Devices.SteelSeries/SteelSeriesDeviceProvider.cs:48-50
    /// and 64-71), so a keyboard's zones there would light those mice. The
    /// MSI 3 Zone keyboard itself, 1770:FF00 (OpenRGB
    /// MSI3ZoneControllerDetect.cpp:17-18), is no SteelSeries device, so no
    /// GameSense path reaches its row: the shared worker lights it
    /// (<see cref="Msi3ZoneLighting"/>). rgb-8-zone holds only
    /// the Rival 600 and 650 (standard-zones.md:35, SteelSeriesDeviceProvider.cs:60-62),
    /// but the general mouse type's wheel and logo zones are on the same
    /// mice, and two handlers on one light confuse the user
    /// (json-handlers-color.md:109).</para>
    /// </summary>
    internal static class GameSenseLights
    {
        /// <summary>Where a light's color goes: the handler's device type,
        /// its fixed zone or its one custom key, and its frame key.</summary>
        internal readonly record struct Handler(string DeviceType, string Zone, int CustomKey, string FrameKey);

        /// <summary>The keyboard logo's address, past every key code.</summary>
        internal const int LogoAddress = 0x100;

        /// <summary>The first rgb-5-zone zone's address.</summary>
        internal const int ZoneBase = 0x200;

        private static readonly string[] ZoneNames = { "one", "two", "three", "four", "five" };

        private static readonly Lazy<Dictionary<string, (LightMap Map, Handler[] Handlers)>> s_types = new(Build);

        /// <summary>The lights of a color type, or null for one painted one
        /// color.</summary>
        internal static LightMap MapOf(string type)
            => type != null && s_types.Value.TryGetValue(type, out var entry) ? entry.Map : null;

        /// <summary>The handler of each light of a type's map, in map order.</summary>
        internal static IReadOnlyList<Handler> HandlersOf(string type)
            => type != null && s_types.Value.TryGetValue(type, out var entry) ? entry.Handlers : Array.Empty<Handler>();

        /// <summary>Every type with lights, for the worker to publish.</summary>
        internal static IEnumerable<KeyValuePair<string, LightMap>> Maps
        {
            get
            {
                foreach (var pair in s_types.Value)
                    yield return new KeyValuePair<string, LightMap>(pair.Key, pair.Value.Map);
            }
        }

        private static Dictionary<string, (LightMap, Handler[])> Build()
        {
            var keyboard = new List<(LightInfo, Handler)>();
            foreach (byte code in SteelSeriesApexLayouts.Keys)
            {
                if (code == SteelSeriesApexLayouts.PlayPauseCode) continue;
                keyboard.Add((SteelSeriesApexLayouts.Light(code),
                    new Handler("rgb-per-key-zones", null, code, "key" + code.ToString(CultureInfo.InvariantCulture))));
            }
            keyboard.Add((new LightInfo(LogoAddress, LightKind.Logo, 0, LightKeySource.None),
                new Handler("rgb-per-key-zones", "logo", 0, "logo")));
            for (int i = 0; i < ZoneNames.Length; i++)
                keyboard.Add((new LightInfo(ZoneBase + i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 },
                    new Handler("rgb-5-zone", ZoneNames[i], 0, "zone" + (i + 1).ToString(CultureInfo.InvariantCulture))));
            keyboard.Sort((a, b) => a.Item1.Address.CompareTo(b.Item1.Address));

            var mouse = new List<(LightInfo, Handler)>
            {
                (new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 }, new Handler("mouse", "wheel", 0, "wheel")),
                (new LightInfo(1, LightKind.Logo, 0, LightKeySource.None), new Handler("mouse", "logo", 0, "logo")),
                (new LightInfo(2, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 }, new Handler("mouse", "base", 0, "base")),
            };

            return new Dictionary<string, (LightMap, Handler[])>(StringComparer.Ordinal)
            {
                ["keyboard"] = Entry(keyboard, "gamesense-sdk rgb-per-key-zones, OpenRGB SteelSeries Apex key codes"),
                ["mouse"] = Entry(mouse, "gamesense-sdk mouse zones"),
            };
        }

        private static (LightMap, Handler[]) Entry(List<(LightInfo Light, Handler Handler)> pairs, string source)
        {
            var lights = new LightInfo[pairs.Count];
            var handlers = new Handler[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                lights[i] = pairs[i].Light;
                handlers[i] = pairs[i].Handler;
            }
            return (new LightMap(lights, source), handlers);
        }

        /// <summary>The event a type's light colors travel on, beside its
        /// one-color event, so changing forms removes one event and binds
        /// the other.</summary>
        internal static string Event(string type) => "LIGHTS_" + type.ToUpperInvariant();

        /// <summary>The type's light event with a context-color handler per
        /// light, run on every update.</summary>
        internal static string BindBody(string game, string type)
        {
            var sb = new StringBuilder(16384);
            sb.Append("{\"game\":\"").Append(game).Append("\",\"event\":\"").Append(Event(type))
              .Append("\",\"min_value\":0,\"max_value\":100,\"value_optional\":true,\"handlers\":[");
            var handlers = HandlersOf(type);
            for (int i = 0; i < handlers.Count; i++)
            {
                var h = handlers[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"device-type\":\"").Append(h.DeviceType).Append('"');
                if (h.Zone != null) sb.Append(",\"zone\":\"").Append(h.Zone).Append('"');
                else sb.Append(",\"custom-zone-keys\":[").Append(h.CustomKey.ToString(CultureInfo.InvariantCulture)).Append(']');
                sb.Append(",\"mode\":\"context-color\",\"context-frame-key\":\"").Append(h.FrameKey).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>One post of the type's light event: each light's color
        /// under its frame key, from <paramref name="colors"/> in map
        /// order.</summary>
        internal static string EventBody(string game, string type, int[] colors)
        {
            var handlers = HandlersOf(type);
            var sb = new StringBuilder(8192);
            sb.Append("{\"game\":\"").Append(game).Append("\",\"event\":\"").Append(Event(type))
              .Append("\",\"data\":{\"value\":100,\"frame\":{");
            for (int i = 0; i < handlers.Count; i++)
            {
                if (i > 0) sb.Append(',');
                int rgb = colors[i];
                sb.Append('"').Append(handlers[i].FrameKey).Append("\":{\"red\":")
                  .Append(((rgb >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"green\":").Append(((rgb >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"blue\":").Append((rgb & 0xFF).ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("}}}");
            return sb.ToString();
        }
    }
}
