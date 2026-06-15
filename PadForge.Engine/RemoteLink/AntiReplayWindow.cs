namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Sliding anti-replay window over a 32-bit datagram sequence number, the
    /// IPsec-style construction (RFC 6479). Sequence comparison uses RFC 1982
    /// serial arithmetic so it survives the 2^32 wrap instead of permanently
    /// rejecting after it. A 64-deep bitmap tolerates that much reordering.
    ///
    /// The transport advances the window only after AEAD verification succeeds,
    /// so a forged sequence can never poison it. Returns false for a duplicate or
    /// a datagram older than the window; true (and records it) otherwise.
    /// </summary>
    public sealed class AntiReplayWindow
    {
        private const int WindowSize = 64;

        private uint _highest;
        private ulong _bitmap;
        private bool _initialized;

        /// <summary>RFC 1982 serial "a is strictly after b" — wrap-safe via the signed diff.</summary>
        public static bool IsAfter(uint a, uint b) => (int)(a - b) > 0;

        /// <summary>
        /// Test <paramref name="seq"/> against the window and, if fresh, record it.
        /// True = accept (newest or an in-window not-yet-seen reorder); false =
        /// duplicate or older than the window.
        /// </summary>
        public bool CheckAndUpdate(uint seq)
        {
            if (!_initialized)
            {
                _initialized = true;
                _highest = seq;
                _bitmap = 1UL;
                return true;
            }

            if (IsAfter(seq, _highest))
            {
                uint shift = seq - _highest; // unsigned distance ahead (wrap-safe)
                _bitmap = shift >= WindowSize ? 1UL : (_bitmap << (int)shift) | 1UL;
                _highest = seq;
                return true;
            }

            uint back = _highest - seq; // distance behind the leading edge (wrap-safe)
            if (back >= WindowSize) return false; // older than the window
            ulong mask = 1UL << (int)back;
            if ((_bitmap & mask) != 0) return false; // already seen
            _bitmap |= mask;
            return true;
        }

        /// <summary>The highest sequence accepted so far (valid once any datagram has been accepted).</summary>
        public uint Highest => _highest;

        /// <summary>True once at least one datagram has been accepted.</summary>
        public bool Initialized => _initialized;
    }
}
