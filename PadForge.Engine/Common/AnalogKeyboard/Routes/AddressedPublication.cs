using System;

namespace PadForge.Engine.Common.AnalogKeyboard.Routes
{
    /// <summary>
    /// HallJoy's per-key publication (physical_analog_state.h:10-67,
    /// AGPL-3.0), which the Addressed and HERO sessions report through. Each
    /// physical key (an 8-bit key ID or HERO position) is bound once to the
    /// code it reports as, and keeps its latest depth in thousandths and the
    /// time it arrived. A code reads as the deepest of its keys whose sample
    /// is fresh, so two keys bound to one code do not release each other, and
    /// a key that stops being sampled drops out once its sample is older than
    /// the freshness window.
    /// </summary>
    public sealed class AddressedPublication
    {
        /// <summary>Codes below this are published (physical_analog_state.h:7).</summary>
        public const int CodeLimit = 0x410;

        private readonly int[] _codes = new int[256];
        private readonly ushort[] _milli = new ushort[256];
        private readonly long[] _stampMs = new long[256];
        private readonly bool[] _stamped = new bool[256];

        public AddressedPublication(long freshMs) => FreshMs = freshMs;

        /// <summary>How long a sample stays current: 500 ms on the Addressed
        /// route (physical_analog_state.h:39), 750 ms on HERO
        /// (aula_hero84he_backend.cpp:39).</summary>
        public long FreshMs { get; }

        /// <summary>Binds a key to a code, once per key. False for key 0, a
        /// code of 0 or past the limit, or a key already bound (Bind,
        /// physical_analog_state.h:24-30).</summary>
        public bool Bind(int keyId, int code)
        {
            if (keyId <= 0 || keyId > 255 || code <= 0 || code >= CodeLimit || _codes[keyId] != 0) return false;
            _codes[keyId] = code;
            return true;
        }

        /// <summary>The code a key is bound to, 0 when unbound.</summary>
        public int CodeOf(int keyId) => keyId > 0 && keyId < 256 ? _codes[keyId] : 0;

        /// <summary>Records a bound key's depth, clamped to 1000, and its time.
        /// False for an unbound key, which HallJoy does not publish
        /// (Publish, physical_analog_state.h:31-37).</summary>
        public bool Publish(int keyId, ushort milli, long nowMs)
        {
            if (keyId <= 0 || keyId > 255 || _codes[keyId] == 0) return false;
            _milli[keyId] = Math.Min(milli, (ushort)1000);
            _stampMs[keyId] = nowMs;
            _stamped[keyId] = true;
            return true;
        }

        private bool IsFresh(int keyId, long nowMs)
            => _stamped[keyId] && nowMs >= _stampMs[keyId] && nowMs - _stampMs[keyId] <= FreshMs;

        /// <summary>The deepest fresh key bound to a code, and whether any
        /// bound key is fresh (Read, physical_analog_state.h:39-52).</summary>
        public (bool Fresh, ushort Milli) Read(int code, long nowMs)
        {
            bool fresh = false;
            ushort milli = 0;
            if (code <= 0 || code >= CodeLimit) return (false, 0);
            for (int keyId = 1; keyId < 256; keyId++)
            {
                if (_codes[keyId] != code || !IsFresh(keyId, nowMs)) continue;
                fresh = true;
                milli = Math.Max(milli, _milli[keyId]);
            }
            return (fresh, milli);
        }

        /// <summary>Writes every code with a fresh, nonzero depth into
        /// <paramref name="output"/>, replacing it.</summary>
        public void Fill(AnalogKeyInputState output, long nowMs)
        {
            output.ResetForReuse();
            for (int keyId = 1; keyId < 256; keyId++)
            {
                int code = _codes[keyId];
                if (code == 0 || _milli[keyId] == 0 || !IsFresh(keyId, nowMs)) continue;
                float depth = _milli[keyId] / 1000f;
                if (depth > output.Get(code)) output.Set(code, depth);
            }
        }
    }
}
