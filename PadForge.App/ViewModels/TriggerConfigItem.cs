using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

        // ── Digit conversion helpers (triggers use unsigned 16-bit: 0–65535) ──
        private static int PctToDigit(double pct) => (int)Math.Round(pct / 100.0 * 65535.0);
        private static double DigitToPct(int digit) => digit / 65535.0 * 100.0;

        private double _deadZone;
        public double DeadZone
        {
            get => _deadZone;
            set { if (SetProperty(ref _deadZone, Math.Clamp(value, 0, 100))) { OnPropertyChanged(nameof(DeadZoneDigit)); RebuildCurvePoints(); } }
        }
        public int DeadZoneDigit
        {
            get => PctToDigit(_deadZone);
            set => DeadZone = DigitToPct(value);
        }

        private double _maxRange = 100;
        public double MaxRange
        {
            get => _maxRange;
            set { if (SetProperty(ref _maxRange, Math.Clamp(value, 1, 100))) { OnPropertyChanged(nameof(MaxRangeDigit)); RebuildCurvePoints(); } }
        }
        public int MaxRangeDigit
        {
            get => PctToDigit(_maxRange);
            set => MaxRange = DigitToPct(value);
        }

        private double _antiDeadZone;
        public double AntiDeadZone
        {
            get => _antiDeadZone;
            set { if (SetProperty(ref _antiDeadZone, Math.Clamp(value, 0, 100))) OnPropertyChanged(nameof(AntiDeadZoneDigit)); }
        }
        public int AntiDeadZoneDigit
        {
            get => PctToDigit(_antiDeadZone);
            set => AntiDeadZone = DigitToPct(value);
        }

        private double _sensitivityCurve;
        public double SensitivityCurve
        {
            get => _sensitivityCurve;
            set { if (SetProperty(ref _sensitivityCurve, Math.Clamp(value, -100, 100))) RebuildCurvePoints(); }
        }

        // ── Sensitivity curve chart ──

        private PointCollection _curvePoints;
        public PointCollection CurvePoints
        {
            get => _curvePoints ??= StickConfigItem.BuildTriggerCurvePoints(_sensitivityCurve, _deadZone, _maxRange);
            private set => SetProperty(ref _curvePoints, value);
        }

        private double _liveCurveX;
        public double LiveCurveX { get => _liveCurveX; set => SetProperty(ref _liveCurveX, value); }

        private double _liveCurveY;
        public double LiveCurveY { get => _liveCurveY; set => SetProperty(ref _liveCurveY, value); }

        public void RebuildCurvePoints()
        {
            CurvePoints = StickConfigItem.BuildTriggerCurvePoints(_sensitivityCurve, _deadZone, _maxRange);
        }

        // Live preview value (0.0-1.0 normalized)
        private double _liveValue;
        public double LiveValue
        {
            get => _liveValue;
            set => SetProperty(ref _liveValue, value);
        }

        private ushort _rawValue;
        public ushort RawValue
        {
            get => _rawValue;
            set { if (SetProperty(ref _rawValue, value)) OnPropertyChanged(nameof(RawDisplay)); }
        }

        /// <summary>Formatted display: "32768 (50.0%)"</summary>
        public string RawDisplay => $"{_rawValue} ({_rawValue / 655.35:F1}%)";

        /// <summary>Raw axis index in VJoyRawState.Axes (custom vJoy only, -1 for gamepad).</summary>
        public int AxisIndex { get; }

        // ── Reset commands ──

        private ICommand _resetAllCommand;
        public ICommand ResetAllCommand => _resetAllCommand ??= new RelayCommand(() =>
        {
            DeadZone = 0; MaxRange = 100;
            AntiDeadZone = 0; SensitivityCurve = 0;
        });

        private ICommand _resetRangeCommand;
        public ICommand ResetRangeCommand => _resetRangeCommand ??= new RelayCommand(() => { DeadZone = 0; MaxRange = 100; });
        private ICommand _resetAntiDeadZoneCommand;
        public ICommand ResetAntiDeadZoneCommand => _resetAntiDeadZoneCommand ??= new RelayCommand(() => AntiDeadZone = 0);
        private ICommand _resetSensitivityCommand;
        public ICommand ResetSensitivityCommand => _resetSensitivityCommand ??= new RelayCommand(() => SensitivityCurve = 0);

        public TriggerConfigItem(int index, string title, int axisIndex = -1)
        {
            Index = index;
            Title = title;
            AxisIndex = axisIndex;
        }
    }
}
