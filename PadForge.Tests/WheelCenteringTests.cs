using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// #588: Vehicle mode's centering on a simulated wheel. No wheel was on
/// hand, so the stability rule, the oscillation guard and the split
/// between the wheel's own spring and the PC are held against
/// <see cref="WheelPlant"/>, a rim with inertia and friction whose motor
/// answers a constant force after a delay.
/// </summary>
public sealed class WheelCenteringTests(ITestOutputHelper output)
{
    const double Mph = 0.44704;
    const double Deg = Math.PI / 180.0;

    static CenteringSettings Settings(SteeringVehicle car, double wheelTorque, double damping = 100, double friction = 0.30,
        double range = 900, double weight = 100)
        => new(true, car, weight, 100, 100, damping, friction, false, 60 * Mph, 1, wheelTorque, range);

    /// <summary>A gear-driven wheel of about 2 N·m: plenty of friction,
    /// a slow loop.</summary>
    static WheelPlant GearWheel() => new()
    {
        Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.22, SpringFull = 12.0,
    };

    /// <summary>A direct-drive base of 8 N·m: little friction, a quick loop.</summary>
    static WheelPlant DirectDrive() => new()
    {
        Inertia = 0.020, Friction = 0.03, Delay = 0.004, WheelTorque = 8.0, DamperFull = 0.8, SpringFull = 60.0,
    };

    /// <summary>What a Release Test would bring back from this wheel,
    /// had it read every number exactly.</summary>
    static WheelCalibration Measured(WheelPlant plant, double ratio = 0, int sign = 1)
    {
        double Spring(int coefficient) => plant.SpringFull * Math.Pow(coefficient / 10000.0, plant.SpringExponent);
        double Damper(int coefficient) => plant.DamperFull * Math.Pow(coefficient / 10000.0, plant.DamperExponent);
        return new WheelCalibration
        {
            Inertia = plant.Inertia, Friction = plant.Friction, Delay = plant.Delay, WheelTorque = plant.WheelTorque,
            DamperMid = Damper(WheelCalibration.DamperCoefficients[0]), DamperFull = Damper(WheelCalibration.DamperCoefficients[1]),
            SpringThird = Spring(WheelCalibration.SpringCoefficients[0]), SpringTwoThirds = Spring(WheelCalibration.SpringCoefficients[1]),
            SpringFull = Spring(WheelCalibration.SpringCoefficients[2]),
            ForceSign = sign, ReturnRatio = ratio,
        };
    }

    /// <summary>Runs centering past its wait for a wheel at rest, its
    /// soft start and the runaway check's tail, with the wheel held
    /// where it is.</summary>
    static void Warm(WheelCentering centering, WheelPlant plant, in CenteringSettings s, WheelCalibration measured, double speed)
    {
        long tick = (long)Math.Round(plant.Time * 1000.0);
        int span = WheelCentering.CalmMs + WheelCentering.SoftStartMs + WheelCentering.RunawayTailMs + 50;
        for (int ms = 0; ms <= span; ms++)
            centering.Step(s, measured, plant.Axis, speed, 1.0, tick - span - 10 + ms);
    }

    /// <summary>Steps centering once a millisecond with the wheel
    /// where <paramref name="degreesAt"/> puts it, on a 900 degree
    /// wheel, and answers the last frame and the most force sent.</summary>
    static (CenteringFrame Last, int MostLevel) Drive(WheelCentering centering, in CenteringSettings s,
        Func<int, double> degreesAt, int from, int to)
    {
        CenteringFrame frame = default;
        int most = 0;
        for (int ms = from; ms <= to; ms++)
        {
            frame = centering.Step(s, null, degreesAt(ms) / 450.0, 60 * Mph, 1.0, 50_000 + ms);
            most = Math.Max(most, Math.Abs((int)frame.Level));
        }
        return (frame, most);
    }

    static int Crossings(double[] trace)
    {
        int n = 0;
        for (int i = 1; i < trace.Length; i++)
            if ((trace[i - 1] > 0) != (trace[i] > 0) && trace[i] != 0) n++;
        return n;
    }

    static double LateSwing(double[] trace, int lastMs)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = trace.Length - lastMs; i < trace.Length; i++)
        {
            min = Math.Min(min, trace[i]);
            max = Math.Max(max, trace[i]);
        }
        return max - min;
    }

    /// <summary>The level has the axis's sign, so the force comes from
    /// the side the wheel is turned to and pushes it back, and a wheel
    /// let go off center comes back toward center.</summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(-30.0)]
    public void TheForcePointsAtCenter(double startDegrees)
    {
        var plant = GearWheel();
        plant.Angle = startDegrees * Deg;
        var s = Settings(SteeringVehicle.Sedan, plant.WheelTorque);
        var centering = new WheelCentering();
        Warm(centering, plant, s, null, 60 * Mph);
        var frame = centering.Step(s, null, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        Assert.Equal(Math.Sign(startDegrees), Math.Sign(frame.Level));
        Assert.Equal(Math.Sign(startDegrees), Math.Sign(frame.TorqueNm));

        var trace = plant.Run(centering, s, null, 60 * Mph, 1.0, 3000);
        Assert.True(Math.Abs(trace[^1]) < Math.Abs(startDegrees * Deg) * 0.3, $"ended at {trace[^1] / Deg:F1} degrees");
    }

    public static TheoryData<string, bool, double, double> Releases
    {
        get
        {
            var data = new TheoryData<string, bool, double, double>();
            foreach (var car in SteeringVehicle.All)
                foreach (bool direct in new[] { false, true })
                    foreach (double mph in new[] { 25.0, 60.0, 120.0 })
                        foreach (double start in new[] { 5.0, 30.0, 90.0 })
                            data.Add(car.Id, direct, mph, start);
            return data;
        }
    }

    /// <summary>The stability rule on a wheel no test has measured,
    /// whose damper is as strong as the rule takes it to be: let go from
    /// 5, 30 and 90 degrees, every car at 25, 60 and 120 mph, the wheel
    /// comes to rest. It is not swinging in the last half second, and
    /// the guard never had to step in.</summary>
    [Theory]
    [MemberData(nameof(Releases))]
    public void AWheelLetGoComesToRest(string carId, bool direct, double mph, double startDegrees)
    {
        var plant = direct ? DirectDrive() : GearWheel();
        plant.DamperFull = plant.WheelTorque / 10.0;
        plant.Angle = startDegrees * Deg;
        var s = Settings(SteeringVehicle.ById(carId), plant.WheelTorque);
        var centering = new WheelCentering();
        Warm(centering, plant, s, null, mph * Mph);
        var trace = plant.Run(centering, s, null, mph * Mph, 1.0, 4000);
        var last = centering.Step(s, null, plant.Axis, mph * Mph, 1.0, (long)(plant.Time * 1000));
        output.WriteLine($"{carId} direct={direct} {mph} mph from {startDegrees}: rests at {trace[^1] / Deg:F2} deg, crossings {Crossings(trace)}, late swing {LateSwing(trace, 500) / Deg:F3} deg, held back {last.HeldBack}");
        Assert.True(LateSwing(trace, 500) < 0.5 * Deg, "still swinging");
        Assert.False(last.HeldBack);
        Assert.False(last.Stopped);
    }

    /// <summary>The guard, on a wheel whose damper gives a tenth of
    /// what the rule took it to give and whose loop is twice as slow:
    /// the wheel may start to swing, and the guard cuts the PC part's
    /// slope until it rests.</summary>
    [Theory]
    [InlineData("x1", 60.0)]
    [InlineData("race", 60.0)]
    [InlineData("sedan", 120.0)]
    public void AWheelWithLessDampingThanAssumedIsBroughtToRest(string carId, double mph)
    {
        var plant = GearWheel();
        plant.DamperFull = plant.WheelTorque / 100.0;
        plant.Delay = 0.020;
        plant.Friction = 0.02;
        plant.Angle = 20 * Deg;
        var s = Settings(SteeringVehicle.ById(carId), plant.WheelTorque, friction: 0.0);
        var centering = new WheelCentering();
        Warm(centering, plant, s, null, mph * Mph);
        var trace = plant.Run(centering, s, null, mph * Mph, 1.0, 8000);
        var last = centering.Step(s, null, plant.Axis, mph * Mph, 1.0, (long)(plant.Time * 1000));
        double widest = 0;
        foreach (double a in trace) widest = Math.Max(widest, Math.Abs(a));
        output.WriteLine($"{carId} {mph} mph: crossings {Crossings(trace)}, widest {widest / Deg:F1} deg, late swing {LateSwing(trace, 500) / Deg:F3} deg, held back {last.HeldBack}");
        Assert.True(last.HeldBack);
        Assert.True(LateSwing(trace, 500) < 1.0 * Deg, "still swinging after eight seconds");
        Assert.True(widest < 120 * Deg, "the wheel swung most of the way to its lock first");
    }

    /// <summary>Lower damping makes the PC part less steep, never
    /// unstable: at 100 percent the slope at center is the model's, and
    /// with the damper and friction both off there is no PC slope to
    /// carry.</summary>
    [Fact]
    public void LessDampingSoftensThePcPart()
    {
        var car = SteeringVehicle.StanfordX1;
        double Slope(double damping, double friction)
        {
            var s = Settings(car, 8.0, damping, friction);
            var centering = new WheelCentering();
            var plant = DirectDrive();
            plant.Angle = 1 * Deg;
            Warm(centering, plant, s, null, 60 * Mph);
            var frame = centering.Step(s, null, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
            return frame.TorqueNm / 1.0;
        }
        double full = Slope(100, 0.30), half = Slope(50, 0.0), none = Slope(0, 0.0);
        output.WriteLine($"N.m per degree at one degree: full {full:F3}, half damping and no friction {half:F3}, neither {none:F3}");
        Assert.InRange(full, 0.40, 0.46);
        Assert.InRange(half, full * 0.4, full * 0.6);
        Assert.Equal(0.0, none, 6);
    }

    /// <summary>The frame's units: friction's limit is the setting over
    /// the wheel's torque, the damper may push back with all the wheel
    /// has, and the force never passes the wheel's torque however heavy
    /// the car.</summary>
    [Fact]
    public void TheFrameIsInTheWheelsOwnUnits()
    {
        var s = Settings(SteeringVehicle.RaceCar, 2.2, friction: 0.44);
        var centering = new WheelCentering();
        var plant = GearWheel();
        plant.Angle = 40 * Deg;
        Warm(centering, plant, s, null, 60 * Mph);
        var frame = centering.Step(s, null, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        Assert.Equal(2000, frame.FrictionSaturation);
        Assert.Equal(10000, frame.DamperSaturation);
        Assert.Equal(32767, frame.Level);
        Assert.Equal(2.2, frame.TorqueNm, 3);
        Assert.Equal(0, frame.SpringCoefficient);
        Assert.Equal(40.0, frame.AngleDegrees, 6);
    }

    /// <summary>Spring mode and a wheel with no torque figure give
    /// nothing: this type takes no part in the spring of Auto Centering
    /// Strength.</summary>
    [Fact]
    public void SpringModeSendsNothing()
    {
        var off = new CenteringSettings(false, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.3, false, 20, 1, 2.2, 900);
        var frame = new WheelCentering().Step(off, null, 0.5, 20, 1.0, 5000);
        Assert.Equal(0, frame.Level);
        Assert.Equal(0, frame.DamperCoefficient);
        Assert.Equal(0, frame.FrictionSaturation);
    }

    /// <summary>Centering comes in over a second, once the wheel has
    /// been at rest for <see cref="WheelCentering.CalmMs"/> after it
    /// turns on.</summary>
    [Fact]
    public void CenteringComesInOverASecond()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var centering = new WheelCentering();
        int calm = WheelCentering.CalmMs;
        short At(long ms) => centering.Step(s, null, 0.1, 60 * Mph, 1.0, 10_000 + ms).Level;
        short start = At(0), waiting = At(calm - 1), begins = At(calm);
        short quarter = At(calm + 250), half = At(calm + 500), full = At(calm + 1000), later = At(calm + 1500);
        Assert.Equal(0, start);
        Assert.Equal(0, waiting);
        Assert.Equal(0, begins);
        Assert.InRange(quarter, full * 0.2, full * 0.3);
        Assert.InRange(half, full * 0.45, full * 0.55);
        Assert.True(full > 0);
        Assert.Equal(full, later);
        centering.Reset();
        Assert.Equal(0, At(calm + 2000));
    }

    /// <summary>A wheel that goes unstepped for longer than
    /// <see cref="WheelCenteringRuntime.StaleMs"/> was let go by its
    /// slot, or the engine stopped. When steps come back, centering
    /// starts afresh: it waits for a wheel at rest and comes in over a
    /// second. A wheel routed through SDL used to get the whole force
    /// back at the first poll after an engine restart.</summary>
    [Fact]
    public void CenteringStartsAfreshAfterTheWheelWentUnstepped()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var runtime = new WheelCenteringRuntime();
        int warm = WheelCentering.CalmMs + WheelCentering.SoftStartMs + 50;
        CenteringFrame frame = default;
        for (int ms = 0; ms <= warm; ms++) frame = runtime.Step(s, "", 0.1, -1, 0, 1000 + ms, out _);
        short full = frame.Level;
        Assert.True(full > 0);

        // A pause no longer than the limit changes nothing.
        long tick = 1000 + warm + WheelCenteringRuntime.StaleMs;
        Assert.Equal(full, runtime.Step(s, "", 0.1, -1, 0, tick, out _).Level);

        // A longer one starts centering afresh.
        tick += WheelCenteringRuntime.StaleMs + 1;
        Assert.Equal(0, runtime.Step(s, "", 0.1, -1, 0, tick, out _).Level);
        for (int ms = 1; ms <= warm; ms++) frame = runtime.Step(s, "", 0.1, -1, 0, tick + ms, out _);
        Assert.Equal(full, frame.Level);
    }

    /// <summary>A Release Test that is stopped, by its button or by its
    /// window closing, may leave the wheel off center and turning.
    /// Centering comes back as it does after a test that ran to its end:
    /// once the wheel is at rest, over a second. It used to come back
    /// whole at the next poll.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CenteringComesBackSoftlyAfterATestIsStopped(bool windowClosed)
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var runtime = new WheelCenteringRuntime();
        int warm = WheelCentering.CalmMs + WheelCentering.SoftStartMs + 50;
        CenteringFrame frame = default;
        for (int ms = 0; ms <= warm; ms++) frame = runtime.Step(s, "", 0.1, -1, 0, 1000 + ms, out _);
        short full = frame.Level;
        Assert.True(full > 0);

        long tick = 1000 + warm + 1;
        Assert.True(runtime.StartTest(s, "", tick));
        runtime.Step(s, "", 0.1, -1, 0, tick, out bool testing);
        Assert.True(testing);
        if (windowClosed) runtime.ClearTest(); else runtime.CancelTest();

        frame = runtime.Step(s, "", 0.1, -1, 0, tick + 1, out testing);
        Assert.False(testing);
        Assert.Equal(0, frame.Level);
        for (int ms = 2; ms <= warm + 2; ms++) frame = runtime.Step(s, "", 0.1, -1, 0, tick + ms, out _);
        Assert.Equal(full, frame.Level);
    }

    /// <summary>A swing before the force has come in is no oscillation of
    /// the force's making. A hand that weaves the wheel evenly while
    /// centering waits for it to rest is not counted, and the force then
    /// comes in whole. The guard used to count the weave and hold the
    /// force back before it had started.</summary>
    [Fact]
    public void AWeaveBeforeTheForceStartsIsNotCounted()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        // Four seconds of an even weave, 20 degrees either side twice a second, then at rest 30 degrees out.
        double Weave(int ms) => ms < 4000 ? 20.0 * Math.Sin(2.0 * Math.PI * ms / 500.0) : 30.0;
        int rest = WheelCentering.CalmMs + WheelCentering.SoftStartMs + WheelCentering.RunawayTailMs + 100;

        var centering = new WheelCentering();
        var (weaving, mostWhileWeaving) = Drive(centering, s, Weave, 0, 3999);
        Assert.Equal(0, mostWhileWeaving);
        Assert.False(weaving.HeldBack);
        var (rested, _) = Drive(centering, s, Weave, 4000, 4000 + rest);
        Assert.False(rested.HeldBack);
        Assert.False(rested.Stopped);

        // The same wheel at rest from the first poll takes the same force.
        var plain = new WheelCentering();
        var (alone, _) = Drive(plain, s, _ => 30.0, 0, rest);
        Assert.True(alone.Level > 1000, $"level {alone.Level}");
        Assert.Equal(alone.Level, rested.Level);
    }

    /// <summary>The force waits for a wheel at rest. A wheel turns
    /// itself from lock to lock when it is plugged in, and a driver may
    /// be steering when the mode turns on: no force comes in while the
    /// wheel moves, nothing is stopped, and once the wheel has stayed
    /// in one place the force comes in as it does at any start.</summary>
    [Fact]
    public void TheForceWaitsForAWheelAtRest()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var centering = new WheelCentering();
        // Two seconds from lock to lock and back at 400 degrees a second, then at rest 30 degrees out.
        double Sweep(int ms)
        {
            if (ms >= 2000) return 30.0;
            double travelled = ms * 0.4 % 1600.0;
            return travelled < 800.0 ? travelled - 400.0 : 1200.0 - travelled;
        }
        var (turning, mostWhileTurning) = Drive(centering, s, Sweep, 0, 1999);
        Assert.Equal(0, mostWhileTurning);
        Assert.False(turning.Stopped);

        var (waiting, mostWhileWaiting) = Drive(centering, s, Sweep, 2000, 2000 + WheelCentering.CalmMs - 1);
        Assert.Equal(0, mostWhileWaiting);
        var (rested, _) = Drive(centering, s, Sweep, 2000 + WheelCentering.CalmMs, 2000 + WheelCentering.CalmMs + WheelCentering.SoftStartMs + 50);
        Assert.False(waiting.Stopped || rested.Stopped);
        Assert.True(rested.Level > 1000, $"level {rested.Level}");
    }

    /// <summary>What the runaway check reads, on wheels that are not
    /// pushed the wrong way. A ten-bit axis moves nine tenths of a
    /// degree a count, which read over one poll is 880 degrees a
    /// second: at rest on the edge of a count, and turned slowly
    /// farther out. A wheel that comes back fast swings past center
    /// and leaves it on the far side. A wheel that stood at center is
    /// turned by a hand, and so is one that comes in and goes out again
    /// short of where it stood. None of them is stopped. A wheel that
    /// stood off center and speeds farther out on that side is.</summary>
    [Fact]
    public void TheRunawayCheckReadsAWheelDrivenFartherOut_AndNothingElse()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        int span = WheelCentering.CalmMs + WheelCentering.SoftStartMs + WheelCentering.RunawayTailMs + 200;

        // At rest on the edge of a count, 20 degrees out.
        var edge = new WheelCentering();
        var (onEdge, _) = Drive(edge, s, ms => ms % 2 == 0 ? 20.0 : 20.0 + 900.0 / 1024.0, 0, span);
        Assert.False(onEdge.Stopped);
        Assert.True(onEdge.Level > 1000, $"level {onEdge.Level}");

        // The moves below start 600 ms in, with the force at three tenths and coming in.
        static double From(double start, double degreesPerSecond, double end, int ms)
        {
            if (ms < 600) return start;
            double at = start + degreesPerSecond * (ms - 600) / 1000.0;
            return degreesPerSecond > 0 ? Math.Min(at, end) : Math.Max(at, end);
        }

        // A ten-bit axis turned farther out at 30 degrees a second: each count is a step in one poll.
        static double Count(double degrees) => Math.Round(degrees * 1024.0 / 900.0) * 900.0 / 1024.0;
        var coarse = new WheelCentering();
        Assert.False(Drive(coarse, s, ms => Count(From(20, 30, 60, ms)), 0, span).Last.Stopped);

        // In from 40 degrees to 10 at 600 degrees a second, and out again to 35 at 400: fast, and short
        // of where it stood.
        var inAndOut = new WheelCentering();
        Assert.False(Drive(inAndOut, s, ms => ms < 650 ? From(40, -600, 10, ms) : Math.Min(35.0, 10.0 + 0.4 * (ms - 650)), 0, span).Last.Stopped);

        // Back from 40 degrees at 600 degrees a second, and on to 70 degrees past center: farther from
        // center than it stood, on the other side.
        var back = new WheelCentering();
        Assert.False(Drive(back, s, ms => From(40, -600, -70, ms), 0, span).Last.Stopped);

        // From a degree off center to 100 degrees at 400 degrees a second, by hand.
        var hand = new WheelCentering();
        Assert.False(Drive(hand, s, ms => From(1, 400, 100, ms), 0, span).Last.Stopped);

        // From 20 degrees out to 120 at 400 degrees a second: driven farther out.
        var driven = new WheelCentering();
        var (gone, _) = Drive(driven, s, ms => From(20, 400, 120, ms), 0, span);
        Assert.True(gone.Stopped);
        Assert.Equal(0, gone.Level);

        // The same move at 150 degrees a second is under the mark.
        var slow = new WheelCentering();
        Assert.False(Drive(slow, s, ms => From(20, 150, 120, ms), 0, span).Last.Stopped);
    }

    /// <summary>A wheel whose motor answers the other way would be
    /// pushed away from center. Let go off center as centering starts,
    /// it speeds away, and the PC part stops before the wheel is far
    /// gone. With the sign the Release Test measured, the same wheel
    /// centers.</summary>
    [Fact]
    public void AWheelPushedTheWrongWayIsStopped_AndCentersOnceMeasured()
    {
        var plant = DirectDrive();
        plant.ForceSign = -1;
        plant.Angle = 10 * Deg;
        var s = Settings(SteeringVehicle.Sedan, plant.WheelTorque);
        var centering = new WheelCentering();
        var trace = plant.Run(centering, s, null, 60 * Mph, 1.0, 3000);
        var last = centering.Step(s, null, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        output.WriteLine($"wrong sign: ended at {trace[^1] / Deg:F1} degrees, stopped {last.Stopped}");
        Assert.True(last.Stopped);
        Assert.Equal(0, last.Level);
        Assert.True(Math.Abs(trace[^1]) < 120 * Deg, "the wheel went most of the way to its lock");

        var again = DirectDrive();
        again.ForceSign = -1;
        again.Angle = 10 * Deg;
        var measured = Measured(again, sign: -1);
        var centered = new WheelCentering();
        var back = again.Run(centered, s, measured, 60 * Mph, 1.0, 4000);
        Assert.True(Math.Abs(back[^1]) < 3 * Deg, $"ended at {back[^1] / Deg:F1} degrees");
    }

    /// <summary>Once the wheel's own spring is measured it takes most
    /// of the slope at center, the PC adds the rest, and the two
    /// together are the model's curve. The spring runs at one of the
    /// coefficients the test measured, here the lowest: the next one up
    /// is steeper than the curve at center.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(8.0)]
    [InlineData(25.0)]
    [InlineData(60.0)]
    public void TheWheelsSpringAndThePcTogetherAreTheCurve(double degrees)
    {
        var plant = DirectDrive();
        // A spring strong enough to take its whole share: 400 N.m over half of 900 degrees is 0.89 N.m a degree.
        plant.SpringFull = 400.0;
        var car = SteeringVehicle.StanfordX1;
        var s = Settings(car, plant.WheelTorque);
        var measured = Measured(plant);
        plant.Angle = degrees * Deg;
        var centering = new WheelCentering();
        Warm(centering, plant, s, measured, 60 * Mph);
        var frame = centering.Step(s, measured, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));

        double halfRange = 450 * Deg;
        double spring = Math.Min(plant.SpringFull * frame.SpringCoefficient / 10000.0 * (degrees * Deg / halfRange),
            frame.SpringSaturation / 10000.0 * plant.WheelTorque);
        double pc = frame.Level / 32767.0 * plant.WheelTorque;
        double curve = centering.Curve.Torque(degrees * Deg);
        output.WriteLine($"{degrees} deg: spring {spring:F3} + PC {pc:F3} = {spring + pc:F3}, curve {curve:F3}, coefficient {frame.SpringCoefficient}, limit {frame.SpringSaturation}");
        Assert.Equal(WheelCalibration.SpringCoefficients[0], frame.SpringCoefficient);
        Assert.True(Math.Abs(spring + pc - curve) <= 0.04 * curve + 0.02, $"{spring + pc} against {curve}");
        if (degrees <= 3.0) Assert.True(spring > pc, "the wheel's own spring carries most of the slope at center");
    }

    /// <summary>A measured wheel returns without swinging past center
    /// when the test's return ratio is in use, and with less damping it
    /// swings past.</summary>
    [Fact]
    public void TheReturnRatioStopsTheSwingPastCenter()
    {
        var car = SteeringVehicle.Sedan;
        double Past(double ratio)
        {
            var plant = GearWheel();
            plant.DamperFull = 2.0;
            plant.SpringFull = 0;
            var measured = Measured(plant, ratio);
            plant.Angle = 90 * Deg;
            var s = Settings(car, plant.WheelTorque, friction: 0.10);
            var centering = new WheelCentering();
            Warm(centering, plant, s, measured, 60 * Mph);
            return WheelPlant.Overshoot(plant.Run(centering, s, measured, 60 * Mph, 1.0, 3000), 1) / Deg;
        }
        double loose = Past(0.0), firm = Past(0.9);
        output.WriteLine($"past center: no return ratio {loose:F2} deg, ratio 0.9 {firm:F2} deg");
        Assert.True(loose > 10.0);
        Assert.True(firm < loose * 0.75);
    }

    /// <summary>The settings read from a device's saved values, with
    /// the wheel's listed torque and the range PadForge sets.</summary>
    [Fact]
    public void TheSettingsReadTheSavedValues()
    {
        var ps = new PadSetting();
        var defaults = CenteringSettings.From(ps, 0, 0);
        Assert.False(defaults.Vehicle);
        Assert.Same(SteeringVehicle.Sedan, defaults.Car);
        Assert.Equal(100, defaults.WeightPercent);
        Assert.Equal(0.30, defaults.Friction, 9);
        Assert.True(defaults.SpeedFromGame);
        Assert.Equal(SpeedUnits.DefaultFixedSpeedKmh / 3.6, defaults.FixedSpeed, 9);
        Assert.Equal(CenteringSettings.UnlistedWheelTorque, defaults.WheelTorque);
        Assert.Equal(900, defaults.Range);

        ps.CenteringMode = "1";
        ps.CenteringVehicle = "race";
        ps.CenteringWeight = "65";
        ps.CenteringFriction = "45";
        ps.CenteringSpeedSource = "1";
        ps.CenteringFixedSpeed = "72";
        ps.CenteringGrip = "3";
        ps.CenteringWheelTorque = "22";
        ps.CenteringWheelRange = "540";
        var read = CenteringSettings.From(ps, 5.0, 0);
        Assert.True(read.Vehicle);
        Assert.Same(SteeringVehicle.RaceCar, read.Car);
        Assert.Equal(65, read.WeightPercent);
        Assert.Equal(0.45, read.Friction, 9);
        Assert.False(read.SpeedFromGame);
        Assert.Equal(20.0, read.FixedSpeed, 9);
        Assert.Equal(2.2, read.WheelTorque, 9);
        Assert.Equal(540, read.Range);
        Assert.Equal(CenteringSettings.SnowGrip, read.GripFor(0.9));
        Assert.Equal(20.0, read.SpeedFor(33.0), 9);

        // The listed figure where none is entered, the set range where PadForge sets one.
        ps.CenteringWheelTorque = "0";
        ps.CenteringSpeedSource = "0";
        ps.CenteringGrip = "0";
        var listed = CenteringSettings.From(ps, 5.0, 1080);
        Assert.Equal(5.0, listed.WheelTorque);
        Assert.Equal(1080, listed.Range);
        Assert.Equal(33.0, listed.SpeedFor(33.0));
        Assert.Equal(20.0, listed.SpeedFor(-1), 9);
        Assert.Equal(0.6, listed.GripFor(0.6));
        Assert.Equal(CenteringSettings.DryGrip, listed.GripFor(0));
        // A value that is no number reads as the default, and one out of range as the nearest in range.
        ps.CenteringWeight = "heavy";
        ps.CenteringFriction = "900";
        var odd = CenteringSettings.From(ps, 0, 0);
        Assert.Equal(100, odd.WeightPercent);
        Assert.Equal(1.5, odd.Friction, 9);
    }

    /// <summary>The saved form of a measurement reads back as written,
    /// and anything else reads as no measurement.</summary>
    [Fact]
    public void AMeasurementReadsBackAsWritten()
    {
        var written = new WheelCalibration
        {
            Inertia = 0.0123, Friction = 0.071, Delay = 0.0065, DamperMid = 0.17, DamperFull = 0.41,
            SpringThird = 4.1, SpringTwoThirds = 9.9, SpringFull = 17.5,
            ForceSign = -1, ReturnRatio = 0.85, WheelTorque = 3.9,
        };
        Assert.Equal("1;0.0123;0.071;0.0065;0.17;0.41;4.1;9.9;17.5;-1;0.85;3.9", written.Format());
        var read = WheelCalibration.Parse(written.Format());
        Assert.NotNull(read);
        Assert.Equal(written.Inertia, read.Inertia);
        Assert.Equal(written.Friction, read.Friction);
        Assert.Equal(written.Delay, read.Delay);
        Assert.Equal(written.DamperMid, read.DamperMid);
        Assert.Equal(written.DamperFull, read.DamperFull);
        Assert.Equal(written.SpringThird, read.SpringThird);
        Assert.Equal(written.SpringTwoThirds, read.SpringTwoThirds);
        Assert.Equal(written.SpringFull, read.SpringFull);
        Assert.Equal(-1, read.ForceSign);
        Assert.Equal(written.ReturnRatio, read.ReturnRatio);
        Assert.Equal(written.WheelTorque, read.WheelTorque);
        Assert.Null(WheelCalibration.Parse(""));
        Assert.Null(WheelCalibration.Parse(null));
        // Another version, a value that is no number, no inertia, and a value short.
        Assert.Null(WheelCalibration.Parse("2;0.01;0.1;0.006;0;0;0;0;0;1;0;2.2"));
        Assert.Null(WheelCalibration.Parse("1;abc;0.1;0.006;0;0;0;0;0;1;0;2.2"));
        Assert.Null(WheelCalibration.Parse("1;0;0.1;0.006;0;0;0;0;0;1;0;2.2"));
        Assert.Null(WheelCalibration.Parse("1;0.01;0.1;0.006;0;0;0;0;0;1;0"));
        Assert.NotNull(WheelCalibration.Parse("1;0.01;0.1;0.006;0;0;0;0;0;1;0;2.2"));
    }

    /// <summary>A measurement's strengths between and at its measured
    /// coefficients: the damper on the line through its two points, the
    /// coefficient for a damping the least that gives it, and the
    /// spring at its three.</summary>
    [Fact]
    public void AMeasurementAnswersForItsCoefficients()
    {
        var m = new WheelCalibration
        {
            Inertia = 0.01, Delay = 0.005, WheelTorque = 5, DamperMid = 0.1, DamperFull = 0.5,
            SpringThird = 2, SpringTwoThirds = 9, SpringFull = 30,
        };
        // The damper's two points are eight fifteenths and the whole of the coefficient.
        Assert.Equal(new[] { 5333, 10000 }, WheelCalibration.DamperCoefficients);
        Assert.True(m.HasDamper);
        Assert.Equal(0.0, m.DampingAt(0), 9);
        Assert.Equal(0.05, m.DampingAt(2667), 4);
        Assert.Equal(0.1, m.DampingAt(5333), 9);
        Assert.Equal(0.3, m.DampingAt(7667), 3);
        Assert.Equal(0.5, m.DampingAt(10000), 9);
        Assert.Equal(0, m.DamperCoefficientFor(0));
        // In hundredths: the least that gives the damping.
        Assert.Equal(2700, m.DamperCoefficientFor(0.05));
        Assert.Equal(7700, m.DamperCoefficientFor(0.3));
        Assert.Equal(10000, m.DamperCoefficientFor(4.0));
        // On a firmware with fifteen steps: the least step that gives it, at that step's own coefficient.
        Assert.Equal(2667, m.DamperCoefficientFor(0.05, WheelCalibration.FourBitDamperSteps));
        Assert.Equal(3333, m.DamperCoefficientFor(0.051, WheelCalibration.FourBitDamperSteps));
        Assert.Equal(8000, m.DamperCoefficientFor(0.3, WheelCalibration.FourBitDamperSteps));
        Assert.Equal(667, m.DamperCoefficientFor(0.001, WheelCalibration.FourBitDamperSteps));
        for (int step = 0; step <= 15; step++)
        {
            // Every step's coefficient lands on that step in the four bits a Logitech or a Fanatec wheel keeps:
            // twice the coefficient's 15 bits, the top four of sixteen (new-lg4ff SCALE_COEFF(k, 4)).
            int coefficient = WheelCalibration.DamperGrid(step, 15);
            int wire = Math.Min(0xffff, (int)((long)coefficient * 0x7fff / 10000) * 2) >> 12;
            Assert.Equal(step, wire);
        }
        Assert.Equal(8, Math.Min(0xffff, (int)((long)WheelCalibration.DamperCoefficients[0] * 0x7fff / 10000) * 2) >> 12);
        Assert.Equal(2.0, m.SpringAt(0));
        Assert.Equal(9.0, m.SpringAt(1));
        Assert.Equal(30.0, m.SpringAt(2));
        Assert.Equal(2.0, m.SpringStiffnessAt(3333), 9);
        Assert.Equal(9.0, m.SpringStiffnessAt(6667), 9);
        Assert.Equal(30.0, m.SpringStiffnessAt(10000), 9);
        Assert.Equal(1.0, m.SpringStiffnessAt(1666), 2);

        // A damper seen at one coefficient only reads on the line through that point.
        var one = new WheelCalibration { Inertia = 0.01, Delay = 0.005, WheelTorque = 5, DamperFull = 0.4 };
        Assert.Equal(0.2, one.DampingAt(5000), 9);
        Assert.Equal(0.4, one.DampingAt(10000), 9);
        var low = new WheelCalibration { Inertia = 0.01, Delay = 0.005, WheelTorque = 5, DamperMid = 0.16 };
        Assert.Equal(0.3, low.DampingAt(10000), 3);
        // A full coefficient that showed less than the middle one reads level from the middle up.
        var odd = new WheelCalibration { Inertia = 0.01, Delay = 0.005, WheelTorque = 5, DamperMid = 0.3, DamperFull = 0.2 };
        Assert.Equal(0.3, odd.DampingAt(10000), 9);
        var none = new WheelCalibration { Inertia = 0.01, Delay = 0.005, WheelTorque = 5 };
        Assert.False(none.HasDamper);
        Assert.Equal(0, none.DamperCoefficientFor(0.2));
        Assert.Equal(0.0, none.SpringStiffnessAt(10000));
    }

    /// <summary>A measurement is in the newton meters of the torque
    /// figure it was taken with. With another figure its torques scale
    /// and the wheel's own facts stay.</summary>
    [Fact]
    public void AMeasurementScalesWithTheTorqueFigure()
    {
        var m = new WheelCalibration
        {
            Inertia = 0.01, Friction = 0.1, Delay = 0.005, WheelTorque = 2.5, DamperMid = 0.1, DamperFull = 0.25,
            SpringThird = 2, SpringTwoThirds = 4, SpringFull = 6, ForceSign = -1, ReturnRatio = 0.7,
        };
        Assert.Same(m, m.ScaledTo(2.5));
        var twice = m.ScaledTo(5.0);
        Assert.Equal(0.02, twice.Inertia, 12);
        Assert.Equal(0.2, twice.Friction, 12);
        Assert.Equal(0.005, twice.Delay);
        Assert.Equal(0.2, twice.DamperMid, 12);
        Assert.Equal(0.5, twice.DamperFull, 12);
        Assert.Equal(4.0, twice.SpringThird, 12);
        Assert.Equal(8.0, twice.SpringTwoThirds, 12);
        Assert.Equal(12.0, twice.SpringFull, 12);
        Assert.Equal(-1, twice.ForceSign);
        Assert.Equal(0.7, twice.ReturnRatio);
        Assert.Equal(5.0, twice.WheelTorque);

        // Under a doubled figure the wheel's own spring reads twice as stiff, and the spring and the PC
        // together are still the curve, in the newton meters of the figure in use.
        var plant = DirectDrive();
        plant.SpringFull = 200.0;
        plant.Angle = 20 * Deg;
        var measured = Measured(plant);
        var doubled = Settings(SteeringVehicle.StanfordX1, plant.WheelTorque * 2);
        var centering = new WheelCentering();
        Warm(centering, plant, doubled, measured, 60 * Mph);
        var frame = centering.Step(doubled, measured, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        var scaled = measured.ScaledTo(doubled.WheelTorque);
        int index = Array.IndexOf(WheelCalibration.SpringCoefficients, frame.SpringCoefficient);
        Assert.True(index >= 0, $"coefficient {frame.SpringCoefficient}");
        double spring = Math.Min(scaled.SpringAt(index) * (20 * Deg / (450 * Deg)), frame.SpringSaturation / 10000.0 * doubled.WheelTorque);
        double pc = frame.Level / 32767.0 * doubled.WheelTorque;
        double curve = centering.Curve.Torque(20 * Deg);
        output.WriteLine($"spring {spring:F3} + PC {pc:F3} = {spring + pc:F3}, curve {curve:F3}, coefficient {frame.SpringCoefficient}");
        Assert.True(Math.Abs(spring + pc - curve) <= 0.04 * curve + 0.02, $"{spring + pc} against {curve}");
    }

    /// <summary>A wheel with no damper of its own is damped from the
    /// PC: let go off center it comes to rest, and no damper effect is
    /// sent. So is a wheel whose damper the test saw nothing of.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AWheelWithNoDamperIsDampedFromThePc(bool direct, bool measuredNone)
    {
        var plant = direct ? DirectDrive() : GearWheel();
        plant.DamperFull = 0;
        plant.SpringFull = 0;
        plant.Angle = 30 * Deg;
        var effects = measuredNone ? CenteringEffects.All : CenteringEffects.Spring | CenteringEffects.Friction;
        var s = new CenteringSettings(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.30, false, 60 * Mph, 1,
            plant.WheelTorque, 900, effects);
        var measured = measuredNone ? Measured(plant) : null;
        var centering = new WheelCentering();
        Warm(centering, plant, s, measured, 60 * Mph);
        var trace = plant.Run(centering, s, measured, 60 * Mph, 1.0, 4000);
        var last = centering.Step(s, measured, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        output.WriteLine($"direct={direct} measured none={measuredNone}: rests at {trace[^1] / Deg:F2} deg, crossings {Crossings(trace)}, late swing {LateSwing(trace, 500) / Deg:F3} deg, held back {last.HeldBack}");
        Assert.Equal(0, last.DamperCoefficient);
        Assert.Equal(0, last.DamperSaturation);
        Assert.True(LateSwing(trace, 500) < 0.5 * Deg, "still swinging");
        Assert.True(Math.Abs(trace[^1]) < 10 * Deg, "the wheel did not come back");
        Assert.False(last.HeldBack);

        // The damping is in the force: a wheel moving toward higher axis values at center is pushed back.
        var moving = new WheelCentering();
        long tick = 50_000;
        for (int ms = 0; ms <= WheelCentering.SoftStartMs + 20; ms++) moving.Step(s, measured, 0.0, 60 * Mph, 1.0, tick + ms);
        tick += WheelCentering.SoftStartMs + 20;
        CenteringFrame frame = default;
        for (int ms = 1; ms <= 40; ms++) frame = moving.Step(s, measured, ms * 0.00002, 60 * Mph, 1.0, tick + ms);
        var still = moving.Step(s, measured, 40 * 0.00002, 60 * Mph, 1.0, tick + 41);
        Assert.True(frame.Level > 0, $"level {frame.Level}");
        Assert.True(frame.Level > still.Level || still.Level > 0);
    }

    /// <summary>A wheel with no friction effect is sent none, and its
    /// friction setting counts for nothing in how steep the PC part may
    /// be.</summary>
    [Fact]
    public void AWheelWithNoFrictionEffectIsSentNone()
    {
        var with = Settings(SteeringVehicle.StanfordX1, 8.0, damping: 0, friction: 0.30);
        var without = new CenteringSettings(true, SteeringVehicle.StanfordX1, 100, 100, 100, 0, 0.30, false, 60 * Mph, 1, 8.0, 900,
            CenteringEffects.Spring | CenteringEffects.Damper);
        CenteringFrame At(in CenteringSettings s)
        {
            var centering = new WheelCentering();
            var plant = DirectDrive();
            plant.Angle = 1 * Deg;
            Warm(centering, plant, s, null, 60 * Mph);
            return centering.Step(s, null, plant.Axis, 60 * Mph, 1.0, (long)(plant.Time * 1000));
        }
        var a = At(with);
        var b = At(without);
        Assert.True(a.FrictionSaturation > 0);
        Assert.True(a.TorqueNm > 0);
        Assert.Equal(0, b.FrictionSaturation);
        Assert.Equal(0.0, b.TorqueNm, 6);
    }

    /// <summary>The guard: a swing of a couple of degrees at eight
    /// hertz that does not die away takes the PC's force down a step,
    /// and further steps follow if it goes on, to nothing. A slow wide
    /// swing counts after more of it, and one that grows counts sooner.
    /// A hand's slow turns, its uneven corrections and a swing that
    /// dies away leave the guard alone.</summary>
    [Fact]
    public void TheGuardCutsASteadyOscillation_AndLeavesAHandAlone()
    {
        static OscillationGuard Feed(Func<double, double> angle, int ms)
        {
            var guard = new OscillationGuard();
            for (int t = 0; t < ms; t++) guard.Add(angle(t / 1000.0), t);
            return guard;
        }
        var steady = Feed(t => 2 * Deg * Math.Sin(2 * Math.PI * 8 * t), 700);
        Assert.Equal(0.5, steady.Factor);
        var longer = Feed(t => 2 * Deg * Math.Sin(2 * Math.PI * 8 * t), 1250);
        Assert.Equal(0.25, longer.Factor);
        var longest = Feed(t => 2 * Deg * Math.Sin(2 * Math.PI * 8 * t), 4000);
        Assert.Equal(0.0, longest.Factor);

        // A slow swing that keeps going is one too, after more of it.
        var slow = Feed(t => 20 * Deg * Math.Sin(2 * Math.PI * 1.5 * t), 3000);
        Assert.Equal(1.0, slow.Factor);
        var slowLonger = Feed(t => 20 * Deg * Math.Sin(2 * Math.PI * 1.5 * t), 4500);
        Assert.Equal(0.5, slowLonger.Factor);
        // One that grows by a third each half cycle goes straight to a tenth, before a steady one would count.
        var growing = Feed(t => 3 * Deg * Math.Exp(1.8 * t) * Math.Sin(2 * Math.PI * 3 * t), 1100);
        Assert.Equal(0.1, growing.Factor);
        // So does a wide one at speed: 100 degrees each way three times a second is 600 degrees a second.
        var violent = Feed(t => 100 * Deg * Math.Sin(2 * Math.PI * 3 * t), 800);
        Assert.Equal(0.1, violent.Factor);
        var violentLonger = Feed(t => 100 * Deg * Math.Sin(2 * Math.PI * 3 * t), 1500);
        Assert.Equal(0.0, violentLonger.Factor);

        // A hand: a slow turn out and back, corrections of uneven width and pace, a swing that dies away.
        var hand = Feed(t => 40 * Deg * Math.Sin(2 * Math.PI * 0.4 * t), 8000);
        Assert.Equal(1.0, hand.Factor);
        var uneven = Feed(t => 15 * Deg * Math.Sin(2 * Math.PI * 0.7 * t) + 10 * Deg * Math.Sin(2 * Math.PI * 1.9 * t + 1) + 8 * Deg * Math.Sin(2 * Math.PI * 3.1 * t + 2), 20000);
        Assert.Equal(1.0, uneven.Factor);
        var dying = Feed(t => 6 * Deg * Math.Exp(-6 * t) * Math.Sin(2 * Math.PI * 7 * t), 2000);
        Assert.Equal(1.0, dying.Factor);
        var noise = Feed(t => 0.05 * Deg * Math.Sin(2 * Math.PI * 30 * t), 2000);
        Assert.Equal(1.0, noise.Factor);

        steady.Reset();
        Assert.Equal(1.0, steady.Factor);
    }

    /// <summary>The curve is sampled at steps of speed and grip. A
    /// game's speed or grip that wavers on the edge of two steps keeps
    /// one curve, and one that keeps changing, a car braking hard,
    /// rebuilds the same curve in place, so the polling thread allocates
    /// nothing for it (audit C6-1).</summary>
    [Fact]
    public void AChangingSpeedRebuildsTheCurveInPlace_AndAWaveringOneKeepsIt()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var centering = new WheelCentering();
        long tick = 10_000;
        centering.Step(s, null, 0.05, 20.12, 0.509, tick++);
        var curve = centering.Curve;
        double speed = curve.Speed, grip = curve.Grip;
        for (int i = 0; i < 200; i++)
        {
            bool odd = (i & 1) != 0;
            centering.Step(s, null, 0.05, odd ? 20.12 : 20.13, odd ? 0.509 : 0.511, tick++);
            Assert.Equal(speed, centering.Curve.Speed);
            Assert.Equal(grip, centering.Curve.Grip);
        }

        // A speed and a grip well into another step move the curve, in the same tables.
        centering.Step(s, null, 0.05, 21.0, 0.60, tick++);
        Assert.Equal(21.0, centering.Curve.Speed);
        Assert.Equal(0.60, centering.Curve.Grip, 9);
        Assert.Same(curve, centering.Curve);
        var fresh = new SteeringCurve(s.Car, s.Car.Feel.Scaled(s.WeightPercent, s.OnCenterPercent, s.AssistPercent), 21.0, 0.60,
            s.Range * Math.PI / 360.0);
        Assert.Equal(fresh.SteepestSlope, centering.Curve.SteepestSlope);
        Assert.Equal(fresh.Torque(10 * Deg), centering.Curve.Torque(10 * Deg));

        // Braking at 1 g with a poll a millisecond crosses a quarter meter a second every 25 polls.
        double braking = 40;
        foreach (long bytes in AllocationWindow.Clean(() =>
        {
            braking -= 0.00981;
            if (braking < 5) braking = 40;
            centering.Step(s, null, 0.05, braking, 1.0, tick++);
        }, 2000))
            Assert.Equal(0, bytes);
    }

    /// <summary>The stability rule runs on the delay the Release Test
    /// measured, up to the longest the test stores: a wheel measured at
    /// 38 ms gets the damping a 38 ms loop needs, not a 30 ms one's
    /// (audit C6-6).</summary>
    [Fact]
    public void TheStabilityRuleRunsOnTheWholeMeasuredDelay()
    {
        var s = new CenteringSettings(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.0, false, 60 * Mph, 1, 8.0, 900,
            CenteringEffects.All, 1000);
        WheelCalibration Wheel(double delay) => new()
        {
            Inertia = 0.02, Friction = 0.0, Delay = delay, WheelTorque = 8.0,
            DamperMid = 5.0 * WheelCalibration.DamperCoefficients[0] / 10000.0, DamperFull = 5.0,
        };
        double DampingAt(double delay) => new WheelCentering().Step(s, Wheel(delay), 0.05, 60 * Mph, 1.0, 10_000).Damping;
        double at30 = DampingAt(0.030), at38 = DampingAt(0.038);
        output.WriteLine($"damping {at30:F3} at 30 ms, {at38:F3} at 38 ms");
        Assert.True(at30 > 0);
        Assert.InRange(at38 / at30, 38 / 30.0 * 0.98, 38 / 30.0 * 1.02);
    }

    /// <summary>A car the game reports stopped reads 0 on the card: the
    /// frame carries the speed in use, and the model's floor stays
    /// inside the curve (audit C6-9).</summary>
    [Fact]
    public void AStoppedCarReadsZero_AndTheCurveKeepsItsFloor()
    {
        var s = Settings(SteeringVehicle.Sedan, 8.0);
        var centering = new WheelCentering();
        var frame = centering.Step(s, null, 0.05, 0.0, 0.7, 10_000);
        Assert.Equal(0.0, frame.Speed);
        Assert.Equal(0.7, frame.Grip);
        Assert.Equal(SteeringCurve.LeastSpeed, centering.Curve.Speed);
        Assert.Equal(0.0, new CenteringLive(true, frame, true, true).Speed);
        frame = centering.Step(s, null, 0.05, 30.1, 0.7, 10_001);
        Assert.Equal(30.1, frame.Speed);
    }
}
