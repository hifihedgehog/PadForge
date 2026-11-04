using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lights of a Logitech keyboard with the per-key feature 0x8081
    /// (#557), from the zones it reports present. The feature numbers its
    /// zones from "an array that contains all RGB Zones, which is known by
    /// both FW and SW for every product and every keyboard layout"
    /// (Logitech's x8081 document, p.1), and the references read the
    /// numbers alike: zone N is the key whose HID usage is N + 3, from A at
    /// zone 1 to the Application key at zone 98, the eight modifiers are
    /// zones 104 to 111 (usages 0xE0 to 0xE7), the brightness key and the
    /// media keys 153 and 155 to 158, G1 to G9 180 to 188, and the logo 210
    /// (Solaar special_keys.py KEYCODES, OpenRGB
    /// RGBController_LogitechHIDPP20.cpp hidpp20_key_name_to_zone and
    /// hidpp20_extras_names, which add G6 to G9 from the G915 X).
    ///
    /// <para>A board reports zones it does not have. Zones 99 to 103, Power,
    /// keypad = and F13 to F15, are on no board (OpenRGB: "No current board
    /// stuffs them"), and a board reports the region key of the other
    /// region: a G515 reports 47 and 97 on an ANSI board (Solaar layout.py:
    /// "G515 reports 47, 97, 99-103, 254"), and every keyleds region file,
    /// read from Logitech's boards, lists the other region's key as
    /// spurious. The region comes from feature 0x4540's layout code
    /// (<see cref="KeysOf"/>), and the region keys a board does not have are
    /// left out, as OpenRGB and Solaar leave them out.</para>
    ///
    /// <para>The PRO X TKL RAPID numbers its five media keys its own way and
    /// wires none of the usual extras, so it has a table of its own, found
    /// by the model ID feature 0x0003 lists (OpenRGB proxrapid_top_strip and
    /// its known_kb_zone_policies entry, Solaar keyboard_pro_x_rapid.py).
    /// The G915 X on its cable uses the usual numbers (OpenRGB
    /// g915x_strip_and_gkeys).</para>
    ///
    /// <para>A zone no reference names becomes a key light with no key yet,
    /// which Map Key Lights asks about. OpenRGB shows such a zone for the
    /// same reason, so a board nobody has studied can still be lit key by
    /// key.</para>
    ///
    /// <para>A keyboard of the G810 class, with the per-key feature 0x8080,
    /// lists its keys itself, by type (<see cref="HidppUnitProbe.KeyTypes"/>):
    /// a key's ID on the keys type is its HID usage, a media key's is its
    /// consumer usage, a G-key's is its number, and the indicators are the
    /// backlight key, the Game Mode key and the three lock lights, 1 to 5
    /// (keyleds layout files: zone 64 codes 1 Light, 2 Game, 3 CapsLockLed,
    /// 4 ScrollLockLed, 5 NumLockLed, zone 2 B5 Next, B6 Prev, B7 Stop, CD
    /// Play, E2 Mute). Its list holds the same region keys of the other
    /// region, and the JIS and Korean language keys on every board
    /// (set_keys_colors.pcapng lists 87 to 8B on a board whose keyleds
    /// files put them on the Japanese layout alone, and 90 and 91 on the
    /// Korean), so they are left out the same way. Its other types list
    /// lights a board may not have either: the boards OpenRGB has studied
    /// keep only the extras its tables place, and the rest it treats as
    /// phantoms, the G910's media keys among them, which light nothing
    /// (RGBController_LogitechHIDPP20.cpp known_kb_8080_strips). A board
    /// no reference has studied keeps every light it lists.</para>
    /// </summary>
    internal static class HidppKeyMap
    {
        /// <summary>The region keys: the ANSI backslash (usage 0x31, zone
        /// 46), the ISO # key (0x32, zone 47) and the ISO key left of Z
        /// (0x64, zone 97).</summary>
        [Flags]
        internal enum RegionKeys
        {
            None = 0,
            Backslash = 1,
            Pound = 2,
            LeftOfZ = 4,
        }

        /// <summary>The PRO X TKL RAPID's USB product ID, the model ID its
        /// 0x0003 answer lists (OpenRGB known_kb_layout_edits, 0xC35B).</summary>
        internal const ushort ProXTklRapid = 0xC35B;

        // The lights beyond the main block of each studied 0x8080 board, as
        // addresses (AddressOf): OpenRGB's strips, with the G810's and the
        // G610's logo, five indicators and five media keys, the G512's two
        // indicators, the G910's G1 to G9, logo and nameplate, and the G
        // Pro's logo and four indicators. The G410 lights its first two
        // indicators alone (OpenRGB HIDPP20_QUIRK_8080_KEYTYPE40_ID12_ONLY,
        // keyleds' G410 layout files).
        private static readonly int[] G810Extras =
        {
            0x1001, 0x4001, 0x4002, 0x4003, 0x4004, 0x4005, 0x02B5, 0x02B6, 0x02B7, 0x02CD, 0x02E2,
        };
        private static readonly int[] G512Extras = { 0x4002, 0x4003 };
        private static readonly int[] G910Extras =
        {
            0x0401, 0x0402, 0x0403, 0x0404, 0x0405, 0x0406, 0x0407, 0x0408, 0x0409, 0x1001, 0x1002,
        };
        private static readonly int[] GProExtras = { 0x1001, 0x4001, 0x4002, 0x4003, 0x4004 };
        private static readonly int[] G410Extras = { 0x4001, 0x4002 };

        /// <summary>Each studied board's extras, by the model ID its 0x0003
        /// answer lists: the G810 (0xC331, 0xC337), G610 (0xC333, 0xC338),
        /// G512 (0xC342, 0xC33C), G910 (0xC32B, 0xC335) and G Pro (0xC339)
        /// as OpenRGB's known_kb_8080_strips keys them, and the G410
        /// (0xC330) as its quirk table does.</summary>
        private static readonly Dictionary<ushort, int[]> s_extras = new()
        {
            [0xC331] = G810Extras, [0xC337] = G810Extras, [0xC333] = G810Extras, [0xC338] = G810Extras,
            [0xC342] = G512Extras, [0xC33C] = G512Extras,
            [0xC32B] = G910Extras, [0xC335] = G910Extras,
            [0xC339] = GProExtras,
            [0xC330] = G410Extras,
        };

        /// <summary>The extras of the studied board a unit is, or null for a
        /// board no reference has studied.</summary>
        internal static int[] ExtrasOf(HidppUnit unit)
        {
            foreach (ushort model in unit.ModelIds)
                if (s_extras.TryGetValue(model, out var extras)) return extras;
            return null;
        }

        /// <summary>
        /// The region keys a board with layout code <paramref name="code"/>
        /// has. Logitech's keyboards say which keys each code has: the
        /// keyleds layout files, read from the boards, carry the backslash on
        /// US (1), Korean (9) and Chinese (0x0B), the # key and the key left
        /// of Z on International (2), UK (3), German (4), French (5), Nordic
        /// (8), Swiss (0x0D), Turkish (0x0E) and Spanish (0x0F), and the #
        /// key alone on Russian (7) and Japanese (0x0A). OpenRGB reads 0x11,
        /// 0x14, 0x16, 0x19, 0x1A, 0x1D, 0x1F, 0x21, 0x24, 0x37 and 0x41 as
        /// ISO and 0x38 as ABNT2, which has both ISO keys
        /// (hidpp20_derive_keyboard_layout). Any other code, and a board
        /// with no 0x4540, reads as ANSI, the default of OpenRGB and Solaar
        /// both.
        /// </summary>
        internal static RegionKeys KeysOf(int code) => code switch
        {
            0x02 or 0x03 or 0x04 or 0x05 or 0x08 or 0x0D or 0x0E or 0x0F
                or 0x11 or 0x14 or 0x16 or 0x19 or 0x1A or 0x1D or 0x1F or 0x21 or 0x24 or 0x37 or 0x38 or 0x41
                => RegionKeys.Pound | RegionKeys.LeftOfZ,
            0x07 or 0x0A => RegionKeys.Pound,
            _ => RegionKeys.Backslash,
        };

        /// <summary>The usage on the Keyboard/Keypad page of the key at
        /// <paramref name="zone"/>, or 0 for a zone that is no key the
        /// references name.</summary>
        internal static ushort UsageOf(int zone) => zone switch
        {
            >= 1 and <= 98 => (ushort)(zone + 3),
            >= 104 and <= 111 => (ushort)(zone + 0x78),
            _ => 0,
        };

        /// <summary>The unit's light map: a keyboard's keys through 0x8081
        /// zones or 0x8080 key types, another device's 0x8081 zones where it
        /// reports two or more (<see cref="OfDeviceZones"/>), else
        /// null.</summary>
        internal static LightMap Of(HidppUnit unit)
        {
            if (unit == null) return null;
            if (!unit.IsKeyboardKind)
                return unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLightingV2 && unit.PerKeyZones.Length > 1
                    ? OfDeviceZones(unit)
                    : null;
            if (unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLightingV2 && unit.PerKeyZones.Length > 0) return OfZones(unit);
            if (unit.PerKeyFeatureId == HidppUnitProtocol.PerKeyLighting && unit.KeyTypes.Length > 0) return OfKeyTypes(unit);
            return null;
        }

        /// <summary>A mouse's or another device's 0x8081 zones, each a zone
        /// at its zone ID, numbered in the order the device reports them.
        /// No reference names where a mouse's zones sit: OpenRGB lists them
        /// by ID alone (LogitechHIDPP20Controller.cpp:3022-3084), and its
        /// G203 LIGHTSYNC controller names three LEDs of one zone and no
        /// more (RGBController_LogitechG203L.cpp:117-123).</summary>
        private static LightMap OfDeviceZones(HidppUnit unit)
        {
            var lights = new List<LightInfo>(unit.PerKeyZones.Length);
            int number = 0;
            foreach (byte zone in unit.PerKeyZones)
                if (zone is >= 1 and <= 254)
                    lights.Add(new LightInfo(zone, LightKind.Zone, 0, LightKeySource.None) { Zone = ++number });
            return new LightMap(lights, "HID++ 0x8081") { Product = ProductOf(unit) };
        }

        private static LightMap OfZones(HidppUnit unit)
        {
            bool rapid = Array.IndexOf(unit.ModelIds, ProXTklRapid) >= 0;
            var region = KeysOf(unit.KeyboardLayout);
            var lights = new List<LightInfo>(unit.PerKeyZones.Length);
            foreach (byte zone in unit.PerKeyZones)
                if (LightOf(zone, region, rapid) is LightInfo light) lights.Add(light);
            return new LightMap(lights, "HID++ 0x8081") { Product = ProductOf(unit) };
        }

        /// <summary>The light at <paramref name="zone"/>, or null for a zone
        /// the board reports and does not have.</summary>
        internal static LightInfo? LightOf(int zone, RegionKeys region, bool proXTklRapid)
        {
            ushort usage = UsageOf(zone);
            if (usage != 0)
                return Has(usage, region) ? new LightInfo(zone, LightKind.Key, usage, LightKeySource.Vendor) : null;
            if (zone is < 1 or > 253 or (>= 99 and <= 103)) return null;
            if (proXTklRapid)
                return zone switch
                {
                    150 => Named(zone, LightLabel.Brightness),
                    152 => Named(zone, LightLabel.PlayPause),
                    153 => Named(zone, LightLabel.Mute),
                    154 => Named(zone, LightLabel.Next),
                    155 => Named(zone, LightLabel.Previous),
                    _ => null,
                };
            return zone switch
            {
                153 => Named(zone, LightLabel.Brightness),
                155 => Named(zone, LightLabel.PlayPause),
                156 => Named(zone, LightLabel.Mute),
                157 => Named(zone, LightLabel.Next),
                158 => Named(zone, LightLabel.Previous),
                >= 180 and <= 188 => Named(zone, LightLabel.GKey) with { LabelNumber = zone - 179 },
                210 => new LightInfo(zone, LightKind.Logo, 0, LightKeySource.None),
                _ => new LightInfo(zone, LightKind.Key, 0, LightKeySource.None),
            };
        }

        private static LightInfo Named(int zone, LightLabel label)
            => new(zone, LightKind.Other, 0, LightKeySource.Vendor) { Label = label };

        private static bool Has(ushort usage, RegionKeys region) => usage switch
        {
            0x31 => (region & RegionKeys.Backslash) != 0,
            0x32 => (region & RegionKeys.Pound) != 0,
            0x64 => (region & RegionKeys.LeftOfZ) != 0,
            _ => true,
        };

        /// <summary>
        /// The zones of a HID++ device painted zone by zone (#557): its
        /// 0x8070 zones or 0x8071 clusters, two or more, each at its own
        /// index. The zone a device places at the logo is the logo, and
        /// every other is numbered in the device's order, since the location
        /// codes name no more than the primary zone, the logo and the two
        /// sides (libratbag hidpp20.h hidpp20_color_led_location, OpenRGB
        /// zone_location_name). Null for a device with fewer than two.
        /// </summary>
        internal static LightMap Zones(HidppUnit unit)
        {
            if (unit == null || unit.Zones.Length < 2) return null;
            var lights = new List<LightInfo>(unit.Zones.Length);
            int number = 0;
            for (int i = 0; i < unit.Zones.Length; i++)
            {
                byte zone = unit.Zones[i].Zone;
                ushort location = i < unit.ZoneLocations.Length ? unit.ZoneLocations[i] : (ushort)0;
                lights.Add(location == LogoLocation
                    ? new LightInfo(zone, LightKind.Logo, 0, LightKeySource.None)
                    : new LightInfo(zone, LightKind.Zone, 0, LightKeySource.None) { Zone = ++number });
            }
            return new LightMap(lights, "HID++ zones");
        }

        /// <summary>The location code of a logo zone or cluster.</summary>
        internal const ushort LogoLocation = 2;

        /// <summary>The address of key <paramref name="id"/> of a 0x8080 key
        /// type: the type's bit above the ID, so no two lights of a board
        /// share one.</summary>
        internal static int AddressOf(ushort type, byte id) => (type << 8) | id;

        /// <summary>The key type and the ID at a 0x8080 address.</summary>
        internal static (ushort Type, byte Id) KeyAt(int address) => ((ushort)(address >> 8), (byte)address);

        private static LightMap OfKeyTypes(HidppUnit unit)
        {
            var extras = ExtrasOf(unit);
            var lights = new List<LightInfo>();
            foreach (var type in unit.KeyTypes)
                foreach (byte id in type.Keys)
                    if (KeyLightOf(type.Type, id, unit.KeyboardLayout, extras) is LightInfo light) lights.Add(light);
            return new LightMap(lights, "HID++ 0x8080") { Product = ProductOf(unit) };
        }

        /// <summary>The light of key <paramref name="id"/> of a 0x8080 key
        /// type, or null for a key the board lists and does not have. A
        /// studied board's <paramref name="extras"/> say which lights beyond
        /// the main block it has.</summary>
        internal static LightInfo? KeyLightOf(ushort type, byte id, int layoutCode, int[] extras)
        {
            int address = AddressOf(type, id);
            if (type == HidppUnitProtocol.KeyTypeKeys)
            {
                if (!Has(id, KeysOf(layoutCode)) || !HasLanguageKey(id, layoutCode)) return null;
                return new LightInfo(address, LightKind.Key, id, LightKeySource.Device);
            }
            if (extras != null && Array.IndexOf(extras, address) < 0) return null;
            switch (type)
            {
                case HidppUnitProtocol.KeyTypeMedia:
                    return id switch
                    {
                        0xB5 => Named(address, LightLabel.Next),
                        0xB6 => Named(address, LightLabel.Previous),
                        0xB7 => Named(address, LightLabel.Stop),
                        0xCD => Named(address, LightLabel.PlayPause),
                        0xE2 => Named(address, LightLabel.Mute),
                        _ => new LightInfo(address, LightKind.Other, 0, LightKeySource.None),
                    };
                case HidppUnitProtocol.KeyTypeGKeys:
                    return Named(address, LightLabel.GKey) with { LabelNumber = id };
                case HidppUnitProtocol.KeyTypeLogo:
                    return new LightInfo(address, LightKind.Logo, 0, LightKeySource.None);
                case HidppUnitProtocol.KeyTypeIndicators:
                    var status = new LightInfo(address, LightKind.Status, 0, LightKeySource.Vendor);
                    return id switch
                    {
                        1 => status with { Label = LightLabel.Brightness },
                        2 => status with { Label = LightLabel.GameMode },
                        3 => status with { Label = LightLabel.CapsLock },
                        4 => status with { Label = LightLabel.ScrollLock },
                        5 => status with { Label = LightLabel.NumLock },
                        _ => status with { KeySource = LightKeySource.None },
                    };
                default:
                    return new LightInfo(address, LightKind.Other, 0, LightKeySource.None);
            }
        }

        /// <summary>Whether a board with layout code
        /// <paramref name="code"/> has the language key at
        /// <paramref name="usage"/>: the five JIS keys (0x87 to 0x8B) are on
        /// the Japanese layout alone and the two Korean keys (0x90, 0x91) on
        /// the Korean, as the keyleds layout files carry them.</summary>
        private static bool HasLanguageKey(ushort usage, int code) => usage switch
        {
            >= 0x87 and <= 0x8B => code == 0x0A,
            0x90 or 0x91 => code == 0x09,
            _ => true,
        };

        /// <summary>The model the names Map Key Lights learns are kept
        /// under: the first model ID the board's 0x0003 answer lists, the
        /// same on every transport, else its name, and its layout code, since
        /// a model's regions have different keys. Null for a board with
        /// neither.</summary>
        internal static string ProductOf(HidppUnit unit)
        {
            string model = unit.ModelIds.Length > 0
                ? unit.ModelIds[0].ToString("X4", CultureInfo.InvariantCulture)
                : unit.Name;
            if (string.IsNullOrWhiteSpace(model)) return null;
            return "hidpp:" + model.Trim() + ":" + unit.KeyboardLayout.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
