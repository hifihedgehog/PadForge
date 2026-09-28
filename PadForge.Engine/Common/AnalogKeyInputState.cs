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
        /// <summary>Most keys one state carries. Past it, further keys in the
        /// same report are dropped, which no reference protocol reaches: the
        /// largest report any of them sends holds 16 keys.</summary>
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

        /// <summary>Records a key's depth, replacing an earlier entry for the
        /// same code. A depth at or below 0 removes the key, and a depth past 1
        /// is clamped. Returns false only when the state is full.</summary>
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
            if (Count >= MaxKeys) return false;
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
        /// for field.</summary>
        public void CopyInto(AnalogKeyInputState dst)
        {
            dst.Count = Count;
            Array.Copy(Codes, dst.Codes, MaxKeys);
            Array.Copy(Depths, dst.Depths, MaxKeys);
        }

        /// <summary>Fresh-constructed state without dropping the arrays.</summary>
        public void ResetForReuse()
        {
            Count = 0;
            Array.Clear(Codes, 0, MaxKeys);
            Array.Clear(Depths, 0, MaxKeys);
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
