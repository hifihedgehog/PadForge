using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents one thumbstick section in the dynamic Sticks tab.
    /// For gamepad presets (Xbox 360/DS4): index 0 = Left, index 1 = Right.
    /// For custom vJoy: index 0..N based on ThumbstickCount.
    /// </summary>
    public class StickConfigItem : ObservableObject
    {
        public string Title { get; }
        public int Index { get; }

        private int _deadZoneX;
        public int DeadZoneX
        {
            get => _deadZoneX;
            set => SetProperty(ref _deadZoneX, Math.Clamp(value, 0, 100));
        }

        private int _deadZoneY;
        public int DeadZoneY
        {
            get => _deadZoneY;
            set => SetProperty(ref _deadZoneY, Math.Clamp(value, 0, 100));
        }

        private int _antiDeadZoneX;
        public int AntiDeadZoneX
        {
            get => _antiDeadZoneX;
            set => SetProperty(ref _antiDeadZoneX, Math.Clamp(value, 0, 100));
        }

        private int _antiDeadZoneY;
        public int AntiDeadZoneY
        {
            get => _antiDeadZoneY;
            set => SetProperty(ref _antiDeadZoneY, Math.Clamp(value, 0, 100));
        }

        private int _linear;
        public int Linear
        {
            get => _linear;
            set => SetProperty(ref _linear, Math.Clamp(value, 0, 100));
        }

        // Live preview values (0.0-1.0 normalized for Canvas positioning)
        private double _liveX = 0.5;
        public double LiveX
        {
            get => _liveX;
            set => SetProperty(ref _liveX, value);
        }

        private double _liveY = 0.5;
        public double LiveY
        {
            get => _liveY;
            set => SetProperty(ref _liveY, value);
        }

        private short _rawX;
        public short RawX
        {
            get => _rawX;
            set => SetProperty(ref _rawX, value);
        }

        private short _rawY;
        public short RawY
        {
            get => _rawY;
            set => SetProperty(ref _rawY, value);
        }

        public StickConfigItem(int index, string title)
        {
            Index = index;
            Title = title;
        }
    }
}
