using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Press depth of every key an analog keyboard reports as moving, one
    /// device's worth (issue #468). Null on <see cref="CustomInputState"/> for
    /// any other device, the <see cref="MidiInputState"/> cost model.
    ///
    /// <para>Sparse on purpose. Every protocol PadForge reads reports only the
    /// keys that are down (a Wooting report carries at most 16, a Huntsman V3
    /// Pro 15), so the state carries those and nothing else. A dense array over
    /// the whole key code space would be copied and compared at the poll rate
    /// for a keyboard with two keys down.</para>
    ///
    /// <para>Key codes follow the AnalogSense convention the references share:
    /// the HID usage on the keyboard page (0x04 = A) for ordinary keys, and a
    /// namespace in the high byte for the rest, the way Wooting's analog
    /// interface packs them (0x3xx consumer keys, 0x4xx Fn and the vendor
    /// keys). A depth is 0 at rest and 1 at the bottom of the press, and an
    /// entry exists only while its depth is above 0.</para>
    ///
    /// <para>Mapping descriptor: <c>"Analog Key N"</c>, N the decimal code.</para>
    /// </summary>
    public sealed class AnalogKeyInputState
    {
        /// <summary>Most keys one state carries. A pushed report holds at most
        /// 16 keys, but the routes that read the whole matrix report every
        /// key above rest, so a full state keeps the deepest keys: a new key
        /// deeper than the shallowest entry takes its place.</summary>
        public const int MaxKeys = 64;

        /// <summary>Upper bound of the key code space: Wooting's namespace
        /// byte reaches 6 (advanced keys), so codes stay below 0x700.</summary>
        public const int CodeCount = 0x700;

        /// <summary>Number of entries in use.</summary>
        public int Count;

        /// <summary>Key code per entry, valid below <see cref="Count"/>.</summary>
        public int[] Codes;

        /// <summary>Depth per entry, 0 exclusive to 1 inclusive, valid below
        /// <see cref="Count"/>.</summary>
        public float[] Depths;

        /// <summary>When a mapping last read each key, Environment.TickCount
        /// by key code (0 for never), or null. The device row owns the array
        /// and every copy of its state carries it, so a mapping read on any
        /// copy marks the key as one a mapping uses.</summary>
        public int[] ReadStamps;

        public AnalogKeyInputState()
        {
            Codes = new int[MaxKeys];
            Depths = new float[MaxKeys];
        }

        /// <summary>Depth of <paramref name="code"/>, 0 when the key is up.</summary>
        public float Get(int code)
        {
            for (int i = 0; i < Count; i++)
                if (Codes[i] == code) return Depths[i];
            return 0f;
        }

        /// <summary><see cref="Get"/> for a mapping: also stamps the key in
        /// <see cref="ReadStamps"/> when the state carries them.</summary>
        public float Read(int code)
        {
            var stamps = ReadStamps;
            if (stamps != null && code > 0 && code < stamps.Length)
                stamps[code] = Environment.TickCount | 1;
            return Get(code);
        }

        /// <summary>Records a key's depth, replacing an earlier entry for the
        /// same code. A depth at or below 0 removes the key, and a depth past 1
        /// is clamped. In a full state the key takes the place of the
        /// shallowest entry when it is deeper. Returns false only when the key
        /// was dropped for being no deeper than every entry of a full
        /// state.</summary>
        public bool Set(int code, float depth)
        {
            if (code <= 0 || code >= CodeCount) return true;
            if (float.IsNaN(depth) || depth <= 0f)
            {
                Remove(code);
                return true;
            }
            if (depth > 1f) depth = 1f;
            for (int i = 0; i < Count; i++)
            {
                if (Codes[i] != code) continue;
                Depths[i] = depth;
                return true;
            }
            if (Count >= MaxKeys)
            {
                int shallowest = 0;
                for (int i = 1; i < Count; i++)
                    if (Depths[i] < Depths[shallowest]) shallowest = i;
                if (depth <= Depths[shallowest]) return false;
                Codes[shallowest] = code;
                Depths[shallowest] = depth;
                return true;
            }
            Codes[Count] = code;
            Depths[Count] = depth;
            Count++;
            return true;
        }

        /// <summary>Drops <paramref name="code"/>, keeping the rest in order.</summary>
        public void Remove(int code)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Codes[i] != code) continue;
                int tail = Count - i - 1;
                if (tail > 0)
                {
                    Array.Copy(Codes, i + 1, Codes, i, tail);
                    Array.Copy(Depths, i + 1, Depths, i, tail);
                }
                Count--;
                Codes[Count] = 0;
                Depths[Count] = 0f;
                return;
            }
        }

        public AnalogKeyInputState Clone()
        {
            var clone = new AnalogKeyInputState();
            CopyInto(clone);
            return clone;
        }

        /// <summary>Deep copy into <paramref name="dst"/>. The whole arrays
        /// travel, not only the used part, so a copy equals its source field
        /// for field. <see cref="ReadStamps"/> travels by reference: it is the
        /// device's.</summary>
        public void CopyInto(AnalogKeyInputState dst)
        {
            dst.Count = Count;
            Array.Copy(Codes, dst.Codes, MaxKeys);
            Array.Copy(Depths, dst.Depths, MaxKeys);
            dst.ReadStamps = ReadStamps;
        }

        /// <summary>Fresh-constructed state without dropping the arrays. The
        /// read stamps detach too: the device row attaches its own to every
        /// state it hands out.</summary>
        public void ResetForReuse()
        {
            Count = 0;
            Array.Clear(Codes, 0, MaxKeys);
            Array.Clear(Depths, 0, MaxKeys);
            ReadStamps = null;
        }

        /// <summary>True when both states hold the same keys at the same depths,
        /// in any order.</summary>
        public bool SameAs(AnalogKeyInputState other)
        {
            if (other == null) return Count == 0;
            if (other.Count != Count) return false;
            for (int i = 0; i < Count; i++)
                if (other.Get(Codes[i]) != Depths[i]) return false;
            return true;
        }
    }
}
