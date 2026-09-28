using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// Report parsers for the analog keyboard families that push their state
    /// (issue #468). Pure functions over the bytes Windows hands back from
    /// ReadFile, where byte 0 is the report ID, or 0 when the collection has
    /// none.
    ///
    /// <para>Each parser follows Soup's AnalogueKeyboard.cpp and the
    /// AnalogSense JavaScript SDK, which agree on every family both cover.
    /// The Wooting parsers also follow the Wooting Analog SDK's device.rs on
    /// the one point where it is stricter: every complete entry in the report
    /// is read and a zero entry is skipped rather than ending the list, and
    /// the old-firmware One and Two scale their values by 1.2.</para>
    /// </summary>
    public static class AnalogKeyboardParsers
    {
        /// <summary>Drops the leading 0 Windows puts in front of a report
        /// from a collection without report IDs. A nonzero first byte is a
        /// report ID and stays, which is Soup's receiveReport rule.</summary>
        public static ReadOnlySpan<byte> StripZeroReportId(ReadOnlySpan<byte> raw)
            => raw.Length > 0 && raw[0] == 0 ? raw.Slice(1) : raw;

        /// <summary>Wooting analog interface v1: up to 16 entries of a
        /// big-endian u16 key code and a u8 value. Each report is the whole
        /// set of keys down, so it replaces <paramref name="target"/>. A key
        /// the old firmware repeats keeps its last value (Soup).</summary>
        public static bool ParseWootingV1(ReadOnlySpan<byte> raw, AnalogKeyInputState target, bool legacyFirmware)
        {
            var data = StripZeroReportId(raw);
            target.ResetForReuse();
            for (int i = 0; i + 3 <= data.Length; i += 3)
            {
                int code = (data[i] << 8) | data[i + 1];
                int value = data[i + 2];
                if (code == 0 || value == 0) continue;
                float depth = legacyFirmware ? value * 1.2f / 255f : value / 255f;
                target.Set(code, depth);
            }
            return true;
        }

        /// <summary>Wooting analog interface v2: 4-byte entries of matrix
        /// position, key code low byte, a packed byte (bit 0 actuated, bits 2
        /// to 5 the key namespace, bits 6 and 7 the value's low bits) and the
        /// value's high byte. The value is 10-bit. Replaces
        /// <paramref name="target"/>.</summary>
        public static bool ParseWootingV2(ReadOnlySpan<byte> raw, AnalogKeyInputState target)
        {
            var data = StripZeroReportId(raw);
            target.ResetForReuse();
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                int key = data[i + 1];
                int packed = data[i + 2];
                int ns = (packed >> 2) & 0x0F;
                int value = (data[i + 3] << 2) | ((packed >> 6) & 0x03);
                int code = (ns << 8) | key;
                if (code == 0 || value == 0) continue;
                target.Set(code, value / 1023f);
            }
            return true;
        }

        /// <summary>Razer Huntsman V2 Analog and Mini Analog, input report 7:
        /// pairs of Razer key number and u8 value, ended by key 0. Replaces
        /// <paramref name="target"/>. A report with another ID is not an
        /// analog report and leaves the state alone.</summary>
        public static bool ParseRazerHuntsmanV2(ReadOnlySpan<byte> raw, AnalogKeyInputState target)
        {
            if (raw.Length < 1 || raw[0] != AnalogKeyboardCatalog.RazerHuntsmanV2ReportId) return false;
            var data = raw.Slice(1);
            target.ResetForReuse();
            for (int i = 0; i + 2 <= data.Length; i += 2)
            {
                int razer = data[i];
                if (razer == 0) break;
                int code = AnalogKeyCodes.RazerToCode(razer);
                if (code != 0) target.Set(code, data[i + 1] / 255f);
            }
            return true;
        }

        /// <summary>Razer Huntsman V3 Pro family, input report 11: triples of
        /// Razer key number, u8 value and one byte both references skip.
        /// Replaces <paramref name="target"/>.</summary>
        public static bool ParseRazerHuntsmanV3(ReadOnlySpan<byte> raw, AnalogKeyInputState target)
        {
            if (raw.Length < 1 || raw[0] != AnalogKeyboardCatalog.RazerHuntsmanV3ReportId) return false;
            var data = raw.Slice(1);
            target.ResetForReuse();
            for (int i = 0; i + 3 <= data.Length; i += 3)
            {
                int razer = data[i];
                if (razer == 0) break;
                int code = AnalogKeyCodes.RazerToCode(razer);
                if (code != 0) target.Set(code, data[i + 1] / 255f);
            }
            return true;
        }

        /// <summary>Razer Tartarus Pro, input report 6: 20 bytes, one per
        /// analog key in <see cref="AnalogKeyCodes.TartarusPro"/> order.
        /// The bytes after them are not analog keys. Replaces
        /// <paramref name="target"/>.</summary>
        public static bool ParseRazerTartarusPro(ReadOnlySpan<byte> raw, AnalogKeyInputState target)
        {
            if (raw.Length < 1 || raw[0] != AnalogKeyboardCatalog.RazerTartarusProReportId) return false;
            var data = raw.Slice(1);
            target.ResetForReuse();
            int n = Math.Min(data.Length, AnalogKeyCodes.TartarusPro.Length);
            for (int i = 0; i < n; i++)
                if (data[i] != 0) target.Set(AnalogKeyCodes.TartarusPro[i], data[i] / 255f);
            return true;
        }

        /// <summary>The full-scale travel value of a NuPhy board: 1600 on the
        /// Air75 HE and Air60 HE, 800 on the others (Soup).</summary>
        public static float NuPhyFullScale(ushort productId)
            => productId == 0x6120 || productId == 0xFEE0 ? 1600f : 800f;

        /// <summary>NuPhy HE line: a report of type 0xA0 carries ONE key, a
        /// big-endian u16 key number at byte 2 and a big-endian u16 value at
        /// byte 4, so it updates that key and leaves the others. A value of 0
        /// is the key's release. Soup's reading, the one HallJoy validated on
        /// hardware. AnalogSense.js reads a u8 at byte 7 instead.</summary>
        public static bool ParseNuPhy(ReadOnlySpan<byte> raw, AnalogKeyInputState target, ushort productId)
        {
            var data = StripZeroReportId(raw);
            if (data.Length < 6 || data[0] != 0xA0) return false;
            int nuphy = (data[2] << 8) | data[3];
            int value = (data[4] << 8) | data[5];
            int code = AnalogKeyCodes.NuPhyToCode(nuphy);
            if (code == 0) return true;
            target.Set(code, value / NuPhyFullScale(productId));
            return true;
        }

        /// <summary>Dispatch for the pushed families.</summary>
        public static bool ParsePushed(AnalogKeyboardProtocol protocol, ReadOnlySpan<byte> raw,
            AnalogKeyInputState target, ushort vendorId, ushort productId)
        {
            switch (protocol)
            {
                case AnalogKeyboardProtocol.WootingV1:
                    return ParseWootingV1(raw, target, vendorId == AnalogKeyboardCatalog.LegacyWootingVendorId);
                case AnalogKeyboardProtocol.WootingV2:
                    return ParseWootingV2(raw, target);
                case AnalogKeyboardProtocol.RazerHuntsmanV2:
                    return ParseRazerHuntsmanV2(raw, target);
                case AnalogKeyboardProtocol.RazerHuntsmanV3:
                    return ParseRazerHuntsmanV3(raw, target);
                case AnalogKeyboardProtocol.RazerTartarusPro:
                    return ParseRazerTartarusPro(raw, target);
                case AnalogKeyboardProtocol.NuPhy:
                    return ParseNuPhy(raw, target, productId);
            }
            return false;
        }
    }
}
