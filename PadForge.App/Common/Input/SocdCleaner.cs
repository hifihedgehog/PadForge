using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// SOCD (simultaneous opposite cardinal directions) cleaner for the
    /// keyboard virtual controller (discussion #205, Snap Tap). Applies a
    /// per-pair bitset transform to the frame's logical key state BEFORE
    /// change detection, so suppress and re-press both fall out of the
    /// existing ProcessKeyWord transitions.
    ///
    /// Semantics follow hitboxer (windows.jai low_level_keyboard_proc):
    /// OPPOSITE = LastWins (press B while A held releases A; releasing B
    /// re-presses the still-held A), NEUTRAL = both suppressed while both
    /// held. FirstWins is the mirror of LastWins with the earlier press
    /// keeping the win.
    /// </summary>
    internal sealed class SocdCleaner
    {
        internal enum SocdMode
        {
            Off,
            LastWins,
            Neutral,
            FirstWins,
        }

        // Winner values for the per-pair state machine.
        internal const byte WinNone = 0;
        internal const byte WinA = 1;
        internal const byte WinB = 2;

        private struct Pair
        {
            public byte VkA;
            public byte VkB;
        }

        private struct PairState
        {
            public bool PrevA;
            public bool PrevB;
            public byte Winner;
        }

        private SocdMode _mode = SocdMode.Off;
        private Pair[] _pairs = Array.Empty<Pair>();
        private PairState[] _states = Array.Empty<PairState>();

        // Last-configured strings so Configure is a no-op (and per-pair
        // state survives) when the caller re-sends identical config.
        private string _configuredMode;
        private string _configuredPairs;

        /// <summary>
        /// Parses the persisted mode name ("Off" / "LastWins" / "Neutral" /
        /// "FirstWins") and pipe-separated "vkA:vkB" pair list. Unknown mode
        /// names and malformed / self-referencing pairs are dropped. Content-
        /// identical reconfiguration keeps the per-pair state (a held winner
        /// is not forgotten by the 30Hz settings sync).
        /// </summary>
        public void Configure(string mode, string pairs)
        {
            if (string.Equals(mode, _configuredMode, StringComparison.Ordinal)
                && string.Equals(pairs, _configuredPairs, StringComparison.Ordinal))
                return;
            _configuredMode = mode;
            _configuredPairs = pairs;

            _mode = mode switch
            {
                "LastWins" => SocdMode.LastWins,
                "Neutral" => SocdMode.Neutral,
                "FirstWins" => SocdMode.FirstWins,
                _ => SocdMode.Off,
            };
            _pairs = ParsePairs(pairs);
            _states = new PairState[_pairs.Length];
        }

        /// <summary>Forgets held-key tracking. Called on VC Connect beside
        /// the _prevKeys reset so a reconnect starts from a clean slate.</summary>
        public void Reset()
        {
            if (_states.Length != 0)
                Array.Clear(_states);
        }

        /// <summary>
        /// Transforms the frame's logical key bitset in place. No-op unless a
        /// mode is active and at least one pair is configured. Pair reads and
        /// prev tracking use a snapshot of the PHYSICAL (pre-transform) bits,
        /// so pairs sharing a key stay independent of application order.
        /// </summary>
        public void Apply(ref ulong k0, ref ulong k1, ref ulong k2, ref ulong k3)
        {
            if (_mode == SocdMode.Off || _pairs.Length == 0) return;

            ulong p0 = k0, p1 = k1, p2 = k2, p3 = k3;
            for (int i = 0; i < _pairs.Length; i++)
            {
                bool a = GetBit(p0, p1, p2, p3, _pairs[i].VkA);
                bool b = GetBit(p0, p1, p2, p3, _pairs[i].VkB);

                StepPair(_mode, a, b, _states[i].PrevA, _states[i].PrevB,
                    ref _states[i].Winner, out bool suppressA, out bool suppressB);
                _states[i].PrevA = a;
                _states[i].PrevB = b;

                if (suppressA) ClearBit(ref k0, ref k1, ref k2, ref k3, _pairs[i].VkA);
                if (suppressB) ClearBit(ref k0, ref k1, ref k2, ref k3, _pairs[i].VkB);
            }
        }

        /// <summary>
        /// Pure per-pair step. Inputs are the pair's PHYSICAL states this
        /// frame and last frame plus the remembered winner; outputs are which
        /// sides to suppress this frame and the updated winner.
        /// </summary>
        internal static void StepPair(SocdMode mode, bool a, bool b,
            bool prevA, bool prevB, ref byte winner,
            out bool suppressA, out bool suppressB)
        {
            suppressA = false;
            suppressB = false;

            if (!a || !b)
            {
                // Zero or one key down: it passes through untouched. When the
                // later key releases, the surviving key owns the win, so the
                // change-detected re-press (hitboxer's "Re-pressing opposite",
                // windows.jai:211-228) happens in the caller for free.
                winner = a ? WinA : b ? WinB : WinNone;
                return;
            }

            if (mode == SocdMode.Neutral)
            {
                // hitboxer NEUTRAL (windows.jai:203-206): both held cancels out.
                suppressA = true;
                suppressB = true;
                winner = WinNone;
                return;
            }

            bool newA = !prevA;
            bool newB = !prevB;
            if (mode == SocdMode.LastWins)
            {
                // Most recent press wins (hitboxer OPPOSITE, windows.jai:180-198).
                // Both new the same frame: B, deterministic. Neither new with no
                // remembered winner (mode enabled mid-hold): B, deterministic.
                if (newB) winner = WinB;
                else if (newA) winner = WinA;
                else if (winner == WinNone) winner = WinB;
            }
            else
            {
                // FirstWins: the earlier press keeps the win. Both new the
                // same frame: A, deterministic. A remembered winner persists;
                // otherwise the non-new side was down first.
                if (newA && newB) winner = WinA;
                else if (winner == WinNone) winner = newB ? WinA : newA ? WinB : WinA;
            }

            suppressA = winner != WinA;
            suppressB = winner != WinB;
        }

        private static Pair[] ParsePairs(string pairs)
        {
            if (string.IsNullOrEmpty(pairs)) return Array.Empty<Pair>();
            var tokens = pairs.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<Pair>(tokens.Length);
            foreach (var token in tokens)
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;
                if (!int.TryParse(token.AsSpan(0, colon), out int vkA)) continue;
                if (!int.TryParse(token.AsSpan(colon + 1), out int vkB)) continue;
                if (vkA is < 1 or > 255 || vkB is < 1 or > 255 || vkA == vkB) continue;
                list.Add(new Pair { VkA = (byte)vkA, VkB = (byte)vkB });
            }
            return list.ToArray();
        }

        private static bool GetBit(ulong k0, ulong k1, ulong k2, ulong k3, byte vk)
        {
            ulong word = (vk >> 6) switch { 0 => k0, 1 => k1, 2 => k2, _ => k3 };
            return (word & (1UL << (vk & 63))) != 0;
        }

        private static void ClearBit(ref ulong k0, ref ulong k1, ref ulong k2, ref ulong k3, byte vk)
        {
            ulong mask = ~(1UL << (vk & 63));
            switch (vk >> 6)
            {
                case 0: k0 &= mask; break;
                case 1: k1 &= mask; break;
                case 2: k2 &= mask; break;
                default: k3 &= mask; break;
            }
        }
    }
}
