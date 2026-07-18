using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// SOCD-style pair cleaning for VIRTUAL CONTROLLER buttons (discussion
    /// #240, extending #205's keyboard Snap Tap to gamepad slots). The
    /// fighting-stick ask: with two paired buttons held, one wins, and
    /// releasing the winner re-presses the still-held partner the same
    /// frame, so alternating attacks never pass through a both-released
    /// gap. Applies to the slot's FINAL combined output right before the
    /// Step 5 submit, so physical, mapped, and macro contributions are all
    /// cleaned uniformly.
    ///
    /// <para>Pair semantics are EXACTLY <see cref="SocdCleaner.StepPair"/>
    /// (the hitboxer-grounded state machine #205 shipped): OPPOSITE =
    /// LastWins, NEUTRAL = both suppressed, FirstWins = the mirror. This
    /// class only adapts the bit surface: Xbox/PlayStation slots pair the
    /// Gamepad button MASKS (the WriteBoolTarget vocabulary), Extended
    /// custom slots pair flat raw button indices (word*32 + bit).</para>
    ///
    /// <para>Config lives on the slot's MappingSet (SocdMode + SocdPairs),
    /// so it rides profiles, Copy From Slot, and the export container like
    /// every other slot-scoped output-shaping structure. Pair grammar:
    /// pipe-separated "NameA:NameB" using mapping TARGET names for
    /// Gamepad slots ("ButtonA", "LeftShoulder", "DPadUp", ...) and
    /// "N" flat indices for Extended slots.</para>
    /// </summary>
    internal sealed class SlotButtonSocd
    {
        private struct Pair
        {
            public ushort MaskA;   // Gamepad surface (0 when extended)
            public ushort MaskB;
            public int IndexA;     // Extended surface (-1 when gamepad)
            public int IndexB;
        }

        private struct PairState
        {
            public bool PrevA;
            public bool PrevB;
            public byte Winner;
        }

        private SocdCleaner.SocdMode _mode = SocdCleaner.SocdMode.Off;
        private Pair[] _pairs = Array.Empty<Pair>();
        private PairState[] _states = Array.Empty<PairState>();

        private string _configuredMode;
        private string _configuredPairs;
        private bool _configuredExtended;

        /// <summary>Parses the persisted mode + pair strings. Identical
        /// reconfiguration is a no-op that keeps per-pair state (a held
        /// winner survives the per-tick refresh), the SocdCleaner
        /// contract. Unknown modes and malformed / self-referencing /
        /// unresolvable pairs are dropped.</summary>
        public void Configure(string mode, string pairs, bool extendedIndices)
        {
            if (string.Equals(mode, _configuredMode, StringComparison.Ordinal)
                && string.Equals(pairs, _configuredPairs, StringComparison.Ordinal)
                && extendedIndices == _configuredExtended)
                return;
            _configuredMode = mode;
            _configuredPairs = pairs;
            _configuredExtended = extendedIndices;

            _mode = mode switch
            {
                "LastWins" => SocdCleaner.SocdMode.LastWins,
                "Neutral" => SocdCleaner.SocdMode.Neutral,
                "FirstWins" => SocdCleaner.SocdMode.FirstWins,
                _ => SocdCleaner.SocdMode.Off,
            };
            _pairs = ParsePairs(pairs, extendedIndices);
            _states = new PairState[_pairs.Length];
        }

        /// <summary>Forgets held-button tracking (slot destroy / profile
        /// transitions), mirroring SocdCleaner.Reset.</summary>
        public void Reset()
        {
            if (_states.Length != 0)
                Array.Clear(_states);
        }

        /// <summary>True when a mode and at least one pair are active.</summary>
        public bool IsActive => _mode != SocdCleaner.SocdMode.Off && _pairs.Length > 0;

        /// <summary>Cleans the Gamepad button bitmask in place. Reads use
        /// a snapshot of the pre-transform bits so pairs sharing a button
        /// stay independent of application order.</summary>
        public ushort ApplyGamepad(ushort buttons)
        {
            if (!IsActive) return buttons;
            ushort physical = buttons;
            for (int i = 0; i < _pairs.Length; i++)
            {
                if (_pairs[i].MaskA == 0 || _pairs[i].MaskB == 0) continue;
                bool a = (physical & _pairs[i].MaskA) != 0;
                bool b = (physical & _pairs[i].MaskB) != 0;
                SocdCleaner.StepPair(_mode, a, b, _states[i].PrevA, _states[i].PrevB,
                    ref _states[i].Winner, out bool suppressA, out bool suppressB);
                _states[i].PrevA = a;
                _states[i].PrevB = b;
                if (suppressA) buttons = (ushort)(buttons & ~_pairs[i].MaskA);
                if (suppressB) buttons = (ushort)(buttons & ~_pairs[i].MaskB);
            }
            return buttons;
        }

        /// <summary>Cleans an Extended slot's raw button words in place
        /// (flat index = word*32 + bit).</summary>
        public void ApplyExtended(uint[] words)
        {
            if (!IsActive || words == null) return;
            for (int i = 0; i < _pairs.Length; i++)
            {
                int ia = _pairs[i].IndexA, ib = _pairs[i].IndexB;
                if (ia < 0 || ib < 0) continue;
                if (ia >> 5 >= words.Length || ib >> 5 >= words.Length) continue;
                bool a = (words[ia >> 5] & (1u << (ia & 31))) != 0;
                bool b = (words[ib >> 5] & (1u << (ib & 31))) != 0;
                SocdCleaner.StepPair(_mode, a, b, _states[i].PrevA, _states[i].PrevB,
                    ref _states[i].Winner, out bool suppressA, out bool suppressB);
                _states[i].PrevA = a;
                _states[i].PrevB = b;
                if (suppressA) words[ia >> 5] &= ~(1u << (ia & 31));
                if (suppressB) words[ib >> 5] &= ~(1u << (ib & 31));
            }
        }

        private static Pair[] ParsePairs(string pairs, bool extendedIndices)
        {
            if (string.IsNullOrEmpty(pairs)) return Array.Empty<Pair>();
            var tokens = pairs.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<Pair>(tokens.Length);
            foreach (var token in tokens)
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1) continue;
                string aName = token.Substring(0, colon).Trim();
                string bName = token.Substring(colon + 1).Trim();
                if (aName.Length == 0 || bName.Length == 0
                    || string.Equals(aName, bName, StringComparison.Ordinal))
                    continue;

                if (extendedIndices)
                {
                    if (!int.TryParse(aName, out int ia) || !int.TryParse(bName, out int ib)) continue;
                    if (ia is < 0 or > 127 || ib is < 0 or > 127 || ia == ib) continue;
                    list.Add(new Pair { IndexA = ia, IndexB = ib, MaskA = 0, MaskB = 0 });
                }
                else
                {
                    ushort ma = ResolveGamepadMask(aName);
                    ushort mb = ResolveGamepadMask(bName);
                    if (ma == 0 || mb == 0 || ma == mb) continue;
                    list.Add(new Pair { MaskA = ma, MaskB = mb, IndexA = -1, IndexB = -1 });
                }
            }
            return list.ToArray();
        }

        /// <summary>Mapping TARGET name → Gamepad button mask, the exact
        /// WriteBoolTarget vocabulary (Step3.MappingSetEval). 0 = unknown.</summary>
        internal static ushort ResolveGamepadMask(string target) => target switch
        {
            "ButtonA" => Engine.Gamepad.A,
            "ButtonB" => Engine.Gamepad.B,
            "ButtonX" => Engine.Gamepad.X,
            "ButtonY" => Engine.Gamepad.Y,
            "LeftShoulder" => Engine.Gamepad.LEFT_SHOULDER,
            "RightShoulder" => Engine.Gamepad.RIGHT_SHOULDER,
            "ButtonBack" => Engine.Gamepad.BACK,
            "ButtonStart" => Engine.Gamepad.START,
            "LeftThumbButton" => Engine.Gamepad.LEFT_THUMB,
            "RightThumbButton" => Engine.Gamepad.RIGHT_THUMB,
            "ButtonGuide" => Engine.Gamepad.GUIDE,
            "DPadUp" => Engine.Gamepad.DPAD_UP,
            "DPadDown" => Engine.Gamepad.DPAD_DOWN,
            "DPadLeft" => Engine.Gamepad.DPAD_LEFT,
            "DPadRight" => Engine.Gamepad.DPAD_RIGHT,
            _ => 0,
        };
    }
}
