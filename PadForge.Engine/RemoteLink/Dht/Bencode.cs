using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// Bencode codec for the mainline DHT KRPC protocol (#294 presence store).
    ///
    /// THE TRAP this exists to avoid (Codex adjudication trap #1): bencode
    /// strings are BYTE strings, not text. The BEP 44 signature is computed over
    /// the exact bencoded preimage, and the target is SHA1 over raw key bytes, so
    /// any UTF-8 round-trip corrupts both. Every string here is
    /// <see cref="byte"/>[] and the encoder emits length-prefixed raw bytes. A
    /// dictionary's keys are sorted by RAW BYTE order, which bencode requires and
    /// which the signature/token checks depend on.
    ///
    /// Supported value types (all KRPC needs): byte[] (string), long (i...e),
    /// List&lt;object&gt;, and SortedDictionary&lt;string, object&gt; where the
    /// string key is ASCII (KRPC keys are all ASCII: "id", "target", "k", ...).
    /// </summary>
    public static class Bencode
    {
        public static byte[] Encode(object value)
        {
            using var ms = new MemoryStream();
            EncodeTo(ms, value);
            return ms.ToArray();
        }

        private static void EncodeTo(Stream s, object value)
        {
            switch (value)
            {
                case byte[] bytes:
                    WriteAscii(s, bytes.Length.ToString());
                    s.WriteByte((byte)':');
                    s.Write(bytes, 0, bytes.Length);
                    break;
                case string str: // convenience: ASCII/UTF-8 to bytes
                    EncodeTo(s, Encoding.UTF8.GetBytes(str));
                    break;
                case long l:
                    s.WriteByte((byte)'i');
                    WriteAscii(s, l.ToString());
                    s.WriteByte((byte)'e');
                    break;
                case int i:
                    EncodeTo(s, (long)i);
                    break;
                case IReadOnlyList<object> list:
                    s.WriteByte((byte)'l');
                    foreach (var item in list) EncodeTo(s, item);
                    s.WriteByte((byte)'e');
                    break;
                case IDictionary<string, object> dict:
                    s.WriteByte((byte)'d');
                    // Keys MUST be emitted sorted by raw byte order (bencode
                    // spec + the signature/token preimages depend on it).
                    var keys = new List<string>(dict.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (var k in keys)
                    {
                        var kb = Encoding.ASCII.GetBytes(k);
                        WriteAscii(s, kb.Length.ToString());
                        s.WriteByte((byte)':');
                        s.Write(kb, 0, kb.Length);
                        EncodeTo(s, dict[k]);
                    }
                    s.WriteByte((byte)'e');
                    break;
                default:
                    throw new ArgumentException($"Bencode cannot encode {value?.GetType().Name ?? "null"}.");
            }
        }

        private static void WriteAscii(Stream s, string ascii)
        {
            foreach (char c in ascii) s.WriteByte((byte)c);
        }

        public static object Decode(byte[] data)
        {
            int pos = 0;
            var result = DecodeAt(data, ref pos);
            return result;
        }

        /// <summary>Decodes and also reports where parsing stopped, so a caller
        /// can reject trailing garbage.</summary>
        public static object Decode(byte[] data, out int consumed)
        {
            int pos = 0;
            var result = DecodeAt(data, ref pos);
            consumed = pos;
            return result;
        }

        private static object DecodeAt(byte[] data, ref int pos)
        {
            if (pos >= data.Length) throw new FormatException("Bencode: unexpected end.");
            byte c = data[pos];
            if (c == (byte)'i')
            {
                pos++;
                int start = pos;
                while (pos < data.Length && data[pos] != (byte)'e') pos++;
                if (pos >= data.Length) throw new FormatException("Bencode: unterminated integer.");
                var numText = Encoding.ASCII.GetString(data, start, pos - start);
                pos++; // consume 'e'
                if (!long.TryParse(numText, out long val)) throw new FormatException("Bencode: bad integer.");
                return val;
            }
            if (c == (byte)'l')
            {
                pos++;
                var list = new List<object>();
                while (pos < data.Length && data[pos] != (byte)'e')
                    list.Add(DecodeAt(data, ref pos));
                if (pos >= data.Length) throw new FormatException("Bencode: unterminated list.");
                pos++; // consume 'e'
                return list;
            }
            if (c == (byte)'d')
            {
                pos++;
                var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    var keyBytes = DecodeString(data, ref pos);
                    var key = Encoding.ASCII.GetString(keyBytes);
                    var val = DecodeAt(data, ref pos);
                    dict[key] = val;
                }
                if (pos >= data.Length) throw new FormatException("Bencode: unterminated dict.");
                pos++; // consume 'e'
                return dict;
            }
            if (c >= (byte)'0' && c <= (byte)'9')
                return DecodeString(data, ref pos);
            throw new FormatException($"Bencode: unexpected byte 0x{c:X2} at {pos}.");
        }

        private static byte[] DecodeString(byte[] data, ref int pos)
        {
            int colon = pos;
            while (colon < data.Length && data[colon] != (byte)':') colon++;
            if (colon >= data.Length) throw new FormatException("Bencode: unterminated string length.");
            var lenText = Encoding.ASCII.GetString(data, pos, colon - pos);
            if (!int.TryParse(lenText, out int len) || len < 0)
                throw new FormatException("Bencode: bad string length.");
            int start = colon + 1;
            // Subtract, never add: this data comes off the open DHT, and
            // "start + len" overflows to a negative for a length near int.MaxValue,
            // which passes the bounds check and hands Array.Copy a length the
            // buffer cannot satisfy.
            if (start > data.Length || len > data.Length - start)
                throw new FormatException("Bencode: string past end.");
            var bytes = new byte[len];
            Array.Copy(data, start, bytes, 0, len);
            pos = start + len;
            return bytes;
        }

        /// <summary>Helper: fetch a byte[] value from a decoded KRPC dict, or
        /// null if absent/wrong type.</summary>
        public static byte[] GetBytes(object dict, string key)
            => dict is IDictionary<string, object> d && d.TryGetValue(key, out var v) && v is byte[] b ? b : null;

        /// <summary>Helper: fetch a long value, or a default if absent.</summary>
        public static long GetLong(object dict, string key, long dflt = 0)
            => dict is IDictionary<string, object> d && d.TryGetValue(key, out var v) && v is long l ? l : dflt;
    }
}
