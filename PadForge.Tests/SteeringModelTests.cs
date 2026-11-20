using PadForge.Engine.Steering;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// #588: the steering feel model against its sources. The paper is
/// Balachandran and Gerdes, "Designing Steering Feel for Steer-by-Wire
/// Vehicles Using Objective Measures" (2014). Its Table III gives its test
/// car's measures at 60 and 25 mph and the ranges of production cars.
/// </summary>
public sealed class SteeringModelTests(ITestOutputHelper output)
{
    const double Mph = 0.44704;
    static readonly double Lock = 450.0 * Math.PI / 180.0;

    static SteeringMeasures Measure(SteeringVehicle car, double mph, double grip = 1.0)
        => SteeringMeasures.From(new SteeringCurve(car, car.Feel, mph * Mph, grip, Lock));

    static void Near(double expected, double actual, double relative, string what)
        => Assert.True(Math.Abs(actual - expected) <= Math.Abs(expected) * relative, $"{what}: {actual} is not within {relative:P0} of {expected}");

    /// <summary>The three settings fitted to the paper's table land on
    /// it: on-center feel 17 and 22 N·m/g at 60 and 25 mph, linearity 25
    /// percent at 60 mph.</summary>
    [Fact]
    public void ThePapersCarHasThePapersFittedMeasures()
    {
        var at60 = Measure(SteeringVehicle.StanfordX1, 60);
        var at25 = Measure(SteeringVehicle.StanfordX1, 25);
        output.WriteLine($"60 mph: on-center {at60.OnCenterFeel:F3} stiffness {at60.TorqueStiffness:F4} sensitivity {at60.Sensitivity:F3} linearity {at60.Linearity:F2}");
        output.WriteLine($"25 mph: on-center {at25.OnCenterFeel:F3} stiffness {at25.TorqueStiffness:F4} sensitivity {at25.Sensitivity:F3} linearity {at25.Linearity:F2}");
        Near(17.0, at60.OnCenterFeel, 0.01, "on-center feel at 60 mph");
        Near(22.0, at25.OnCenterFeel, 0.01, "on-center feel at 25 mph");
        Near(25.0, at60.Linearity, 0.02, "linearity at 60 mph");
    }

    /// <summary>What the model then predicts, against the paper's own
    /// figures for the measures the fit never saw: torque stiffness 0.37
    /// and 0.12 N·m/deg, steering sensitivity 2.33 and 0.52 g per 100
    /// degrees, linearity 22.7 percent at 25 mph. The steady-state model
    /// lands within a fifth of each.</summary>
    [Fact]
    public void ThePapersCarPredictsThePapersOtherMeasures()
    {
        var at60 = Measure(SteeringVehicle.StanfordX1, 60);
        var at25 = Measure(SteeringVehicle.StanfordX1, 25);
        Near(0.37, at60.TorqueStiffness, 0.20, "torque stiffness at 60 mph");
        Near(0.12, at25.TorqueStiffness, 0.20, "torque stiffness at 25 mph");
        Near(2.33, at60.Sensitivity, 0.20, "sensitivity at 60 mph");
        Near(0.52, at25.Sensitivity, 0.20, "sensitivity at 25 mph");
        Near(22.7, at25.Linearity, 0.20, "linearity at 25 mph");
        // The values this model gives, held so a change to it shows.
        Near(0.4349, at60.TorqueStiffness, 0.01, "this model's torque stiffness at 60 mph");
        Near(0.1101, at25.TorqueStiffness, 0.01, "this model's torque stiffness at 25 mph");
        Near(2.480, at60.Sensitivity, 0.01, "this model's sensitivity at 60 mph");
        Near(0.489, at25.Sensitivity, 0.01, "this model's sensitivity at 25 mph");
    }

    public static TheoryData<string, double, double, double> ProductionCars => new()
    {
        // id, on-center feel and linearity stated for 60 mph, and the sensitivity the fit measured there
        { "compact", 14.0, 30.0, 1.049 },
        { "sedan", 17.0, 25.0, 0.952 },
        { "van", 12.0, 20.0, 0.663 },
    };

    /// <summary>Each production car has the on-center feel and linearity
    /// stated for it, and every measure sits inside the range the paper
    /// quotes for production cars at 60 mph: on-center feel 7 to 33
    /// N·m/g, sensitivity 0.27 to 1.42 g per 100 degrees, linearity 6 to
    /// 121 percent.</summary>
    [Theory]
    [MemberData(nameof(ProductionCars))]
    public void AProductionCarHasItsStatedFeel_InsideThePublishedRanges(string id, double onCenter, double linearity, double sensitivity)
    {
        var car = SteeringVehicle.ById(id);
        Assert.Equal(id, car.Id);
        var m = Measure(car, 60);
        output.WriteLine($"{id}: on-center {m.OnCenterFeel:F3} stiffness {m.TorqueStiffness:F4} sensitivity {m.Sensitivity:F3} linearity {m.Linearity:F2} peak {m.PeakTorque:F2}");
        Near(onCenter, m.OnCenterFeel, 0.01, "on-center feel");
        Near(linearity, m.Linearity, 0.02, "linearity");
        Near(sensitivity, m.Sensitivity, 0.01, "sensitivity");
        Assert.InRange(m.OnCenterFeel, 7.0, 33.0);
        Assert.InRange(m.Sensitivity, 0.27, 1.42);
        Assert.InRange(m.Linearity, 6.0, 121.0);
    }

    /// <summary>The race car has its stated feel. Its on-center feel and
    /// linearity are inside the production ranges. Its steering is far
    /// quicker than a production car's, and the paper's own car is above
    /// that range too, as the paper says.</summary>
    [Fact]
    public void TheRaceCarHasItsStatedFeel_AndQuickSteering()
    {
        var m = Measure(SteeringVehicle.RaceCar, 60);
        output.WriteLine($"race: on-center {m.OnCenterFeel:F3} stiffness {m.TorqueStiffness:F4} sensitivity {m.Sensitivity:F3} linearity {m.Linearity:F2} peak {m.PeakTorque:F2}");
        Near(26.0, m.OnCenterFeel, 0.01, "on-center feel");
        Near(80.0, m.Linearity, 0.02, "linearity");
        Near(3.617, m.Sensitivity, 0.01, "sensitivity");
        Assert.InRange(m.OnCenterFeel, 7.0, 33.0);
        Assert.InRange(m.Linearity, 6.0, 121.0);
        Assert.True(m.Sensitivity > 1.42);
        Assert.True(Measure(SteeringVehicle.StanfordX1, 60).Sensitivity > 1.42);
    }

    /// <summary>The cornering stiffness each car's source gives. The
    /// three CommonRoad cars take the tire's stiffness for the rear
    /// axle's load, and a front stiffness below the tire's that puts the
    /// linear model's sensitivity on the stated value. The race car's
    /// axles are two Magic Formula tires each.</summary>
    [Fact]
    public void TheCarsCarryTheirSourcesStiffness()
    {
        Near(71319.4, SteeringVehicle.Compact.FrontStiffness, 0.0001, "compact front");
        Near(97384.2, SteeringVehicle.Compact.RearStiffness, 0.0001, "compact rear");
        Near(49497.2, SteeringVehicle.Sedan.FrontStiffness, 0.0001, "sedan front");
        Near(105400.3, SteeringVehicle.Sedan.RearStiffness, 0.0001, "sedan rear");
        Near(54169.1, SteeringVehicle.Van.FrontStiffness, 0.0001, "van front");
        Near(148050.1, SteeringVehicle.Van.RearStiffness, 0.0001, "van rear");
        Near(124765.1, SteeringVehicle.RaceCar.FrontStiffness, 0.0001, "race front");
        Near(140535.7, SteeringVehicle.RaceCar.RearStiffness, 0.0001, "race rear");
        // The front axle's effective stiffness is below the tire's on each production car.
        foreach (var car in new[] { SteeringVehicle.Compact, SteeringVehicle.Sedan, SteeringVehicle.Van })
            Assert.True(car.FrontStiffness < SteeringVehicle.CommonRoadStiffnessPerLoad * car.FrontLoad, car.Id);
        Assert.Equal(110e3, SteeringVehicle.StanfordX1.FrontStiffness);
        Assert.Equal(148e3, SteeringVehicle.StanfordX1.RearStiffness);
    }

    /// <summary>The Fiala force and its inverse agree below the tire's
    /// limit, and the force stops at grip times load.</summary>
    [Theory]
    [InlineData(110e3, 8626.0, 1.0)]
    [InlineData(49497.2, 5916.9, 0.3)]
    public void TheTireForceAndItsInverseAgree(double stiffness, double load, double grip)
    {
        for (double share = 0.0; share < 0.999; share += 0.037)
        {
            double force = share * grip * load;
            double slip = SteeringModel.FialaSlip(force, stiffness, load, grip);
            Near(force == 0 ? 1e-12 : force, SteeringModel.FialaForce(slip, stiffness, load, grip) + (force == 0 ? 1e-12 : 0), 1e-9, $"share {share}");
        }
        Assert.Equal(grip * load, SteeringModel.FialaForce(1.2, stiffness, load, grip), 6);
        // Near zero the force is stiffness times slip.
        Near(stiffness * 1e-5, SteeringModel.FialaForce(1e-5, stiffness, load, grip), 1e-3, "linear range");
    }

    /// <summary>The curve is odd in the angle, zero at center, and reads
    /// the lock's torque past the lock.</summary>
    [Fact]
    public void TheCurveIsOdd_AndHoldsAtTheLock()
    {
        var curve = new SteeringCurve(SteeringVehicle.Sedan, SteeringVehicle.Sedan.Feel, 60 * Mph, 1.0, Lock);
        Assert.Equal(0.0, curve.Torque(0.0));
        for (double degrees = 0.5; degrees < 450; degrees *= 1.7)
        {
            double angle = degrees * Math.PI / 180.0;
            Assert.Equal(curve.Torque(angle), -curve.Torque(-angle), 12);
            Assert.True(curve.Torque(angle) > 0);
        }
        Assert.Equal(curve.Torque(Lock), curve.Torque(Lock * 2), 12);
        Assert.True(curve.CenterSlope > 0);
        Assert.True(curve.SteepestSlope >= curve.CenterSlope * 0.999);
    }

    /// <summary>The table reads what the model gives directly: at ten
    /// degrees the paper's car asks 1.82 N·m at 60 mph and 1.07 at 25,
    /// and its torque peaks at 4.64 N·m near 39 degrees at 60 mph, where
    /// the front tires reach their limit.</summary>
    [Fact]
    public void TheCurveReadsTheModelsTorque()
    {
        var car = SteeringVehicle.StanfordX1;
        var at60 = new SteeringCurve(car, car.Feel, 60 * Mph, 1.0, Lock);
        var at25 = new SteeringCurve(car, car.Feel, 25 * Mph, 1.0, Lock);
        double ten = 10 * Math.PI / 180.0;
        Near(1.8237, at60.Torque(ten), 0.005, "10 degrees at 60 mph");
        Near(1.0734, at25.Torque(ten), 0.005, "10 degrees at 25 mph");
        double peak = 0, peakAt = 0;
        for (double degrees = 0; degrees <= 90; degrees += 0.1)
        {
            double t = at60.Torque(degrees * Math.PI / 180.0);
            if (t > peak) { peak = t; peakAt = degrees; }
        }
        Near(4.6416, peak, 0.005, "peak within 90 degrees");
        Assert.InRange(peakAt, 38.5, 40.5);
        // Past the limit the steering goes light: the torque at 60 degrees is below the peak.
        Assert.True(at60.Torque(60 * Math.PI / 180.0) < peak * 0.85);
    }

    /// <summary>With the weighting's lower limit at 0.40 the paper's
    /// car's torque never falls with angle until the car is past 0.8 g,
    /// at 60 and at 25 mph. At 0.20 and 0.30 it dipped, by up to 0.3
    /// N·m.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(25)]
    public void ThePapersCarsTorqueRisesUntilTheLimit(double mph)
    {
        var car = SteeringVehicle.StanfordX1;
        var curve = new SteeringCurve(car, car.Feel, mph * Mph, 1.0, Lock);
        double high = 0;
        for (double degrees = 0; degrees <= 180; degrees += 0.25)
        {
            double angle = degrees * Math.PI / 180.0;
            if (curve.LateralAcceleration(angle) > 0.8 * SteeringVehicle.Gravity) break;
            double t = curve.Torque(angle);
            Assert.True(t >= high - 1e-6, $"fell at {degrees} degrees: {t} after {high}");
            if (t > high) high = t;
        }
        Assert.True(high > 1.0);
    }

    /// <summary>Less grip leaves the slope at center as it is and lowers
    /// the peak: the tire's stiffness is the same, and its limit comes
    /// sooner.</summary>
    [Fact]
    public void LessGripLowersThePeak_NotTheCenter()
    {
        var car = SteeringVehicle.Sedan;
        var dry = new SteeringCurve(car, car.Feel, 60 * Mph, 1.0, Lock);
        var ice = new SteeringCurve(car, car.Feel, 60 * Mph, 0.1, Lock);
        Near(dry.CenterSlope, ice.CenterSlope, 0.02, "slope at center");
        double quarter = 90 * Math.PI / 180.0;
        double dryPeak = 0, icePeak = 0;
        for (double a = 0; a <= quarter; a += quarter / 400)
        {
            dryPeak = Math.Max(dryPeak, dry.Torque(a));
            icePeak = Math.Max(icePeak, ice.Torque(a));
        }
        Assert.True(icePeak < dryPeak * 0.6, $"ice {icePeak}, dry {dryPeak}");
    }

    /// <summary>At walking pace the aligning moment is nothing and the
    /// jacking torque centers the wheel, in a line with the angle
    /// (the paper's section II-A: jacking torque "accounts for most of
    /// the self-centering effect of the steering feel at low
    /// speed").</summary>
    [Fact]
    public void AtWalkingPaceTheJackingTorqueCenters()
    {
        var car = SteeringVehicle.Sedan;
        var curve = new SteeringCurve(car, car.Feel, 1.0, 1.0, Lock);
        double ninety = 90 * Math.PI / 180.0;
        double jacking = car.Feel.Gain * car.Feel.JackingStiffness * ninety / car.SteeringRatio;
        Assert.InRange(curve.Torque(ninety), jacking * 0.97, jacking * 1.2);
        // No measure that needs lateral acceleration exists there.
        var m = SteeringMeasures.From(curve);
        Assert.True(double.IsNaN(m.OnCenterFeel));
        Assert.True(double.IsNaN(m.Sensitivity));
        Assert.True(m.PeakTorque > 0);
    }

    /// <summary>The user's scales: Steering Weight and On-Center Feel
    /// scale the gain and the jacking stiffness, Power Assist at 0 is no
    /// assist, at 100 the car's own, and above it more, down to the
    /// least floor.</summary>
    [Fact]
    public void TheScalesMoveTheSettingsTheyName()
    {
        var feel = new SteeringFeel(0.04, 700, 0.01, 0.40);
        var same = feel.Scaled(100, 100, 100);
        Assert.Equal(feel.Gain, same.Gain, 12);
        Assert.Equal(feel.JackingStiffness, same.JackingStiffness, 9);
        Assert.Equal(0.40, same.AssistFloor, 12);
        Assert.Equal(0.02, feel.Scaled(50, 100, 100).Gain, 12);
        Assert.Equal(1400, feel.Scaled(100, 200, 100).JackingStiffness, 9);
        Assert.Equal(1.0, feel.Scaled(100, 100, 0).AssistFloor, 12);
        Assert.Equal(0.70, feel.Scaled(100, 100, 50).AssistFloor, 12);
        Assert.Equal(SteeringFeel.LeastAssistFloor, feel.Scaled(100, 100, 200).AssistFloor, 12);

        // Steering Weight scales the whole curve.
        var car = SteeringVehicle.Sedan;
        var full = new SteeringCurve(car, car.Feel, 60 * Mph, 1.0, Lock);
        var half = new SteeringCurve(car, car.Feel.Scaled(50, 100, 100), 60 * Mph, 1.0, Lock);
        Near(full.Torque(0.2) / 2, half.Torque(0.2), 1e-9, "half weight");
        // No assist is a heavier wheel off center.
        var none = new SteeringCurve(car, car.Feel.Scaled(100, 100, 0), 60 * Mph, 1.0, Lock);
        Assert.True(none.Torque(0.5) > full.Torque(0.5) * 1.5);
    }

    /// <summary>The list names five cars with distinct keys, the sedan
    /// first, and an unknown key reads the first.</summary>
    [Fact]
    public void TheListHasFiveCars_AndAnUnknownKeyReadsTheFirst()
    {
        Assert.Equal(new[] { "sedan", "compact", "van", "race", "x1" }, SteeringVehicle.All.Select(v => v.Id).ToArray());
        Assert.Same(SteeringVehicle.Sedan, SteeringVehicle.ById("nothing"));
        Assert.Same(SteeringVehicle.Sedan, SteeringVehicle.ById(null));
        Assert.Same(SteeringVehicle.RaceCar, SteeringVehicle.ById("RACE"));
    }
}
