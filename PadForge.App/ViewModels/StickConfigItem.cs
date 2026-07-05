using System;
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
    /// For gamepad presets (Xbox / PlayStation): index 0 = Left, index 1 = Right.
    /// For custom Extended: index 0..N based on ThumbstickCount.
    /// </summary>
    public class StickConfigItem : ObservableObject
    {
        public static string[] CurvePresetNames { get; private set; } =
            Common.CurveLut.BuildPresetDisplayNames();

        static StickConfigItem()
        {
            Resources.Strings.Strings.CultureChanged += () =>
                CurvePresetNames = Common.CurveLut.BuildPresetDisplayNames();
        }

        public string PresetNameX => Common.CurveLut.MatchPreset(SensitivityCurveX);
        public string PresetNameY => Common.CurveLut.MatchPreset(SensitivityCurveY);

        public string Title { get; }
        public int Index { get; }
        public string IconLabel { get; }

        /// <summary>Per-card "Reset {Title}" tooltip — composes the localized
        /// section reset format with the stick's own localized name so the
        /// tooltip reads "Reset Left Stick" / "Reset Right Stick" / etc.
        /// matching the Pad_ResetLeftTrigger / Pad_ResetRightTrigger style
        /// used by the Adaptive Triggers tab. Recomputed on culture change
        /// would require notification, but Title is set at construction
        /// from the localized resource and stays valid for the lifetime
        /// of the slot's stick set.</summary>
        public string ResetAllToolTip
            => string.Format(Resources.Strings.Strings.Instance.Pad_ResetSection_Format, Title);

        // ── Digit conversion helpers (stick axes use signed 16-bit: ±32768) ──
        private static int PctToDigit(double pct) => (int)Math.Round(pct / 100.0 * 32768.0);
        private static double DigitToPct(int digit) => digit / 32768.0 * 100.0;

        private double _deadZoneX;
        public double DeadZoneX
        {
            get => _deadZoneX;
            set { if (SetProperty(ref _deadZoneX, Math.Clamp(value, 0, 100))) { OnPropertyChanged(nameof(DeadZoneXDigit)); OnPropertyChanged(nameof(DeadZoneSteelGeometry)); RebuildCurvePoints(); } }
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
            set { if (SetProperty(ref _deadZoneY, Math.Clamp(value, 0, 100))) { OnPropertyChanged(nameof(DeadZoneYDigit)); OnPropertyChanged(nameof(DeadZoneSteelGeometry)); RebuildCurvePoints(); } }
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
            set
            {
                var clamped = Math.Clamp(value, 1, 100);
                if (SetProperty(ref _maxRangeYNeg, clamped))
                {
                    OnPropertyChanged(nameof(MaxRangeYNegDigit));
                    RebuildCurvePoints();
                }
            }
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
                    OnPropertyChanged(nameof(DeadZoneSteelGeometry));
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

        /// <summary>Steel DZ fill for the radar (#175 competitor item 3): the
        /// dead region as one geometry the plot paints in ashen steel.
        /// Coordinates are fixed to the 200x200 plot (center 100,100 so
        /// 1% of deflection = 1px of radius, matching
        /// StickTrailBehavior.PlotSize). Radial shapes fill an ellipse
        /// (rx/ry = DZ X/Y%), axial shapes fill the cross-band (vertical
        /// band 2*DZX% wide, horizontal band 2*DZY% tall), Hybrid fills
        /// both. Sloped variants reuse the axial cross-band silhouette:
        /// their true wedge region tapers to zero at center, and the band
        /// is the honest at-a-glance stand-in. Frozen; recomputed via
        /// change notification from DeadZoneX/DeadZoneY/DeadZoneShape.</summary>
        public Geometry DeadZoneSteelGeometry
        {
            get
            {
                double rx = _deadZoneX;   // DZ% of the 100px plot radius == px
                double ry = _deadZoneY;
                Geometry g;
                if (IsRadialShape)
                {
                    g = new EllipseGeometry(new Point(100, 100), rx, ry);
                }
                else if (HasSlopedWedges)
                {
                    // Sloped variants (user report 2026-07-04: the axial
                    // cross was wrong for these): four wedges from center
                    // to the plot edges, the exact silhouette the proven
                    // SlopedWedgeGeometryConverter drew. X dies inside the
                    // vertical wedge pair, Y inside the horizontal pair.
                    var geo = new PathGeometry();
                    AddDzWedge(geo, 100, 100, 100 - rx, 0, 100 + rx, 0);
                    AddDzWedge(geo, 100, 100, 100 - rx, 200, 100 + rx, 200);
                    AddDzWedge(geo, 100, 100, 200, 100 - ry, 200, 100 + ry);
                    AddDzWedge(geo, 100, 100, 0, 100 - ry, 0, 100 + ry);
                    g = geo;
                }
                else
                {
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    group.Children.Add(new RectangleGeometry(new Rect(100 - rx, 0, rx * 2, 200)));
                    group.Children.Add(new RectangleGeometry(new Rect(0, 100 - ry, 200, ry * 2)));
                    if (IsHybridShape)
                        group.Children.Add(new EllipseGeometry(new Point(100, 100), rx, ry));
                    g = group;
                }
                g.Freeze();
                return g;
            }
        }

        private static void AddDzWedge(PathGeometry geo,
            double cx, double cy, double x1, double y1, double x2, double y2)
        {
            var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment(new Point(x1, y1), true));
            fig.Segments.Add(new LineSegment(new Point(x2, y2), true));
            geo.Figures.Add(fig);
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

        /// <summary>IN readout (#175): pre-pipeline position in the same
        /// axis-unit format as RawDisplay, derived from RawPosX/RawPosY.</summary>
        public string InDisplay =>
            $"X: {(int)Math.Round(_rawPosX * 65535.0 - 32768.0)} ({_rawPosX * 100.0:F1}%)  Y: {(int)Math.Round(_rawPosY * 65535.0 - 32768.0)} ({_rawPosY * 100.0:F1}%)";

        /// <summary>Formatted display string: "X: -1234 (50.0%)  Y: 5678 (58.7%)"</summary>
        public string RawDisplay =>
            $"X: {_rawX} ({(_rawX + 32768.0) / 655.35:F1}%)  Y: {_rawY} ({(_rawY + 32768.0) / 655.35:F1}%)";

        /// <summary>Unprocessed hardware value for calibration (not affected by offset/deadzone).</summary>
        // Raw-stage dot position (0-1, pre-pipeline) for the two-stage
        // XY plot (#175). LiveX/LiveY hold the processed stage.
        public double RawPosX
        {
            get => _rawPosX;
            set { if (SetProperty(ref _rawPosX, value)) OnPropertyChanged(nameof(InDisplay)); }
        }
        private double _rawPosX = 0.5;
        public double RawPosY
        {
            get => _rawPosY;
            set { if (SetProperty(ref _rawPosY, value)) OnPropertyChanged(nameof(InDisplay)); }
        }
        private double _rawPosY = 0.5;

        public short HardwareRawX { get; set; }

        /// <summary>Unprocessed hardware value for calibration (not affected by offset/deadzone).</summary>
        public short HardwareRawY { get; set; }

        /// <summary>Raw axis index for X in ExtendedRawState.Axes (custom Extended only, -1 for gamepad).</summary>
        public int AxisXIndex { get; }

        /// <summary>Raw axis index for Y in ExtendedRawState.Axes (custom Extended only, -1 for gamepad).</summary>
        public int AxisYIndex { get; }

        // ── Sensitivity curve charts (using CurveEditor UserControl now) ──

        // Live input values for the CurveEditor's LiveInput binding (normalized 0-1 unsigned).
        private double _liveInputX;
        public double LiveInputX { get => _liveInputX; set => SetProperty(ref _liveInputX, value); }
        private double _liveInputY;
        public double LiveInputY { get => _liveInputY; set => SetProperty(ref _liveInputY, value); }

        public void RebuildCurvePoints() { /* CurveEditor redraws via CurveString binding */ }

        /// <summary>Apply curve using spline LUT. Used by preview and Extended processing.</summary>
        internal static double ApplyCurve(double magnitude, string curveString)
        {
            var lut = CurveLut.GetOrBuild(curveString);
            if (lut == null) return magnitude;
            return CurveLut.Lookup(lut, Math.Clamp(magnitude, 0, 1));
        }

        public StickConfigItem(int index, string title, int axisXIndex = -1, int axisYIndex = -1, string iconLabel = "")
        {
            Index = index;
            Title = title;
            AxisXIndex = axisXIndex;
            AxisYIndex = axisYIndex;
            IconLabel = iconLabel ?? string.Empty;
        }

        // ── Steering mode (v3.4 #94) ──
        // Per-stick steering source kind + tunables. SteeringKind is the
        // MappingSource.Kind the engine dispatches on; the params feed the matching
        // Param* fields on the stick's MappingSet rows at build time.
        // Motion Lean is no longer a per-stick mode — it's the "Motion Lean" INPUT
        // descriptor (picked from the input dropdown like any gyro input; tuning
        // lives on the gyro tab's Motion Steering card). The engine MotionLeanX
        // kind still backs that descriptor's evaluation. A stored "MotionLeanX"
        // here falls back to Linear via IndexOf.
        private static readonly string[] SteeringModeKinds =
            { "Direct", "WindingStick", "AngleToAxisX", "AngleToAxisY" };

        private int _steeringModeIndex; // 0 = Linear (Direct)
        public int SteeringModeIndex
        {
            get => _steeringModeIndex;
            set
            {
                if (SetProperty(ref _steeringModeIndex, Math.Clamp(value, 0, SteeringModeKinds.Length - 1)))
                {
                    OnPropertyChanged(nameof(SteeringKind));
                    OnPropertyChanged(nameof(IsSteeringActive));
                    OnPropertyChanged(nameof(IsWindingMode));
                    OnPropertyChanged(nameof(IsAngleMode));
                }
            }
        }

        /// <summary>The MappingSource.Kind this stick's steering mode maps to
        /// ("Direct" when Linear).</summary>
        public string SteeringKind => SteeringModeKinds[Math.Clamp(_steeringModeIndex, 0, SteeringModeKinds.Length - 1)];

        /// <summary>Sets the mode index from a MappingSource.Kind string (load path).</summary>
        public void SetSteeringKind(string kind)
        {
            int i = Array.IndexOf(SteeringModeKinds, kind ?? "Direct");
            SteeringModeIndex = i >= 0 ? i : 0;
        }

        public bool IsSteeringActive => _steeringModeIndex != 0;
        public bool IsWindingMode => _steeringModeIndex == 1;
        public bool IsAngleMode => _steeringModeIndex == 2 || _steeringModeIndex == 3;

        private double _windRangeDeg = 900;
        public double WindRangeDeg { get => _windRangeDeg; set => SetProperty(ref _windRangeDeg, Math.Clamp(value, 90, 2520)); }
        private double _windPower = 1;
        public double WindPower { get => _windPower; set => SetProperty(ref _windPower, Math.Clamp(value, 0, 4)); }
        private double _windUnwindRate = 1800;
        public double WindUnwindRate { get => _windUnwindRate; set => SetProperty(ref _windUnwindRate, Math.Clamp(value, 0, 10000)); }

        private double _angleInnerDz;
        public double AngleInnerDz { get => _angleInnerDz; set => SetProperty(ref _angleInnerDz, Math.Clamp(value, 0, 89)); }
        private double _angleOuterDz = 10;
        public double AngleOuterDz { get => _angleOuterDz; set => SetProperty(ref _angleOuterDz, Math.Clamp(value, 0, 89)); }

        // Motion-lean deadzones + controller orientation moved to PadViewModel's
        // Motion Steering (gyro tab); the per-stick steering modes here are Winding
        // and Angle only, which use the Wind*/Angle* params above.

        private ICommand _resetSteeringModeCommand;
        public ICommand ResetSteeringModeCommand => _resetSteeringModeCommand ??= new RelayCommand(() => SteeringModeIndex = 0);
        private ICommand _resetWindRangeCommand, _resetWindPowerCommand, _resetWindUnwindRateCommand;
        public ICommand ResetWindRangeCommand => _resetWindRangeCommand ??= new RelayCommand(() => WindRangeDeg = 900);
        public ICommand ResetWindPowerCommand => _resetWindPowerCommand ??= new RelayCommand(() => WindPower = 1);
        public ICommand ResetWindUnwindRateCommand => _resetWindUnwindRateCommand ??= new RelayCommand(() => WindUnwindRate = 1800);
        private ICommand _resetAngleInnerDzCommand, _resetAngleOuterDzCommand;
        public ICommand ResetAngleInnerDzCommand => _resetAngleInnerDzCommand ??= new RelayCommand(() => AngleInnerDz = 0);
        public ICommand ResetAngleOuterDzCommand => _resetAngleOuterDzCommand ??= new RelayCommand(() => AngleOuterDz = 10);

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
            SteeringModeIndex = 0;
            WindRangeDeg = 900; WindPower = 1; WindUnwindRate = 1800;
            AngleInnerDz = 0; AngleOuterDz = 10;
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
