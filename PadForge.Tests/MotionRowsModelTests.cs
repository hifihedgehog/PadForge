using System;
using System.Numerics;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// The motion the Motion Pitch, Yaw and Roll rows drive (#475), checked
/// against closed-form angles in SDL's sensor frame: +X right, +Y up through
/// the top face, +Z toward the player, positive pitch raising the nose.
/// </summary>
public class MotionRowsModelTests
{
    private const float Deg = MathF.PI / 180f;

    private static MotionRowsModel.Axis Speed(float value, float dps = 360f, float min = 0f, float dz = 0f)
        => new() { Value = value, SpeedDps = dps, MinSpeedDps = min, Deadzone = dz, AngleDeg = 85f };

    private static MotionRowsModel.Axis Angle(float value, float deg = 85f, float dz = 0f)
        => new() { Value = value, Angle = true, AngleDeg = deg, Deadzone = dz, SpeedDps = 360f };

    private static readonly MotionRowsModel.Axis Rest = Speed(0f);

    /// <summary>Runs frames of <paramref name="frameMs"/> for
    /// <paramref name="seconds"/>, starting the clock first, and returns the
    /// gyro integrated over them, degrees.</summary>
    private static Vector3 Run(MotionRowsModel m, MotionRowsModel.Axis p, MotionRowsModel.Axis y,
        MotionRowsModel.Axis r, float seconds, float frameMs = 1f, long startUs = 1_000_000)
    {
        m.Step(Rest, Rest, Rest, startUs);
        var sum = Vector3.Zero;
        long frameUs = (long)(frameMs * 1000f);
        int frames = (int)MathF.Round(seconds * 1000f / frameMs);
        for (int i = 1; i <= frames; i++)
        {
            m.Step(p, y, r, startUs + i * frameUs);
            sum += m.GyroDps * (frameMs / 1000f);
        }
        return sum;
    }

    private static Vector3 Gravity(MotionRowsModel m)
        => MotionRowsModel.ToBody(m.Orientation, Vector3.UnitY);

    private static void Near(Vector3 expected, Vector3 actual, float tol)
    {
        Assert.InRange(actual.X, expected.X - tol, expected.X + tol);
        Assert.InRange(actual.Y, expected.Y - tol, expected.Y + tol);
        Assert.InRange(actual.Z, expected.Z - tol, expected.Z + tol);
    }

    // ── Directions ──

    [Fact]
    public void SpeedPushedUpRaisesTheNose()
    {
        var m = new MotionRowsModel();
        var turned = Run(m, Speed(-1f, 30f), Rest, Rest, 2f);
        Assert.Equal(60f, turned.X, 0.5f);
        // Nose up 60°: world up reads toward the far end, -Z.
        Near(new Vector3(0f, MathF.Cos(60f * Deg), -MathF.Sin(60f * Deg)), Gravity(m), 0.01f);
        var nose = Vector3.Transform(-Vector3.UnitZ, m.Orientation);
        Assert.True(nose.Y > 0.8f, $"nose should point up, got {nose}");
    }

    [Fact]
    public void SpeedPushedRightTurnsRightAndRollsRight()
    {
        var m = new MotionRowsModel();
        var turned = Run(m, Rest, Speed(1f, 90f), Rest, 1f);
        Assert.Equal(-90f, turned.Y, 0.5f);   // negative yaw is a right turn
        var nose = Vector3.Transform(-Vector3.UnitZ, m.Orientation);
        Assert.True(nose.X > 0.99f, $"nose should point right, got {nose}");

        var r = new MotionRowsModel();
        turned = Run(r, Rest, Rest, Speed(1f, 30f), 1f);
        Assert.Equal(-30f, turned.Z, 0.5f);
        var right = Vector3.Transform(Vector3.UnitX, r.Orientation);
        Assert.True(right.Y < -0.49f, $"the right side should drop, got {right}");
    }

    [Fact]
    public void AnglePushedUpTipsTheControllerForward()
    {
        var m = new MotionRowsModel();
        Run(m, Angle(-1f, 30f), Rest, Rest, 1f);
        var nose = Vector3.Transform(-Vector3.UnitZ, m.Orientation);
        Assert.True(nose.Y < -0.49f, $"Angle pushed up should tip the nose down, got {nose}");
        Near(new Vector3(0f, MathF.Cos(30f * Deg), MathF.Sin(30f * Deg)), Gravity(m), 0.001f);
    }

    [Fact]
    public void AnglePushedRightLowersTheRightSide()
    {
        var m = new MotionRowsModel();
        Run(m, Rest, Rest, Angle(1f, 20f), 1f);
        var right = Vector3.Transform(Vector3.UnitX, m.Orientation);
        Assert.Equal(-MathF.Sin(20f * Deg), right.Y, 0.001f);
    }

    [Fact]
    public void YawHasNoAngleResponse()
    {
        var m = new MotionRowsModel();
        var yaw = Angle(1f, 30f);
        var turned = Run(m, Rest, yaw, Rest, 1f);
        Assert.Equal(-360f, turned.Y, 1f);   // read as Speed, 360°/s
    }

    // ── Angle ──

    [Fact]
    public void AnAngleLeanSettlesOnItsTargetAndLevelsOnRelease()
    {
        var m = new MotionRowsModel();
        var turned = Run(m, Angle(1f, 85f), Rest, Rest, 0.5f);
        Assert.Equal(85f, turned.X, 0.5f);       // the gyro integrates to the lean
        Assert.Equal(85f, PitchOf(m), 0.01f);
        turned = Run(m, Angle(0f), Rest, Rest, 0.5f, startUs: 10_000_000);
        Assert.Equal(-85f, turned.X, 0.5f);
        Assert.Equal(0f, PitchOf(m), 0.01f);
    }

    private static float PitchOf(MotionRowsModel m)
    {
        var nose = Vector3.Transform(-Vector3.UnitZ, m.Orientation);
        return MathF.Asin(Math.Clamp(nose.Y, -1f, 1f)) / Deg;
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(16f)]
    [InlineData(75f)]
    [InlineData(100f)]
    [InlineData(249f)]
    public void AStalledFrameNeitherSwingsTheLeanPastItsTargetNorOutrunsTheCap(float stallMs)
    {
        // Dolphin's step at a variable dt swung a 0→85° lean far past its
        // target after a 75 to 100 ms stall. Fixed 5 ms slices keep it on.
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        long t = 0;
        float maxPitch = 0f, maxRate = 0f;
        for (int i = 0; i < 400; i++)
        {
            t += i == 30 ? (long)(stallMs * 1000f) : 1000;
            m.Step(Angle(1f, 85f), Rest, Rest, t);
            maxPitch = MathF.Max(maxPitch, PitchOf(m));
            maxRate = MathF.Max(maxRate, MathF.Abs(m.GyroDps.X));
        }
        Assert.InRange(maxPitch, 84.9f, 85.01f);
        Assert.InRange(maxRate, 0f, MotionRowsModel.MaxRateDps + 1f);
        Assert.Equal(85f, PitchOf(m), 0.01f);
    }

    [Theory]
    [InlineData(25, 33f)]
    [InlineData(30, 50f)]
    [InlineData(25, 100f)]
    [InlineData(5, 249f)]
    public void AStalledFrameNeverTurnsASmallLeanBackward(int stallAt, float stallMs)
    {
        // A 10° lean caught mid-approach by a stall: steps longer than a few
        // milliseconds swing it up to 50° the wrong way before it settles.
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        long t = 0;
        float last = 0f;
        for (int i = 0; i < 400; i++)
        {
            t += i == stallAt ? (long)(stallMs * 1000f) : 1000;
            m.Step(Angle(1f, 10f), Rest, Rest, t);
            float pitch = PitchOf(m);
            Assert.True(pitch >= last - 0.001f, $"frame {i}: the lean moved back from {last} to {pitch}");
            Assert.InRange(pitch, 0f, 10.01f);
            last = pitch;
        }
        Assert.Equal(10f, PitchOf(m), 0.01f);
    }

    [Theory]
    [InlineData(200f, 90f)]
    [InlineData(-30f, 0f)]
    [InlineData(float.NaN, 0f)]
    public void ALeanAngleOutsideItsRangeReadsAsTheNearestEnd(float setting, float expected)
    {
        var m = new MotionRowsModel();
        Run(m, Angle(1f, setting), Rest, Rest, 0.5f);
        Assert.Equal(expected, PitchOf(m), 0.05f);

        var r = new MotionRowsModel();
        Run(r, Rest, Rest, Angle(1f, setting), 0.5f);
        var right = Vector3.Transform(Vector3.UnitX, r.Orientation);
        Assert.Equal(-MathF.Sin(expected * Deg), right.Y, 0.001f);
    }

    [Fact]
    public void AFullFlipStaysUnderTheCap()
    {
        var m = new MotionRowsModel();
        Run(m, Angle(-1f, 90f), Rest, Rest, 0.5f);
        float peak = 0f;
        long t = 10_000_000;
        m.Step(Angle(-1f, 90f), Rest, Rest, t);
        for (int i = 1; i <= 500; i++)
        {
            m.Step(Angle(1f, 90f), Rest, Rest, t + i * 1000);
            peak = MathF.Max(peak, MathF.Abs(m.GyroDps.X));
        }
        Assert.InRange(peak, 1500f, MotionRowsModel.MaxRateDps + 1f);
        Assert.Equal(90f, PitchOf(m), 0.05f);
    }

    // ── Composition ──

    [Fact]
    public void ACombinedLeanPitchesThenRolls()
    {
        // Pitch is the outer lean axis, Dolphin's order: body to world is
        // pitch(θ) · roll(φ), so gravity reads (cos θ sin φ, cos θ cos φ, -sin θ).
        var m = new MotionRowsModel();
        Run(m, Angle(1f, 30f), Rest, Angle(1f, 45f), 0.5f);
        float th = 30f * Deg, ph = -45f * Deg;
        Near(new Vector3(MathF.Cos(th) * MathF.Sin(ph), MathF.Cos(th) * MathF.Cos(ph), -MathF.Sin(th)), Gravity(m), 0.001f);
    }

    [Fact]
    public void ASpeedTurnHappensInTheControllersOwnFrame()
    {
        // Turned right 90°, then nosed up 30°: the nose points right and up,
        // the local rotation GamepadMotionHelpers integrates.
        var m = new MotionRowsModel();
        Run(m, Rest, Speed(1f), Rest, 0.25f);
        Run(m, Speed(-1f, 300f), Rest, Rest, 0.1f, startUs: 10_000_000);
        var nose = Vector3.Transform(-Vector3.UnitZ, m.Orientation);
        Near(new Vector3(MathF.Cos(30f * Deg), MathF.Sin(30f * Deg), 0f), nose, 0.002f);
    }

    [Fact]
    public void ALeanPushedOnPastItsLandingKeepsItsSpeed()
    {
        // Dolphin lands the lean with the speed of its last step (Dynamics.cpp
        // ApproachAngleWithAccel), so a target that moves on the next frame
        // continues from that speed instead of from rest.
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        long t = 0;
        int landed = -1;
        for (int i = 0; i < 400 && landed < 0; i++)
        {
            t += 1000;
            m.Step(Angle(1f, 30f), Rest, Rest, t);
            if (MathF.Abs(PitchOf(m) - 30f) < 0.0001f) landed = i;
        }
        Assert.True(landed > 0, "the lean never landed");
        t += 1000;
        m.Step(Angle(1f, 60f), Rest, Rest, t);
        Assert.True(m.GyroDps.X > 60f, $"the lean restarted from rest: {m.GyroDps.X}°/s");
    }

    [Fact]
    public void AHeldLeanStaysAgainstGravityWhileSpeedTurns()
    {
        // 30° nose-up lean held (Angle pushed down), 90°/s of yaw for a second.
        var m = new MotionRowsModel();
        Run(m, Angle(1f, 30f), Rest, Rest, 0.5f);
        Run(m, Angle(1f, 30f), Speed(1f, 90f), Rest, 1f, startUs: 10_000_000);
        Near(new Vector3(0f, MathF.Cos(30f * Deg), -MathF.Sin(30f * Deg)), Gravity(m), 0.002f);
    }

    [Fact]
    public void TheGyroIntegratesBackToTheOrientationOnACombinedTilt()
    {
        var m = new MotionRowsModel();
        var q = Quaternion.Identity;
        m.Step(Rest, Rest, Rest, 0);
        for (int i = 1; i <= 600; i++)
        {
            var p = i < 300 ? Angle(1f, 60f) : Angle(-0.5f, 60f);
            var r = i < 300 ? Angle(1f, 60f) : Angle(0.25f, 60f);
            m.Step(p, Rest, r, i * 1000);
            var w = m.GyroDps * Deg * 0.001f;
            if (w.Length() > 0f)
                q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(Vector3.Normalize(w), w.Length()));
        }
        Near(MotionRowsModel.ToBody(m.Orientation, Vector3.UnitY),
             MotionRowsModel.ToBody(q, Vector3.UnitY), 0.002f);
    }

    [Theory]
    [InlineData(16f)]
    [InlineData(33f)]
    public void TheGyroIntegratesToTheLeanWhenAFrameSpansSeveralSlices(float frameMs)
    {
        // The rate is the mean of the frame's slices. Reporting one slice
        // for all of them misses about a degree of an 85° lean.
        var m = new MotionRowsModel();
        var turned = Run(m, Angle(1f, 85f), Rest, Rest, 0.5f, frameMs);
        Assert.Equal(85f, PitchOf(m), 0.01f);
        Assert.Equal(85f, turned.X, 0.05f);
    }

    [Fact]
    public void LevelReturnsTheSpeedTurnWithoutReportingIt()
    {
        var m = new MotionRowsModel();
        Run(m, Speed(-1f, 90f), Rest, Rest, 0.5f);
        Assert.NotEqual(Quaternion.Identity, m.Orientation);
        m.Level();
        Assert.Equal(Quaternion.Identity, m.Orientation);
        m.Step(Rest, Rest, Rest, 1_000_000 + 501_000);
        Assert.Equal(Vector3.Zero, m.GyroDps);
    }

    [Fact]
    public void LevelKeepsAHeldLean()
    {
        var m = new MotionRowsModel();
        Run(m, Angle(1f, 40f), Speed(1f, 90f), Rest, 0.5f);
        m.Level();
        Assert.Equal(40f, PitchOf(m), 0.05f);
    }

    [Fact]
    public void AGapRestartsTheClockWithoutTurning()
    {
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        m.Step(Rest, Speed(1f, 100f), Rest, 300_000);
        Assert.Equal(Vector3.Zero, m.GyroDps);
        Assert.Equal(Quaternion.Identity, m.Orientation);
        m.Step(Rest, Speed(1f, 100f), Rest, 301_000);
        Assert.Equal(-100f, m.GyroDps.Y, 0.5f);
    }

    [Fact]
    public void RestartClockSkipsTheNextGap()
    {
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        m.RestartClock();
        m.Step(Rest, Speed(1f, 100f), Rest, 100_000);
        Assert.Equal(Quaternion.Identity, m.Orientation);
    }

    [Fact]
    public void ResetReturnsToRest()
    {
        var m = new MotionRowsModel();
        Run(m, Angle(1f, 40f), Speed(1f, 90f), Rest, 0.5f);
        m.Reset();
        Assert.Equal(Quaternion.Identity, m.Orientation);
        Assert.Equal(Vector3.Zero, m.GyroDps);
    }

    // ── Shaping ──

    [Theory]
    [InlineData(0.2f, 0.2f, 0f)]
    [InlineData(-0.6f, 0.2f, -0.5f)]
    [InlineData(1f, 0.2f, 1f)]
    [InlineData(0.5f, 0f, 0.5f)]
    [InlineData(float.NaN, 0f, 0f)]
    public void TheDeadzoneRescalesFromItsEdge(float value, float dz, float expected)
        => Assert.Equal(expected, MotionRowsModel.Shape(value, dz), 0.0001f);

    [Fact]
    public void MinimumSpeedStartsTheTurnPastTheDeadzone()
    {
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        m.Step(Rest, Speed(0.6f, 300f, min: 20f, dz: 0.2f), Rest, 10_000);
        // Shaped 0.5: 20 + (300 - 20) * 0.5 = 160°/s, a right turn.
        Assert.Equal(-160f, m.GyroDps.Y, 0.5f);
        m.Step(Rest, Speed(0.21f, 300f, min: 20f, dz: 0.2f), Rest, 20_000);
        Assert.InRange(-m.GyroDps.Y, 20f, 25f);
        m.Step(Rest, Speed(0.1f, 300f, min: 20f, dz: 0.2f), Rest, 30_000);
        Assert.Equal(0f, m.GyroDps.Y, 0.001f);
    }

    [Fact]
    public void ANonFiniteSettingReadsAsZero()
    {
        var m = new MotionRowsModel();
        m.Step(Rest, Rest, Rest, 0);
        m.Step(Rest, Speed(1f, float.NaN), Rest, 10_000);
        Assert.Equal(Vector3.Zero, m.GyroDps);
        m.Step(Rest, Speed(1f, float.PositiveInfinity), Rest, 20_000);
        Assert.True(float.IsFinite(m.GyroDps.Y));
    }
}
