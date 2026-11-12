using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Per-slot MIDI output configuration. Dynamic CC and note counts
    /// with configurable starting CC/note numbers, channel, and velocity.
    /// Ranges are interdependent: CcCount is clamped to 0..(128 - StartCc),
    /// and NoteCount is clamped to 0..(128 - StartNote).
    /// </summary>
    public class MidiSlotConfig : ObservableObject
    {
        private int _channel = 1;
        /// <summary>MIDI channel (1-16, displayed as 1-based, stored as 1-based).</summary>
        public int Channel
        {
            get => _channel;
            set => SetProperty(ref _channel, Math.Clamp(value, 1, 16));
        }

        private int _ccCount = 6;
        /// <summary>Number of CC outputs. Max is 128 - StartCc.</summary>
        public int CcCount
        {
            get => _ccCount;
            set => SetProperty(ref _ccCount, Math.Clamp(value, 0, 128 - _startCc));
        }

        private int _startCc = 1;
        /// <summary>Starting CC number (0-127). Changing this re-clamps CcCount.</summary>
        public int StartCc
        {
            get => _startCc;
            set
            {
                if (SetProperty(ref _startCc, Math.Clamp(value, 0, 127)))
                    CcCount = _ccCount; // re-clamp against new start
            }
        }

        private int _noteCount = 11;
        /// <summary>Number of note outputs. Max is 128 - StartNote.</summary>
        public int NoteCount
        {
            get => _noteCount;
            set => SetProperty(ref _noteCount, Math.Clamp(value, 0, 128 - _startNote));
        }

        private int _startNote = 60;
        /// <summary>Starting note number (0-127). Changing this re-clamps NoteCount.</summary>
        public int StartNote
        {
            get => _startNote;
            set
            {
                if (SetProperty(ref _startNote, Math.Clamp(value, 0, 127)))
                    NoteCount = _noteCount; // re-clamp against new start
            }
        }

        private byte _velocity = 127;
        /// <summary>Note velocity for button presses (1-127). A MIDI 1.0
        /// Note On with velocity 0 is a Note Off (Microsoft's in-box synth
        /// documentation, NAudio's MidiEvent.IsNoteOff), so 0 would turn
        /// every press into a release. A 0 from the bar or from an older
        /// file reads as 1, the quietest Note On.</summary>
        public byte Velocity
        {
            get => _velocity;
            set => SetProperty(ref _velocity, Math.Clamp(value, (byte)1, (byte)127));
        }

        private string _outputPort = string.Empty;
        /// <summary>The port this slot sends to under the legacy MIDI API, by
        /// the name the slot's port picker shows. Empty for None, and until
        /// the user picks one. The Windows MIDI Services APIs create the
        /// slot's own port and ignore it.</summary>
        public string OutputPort
        {
            get => _outputPort;
            set => SetProperty(ref _outputPort, value ?? string.Empty);
        }

        private bool _outputPortChosen;
        /// <summary>Whether <see cref="OutputPort"/>, None included, is a
        /// choice for the profile in use: picked or reset in the slot's port
        /// picker, or applied from a snapshot that carries one. A profile saved
        /// while it is not set records no choice
        /// (<see cref="OutputPortSnapshot"/>), so a profile that never had a
        /// port picked does not take the port the slot kept from the profile
        /// before it. It raises PropertyChanged like the fields beside it,
        /// since the file records it: choosing None on a slot that has no
        /// port changes this and not the port, and the settings would
        /// otherwise never hear of the choice.</summary>
        public bool OutputPortChosen
        {
            get => _outputPortChosen;
            set => SetProperty(ref _outputPortChosen, value);
        }

        /// <summary>Makes <paramref name="port"/> the slot's port, as the
        /// user's choice. Empty is None.</summary>
        public void ChooseOutputPort(string port)
        {
            OutputPort = port;
            OutputPortChosen = true;
        }

        /// <summary>The port as a snapshot records it: its name, empty for
        /// None, or null when the slot holds no choice of its own.</summary>
        public string OutputPortSnapshot => OutputPortChosen ? OutputPort : null;

        /// <summary>Resets every field to its fresh-install default IN
        /// PLACE, preserving the instance (external PropertyChanged
        /// subscribers survive; same invariant as ExtendedSlotConfig).
        /// Start values are written before counts so the count clamps
        /// evaluate against the default starts.</summary>
        public void ResetToDefaults()
        {
            Channel = 1;
            StartCc = 1;
            CcCount = 6;
            StartNote = 60;
            NoteCount = 11;
            Velocity = 127;
            OutputPort = string.Empty;
            OutputPortChosen = false;
        }

        /// <summary>Returns CC numbers array: sequential from StartCc for CcCount entries.</summary>
        public int[] GetCcNumbers()
        {
            var arr = new int[_ccCount];
            for (int i = 0; i < _ccCount; i++)
                arr[i] = _startCc + i;
            return arr;
        }

        private static readonly string[] PitchLetters = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>A note's pitch, as the picture's keys are labeled and
        /// the bar under the picture heads a note: 60 is C4. Null for a
        /// number that is no MIDI note.</summary>
        public static string PitchName(int note)
            => (uint)note < 128
                ? PitchLetters[note % 12] + (note / 12 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

        /// <summary>Returns note numbers array: sequential from StartNote for NoteCount entries.</summary>
        public int[] GetNoteNumbers()
        {
            var arr = new int[_noteCount];
            for (int i = 0; i < _noteCount; i++)
                arr[i] = _startNote + i;
            return arr;
        }
    }

    /// <summary>XML-serializable snapshot of a MIDI slot's configuration.</summary>
    public class MidiSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        [XmlAttribute] public int Channel { get; set; } = 1;
        [XmlAttribute] public int CcCount { get; set; } = 6;
        [XmlAttribute] public int StartCc { get; set; } = 1;
        [XmlAttribute] public int NoteCount { get; set; } = 11;
        [XmlAttribute] public int StartNote { get; set; } = 60;
        [XmlAttribute] public byte Velocity { get; set; } = 127;
        /// <summary>The legacy API's output port: a port's name, empty for
        /// None, or null when the snapshot records no choice, which is left
        /// out of the file. None is written as an empty attribute, since
        /// "A slot set to None does not start". Applying a snapshot with no
        /// choice keeps the slot's current port (<see cref="ApplyOutputPortTo"/>),
        /// so a profile saved before a port was picked does not clear
        /// it.</summary>
        [XmlAttribute] public string OutputPort { get; set; }

        public bool ShouldSerializeOutputPort() => OutputPort != null;

        /// <summary>Applies <see cref="OutputPort"/> when this snapshot has a
        /// choice, None included, and marks it the slot's choice. A snapshot
        /// with none keeps the slot's port and clears the mark, so the next
        /// save of this profile records no choice again instead of the port
        /// the slot kept from the profile before it.</summary>
        public void ApplyOutputPortTo(MidiSlotConfig config)
        {
            if (config == null) return;
            if (OutputPort != null)
                config.ChooseOutputPort(OutputPort);
            else
                config.OutputPortChosen = false;
        }
    }
}
