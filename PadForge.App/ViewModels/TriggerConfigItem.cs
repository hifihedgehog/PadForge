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
    /// For gamepad presets (Xbox / PlayStation): index 0 = Left, index 1 = Right.
    /// For custom Extended: index 0..N based on TriggerCount.
    /// </summary>
    public partial class TriggerConfigItem : FiniteObservableObject
    {
        public static string[] CurvePresetNames { get; private set; } =
            Common.CurveLut.BuildPresetDisplayNames();

        // Culture-stamped rebuild, no handler-order dependence (see the
        // StickConfigItem twin for the ordering defect this replaces).
        private static string s_presetCulture = System.Globalization.CultureInfo.CurrentUICulture.Name;

        internal static void EnsurePresetsCultureCurrent()
        {
            string culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (culture == s_presetCulture) return;
            s_presetCulture = culture;
            CurvePresetNames = Common.CurveLut.BuildPresetDisplayNames();
        }

        public string PresetName => Common.CurveLut.MatchPreset(SensitivityCurve);

        public string Title { get; }
        public int Index { get; }
        public string IconLabel { get; }
        public bool IconRightSide { get; }

        /// <summary>The card's screen-reader name, its title: the card is a
        /// list item, and ItemAutomationPeer.GetNameCore falls back to the
        /// item's ToString(). Without it the card read as the first plain
        /// text it shows, the words of its Reset All button, since the
        /// title is drawn in runs.</summary>
        public override string ToString() => Title ?? string.Empty;

        /// <summary>Per-card "Reset {Title}" tooltip — see StickConfigItem
        /// for the format-string rationale.</summary>
        public string ResetAllToolTip
            => string.Format(Resources.Strings.Strings.Instance.Pad_ResetSection_Format, Title);

        /// <summary>The key this trigger's More row is stored under, so each
        /// trigger opens and stays open on its own, as a stick's does.</summary>
        public string MoreStateKey => "Pad.Triggers.Trigger" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".More";

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

        // Raw-stage value (0-1, pre-pipeline) for the two-stage bars (#175).
        public double RawNorm { get => _rawNorm; set => SetProperty(ref _rawNorm, value); }
        private double _rawNorm;

        public void RebuildCurvePoints() { /* CurveEditor redraws via CurveString binding */ }

        // Live preview value (0.0-1.0 normalized)
        private double _liveValue;
        public double LiveValue
        {
            get => _liveValue;
            set { if (SetProperty(ref _liveValue, value)) OnPropertyChanged(nameof(OutDisplay)); }
        }

        private ushort _rawValue;
        public ushort RawValue
        {
            get => _rawValue;
            set { if (SetProperty(ref _rawValue, value)) OnPropertyChanged(nameof(RawDisplay)); }
        }

        /// <summary>Formatted display: "32768 (50.0%)"</summary>
        public string RawDisplay => $"{_rawValue} ({_rawValue / 655.35:F1}%)";

        /// <summary>OUT readout (#175): forged trigger value in the same
        /// axis-unit format as RawDisplay, derived from LiveValue (0..1).</summary>
        public string OutDisplay => $"{(int)System.Math.Round(_liveValue * 65535.0)} ({_liveValue * 100.0:F1}%)";

        /// <summary>Raw axis index in RawHidState.Axes (custom Extended only, -1 for gamepad).</summary>
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

        public TriggerConfigItem(int index, string title, int axisIndex = -1, string iconLabel = "", bool iconRightSide = false)
        {
            Index = index;
            Title = title;
            AxisIndex = axisIndex;
            IconLabel = iconLabel ?? string.Empty;
            IconRightSide = iconRightSide;
            // Weak event: no unsubscribe needed.
            Resources.Strings.Strings.CultureChanged += OnCultureChanged;
        }

        /// <summary>Instance accessor over <see cref="CurvePresetNames"/>.
        /// x:Static bindings evaluate once and never see the static-ctor
        /// rebuild; see the StickConfigItem twin.</summary>
        public string[] CurvePresetChoices => CurvePresetNames;

        private void OnCultureChanged()
        {
            EnsurePresetsCultureCurrent();
            OnPropertyChanged(nameof(CurvePresetChoices));
            OnPropertyChanged(nameof(PresetName));
        }
    }
}
