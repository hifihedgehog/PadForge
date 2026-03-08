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
        /// <summary>Note velocity for button presses (0-127).</summary>
        public byte Velocity
        {
            get => _velocity;
            set => SetProperty(ref _velocity, Math.Clamp(value, (byte)0, (byte)127));
        }

        /// <summary>Returns CC numbers array: sequential from StartCc for CcCount entries.</summary>
        public int[] GetCcNumbers()
        {
            var arr = new int[_ccCount];
            for (int i = 0; i < _ccCount; i++)
                arr[i] = _startCc + i;
            return arr;
        }

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
    }
}
