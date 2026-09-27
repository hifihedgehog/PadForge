using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Pads in iCade mode (hifihedgehog/SDL#33 Part 16). Such a pad pairs as a
    /// Bluetooth keyboard and types a letter for each press and another for
    /// each release. No source publishes the IDs those keyboards use, so the
    /// fork decodes one only when SDL_JOYSTICK_ICADE_DEVICES lists its pair
    /// (docs/README-icade.md). The ION iCade cabinet, 15E4:0132, needs no
    /// entry. The user marks the keyboard from its row on the Devices page,
    /// and PadForge keeps the list and writes the hint.
    /// </summary>
    public static class ICadePads
    {
        public const ushort CabinetVendor = 0x15E4;
        public const ushort CabinetProduct = 0x0132;

        /// <summary>The fork decodes at most 32 listed pairs
        /// (SDL_ICADE_MAX_PAIRS in SDL_icade_proto.h).</summary>
        public const int MaxPairs = 32;

        /// <summary>The name the fork gives a listed pad's joystick begins
        /// with this (SDL_icadejoystick.c).</summary>
        public const string JoystickNamePrefix = "iCade Controller (";

        /// <summary>A pair in the hint's own form, "0xVVVV/0xPPPP".</summary>
        public static string Entry(ushort vendor, ushort product) =>
            string.Format(CultureInfo.InvariantCulture, "0x{0:X4}/0x{1:X4}", vendor, product);

        /// <summary>Reads an entry the way the fork's parser does: "0x" and
        /// one to four hex digits on each side of a slash, spaces around the
        /// numbers ignored, vendor 0 refused (SDL_ICade_ParseDevices).</summary>
        public static bool TryParse(string entry, out ushort vendor, out ushort product)
        {
            vendor = product = 0;
            if (string.IsNullOrWhiteSpace(entry)) return false;
            string[] parts = entry.Split('/');
            if (parts.Length != 2) return false;
            if (!TryParseNumber(parts[0].Trim(), out vendor) || !TryParseNumber(parts[1].Trim(), out product))
                return false;
            return vendor != 0;
        }

        private static bool TryParseNumber(string text, out ushort value)
        {
            value = 0;
            if (text.Length < 3 || text.Length > 6 || text[0] != '0' || (text[1] != 'x' && text[1] != 'X'))
                return false;
            return ushort.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The hint's value: each listed pair once, in list order,
        /// unreadable entries dropped, at most <see cref="MaxPairs"/>.</summary>
        public static string HintValue(IEnumerable<string> entries)
        {
            if (entries == null) return string.Empty;
            var seen = new HashSet<uint>();
            var parts = new List<string>();
            foreach (string entry in entries)
            {
                if (!TryParse(entry, out ushort vendor, out ushort product)) continue;
                if (!seen.Add(((uint)vendor << 16) | product)) continue;
                parts.Add(Entry(vendor, product));
                if (parts.Count == MaxPairs) break;
            }
            return string.Join(",", parts);
        }

        /// <summary>Whether the list names this pair.</summary>
        public static bool Lists(IEnumerable<string> entries, ushort vendor, ushort product)
        {
            if (entries == null) return false;
            foreach (string entry in entries)
                if (TryParse(entry, out ushort v, out ushort p) && v == vendor && p == product)
                    return true;
            return false;
        }

        /// <summary>A keyboard the user can mark as a pad in iCade mode: one
        /// on a Bluetooth link, where these pads pair, with IDs the fork can
        /// match, and not the cabinet, which the fork decodes already.</summary>
        public static bool CanMark(bool isKeyboard, bool bluetooth, ushort vendor, ushort product) =>
            isKeyboard && bluetooth && vendor != 0 && !(vendor == CabinetVendor && product == CabinetProduct);
    }
}
