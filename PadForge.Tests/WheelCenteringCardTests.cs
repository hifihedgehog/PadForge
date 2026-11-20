using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Tests;

/// <summary>
/// The Centering card and the Release Test window (#588): the card's view
/// model against the settings the engine reads, what each kind of wheel is
/// offered, the chart's scales, the window's sentences, and the card as the
/// page writes it.
/// </summary>
[Collection("SettingsManagerStatics")]
public class WheelCenteringCardTests
{
    private static readonly WheelCenteringFacts Native =
        new(true, true, true, 2.3, CenteringEffects.All, WheelCalibration.FourBitDamperSteps);

    private static readonly WheelCenteringFacts Generic =
        new(false, true, true, 0, CenteringEffects.Spring | CenteringEffects.Damper, WheelCalibration.FineDamperSteps);

    private static PadSetting VehicleSetting() => new() { CenteringMode = "1" };

    private static PadViewModel Loaded(PadSetting ps, WheelCenteringFacts facts)
    {
        var vm = new PadViewModel(0);
        vm.LoadCentering(ps, facts);
        return vm;
    }

    private static string[] Saved(PadSetting ps) => new[]
    {
        ps.CenteringMode, ps.CenteringVehicle, ps.CenteringSpeedSource, ps.CenteringFixedSpeed, ps.CenteringWeight, ps.CenteringOnCenter,
        ps.CenteringAssist, ps.CenteringDamping, ps.CenteringFriction, ps.CenteringGrip, ps.CenteringWheelTorque, ps.CenteringWheelRange,
    };

    // ── Load and save ──

    [Fact]
    public void EverySettingOfTheCard_LoadsAndSaves()
    {
        var ps = new PadSetting
        {
            CenteringMode = "1", CenteringVehicle = "van", CenteringSpeedSource = "1", CenteringFixedSpeed = "64",
            CenteringWeight = "150", CenteringOnCenter = "80", CenteringAssist = "120", CenteringDamping = "70",
            CenteringFriction = "45", CenteringGrip = "3", CenteringWheelTorque = "80", CenteringWheelRange = "540",
        };
        var vm = Loaded(ps, Generic);
        Assert.Equal(1, vm.CenteringMode);
        Assert.Equal("van", vm.CenteringVehicle);
        Assert.Equal(1, vm.CenteringSpeedSource);
        Assert.Equal(64, vm.CenteringFixedSpeed);
        Assert.Equal(150, vm.CenteringWeight);
        Assert.Equal(80, vm.CenteringOnCenter);
        Assert.Equal(120, vm.CenteringAssist);
        Assert.Equal(70, vm.CenteringDamping);
        Assert.Equal(0.45, vm.CenteringFrictionNm, 6);
        Assert.Equal(3, vm.CenteringGrip);
        Assert.Equal(8.0, vm.CenteringWheelTorqueNm, 6);
        Assert.Equal(540, vm.CenteringWheelRange);

        var back = new PadSetting();
        vm.SaveCentering(back);
        Assert.Equal(Saved(ps), Saved(back));
    }

    [Fact]
    public void ASettingThatDoesNotParse_LoadsAsItsDefault_AndAValuePastItsRangeIsHeld()
    {
        var ps = new PadSetting
        {
            CenteringMode = "x", CenteringVehicle = "no such car", CenteringFixedSpeed = "", CenteringWeight = "900",
            CenteringFriction = "-4", CenteringGrip = "9", CenteringWheelTorque = "9999", CenteringWheelRange = "3",
        };
        var vm = Loaded(ps, Generic);
        var fresh = new PadSetting();
        Assert.Equal(0, vm.CenteringMode);
        Assert.Equal(fresh.CenteringVehicle, vm.CenteringVehicle);
        Assert.Equal(SpeedUnits.DefaultFixedSpeedKmh, vm.CenteringFixedSpeed);
        Assert.Equal(200, vm.CenteringWeight);
        Assert.Equal(0, vm.CenteringFriction);
        Assert.Equal(4, vm.CenteringGrip);
        Assert.Equal(500, vm.CenteringWheelTorque);
        Assert.Equal(40, vm.CenteringWheelRange);
    }

    /// <summary>The card draws the curve from the same settings the force
    /// feedback pass runs: what the view model builds is what
    /// CenteringSettings.From reads out of the settings it saved.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCardBuildsTheSettingsTheEngineReads(bool native)
    {
        var facts = native ? Native : Generic;
        var ps = new PadSetting
        {
            CenteringMode = "1", CenteringVehicle = "compact", CenteringSpeedSource = "1", CenteringFixedSpeed = "88",
            CenteringWeight = "130", CenteringOnCenter = "60", CenteringAssist = "140", CenteringDamping = "90",
            CenteringFriction = "20", CenteringGrip = "2", CenteringWheelTorque = "0", CenteringWheelRange = "360",
        };
        var vm = new PadViewModel(0);
        vm.LoadWheelRotationRange(720, null);
        vm.WheelRotationRangeEnabled = true;
        vm.LoadCentering(ps, facts);
        var back = new PadSetting();
        vm.SaveCentering(back);

        var mine = vm.CenteringSettingsNow();
        var engine = CenteringSettings.From(back, facts.ListedTorque, native ? 720 : 0, facts.Effects, facts.DamperSteps);
        Assert.Equal(engine.Vehicle, mine.Vehicle);
        Assert.Same(engine.Car, mine.Car);
        Assert.Equal(engine.WeightPercent, mine.WeightPercent);
        Assert.Equal(engine.OnCenterPercent, mine.OnCenterPercent);
        Assert.Equal(engine.AssistPercent, mine.AssistPercent);
        Assert.Equal(engine.DampingPercent, mine.DampingPercent);
        Assert.Equal(engine.Friction, mine.Friction, 9);
        Assert.Equal(engine.SpeedFromGame, mine.SpeedFromGame);
        Assert.Equal(engine.FixedSpeed, mine.FixedSpeed, 9);
        Assert.Equal(engine.GripChoice, mine.GripChoice);
        Assert.Equal(engine.WheelTorque, mine.WheelTorque, 9);
        Assert.Equal(engine.Range, mine.Range, 9);
        Assert.Equal(engine.Effects, mine.Effects);
        Assert.Equal(engine.DamperSteps, mine.DamperSteps);
        // The two wheels differ where they should: the range PadForge sets,
        // the listed torque against the unlisted start, the damper's steps.
        Assert.Equal(native ? 720 : 360, mine.Range);
        Assert.Equal(native ? 2.3 : CenteringSettings.UnlistedWheelTorque, mine.WheelTorque);
        Assert.Equal(native ? WheelCalibration.FourBitDamperSteps : WheelCalibration.FineDamperSteps, mine.DamperSteps);
    }

    // ── What each wheel is offered ──

    [Fact]
    public void AWheelsFacts_AreTheForcePassesOwn()
    {
        // A Logitech G29: the maker's torque, a damper in four bits, every effect.
        var g29 = WheelCenteringFacts.Of(new UserDevice { VendorId = 0x046D, ProdId = 0xC24F, CapType = InputDeviceType.Driving });
        Assert.True(g29.NativeWheel);
        Assert.True(g29.VehicleAvailable);
        Assert.True(g29.SpringAvailable);
        Assert.True(g29.HasCard);
        Assert.Equal(2.3, g29.ListedTorque);
        Assert.Equal(CenteringEffects.All, g29.Effects);
        Assert.Equal(WheelCalibration.FourBitDamperSteps, g29.DamperSteps);
        Assert.Equal(WheelCenteringWriter.DamperSteps(0x046D, 0xC24F), g29.DamperSteps);

        // A Thrustmaster T300RS: no maker's figure, a damper in hundredths.
        var t300 = WheelCenteringFacts.Of(new UserDevice { VendorId = 0x044F, ProdId = 0xB66E, CapType = InputDeviceType.Driving });
        Assert.True(ThrustmasterRawHidWriter.IsThrustmasterWheel(0x044F, 0xB66E));
        Assert.True(t300.NativeWheel);
        Assert.Equal(0, t300.ListedTorque);
        Assert.Equal(WheelCalibration.FineDamperSteps, t300.DamperSteps);

        // A wheel no native writer drives is asked of its driver, and one
        // that is not connected has no driver to ask.
        var unplugged = WheelCenteringFacts.Of(new UserDevice { VendorId = 0x1234, ProdId = 0x5678, CapType = InputDeviceType.Driving });
        Assert.False(unplugged.HasCard);
        Assert.Equal(default, WheelCenteringFacts.Of(null));
        Assert.Equal(default, WheelCenteringFacts.Of(new UserDevice { VendorId = 0x045E, ProdId = 0x028E, CapType = InputDeviceType.Gamepad }));
        // The page's two tests of a native wheel are this one.
        Assert.True(WheelCenteringFacts.IsNativeWheel(0x046D, 0xC24F));
        Assert.False(WheelCenteringFacts.IsNativeWheel(0x045E, 0x028E));
    }

    [Fact]
    public void AWheelThatCannotRunVehicleMode_StaysInSpring()
    {
        var springOnly = new WheelCenteringFacts(false, false, true, 0, CenteringEffects.Spring, WheelCalibration.FineDamperSteps);
        var vm = Loaded(VehicleSetting(), springOnly);
        // The saved mode is kept for a wheel that can run it.
        Assert.Equal(1, vm.CenteringMode);
        Assert.False(vm.CenteringVehicleAvailable);
        Assert.False(vm.CenteringIsVehicle);
        Assert.True(vm.CenteringIsSpring);
        Assert.True(vm.CenteringStrengthShown);
        Assert.False(vm.CenteringWheelRangeShown);
        Assert.Null(vm.CenteringPreview);
        var back = new PadSetting();
        vm.SaveCentering(back);
        Assert.Equal("1", back.CenteringMode);

        // The same settings on a wheel that can: the page tells the card when the wheel connects.
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SetCenteringWheel(Generic);
        Assert.True(vm.CenteringIsVehicle);
        Assert.False(vm.CenteringStrengthShown);
        Assert.NotNull(vm.CenteringPreview);
        Assert.Contains(nameof(PadViewModel.CenteringIsVehicle), changed);
        Assert.Contains(nameof(PadViewModel.CenteringStrengthShown), changed);
        Assert.Contains(nameof(PadViewModel.CenteringWheelRangeShown), changed);
    }

    [Fact]
    public void AWheelWithNoSpring_ReadsOffForItsFirstMode_AndShowsNoStrength()
    {
        var s = Strings.Instance;
        var vm = Loaded(new PadSetting(), Generic);
        Assert.Equal(s.Pad_Centering_Mode_Spring, vm.CenteringModeSpringLabel);
        Assert.True(vm.CenteringStrengthShown);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SetCenteringWheel(new WheelCenteringFacts(false, true, false, 0, CenteringEffects.None, WheelCalibration.FineDamperSteps));
        Assert.Equal(s.Common_Off, vm.CenteringModeSpringLabel);
        Assert.False(vm.CenteringStrengthShown);
        Assert.Contains(nameof(PadViewModel.CenteringModeSpringLabel), changed);
        // With nothing to run itself, the wheel still has Vehicle mode, from the PC.
        vm.CenteringMode = 1;
        Assert.True(vm.CenteringIsVehicle);
        Assert.Equal(CenteringEffects.None, vm.CenteringSettingsNow().Effects);
    }

    [Fact]
    public void TheWheelRangeRow_ShowsWherePadForgeSetsNoRange()
    {
        var vm = new PadViewModel(0);
        vm.LoadWheelRotationRange(900, null);
        vm.WheelRotationRangeEnabled = true;
        vm.LoadCentering(new PadSetting { CenteringMode = "1", CenteringWheelRange = "360" }, Native);
        // PadForge sets this wheel's range, so the range is that one.
        Assert.False(vm.CenteringWheelRangeShown);
        Assert.Equal(900, vm.CenteringSettingsNow().Range);
        Assert.Equal(CenteringPreview.WidestSpanDegrees, vm.CenteringPreview.SpanDegrees);

        // Set Rotation Range off leaves the range to the wheel, and the card asks for it.
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.WheelRotationRangeEnabled = false;
        Assert.True(vm.CenteringWheelRangeShown);
        Assert.Contains(nameof(PadViewModel.CenteringWheelRangeShown), changed);
        Assert.Equal(360, vm.CenteringSettingsNow().Range);
        // A wheel that turns 360 degrees draws to its lock, not to 90.
        Assert.Equal(90.0, vm.CenteringPreview.SpanDegrees);
        vm.CenteringWheelRange = 120;
        Assert.Equal(60.0, vm.CenteringPreview.SpanDegrees);

        // The range PadForge sets moves the curve as the Wheel card's slider moves.
        vm.WheelRotationRangeEnabled = true;
        vm.WheelRotationRange = 100;
        Assert.Equal(50.0, vm.CenteringPreview.SpanDegrees);

        // A wheel routed through SDL has no range from PadForge at all.
        var generic = Loaded(new PadSetting { CenteringMode = "1", CenteringWheelRange = "360" }, Generic);
        Assert.True(generic.CenteringWheelRangeShown);
        Assert.Equal(360, generic.CenteringSettingsNow().Range);
        // In Spring mode the row is not shown: the spring has no use for a range.
        generic.CenteringMode = 0;
        Assert.False(generic.CenteringWheelRangeShown);
    }

    [Fact]
    public void WheelTorque_IsTheEnteredFigure_ThenTheListedOne_ThenTheUnlistedStart()
    {
        var vm = Loaded(VehicleSetting(), Native);
        Assert.Equal(2.3, vm.CenteringWheelTorqueNm);
        Assert.False(vm.CenteringTorqueUnset);
        Assert.Equal(0, vm.CenteringWheelTorque);

        vm.CenteringWheelTorqueNm = 5.04;
        Assert.Equal(50, vm.CenteringWheelTorque);
        Assert.Equal(5.0, vm.CenteringWheelTorqueNm);
        Assert.Equal(5.0, vm.CenteringPreview.WheelTorque);
        // The listed figure typed again is no entry: the wheel follows its list.
        vm.CenteringWheelTorqueNm = 2.3;
        Assert.Equal(0, vm.CenteringWheelTorque);
        Assert.Equal(2.3, vm.CenteringWheelTorqueNm);
        // Nothing that is no number, and nothing past what a wheel gives.
        vm.CenteringWheelTorqueNm = double.NaN;
        Assert.Equal(0, vm.CenteringWheelTorque);
        vm.CenteringWheelTorqueNm = 400;
        Assert.Equal(500, vm.CenteringWheelTorque);

        var unlisted = Loaded(VehicleSetting(), Generic);
        Assert.Equal(CenteringSettings.UnlistedWheelTorque, unlisted.CenteringWheelTorqueNm);
        Assert.True(unlisted.CenteringTorqueUnset);
        // The card says so, running or not, and stops saying it once a figure is entered.
        unlisted.UpdateCenteringLive(default);
        Assert.Equal(Strings.Instance.Pad_Centering_Status_TorqueUnset, unlisted.CenteringStatusText);
        Assert.True(unlisted.CenteringHasStatus);
        unlisted.CenteringWheelTorqueNm = 8;
        unlisted.UpdateCenteringLive(default);
        Assert.Equal(string.Empty, unlisted.CenteringStatusText);
        Assert.False(unlisted.CenteringHasStatus);
        // Spring mode has no force to hold low.
        var spring = Loaded(new PadSetting(), Generic);
        spring.UpdateCenteringLive(default);
        Assert.Equal(string.Empty, spring.CenteringStatusText);
    }

    [Fact]
    public void Speeds_ReadInTheRegionsUnit_AndSaveInKilometersAnHour()
    {
        bool miles = PadViewModel.SpeedInMiles;
        var vm = Loaded(new PadSetting { CenteringMode = "1", CenteringSpeedSource = "1", CenteringFixedSpeed = "100" }, Native);
        Assert.Equal(miles ? "mph" : "km/h", vm.CenteringSpeedUnit);
        Assert.Equal(miles ? 62 : 100, vm.CenteringFixedSpeedShown);
        Assert.Equal(miles ? 200 : 320, vm.CenteringFixedSpeedMost);
        Assert.Equal(vm.CenteringFixedSpeedShown.ToString("F0", CultureInfo.CurrentCulture), vm.CenteringSpeedText);
        Assert.Equal(vm.CenteringSpeedText + " " + vm.CenteringSpeedUnit, vm.CenteringSpeedLegend);
        // The chart's second line is the car at 25 mph, written in the unit shown.
        Assert.Equal((miles ? "25" : "40") + " " + vm.CenteringSpeedUnit, vm.CenteringReferenceLegend);

        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.CenteringFixedSpeedShown = 60;
        Assert.Equal(miles ? 97 : 60, vm.CenteringFixedSpeed);
        Assert.Contains(nameof(PadViewModel.CenteringFixedSpeedShown), changed);
        Assert.Equal(60, vm.CenteringFixedSpeedShown);
        // The slider's far end is inside what is saved.
        vm.CenteringFixedSpeedShown = vm.CenteringFixedSpeedMost;
        Assert.Equal(320, vm.CenteringFixedSpeed);
        var back = new PadSetting();
        vm.SaveCentering(back);
        Assert.Equal("320", back.CenteringFixedSpeed);
    }

    /// <summary>A wheel's fixed speed starts round in the unit it is read in:
    /// 60 mph where speeds read in miles, 100 km/h elsewhere. The card, a
    /// fresh device's settings and the force feedback pass start from the
    /// one figure.</summary>
    [Fact]
    public void TheFixedSpeed_StartsRoundInTheUnitItIsReadIn()
    {
        Assert.True(SpeedUnits.MilesIn(new RegionInfo("US")));
        Assert.True(SpeedUnits.MilesIn(new RegionInfo("GB")));
        Assert.False(SpeedUnits.MilesIn(new RegionInfo("DE")));
        Assert.False(SpeedUnits.MilesIn(new RegionInfo("CA")));
        Assert.False(SpeedUnits.MilesIn(new RegionInfo("JP")));
        Assert.False(SpeedUnits.MilesIn(null));
        Assert.Equal(97, SpeedUnits.FixedSpeedKmhFor(true));
        Assert.Equal(100, SpeedUnits.FixedSpeedKmhFor(false));
        Assert.Equal(60, SpeedUnits.Shown(97 / 3.6, true));
        Assert.Equal(100, SpeedUnits.Shown(100 / 3.6, false));
        Assert.Equal(PadViewModel.SpeedInMiles, SpeedUnits.Miles);
        Assert.Equal(SpeedUnits.FixedSpeedKmhFor(SpeedUnits.Miles), SpeedUnits.DefaultFixedSpeedKmh);

        // A fresh device's settings, the card with nothing loaded, and the engine reading an empty setting.
        string start = SpeedUnits.DefaultFixedSpeedKmh.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(start, new PadSetting().CenteringFixedSpeed);
        var vm = new PadViewModel(0);
        Assert.Equal(SpeedUnits.DefaultFixedSpeedKmh, vm.CenteringFixedSpeed);
        Assert.Equal(SpeedUnits.Miles ? 60 : 100, vm.CenteringFixedSpeedShown);
        var unset = CenteringSettings.From(new PadSetting { CenteringFixedSpeed = "" }, 0, 0);
        Assert.Equal(SpeedUnits.DefaultFixedSpeedKmh / 3.6, unset.FixedSpeed, 9);
        // The row's reset goes back to it.
        vm.CenteringFixedSpeed = 200;
        vm.ResetSettingCommand.Execute(nameof(PadViewModel.CenteringFixedSpeed));
        Assert.Equal(SpeedUnits.DefaultFixedSpeedKmh, vm.CenteringFixedSpeed);
    }

    // ── What the card draws ──

    [Fact]
    public void ThePreview_IsTheCarAtTheSpeedInUse_WithItsMeasures()
    {
        var culture = CultureInfo.CurrentCulture;
        var vm = Loaded(new PadSetting { CenteringMode = "1", CenteringSpeedSource = "1", CenteringFixedSpeed = "97", CenteringWheelTorque = "50" }, Native);
        var preview = vm.CenteringPreview;
        Assert.NotNull(preview);
        Assert.Equal(97 / 3.6, preview.Curve.Speed, 6);
        Assert.Equal(CenteringPreview.ReferenceSpeed, preview.Reference.Speed, 6);
        Assert.Equal(RoadGrip.Dry, preview.Curve.Grip, 6);
        Assert.Equal(0.30, preview.Friction, 6);
        Assert.Equal(5.0, preview.WheelTorque, 6);

        // The measures are the curve's, as SteeringMeasures reads them.
        var measures = SteeringMeasures.From(preview.Curve);
        Assert.Equal(measures.OnCenterFeel.ToString("F1", culture), vm.CenteringOnCenterFeelText);
        Assert.Equal(measures.TorqueStiffness.ToString("F2", culture), vm.CenteringStiffnessText);
        Assert.Equal(measures.Linearity.ToString("F0", culture), vm.CenteringLinearityText);
        Assert.Equal(preview.PeakTorque.ToString("F1", culture), vm.CenteringPeakText);
        Assert.Equal(string.Format(culture, Strings.Instance.Pad_Centering_PeakOf_Format, 5.0.ToString("F1", culture)), vm.CenteringPeakOfText);

        // The peak is the most the curve asks inside the span, at the angle it asks it.
        double most = 0, at = 0;
        for (int i = 1; i <= 360; i++)
        {
            double degrees = preview.SpanDegrees * i / 360;
            double torque = preview.Curve.Torque(degrees * Math.PI / 180.0);
            if (torque > most) { most = torque; at = degrees; }
        }
        Assert.True(most > 0);
        Assert.Equal(most, preview.PeakTorque, 9);
        Assert.Equal(at, preview.PeakAngleDegrees, 9);
        Assert.Equal(preview.PeakTorque > preview.WheelTorque + 1e-9, vm.CenteringPeakOverLimit);

        // The chart's spoken name says what it plots and where it peaks.
        Assert.Equal(string.Format(culture, Strings.Instance.Pad_Centering_Chart_Name_Format, vm.CenteringSpeedLegend, vm.CenteringPeakText,
            preview.PeakAngleDegrees.ToString("F0", culture)), vm.CenteringChartName);

        // A setting that shapes the curve redraws it, and Damping, which shapes none of it, does not.
        vm.CenteringWeight = 150;
        Assert.NotSame(preview, vm.CenteringPreview);
        Assert.True(vm.CenteringPreview.PeakTorque > preview.PeakTorque);
        var held = vm.CenteringPreview;
        vm.CenteringDamping = 60;
        Assert.Same(held, vm.CenteringPreview);
        // A wheel that gives less than the car asks cuts the curve's top, and the card marks the figure.
        vm.CenteringWheelTorqueNm = 1.0;
        Assert.True(vm.CenteringPreview.PeakOverLimit);
        Assert.True(vm.CenteringPeakOverLimit);
        // Spring mode draws nothing.
        vm.CenteringMode = 0;
        Assert.Null(vm.CenteringPreview);
    }

    [Fact]
    public void ALiveFrame_MovesTheMark_AndAGamesSpeedAndGripMoveTheCurve()
    {
        var culture = CultureInfo.CurrentCulture;
        var vm = Loaded(new PadSetting { CenteringMode = "1", CenteringFixedSpeed = "100", CenteringWheelTorque = "50" }, Native);
        Assert.False(vm.CenteringLiveShown);
        Assert.Equal(string.Empty, vm.CenteringAngleText);
        Assert.Equal(string.Empty, vm.CenteringTorqueText);
        Assert.Equal(100 / 3.6, vm.CenteringPreview.Curve.Speed, 6);

        var frame = new CenteringFrame { AngleDegrees = -10.04, TorqueNm = -1.816, Speed = 30.0, Grip = RoadGrip.Wet };
        vm.UpdateCenteringLive(new CenteringLive(true, frame, true, true));
        Assert.True(vm.CenteringLiveShown);
        Assert.Equal(-10.04, vm.CenteringLiveAngle);
        Assert.Equal(-1.816, vm.CenteringLiveTorque);
        // A real minus sign, as the chart's labels have, and none on an angle that rounds to nothing.
        Assert.Equal("\u2212" + 10.0.ToString("F1", culture), vm.CenteringAngleText);
        Assert.Equal(0.0.ToString("F1", culture), PadViewModel.Signed(-0.04, 1, culture));
        Assert.Equal(12.35.ToString("F1", culture), PadViewModel.Signed(12.35, 1, culture));
        // The torque reads as a size: the mark shows which way it pushes.
        Assert.Equal(1.816.ToString("F2", culture), vm.CenteringTorqueText);
        Assert.True(vm.CenteringSpeedIsLive);
        Assert.Equal(30.0, vm.CenteringPreview.Curve.Speed, 6);
        Assert.Equal(RoadGrip.Wet, vm.CenteringPreview.Curve.Grip, 6);

        // A speed that moved by less than one of the unit shown keeps the curve.
        var held = vm.CenteringPreview;
        frame.Speed = 30.05;
        vm.UpdateCenteringLive(new CenteringLive(true, frame, true, true));
        Assert.Same(held, vm.CenteringPreview);
        frame.Speed = 40.0;
        vm.UpdateCenteringLive(new CenteringLive(true, frame, true, true));
        Assert.NotSame(held, vm.CenteringPreview);
        Assert.Equal(40.0, vm.CenteringPreview.Curve.Speed, 6);

        // A grip preset is the user's word, whatever the game sends.
        vm.CenteringGrip = 4;
        Assert.Equal(RoadGrip.Ice, vm.CenteringPreview.Curve.Grip, 6);
        vm.CenteringGrip = 0;
        Assert.Equal(RoadGrip.Wet, vm.CenteringPreview.Curve.Grip, 6);

        // The mode runs on its fixed speed while no game sends one: the mark stays, the figure is no live one.
        vm.UpdateCenteringLive(new CenteringLive(true, frame, false, false));
        Assert.True(vm.CenteringLiveShown);
        Assert.False(vm.CenteringSpeedIsLive);
        Assert.Equal(100 / 3.6, vm.CenteringPreview.Curve.Speed, 6);
        Assert.Equal(RoadGrip.Dry, vm.CenteringPreview.Curve.Grip, 6);

        // Nothing polled: the mark and the readouts rest.
        vm.UpdateCenteringLive(default);
        Assert.False(vm.CenteringLiveShown);
        Assert.Equal(string.Empty, vm.CenteringAngleText);
        Assert.Equal(string.Empty, vm.CenteringTorqueText);
        // Spring mode shows no mark for a frame that says it is live.
        vm.CenteringMode = 0;
        vm.UpdateCenteringLive(new CenteringLive(true, frame, true, true));
        Assert.False(vm.CenteringLiveShown);
    }

    [Fact]
    public void WhatHoldsTheForceBack_IsSaidUnderTheRows()
    {
        var s = Strings.Instance;
        var vm = Loaded(new PadSetting { CenteringMode = "1", CenteringWheelTorque = "50" }, Native);
        var frame = new CenteringFrame { HeldBack = true };
        vm.UpdateCenteringLive(new CenteringLive(true, frame, false, false));
        Assert.Equal(s.Pad_Centering_Status_HeldBack, vm.CenteringStatusText);
        Assert.True(vm.CenteringHasStatus);
        // Stopped is the stronger word.
        frame.Stopped = true;
        vm.UpdateCenteringLive(new CenteringLive(true, frame, false, false));
        Assert.Equal(s.Pad_Centering_Status_Stopped, vm.CenteringStatusText);
        // A frame that is not live says nothing of the wheel.
        vm.UpdateCenteringLive(new CenteringLive(false, frame, false, false));
        Assert.Equal(string.Empty, vm.CenteringStatusText);
        Assert.False(vm.CenteringHasStatus);
    }

    [Fact]
    public void TheReleaseTestRow_SaysWhetherTheWheelIsMeasured()
    {
        var s = Strings.Instance;
        var vm = new PadViewModel(0);
        Assert.Equal(s.Pad_Centering_ReleaseTest_NotRun, vm.CenteringReleaseStatus);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.CenteringMeasured = true;
        Assert.Equal(s.Pad_Centering_ReleaseTest_Measured, vm.CenteringReleaseStatus);
        Assert.Contains(nameof(PadViewModel.CenteringReleaseStatus), changed);
    }

    // ── Resets ──

    [Fact]
    public void EveryRowsReset_RestoresItsDefault_AndReadsItsBoxAgain()
    {
        var ps = new PadSetting
        {
            CenteringMode = "1", CenteringVehicle = "race", CenteringSpeedSource = "1", CenteringFixedSpeed = "64",
            CenteringWeight = "150", CenteringOnCenter = "80", CenteringAssist = "120", CenteringDamping = "70",
            CenteringFriction = "45", CenteringGrip = "3", CenteringWheelTorque = "80", CenteringWheelRange = "540",
        };
        var fresh = new PadSetting();
        var defaults = new Dictionary<string, (Func<PadSetting, string> Saved, string Shown)>
        {
            [nameof(PadViewModel.CenteringVehicle)] = (p => p.CenteringVehicle, null),
            [nameof(PadViewModel.CenteringSpeedSource)] = (p => p.CenteringSpeedSource, null),
            [nameof(PadViewModel.CenteringFixedSpeed)] = (p => p.CenteringFixedSpeed, nameof(PadViewModel.CenteringFixedSpeedShown)),
            [nameof(PadViewModel.CenteringWeight)] = (p => p.CenteringWeight, null),
            [nameof(PadViewModel.CenteringOnCenter)] = (p => p.CenteringOnCenter, null),
            [nameof(PadViewModel.CenteringAssist)] = (p => p.CenteringAssist, null),
            [nameof(PadViewModel.CenteringDamping)] = (p => p.CenteringDamping, null),
            [nameof(PadViewModel.CenteringFriction)] = (p => p.CenteringFriction, nameof(PadViewModel.CenteringFrictionNm)),
            [nameof(PadViewModel.CenteringGrip)] = (p => p.CenteringGrip, null),
            [nameof(PadViewModel.CenteringWheelTorque)] = (p => p.CenteringWheelTorque, nameof(PadViewModel.CenteringWheelTorqueNm)),
            [nameof(PadViewModel.CenteringWheelRange)] = (p => p.CenteringWheelRange, null),
            [nameof(PadViewModel.CenteringMode)] = (p => p.CenteringMode, null),
        };
        foreach (var (name, want) in defaults)
        {
            var vm = Loaded(ps, Generic);
            Assert.True(PadViewModel.CanResetSetting(name), name);
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.ResetSettingCommand.Execute(name);
            var back = new PadSetting();
            vm.SaveCentering(back);
            Assert.Equal(want.Saved(fresh), want.Saved(back));
            // The row's own setting alone: every other one keeps what it had.
            var kept = Saved(back).Zip(Saved(ps)).Count(pair => pair.First == pair.Second);
            Assert.Equal(11, kept);
            Assert.Contains(name, changed);
            if (want.Shown != null) Assert.Contains(want.Shown, changed);
        }

        // A reset of a value already at its default still reads its box again,
        // so text typed and not yet taken is put back.
        var still = Loaded(new PadSetting(), Generic);
        var raised = new List<string>();
        still.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        still.ResetSettingCommand.Execute(nameof(PadViewModel.CenteringFriction));
        Assert.Contains(nameof(PadViewModel.CenteringFriction), raised);
        Assert.Contains(nameof(PadViewModel.CenteringFrictionNm), raised);
    }

    [Fact]
    public void ResetAll_PutsTheCardBack_AndLeavesTheWheelCardAlone_AndEachCardsResetIsItsOwn()
    {
        var ps = new PadSetting
        {
            CenteringMode = "1", CenteringVehicle = "van", CenteringSpeedSource = "1", CenteringFixedSpeed = "64",
            CenteringWeight = "150", CenteringOnCenter = "80", CenteringAssist = "120", CenteringDamping = "70",
            CenteringFriction = "45", CenteringGrip = "3", CenteringWheelTorque = "80", CenteringWheelRange = "540",
        };
        var vm = new PadViewModel(0);
        vm.LoadWheelRotationRange(540, null);
        vm.WheelRotationRangeEnabled = false;
        vm.WheelRpmLeds = true;
        vm.WheelAutoCenter = 40;
        vm.LoadCentering(ps, Native);
        vm.CenteringMeasured = true;

        vm.ResetCenteringAllCommand.Execute(null);
        var back = new PadSetting();
        vm.SaveCentering(back);
        Assert.Equal(Saved(new PadSetting()), Saved(back));
        // Auto Centering Strength is the card's Spring mode, so it goes back too.
        Assert.Equal(0, vm.WheelAutoCenter);
        // The Wheel card's settings are its own, and the wheel's measurement is the wheel's.
        Assert.Equal(540, vm.WheelRotationRange);
        Assert.False(vm.WheelRotationRangeEnabled);
        Assert.True(vm.WheelRpmLeds);
        Assert.True(vm.CenteringMeasured);

        // The Wheel card's Reset All no longer reaches the strength, which left it.
        vm.WheelAutoCenter = 40;
        vm.CenteringWeight = 150;
        vm.ResetWheelAllCommand.Execute(null);
        Assert.Equal(900, vm.WheelRotationRange);
        Assert.True(vm.WheelRotationRangeEnabled);
        Assert.False(vm.WheelRpmLeds);
        Assert.Equal(40, vm.WheelAutoCenter);
        Assert.Equal(150, vm.CenteringWeight);
    }

    // ── The charts ──

    [Theory]
    [InlineData(5.5, 2.0)]
    [InlineData(1.1, 0.5)]
    [InlineData(27.5, 10.0)]
    [InlineData(0.33, 0.1)]
    [InlineData(4.0, 1.0)]
    [InlineData(0.0, 1.0)]
    public void ATorqueScale_StepsByOneTwoOrFive(double reach, double step)
    {
        Assert.Equal(step, SteeringChartBase.NiceStep(reach), 9);
        // Two to four steps reach the top.
        if (reach > 0) Assert.InRange(reach / SteeringChartBase.NiceStep(reach), 1.999, 4.001);
    }

    [Theory]
    [InlineData(90.0, 30.0)]
    [InlineData(60.0, 20.0)]
    [InlineData(45.0, 15.0)]
    [InlineData(20.0, 5.0)]
    [InlineData(4.0, 1.0)]
    public void AnAngleScale_StepsByAThirdOfItsSpan(double span, double step)
        => Assert.Equal(step, SteeringTorqueChart.AngleStep(span));

    [Fact]
    public void TheTorqueChart_DrawsToTheWheelsLimit_WhileTheLimitIsNear()
    {
        var vm = Loaded(new PadSetting { CenteringMode = "1", CenteringSpeedSource = "1", CenteringFixedSpeed = "97", CenteringWheelTorque = "50" }, Native);
        var preview = vm.CenteringPreview;
        // The band's top is the torque with friction added, held to what the wheel gives.
        double top = Math.Min(preview.PeakTorque + preview.Friction, preview.WheelTorque);
        Assert.True(preview.WheelTorque <= 2 * top);
        Assert.Equal(preview.WheelTorque * 1.1, SteeringTorqueChart.Reach(preview, out bool shown), 9);
        Assert.True(shown);

        // A base that gives far more than the car asks would flatten the curve, so its limit is off the plot.
        vm.CenteringWheelTorqueNm = 25;
        preview = vm.CenteringPreview;
        top = preview.PeakTorque + preview.Friction;
        Assert.True(preview.WheelTorque > 2 * top);
        Assert.Equal(top * 1.1, SteeringTorqueChart.Reach(preview, out shown), 9);
        Assert.False(shown);
    }

    [Fact]
    public void TheReleaseChart_DrawsBothReleasesFromOneSide()
    {
        // The test lets the wheel go from the side it stands on, so the second release may start
        // across from the first. The chart turns that one over.
        Assert.Equal(1.0, ReleaseTraceChart.Side(new[] { 90.0, 40.0, -3.0 }));
        Assert.Equal(-1.0, ReleaseTraceChart.Side(new[] { -90.0, -40.0, 3.0 }));
        Assert.Equal(1.0, ReleaseTraceChart.Side(Array.Empty<double>()));
        Assert.Equal(1.0, ReleaseTraceChart.Side(null));
    }

    // ── The Release Test window's words ──

    private static double[] Trace(params (int Ms, double Angle)[] points)
    {
        var trace = new double[ReleaseTest.ReleaseMs];
        int at = 0;
        for (int ms = 0; ms < trace.Length; ms++)
        {
            while (at + 1 < points.Length && points[at + 1].Ms <= ms) at++;
            trace[ms] = points[at].Angle;
        }
        return trace;
    }

    [Fact]
    public void TheResultSentence_SaysHowFarTheWheelSwungPastCenter_BeforeAndNow()
    {
        var s = Strings.Instance;
        var culture = CultureInfo.CurrentCulture;
        double[] before = Trace((0, 90), (300, -41.6), (600, 20), (900, -8), (1500, 0.4));
        double[] calm = Trace((0, 90), (400, 30), (900, 6), (1500, 3.2));
        var status = new ReleaseTestStatus { Outcome = ReleaseTestOutcome.Done, Before = before, After = calm, ReleaseAngle = 90 };
        Assert.Equal(41.6, ReleaseTestStatus.PastCenter(before), 9);
        Assert.Equal(0, ReleaseTestStatus.PastCenter(calm), 9);
        Assert.Equal(3.2, ReleaseTestStatus.RestsAt(calm), 9);
        Assert.Equal(string.Format(culture, s.ReleaseTest_Result_Before_Format, "42") + " "
            + string.Format(culture, s.ReleaseTest_Result_AfterNone_Format, "3"),
            ReleaseTestDialog.ResultSentence(status, culture));

        // A release that still swings past center says by how much.
        double[] swings = Trace((0, 90), (300, -6.4), (700, 1.1), (1500, -0.6));
        var swinging = new ReleaseTestStatus { Outcome = ReleaseTestOutcome.Done, Before = before, After = swings, ReleaseAngle = 90 };
        Assert.Equal(string.Format(culture, s.ReleaseTest_Result_Before_Format, "42") + " "
            + string.Format(culture, s.ReleaseTest_Result_After_Format, "6", "1"),
            ReleaseTestDialog.ResultSentence(swinging, culture));

        // A release from the other side counts the other way.
        double[] mirrored = before.Select(a => -a).ToArray();
        Assert.Equal(41.6, ReleaseTestStatus.PastCenter(mirrored), 9);
        // A swing under half a degree would read as 0 in the sentence, so it reads as none.
        double[] barely = Trace((0, 90), (400, -0.4), (900, 0.2));
        Assert.True(ReleaseTestStatus.PastCenter(barely) < ReleaseTest.NoOvershootDegrees);
        var nearly = new ReleaseTestStatus { Outcome = ReleaseTestOutcome.Done, Before = before, After = barely, ReleaseAngle = 90 };
        Assert.EndsWith(string.Format(culture, s.ReleaseTest_Result_AfterNone_Format, "0"), ReleaseTestDialog.ResultSentence(nearly, culture));
        // No trace yet is no swing and no rest.
        Assert.Equal(0, ReleaseTestStatus.PastCenter(Array.Empty<double>()));
        Assert.Equal(0, ReleaseTestStatus.RestsAt(null));
    }

    [Fact]
    public void ATestThatEndsWithNoMeasurement_SaysWhy()
    {
        var s = Strings.Instance;
        Assert.Equal(s.ReleaseTest_Stopped, ReleaseTestDialog.OutcomeLine(ReleaseTestOutcome.Canceled));
        Assert.Equal(s.ReleaseTest_Fail_Moving, ReleaseTestDialog.OutcomeLine(ReleaseTestOutcome.Moving));
        Assert.Equal(s.ReleaseTest_Fail_NoMotion, ReleaseTestDialog.OutcomeLine(ReleaseTestOutcome.NoMotion));
        Assert.Equal(s.ReleaseTest_Fail_NearLock, ReleaseTestDialog.OutcomeLine(ReleaseTestOutcome.NearLock));
        Assert.Equal(s.ReleaseTest_Fail_Unreadable, ReleaseTestDialog.OutcomeLine(ReleaseTestOutcome.Unreadable));
        // Every outcome that is no measurement has a line of its own.
        var lines = Enum.GetValues<ReleaseTestOutcome>()
            .Where(o => o is not (ReleaseTestOutcome.Running or ReleaseTestOutcome.Done))
            .Select(ReleaseTestDialog.OutcomeLine).ToList();
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public void TheWindowNamesTheAngleTheTestLetsGoFrom()
    {
        // 90 degrees on a wheel that turns far enough, six tenths of the way to the lock on one that does not.
        var wide = new CenteringSettings(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.3, false, 27.8, 1, 5.0, 900);
        var narrow = new CenteringSettings(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.3, false, 27.8, 1, 5.0, 180);
        Assert.Equal(90.0, ReleaseTest.ReleaseAngleFor(wide));
        Assert.Equal(54.0, ReleaseTest.ReleaseAngleFor(narrow));
        Assert.Equal(ReleaseTest.ReleaseAngleFor(wide), new ReleaseTest(wide).ReleaseAngle);
        Assert.Equal(ReleaseTest.ReleaseAngleFor(narrow), new ReleaseTest(narrow).ReleaseAngle);
    }

    // ── The card as the page writes it ──

    private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

    private static XElement Named(XDocument page, string name)
        => page.Descendants().Single(e => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name") == name);

    private static string Key(string binding)
        => binding != null && binding.StartsWith("{Binding ", StringComparison.Ordinal) ? binding.Substring(9).Split(',', '}')[0] : binding;

    private static string LabelOf(XElement row) => Key((string)row.Elements().First().Attribute("Text"));

    [Fact]
    public void TheWheelGroup_HoldsTheWheelCard_ThenTheCenteringCard()
    {
        var page = Page();
        var group = Named(page, "TabWheel");
        var cards = group.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardBorder}").ToList();
        Assert.Equal(new[] { "WheelCard", "CenteringCard" },
            cards.Select(c => (string)c.Attributes().First(a => a.Name.LocalName == "Name")));

        // The Wheel card: the range and the shift lights. Auto Centering Strength left it.
        var wheelRows = cards[0].Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
        Assert.Equal(new[] { "Pad_Wheel_SetRotationRange", "Pad_Wheel_RotationRange", "Pad_Wheel_RpmLeds" }, wheelRows.Select(LabelOf));
        Assert.DoesNotContain("WheelAutoCenter", cards[0].ToString());
        Assert.Equal("Steering range and shift lights for force feedback wheels.", Strings.Instance.Pad_Wheel_Subtitle);

        // The Centering card: Mode, the spring's strength, then Vehicle mode's panel.
        var stack = cards[1].Elements().Single(e => !e.Name.LocalName.Contains('.')).Elements().ToList();
        Assert.Equal(new[] { "DockPanel", "SettingRow", "SettingRow", "StackPanel" }, stack.Select(e => e.Name.LocalName));
        Assert.Equal("Pad_Centering_Mode", LabelOf(stack[1]));
        Assert.Equal("{Binding CenteringVehicleAvailable, Converter={StaticResource BoolToVisibilityConverter}}", (string)stack[1].Attribute("Visibility"));
        var modes = stack[1].Elements().Single(e => e.Name.LocalName == "ComboBox");
        Assert.Equal("{Binding CenteringMode, Mode=TwoWay}", (string)modes.Attribute("SelectedIndex"));
        // Spring first and Vehicle second, as the saved 0 and 1, and the first reads Off on a wheel with no spring.
        Assert.Equal(new[] { "CenteringModeSpringLabel", "Pad_Centering_Mode_Vehicle" }, modes.Elements().Select(i => Key((string)i.Attribute("Content"))));
        Assert.Equal("Pad_Wheel_Autocenter", LabelOf(stack[2]));
        Assert.Equal("{Binding CenteringStrengthShown, Converter={StaticResource BoolToVisibilityConverter}}", (string)stack[2].Attribute("Visibility"));
        Assert.Contains("{Binding WheelAutoCenter, Mode=TwoWay}", stack[2].ToString());
        Assert.Contains("{Binding ResetWheelAutoCenterCommand}", stack[2].ToString());

        // Vehicle mode: its rows, the line that says what holds the force back, the well, More, and the Release Test.
        var vehicle = stack[3];
        Assert.Equal("{Binding CenteringIsVehicle, Converter={StaticResource BoolToVisibilityConverter}}", (string)vehicle.Attribute("Visibility"));
        var parts = vehicle.Elements().ToList();
        Assert.Equal(new[] { "SettingRow", "SettingRow", "SettingRow", "SettingRow", "TextBlock", "Border", "MoreRow", "Button" },
            parts.Select(e => e.Name.LocalName));
        Assert.Equal(new[] { "Pad_Centering_Vehicle", "Pad_Centering_Speed", "Pad_Centering_Weight", "Pad_Centering_WheelRange" },
            parts.Take(4).Select(LabelOf));
        Assert.Equal("{Binding CenteringWheelRangeShown, Converter={StaticResource BoolToVisibilityConverter}}", (string)parts[3].Attribute("Visibility"));
        Assert.Equal("{Binding CenteringStatusText}", (string)parts[4].Attribute("Text"));
        Assert.Equal("{Binding CenteringHasStatus, Converter={StaticResource BoolToVisibilityConverter}}", (string)parts[4].Attribute("Visibility"));
        Assert.Equal("{DynamicResource WaitBrush}", (string)parts[4].Attribute("Foreground"));

        // The well: its head, the chart, and the four measures.
        var well = parts[5];
        Assert.Equal("{StaticResource LiveWell}", (string)well.Attribute("Style"));
        var inWell = well.Elements().Single().Elements().ToList();
        Assert.Equal(new[] { "FlexLinePanel", "SteeringTorqueChart", "UniformGrid" }, inWell.Select(e => e.Name.LocalName));
        var head = inWell[0].Elements().ToList();
        Assert.Equal("Pad_Centering_Chart", Key((string)head[0].Attribute("Text")));
        Assert.Equal(new[] { "{StaticResource LegendPillEmber}", "{StaticResource LegendPill}" }, head.Skip(1).Take(2).Select(e => (string)e.Attribute("Style")));
        Assert.Contains("{Binding CenteringSpeedLegend}", head[1].ToString());
        Assert.Contains("{Binding CenteringReferenceLegend}", head[2].ToString());
        // The angle is a reading, cold, and the torque is PadForge's own force, ember. Both show
        // only while Vehicle mode runs on the wheel.
        Assert.Equal("{Binding CenteringLiveShown, Converter={StaticResource BoolToVisibilityConverter}}", (string)head[3].Attribute("Visibility"));
        string readouts = head[3].ToString();
        Assert.Contains("Text=\"{Binding CenteringAngleText, Mode=OneWay}\" Foreground=\"{DynamicResource ColdBrush}\"", readouts);
        Assert.Contains("Text=\"{Binding CenteringTorqueText, Mode=OneWay}\" Foreground=\"{DynamicResource EmberHotBrush}\"", readouts);
        var chart = inWell[1];
        Assert.Equal("{Binding CenteringPreview}", (string)chart.Attribute("Preview"));
        Assert.Equal("{Binding CenteringLiveShown}", (string)chart.Attribute("LiveShown"));
        Assert.Equal("{Binding CenteringLiveAngle}", (string)chart.Attribute("LiveAngle"));
        Assert.Equal("{Binding CenteringLiveTorque}", (string)chart.Attribute("LiveTorque"));
        Assert.Equal("Pad_Centering_WheelLimit_Format", Key((string)chart.Attribute("LimitLabelFormat")));
        Assert.Equal("{Binding CenteringChartName, TargetNullValue=''}",
            (string)chart.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name"));
        Assert.Equal("4", (string)inWell[2].Attribute("Columns"));
        var tiles = inWell[2].Elements().ToList();
        Assert.Equal(new[] { "Pad_Centering_OnCenterFeel", "Pad_Centering_TorqueStiffness", "Pad_Centering_Linearity", "Pad_Centering_PeakTorque" },
            tiles.Select(t => Key((string)t.Elements().First(e => e.Name.LocalName == "TextBlock").Attribute("Text"))));
        Assert.Equal(new[] { "CenteringOnCenterFeelText", "CenteringStiffnessText", "CenteringLinearityText", "CenteringPeakText" },
            tiles.Select(t => Key((string)t.Descendants().First(e => e.Name.LocalName == "Run").Attribute("Text"))));
        // Each measure says what it is, and Peak Torque says when the wheel has less than the car asks.
        Assert.Equal(new[] { "Pad_Centering_OnCenterFeel_Tooltip", "Pad_Centering_TorqueStiffness_Tooltip", "Pad_Centering_Linearity_Tooltip" },
            tiles.Take(3).Select(t => Key((string)t.Attribute("ToolTip"))));
        string peak = tiles[3].ToString();
        Assert.Contains("Pad_Centering_PeakTorque_Tooltip", peak);
        Assert.Contains("Pad_Centering_PeakOver", peak);
        Assert.Contains("{Binding CenteringPeakOverLimit}", peak);
        Assert.Contains("{DynamicResource WaitBrush}", peak);

        // Under More, in the board's order.
        var more = parts[6];
        Assert.Equal(new[] { "Pad_Centering_OnCenterFeel", "Pad_Centering_Assist", "Pad_Centering_Damping", "Pad_Centering_Friction",
                "Pad_Centering_Grip", "Pad_Centering_FixedSpeed", "Pad_Centering_WheelTorque" },
            more.Descendants().Where(e => e.Name.LocalName == "SettingRow").Select(LabelOf));
        // The hint names those rows in that order.
        Assert.Equal("On-center feel, power assist, damping, friction, grip, fixed speed, wheel torque", Strings.Instance.Pad_More_Centering);
        var grip = more.Descendants().Single(e => e.Name.LocalName == "ComboBox");
        Assert.Equal(new[] { "Pad_Centering_FromGame", "Pad_Centering_Grip_Dry", "Pad_Centering_Grip_Wet", "Pad_Centering_Grip_Snow", "Pad_Centering_Grip_Ice" },
            grip.Elements().Select(i => Key((string)i.Attribute("Content"))));
        // Friction moves in steps of 0.05 N·m, and a whole number's slider in whole steps.
        var friction = more.Descendants().Single(e => e.Name.LocalName == "Slider" && (string)e.Attribute("Value") == "{Binding CenteringFrictionNm, Mode=TwoWay}");
        Assert.Equal(("0", "1.5", "0.05"), ((string)friction.Attribute("Minimum"), (string)friction.Attribute("Maximum"), (string)friction.Attribute("TickFrequency")));
        Assert.All(cards[1].Descendants().Where(e => e.Name.LocalName == "Slider" && e != friction),
            slider => Assert.True((string)slider.Attribute("TickFrequency") is "1" or "10", (string)slider.Attribute("Value")));

        // The Release Test: the card's foot, a button that opens its window.
        var foot = parts[7];
        Assert.Equal("{StaticResource CardFootLink}", (string)foot.Attribute("Style"));
        Assert.Equal("CenteringReleaseTest_Click", (string)foot.Attribute("Click"));
        Assert.Equal("Pad_Centering_ReleaseTest", Key((string)foot.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name")));
        Assert.Equal("Pad_Centering_ReleaseTest_Desc", Key((string)foot.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText")));
        Assert.Equal("{Binding CenteringReleaseStatus}", (string)foot.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.ItemStatus"));
        Assert.Contains("{Binding CenteringReleaseStatus}", foot.Elements().Single().ToString());

        // Every row of the card has one reset, its own.
        var rows = cards[1].Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
        Assert.Equal(13, rows.Count);
        Assert.All(rows, row => Assert.Single(row.Elements(), e => e.Name.LocalName == "SettingResetButton"
            || (string)e.Attribute("Style") == "{StaticResource ResetButton}"));
        var resets = cards[1].Descendants().Where(e => e.Name.LocalName == "SettingResetButton").ToList();
        Assert.Equal(12, resets.Count);
        Assert.All(resets, reset =>
        {
            string name = (string)reset.Attribute("CommandParameter");
            Assert.True(PadViewModel.CanResetSetting(name), name);
            Assert.Equal("Reset_PadViewModel_" + name, (string)reset.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.AutomationId"));
            // The reset names the row it stands in.
            Assert.Equal(LabelOf(reset.Parent), Key((string)reset.Attribute("SettingLabel")));
        });
    }

    [Fact]
    public void TheCardsStyles_AreThePagesOwn()
    {
        var icons = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Style(string key) => icons.Descendants().Single(e => e.Name.LocalName == "Style" && (string)e.Attribute(x + "Key") == key);
        string Setter(XElement style, string property) => (string)style.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property)?.Attribute("Value");

        // A legend's pill: 20 tall, the smallest text, and each kind's own fill, edge and words.
        var pill = Style("LegendPill");
        Assert.Equal("20", Setter(pill, "MinHeight"));
        Assert.Equal("10", Setter(pill, "CornerRadius"));
        Assert.Equal("12", Setter(pill, "TextElement.FontSize"));
        Assert.Equal("{DynamicResource SteelPillLineBrush}", Setter(pill, "BorderBrush"));
        var ember = Style("LegendPillEmber");
        Assert.Equal("{StaticResource LegendPill}", (string)ember.Attribute("BasedOn"));
        Assert.Equal(("{DynamicResource EmberWellBrush}", "{DynamicResource EmberEdgeBrush}", "{DynamicResource EmberHotBrush}"),
            (Setter(ember, "Background"), Setter(ember, "BorderBrush"), Setter(ember, "TextElement.Foreground")));
        var cold = Style("LegendPillCold");
        Assert.Equal("{StaticResource LegendPill}", (string)cold.Attribute("BasedOn"));
        Assert.Equal("{DynamicResource ColdBrush}", Setter(cold, "TextElement.Foreground"));

        // The foot's row is the More row's width and height of line: it takes back the card's padding
        // and sits where a More row's own take-back leaves it, under a hairline, with the card's corners.
        var foot = Style("CardFootLink");
        var more = icons.Descendants().Single(e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "{x:Type controls:MoreRow}"
            && e.Attribute(x + "Key") == null);
        Assert.Equal(Setter(more, "Margin"), Setter(foot, "Margin"));
        Assert.Equal("48", Setter(foot, "MinHeight"));
        Assert.Equal("Hand", Setter(foot, "Cursor"));
        string template = foot.ToString();
        Assert.Contains("BorderThickness=\"0,1,0,0\"", template);
        Assert.Contains("CornerRadius=\"0,0,11,11\"", template);
        Assert.Contains("{DynamicResource SteelCardHoverBrush}", template);
    }

    [Fact]
    public void TheReleaseTestWindow_HasItsChart_ItsFigures_AndAFootForEachState()
    {
        var window = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "ReleaseTestDialog.xaml"));
        var root = window.Root!;
        Assert.Equal("DialogWindow", root.Name.LocalName);
        Assert.Equal("Pad_Centering_ReleaseTest", Key((string)root.Attribute("Title")));
        var foot = root.Elements().Single(e => e.Name.LocalName == "DialogWindow.Foot").Elements().Single();
        Assert.Equal("{StaticResource DialogFootPanel}", (string)foot.Attribute("Style"));
        var buttons = foot.Elements().ToList();
        // Run Again leads the foot, quiet. Cancel closes. The last three are one place, a state each.
        Assert.Equal(new[] { "ReleaseTest_RunAgain", "Common_Cancel", "Common_Start", "Common_Stop", "Common_Apply" },
            buttons.Select(b => Key((string)b.Attribute("Content"))));
        Assert.Equal(new[] { "{StaticResource DialogQuietButton}", "{StaticResource DialogButton}", "{StaticResource DialogPrimaryButton}",
                "{StaticResource DialogPrimaryButton}", "{StaticResource DialogPrimaryButton}" },
            buttons.Select(b => (string)b.Attribute("Style")));
        Assert.Equal("True", (string)buttons[1].Attribute("IsCancel"));
        Assert.Equal(new[] { "Start_Click", null, "Start_Click", "Stop_Click", "Apply_Click" }, buttons.Select(b => (string)b.Attribute("Click")));
        // The window opens ready: Start shows, and the buttons of the other states do not.
        Assert.Equal(new[] { "Collapsed", null, null, "Collapsed", "Collapsed" }, buttons.Select(b => (string)b.Attribute("Visibility")));

        var body = root.Elements().Single(e => e.Name.LocalName == "StackPanel").Elements().ToList();
        Assert.Equal(new[] { "Border", "FlexLinePanel", "UniformGrid", "TextBlock" }, body.Select(e => e.Name.LocalName));
        Assert.Equal("ReleaseTraceChart", body[0].Elements().Single().Name.LocalName);
        Assert.Equal(("{DynamicResource SteelFieldBrush}", "{DynamicResource SteelLineSoftBrush}"),
            ((string)body[0].Attribute("Background"), (string)body[0].Attribute("BorderBrush")));
        var legend = body[1].Elements().ToList();
        // The dialogs' names for the pages' legend pills.
        Assert.Equal(new[] { null, "{StaticResource DialogLegendPill}", "{StaticResource DialogLegendPillCold}" }, legend.Select(e => (string)e.Attribute("Style")));
        string looks = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Dialogs.xaml");
        Assert.Contains("<Style x:Key=\"DialogLegendPill\" TargetType=\"Border\" BasedOn=\"{StaticResource LegendPill}\"/>", looks);
        Assert.Contains("<Style x:Key=\"DialogLegendPillCold\" TargetType=\"Border\" BasedOn=\"{StaticResource LegendPillCold}\"/>", looks);
        Assert.Contains("ReleaseTest_Before", legend[1].ToString());
        Assert.Contains("ReleaseTest_After", legend[2].ToString());
        Assert.Equal(new[] { "Pad_Centering_Friction", "ReleaseTest_Inertia", "Pad_Centering_Damping" },
            body[2].Elements().Select(t => Key((string)t.Elements().First().Attribute("Text"))));
        Assert.Equal(new[] { "N·m", "kg·m²", "N·m·s/rad" },
            body[2].Elements().Select(t => (string)t.Descendants().Last(e => e.Name.LocalName == "Run").Attribute("Text")));

        // Closing the window by any way stops a test, and Apply is what keeps a measurement.
        string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ReleaseTestDialog.xaml.cs");
        Assert.Contains("_device.Centering.ClearTest();", code);
        Assert.Equal(1, code.Split("_device.WheelCalibration = ").Length - 1);
        Assert.Contains("if (_result == null) return;\n            _device.WheelCalibration = _result.Format();", code.Replace("\r\n", "\n"));
        // A wheel no slot uses is told so before a test that could never step is started.
        Assert.True(code.IndexOf("_device.Centering.IsPolled(now)", StringComparison.Ordinal)
            < code.IndexOf("_device.Centering.StartTest(", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCardsSettings_AreSavedByEveryPathThatSavesTheWheels()
    {
        // The two places a device's settings load into the card and the two that save them.
        string input = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
        string settings = ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs");
        foreach (string text in new[] { input, settings })
        {
            Assert.Equal(1, text.Split("LoadCenteringSettings(padVm, ps, ").Length - 1);
            Assert.Equal(1, text.Split("padVm.SaveCentering(ps);").Length - 1);
            // Each beside the wheel's own lines, so a path that gains one gains the other.
            Assert.True(text.IndexOf("padVm.WheelRpmLeds = ps.WheelRpmLeds == \"1\";", StringComparison.Ordinal)
                < text.IndexOf("LoadCenteringSettings(padVm, ps, ", StringComparison.Ordinal));
            Assert.True(text.IndexOf("ps.WheelRpmLeds = padVm.WheelRpmLeds ? \"1\" : \"0\";", StringComparison.Ordinal)
                < text.IndexOf("padVm.SaveCentering(ps);", StringComparison.Ordinal));
        }

        // A change to any of the twelve marks the settings unsaved.
        string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
        foreach (string name in new[]
                 {
                     nameof(PadViewModel.CenteringMode), nameof(PadViewModel.CenteringVehicle), nameof(PadViewModel.CenteringSpeedSource),
                     nameof(PadViewModel.CenteringFixedSpeed), nameof(PadViewModel.CenteringWeight), nameof(PadViewModel.CenteringOnCenter),
                     nameof(PadViewModel.CenteringAssist), nameof(PadViewModel.CenteringDamping), nameof(PadViewModel.CenteringFriction),
                     nameof(PadViewModel.CenteringGrip), nameof(PadViewModel.CenteringWheelTorque), nameof(PadViewModel.CenteringWheelRange),
                 })
        {
            Assert.Contains("nameof(PadViewModel." + name + ") or", window);
        }
        // What the card only shows is no setting: a live frame at 30 a second must not queue a save.
        foreach (string shown in new[] { "CenteringLiveAngle", "CenteringPreview", "CenteringStatusText", "CenteringSpeedText", "CenteringMeasured" })
            Assert.DoesNotContain("nameof(PadViewModel." + shown + ")", window);
    }
}
