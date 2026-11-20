using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Centering card draws of a wheel's Vehicle mode (#588): the
    /// torque curve at the speed in use and at 25 mph, the band friction
    /// adds to it, and the measures read off it. Built when a setting, the
    /// speed or the grip changes, and handed to the chart whole.
    /// </summary>
    public sealed class CenteringPreview
    {
        /// <summary>The reference speed the chart's second line is drawn at:
        /// 25 mph, the lower of the two speeds the paper tests at.</summary>
        public const double ReferenceSpeed = 25 * 0.44704;

        /// <summary>The widest the chart draws either side of center, in
        /// degrees. A wheel that turns less draws to its lock.</summary>
        public const double WidestSpanDegrees = 90.0;

        public SteeringCurve Curve { get; }
        public SteeringCurve Reference { get; }
        public SteeringMeasures Measures { get; }

        /// <summary>Friction in N·m, and the most torque the wheel gives.</summary>
        public double Friction { get; }
        public double WheelTorque { get; }

        /// <summary>How far the chart draws either side of center, in degrees.</summary>
        public double SpanDegrees { get; }

        /// <summary>The most torque the car asks inside the chart's span,
        /// in N·m, and the angle it asks it at, in degrees.</summary>
        public double PeakTorque { get; }
        public double PeakAngleDegrees { get; }

        /// <summary>The car asks more than the wheel has.</summary>
        public bool PeakOverLimit => PeakTorque > WheelTorque + 1e-9;

        public CenteringPreview(in CenteringSettings settings, double speed, double grip)
        {
            double halfRange = settings.Range * Math.PI / 360.0;
            var feel = settings.Car.Feel.Scaled(settings.WeightPercent, settings.OnCenterPercent, settings.AssistPercent);
            Curve = new SteeringCurve(settings.Car, feel, speed, grip, halfRange);
            Reference = new SteeringCurve(settings.Car, feel, ReferenceSpeed, grip, halfRange);
            Measures = SteeringMeasures.From(Curve);
            Friction = settings.Friction;
            WheelTorque = settings.WheelTorque;
            SpanDegrees = Math.Min(WidestSpanDegrees, settings.Range / 2.0);

            double peak = 0, peakAt = 0;
            const int Samples = 360;
            for (int i = 1; i <= Samples; i++)
            {
                double degrees = SpanDegrees * i / Samples;
                double torque = Curve.Torque(degrees * Math.PI / 180.0);
                if (torque > peak) { peak = torque; peakAt = degrees; }
            }
            PeakTorque = peak;
            PeakAngleDegrees = peakAt;
        }
    }

    public partial class PadViewModel
    {
        // ─── Wheel centering (#588), persisted in the PadSetting Centering* fields ───

        /// <summary>The Wheel group's tab, which holds the Wheel card and
        /// the Centering card.</summary>
        public const int WheelTabIndex = 11;

        private bool _centeringLoading;

        private int _centeringMode;
        /// <summary>0 Spring, the Auto Centering Strength slider. 1 Vehicle,
        /// a car's steering torque from the tire model.</summary>
        public int CenteringMode
        {
            get => _centeringMode;
            set
            {
                if (!SetProperty(ref _centeringMode, value == 1 ? 1 : 0)) return;
                RaiseCenteringModeChanged();
                RefreshCenteringPreview();
            }
        }

        public bool CenteringIsVehicle => _centeringMode == 1 && _centeringVehicleAvailable;
        public bool CenteringIsSpring => !CenteringIsVehicle;

        /// <summary>The Auto Centering Strength row shows: Spring mode, on a
        /// wheel that has a spring.</summary>
        public bool CenteringStrengthShown => CenteringIsSpring && _centeringSpringAvailable;

        private void RaiseCenteringModeChanged()
        {
            OnPropertyChanged(nameof(CenteringIsVehicle));
            OnPropertyChanged(nameof(CenteringIsSpring));
            OnPropertyChanged(nameof(CenteringStrengthShown));
            OnPropertyChanged(nameof(CenteringWheelRangeShown));
        }

        private bool _centeringVehicleAvailable = true;
        /// <summary>Whether the selected wheel can run Vehicle mode: a
        /// Logitech, a Fanatec or a Thrustmaster wheel, or a wheel routed
        /// through SDL that has a constant force. Set with the device.</summary>
        public bool CenteringVehicleAvailable
        {
            get => _centeringVehicleAvailable;
            private set
            {
                if (SetProperty(ref _centeringVehicleAvailable, value)) RaiseCenteringModeChanged();
            }
        }

        private bool _centeringSpringAvailable = true;
        /// <summary>Whether the selected wheel has a centering spring for
        /// Auto Centering Strength: every Logitech, Fanatec and Thrustmaster
        /// wheel, and a wheel routed through SDL whose driver lists a
        /// spring. Set with the device.</summary>
        public bool CenteringSpringAvailable
        {
            get => _centeringSpringAvailable;
            private set
            {
                if (!SetProperty(ref _centeringSpringAvailable, value)) return;
                OnPropertyChanged(nameof(CenteringStrengthShown));
                OnPropertyChanged(nameof(CenteringModeSpringLabel));
            }
        }

        /// <summary>The Mode list's first choice: Spring, or Off on a wheel
        /// with no spring, where that choice centers nothing.</summary>
        public string CenteringModeSpringLabel => _centeringSpringAvailable
            ? Strings.Instance.Pad_Centering_Mode_Spring
            : Strings.Instance.Common_Off;

        private string _centeringVehicle = SteeringVehicle.All[0].Id;
        /// <summary>The car's id (<see cref="SteeringVehicle.ById"/>).</summary>
        public string CenteringVehicle
        {
            get => _centeringVehicle;
            set
            {
                if (SetProperty(ref _centeringVehicle, SteeringVehicle.ById(value).Id)) RefreshCenteringPreview();
            }
        }

        private IReadOnlyList<GyroLabeledOption> _centeringVehicleOptions;
        /// <summary>The cars, in the list's order, each under its name in
        /// the interface's language.</summary>
        public IReadOnlyList<GyroLabeledOption> CenteringVehicleOptions => _centeringVehicleOptions ??= new[]
        {
            new GyroLabeledOption(() => Strings.Instance.Pad_Centering_Car_Sedan, SteeringVehicle.Sedan.Id),
            new GyroLabeledOption(() => Strings.Instance.Pad_Centering_Car_Compact, SteeringVehicle.Compact.Id),
            new GyroLabeledOption(() => Strings.Instance.Pad_Centering_Car_Van, SteeringVehicle.Van.Id),
            new GyroLabeledOption(() => Strings.Instance.Pad_Centering_Car_Race, SteeringVehicle.RaceCar.Id),
            new GyroLabeledOption(() => Strings.Instance.Pad_Centering_Car_X1, SteeringVehicle.StanfordX1.Id),
        };

        private int _centeringSpeedSource;
        /// <summary>0 From the Game, 1 Fixed.</summary>
        public int CenteringSpeedSource
        {
            get => _centeringSpeedSource;
            set
            {
                if (SetProperty(ref _centeringSpeedSource, value == 1 ? 1 : 0)) RefreshCenteringPreview();
            }
        }

        private int _centeringFixedSpeed = SpeedUnits.DefaultFixedSpeedKmh;
        /// <summary>The fixed speed in km/h, 0 to 320, as it is saved.</summary>
        public int CenteringFixedSpeed
        {
            get => _centeringFixedSpeed;
            set
            {
                if (!SetProperty(ref _centeringFixedSpeed, Math.Clamp(value, 0, 320))) return;
                OnPropertyChanged(nameof(CenteringFixedSpeedShown));
                RefreshCenteringPreview();
            }
        }

        /// <summary>Whether speeds read in miles an hour on this PC
        /// (<see cref="SpeedUnits.Miles"/>).</summary>
        public static bool SpeedInMiles => SpeedUnits.Miles;

        /// <summary>The unit speeds read in: mph or km/h.</summary>
        public string CenteringSpeedUnit => SpeedInMiles ? "mph" : "km/h";

        /// <summary>The fixed speed's most, in the unit shown.</summary>
        public int CenteringFixedSpeedMost => SpeedInMiles ? 200 : 320;

        /// <summary>The fixed speed in the unit shown.</summary>
        public int CenteringFixedSpeedShown
        {
            get => SpeedInMiles ? (int)Math.Round(_centeringFixedSpeed / SpeedUnits.KmhPerMph) : _centeringFixedSpeed;
            set => CenteringFixedSpeed = SpeedInMiles ? (int)Math.Round(value * SpeedUnits.KmhPerMph) : value;
        }

        private int _centeringWeight = 100, _centeringOnCenter = 100, _centeringAssist = 100, _centeringDamping = 100;

        /// <summary>Steering Weight in percent, the tire moment gain.</summary>
        public int CenteringWeight
        {
            get => _centeringWeight;
            set { if (SetProperty(ref _centeringWeight, Math.Clamp(value, 0, 200))) RefreshCenteringPreview(); }
        }

        /// <summary>On-Center Feel in percent, the jacking stiffness.</summary>
        public int CenteringOnCenter
        {
            get => _centeringOnCenter;
            set { if (SetProperty(ref _centeringOnCenter, Math.Clamp(value, 0, 200))) RefreshCenteringPreview(); }
        }

        /// <summary>Power Assist in percent, the weighting's lower limit.</summary>
        public int CenteringAssist
        {
            get => _centeringAssist;
            set { if (SetProperty(ref _centeringAssist, Math.Clamp(value, 0, 200))) RefreshCenteringPreview(); }
        }

        /// <summary>Damping in percent of what returns the wheel without
        /// swinging past center.</summary>
        public int CenteringDamping
        {
            get => _centeringDamping;
            set => SetProperty(ref _centeringDamping, Math.Clamp(value, 0, 200));
        }

        private int _centeringFriction = 30;
        /// <summary>Friction in hundredths of a newton meter, 0 to 150, as
        /// it is saved.</summary>
        public int CenteringFriction
        {
            get => _centeringFriction;
            set
            {
                if (!SetProperty(ref _centeringFriction, Math.Clamp(value, 0, 150))) return;
                OnPropertyChanged(nameof(CenteringFrictionNm));
                RefreshCenteringPreview();
            }
        }

        /// <summary>Friction in N·m.</summary>
        public double CenteringFrictionNm
        {
            get => _centeringFriction / 100.0;
            set => CenteringFriction = double.IsFinite(value) ? (int)Math.Round(value * 100.0) : _centeringFriction;
        }

        private int _centeringGrip;
        /// <summary>0 From the Game, 1 Dry, 2 Wet, 3 Snow, 4 Ice.</summary>
        public int CenteringGrip
        {
            get => _centeringGrip;
            set { if (SetProperty(ref _centeringGrip, Math.Clamp(value, 0, 4))) RefreshCenteringPreview(); }
        }

        private int _centeringWheelTorque;
        /// <summary>The wheel's torque in tenths of a newton meter as it is
        /// saved, 0 where none is entered and the listed figure
        /// applies.</summary>
        public int CenteringWheelTorque
        {
            get => _centeringWheelTorque;
            set
            {
                if (!SetProperty(ref _centeringWheelTorque, Math.Clamp(value, 0, 500))) return;
                OnPropertyChanged(nameof(CenteringWheelTorqueNm));
                OnPropertyChanged(nameof(CenteringTorqueUnset));
                RefreshCenteringPreview();
            }
        }

        private double _centeringListedTorque;
        /// <summary>The maker's figure for the selected wheel in N·m, or 0
        /// (<see cref="WheelTorqueList"/>). Set with the device.</summary>
        public double CenteringListedTorque
        {
            get => _centeringListedTorque;
            private set
            {
                if (!SetProperty(ref _centeringListedTorque, value)) return;
                OnPropertyChanged(nameof(CenteringWheelTorqueNm));
                OnPropertyChanged(nameof(CenteringTorqueUnset));
            }
        }

        /// <summary>The torque in use, in N·m: the entered figure, the
        /// listed one, or what an unlisted wheel starts from. Entering one
        /// saves it.</summary>
        public double CenteringWheelTorqueNm
        {
            get => _centeringWheelTorque > 0 ? _centeringWheelTorque / 10.0
                : _centeringListedTorque > 0 ? _centeringListedTorque : CenteringSettings.UnlistedWheelTorque;
            set
            {
                if (!double.IsFinite(value)) return;
                int tenths = (int)Math.Round(Math.Clamp(value, 0, 50) * 10.0);
                // The listed figure entered again is no entry: the wheel goes on following its list.
                if (_centeringListedTorque > 0 && tenths == (int)Math.Round(_centeringListedTorque * 10.0)) tenths = 0;
                CenteringWheelTorque = tenths;
                OnPropertyChanged(nameof(CenteringWheelTorqueNm));
            }
        }

        /// <summary>No figure is entered and none is listed, so the forces
        /// are held low.</summary>
        public bool CenteringTorqueUnset => _centeringWheelTorque <= 0 && _centeringListedTorque <= 0;

        private int _centeringWheelRange = 900;
        /// <summary>How far the wheel turns from lock to lock in degrees,
        /// for a wheel whose range PadForge does not set.</summary>
        public int CenteringWheelRange
        {
            get => _centeringWheelRange;
            set { if (SetProperty(ref _centeringWheelRange, Math.Clamp(value, 40, 2520))) RefreshCenteringPreview(); }
        }

        private bool _centeringRangeIsSet = true;
        /// <summary>The Wheel Range row shows in Vehicle mode on a wheel
        /// whose range PadForge does not set: one routed through SDL, or a
        /// native wheel with Set Rotation Range off.</summary>
        public bool CenteringWheelRangeShown => CenteringIsVehicle && !(_centeringRangeIsSet && WheelRotationRangeEnabled);

        /// <summary>The range the model works in, in degrees.</summary>
        private double CenteringRange => _centeringRangeIsSet && WheelRotationRangeEnabled ? WheelRotationRange : _centeringWheelRange;

        private WheelCenteringFacts _centeringFacts = new(true, true, true, 0, CenteringEffects.All, WheelCalibration.FineDamperSteps);

        /// <summary>The facts of the selected device's wheel: its listed
        /// torque, whether PadForge sets its range, and which of the two
        /// modes it can run. Set when the device's settings load, and again
        /// by the page when the wheel connects or leaves.</summary>
        internal void SetCenteringWheel(in WheelCenteringFacts facts)
        {
            if (facts == _centeringFacts) return;
            _centeringFacts = facts;
            CenteringListedTorque = facts.ListedTorque;
            CenteringVehicleAvailable = facts.VehicleAvailable;
            CenteringSpringAvailable = facts.SpringAvailable;
            if (_centeringRangeIsSet != facts.NativeWheel)
            {
                _centeringRangeIsSet = facts.NativeWheel;
                OnPropertyChanged(nameof(CenteringWheelRangeShown));
            }
            RefreshCenteringPreview();
        }

        /// <summary>Loads the Centering section of a device's settings with
        /// the facts of its wheel.</summary>
        internal void LoadCentering(PadSetting ps, in WheelCenteringFacts facts)
        {
            static int Int(string s, int fallback)
                => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
            _centeringLoading = true;
            try
            {
                SetCenteringWheel(facts);
                CenteringMode = Int(ps.CenteringMode, 0);
                CenteringVehicle = ps.CenteringVehicle;
                CenteringSpeedSource = Int(ps.CenteringSpeedSource, 0);
                CenteringFixedSpeed = Int(ps.CenteringFixedSpeed, SpeedUnits.DefaultFixedSpeedKmh);
                CenteringWeight = Int(ps.CenteringWeight, 100);
                CenteringOnCenter = Int(ps.CenteringOnCenter, 100);
                CenteringAssist = Int(ps.CenteringAssist, 100);
                CenteringDamping = Int(ps.CenteringDamping, 100);
                CenteringFriction = Int(ps.CenteringFriction, 30);
                CenteringGrip = Int(ps.CenteringGrip, 0);
                CenteringWheelTorque = Int(ps.CenteringWheelTorque, 0);
                CenteringWheelRange = Int(ps.CenteringWheelRange, 900);
            }
            finally { _centeringLoading = false; }
            RefreshCenteringPreview();
        }

        /// <summary>The Centering section back into a device's settings.</summary>
        public void SaveCentering(PadSetting ps)
        {
            var ic = CultureInfo.InvariantCulture;
            ps.CenteringMode = _centeringMode.ToString(ic);
            ps.CenteringVehicle = _centeringVehicle;
            ps.CenteringSpeedSource = _centeringSpeedSource.ToString(ic);
            ps.CenteringFixedSpeed = _centeringFixedSpeed.ToString(ic);
            ps.CenteringWeight = _centeringWeight.ToString(ic);
            ps.CenteringOnCenter = _centeringOnCenter.ToString(ic);
            ps.CenteringAssist = _centeringAssist.ToString(ic);
            ps.CenteringDamping = _centeringDamping.ToString(ic);
            ps.CenteringFriction = _centeringFriction.ToString(ic);
            ps.CenteringGrip = _centeringGrip.ToString(ic);
            ps.CenteringWheelTorque = _centeringWheelTorque.ToString(ic);
            ps.CenteringWheelRange = _centeringWheelRange.ToString(ic);
        }

        /// <summary>The settings as the engine reads them, from what the
        /// card shows now and what the wheel runs itself: the Release Test
        /// starts from these.</summary>
        public CenteringSettings CenteringSettingsNow()
            => new CenteringSettings(_centeringMode == 1, SteeringVehicle.ById(_centeringVehicle), _centeringWeight, _centeringOnCenter,
                _centeringAssist, _centeringDamping, _centeringFriction / 100.0, _centeringSpeedSource != 1, _centeringFixedSpeed / 3.6,
                _centeringGrip, CenteringWheelTorqueNm, CenteringRange, _centeringFacts.Effects, _centeringFacts.DamperSteps);

        // ─── What the card draws ───

        private CenteringPreview _centeringPreview;
        /// <summary>The chart's curves and the measures, at the speed and
        /// the grip in use.</summary>
        public CenteringPreview CenteringPreview
        {
            get => _centeringPreview;
            private set => SetProperty(ref _centeringPreview, value);
        }

        private double _centeringLiveSpeed = -1, _centeringLiveGrip;
        private bool _centeringLiveActive;

        /// <summary>The speed and the grip the preview is drawn at: a
        /// game's while Vehicle mode runs on one, the settings' own
        /// otherwise.</summary>
        private (double Speed, double Grip) CenteringDrawnAt()
        {
            double grip = _centeringGrip switch
            {
                1 => CenteringSettings.DryGrip,
                2 => CenteringSettings.WetGrip,
                3 => CenteringSettings.SnowGrip,
                4 => CenteringSettings.IceGrip,
                _ => _centeringLiveActive && _centeringLiveGrip > 0 ? _centeringLiveGrip : CenteringSettings.DryGrip,
            };
            double speed = _centeringLiveActive && _centeringLiveSpeed >= 0 ? _centeringLiveSpeed : _centeringFixedSpeed / 3.6;
            return (speed, grip);
        }

        /// <summary>Rebuilds what the card draws from the settings.</summary>
        public void RefreshCenteringPreview()
        {
            if (_centeringLoading) return;
            if (!CenteringIsVehicle)
            {
                CenteringPreview = null;
                return;
            }
            var (speed, grip) = CenteringDrawnAt();
            var preview = new CenteringPreview(CenteringSettingsNow(), speed, grip);
            CenteringPreview = preview;

            var culture = CultureInfo.CurrentCulture;
            var m = preview.Measures;
            CenteringOnCenterFeelText = Figure(m.OnCenterFeel, "F1", culture);
            CenteringStiffnessText = Figure(m.TorqueStiffness, "F2", culture);
            CenteringLinearityText = Figure(m.Linearity, "F0", culture);
            CenteringPeakText = Figure(preview.PeakTorque, "F1", culture);
            CenteringPeakOfText = string.Format(culture, Strings.Instance.Pad_Centering_PeakOf_Format,
                preview.WheelTorque.ToString("F1", culture));
            CenteringPeakOverLimit = preview.PeakOverLimit;
            CenteringSpeedText = SpeedFigure(speed, culture);
            CenteringSpeedLegend = CenteringSpeedText + " " + CenteringSpeedUnit;
            CenteringReferenceLegend = SpeedFigure(CenteringPreview.ReferenceSpeed, culture) + " " + CenteringSpeedUnit;
            CenteringChartName = string.Format(culture, Strings.Instance.Pad_Centering_Chart_Name_Format,
                CenteringSpeedLegend, CenteringPeakText, preview.PeakAngleDegrees.ToString("F0", culture));
        }

        /// <summary>What a figure reads while there is none to show.</summary>
        public const string NoFigure = "–";

        /// <summary>A figure that can fall under zero, with the minus sign
        /// the chart's own labels have, and none on a figure that rounds to
        /// zero.</summary>
        internal static string Signed(double value, int decimals, CultureInfo culture)
        {
            // Rounded as every other figure on the card is, by its format.
            string size = Math.Abs(value).ToString("F" + decimals, culture);
            bool nothing = true;
            foreach (char digit in size)
            {
                if (digit < '1' || digit > '9') continue;
                nothing = false;
                break;
            }
            return (value < 0 && !nothing ? "−" : string.Empty) + size;
        }

        /// <summary>A figure for a tile, or <see cref="NoFigure"/> where
        /// the car never reaches what the measure needs.</summary>
        private static string Figure(double value, string format, CultureInfo culture)
            => double.IsFinite(value) ? value.ToString(format, culture) : NoFigure;

        /// <summary>The range PadForge sets on the wheel is the range the
        /// model works in, so the card follows the Wheel card's two range
        /// settings.</summary>
        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName == nameof(WheelRotationRangeEnabled))
            {
                OnPropertyChanged(nameof(CenteringWheelRangeShown));
                RefreshCenteringPreview();
            }
            else if (e.PropertyName == nameof(WheelRotationRange)) RefreshCenteringPreview();
        }

        private static string SpeedFigure(double metersPerSecond, CultureInfo culture)
            => SpeedUnits.Shown(metersPerSecond, SpeedInMiles).ToString("F0", culture);

        private string _centeringOnCenterFeelText = NoFigure, _centeringStiffnessText = NoFigure, _centeringLinearityText = NoFigure,
            _centeringPeakText = NoFigure, _centeringPeakOfText = string.Empty, _centeringSpeedText = string.Empty,
            _centeringAngleText = string.Empty, _centeringTorqueText = string.Empty, _centeringChartName = string.Empty,
            _centeringStatusText = string.Empty, _centeringSpeedLegend = string.Empty, _centeringReferenceLegend = string.Empty;

        /// <summary>The chart's two legends: the speed its band is drawn at,
        /// and the speed of its second line, each with its unit.</summary>
        public string CenteringSpeedLegend { get => _centeringSpeedLegend; private set => SetProperty(ref _centeringSpeedLegend, value); }
        public string CenteringReferenceLegend { get => _centeringReferenceLegend; private set => SetProperty(ref _centeringReferenceLegend, value); }

        public string CenteringOnCenterFeelText { get => _centeringOnCenterFeelText; private set => SetProperty(ref _centeringOnCenterFeelText, value); }
        public string CenteringStiffnessText { get => _centeringStiffnessText; private set => SetProperty(ref _centeringStiffnessText, value); }
        public string CenteringLinearityText { get => _centeringLinearityText; private set => SetProperty(ref _centeringLinearityText, value); }
        public string CenteringPeakText { get => _centeringPeakText; private set => SetProperty(ref _centeringPeakText, value); }
        public string CenteringPeakOfText { get => _centeringPeakOfText; private set => SetProperty(ref _centeringPeakOfText, value); }

        private bool _centeringPeakOverLimit;
        public bool CenteringPeakOverLimit { get => _centeringPeakOverLimit; private set => SetProperty(ref _centeringPeakOverLimit, value); }

        /// <summary>The speed the model runs at, in the unit shown.</summary>
        public string CenteringSpeedText { get => _centeringSpeedText; private set => SetProperty(ref _centeringSpeedText, value); }

        private bool _centeringSpeedIsLive;
        /// <summary>That speed is a game's, read this moment, and no
        /// setting's: the figure is lit as a live reading is.</summary>
        public bool CenteringSpeedIsLive { get => _centeringSpeedIsLive; private set => SetProperty(ref _centeringSpeedIsLive, value); }

        /// <summary>The wheel's angle in degrees and the torque at the rim
        /// in N·m, as the wheel runs.</summary>
        public string CenteringAngleText { get => _centeringAngleText; private set => SetProperty(ref _centeringAngleText, value); }
        public string CenteringTorqueText { get => _centeringTorqueText; private set => SetProperty(ref _centeringTorqueText, value); }

        /// <summary>The chart's spoken name: what it plots and where the
        /// curve peaks.</summary>
        public string CenteringChartName { get => _centeringChartName; private set => SetProperty(ref _centeringChartName, value); }

        private double _centeringLiveAngle, _centeringLiveTorque;
        private bool _centeringLiveShown;

        /// <summary>The chart's live mark shows: Vehicle mode is running
        /// on the wheel.</summary>
        public bool CenteringLiveShown { get => _centeringLiveShown; private set => SetProperty(ref _centeringLiveShown, value); }

        /// <summary>Where the chart's live mark stands, in degrees and in
        /// N·m, while <see cref="CenteringLiveShown"/>.</summary>
        public double CenteringLiveAngle { get => _centeringLiveAngle; private set => SetProperty(ref _centeringLiveAngle, value); }
        public double CenteringLiveTorque { get => _centeringLiveTorque; private set => SetProperty(ref _centeringLiveTorque, value); }

        /// <summary>The line under the card's rows that says what holds the
        /// force back, or nothing.</summary>
        public string CenteringStatusText
        {
            get => _centeringStatusText;
            private set
            {
                if (SetProperty(ref _centeringStatusText, value ?? string.Empty)) OnPropertyChanged(nameof(CenteringHasStatus));
            }
        }

        public bool CenteringHasStatus => _centeringStatusText.Length > 0;

        private bool _centeringMeasured;
        /// <summary>The Release Test has measured the selected wheel.</summary>
        public bool CenteringMeasured
        {
            get => _centeringMeasured;
            set
            {
                if (SetProperty(ref _centeringMeasured, value)) OnPropertyChanged(nameof(CenteringReleaseStatus));
            }
        }

        /// <summary>The Release Test row's state.</summary>
        public string CenteringReleaseStatus => _centeringMeasured
            ? Strings.Instance.Pad_Centering_ReleaseTest_Measured
            : Strings.Instance.Pad_Centering_ReleaseTest_NotRun;

        /// <summary>Takes the wheel's last frame from the engine: the live
        /// mark and readouts, the speed and the grip a game gives, and what
        /// holds the force back. Called on the page's timer while the card
        /// shows.</summary>
        public void UpdateCenteringLive(in CenteringLive live)
        {
            var culture = CultureInfo.CurrentCulture;
            bool active = live.Active && CenteringIsVehicle;
            if (active)
            {
                CenteringLiveAngle = live.AngleDegrees;
                CenteringLiveTorque = live.TorqueNm;
                CenteringAngleText = Signed(live.AngleDegrees, 1, culture);
                CenteringTorqueText = Math.Abs(live.TorqueNm).ToString("F2", culture);
            }
            // With no reading the two readouts are not shown, so they hold nothing.
            else
            {
                CenteringAngleText = string.Empty;
                CenteringTorqueText = string.Empty;
            }
            CenteringLiveShown = active;

            // A game's speed moves the curve, so the preview follows it, a mile or a kilometer an hour at a time.
            double speed = active && live.SpeedFromGame ? live.Speed : -1;
            double grip = active && live.GripFromGame ? live.Grip : 0;
            CenteringSpeedIsLive = speed >= 0;
            bool moved = active != _centeringLiveActive
                || Math.Abs(SpeedStep(speed) - SpeedStep(_centeringLiveSpeed)) >= 1
                || Math.Abs(grip - _centeringLiveGrip) >= 0.02;
            _centeringLiveActive = active;
            if (moved)
            {
                _centeringLiveSpeed = speed;
                _centeringLiveGrip = grip;
                RefreshCenteringPreview();
            }

            string status = string.Empty;
            if (CenteringIsVehicle)
            {
                if (active && live.Stopped) status = Strings.Instance.Pad_Centering_Status_Stopped;
                else if (active && live.HeldBack) status = Strings.Instance.Pad_Centering_Status_HeldBack;
                else if (CenteringTorqueUnset) status = Strings.Instance.Pad_Centering_Status_TorqueUnset;
            }
            CenteringStatusText = status;
        }

        private static double SpeedStep(double metersPerSecond)
            => metersPerSecond < 0 ? -1 : SpeedUnits.Shown(metersPerSecond, SpeedInMiles);

        /// <summary>The words the card reads from the string table, after the
        /// interface's language changes: the Mode list's first choice, the
        /// Release Test row's state, and the lines built from a format.</summary>
        private void RaiseCenteringWords()
        {
            OnPropertyChanged(nameof(CenteringModeSpringLabel));
            OnPropertyChanged(nameof(CenteringReleaseStatus));
            RefreshCenteringPreview();
        }

        // ─── Resets ───

        /// <summary>A setting of the Centering card back to its default, for
        /// the row's reset button. False for a name that is not the
        /// card's.</summary>
        private bool ResetCenteringSetting(string name)
        {
            switch (name)
            {
                case nameof(CenteringMode): CenteringMode = 0; break;
                case nameof(CenteringVehicle): CenteringVehicle = SteeringVehicle.All[0].Id; break;
                case nameof(CenteringSpeedSource): CenteringSpeedSource = 0; break;
                case nameof(CenteringWeight): CenteringWeight = 100; break;
                case nameof(CenteringOnCenter): CenteringOnCenter = 100; break;
                case nameof(CenteringAssist): CenteringAssist = 100; break;
                case nameof(CenteringDamping): CenteringDamping = 100; break;
                case nameof(CenteringGrip): CenteringGrip = 0; break;
                case nameof(CenteringWheelRange): CenteringWheelRange = 900; break;
                // The three below are typed in another unit than they are
                // saved in, so the box that shows the unit reads again too.
                case nameof(CenteringFriction):
                    CenteringFriction = 30;
                    OnPropertyChanged(nameof(CenteringFrictionNm));
                    break;
                case nameof(CenteringFixedSpeed):
                    CenteringFixedSpeed = SpeedUnits.DefaultFixedSpeedKmh;
                    OnPropertyChanged(nameof(CenteringFixedSpeedShown));
                    break;
                case nameof(CenteringWheelTorque):
                    CenteringWheelTorque = 0;
                    OnPropertyChanged(nameof(CenteringWheelTorqueNm));
                    break;
                default: return false;
            }
            return true;
        }

        private RelayCommand _resetCenteringAllCommand;

        /// <summary>Every setting on the Centering card back to its
        /// default: Spring mode, with the slider at nothing. The wheel's
        /// measurement is the wheel's, and stays.</summary>
        public RelayCommand ResetCenteringAllCommand => _resetCenteringAllCommand ??= new RelayCommand(() =>
        {
            CenteringMode = 0;
            WheelAutoCenter = 0;
            CenteringVehicle = SteeringVehicle.All[0].Id;
            CenteringSpeedSource = 0;
            CenteringFixedSpeed = SpeedUnits.DefaultFixedSpeedKmh;
            CenteringWeight = 100;
            CenteringOnCenter = 100;
            CenteringAssist = 100;
            CenteringDamping = 100;
            CenteringFriction = 30;
            CenteringGrip = 0;
            CenteringWheelTorque = 0;
            CenteringWheelRange = 900;
        });
    }
}
