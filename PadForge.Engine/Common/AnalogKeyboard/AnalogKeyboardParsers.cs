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
        /// <paramref name="target"/>. On the boards with split keys the
        /// matrix position also publishes the key's physical alias
        /// (<see cref="WootingSplitKey"/>). Two records with one code, the
        /// halves of a split Space, publish the deeper value, as HallJoy's
        /// plugin host merges them (update_from_keyboard,
        /// UniversalAnalogPluginFixed main.cpp:685-704). The Wooting SDK keeps
        /// the later record instead.</summary>
        public static bool ParseWootingV2(ReadOnlySpan<byte> raw, AnalogKeyInputState target, ushort productId = 0)
        {
            var data = StripZeroReportId(raw);
            target.ResetForReuse();
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                int key = data[i + 1];
                int packed = data[i + 2];
                int ns = (packed >> 2) & 0x0F;
                int value = (data[i + 3] << 2) | ((packed >> 6) & 0x03);
                if (value == 0) continue;
                float depth = value / 1023f;
                int split = WootingSplitKey(productId, data[i]);
                if (split != 0 && depth > target.Get(split)) target.Set(split, depth);
                int code = (ns << 8) | key;
                if (code == 0) continue;
                if (depth > target.Get(code)) target.Set(code, depth);
            }
            return true;
        }

        /// <summary>The physical alias a Wooting v2 matrix position carries,
        /// or 0. HallJoy's halljoy_wooting_physical.h: on the 60HE v2 (0x1340)
        /// and the 80HE+ (0x1410), row 5 column 4 is the left Space half,
        /// column 8 the right half, column 6 the center Fn key, and column 13
        /// on the 60HE v2 or column 12 on the 80HE+ the right Fn key. The
        /// position byte is row in bits 5 to 7 and column in bits 0 to 4.
        /// HallJoy matches the exact product ID. This matches it under the
        /// gamepad-mode mask, because the mode changes the product ID's low
        /// nibble and not the key matrix.</summary>
        public static int WootingSplitKey(ushort productId, int matrixPosition)
        {
            int model = productId & AnalogKeyboardCatalog.WootingPidModeMask;
            if (model != 0x1340 && model != 0x1410) return 0;
            if ((matrixPosition >> 5) != 5) return 0;
            return (matrixPosition & 0x1F) switch
            {
                4 => AnalogKeyCodes.LeftSpace,
                8 => AnalogKeyCodes.RightSpace,
                6 => AnalogKeyCodes.CenterFn,
                12 when model == 0x1410 => AnalogKeyCodes.RightFn,
                13 when model == 0x1340 => AnalogKeyCodes.RightFn,
                _ => 0,
            };
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

        /// <summary>Razer Huntsman V3 family, input report 11: triples of
        /// Razer key number and a big-endian u16 travel, ended by key 0.
        /// Razer's own parser in Synapse Web (parseAnalogADCNotificationEvents,
        /// <c>getUint16</c> over bytes 2 and 3 of the report) reads the value
        /// this way, and its device configs give the full scale as
        /// <c>eventDataSize</c> (<see cref="AnalogKeyboardCatalog.RazerFullScale"/>).
        /// Abbytech's reader decodes the same (key, u16) list. Soup and
        /// AnalogSense.js read the high byte over 255, the same depth with 8
        /// bits of it. Replaces <paramref name="target"/>.</summary>
        public static bool ParseRazerHuntsmanV3(ReadOnlySpan<byte> raw, AnalogKeyInputState target, ushort productId)
        {
            if (raw.Length < 1 || raw[0] != AnalogKeyboardCatalog.RazerHuntsmanV3ReportId) return false;
            var data = raw.Slice(1);
            float fullScale = AnalogKeyboardCatalog.RazerFullScale(productId);
            target.ResetForReuse();
            for (int i = 0; i + 3 <= data.Length; i += 3)
            {
                int razer = data[i];
                if (razer == 0) break;
                int code = AnalogKeyCodes.RazerToCode(razer);
                int travel = (data[i + 1] << 8) | data[i + 2];
                if (code != 0 && travel != 0) target.Set(code, Math.Min(travel / fullScale, 1f));
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

        /// <summary>Dispatch for the pushed families.</summary>
        public static bool ParsePushed(AnalogKeyboardProtocol protocol, ReadOnlySpan<byte> raw,
            AnalogKeyInputState target, ushort vendorId, ushort productId)
        {
            switch (protocol)
            {
                case AnalogKeyboardProtocol.WootingV1:
                    return ParseWootingV1(raw, target, vendorId == AnalogKeyboardCatalog.LegacyWootingVendorId);
                case AnalogKeyboardProtocol.WootingV2:
                    return ParseWootingV2(raw, target, productId);
                case AnalogKeyboardProtocol.RazerHuntsmanV2:
                    return ParseRazerHuntsmanV2(raw, target);
                case AnalogKeyboardProtocol.RazerHuntsmanV3:
                    return ParseRazerHuntsmanV3(raw, target, productId);
                case AnalogKeyboardProtocol.RazerTartarusPro:
                    return ParseRazerTartarusPro(raw, target);
            }
            return false;
        }
    }
}
