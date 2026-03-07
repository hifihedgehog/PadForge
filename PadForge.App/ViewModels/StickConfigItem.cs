using System.Collections.Generic;
using System.Windows.Threading;
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

        private double _deadZoneX;
        public double DeadZoneX
        {
            get => _deadZoneX;
            set => SetProperty(ref _deadZoneX, Math.Clamp(value, 0, 100));
        }

        private double _deadZoneY;
        public double DeadZoneY
        {
            get => _deadZoneY;
            set => SetProperty(ref _deadZoneY, Math.Clamp(value, 0, 100));
        }

        private double _antiDeadZoneX;
        public double AntiDeadZoneX
        {
            get => _antiDeadZoneX;
            set => SetProperty(ref _antiDeadZoneX, Math.Clamp(value, 0, 100));
        }

        private double _antiDeadZoneY;
        public double AntiDeadZoneY
        {
            get => _antiDeadZoneY;
            set => SetProperty(ref _antiDeadZoneY, Math.Clamp(value, 0, 100));
        }

        private double _linear;
        public double Linear
        {
            get => _linear;
            set => SetProperty(ref _linear, Math.Clamp(value, 0, 100));
        }

        private double _maxRangeX = 100;
        public double MaxRangeX
        {
            get => _maxRangeX;
            set => SetProperty(ref _maxRangeX, Math.Clamp(value, 1, 100));
        }

        private double _maxRangeY = 100;
        public double MaxRangeY
        {
            get => _maxRangeY;
            set => SetProperty(ref _maxRangeY, Math.Clamp(value, 1, 100));
        }

        private double _centerOffsetX;
        public double CenterOffsetX
        {
            get => _centerOffsetX;
            set => SetProperty(ref _centerOffsetX, Math.Clamp(value, -100, 100));
        }

        private double _centerOffsetY;
        public double CenterOffsetY
        {
            get => _centerOffsetY;
            set => SetProperty(ref _centerOffsetY, Math.Clamp(value, -100, 100));
        }

        private bool _isCalibrating;
        public bool IsCalibrating
        {
            get => _isCalibrating;
            set => SetProperty(ref _isCalibrating, value);
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
            set { if (SetProperty(ref _rawX, value)) OnPropertyChanged(nameof(RawDisplay)); }
        }

        private short _rawY;
        public short RawY
        {
            get => _rawY;
            set { if (SetProperty(ref _rawY, value)) OnPropertyChanged(nameof(RawDisplay)); }
        }

        /// <summary>Formatted display string: "X: -1234 (50.0%)  Y: 5678 (58.7%)"</summary>
        public string RawDisplay =>
            $"X: {_rawX} ({(_rawX + 32768.0) / 655.35:F1}%)  Y: {_rawY} ({(_rawY + 32768.0) / 655.35:F1}%)";

        /// <summary>Unprocessed hardware value for calibration (not affected by offset/dead zone).</summary>
        public short HardwareRawX { get; set; }

        /// <summary>Unprocessed hardware value for calibration (not affected by offset/dead zone).</summary>
        public short HardwareRawY { get; set; }

        /// <summary>Raw axis index for X in VJoyRawState.Axes (custom vJoy only, -1 for gamepad).</summary>
        public int AxisXIndex { get; }

        /// <summary>Raw axis index for Y in VJoyRawState.Axes (custom vJoy only, -1 for gamepad).</summary>
        public int AxisYIndex { get; }

        public StickConfigItem(int index, string title, int axisXIndex = -1, int axisYIndex = -1)
        {
            Index = index;
            Title = title;
            AxisXIndex = axisXIndex;
            AxisYIndex = axisYIndex;
        }

        /// <summary>
        /// Starts center calibration by sampling RawX/RawY over ~0.5s (15 frames)
        /// and setting CenterOffsetX/Y to negate the average drift.
        /// </summary>
        public void StartCalibration()
        {
            if (IsCalibrating) return;
            IsCalibrating = true;

            var samplesX = new List<short>(15);
            var samplesY = new List<short>(15);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    samplesX.Add(HardwareRawX);
                    samplesY.Add(HardwareRawY);
                    if (samplesX.Count >= 15)
                    {
                        timer.Stop();
                        double avgX = 0, avgY = 0;
                        for (int i = 0; i < samplesX.Count; i++)
                        {
                            avgX += samplesX[i];
                            avgY += samplesY[i];
                        }
                        avgX /= samplesX.Count;
                        avgY /= samplesY.Count;

                        // Negate the drift and convert to percentage of full range
                        CenterOffsetX = Math.Round(-avgX / 32768.0 * 100.0, 1);
                        CenterOffsetY = Math.Round(-avgY / 32768.0 * 100.0, 1);
                        IsCalibrating = false;
                    }
                }
                catch
                {
                    timer.Stop();
                    IsCalibrating = false;
                }
            };
            timer.Start();
        }
    }
}
