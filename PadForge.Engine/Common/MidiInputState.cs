using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Full MIDI input namespace for one MIDI input device, channel-merged
    /// (omni): a note or CC carries the same meaning regardless of the
    /// channel it arrived on. Sized to the MIDI spec, not to the gamepad
    /// axis/button arrays — every one of the 128 notes and 128 CCs is
    /// always available, so a MIDI device needs no per-device window
    /// configuration. Null on <see cref="CustomInputState"/> for any
    /// non-MIDI device, mirroring the <see cref="TouchpadInputState"/>
    /// sub-state cost model (zero allocation when the capability is absent).
    ///
    /// Mapping descriptors that resolve against this state:
    ///   "Midi Note N"      — note N held (N = 0..127)
    ///   "Midi CC N"        — controller N value, absolute (N = 0..127)
    ///   "Midi CC N Up"     — relative-encoder clockwise pulse (momentary)
    ///   "Midi CC N Down"   — relative-encoder counter-clockwise pulse
    ///   "Midi Pitch Bend"  — pitch bend
    /// </summary>
    public sealed class MidiInputState
    {
        /// <summary>Number of MIDI notes (0..127).</summary>
        public const int NoteCount = 128;

        /// <summary>Number of MIDI continuous controllers (0..127).</summary>
        public const int CcCount = 128;

        /// <summary>Pitch-bend value at rest (14-bit center, scaled to 0–65535).</summary>
        public const int PitchBendCenter = 32768;

        /// <summary>True while the note is held. Indexed by note number.</summary>
        public bool[] Notes;

        /// <summary>Controller values, 0–127. Indexed by CC number.</summary>
        public byte[] Cc;

        /// <summary>Momentary "encoder turned clockwise" pulse per CC. An
        /// endless rotary encoder in relative (two's-complement) mode pulses
        /// this for one short window per detent; the device shapes the pulse
        /// so a 60 Hz poll catches each step. Indexed by CC number.</summary>
        public bool[] CcUp;

        /// <summary>Momentary "encoder turned counter-clockwise" pulse per CC.</summary>
        public bool[] CcDown;

        /// <summary>Pitch bend, scaled 0–65535 (center = <see cref="PitchBendCenter"/>).</summary>
        public int PitchBend;

        public MidiInputState()
        {
            Notes = new bool[NoteCount];
            Cc = new byte[CcCount];
            CcUp = new bool[CcCount];
            CcDown = new bool[CcCount];
            PitchBend = PitchBendCenter;
        }

        public MidiInputState Clone()
        {
            var clone = new MidiInputState();
            Array.Copy(Notes, clone.Notes, NoteCount);
            Array.Copy(Cc, clone.Cc, CcCount);
            Array.Copy(CcUp, clone.CcUp, CcCount);
            Array.Copy(CcDown, clone.CcDown, CcCount);
            clone.PitchBend = PitchBend;
            return clone;
        }
    }
}
