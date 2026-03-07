using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Per-slot MIDI output configuration. Drives port selection, channel,
    /// CC numbers for axes, and note numbers for buttons.
    /// </summary>
    public class MidiSlotConfig : ObservableObject
    {
        private string _portName = "";
        /// <summary>MIDI output port name (e.g., "loopMIDI Port").</summary>
        public string PortName
        {
            get => _portName;
            set => SetProperty(ref _portName, value ?? "");
        }

        private int _channel = 1;
        /// <summary>MIDI channel (1-16, displayed as 1-based, stored as 1-based).</summary>
        public int Channel
        {
            get => _channel;
            set => SetProperty(ref _channel, Math.Clamp(value, 1, 16));
        }

        // CC numbers for axes (default: LX=1, LY=2, LT=3, RX=4, RY=5, RT=6)
        private int _ccLeftX = 1;
        public int CcLeftX { get => _ccLeftX; set => SetProperty(ref _ccLeftX, Math.Clamp(value, 0, 127)); }

        private int _ccLeftY = 2;
        public int CcLeftY { get => _ccLeftY; set => SetProperty(ref _ccLeftY, Math.Clamp(value, 0, 127)); }

        private int _ccLeftTrigger = 3;
        public int CcLeftTrigger { get => _ccLeftTrigger; set => SetProperty(ref _ccLeftTrigger, Math.Clamp(value, 0, 127)); }

        private int _ccRightX = 4;
        public int CcRightX { get => _ccRightX; set => SetProperty(ref _ccRightX, Math.Clamp(value, 0, 127)); }

        private int _ccRightY = 5;
        public int CcRightY { get => _ccRightY; set => SetProperty(ref _ccRightY, Math.Clamp(value, 0, 127)); }

        private int _ccRightTrigger = 6;
        public int CcRightTrigger { get => _ccRightTrigger; set => SetProperty(ref _ccRightTrigger, Math.Clamp(value, 0, 127)); }

        // Note numbers for buttons (default: A=60..Guide=70)
        private int _noteA = 60;
        public int NoteA { get => _noteA; set => SetProperty(ref _noteA, Math.Clamp(value, 0, 127)); }

        private int _noteB = 61;
        public int NoteB { get => _noteB; set => SetProperty(ref _noteB, Math.Clamp(value, 0, 127)); }

        private int _noteX = 62;
        public int NoteX { get => _noteX; set => SetProperty(ref _noteX, Math.Clamp(value, 0, 127)); }

        private int _noteY = 63;
        public int NoteY { get => _noteY; set => SetProperty(ref _noteY, Math.Clamp(value, 0, 127)); }

        private int _noteLB = 64;
        public int NoteLB { get => _noteLB; set => SetProperty(ref _noteLB, Math.Clamp(value, 0, 127)); }

        private int _noteRB = 65;
        public int NoteRB { get => _noteRB; set => SetProperty(ref _noteRB, Math.Clamp(value, 0, 127)); }

        private int _noteBack = 66;
        public int NoteBack { get => _noteBack; set => SetProperty(ref _noteBack, Math.Clamp(value, 0, 127)); }

        private int _noteStart = 67;
        public int NoteStart { get => _noteStart; set => SetProperty(ref _noteStart, Math.Clamp(value, 0, 127)); }

        private int _noteLS = 68;
        public int NoteLS { get => _noteLS; set => SetProperty(ref _noteLS, Math.Clamp(value, 0, 127)); }

        private int _noteRS = 69;
        public int NoteRS { get => _noteRS; set => SetProperty(ref _noteRS, Math.Clamp(value, 0, 127)); }

        private int _noteGuide = 70;
        public int NoteGuide { get => _noteGuide; set => SetProperty(ref _noteGuide, Math.Clamp(value, 0, 127)); }

        private byte _velocity = 127;
        /// <summary>Note velocity for button presses (0-127).</summary>
        public byte Velocity
        {
            get => _velocity;
            set => SetProperty(ref _velocity, Math.Clamp(value, (byte)0, (byte)127));
        }

        /// <summary>Returns CC numbers array for MidiVirtualController.</summary>
        public int[] GetCcNumbers() => new[] { _ccLeftX, _ccLeftY, _ccLeftTrigger, _ccRightX, _ccRightY, _ccRightTrigger };

        /// <summary>Returns note numbers array for MidiVirtualController.</summary>
        public int[] GetNoteNumbers() => new[] { _noteA, _noteB, _noteX, _noteY, _noteLB, _noteRB, _noteBack, _noteStart, _noteLS, _noteRS, _noteGuide };
    }

    /// <summary>XML-serializable snapshot of a MIDI slot's configuration.</summary>
    public class MidiSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        [XmlAttribute] public string PortName { get; set; } = "";
        [XmlAttribute] public int Channel { get; set; } = 1;
        [XmlAttribute] public byte Velocity { get; set; } = 127;

        // CC numbers (attributes for compact XML)
        [XmlAttribute] public int CcLX { get; set; } = 1;
        [XmlAttribute] public int CcLY { get; set; } = 2;
        [XmlAttribute] public int CcLT { get; set; } = 3;
        [XmlAttribute] public int CcRX { get; set; } = 4;
        [XmlAttribute] public int CcRY { get; set; } = 5;
        [XmlAttribute] public int CcRT { get; set; } = 6;

        // Note numbers
        [XmlAttribute] public int NoteA { get; set; } = 60;
        [XmlAttribute] public int NoteB { get; set; } = 61;
        [XmlAttribute] public int NoteX { get; set; } = 62;
        [XmlAttribute] public int NoteY { get; set; } = 63;
        [XmlAttribute] public int NoteLB { get; set; } = 64;
        [XmlAttribute] public int NoteRB { get; set; } = 65;
        [XmlAttribute] public int NoteBack { get; set; } = 66;
        [XmlAttribute] public int NoteStart { get; set; } = 67;
        [XmlAttribute] public int NoteLS { get; set; } = 68;
        [XmlAttribute] public int NoteRS { get; set; } = 69;
        [XmlAttribute] public int NoteGuide { get; set; } = 70;
    }
}
