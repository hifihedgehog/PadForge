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

        /// <summary>The unit's light map, or null for a unit that is not a
        /// keyboard lit through 0x8081 or that reports no zone.</summary>
        internal static LightMap Of(HidppUnit unit)
        {
            if (unit == null || !unit.IsKeyboardKind || unit.PerKeyFeatureId != HidppUnitProtocol.PerKeyLightingV2
                || unit.PerKeyZones.Length == 0)
                return null;
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
