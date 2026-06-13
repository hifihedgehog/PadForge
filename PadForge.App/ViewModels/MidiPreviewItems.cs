using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>One key in the Devices-page MIDI piano preview. Notes wrap
    /// in a WrapPanel (128 keys is too wide for a single linear keyboard),
    /// coloured white/black like a piano and lit while held.</summary>
    public sealed class MidiNoteKeyItem : ObservableObject
    {
        private static readonly bool[] BlackInOctave =
            { false, true, false, true, false, false, true, false, true, false, true, false };
        private static readonly string[] Letters =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public int NoteNumber { get; init; }

        /// <summary>Full note name, e.g. "C4" (middle C = note 60).</summary>
        public string Label { get; init; }

        /// <summary>"C0".."C9" octave marker shown under C keys only; empty
        /// for the rest so the wrap stays readable.</summary>
        public string OctaveMarker { get; init; }

        public bool IsBlackKey { get; init; }

        private bool _isOn;
        public bool IsOn { get => _isOn; set => SetProperty(ref _isOn, value); }

        public static MidiNoteKeyItem Build(int note) => new()
        {
            NoteNumber = note,
            Label = $"{Letters[note % 12]}{note / 12 - 1}",
            OctaveMarker = note % 12 == 0 ? $"C{note / 12 - 1}" : string.Empty,
            IsBlackKey = BlackInOctave[note % 12],
        };
    }

    /// <summary>One controller in the Devices-page MIDI CC preview: number,
    /// live value (0-127), and a normalized 0-1 fill for the bar.</summary>
    public sealed class MidiCcItem : ObservableObject
    {
        public int CcNumber { get; init; }
        public string Label { get; init; }

        private int _value;
        public int Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                {
                    OnPropertyChanged(nameof(Normalized));
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        /// <summary>0-1 fill fraction for the value bar.</summary>
        public double Normalized => _value / 127.0;

        /// <summary>True once the controller has moved off zero, so idle CCs
        /// read dimmer than active ones.</summary>
        public bool IsActive => _value > 0;

        public static MidiCcItem Build(int cc) => new()
        {
            CcNumber = cc,
            Label = $"CC {cc}",
        };
    }
}
