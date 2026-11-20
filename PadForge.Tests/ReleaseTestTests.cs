using PadForge.Engine.Steering;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// #588: the Release Test on simulated wheels whose numbers are known.
/// The test sees what a wheel shows PadForge, its axis in sixteen bits a
/// millisecond apart, and must come back with the wheel's inertia,
/// friction, delay, damper and spring.
/// </summary>
public sealed class ReleaseTestTests(ITestOutputHelper output)
{
    const double Mph = 0.44704;
    const double Deg = Math.PI / 180.0;

    static CenteringSettings Settings(double wheelTorque, double range = 900)
        => new(true, SteeringVehicle.Sedan, 100, 100, 100, 100, 0.30, false, 60 * Mph, 1, wheelTorque, range);

    /// <summary>The axis as SDL gives it: sixteen bits.</summary>
    static double Axis(WheelPlant plant) => Math.Round(plant.Axis * 32767.0) / 32767.0;

    /// <summary>A test whose search for the return ratio runs in place,
    /// so the simulated wheel's millisecond clock does not run on while
    /// a worker would compute it.</summary>
    static ReleaseTest New(in CenteringSettings s, WheelCalibration previous = null) => new(s, previous, work => work());

    static ReleaseTest Run(WheelPlant plant, CenteringSettings s, int limitMs = 40000, Action<int> each = null)
    {
        var test = New(s);
        for (int ms = 0; ms < limitMs && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            each?.Invoke(ms);
            var frame = test.Step(Axis(plant), ms);
            plant.Command(frame);
            plant.Advance(0.001);
        }
        return test;
    }

    static void Near(double expected, double actual, double relative, string what)
        => Assert.True(Math.Abs(actual - expected) <= Math.Abs(expected) * relative, $"{what}: {actual} is not within {relative:P0} of {expected}");

    public static TheoryData<string, double, double, double, double, double, double> Wheels => new()
    {
        // name, inertia, friction, delay, wheel torque, damper at full, spring at full
        { "gear", 0.015, 0.15, 0.008, 2.2, 0.30, 12.0 },
        { "belt", 0.012, 0.08, 0.006, 3.9, 0.50, 25.0 },
        { "direct", 0.020, 0.03, 0.004, 8.0, 0.80, 60.0 },
        { "light", 0.006, 0.05, 0.005, 5.0, 0.40, 40.0 },
    };

    /// <summary>The test comes back with each wheel's own numbers:
    /// inertia within 15 percent, friction within 0.03 N·m or 30
    /// percent, delay within 3 ms, the damper's strength at each of its
    /// two coefficients within 25 percent and the spring's at each of
    /// its three within 15. The second run of each wheel has a firmware
    /// where twice the coefficient is not twice the strength, which is
    /// why each coefficient is measured.</summary>
    [Theory]
    [MemberData(nameof(Wheels))]
    public void TheTestMeasuresTheWheel(string name, double inertia, double friction, double delay, double torque, double damper, double spring)
    {
        foreach (var (damperExponent, springExponent) in new[] { (1.0, 1.0), (0.6, 1.7) })
        {
            var plant = new WheelPlant
            {
                Inertia = inertia, Friction = friction, Delay = delay, WheelTorque = torque, DamperFull = damper, SpringFull = spring,
                DamperExponent = damperExponent, SpringExponent = springExponent,
            };
            plant.Angle = 12 * Deg;
            int ms = 0;
            var test = Run(plant, Settings(torque), each: at => ms = at);
            Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
            var m = test.Result;
            Assert.NotNull(m);
            double damperMid = damper * Math.Pow(WheelCalibration.DamperCoefficients[0] / 10000.0, damperExponent);
            double springThird = spring * Math.Pow(0.3333, springExponent), springTwoThirds = spring * Math.Pow(0.6667, springExponent);
            output.WriteLine($"{name} ({damperExponent}, {springExponent}) in {ms} ms: inertia {m.Inertia:F4} ({inertia}), friction {m.Friction:F3} ({friction}), delay {m.Delay * 1000:F1} ms ({delay * 1000}), damper {m.DamperMid:F3} {m.DamperFull:F3} ({damperMid:F3} {damper}), spring {m.SpringThird:F2} {m.SpringTwoThirds:F2} {m.SpringFull:F2} ({springThird:F2} {springTwoThirds:F2} {spring}), ratio {m.ReturnRatio:F1}, sign {m.ForceSign}");
            Near(inertia, m.Inertia, 0.15, "inertia");
            Assert.True(Math.Abs(m.Friction - friction) <= Math.Max(0.03, friction * 0.30), $"friction {m.Friction} against {friction}");
            Assert.True(Math.Abs(m.Delay - delay) <= 0.003, $"delay {m.Delay} against {delay}");
            Near(damperMid, m.DamperMid, 0.25, "damper at its middle coefficient");
            Near(damper, m.DamperFull, 0.25, "damper at full");
            Near(springThird, m.SpringThird, 0.15, "spring at a third");
            Near(springTwoThirds, m.SpringTwoThirds, 0.15, "spring at two thirds");
            Near(spring, m.SpringFull, 0.15, "spring at full");
            Check(name, test, torque, m);
        }
    }

    /// <summary>What every finished test holds, whatever the wheel.</summary>
    void Check(string name, ReleaseTest test, double torque, WheelCalibration m)
    {
        Assert.Equal(1, m.ForceSign);
        Assert.Equal(torque, m.WheelTorque);
        Assert.True(m.IsValid);

        // Both releases were recorded from the release angle, and the wheel was never near its lock.
        Assert.Equal(ReleaseTest.ReleaseMs, test.Before.Count);
        Assert.Equal(ReleaseTest.ReleaseMs, test.After.Count);
        Assert.InRange(Math.Abs(test.Before[0]), test.ReleaseAngle - 25, test.ReleaseAngle + 25);
        Assert.InRange(Math.Abs(test.After[0]), test.ReleaseAngle - 25, test.ReleaseAngle + 25);
        double PastCenter(IReadOnlyList<double> trace)
        {
            double start = trace[0], most = 0;
            foreach (double a in trace) most = Math.Max(most, start > 0 ? -a : a);
            return most;
        }
        output.WriteLine($"{name}: before swung {PastCenter(test.Before):F1} deg past center and ended at {test.Before[^1]:F1}, after {PastCenter(test.After):F1} and {test.After[^1]:F1}");
        Assert.True(Math.Abs(test.After[^1]) < 8.0, "the measured wheel did not come back to center");
        // The promise of the test: with its measurement the wheel returns without swinging past center.
        // The search runs on the wheel as measured, and the release on the wheel itself, so a part of a
        // degree may be left: in the whole degrees the window writes, no more than 1.
        Assert.True(PastCenter(test.After) < 1.5, $"the measured wheel swung {PastCenter(test.After):F1} degrees past center");
    }

    /// <summary>An axis of few bits reads the angle in steps: ten bits
    /// are nine tenths of a degree a count on a 900 degree wheel. The
    /// step's delay is what the stability rule multiplies the PC part's
    /// slope by, so a delay read short would let the wheel swing. The
    /// test reads it at the wheel's own or longer, never shorter, from
    /// wherever inside a count the wheel starts, and the inertia within
    /// a tenth. The fit this replaced read 1 ms on both wheels.</summary>
    [Theory]
    [InlineData(0.015, 0.15, 0.008, 2.2, 0.30, 12.0)]
    [InlineData(0.020, 0.03, 0.004, 8.0, 0.80, 60.0)]
    public void ACoarseAxisNeverReadsTheDelayShort(double inertia, double friction, double delay, double torque, double damper, double spring)
    {
        var random = new Random(5);
        double shortest = double.MaxValue, longest = 0;
        for (int run = 0; run < 12; run++)
        {
            var plant = new WheelPlant
            {
                Inertia = inertia, Friction = friction, Delay = delay, WheelTorque = torque, DamperFull = damper, SpringFull = spring,
                Angle = (random.NextDouble() * 40 - 20) * Deg,
            };
            var test = New(Settings(torque));
            for (int ms = 0; ms < 60000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
            {
                plant.Command(test.Step(Math.Round(plant.Axis * 511.0) / 511.0, ms));
                plant.Advance(0.001);
            }
            Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
            shortest = Math.Min(shortest, test.Result.Delay);
            longest = Math.Max(longest, test.Result.Delay);
            Near(inertia, test.Result.Inertia, 0.10, "inertia");
        }
        output.WriteLine($"delay {shortest * 1000:F1} to {longest * 1000:F1} ms, the wheel's {delay * 1000:F0} and a poll");
        Assert.True(shortest >= delay, $"read {shortest * 1000:F1} ms where the wheel has {delay * 1000:F0}");
        Assert.True(longest <= delay + 0.010, $"read {longest * 1000:F1} ms where the wheel has {delay * 1000:F0}");
    }

    /// <summary>A coarse axis that also flickers by a count at rest, as
    /// a potentiometer's does. The small angles the test reads rest and
    /// first motion by are held to the axis's own counts, so the test
    /// runs, where it used to end saying the wheel would not come to
    /// rest. A run may still end that way and is run again. No run
    /// reads the delay short: few counts leave the step's delay
    /// unknown to several milliseconds, so it is never taken under the
    /// delay an unmeasured wheel gets.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void AFlickeringCoarseAxisIsMeasured_AndNeverReadsTheDelayShort(int bits)
    {
        var random = new Random(9);
        double counts = (1 << (bits - 1)) - 1;
        int done = 0;
        foreach (var (inertia, friction, delay, torque, damper, spring) in new[]
            { (0.015, 0.15, 0.008, 2.2, 0.30, 12.0), (0.012, 0.08, 0.006, 3.9, 0.50, 25.0), (0.020, 0.03, 0.004, 8.0, 0.80, 60.0) })
        {
            for (int run = 0; run < 10; run++)
            {
                var plant = new WheelPlant
                {
                    Inertia = inertia, Friction = friction, Delay = delay, WheelTorque = torque, DamperFull = damper, SpringFull = spring,
                    Angle = (random.NextDouble() * 40 - 20) * Deg,
                };
                var test = New(Settings(torque));
                for (int ms = 0; ms < 60000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
                {
                    plant.Command(test.Step(Math.Round(plant.Axis * counts + random.Next(-1, 2) * 0.5) / counts, ms));
                    plant.Advance(0.001);
                }
                Assert.True(test.Outcome is ReleaseTestOutcome.Done or ReleaseTestOutcome.Moving, $"ended {test.Outcome}");
                if (test.Outcome != ReleaseTestOutcome.Done) continue;
                done++;
                Assert.True(test.Result.Delay >= WheelCentering.UnmeasuredDelay, $"read {test.Result.Delay * 1000:F1} ms");
                Near(inertia, test.Result.Inertia, 0.15, "inertia");
            }
        }
        output.WriteLine($"{bits} bits: {done} of 30 runs measured the wheel");
        Assert.True(done >= 24, $"only {done} of 30 runs measured the wheel");
    }

    /// <summary>A wheel whose motor answers the other way: the test
    /// reads the sign from the first motion, every later push still
    /// goes where it is meant to, and the wheel centers on the measured
    /// sign.</summary>
    [Fact]
    public void AWheelThatAnswersTheOtherWayIsMeasuredWithItsSign()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0, ForceSign = -1 };
        plant.Angle = -8 * Deg;
        var test = Run(plant, Settings(2.2));
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        Assert.Equal(-1, test.Result.ForceSign);
        Near(0.015, test.Result.Inertia, 0.15, "inertia");
        Assert.True(Math.Abs(test.After[^1]) < 8.0);
    }

    /// <summary>A wheel with no damper and no spring effect: the test
    /// says so with zeros and still measures the rest.</summary>
    [Fact]
    public void AWheelWithNoDamperOrSpringReadsZeros()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0, SpringFull = 0 };
        var test = Run(plant, Settings(2.2));
        output.WriteLine($"outcome {test.Outcome}");
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        Assert.Equal(0, test.Result.DamperMid);
        Assert.Equal(0, test.Result.DamperFull);
        Assert.False(test.Result.HasDamper);
        Assert.Equal(0, test.Result.SpringThird);
        Assert.Equal(0, test.Result.SpringTwoThirds);
        Assert.Equal(0, test.Result.SpringFull);
        Near(0.015, test.Result.Inertia, 0.15, "inertia");
        // With no damper to set, the wheel's damping came from the PC, and it still came back.
        Assert.True(Math.Abs(test.After[^1]) < 8.0, $"ended at {test.After[^1]:F1} degrees");
    }

    /// <summary>A spring too weak to hold the wheel at its lowest
    /// coefficient reads as none there, and the stronger coefficients
    /// keep their figures.</summary>
    [Fact]
    public void ASpringThatShowsAtSomeCoefficientsOnlyKeepsThose()
    {
        var plant = new WheelPlant
        {
            Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30,
            // Nothing below half the coefficient, then the full strength at once.
            SpringStrength = c => c < 5000 ? 0 : 12.0,
        };
        var test = Run(plant, Settings(2.2));
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        output.WriteLine($"spring {test.Result.SpringThird:F2} {test.Result.SpringTwoThirds:F2} {test.Result.SpringFull:F2}");
        Assert.Equal(0, test.Result.SpringThird);
        Near(12.0, test.Result.SpringTwoThirds, 0.15, "spring at two thirds");
        Near(12.0, test.Result.SpringFull, 0.15, "spring at full");
    }

    /// <summary>Progress never goes back, and ends at 1.</summary>
    [Fact]
    public void ProgressOnlyGoesForward()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        var test = New(Settings(2.2));
        double before = 0;
        for (int ms = 0; ms < 40000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
            Assert.True(test.Progress >= before, $"progress went from {before} to {test.Progress} at {ms} ms");
            before = test.Progress;
        }
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        Assert.Equal(1.0, test.Progress);
    }

    /// <summary>The test reads the same wheel the same at other
    /// polling rates: four polls a millisecond, and one every four.</summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(4.0)]
    public void TheTestReadsTheWheelAtOtherPollingRates(double pollMs)
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        var test = New(Settings(2.2));
        for (double at = 0; at < 40000 && test.Outcome == ReleaseTestOutcome.Running; at += pollMs)
        {
            plant.Command(test.Step(Axis(plant), (long)at));
            plant.Advance(pollMs / 1000.0);
        }
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        var m = test.Result;
        output.WriteLine($"poll {pollMs} ms: inertia {m.Inertia:F4}, friction {m.Friction:F3}, delay {m.Delay * 1000:F1} ms, damper {m.DamperMid:F3} {m.DamperFull:F3}, spring {m.SpringThird:F2} {m.SpringTwoThirds:F2} {m.SpringFull:F2}");
        Near(0.015, m.Inertia, 0.20, "inertia");
        Near(0.30, m.DamperFull, 0.30, "damper at full");
        Near(12.0, m.SpringFull, 0.20, "spring at full");
        Assert.Equal(ReleaseTest.ReleaseMs, test.Before.Count);
        Assert.Equal(ReleaseTest.ReleaseMs, test.After.Count);
    }

    /// <summary>A second test's first release centers with the first
    /// test's measurement, as the wheel centered before it.</summary>
    [Fact]
    public void ASecondTestStartsFromTheFirstMeasurement()
    {
        WheelPlant Wheel() => new() { Inertia = 0.020, Friction = 0.03, Delay = 0.004, WheelTorque = 8.0, DamperFull = 0.80, SpringFull = 60.0 };
        var first = Run(Wheel(), Settings(8.0));
        Assert.Equal(ReleaseTestOutcome.Done, first.Outcome);
        var plant = Wheel();
        var second = New(Settings(8.0), first.Result);
        for (int ms = 0; ms < 40000 && second.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(second.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.Done, second.Outcome);
        double Past(IReadOnlyList<double> trace)
        {
            double start = trace[0], most = 0;
            foreach (double a in trace) most = Math.Max(most, start > 0 ? -a : a);
            return most;
        }
        output.WriteLine($"first: before {Past(first.Before):F1}, after {Past(first.After):F1}. second: before {Past(second.Before):F1}, after {Past(second.After):F1}");
        Assert.True(Math.Abs(Past(second.Before) - Past(first.After)) < 4.0, "the second test's first release is the first test's last");
    }

    /// <summary>A wheel held in a hand never moves under the ramp, and
    /// the test stops having commanded no more than a third of the
    /// wheel's torque.</summary>
    [Fact]
    public void AHeldWheelStopsTheTest()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 5.0, Delay = 0.008, WheelTorque = 2.2 };
        short most = 0;
        var test = New(Settings(2.2));
        for (int ms = 0; ms < 20000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            var frame = test.Step(Axis(plant), ms);
            most = Math.Max(most, Math.Abs(frame.Level));
            plant.Command(frame);
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.NoMotion, test.Outcome);
        Assert.Null(test.Result);
        Assert.True(most <= 0.34 * 32767, $"commanded {most}");
        Assert.Equal(0, test.Step(Axis(plant), 30000).Level);
    }

    /// <summary>A wheel that starts next to its lock stops the test at
    /// once, and one a hand keeps turning never lets it start.</summary>
    [Fact]
    public void AWheelAtItsLockOrInMotionStopsTheTest()
    {
        var atLock = new WheelPlant { WheelTorque = 2.2 };
        atLock.Angle = 440 * Deg;
        Assert.Equal(ReleaseTestOutcome.NearLock, Run(atLock, Settings(2.2), 2000).Outcome);

        var turning = new WheelPlant { WheelTorque = 2.2, Friction = 0.05 };
        var test = Run(turning, Settings(2.2), 6000, ms => turning.HandTorque = 0.4 * Math.Sin(ms / 150.0));
        Assert.Equal(ReleaseTestOutcome.Moving, test.Outcome);
    }

    /// <summary>Canceling ends the test with an empty frame, whatever
    /// step it was on.</summary>
    [Fact]
    public void CancelingEndsTheTest()
    {
        var plant = new WheelPlant { WheelTorque = 2.2 };
        var test = New(Settings(2.2));
        for (int ms = 0; ms < 1500; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.Running, test.Outcome);
        test.Cancel();
        Assert.Equal(ReleaseTestOutcome.Canceled, test.Outcome);
        var frame = test.Step(Axis(plant), 1500);
        Assert.Equal(0, frame.Level);
        Assert.Equal(0, frame.SpringCoefficient);
        Assert.Null(test.Result);
        Assert.InRange(test.Progress, 0.99, 1.0);
    }

    /// <summary>The whole test stays inside the share of the wheel's
    /// torque it promises, and takes under half a minute.</summary>
    [Fact]
    public void TheTestStaysInsideItsTorqueShare_AndIsQuick()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        var test = New(Settings(2.2));
        short most = 0;
        int ms = 0, pushing = 0;
        for (; ms < 40000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            var frame = test.Step(Axis(plant), ms);
            // The releases run the wheel's own centering, which may use all of its torque.
            if (test.Before.Count == 0 || (test.Before.Count == ReleaseTest.ReleaseMs && test.After.Count == 0))
            {
                most = Math.Max(most, Math.Abs(frame.Level));
                pushing++;
            }
            plant.Command(frame);
            plant.Advance(0.001);
        }
        output.WriteLine($"done in {ms} ms, most level outside the releases {most}");
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        Assert.True(most <= ReleaseTest.MostShare * 32767 + 1, $"commanded {most}");
        Assert.True(ms < 30000);
        Assert.True(pushing > 0);
    }

    /// <summary>The search for the return ratio runs a simulated release
    /// of two and a half seconds for each ratio it tries, so it runs off
    /// the polling thread: the poll that reaches it hands it to a worker
    /// and returns, the polls after it send nothing and wait, and the
    /// first poll after the answer goes on to the releases, with the
    /// ratio a search in place would choose (audit C6-2).</summary>
    [Fact]
    public async Task TheSearchRunsOnAWorker_AndThePollsWaitForIt()
    {
        WheelPlant Wheel() => new() { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        var plant = Wheel();
        Action posted = null;
        int posts = 0;
        var test = new ReleaseTest(Settings(2.2), null, work => { posts++; posted = work; });
        int ms = 0;
        for (; ms < 40000 && posted == null && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.True(posted != null, $"the search was not handed to a worker: the test ended {test.Outcome}");
        Assert.Equal(1, posts);

        // The answer has not come: every poll returns with nothing sent, and no release starts.
        for (int i = 0; i < 500; i++, ms++)
        {
            var frame = test.Step(Axis(plant), ms);
            Assert.Equal(0, frame.Level);
            Assert.Equal(ReleaseTestOutcome.Running, test.Outcome);
            Assert.Empty(test.Before);
            plant.Command(frame);
            plant.Advance(0.001);
        }
        Assert.Equal(1, posts);

        await Task.Run(posted).WaitAsync(TimeSpan.FromSeconds(60));
        for (; ms < 80000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
        Assert.Equal(1, posts);
        var inPlace = Run(Wheel(), Settings(2.2));
        Assert.Equal(inPlace.Result.ReturnRatio, test.Result.ReturnRatio);
        Assert.Equal(inPlace.Result.Inertia, test.Result.Inertia);
    }

    /// <summary>With no schedule given, the search runs on the thread
    /// pool: the polls go on with the clock held still until its answer
    /// moves the test to the releases.</summary>
    [Fact]
    public void TheSearchRunsOnTheThreadPoolByDefault()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        var test = new ReleaseTest(Settings(2.2));
        int ms = 0;
        for (; ms < 40000 && !test.Searching && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.True(test.Searching, $"the test never searched: it ended {test.Outcome}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (test.Searching && clock.ElapsedMilliseconds < 60000)
        {
            Assert.Equal(0, test.Step(Axis(plant), ms).Level);
            Thread.Sleep(1);
        }
        Assert.False(test.Searching, "the search did not finish in a minute");
        for (; ms < 80000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
    }

    /// <summary>A test canceled while its search runs ends at once, and
    /// the answer that comes after is let go.</summary>
    [Fact]
    public void ATestCanceledDuringItsSearchEnds_AndTheLateAnswerIsDropped()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0 };
        Action posted = null;
        var test = new ReleaseTest(Settings(2.2), null, work => posted = work);
        for (int ms = 0; ms < 40000 && posted == null && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.NotNull(posted);
        test.Cancel();
        posted();
        var frame = test.Step(Axis(plant), 50000);
        Assert.Equal(ReleaseTestOutcome.Canceled, test.Outcome);
        Assert.Equal(0, frame.Level);
        Assert.Null(test.Result);
        Assert.Empty(test.Before);
    }

    /// <summary>The first release of a wheel that answers the other way,
    /// with no earlier measurement, centers with the sign alone: one
    /// measurement made once, so the poll makes nothing and the
    /// composer's caches, which hold a measurement by reference, keep
    /// their work (audit C6-3).</summary>
    [Fact]
    public void TheFirstReleaseOfAReversedWheelMakesItsMeasurementOnce()
    {
        var plant = new WheelPlant { Inertia = 0.015, Friction = 0.15, Delay = 0.008, WheelTorque = 2.2, DamperFull = 0.30, SpringFull = 12.0, ForceSign = -1 };
        plant.Angle = -8 * Deg;
        var test = New(Settings(2.2));
        int ms = 0;
        for (; ms < 40000 && test.Before.Count < 100 && test.Outcome == ReleaseTestOutcome.Running; ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Equal(ReleaseTestOutcome.Running, test.Outcome);
        var first = test.BeforeMeasurement();
        Assert.NotNull(first);
        Assert.Equal(-1, first.ForceSign);
        Assert.Same(first, test.BeforeMeasurement());
        for (int i = 0; i < 50; i++, ms++)
        {
            plant.Command(test.Step(Axis(plant), ms));
            plant.Advance(0.001);
        }
        Assert.Same(first, test.BeforeMeasurement());
    }

    /// <summary>On an axis too coarse to read the step's delay, the
    /// measurement keeps no delay under the one an unmeasured wheel gets,
    /// for the stability rule. The damper is still read against the
    /// wheel's own lag: a wheel with a 3 ms loop on a twelve-bit axis
    /// reads its damper within a quarter at full, and runs within a
    /// quarter of its damping at the middle coefficient, a weak damper
    /// that takes little off the step among them. The middle reading of
    /// the weakest may come back 0 (within a twentieth of the free
    /// distance on 68 counts), and the wheel then runs on the line
    /// through its full reading (WheelCalibration.DampingAt), so the
    /// damping it runs on is what is held (audit C6-8).</summary>
    [Theory]
    [InlineData(0.015, 0.15, 2.2, 0.30)]
    [InlineData(0.015, 0.15, 2.2, 0.06)]
    [InlineData(0.015, 0.15, 2.2, 0.03)]
    [InlineData(0.020, 0.03, 8.0, 0.15)]
    [InlineData(0.020, 0.03, 8.0, 0.08)]
    [InlineData(0.006, 0.05, 5.0, 0.04)]
    [InlineData(0.006, 0.05, 5.0, 0.02)]
    public void ACoarseAxisReadsTheDamperAgainstTheWheelsOwnLag(double inertia, double friction, double torque, double damperFull)
    {
        var random = new Random(3);
        double damperMid = damperFull * WheelCalibration.DamperCoefficients[0] / 10000.0;
        var misses = new List<string>();
        for (int run = 0; run < 8; run++)
        {
            var plant = new WheelPlant
            {
                Inertia = inertia, Friction = friction, Delay = 0.003, WheelTorque = torque, DamperFull = damperFull, SpringFull = 12.0,
                Angle = (random.NextDouble() * 40 - 20) * Deg,
            };
            var test = New(Settings(torque));
            for (int ms = 0; ms < 60000 && test.Outcome == ReleaseTestOutcome.Running; ms++)
            {
                plant.Command(test.Step(Math.Round(plant.Axis * 2047.0) / 2047.0, ms));
                plant.Advance(0.001);
            }
            Assert.Equal(ReleaseTestOutcome.Done, test.Outcome);
            var m = test.Result;
            output.WriteLine($"run {run}: delay {m.Delay * 1000:F1} ms, damper {m.DamperMid:F4} {m.DamperFull:F4} ({damperMid:F4} {damperFull}), spring {m.SpringThird:F2} {m.SpringTwoThirds:F2} {m.SpringFull:F2}, inertia {m.Inertia:F4}");
            Assert.True(m.Delay >= WheelCentering.UnmeasuredDelay, $"read {m.Delay * 1000:F1} ms");
            double runsMid = m.DampingAt(WheelCalibration.DamperCoefficients[0]);
            if (Math.Abs(runsMid - damperMid) > 0.25 * damperMid) misses.Add($"run {run}: damping at the middle coefficient {runsMid:F4} against {damperMid:F4}");
            if (Math.Abs(m.DamperFull - damperFull) > 0.25 * damperFull) misses.Add($"run {run}: damper at full {m.DamperFull:F4} against {damperFull}");
        }
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }
}
