using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The MSI Mystic Light SDK worker (#557 item 11). While MSI's Mystic
    /// Light runs (<see cref="VendorPrograms.MsiMysticLight"/>, the process
    /// Aurora waits for before it opens the SDK: Aurora MsiDevice.cs:22-26),
    /// the MSI devices it lights are lit through its SDK, LED by LED.
    ///
    /// <para>Paths. The SDK names a device by a type string and numbers its
    /// LEDs from 0 (MLAPI_GetDeviceInfo, Mystic Light SDK 1.0.0.08 p.3).
    /// RGB.NET lights the three types it knows, MSI_MB, MSI_VGA and
    /// MSI_MOUSE (MsiDeviceProvider.cs:87-109), and each is a path of its
    /// own here. Every other type the SDK reports goes to one more path,
    /// "other", lit through the same documented calls. A path's lights are
    /// its types' LEDs in the order the SDK lists them, one zone each.</para>
    ///
    /// <para>Each LED is set to the "Steady" style once, before its first
    /// color, as RGB.NET sets it so that the LED shows a color it is given
    /// (MsiMainboardRGBDevice.cs:36-41, the same in the graphics card and
    /// mouse devices), and is then colored with MLAPI_SetLedColor
    /// (MsiDeviceUpdateQueue.cs:41-42). Only LEDs whose color changed are
    /// sent again.</para>
    ///
    /// <para>Hand-back. Before it changes an LED the worker reads its style
    /// and color (MLAPI_GetLedStyle, MLAPI_GetLedColor, pp. 4-5), and when
    /// the LED's path is no longer claimed, the program stops or the session
    /// ends, it sets them back, color first and style after, so the effect
    /// the user chose in Mystic Light resumes in its own color. The session
    /// ends with MLAPI_Release (p.4) and the library freed. RGB.NET frees
    /// the library and restores nothing (MsiDeviceProvider.cs:115-126).</para>
    /// </summary>
    internal sealed class MysticLightBackend : SdkCategoryBackend
    {
        internal const string SteadyStyle = "Steady";
        internal const int MlapiOk = 0;

        /// <summary>The paths, in paint order.</summary>
        internal static readonly string[] PathKeys = { "MSI_MB", "MSI_VGA", "MSI_MOUSE", "other" };

        private readonly IMysticLightNative _native;
        private bool _loaded;

        /// <summary>The LEDs of each path, as (type, index), in the order
        /// the SDK lists them.</summary>
        private readonly Dictionary<string, List<(string Type, int Index)>> _leds = new(StringComparer.Ordinal);

        /// <summary>Each LED's style and color before the session changed
        /// it, kept from its first change to its hand-back.</summary>
        private readonly Dictionary<(string Type, int Index), (string Style, int R, int G, int B)> _saved = new();

        public MysticLightBackend() : this(null) { }

        internal MysticLightBackend(IMysticLightNative native, int pollMs = 50, int retryMs = 30000, int stopWaitMs = 3000)
            : base(pollMs, retryMs, stopWaitMs)
        {
            _native = native ?? new MysticLightNative();
        }

        public override OutputFamily Family => OutputFamily.MysticLightType;

        protected override IReadOnlyList<string> Categories => PathKeys;

        protected override string LogName => "MYSTIC LIGHT SDK";

        protected override bool Installed(PeripheralPresence presence) => presence.MsiMysticLightSdk;

        protected override bool ProgramRuns(PeripheralPresence presence)
            => presence.Programs.FirstOf(MysticLightList) != null;

        private static readonly IReadOnlyList<VendorProgram> MysticLightList = new[] { VendorPrograms.MsiMysticLight };

        /// <summary>The path an SDK type is lit through.</summary>
        internal static string PathOfType(string type)
            => Array.IndexOf(PathKeys, type) >= 0 && type != "other" ? type : "other";

        protected override LightMap StaticMap(string category) => null;

        protected override bool Open(out string detail)
        {
            _leds.Clear();
            _saved.Clear();
            if (!_native.TryLoad(out detail)) return false;
            _loaded = true;
            int status = _native.Initialize();
            if (status != MlapiOk)
            {
                detail = "MLAPI_Initialize answered " + status.ToString(CultureInfo.InvariantCulture);
                Unload();
                return false;
            }
            status = _native.GetDeviceInfo(out string[] types, out string[] counts);
            if (status != MlapiOk || types == null || counts == null || types.Length != counts.Length)
            {
                detail = "MLAPI_GetDeviceInfo answered " + status.ToString(CultureInfo.InvariantCulture);
                ReleaseAndUnload();
                return false;
            }
            foreach (string key in PathKeys) _leds[key] = new List<(string, int)>();
            for (int i = 0; i < types.Length; i++)
            {
                if (string.IsNullOrEmpty(types[i])) continue;
                if (!int.TryParse(counts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
                    continue;
                var list = _leds[PathOfType(types[i])];
                for (int led = 0; led < count; led++) list.Add((types[i], led));
            }
            detail = types.Length.ToString(CultureInfo.InvariantCulture) + " device types";
            return true;
        }

        protected override LightMap SessionMap(string category)
        {
            if (!_leds.TryGetValue(category, out var leds) || leds.Count < 2) return null;
            var lights = new LightInfo[leds.Count];
            for (int i = 0; i < leds.Count; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return new LightMap(lights, "MSI Mystic Light SDK, " + category + " LEDs");
        }

        protected override bool Paint(string category, SdkPicture picture, SdkPicture last)
        {
            if (!_leds.TryGetValue(category, out var leds)) return true;
            for (int i = 0; i < leds.Count; i++)
            {
                var led = leds[i];
                int rgb = picture.ColorAt(i);
                bool first = !_saved.ContainsKey(led);
                if (!first && last != null && last.ColorAt(i) == rgb) continue;
                if (first)
                {
                    // The LED's own style and color, for its hand-back,
                    // before anything of PadForge's reaches it.
                    if (_native.GetLedStyle(led.Type, led.Index, out string style) != MlapiOk
                        || _native.GetLedColor(led.Type, led.Index, out int r, out int g, out int b) != MlapiOk)
                        return false;
                    _saved[led] = (style, r, g, b);
                    if (_native.SetLedStyle(led.Type, led.Index, SteadyStyle) != MlapiOk) return false;
                }
                if (_native.SetLedColor(led.Type, led.Index, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) != MlapiOk)
                    return false;
            }
            return true;
        }

        /// <summary>A path nobody claims any more: each of its LEDs gets its
        /// own color and style back.</summary>
        protected override void Release(string category)
        {
            if (!_leds.TryGetValue(category, out var leds)) return;
            foreach (var led in leds) Restore(led);
        }

        private void Restore((string Type, int Index) led)
        {
            if (!_saved.TryGetValue(led, out var saved)) return;
            _saved.Remove(led);
            try
            {
                _native.SetLedColor(led.Type, led.Index, saved.R, saved.G, saved.B);
                if (!string.IsNullOrEmpty(saved.Style)) _native.SetLedStyle(led.Type, led.Index, saved.Style);
            }
            catch { }
        }

        protected override void Close()
        {
            foreach (var led in new List<(string, int)>(_saved.Keys)) Restore(led);
            _leds.Clear();
            ReleaseAndUnload();
        }

        private void ReleaseAndUnload()
        {
            try { _native.Release(); } catch { }
            Unload();
        }

        private void Unload()
        {
            if (!_loaded) return;
            _loaded = false;
            try { _native.Unload(); } catch { }
        }
    }
}
