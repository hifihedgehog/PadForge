using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Engine.Data;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents one thumbstick section in the dynamic Sticks tab.
    /// For gamepad presets (Xbox 360/DS4): index 0 = Left, index 1 = Right.
    /// For custom vJoy: index 0..N based on ThumbstickCount.
    /// </summary>
    public class StickConfigItem : ObservableObject
    {
        public static string[] CurvePresetNames { get; } =
            [.. Array.ConvertAll(Common.CurveLut.Presets, p => p.Name), "Custom"];

        public string PresetNameX => Common.CurveLut.MatchPreset(SensitivityCurveX);
        public string PresetNameY => Common.CurveLut.MatchPreset(SensitivityCurveY);

        public string Title { get; }
        public int Index { get; }

        // ── Digit conversion helpers (stick axes use signed 16-bit: ±32768) ──
        private static int PctToDigit(double pct) => (int)Math.Round(pct / 100.0 * 32768.0);
        private static double DigitToPct(int digit) => digit / 32768.0 * 100.0;

        private double _deadZoneX;
        public double DeadZoneX
        {
            get => _deadZoneX;
            set { if (SetProperty(ref _deadZoneX, Math.Clamp(value, 0, 100))) { OnPropertyChanged(nameof(DeadZoneXDigit)); RebuildCurvePoints(); } }
        }
        public int DeadZoneXDigit
        {
            get => PctToDigit(_deadZoneX);
            set => DeadZoneX = DigitToPct(value);
        }

        private double _deadZoneY;
        public double DeadZoneY
        {
            get => _deadZoneY;
            set { if (SetProperty(ref _deadZoneY, Math.Clamp(value, 0, 100))) { OnPropertyChanged(nameof(DeadZoneYDigit)); RebuildCurvePoints(); } }
        }
        public int DeadZoneYDigit
        {
            get => PctToDigit(_deadZoneY);
            set => DeadZoneY = DigitToPct(value);
        }

        private double _antiDeadZoneX;
        public double AntiDeadZoneX
        {
            get => _antiDeadZoneX;
            set { if (SetProperty(ref _antiDeadZoneX, Math.Clamp(value, 0, 100))) OnPropertyChanged(nameof(AntiDeadZoneXDigit)); }
        }
        public int AntiDeadZoneXDigit
        {
            get => PctToDigit(_antiDeadZoneX);
            set => AntiDeadZoneX = DigitToPct(value);
        }

        private double _antiDeadZoneY;
        public double AntiDeadZoneY
        {
            get => _antiDeadZoneY;
            set { if (SetProperty(ref _antiDeadZoneY, Math.Clamp(value, 0, 100))) OnPropertyChanged(nameof(AntiDeadZoneYDigit)); }
        }
        public int AntiDeadZoneYDigit
        {
            get => PctToDigit(_antiDeadZoneY);
            set => AntiDeadZoneY = DigitToPct(value);
        }

        private double _linear;
        public double Linear
        {
            get => _linear;
            set { if (SetProperty(ref _linear, Math.Clamp(value, 0, 100))) RebuildCurvePoints(); }
        }

        private string _sensitivityCurveX = "0,0;1,1";
        public string SensitivityCurveX
        {
            get => _sensitivityCurveX;
            set { if (SetProperty(ref _sensitivityCurveX, value ?? "0,0;1,1")) { RebuildCurvePoints(); OnPropertyChanged(nameof(PresetNameX)); } }
        }

        private string _sensitivityCurveY = "0,0;1,1";
        public string SensitivityCurveY
        {
            get => _sensitivityCurveY;
            set { if (SetProperty(ref _sensitivityCurveY, value ?? "0,0;1,1")) { RebuildCurvePoints(); OnPropertyChanged(nameof(PresetNameY)); } }
        }

        private double _maxRangeX = 100;
        public double MaxRangeX
        {
            get => _maxRangeX;
            set { if (SetProperty(ref _maxRangeX, Math.Clamp(value, 1, 100))) { OnPropertyChanged(nameof(MaxRangeXDigit)); RebuildCurvePoints(); } }
        }
        public int MaxRangeXDigit
        {
            get => PctToDigit(_maxRangeX);
            set => MaxRangeX = DigitToPct(value);
        }

        private double _maxRangeY = 100;
        public double MaxRangeY
        {
            get => _maxRangeY;
            set { if (SetProperty(ref _maxRangeY, Math.Clamp(value, 1, 100))) { OnPropertyChanged(nameof(MaxRangeYDigit)); RebuildCurvePoints(); } }
        }
        public int MaxRangeYDigit
        {
            get => PctToDigit(_maxRangeY);
            set => MaxRangeY = DigitToPct(value);
        }

        private double _maxRangeXNeg = 100;
        public double MaxRangeXNeg
        {
            get => _maxRangeXNeg;
            set { if (SetProperty(ref _maxRangeXNeg, Math.Clamp(value, 1, 100))) { OnPropertyChanged(nameof(MaxRangeXNegDigit)); RebuildCurvePoints(); } }
        }
        public int MaxRangeXNegDigit
        {
            get => PctToDigit(_maxRangeXNeg);
            set => MaxRangeXNeg = DigitToPct(value);
        }

        private double _maxRangeYNeg = 100;
        public double MaxRangeYNeg
        {
            get => _maxRangeYNeg;
            set { if (SetProperty(ref _maxRangeYNeg, Math.Clamp(value, 1, 100))) { OnPropertyChanged(nameof(MaxRangeYNegDigit)); RebuildCurvePoints(); } }
        }
        public int MaxRangeYNegDigit
        {
            get => PctToDigit(_maxRangeYNeg);
            set => MaxRangeYNeg = DigitToPct(value);
        }

        private double _centerOffsetX;
        public double CenterOffsetX
        {
            get => _centerOffsetX;
            set { if (SetProperty(ref _centerOffsetX, Math.Clamp(value, -100, 100))) OnPropertyChanged(nameof(CenterOffsetXDigit)); }
        }
        public int CenterOffsetXDigit
        {
            get => PctToDigit(_centerOffsetX);
            set => CenterOffsetX = DigitToPct(value);
        }

        private double _centerOffsetY;
        public double CenterOffsetY
        {
            get => _centerOffsetY;
            set { if (SetProperty(ref _centerOffsetY, Math.Clamp(value, -100, 100))) OnPropertyChanged(nameof(CenterOffsetYDigit)); }
        }
        public int CenterOffsetYDigit
        {
            get => PctToDigit(_centerOffsetY);
            set => CenterOffsetY = DigitToPct(value);
        }

        private DeadZoneShape _deadZoneShape = DeadZoneShape.ScaledRadial;
        public DeadZoneShape DeadZoneShape
        {
            get => _deadZoneShape;
            set
            {
                if (SetProperty(ref _deadZoneShape, value))
                {
                    OnPropertyChanged(nameof(DeadZoneShapeIndex));
                    OnPropertyChanged(nameof(IsAxialShape));
                    OnPropertyChanged(nameof(IsRadialShape));
                    OnPropertyChanged(nameof(IsSlopedShape));
                    OnPropertyChanged(nameof(IsHybridShape));
                    OnPropertyChanged(nameof(HasSlopedWedges));
                }
            }
        }

        /// <summary>Display order for the DZ shape dropdown (default first, then grouped by type).</summary>
        private static readonly DeadZoneShape[] ShapeDisplayOrder =
        {
            DeadZoneShape.ScaledRadial,      // 0 — default
            DeadZoneShape.Radial,            // 1
            DeadZoneShape.Axial,             // 2
            DeadZoneShape.Hybrid,            // 3
            DeadZoneShape.SlopedScaledAxial, // 4
            DeadZoneShape.SlopedAxial,       // 5
        };

        /// <summary>Int wrapper for ComboBox SelectedIndex binding (maps display order ↔ enum).</summary>
        public int DeadZoneShapeIndex
        {
            get => Array.IndexOf(ShapeDisplayOrder, _deadZoneShape) is int i and >= 0 ? i : 0;
            set
            {
                if (value >= 0 && value < ShapeDisplayOrder.Length)
                    DeadZoneShape = ShapeDisplayOrder[value];
            }
        }

        /// <summary>True for Axial (independent per-axis, cross-shaped DZ region).</summary>
        public bool IsAxialShape => _deadZoneShape == DeadZoneShape.Axial;

        /// <summary>True for Radial / Scaled Radial (circle/ellipse DZ gate only).</summary>
        public bool IsRadialShape => _deadZoneShape == DeadZoneShape.Radial
                                  || _deadZoneShape == DeadZoneShape.ScaledRadial;

        /// <summary>True for Sloped Axial / Sloped Scaled Axial (wedge-only DZ).</summary>
        public bool IsSlopedShape => _deadZoneShape == DeadZoneShape.SlopedAxial
                                  || _deadZoneShape == DeadZoneShape.SlopedScaledAxial;

        /// <summary>True for Hybrid (circle + wedges).</summary>
        public bool IsHybridShape => _deadZoneShape == DeadZoneShape.Hybrid;

        /// <summary>True for shapes with sloped wedge regions (Sloped, Sloped Scaled, Hybrid).</summary>
        public bool HasSlopedWedges => _deadZoneShape == DeadZoneShape.SlopedAxial
                                    || _deadZoneShape == DeadZoneShape.SlopedScaledAxial
                                    || _deadZoneShape == DeadZoneShape.Hybrid;

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

        // ── Sensitivity curve charts (using CurveEditor UserControl now) ──

        // Live input values for the CurveEditor's LiveInput binding (normalized 0-1 unsigned).
        private double _liveInputX;
        public double LiveInputX { get => _liveInputX; set => SetProperty(ref _liveInputX, value); }
        private double _liveInputY;
        public double LiveInputY { get => _liveInputY; set => SetProperty(ref _liveInputY, value); }

        public void RebuildCurvePoints() { /* CurveEditor redraws via CurveString binding */ }

        /// <summary>Apply curve using spline LUT. Used by preview and vJoy processing.</summary>
        internal static double ApplyCurve(double magnitude, string curveString)
        {
            var lut = CurveLut.GetOrBuild(curveString);
            if (lut == null) return magnitude;
            return CurveLut.Lookup(lut, Math.Clamp(magnitude, 0, 1));
        }

        /// <summary>
        /// Builds a 0..1 → 0..1 curve for triggers (unsigned, dead zone flattened).
        /// </summary>
        internal static PointCollection BuildTriggerCurvePoints(string curveString, double deadZone, double maxRange, int chartSize = 120, int sampleCount = 64)
        {
            double dzNorm = deadZone / 100.0;
            double mrNorm = maxRange / 100.0;
            if (mrNorm <= dzNorm) mrNorm = dzNorm + 0.01;
            var lut = CurveLut.GetOrBuild(curveString);

            var pts = new PointCollection(sampleCount + 1);
            for (int i = 0; i <= sampleCount; i++)
            {
                double t = i / (double)sampleCount;
                double output;
                if (t < dzNorm)
                    output = 0;
                else
                {
                    double remapped = Math.Min((t - dzNorm) / (mrNorm - dzNorm), 1.0);
                    output = lut != null ? CurveLut.Lookup(lut, remapped) : remapped;
                }
                pts.Add(new Point(t * chartSize, (1.0 - output) * chartSize));
            }
            return pts;
        }

        public StickConfigItem(int index, string title, int axisXIndex = -1, int axisYIndex = -1)
        {
            Index = index;
            Title = title;
            AxisXIndex = axisXIndex;
            AxisYIndex = axisYIndex;
        }

        // ── Reset commands ──

        private ICommand _resetAllCommand;
        public ICommand ResetAllCommand => _resetAllCommand ??= new RelayCommand(() =>
        {
            DeadZoneShape = DeadZoneShape.ScaledRadial;
            CenterOffsetX = 0; CenterOffsetY = 0;
            DeadZoneX = 0; DeadZoneY = 0;
            AntiDeadZoneX = 0; AntiDeadZoneY = 0;
            Linear = 0;
            SensitivityCurveX = "0,0;1,1"; SensitivityCurveY = "0,0;1,1";
            MaxRangeX = 100; MaxRangeY = 100;
            MaxRangeXNeg = 100; MaxRangeYNeg = 100;
        });

        private ICommand _resetDeadZoneShapeCommand;
        public ICommand ResetDeadZoneShapeCommand => _resetDeadZoneShapeCommand ??= new RelayCommand(() => DeadZoneShape = DeadZoneShape.ScaledRadial);

        private ICommand _resetCenterOffsetXCommand, _resetCenterOffsetYCommand;
        public ICommand ResetCenterOffsetXCommand => _resetCenterOffsetXCommand ??= new RelayCommand(() => CenterOffsetX = 0);
        public ICommand ResetCenterOffsetYCommand => _resetCenterOffsetYCommand ??= new RelayCommand(() => CenterOffsetY = 0);
        private ICommand _resetDeadZoneXCommand, _resetDeadZoneYCommand;
        public ICommand ResetDeadZoneXCommand => _resetDeadZoneXCommand ??= new RelayCommand(() => DeadZoneX = 0);
        public ICommand ResetDeadZoneYCommand => _resetDeadZoneYCommand ??= new RelayCommand(() => DeadZoneY = 0);
        private ICommand _resetAntiDeadZoneXCommand, _resetAntiDeadZoneYCommand;
        public ICommand ResetAntiDeadZoneXCommand => _resetAntiDeadZoneXCommand ??= new RelayCommand(() => AntiDeadZoneX = 0);
        public ICommand ResetAntiDeadZoneYCommand => _resetAntiDeadZoneYCommand ??= new RelayCommand(() => AntiDeadZoneY = 0);
        private ICommand _resetLinearCommand;
        public ICommand ResetLinearCommand => _resetLinearCommand ??= new RelayCommand(() => Linear = 0);
        private ICommand _resetSensitivityXCommand, _resetSensitivityYCommand;
        public ICommand ResetSensitivityXCommand => _resetSensitivityXCommand ??= new RelayCommand(() => SensitivityCurveX = "0,0;1,1");
        public ICommand ResetSensitivityYCommand => _resetSensitivityYCommand ??= new RelayCommand(() => SensitivityCurveY = "0,0;1,1");
        private ICommand _resetMaxRangeXCommand, _resetMaxRangeYCommand;
        public ICommand ResetMaxRangeXCommand => _resetMaxRangeXCommand ??= new RelayCommand(() => MaxRangeX = 100);
        public ICommand ResetMaxRangeYCommand => _resetMaxRangeYCommand ??= new RelayCommand(() => MaxRangeY = 100);
        private ICommand _resetMaxRangeXNegCommand, _resetMaxRangeYNegCommand;
        public ICommand ResetMaxRangeXNegCommand => _resetMaxRangeXNegCommand ??= new RelayCommand(() => MaxRangeXNeg = 100);
        public ICommand ResetMaxRangeYNegCommand => _resetMaxRangeYNegCommand ??= new RelayCommand(() => MaxRangeYNeg = 100);

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
