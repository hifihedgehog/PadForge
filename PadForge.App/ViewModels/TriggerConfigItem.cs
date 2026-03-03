using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents one trigger section in the dynamic Triggers tab.
    /// For gamepad presets (Xbox 360/DS4): index 0 = Left, index 1 = Right.
    /// For custom vJoy: index 0..N based on TriggerCount.
    /// </summary>
    public class TriggerConfigItem : ObservableObject
    {
        public string Title { get; }
        public int Index { get; }

        private int _deadZone;
        public int DeadZone
        {
            get => _deadZone;
            set => SetProperty(ref _deadZone, Math.Clamp(value, 0, 100));
        }

        private int _maxRange = 100;
        public int MaxRange
        {
            get => _maxRange;
            set => SetProperty(ref _maxRange, Math.Clamp(value, 1, 100));
        }

        private int _antiDeadZone;
        public int AntiDeadZone
        {
            get => _antiDeadZone;
            set => SetProperty(ref _antiDeadZone, Math.Clamp(value, 0, 100));
        }

        // Live preview value (0.0-1.0 normalized)
        private double _liveValue;
        public double LiveValue
        {
            get => _liveValue;
            set => SetProperty(ref _liveValue, value);
        }

        private byte _rawValue;
        public byte RawValue
        {
            get => _rawValue;
            set => SetProperty(ref _rawValue, value);
        }

        public TriggerConfigItem(int index, string title)
        {
            Index = index;
            Title = title;
        }
    }
}
