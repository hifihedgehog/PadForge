using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents one trigger section in the dynamic Triggers tab.
    /// For gamepad presets (Xbox 360/DS4): index 0 = Left, index 1 = Right.
    /// For custom vJoy: index 0..N based on TriggerCount.
    /// </summary>
    public class TriggerConfigItem : ObservableObject
    {
        public static string[] CurvePresetNames { get; private set; } =
            Common.CurveLut.BuildPresetDisplayNames();

        static TriggerConfigItem()
        {
            Resources.Strings.Strings.CultureChanged += () =>
                CurvePresetNames = Common.CurveLut.BuildPresetDisplayNames();
        }

        public string PresetName => Common.CurveLut.MatchPreset(SensitivityCurve);

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

        private string _sensitivityCurve = "0,0;1,1";
        public string SensitivityCurve
        {
            get => _sensitivityCurve;
            set { if (SetProperty(ref _sensitivityCurve, value ?? "0,0;1,1")) { RebuildCurvePoints(); OnPropertyChanged(nameof(PresetName)); } }
        }

        // ── Live input for CurveEditor binding ──

        private double _liveInput;
        public double LiveInputForCurve { get => _liveInput; set => SetProperty(ref _liveInput, value); }

        public void RebuildCurvePoints() { /* CurveEditor redraws via CurveString binding */ }

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
            AntiDeadZone = 0; SensitivityCurve = "0,0;1,1";
        });

        private ICommand _resetRangeCommand;
        public ICommand ResetRangeCommand => _resetRangeCommand ??= new RelayCommand(() => { DeadZone = 0; MaxRange = 100; });
        private ICommand _resetAntiDeadZoneCommand;
        public ICommand ResetAntiDeadZoneCommand => _resetAntiDeadZoneCommand ??= new RelayCommand(() => AntiDeadZone = 0);
        private ICommand _resetSensitivityCommand;
        public ICommand ResetSensitivityCommand => _resetSensitivityCommand ??= new RelayCommand(() => SensitivityCurve = "0,0;1,1");

        public TriggerConfigItem(int index, string title, int axisIndex = -1)
        {
            Index = index;
            Title = title;
            AxisIndex = axisIndex;
        }
    }
}
