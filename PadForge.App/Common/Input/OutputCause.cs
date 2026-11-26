namespace PadForge.Common.Input
{
    /// <summary>
    /// What a piece of PadForge's key or mouse output came from (#600): the
    /// person's own devices alone, a program's session, or nothing proven.
    /// The zero value is unproven, and ProgramInputGate holds unproven output
    /// as it holds a program's, so a producer that forgets to say where its
    /// output came from is held, never let out.
    /// </summary>
    internal readonly struct OutputCause
    {
        // 0 unproven, -1 the person's own devices alone, above 0 the number
        // of the program's session.
        private readonly int _value;

        private OutputCause(int value) => _value = value;

        /// <summary>Output proven to come from the person's own devices
        /// alone.</summary>
        internal static OutputCause Own => new(-1);

        /// <summary>Output a program's session fed. A session number of 0
        /// or below gives a program's output with no session known, which
        /// is held as a program's.</summary>
        internal static OutputCause Program(int session) => new(session > 0 ? session : 0);

        internal bool IsOwn => _value == -1;

        /// <summary>The session that fed it, 0 when none is known.</summary>
        internal int Session => _value > 0 ? _value : 0;

        /// <summary>The cause of output two causes fed together: own only
        /// when both are, and otherwise the first program's.</summary>
        internal OutputCause And(OutputCause other)
        {
            if (IsOwn) return other;
            if (other.IsOwn || _value > 0) return this;
            return other;
        }

        internal int Raw => _value;

        internal static OutputCause FromRaw(int raw) => new(raw);

        public override string ToString() => IsOwn ? "own" : _value > 0 ? "program " + _value : "unproven";
    }

    /// <summary>
    /// The cause of each part of a Keyboard + Mouse slot's output for one
    /// pass (#600): the keys, buttons and pointer proven the person's own,
    /// and the cause of the rest. The zero value proves nothing, so a slot
    /// whose causes were never worked out has all its output held.
    /// </summary>
    internal struct KbmCause
    {
        /// <summary>The keys proven own, as KbmRawState packs keys.</summary>
        public ulong OwnKeys0, OwnKeys1, OwnKeys2, OwnKeys3;

        /// <summary>The mouse buttons proven own, as KbmRawState packs
        /// them.</summary>
        public byte OwnButtons;

        /// <summary>Whether the movement, the wheels and the absolute
        /// pointer are proven own.</summary>
        public bool OwnPointer;

        /// <summary>The cause of every part not proven own.</summary>
        public OutputCause Other;

        /// <summary>Every part proven own.</summary>
        internal static KbmCause AllOwn => new()
        {
            OwnKeys0 = ulong.MaxValue, OwnKeys1 = ulong.MaxValue, OwnKeys2 = ulong.MaxValue, OwnKeys3 = ulong.MaxValue,
            OwnButtons = 0xFF, OwnPointer = true, Other = OutputCause.Own,
        };

        /// <summary>Every part of <paramref name="cause"/>.</summary>
        internal static KbmCause All(OutputCause cause) => cause.IsOwn ? AllOwn : new KbmCause { Other = cause };

        internal OutputCause Key(int vk)
        {
            ulong word = (vk >> 6) switch { 0 => OwnKeys0, 1 => OwnKeys1, 2 => OwnKeys2, _ => OwnKeys3 };
            return (word & (1UL << (vk & 63))) != 0 ? OutputCause.Own : Other;
        }

        internal OutputCause Button(int bit) => (OwnButtons & (1 << bit)) != 0 ? OutputCause.Own : Other;

        internal OutputCause Pointer => OwnPointer ? OutputCause.Own : Other;

        /// <summary>Takes the proof from key <paramref name="vk"/> and
        /// gives it <paramref name="cause"/>.</summary>
        internal void MarkKey(int vk, OutputCause cause)
        {
            ulong bit = 1UL << (vk & 63);
            switch (vk >> 6)
            {
                case 0: OwnKeys0 &= ~bit; break;
                case 1: OwnKeys1 &= ~bit; break;
                case 2: OwnKeys2 &= ~bit; break;
                default: OwnKeys3 &= ~bit; break;
            }
            Other = Other.IsOwn ? cause : Other.And(cause);
        }

        /// <summary>Whether any part is not proven own.</summary>
        internal bool AnyMarked => (OwnKeys0 & OwnKeys1 & OwnKeys2 & OwnKeys3) != ulong.MaxValue
            || OwnButtons != 0xFF || !OwnPointer;
    }
}
