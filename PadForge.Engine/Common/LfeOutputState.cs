namespace PadForge.Engine.Common
{
    /// <summary>
    /// Packed per-slot LFE voice state for the rumble-to-audio feature
    /// (issue #236). Four unipolar ushort voices packed into one long so
    /// the poll thread can publish the whole evaluated pack with a single
    /// <c>Volatile.Write</c> and the audio render thread can read it with
    /// a single <c>Volatile.Read</c>. No locks, no torn reads, no
    /// allocation on either side.
    ///
    /// <para>Bit layout: bits 0-15 = low voice (left/heavy body motor),
    /// 16-31 = high voice (right/light body motor), 32-47 = left trigger
    /// voice, 48-63 = right trigger voice. Each voice is the game-authored
    /// magnitude 0..65535; the renderer maps it to carrier amplitude.</para>
    /// </summary>
    public static class LfeOutputState
    {
        /// <summary>Packs the four voice magnitudes into one long.</summary>
        public static long Pack(ushort low, ushort high, ushort triggerLeft, ushort triggerRight)
        {
            return (long)low
                 | ((long)high << 16)
                 | ((long)triggerLeft << 32)
                 | ((long)triggerRight << 48);
        }

        /// <summary>Unpacks the low (heavy body motor) voice.</summary>
        public static ushort Low(long packed) => (ushort)(packed & 0xFFFF);

        /// <summary>Unpacks the high (light body motor) voice.</summary>
        public static ushort High(long packed) => (ushort)((packed >> 16) & 0xFFFF);

        /// <summary>Unpacks the left trigger voice.</summary>
        public static ushort TriggerLeft(long packed) => (ushort)((packed >> 32) & 0xFFFF);

        /// <summary>Unpacks the right trigger voice.</summary>
        public static ushort TriggerRight(long packed) => (ushort)((packed >> 48) & 0xFFFF);

        /// <summary>Unpacks the voice selected by <paramref name="voice"/>
        /// (0 = low, 1 = high, 2 = trigger left, 3 = trigger right).
        /// Out-of-range indices read 0.</summary>
        public static ushort Voice(long packed, int voice)
        {
            return voice switch
            {
                0 => Low(packed),
                1 => High(packed),
                2 => TriggerLeft(packed),
                3 => TriggerRight(packed),
                _ => (ushort)0,
            };
        }
    }
}
