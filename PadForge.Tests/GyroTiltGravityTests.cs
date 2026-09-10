using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests;

public class GyroTiltGravityTests
{
    [Theory]
    [InlineData("roll-1ms")]
    [InlineData("roll-4ms")]
    [InlineData("mixed-8ms")]
    [InlineData("translation-4ms")]
    [InlineData("missing-accel-4ms")]
    public void GravityMatchesTheUnmodifiedReference(string scenario)
    {
        // Produced by GamepadMotionHelpers 39b578aa, the exact dependency
        // pinned by JoyShockMapper bb697844. Covers all three gyro axes,
        // changing acceleration, several cadences, and missing acceleration.
        var estimator = new GyroTiltGravityEstimator();
        int samples = 0;
        foreach (string row in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "GyroTiltReference.csv")))
        {
            string[] fields = row.Split(',');
            if (fields[0] != scenario) continue;
            float[] values = fields.Skip(1).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var gyro = new Vector3(values[1], values[2], values[3]);
            var accel = new Vector3(values[4], values[5], values[6]);
            var expected = new Vector3(values[7], values[8], values[9]);
            Assert.True(samples == 0 ? estimator.Seed(accel) : estimator.Update(gyro, accel, values[0]));
            float error = Vector3.Distance(expected, estimator.Gravity);
            Assert.True(error < 0.002f, $"{scenario} sample {samples}: error={error}, expected={expected}, actual={estimator.Gravity}");
            samples++;
        }
        Assert.True(samples >= 200, "The reference scenario must not be empty or truncated.");
    }

    [Fact]
    public void InvalidSamplesCannotPublishNaNsAndTheNextRealSampleReseeds()
    {
        var estimator = new GyroTiltGravityEstimator();
        Assert.False(estimator.Seed(Vector3.Zero));
        Assert.False(estimator.Seed(new Vector3(0, 1, 0)));
        Assert.Equal(Vector3.Zero, estimator.Gravity);
        var rest = new Vector3(0, 9.81f, 0);
        Assert.True(estimator.Seed(rest));
        Assert.False(estimator.Update(new Vector3(float.NaN, 0, 0), rest, .001f));
        Assert.False(estimator.HasValue);
        Assert.Equal(Vector3.Zero, estimator.Gravity);
        Assert.True(estimator.Update(Vector3.Zero, rest, .001f));
        Assert.InRange(Vector3.Distance(rest, estimator.Gravity), 0, .00001f);
        Assert.False(estimator.Update(Vector3.Zero, new Vector3(float.PositiveInfinity), .001f));
        Assert.Equal(Vector3.Zero, estimator.Gravity);
    }

    [Fact]
    public void RotationAroundGravityDoesNotInventHeldYawSteering()
    {
        var estimator = new GyroTiltGravityEstimator();
        var rest = new Vector3(0, 9.81f, 0);
        estimator.Seed(rest);
        for (int i = 0; i < 1000; i++)
            estimator.Update(new Vector3(0, MathF.PI / 2, 0), rest, .001f);
        Assert.InRange(Vector3.Distance(rest, estimator.Gravity), 0, .001f);
    }

    [Fact]
    public void ZeroElapsedTimeKeepsTheLastValidEstimate()
    {
        var estimator = new GyroTiltGravityEstimator();
        var rest = new Vector3(0, 9.81f, 0);
        estimator.Seed(rest);
        Assert.True(estimator.Update(new Vector3(1, 2, 3), new Vector3(3, 8, 2), 0));
        Assert.True(estimator.HasValue);
        Assert.InRange(Vector3.Distance(rest, estimator.Gravity), 0, .00001f);
    }
}
