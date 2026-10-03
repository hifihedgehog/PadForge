using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// Simulated pitch and roll for the DualShock 3 (#472, #474): the
/// accelerometer-derived rate, its gates, where it enters the gyro read,
/// its per-(device, slot) runtime, its settings, the yaw calibration bound
/// a press of Calibrate allows, and the direct path's yaw sign.
/// </summary>
[Collection("SettingsManagerStatics")]
public class GyroSimulationTests
{
    private const float G = 9.80665f;
    private const float Deg = MathF.PI / 180f;
    private const float CountG = G / 113f;
    private static readonly Vector3 FaceUp = new(0, G, 0);

    /// <summary>World up seen in the pad's frame after the pad turns by
    /// <paramref name="angle"/> about its own <paramref name="axis"/>: the
    /// inverse rotation of the resting reading.</summary>
    private static Vector3 Turned(Vector3 rest, Vector3 axis, float angle)
        => Vector3.Transform(rest, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), -angle));

    private static Vector3 Quantized(Vector3 a)
        => new(MathF.Round(a.X / CountG) * CountG, MathF.Round(a.Y / CountG) * CountG,
               MathF.Round(a.Z / CountG) * CountG);

    /// <summary>Polls the estimator at 1 kHz for <paramref name="seconds"/>.
    /// With <paramref name="deviceHz"/> the reading is sampled at that rate
    /// and held between reports, as a DS3's feature report repeats.</summary>
    private static List<Vector3> Drive(AccelRateEstimator est, Func<float, Vector3> accelAt, float seconds,
        float tau = 0.1f, float deviceHz = 0, bool quantize = false, float start = 0)
    {
        var rates = new List<Vector3>();
        const float dt = 0.001f;
        int polls = (int)MathF.Round(seconds / dt);
        for (int i = 1; i <= polls; i++)
        {
            float t = start + i * dt;
            float sampleT = deviceHz > 0 ? MathF.Floor(t * deviceHz) / deviceHz : t;
            var a = accelAt(sampleT);
            if (quantize) a = Quantized(a);
            est.Update(a, dt, tau);
            rates.Add(est.Rate);
        }
        return rates;
    }

    private static AccelRateEstimator Seeded(Vector3 rest)
    {
        var est = new AccelRateEstimator();
        est.Update(rest, 0f, 0.1f);
        Assert.True(est.HasValue);
        return est;
    }

    // ── the estimator: signs ──

    /// <summary>SDL counts counter-clockwise rotation seen from the positive
    /// axis as positive. Turning about +X raises the far edge, turning about
    /// +Z raises the right grip, and both read positive, face up and face
    /// down alike.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void RaisingTheFarEdgeOrTheRightGripReadsPositive(float face)
    {
        var rest = FaceUp * face;
        float w = 60 * Deg;
        var pitch = Drive(Seeded(rest), t => Turned(rest, Vector3.UnitX, w * t), 1f);
        Assert.InRange(pitch[^1].X, w * 0.98f, w * 1.02f);
        Assert.InRange(MathF.Abs(pitch[^1].Z), 0, w * 0.01f);
        var roll = Drive(Seeded(rest), t => Turned(rest, Vector3.UnitZ, w * t), 1f);
        Assert.InRange(roll[^1].Z, w * 0.98f, w * 1.02f);
        Assert.InRange(MathF.Abs(roll[^1].X), 0, w * 0.01f);
    }

    // ── the estimator: geometry ──

    /// <summary>A rotation about any horizontal axis reads in full, however
    /// far the pad is pitched along that same axis.</summary>
    [Fact]
    public void AboutAHorizontalAxisTheRateReadsInFull()
    {
        var pitched = Turned(FaceUp, Vector3.UnitX, 45 * Deg);
        float w = 40 * Deg;
        var rates = Drive(Seeded(pitched), t => Turned(pitched, Vector3.UnitX, w * t), 1f);
        Assert.InRange(rates[^1].X, w * 0.98f, w * 1.02f);
    }

    /// <summary>The part of a rotation along gravity is invisible. Rolled
    /// 30°, a pitch about the pad's own axis reads cos² 30° = 0.75.</summary>
    [Fact]
    public void AcrossATiltThePadsOwnAxisLosesItsPartAlongGravity()
    {
        var rolled = Turned(FaceUp, Vector3.UnitZ, 30 * Deg);
        float w = 40 * Deg;
        var rates = Drive(Seeded(rolled), t => Turned(rolled, Vector3.UnitX, w * t), 1f);
        Assert.InRange(rates[^1].X / w, 0.73f, 0.77f);
    }

    /// <summary>Turning about gravity leaves the reading where it was, so
    /// the input is constant and the rate reads zero.</summary>
    [Fact]
    public void TurningAboutGravityIsAConstantReadingAndReadsZero()
    {
        float w = 90 * Deg;
        var rates = Drive(Seeded(FaceUp), t => Turned(FaceUp, Vector3.UnitY, w * t), 1f);
        Assert.All(rates, r => Assert.True(r.Length() < 1e-4f));
    }

    /// <summary>Tilted back 30°, a turn in the pad's own plane shows on the
    /// simulated axes at sin·cos of its rate, 0.43, the crosstalk the issue
    /// states. It starts on roll and swings toward pitch as the tilt's
    /// direction turns with the pad, so the size is what holds.</summary>
    [Fact]
    public void AnInPlaneTurnOnATiltedPadLeaksOntoTheSimulatedAxes()
    {
        var back = Turned(FaceUp, Vector3.UnitX, 30 * Deg);
        float w = 60 * Deg;
        var rates = Drive(Seeded(back), t => Turned(back, Vector3.UnitY, w * t), 1f);
        float leak = MathF.Sqrt(rates[^1].X * rates[^1].X + rates[^1].Z * rates[^1].Z);
        Assert.InRange(leak / w, 0.41f, 0.45f);
        var early = Drive(Seeded(back), t => Turned(back, Vector3.UnitY, w * t), 0.6f, tau: 0.02f);
        Assert.True(early[100].Z > 0.35f * w);
    }

    // ── the estimator: sampling ──

    [Fact]
    public void AStillPadSettlesToZero()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t), 0.5f);
        var end = Turned(FaceUp, Vector3.UnitX, w * 0.5f);
        var rates = Drive(est, _ => end, 1f);
        Assert.True(rates[^1].Length() < 0.01f * Deg);
    }

    /// <summary>A DS3 reports about 100 times a second and the poll runs at
    /// 1 kHz. Smoothing every poll keeps the mean rate true and no poll
    /// spikes, where skipping repeats would hold each rate over the next
    /// interval.</summary>
    [Theory]
    [InlineData(60f, 0.1f)]
    [InlineData(60f, 0.02f)]
    [InlineData(5f, 0.1f)]
    [InlineData(5f, 0.02f)]
    public void PollingFasterThanThePadReportsKeepsTheMeanRate(float degPerSec, float tau)
    {
        float w = degPerSec * Deg;
        var est = Seeded(Quantized(FaceUp));
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t), 3f, tau, deviceHz: 100, quantize: true);
        var settled = rates.Skip(1000).Select(r => r.X).ToList();
        float mean = settled.Average();
        Assert.InRange(mean / w, 0.96f, 1.04f);
        // Smoothing every poll leaves a ripple from the report rate and the
        // accelerometer's count steps: peaks of 1.11 times the rate at 100 ms
        // and 1.62 at 20 ms in a model of this run. A per-poll difference
        // would spike to ten times the rate.
        if (degPerSec >= 60f) Assert.True(settled.Max() < w * (tau >= 0.1f ? 1.2f : 2.0f));
    }

    /// <summary>A DS3 lying still flickers one count. At the default 100 ms
    /// that reads about 2.5 deg/s RMS, under the Gyro tab's 3 deg/s
    /// deadzone, and at 20 ms it reads several times that.</summary>
    [Fact]
    public void AOneCountFlickerAtRestStaysSmall()
    {
        var rng = new Random(472);
        var est = Seeded(Quantized(FaceUp));
        var flicker = new Vector3[400];
        for (int i = 0; i < flicker.Length; i++)
            flicker[i] = Quantized(FaceUp) + new Vector3(rng.Next(2) * CountG, 0, 0);
        var rates = Drive(est, t => flicker[Math.Min(flicker.Length - 1, (int)(t * 100))], 3.9f, 0.1f, deviceHz: 100);
        double rms = Math.Sqrt(rates.Skip(500).Average(r => (double)r.Z * r.Z)) / Deg;
        Assert.InRange(rms, 2.0, 3.0);
        var fast = Drive(Seeded(Quantized(FaceUp)), t => flicker[Math.Min(flicker.Length - 1, (int)(t * 100))], 3.9f, 0.02f, deviceHz: 100);
        double fastRms = Math.Sqrt(fast.Skip(500).Average(r => (double)r.Z * r.Z)) / Deg;
        Assert.True(fastRms > 3 * rms);
    }

    // ── the estimator: gates ──

    /// <summary>A shove along gravity reads zero while it lasts, and the
    /// turn made during it still arrives as the smoothing catches up.</summary>
    [Fact]
    public void AShoveReadsZeroAndTheTurnArrivesAfterIt()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Vector3 At(float t)
        {
            var a = Turned(FaceUp, Vector3.UnitX, w * t);
            return t > 0.5f && t <= 0.6f ? a * 1.5f : a;
        }
        var rates = Drive(est, At, 1.5f);
        for (int i = 501; i < 600; i++) Assert.Equal(Vector3.Zero, rates[i]);
        // The whole turn, 1.5 s at 60 deg/s, minus what is still in the
        // smoothing lag at the end (about rate * tau).
        float integrated = rates.Sum(r => r.X) * 0.001f;
        Assert.InRange(integrated / (w * 1.5f - w * 0.1f), 0.97f, 1.03f);
    }

    /// <summary>Short shoves far apart each release their turn. The time a
    /// shove spends outside the window is paid back while the pad sits
    /// inside it, so two 0.15 s shoves half a second apart never add up to
    /// a reseed, and both 9° turns arrive.</summary>
    [Fact]
    public void ShortShovesFarApartEachReleaseTheirTurn()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        float Angle(float t) => w * (Math.Clamp(t - 0.2f, 0f, 0.15f) + Math.Clamp(t - 0.85f, 0f, 0.15f));
        static bool Shoving(float t) => (t > 0.2f && t <= 0.35f) || (t > 0.85f && t <= 1.0f);
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, Angle(t)) * (Shoving(t) ? 1.5f : 1f), 1.6f);
        float turned = rates.Sum(r => r.X) * 0.001f;
        Assert.InRange(turned / Deg, 17.5f, 18.5f);
    }

    /// <summary>A shove over 0.25 s reseeds. The turn made during it does
    /// not arrive as one burst afterward.</summary>
    [Fact]
    public void ALongShoveReseeds()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Vector3 At(float t)
        {
            float angle = w * MathF.Min(t, 0.9f);
            var a = Turned(FaceUp, Vector3.UnitX, angle);
            return t > 0.5f && t <= 0.9f ? a * 1.5f : a;
        }
        var rates = Drive(est, At, 1.5f);
        Assert.True(rates.Skip(901).Max(r => r.X) < 0.5f * Deg);
    }

    /// <summary>A shake along gravity dips back into the window every cycle.
    /// The time outside is paid down, not cleared, so the shake still adds
    /// up to a reseed every few cycles. When the pad stops, only the turn
    /// since the last reseed swings in: 12° here, against 6° of ordinary
    /// lag without the shake. Cleared on every dip, the same shake never
    /// reseeds and releases 27°.</summary>
    [Fact]
    public void AShakeThatDipsIntoTheWindowStillReseeds()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Vector3 At(float t)
        {
            float angle = w * MathF.Min(t, 1f);
            var a = Turned(FaceUp, Vector3.UnitX, angle);
            // A 4 Hz shake of one g along gravity while the pad turns.
            return t < 1f ? a * (1f + MathF.Sin(2 * MathF.PI * 4 * t)) : a;
        }
        var rates = Drive(est, At, 2f, deviceHz: 100);
        float after = rates.Skip(1000).Sum(r => r.X) * 0.001f;
        Assert.True(after < 20 * Deg, $"turned {after / Deg:F1} deg after the pad stopped");
    }

    /// <summary>The window is yuzu's 0.75 g to 1.25 g. Just outside it a
    /// sample cannot seed the estimate, and just inside it the turn reads
    /// in full.</summary>
    [Theory]
    [InlineData(0.74f, false)]
    [InlineData(0.76f, true)]
    [InlineData(1.24f, true)]
    [InlineData(1.26f, false)]
    public void TheWindowsEdgesSitAtYuzusValues(float scale, bool reads)
    {
        float w = 60 * Deg;
        var est = new AccelRateEstimator();
        est.Update(FaceUp * scale, 0f, 0.1f);
        Assert.Equal(reads, est.HasValue);
        if (!reads) return;
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t) * scale, 1f);
        Assert.InRange(rates[^1].X / w, 0.98f, 1.02f);
    }

    /// <summary>PadForge's own path reads DS3-E lying flat at 1.18 g. The
    /// window centers on that, so a 0.08 g lift while turning still reads,
    /// where a window fixed at 1.25 g read nothing, and a 0.3 g shove reads
    /// zero either way.</summary>
    [Theory]
    [InlineData(0.08f, true)]
    [InlineData(-0.08f, true)]
    [InlineData(0.3f, false)]
    [InlineData(-0.3f, false)]
    public void AnUncalibratedPadKeepsAQuarterGEitherWay(float push, bool reads)
    {
        const float rest = 1.18f;
        float w = 30 * Deg;
        var est = Seeded(FaceUp * rest);
        Vector3 At(float t)
        {
            var a = Turned(FaceUp, Vector3.UnitX, w * t) * rest;
            return t > 0.5f && t <= 0.56f ? a * ((rest + push) / rest) : a;
        }
        var rates = Drive(est, At, 0.56f);
        if (reads) Assert.True(rates[^1].X > 0.9f * w);
        else Assert.Equal(Vector3.Zero, rates[^1]);
    }

    /// <summary>Without a per-pad zero the resting length changes with the
    /// pad's attitude, 1.18 g flat to 0.81 g upside down for DS3-E. Turning
    /// steadily while the length falls a third of a g over three seconds
    /// keeps reading, because the window follows the length.</summary>
    [Fact]
    public void TheRestingLengthFollowsTheAttitude()
    {
        float w = 30 * Deg;
        var est = Seeded(FaceUp * 1.18f);
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t) * (1.18f - 0.11f * t), 3f);
        Assert.True(rates[^1].X > 0.9f * w);
    }

    /// <summary>Seeded face down at 0.80 g on PadForge's own path, then
    /// turned face up, where the same pad reads 1.18 g: past a quarter second
    /// outside the window the estimate starts over, and the turn reads.</summary>
    [Fact]
    public void APadSeededInOneAttitudeReadsInAnother()
    {
        float w = 30 * Deg;
        var est = Seeded(FaceUp * 0.80f);
        float Length(float t) => t < 0.5f ? 0.80f + (1.18f - 0.80f) * t / 0.5f : 1.18f;
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t) * Length(t), 2f);
        Assert.True(rates[^1].X > 0.9f * w);
    }

    [Fact]
    public void AShoveAtTheWindowsEdgeReadsZeroAndHolds()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t), 0.5f);
        var held = est.Rate;
        Assert.True(held.X > 0.9f * w);
        var rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t) * 1.26f, 0.1f, start: 0.5f);
        Assert.All(rates, r => Assert.Equal(Vector3.Zero, r));
        rates = Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t) * 0.74f, 0.1f, start: 0.6f);
        Assert.All(rates, r => Assert.Equal(Vector3.Zero, r));
    }

    [Fact]
    public void APollGapReseeds()
    {
        float w = 60 * Deg;
        var est = Seeded(FaceUp);
        Drive(est, t => Turned(FaceUp, Vector3.UnitX, w * t), 0.5f);
        est.Update(Turned(FaceUp, Vector3.UnitX, w * 0.8f), 0.3f, 0.1f);
        Assert.True(est.HasValue);
        Assert.Equal(Vector3.Zero, est.Rate);
    }

    [Fact]
    public void AnAllZeroOrNonFiniteSampleResets()
    {
        var est = Seeded(FaceUp);
        est.Update(Vector3.Zero, 0.001f, 0.1f);
        Assert.False(est.HasValue);
        est = Seeded(FaceUp);
        est.Update(new Vector3(float.NaN, G, 0), 0.001f, 0.1f);
        Assert.False(est.HasValue);
        // A shove cannot seed the direction.
        est.Update(FaceUp * 1.5f, 0.001f, 0.1f);
        Assert.False(est.HasValue);
        // Neither does a clock that ran backward.
        est = Seeded(FaceUp);
        est.Update(FaceUp, -0.001f, 0.1f);
        Assert.False(est.HasValue);
    }

    // ── the estimator: smoothing ──

    [Theory]
    [InlineData(0f, 0.02f)]
    [InlineData(-50f, 0.02f)]
    [InlineData(float.NaN, 0.1f)]
    [InlineData(1e6f, 0.25f)]
    [InlineData(100f, 0.1f)]
    public void TheSmoothingTimeIsBounded(float milliseconds, float seconds)
    {
        Assert.Equal(seconds, SimulatedGyro.SmoothingSeconds(milliseconds), 5);
        Assert.Equal(seconds, AccelRateEstimator.SmoothingSeconds(milliseconds / 1000f), 5);
    }

    /// <summary>The time constant sets the response: one tau into a steady
    /// turn the rate has reached 63% of it.</summary>
    [Fact]
    public void TheSmoothingTimeSetsTheResponse()
    {
        float w = 60 * Deg;
        var rates = Drive(Seeded(FaceUp), t => Turned(FaceUp, Vector3.UnitX, w * t), 0.2f, tau: 0.1f);
        Assert.InRange(rates[99].X / w, 0.60f, 0.66f);
    }

    /// <summary>PadForge's own path publishes the accelerometer against a
    /// fixed 512. DsHidMini's DS3-A1a rests 17, 2 and 18 counts off in SDL's
    /// frame, 0.22 g, which bends the gain with the pad's attitude: a roll
    /// tilted 60 degrees back or forward reads 0.77 or 1.31 of what the
    /// same pad reads without the offset, the figures the issue states.</summary>
    [Theory]
    [InlineData(-60, 0.7715f)]
    [InlineData(60, 1.3104f)]
    public void AnAccelerometerOffsetBendsTheGain(int tilt, float ratio)
    {
        // A slow turn and a short smoothing time, so the attitude barely
        // moves while the filter settles.
        var offset = new Vector3(-17, 2, 18) * CountG;
        float w = 10 * Deg;
        var rest = Turned(FaceUp, Vector3.UnitX, tilt * Deg);
        var clean = Drive(Seeded(rest), t => Turned(rest, Vector3.UnitZ, w * t), 0.15f, tau: 0.02f);
        var biased = Drive(Seeded(rest + offset), t => Turned(rest, Vector3.UnitZ, w * t) + offset, 0.15f, tau: 0.02f);
        Assert.InRange(biased[^1].Z / clean[^1].Z, ratio - 0.02f, ratio + 0.02f);
        // A zero offset changes nothing, so the ratio above is the offset's.
        var control = Drive(Seeded(rest), t => Turned(rest, Vector3.UnitZ, w * t) + Vector3.Zero, 0.15f, tau: 0.02f);
        Assert.Equal(1f, control[^1].Z / clean[^1].Z, 4);
    }

    // ── which devices ──

    [Theory]
    [InlineData((ushort)0x054C, (ushort)0x0268, true, true, SimulatedGyro.Axes.Pitch | SimulatedGyro.Axes.Roll)]
    [InlineData((ushort)0x054C, (ushort)0x0268, true, false, SimulatedGyro.Axes.None)]
    [InlineData((ushort)0x054C, (ushort)0x0268, false, true, SimulatedGyro.Axes.None)]
    [InlineData((ushort)0x054C, (ushort)0x0CE6, true, true, SimulatedGyro.Axes.None)]
    [InlineData((ushort)0x057E, (ushort)0x0306, false, true, SimulatedGyro.Axes.None)]
    public void OnlyTheDualShock3LacksAxesItsAccelerometerSupplies(ushort vid, ushort pid, bool gyro, bool accel,
        SimulatedGyro.Axes expected)
        => Assert.Equal(expected, SimulatedGyro.MissingAxes(vid, pid, gyro, accel));

    // ── the funnel ──

    private const string DevA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string DevB = "bbbbbbbb-0000-0000-0000-000000000002";

    private sealed class FunnelRig : IDisposable
    {
        private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
        private readonly Func<string, int, (float, float, float)> _bias = SourceCoercion.GyroBiasProvider;
        private readonly Func<string, int, (float, float, float)> _auxBias = SourceCoercion.GyroAuxBiasProvider;
        private readonly Func<string, int, SimulatedGyroSample?> _sim = SourceCoercion.SimulatedGyroProvider;
        public string Grip = "Pointing";

        public FunnelRig()
        {
            SourceCoercion.GyroTuningProvider = (dev, slot) => new SourceCoercion.GyroTuning
            {
                SensH = 1, SensV = 1, OutputCurve = "Linear", Space = "Local", Grip = Grip,
                SimulateGyro = dev == DevA && slot == 0,
                SimulationSmoothingSeconds = 0.1f,
            };
            SourceCoercion.GyroBiasProvider = (_, _) => (0f, 0.1f, 0.3f);
            SourceCoercion.GyroAuxBiasProvider = (_, _) => (0f, 0f, 0f);
            SourceCoercion.SimulatedGyroProvider = (dev, slot)
                => new SimulatedGyroSample(1f, 9f, 2f, SimulatedGyro.Axes.Pitch | SimulatedGyro.Axes.Roll);
        }

        public void Dispose()
        {
            SourceCoercion.GyroTuningProvider = _tuning;
            SourceCoercion.GyroBiasProvider = _bias;
            SourceCoercion.GyroAuxBiasProvider = _auxBias;
            SourceCoercion.SimulatedGyroProvider = _sim;
        }
    }

    private static CustomInputState YawOnly()
    {
        var state = new CustomInputState();
        state.Gyro[0] = 0f; state.Gyro[1] = 0.5f; state.Gyro[2] = 0f;
        state.GyroAux[0] = 0.7f; state.GyroAux[1] = 0.8f; state.GyroAux[2] = 0.9f;
        return state;
    }

    /// <summary>Pitch and roll come from the simulation without the stored
    /// bias, which belongs to the sensor. Yaw stays the gyro, debiased.</summary>
    [Fact]
    public void TheFunnelReadsSimulatedPitchAndRollAndTheRealYaw()
    {
        using var rig = new FunnelRig();
        SourceCoercion.GetPassthroughGyro(YawOnly(), DevA, 0, out float p, out float y, out float r);
        Assert.Equal(1f, p);
        Assert.Equal(0.4f, y, 5);
        Assert.Equal(2f, r);
    }

    /// <summary>The option lives on one (device, slot). The same device on
    /// another slot, and another device on the same slot, read the sensor.</summary>
    [Fact]
    public void TheOptionIsPerDeviceAndSlot()
    {
        using var rig = new FunnelRig();
        SourceCoercion.GetPassthroughGyro(YawOnly(), DevA, 1, out float p1, out _, out float r1);
        Assert.Equal(0f, p1);
        Assert.Equal(-0.3f, r1, 5);
        SourceCoercion.GetPassthroughGyro(YawOnly(), DevB, 0, out float p2, out _, out float r2);
        Assert.Equal(0f, p2);
        Assert.Equal(-0.3f, r2, 5);
    }

    /// <summary>The grip picks the source axis first. Sideways, Face Up
    /// reads output pitch from source roll and output roll from minus
    /// source pitch, and both come from the simulation.</summary>
    [Fact]
    public void TheGripPicksTheSimulatedSourceAxis()
    {
        using var rig = new FunnelRig { Grip = "Sideways" };
        SourceCoercion.GetPassthroughGyro(YawOnly(), DevA, 0, out float p, out _, out float r);
        Assert.Equal(2f, p);
        Assert.Equal(-1f, r);
    }

    [Fact]
    public void TheAuxSensorIsNeverSimulated()
    {
        using var rig = new FunnelRig();
        SourceCoercion.GetPassthroughGyro(YawOnly(), DevA, 0, out float p, out float y, out float r, aux: true);
        Assert.Equal(0.7f, p, 5);
        Assert.Equal(0.8f, y, 5);
        Assert.Equal(0.9f, r, 5);
    }

    /// <summary>A fused read averages the body with the left Joy-Con. Only
    /// the body half takes the simulation: the left half is a second
    /// sensor and keeps its own reading.</summary>
    [Fact]
    public void AFusedReadSimulatesOnlyTheBodyHalf()
    {
        using var rig = new FunnelRig();
        var aux = SourceCoercion.HasGyroAuxProvider;
        try
        {
            var src = new MappingSource { Descriptor = "Gyro Pitch", DeviceGuid = DevA };
            SourceCoercion.HasGyroAuxProvider = _ => false;
            float body = SourceCoercion.EvaluateForBipolarAxisTarget(YawOnly(), src, 0, evaluatedDeviceGuid: DevA);
            SourceCoercion.HasGyroAuxProvider = _ => true;
            float fused = SourceCoercion.EvaluateForBipolarAxisTarget(YawOnly(), src, 0, evaluatedDeviceGuid: DevA);
            // Body pitch 1.0 from the simulation, left pitch 0.7 from GyroAux.
            Assert.Equal(0.85f, fused / body, 3);
        }
        finally { SourceCoercion.HasGyroAuxProvider = aux; }
    }

    [Fact]
    public void TheReadoutHelperReplacesOnlyTheAxesTheSimulationSupplies()
    {
        using var rig = new FunnelRig();
        float p = 5, y = 6, r = 7;
        SourceCoercion.ApplySimulatedGyro(DevA, 0, ref p, ref y, ref r);
        Assert.Equal((1f, 6f, 2f), (p, y, r));
        p = 5; y = 6; r = 7;
        SourceCoercion.ApplySimulatedGyro(DevA, 1, ref p, ref y, ref r);
        Assert.Equal((5f, 6f, 7f), (p, y, r));
    }

    /// <summary>A mapping row on the gyro reads through the same funnel.</summary>
    [Fact]
    public void AGyroMappingRowReadsTheSimulatedPitch()
    {
        using var rig = new FunnelRig();
        var src = new MappingSource { Descriptor = "Gyro Pitch", DeviceGuid = DevA };
        float on = SourceCoercion.EvaluateForBipolarAxisTarget(YawOnly(), src, 0, evaluatedDeviceGuid: DevA);
        float off = SourceCoercion.EvaluateForBipolarAxisTarget(YawOnly(), src, 1, evaluatedDeviceGuid: DevA);
        Assert.True(on > 0.05f);
        Assert.Equal(0f, off);
    }

    /// <summary>The motion snapshot the virtual controller and DSU read
    /// carries the simulated pitch and roll in deg/s beside the real yaw.</summary>
    [Fact]
    public void TheMotionSnapshotCarriesTheSimulatedAxes()
    {
        using var rig = new FunnelRig();
        var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input") { HasGyro = true, HasAccel = true };
        try
        {
            var device = new UserDevice
            {
                InstanceGuid = Guid.Parse(DevA), Device = wrapper, InputState = YawOnly(),
                IsOnline = true, HasGyro = true, HasAccel = true, VendorId = 0x054C, ProdId = 0x0268,
            };
            var capture = typeof(InputManager).GetMethod("CaptureMotionSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
            var snap = (MotionSnapshot)capture.Invoke(null, new object[]
            {
                ValueTuple.Create(device, (MappingSource)null), ValueTuple.Create((UserDevice)null, (MappingSource)null), 0, 0L,
            });
            const float RadToDeg = 180f / MathF.PI;
            Assert.Equal(1f * RadToDeg, snap.GyroPitch, 3);
            Assert.Equal(0.4f * RadToDeg, snap.GyroYaw, 3);
            Assert.Equal(2f * RadToDeg, snap.GyroRoll, 3);
        }
        finally { wrapper.Dispose(); }
    }

    // ── the runtime ──

    private sealed class RuntimeRig : IDisposable
    {
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly Func<string, int, SourceCoercion.GyroTuning> _tuning = SourceCoercion.GyroTuningProvider;
        public InputManager Manager { get; } = new();
        public readonly HashSet<(Guid, int)> On = new();
        public readonly Dictionary<int, float> Smoothing = new();
        private long _ticks = Stopwatch.Frequency;

        public RuntimeRig()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SourceCoercion.GyroTuningProvider = (dev, slot) => new SourceCoercion.GyroTuning
            {
                Grip = "Pointing",
                SimulateGyro = Guid.TryParse(dev, out var g) && On.Contains((g, slot)),
                SimulationSmoothingSeconds = Smoothing.TryGetValue(slot, out float tau) ? tau : 0.1f,
            };
        }

        public UserDevice Add(int slot, ushort pid = 0x0268)
        {
            var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input") { HasGyro = true, HasAccel = true };
            wrapper.SetConnected(true);
            var device = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), Device = wrapper, InputState = new CustomInputState(),
                IsOnline = true, HasGyro = true, HasAccel = true, CapType = InputDeviceType.Gamepad,
                VendorId = 0x054C, ProdId = pid,
            };
            SettingsManager.UserDevices.Items.Add(device);
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot });
            return device;
        }

        /// <summary>The assigned-slot snapshot refreshes every 250 ms. A test
        /// that reassigns forces the refresh instead of waiting.</summary>
        public void RefreshSlots()
            => typeof(InputManager).GetField("_assignedSlotsRefreshTick", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(Manager, 0L);

        public void Tick(UserDevice device, Vector3 accel, float seconds = 0.001f)
        {
            var state = device.InputState;
            state.Accel[0] = accel.X; state.Accel[1] = accel.Y; state.Accel[2] = accel.Z;
            _ticks += (long)(seconds * Stopwatch.Frequency);
            Manager.UpdateGyroSimulation(device, device.Device, state, _ticks);
        }

        public SimulatedGyroSample? Read(UserDevice device, int slot) => Manager.ReadGyroSimulation(device.InstanceGuidString, slot);

        public void Dispose()
        {
            foreach (var device in SettingsManager.UserDevices.Items) device.Device?.Dispose();
            Manager.Dispose();
            SourceCoercion.GyroTuningProvider = _tuning;
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
        }
    }

    [Fact]
    public void TheRuntimeEstimatesOnlyTheSlotsThatTurnedTheOptionOn()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ds3.InstanceGuid, MapTo = 1 });
        rig.On.Add((ds3.InstanceGuid, 0));
        float w = 60 * Deg;
        for (int i = 0; i <= 1000; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f));
        var sample = rig.Read(ds3, 0);
        Assert.True(sample.HasValue);
        Assert.InRange(sample.Value.Pitch, w * 0.98f, w * 1.02f);
        Assert.Equal(SimulatedGyro.Axes.Pitch | SimulatedGyro.Axes.Roll, sample.Value.Axes);
        Assert.Null(rig.Read(ds3, 1));
    }

    /// <summary>A DS3 setting pasted onto a pad with a full gyro does
    /// nothing: no estimator exists for it, and the funnel reads the sensor.</summary>
    [Fact]
    public void APadWithAFullGyroNeverGetsAnEstimator()
    {
        using var rig = new RuntimeRig();
        var dualSense = rig.Add(0, pid: 0x0CE6);
        rig.On.Add((dualSense.InstanceGuid, 0));
        for (int i = 0; i < 50; i++) rig.Tick(dualSense, FaceUp);
        Assert.Null(rig.Read(dualSense, 0));
    }

    [Fact]
    public void TurningTheOptionOffUnassigningOrGoingOfflineReadsNothing()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        rig.Tick(ds3, FaceUp);
        Assert.NotNull(rig.Read(ds3, 0));

        ds3.IsOnline = false;
        Assert.Null(rig.Read(ds3, 0));
        ds3.IsOnline = true;
        Assert.NotNull(rig.Read(ds3, 0));

        rig.On.Clear();
        rig.Tick(ds3, FaceUp);
        Assert.Null(rig.Read(ds3, 0));

        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        Assert.NotNull(rig.Read(ds3, 0));
        SettingsManager.UserSettings.Items.Clear();
        rig.RefreshSlots();
        rig.Tick(ds3, FaceUp);
        Assert.Null(rig.Read(ds3, 0));
    }

    /// <summary>A failed read and a new wrapper both start the estimate
    /// over: the rate in flight drops, and the next sample seeds instead of
    /// differencing against a pose from before the gap.</summary>
    [Fact]
    public void AFailedReadOrANewWrapperStartsTheEstimateOver()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        float w = 60 * Deg;
        for (int i = 0; i <= 300; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f));
        Assert.True(rig.Read(ds3, 0).Value.Pitch > 0.5f * w);
        rig.Manager.InvalidateGyroSimulation(ds3.InstanceGuid);
        var far = Turned(FaceUp, Vector3.UnitX, 50 * Deg);
        rig.Tick(ds3, far);
        Assert.Equal(0f, rig.Read(ds3, 0).Value.Pitch);
        rig.Tick(ds3, far);
        Assert.Equal(0f, rig.Read(ds3, 0).Value.Pitch, 4);

        for (int i = 0; i <= 300; i++) rig.Tick(ds3, Turned(far, Vector3.UnitX, w * i * 0.001f));
        Assert.True(rig.Read(ds3, 0).Value.Pitch > 0.5f * w);
        var old = ds3.Device;
        var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input") { HasGyro = true, HasAccel = true };
        wrapper.SetConnected(true);
        ds3.Device = wrapper;
        old.Dispose();
        rig.Tick(ds3, FaceUp);
        Assert.Equal(0f, rig.Read(ds3, 0).Value.Pitch);
    }

    /// <summary>Each slot runs its own smoothing time. 100 ms into a steady
    /// turn, a 20 ms slot has caught up and a 250 ms slot reads a third.</summary>
    [Fact]
    public void EachSlotRunsItsOwnSmoothingTime()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ds3.InstanceGuid, MapTo = 1 });
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.On.Add((ds3.InstanceGuid, 1));
        rig.Smoothing[0] = 0.02f;
        rig.Smoothing[1] = 0.25f;
        float w = 60 * Deg;
        for (int i = 0; i <= 100; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f));
        Assert.InRange(rig.Read(ds3, 0).Value.Pitch / w, 0.97f, 1.0f);
        Assert.InRange(rig.Read(ds3, 1).Value.Pitch / w, 0.30f, 0.36f);
    }

    /// <summary>A read between a wrapper swap and the next poll returns
    /// nothing: the estimate belongs to the wrapper it was built on.</summary>
    [Fact]
    public void AReadAfterAWrapperSwapReturnsNothingUntilThePollSeesIt()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        Assert.NotNull(rig.Read(ds3, 0));
        var old = ds3.Device;
        var wrapper = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input") { HasGyro = true, HasAccel = true };
        wrapper.SetConnected(true);
        ds3.Device = wrapper;
        old.Dispose();
        Assert.Null(rig.Read(ds3, 0));
    }

    [Fact]
    public void DisconnectAndDisposeDropTheEstimate()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        float w = 60 * Deg;
        for (int i = 0; i <= 200; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f));
        typeof(InputManager).GetMethod("DisconnectGyroSimulation", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(rig.Manager, new object[] { ds3.InstanceGuid });
        Assert.Null(rig.Read(ds3, 0));
        rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, 40 * Deg));
        Assert.Equal(0f, rig.Read(ds3, 0).Value.Pitch);
        rig.Manager.Dispose();
        Assert.Null(rig.Read(ds3, 0));
    }

    /// <summary>The profile switch the app runs resets the estimate, through
    /// the real InputService path.</summary>
    [Fact]
    public void TheAppsProfileSwitchResetsTheEstimate()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        float w = 60 * Deg;
        for (int i = 0; i <= 200; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f));
        Assert.NotNull(rig.Read(ds3, 0));
        using var service = new InputService(new MainViewModel());
        typeof(InputService).GetField("_inputManager", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, rig.Manager);
        typeof(InputService).GetMethod("ResetRuntimeStateForProfileSwitch", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(service, null);
        Assert.Null(rig.Read(ds3, 0));
    }

    /// <summary>The Gyro tab's readout reads from the UI thread while the
    /// polling thread writes and a profile switch resets.</summary>
    [Fact]
    public async Task ResetAndReadCanRunBesideThePollingWriter()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        float w = 60 * Deg;
        await Task.WhenAll(
            Task.Run(() => { for (int i = 1; i <= 1000; i++) rig.Tick(ds3, Turned(FaceUp, Vector3.UnitX, w * i * 0.001f)); }),
            Task.Run(() => { for (int i = 0; i < 1000; i++) rig.Manager.ResetGyroSimulation(ds3.InstanceGuid); }),
            Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    var sample = rig.Read(ds3, 0);
                    if (sample is { } s)
                        Assert.True(float.IsFinite(s.Pitch) && float.IsFinite(s.Yaw) && float.IsFinite(s.Roll));
                }
            }));
    }

    [Fact]
    public void PruneForgetsADeviceThatLeftTheList()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        Assert.NotNull(rig.Read(ds3, 0));
        typeof(InputManager).GetMethod("PruneGyroSimulation", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(rig.Manager, new object[] { Array.Empty<UserDevice>() });
        Assert.Null(rig.Read(ds3, 0));
    }

    [Fact]
    public void AProfileSwitchResetsTheEstimate()
    {
        using var rig = new RuntimeRig();
        var ds3 = rig.Add(0);
        rig.On.Add((ds3.InstanceGuid, 0));
        rig.Tick(ds3, FaceUp);
        rig.Manager.ResetGyroSimulation();
        Assert.Null(rig.Read(ds3, 0));
        rig.Tick(ds3, FaceUp);
        Assert.NotNull(rig.Read(ds3, 0));
    }

    // ── settings ──

    [Fact]
    public void TheSettingsDefaultOffAt100Milliseconds()
    {
        var ps = new PadSetting();
        Assert.Equal("0", ps.GyroSimulation);
        Assert.Equal("100", ps.GyroSimulationSmoothingMs);
        var vm = new PadViewModel(0);
        Assert.False(vm.GyroSimulation);
        Assert.Equal(100, vm.GyroSimulationSmoothingMs);
    }

    [Fact]
    public void TheViewModelClampsTheSmoothingAndItsResetsRestoreTheDefaults()
    {
        var vm = new PadViewModel(0);
        vm.GyroSimulationSmoothingMs = 0;
        Assert.Equal(20, vm.GyroSimulationSmoothingMs);
        vm.GyroSimulationSmoothingMs = 1e6;
        Assert.Equal(250, vm.GyroSimulationSmoothingMs);
        vm.GyroSimulationSmoothingMs = -50;
        Assert.Equal(20, vm.GyroSimulationSmoothingMs);
        vm.GyroSimulationSmoothingMs = double.NaN;
        Assert.Equal(100, vm.GyroSimulationSmoothingMs);
        vm.GyroSimulation = true;
        vm.ResetGyroSimulationCommand.Execute(null);
        Assert.False(vm.GyroSimulation);
        vm.ResetGyroSimulationSmoothingCommand.Execute(null);
        Assert.Equal(100, vm.GyroSimulationSmoothingMs);
        vm.GyroSimulation = true;
        vm.GyroSimulationSmoothingMs = 40;
        vm.ResetGyroSimulationCardCommand.Execute(null);
        Assert.False(vm.GyroSimulation);
        Assert.Equal(100, vm.GyroSimulationSmoothingMs);
    }

    private static readonly string[] SettingNames = { "GyroSimulation", "GyroSimulationSmoothingMs" };

    [Fact]
    public void TheSettingsSurviveXmlCloneChecksumAndBothViewModelSyncPairs()
    {
        var devices = SettingsManager.UserDevices;
        var settings = SettingsManager.UserSettings;
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), IsOnline = true, VendorId = 0x054C, ProdId = 0x0268, HasGyro = true, HasAccel = true };
            SettingsManager.UserDevices.Items.Add(ud);
            var assignment = new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = 0 };
            var ps = new PadSetting { GyroSimulation = "1", GyroSimulationSmoothingMs = "150" };
            assignment.SetPadSetting(ps);
            SettingsManager.UserSettings.Items.Add(assignment);

            foreach (string name in SettingNames)
            {
                var copy = ps.CloneDeep();
                Assert.Equal(typeof(PadSetting).GetProperty(name).GetValue(ps), typeof(PadSetting).GetProperty(name).GetValue(copy));
                var before = copy.ComputeChecksum();
                typeof(PadSetting).GetProperty(name).SetValue(copy, "7");
                Assert.NotEqual(before, copy.ComputeChecksum());
            }
            var serializer = new XmlSerializer(typeof(PadSetting));
            using var text = new StringWriter();
            serializer.Serialize(text, ps);
            var restored = (PadSetting)serializer.Deserialize(new StringReader(text.ToString()));
            Assert.Equal("1", restored.GyroSimulation);
            Assert.Equal("150", restored.GyroSimulationSmoothingMs);

            var main = new MainViewModel();
            var service = new InputService(main);
            var store = new SettingsService(main);
            var vm = main.Pads[0];
            var mapped = new PadViewModel.MappedDeviceInfo { InstanceGuid = ud.InstanceGuid, Name = "DualShock 3", IsOnline = true };
            vm.MappedDevices.Add(mapped);
            vm.SelectedMappedDevice = mapped;
            InputService.LoadPadSettingIntoViewModel(vm, restored);
            Assert.True(vm.GyroSimulation);
            Assert.Equal(150, vm.GyroSimulationSmoothingMs);
            assignment.SetPadSetting(new PadSetting());
            typeof(InputService).GetMethod("SaveViewModelToPadSetting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { vm, ud.InstanceGuid, false });
            Assert.Equal("1", assignment.GetPadSetting().GyroSimulation);
            Assert.Equal("150", assignment.GetPadSetting().GyroSimulationSmoothingMs);

            vm.GyroSimulation = false;
            vm.GyroSimulationSmoothingMs = 100;
            assignment.SetPadSetting(ps);
            typeof(SettingsService).GetMethod("LoadPadSettings", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(store, new object[] { new[] { assignment }, new[] { ps } });
            Assert.True(vm.GyroSimulation);
            Assert.Equal(150, vm.GyroSimulationSmoothingMs);
            assignment.SetPadSetting(new PadSetting());
            store.UpdatePadSettingsFromViewModels();
            Assert.Equal("1", assignment.GetPadSetting().GyroSimulation);
            Assert.Equal("150", assignment.GetPadSetting().GyroSimulationSmoothingMs);
        }
        finally
        {
            SettingsManager.UserDevices = devices;
            SettingsManager.UserSettings = settings;
        }
    }

    /// <summary>The slot summary prints SIM only for a device the card
    /// serves, so a DS3 setting pasted onto a DualSense shows no heat.</summary>
    [Fact]
    public void TheSummaryTokenPrintsOnlyForADeviceTheCardServes()
    {
        var append = typeof(InputService).GetMethod("AppendGyroStageTokens", BindingFlags.NonPublic | BindingFlags.Static);
        List<string> Tokens(PadSetting ps, bool simulates)
        {
            var parts = new List<string>();
            append.Invoke(null, new object[] { parts, ps, simulates });
            return parts;
        }
        var on = new PadSetting { GyroSimulation = "1" };
        Assert.Contains("SIM", Tokens(on, true));
        Assert.DoesNotContain(Tokens(on, false), t => t.StartsWith("SIM"));
        Assert.Contains("SIM 150ms", Tokens(new PadSetting { GyroSimulation = "1", GyroSimulationSmoothingMs = "150" }, true));
        Assert.DoesNotContain(Tokens(new PadSetting(), true), t => t.StartsWith("SIM"));
        // The token shows what the filter runs at.
        Assert.Contains("SIM 250ms", Tokens(new PadSetting { GyroSimulation = "1", GyroSimulationSmoothingMs = "1e6" }, true));
        Assert.Contains("SIM", Tokens(new PadSetting { GyroSimulation = "1", GyroSimulationSmoothingMs = "NaN" }, true));
    }

    /// <summary>The setting reaches the estimator as seconds, clamped: the
    /// tuning the app's provider builds from a profile turns "150" into
    /// 0.15 s, "0" into 0.02 s and "NaN" into the default.</summary>
    [Theory]
    [InlineData("150", 0.15f)]
    [InlineData("0", 0.02f)]
    [InlineData("NaN", 0.1f)]
    public void TheSmoothingSettingReachesTheTuningAsSeconds(string stored, float seconds)
    {
        var t = InputService.GyroTuningFromPadSetting(new PadSetting { GyroSimulation = "1", GyroSimulationSmoothingMs = stored });
        Assert.True(t.SimulateGyro);
        Assert.Equal(seconds, t.SimulationSmoothingSeconds, 4);
        Assert.False(InputService.GyroTuningFromPadSetting(new PadSetting()).SimulateGyro);
    }

    // ── calibration ──

    private static readonly Guid CalGuid = Guid.Parse("47200000-0000-0000-0000-000000000474");

    private const string PadA = "001fe2a1b2c3/direct";
    private const string PadB = "001fe2d4e5f6/direct";

    /// <summary>Stands in for the pad identity InputService wires: the
    /// direct path's connection or DsHidMini's device node.</summary>
    private sealed class PadAddress : IDisposable
    {
        private readonly Func<UserDevice, string> _old = GyroCalibratorService.UnitIdentityProvider;
        public string Value;
        public PadAddress(string value)
        {
            Value = value;
            GyroCalibratorService.UnitIdentityProvider = _ => Value;
        }
        public void Dispose() => GyroCalibratorService.UnitIdentityProvider = _old;
    }

    private static (UserDevice ud, CustomInputState state) Pad(ushort vid, ushort pid)
    {
        var state = new CustomInputState();
        var ud = new UserDevice
        {
            InstanceGuid = CalGuid, ProductName = "Calibration Pad", IsOnline = true,
            HasGyro = true, HasAccel = true, InputState = state, VendorId = vid, ProdId = pid,
        };
        return (ud, state);
    }

    /// <summary>Units on record rest at word 727, 2.7 rad/s while still. The
    /// automatic pass refuses that: the rule for this part is never to learn
    /// its center unattended.</summary>
    [Fact]
    public async Task TheAutomaticPassRefusesADs3YawOffset()
    {
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(727);
        var ps = new PadSetting();
        Assert.False(await new GyroCalibratorService().RecalibrateAsync(ud, ps, 250));
        Assert.Equal("0", ps.GyroBiasYaw);
    }

    [Fact]
    public async Task APressCalibratesADs3YawOffset()
    {
        using var address = new PadAddress(PadA);
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(727);
        var ps = new PadSetting();
        Assert.True(await new GyroCalibratorService().RecalibrateAsync(ud, ps, 250, deliberate: true));
        Assert.Equal(state.Gyro[1], float.Parse(ps.GyroBiasYaw, System.Globalization.CultureInfo.InvariantCulture), 4);
        Assert.Equal(PadA, ps.GyroCalibratedDevice);
        Assert.True(GyroCalibratorService.CalibrationApplies(ud, ps));
        new GyroCalibratorService().ResetCalibration(ps);
        Assert.Equal("", ps.GyroCalibratedDevice);
    }

    /// <summary>The automatic pass keeps the 0.15 rad/s bound on a DS3's
    /// yaw: word 523 sits at 0.140 rad/s and calibrates, word 524 sits at
    /// 0.153 and does not. The connect-time lane is the one that refuses.</summary>
    [Theory]
    [InlineData(523, true)]
    [InlineData(524, false)]
    [InlineData(727, false)]
    public async Task TheAutomaticPassKeepsTheMemsBoundOnADs3(int word, bool accepted)
    {
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(word);
        var ps = new PadSetting();
        Assert.Equal(accepted, await new GyroCalibratorService().EnsureAutoCalibratedAsync(ud, ps));
        if (!accepted) Assert.Equal("0", ps.GyroBiasYaw);
    }

    /// <summary>A calibration belongs to the device it measured. A DS3
    /// ignores one from before its yaw sign changed (no device recorded)
    /// or from another unit, and measures again. Another device ignores a
    /// bias too large for its own gyro, which only a DS3 could have written.</summary>
    [Fact]
    public void ACalibrationAppliesOnlyToTheDeviceThatMeasuredIt()
    {
        using var address = new PadAddress(PadA);
        var (ds3, _) = Pad(0x054C, 0x0268);
        // Letters, so the upper-cased copy below differs from the string the
        // pad writes.
        ds3.InstanceGuid = Guid.Parse("4720abcd-ef00-0000-0000-000000000474");
        var stale = new PadSetting { GyroBiasYaw = "2.75", GyroCalibratedAtUtc = "2026-07-01T00:00:00Z" };
        Assert.False(GyroCalibratorService.CalibrationApplies(ds3, stale));
        Assert.True(GyroCalibratorService.WouldCalibrate(ds3, stale));
        stale.GyroCalibratedDevice = Guid.NewGuid().ToString();
        Assert.False(GyroCalibratorService.CalibrationApplies(ds3, stale));
        stale.GyroCalibratedDevice = PadA.ToUpperInvariant();
        Assert.True(GyroCalibratorService.CalibrationApplies(ds3, stale));
        Assert.False(GyroCalibratorService.WouldCalibrate(ds3, stale));

        var (dualSense, _) = Pad(0x054C, 0x0CE6);
        var carried = new PadSetting { GyroBiasYaw = "2.75", GyroCalibratedAtUtc = "2026-10-01T00:00:00Z", GyroCalibratedDevice = ds3.InstanceGuidString };
        Assert.False(GyroCalibratorService.CalibrationApplies(dualSense, carried));
        Assert.True(GyroCalibratorService.WouldCalibrate(dualSense, carried));
        var own = new PadSetting { GyroBiasYaw = "0.1", GyroCalibratedAtUtc = "2026-10-01T00:00:00Z" };
        Assert.True(GyroCalibratorService.CalibrationApplies(dualSense, own));
        Assert.False(GyroCalibratorService.WouldCalibrate(dualSense, own));
    }

    /// <summary>A pad other than a DS3 carrying a stamped primary above the
    /// MEMS bound gets the full pass too, where a stamped primary of its own
    /// would get only the aux upgrade.</summary>
    [Fact]
    public async Task ACarriedPrimaryOnAnotherPadIsMeasuredAgainInFull()
    {
        var (ud, state) = Pad(0x057E, 0x2008);
        ud.HasGyroAux = true;
        state.Gyro[0] = 0.01f;
        state.GyroAux[0] = 0.02f;
        var ps = new PadSetting { GyroBiasPitch = "0.5", GyroCalibratedAtUtc = "2026-01-01T00:00:00Z" };
        Assert.True(await new GyroCalibratorService().EnsureAutoCalibratedAsync(ud, ps));
        Assert.Equal(0.01f, float.Parse(ps.GyroBiasPitch, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(0.02f, float.Parse(ps.GyroAuxBiasPitch, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.NotEqual("2026-01-01T00:00:00Z", ps.GyroCalibratedAtUtc);
    }

    /// <summary>The funnel subtracts a stored bias only when the calibration
    /// belongs to the pad: a DS3 drops one it didn't measure and keeps one it
    /// did, and another pad drops a bias only a DS3 could have written.</summary>
    [Fact]
    public void TheFunnelSubtractsOnlyABiasThePadOwns()
    {
        using var address = new PadAddress(PadA);
        var (ds3, _) = Pad(0x054C, 0x0268);
        var ps = new PadSetting { GyroBiasYaw = "2.75", GyroCalibratedAtUtc = "2026-10-01T00:00:00Z" };
        Assert.Equal((0f, 0f, 0f), InputService.GyroBiasFromPadSetting(ds3, ps));
        ps.GyroCalibratedDevice = GyroCalibratorService.CalibrationOwner(ds3);
        Assert.Equal((0f, 2.75f, 0f), InputService.GyroBiasFromPadSetting(ds3, ps));
        var (dualSense, _) = Pad(0x054C, 0x0CE6);
        Assert.Equal((0f, 0f, 0f), InputService.GyroBiasFromPadSetting(dualSense, ps));
        Assert.Equal((0.05f, 0f, 0f), InputService.GyroBiasFromPadSetting(dualSense, new PadSetting { GyroBiasPitch = "0.05" }));
    }

    /// <summary>A stale DS3 calibration gets a full automatic pass, not the
    /// aux-only upgrade a stamped profile would get, so the primary bias is
    /// measured again under the new sign.</summary>
    [Fact]
    public async Task AStaleDs3CalibrationIsMeasuredAgainInFull()
    {
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(515);
        var ps = new PadSetting { GyroBiasYaw = "-0.04", GyroCalibratedAtUtc = "2026-07-01T00:00:00Z" };
        Assert.True(await new GyroCalibratorService().EnsureAutoCalibratedAsync(ud, ps));
        Assert.Equal(state.Gyro[1], float.Parse(ps.GyroBiasYaw, System.Globalization.CultureInfo.InvariantCulture), 4);
        Assert.Equal(ud.InstanceGuidString, ps.GyroCalibratedDevice);
    }

    /// <summary>Every DualShock 3 on PadForge's own path shares one device
    /// row, so a calibration names the pad by its address. Pad B on the row
    /// pad A calibrated reads A's bias as none and is measured again, as is
    /// a pad whose address can't be read, and a profile from another PC.</summary>
    [Fact]
    public async Task TwoPadsOnOneRowKeepTheirOwnCalibration()
    {
        using var address = new PadAddress(PadA);
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(727);
        var ps = new PadSetting();
        Assert.True(await new GyroCalibratorService().RecalibrateAsync(ud, ps, 250, deliberate: true));
        Assert.Equal((0f, state.Gyro[1], 0f), InputService.GyroBiasFromPadSetting(ud, ps));

        address.Value = PadB;
        Assert.False(GyroCalibratorService.CalibrationApplies(ud, ps));
        Assert.True(GyroCalibratorService.WouldCalibrate(ud, ps));
        Assert.Equal((0f, 0f, 0f), InputService.GyroBiasFromPadSetting(ud, ps));

        address.Value = null;
        Assert.False(GyroCalibratorService.CalibrationApplies(ud, ps));

        address.Value = PadA;
        Assert.True(GyroCalibratorService.CalibrationApplies(ud, ps));
    }

    /// <summary>The owner names the pad, not the row: a DsHidMini row re-keyed
    /// by a switch from USB to Bluetooth keeps the pad's calibration. It
    /// names what serves the yaw too, so a calibration doesn't cross from
    /// PadForge's own path to DsHidMini, or across a DsHidMini update, which
    /// center the word differently.</summary>
    [Fact]
    public void ACalibrationFollowsThePadAndTheDriverServingIt()
    {
        const string Usb315 = "001fe2a1b2c3/node:3.15.0.0";
        using var address = new PadAddress(Usb315);
        var (ud, _) = Pad(0x054C, 0x0268);
        var ps = new PadSetting { GyroBiasYaw = "2.75", GyroCalibratedAtUtc = "2026-10-01T00:00:00Z", GyroCalibratedDevice = Usb315 };
        Assert.True(GyroCalibratorService.CalibrationApplies(ud, ps));
        ud.InstanceGuid = Guid.NewGuid();
        Assert.True(GyroCalibratorService.CalibrationApplies(ud, ps));
        address.Value = "001fe2a1b2c3/direct";
        Assert.False(GyroCalibratorService.CalibrationApplies(ud, ps));
        address.Value = "001fe2a1b2c3/node:3.5.1.0";
        Assert.False(GyroCalibratorService.CalibrationApplies(ud, ps));
    }

    /// <summary>A re-key carries a calibration owned by the row alone to the
    /// new row, and leaves one that names a pad as it is.</summary>
    [Fact]
    public void ARekeyCarriesARowOwnedCalibration()
    {
        var settings = SettingsManager.UserSettings;
        try
        {
            SettingsManager.UserSettings = new SettingsCollection();
            Guid oldRow = Guid.NewGuid(), newRow = Guid.NewGuid();
            var byRow = new UserSetting { InstanceGuid = newRow, MapTo = 0 };
            byRow.SetPadSetting(new PadSetting { GyroCalibratedDevice = oldRow.ToString() });
            var byPad = new UserSetting { InstanceGuid = newRow, MapTo = 1 };
            byPad.SetPadSetting(new PadSetting { GyroCalibratedDevice = PadA });
            SettingsManager.UserSettings.Items.Add(byRow);
            SettingsManager.UserSettings.Items.Add(byPad);
            InputService.RemapDeviceGuidsInStoredPadSettings(oldRow, newRow);
            Assert.Equal(newRow.ToString(), byRow.GetPadSetting().GyroCalibratedDevice);
            Assert.Equal(PadA, byPad.GetPadSetting().GyroCalibratedDevice);
        }
        finally { SettingsManager.UserSettings = settings; }
    }

    /// <summary>Step 1 reads a DsHidMini pad's node when its row comes online,
    /// so the first bias read knows the pad. A later connection whose read
    /// is still running gets the row's last identity, not none.</summary>
    [Fact]
    public async Task TheNodeIsReadAtArrivalAndTheRowsLastIdentityCoversALaterRead()
    {
        var gate = new System.Threading.ManualResetEventSlim(true);
        string served = "001fe2a1b2c3/node:3.15.0.0";
        Ds3UnitIdentity.ResetForTests();
        Ds3UnitIdentity.NodeReader = _ => { gate.Wait(5000); return served; };
        var first = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input");
        var second = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input");
        var third = new WebControllerDevice(Guid.NewGuid().ToString(), "Motion Input");
        try
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0268, Device = first, DevicePath = @"\\?\hid#vid_054c&pid_0268#a" };
            Ds3UnitIdentity.Prime(ud);
            Assert.Equal(served, Ds3UnitIdentity.Identity(ud));

            gate.Reset();
            ud.Device = second;
            Assert.Equal(served, Ds3UnitIdentity.Identity(ud));
            served = "001fe2d4e5f6/node:3.15.0.0";
            gate.Set();
            for (int i = 0; i < 100 && Ds3UnitIdentity.Identity(ud) != served; i++) await Task.Delay(10);
            Assert.Equal(served, Ds3UnitIdentity.Identity(ud));

            // Only a DualShock 3 is read at arrival.
            var other = new UserDevice { InstanceGuid = Guid.NewGuid(), VendorId = 0x054C, ProdId = 0x0CE6, Device = third, DevicePath = "x" };
            int reads = 0;
            Ds3UnitIdentity.NodeReader = _ => { reads++; return null; };
            Ds3UnitIdentity.Prime(other);
            Assert.Equal(0, reads);
        }
        finally
        {
            gate.Set();
            Ds3UnitIdentity.ResetForTests();
            first.Dispose();
            second.Dispose();
            third.Dispose();
        }
    }

    /// <summary>Without the pad's address a press takes only an offset any
    /// DualShock 3 could share, inside the MEMS bound, and a large bias stored
    /// against the row alone never applies.</summary>
    [Fact]
    public async Task WithoutTheAddressAPressTakesOnlyASmallOffset()
    {
        using var address = new PadAddress(null);
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(727);
        var ps = new PadSetting();
        Assert.False(await new GyroCalibratorService().RecalibrateAsync(ud, ps, 250, deliberate: true));
        Assert.Equal("0", ps.GyroBiasYaw);
        state.Gyro[1] = Ds3DirectService.YawFromWord(515);
        Assert.True(await new GyroCalibratorService().RecalibrateAsync(ud, ps, 250, deliberate: true));
        Assert.Equal(ud.InstanceGuidString, ps.GyroCalibratedDevice);
        Assert.True(GyroCalibratorService.CalibrationApplies(ud, ps));
        ps.GyroBiasYaw = "2.75";
        Assert.False(GyroCalibratorService.CalibrationApplies(ud, ps));
    }

    /// <summary>A press that starts on one pad and ends on another writes
    /// nothing.</summary>
    [Fact]
    public async Task APressThatEndsOnAnotherPadWritesNothing()
    {
        using var address = new PadAddress(PadA);
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(600);
        var ps = new PadSetting();
        var run = new GyroCalibratorService().RecalibrateAsync(ud, ps, 400, deliberate: true);
        await Task.Delay(100);
        address.Value = PadB;
        Assert.False(await run);
        Assert.Equal("", ps.GyroCalibratedDevice);
    }

    /// <summary>A stored value that isn't a number is no calibration, for the
    /// funnel and the automatic pass alike, so the pad is measured again.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("1e39")]
    public void ANonFiniteStoredBiasIsNoCalibration(string stored)
    {
        var (dualSense, _) = Pad(0x054C, 0x0CE6);
        var ps = new PadSetting { GyroBiasPitch = "0.05", GyroBiasYaw = stored, GyroCalibratedAtUtc = "2026-10-01T00:00:00Z" };
        Assert.False(GyroCalibratorService.CalibrationApplies(dualSense, ps));
        Assert.True(GyroCalibratorService.WouldCalibrate(dualSense, ps));
        Assert.Equal((0f, 0f, 0f), InputService.GyroBiasFromPadSetting(dualSense, ps));
        // A DS3 whose owner matches, where the size test doesn't run.
        using var address = new PadAddress(PadA);
        var (ds3, _) = Pad(0x054C, 0x0268);
        ps.GyroCalibratedDevice = GyroCalibratorService.CalibrationOwner(ds3);
        Assert.False(GyroCalibratorService.CalibrationApplies(ds3, ps));
        Assert.Equal((0f, 0f, 0f), InputService.GyroBiasFromPadSetting(ds3, ps));
    }

    [Theory]
    [InlineData("00265C507543", "00265c507543")]
    [InlineData("00:26:5c:50:75:43", "00265c507543")]
    [InlineData("00-26-5C-50-75-43", "00265c507543")]
    [InlineData("02265C507543", null)]
    [InlineData("000000000000", null)]
    [InlineData("00265C50754", null)]
    [InlineData("00265C50754G", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyAnAddressThatNamesOnePadCounts(string raw, string expected)
    {
        Assert.Equal(expected, Ds3UnitIdentity.Normalize(raw));
    }

    [Fact]
    public void TheUsbReplyCarriesThePadsAddressInBytesFourToNine()
    {
        var f2 = new byte[17];
        new byte[] { 0x00, 0x1F, 0xE2, 0xA1, 0xB2, 0xC3 }.CopyTo(f2, 4);
        Assert.Equal("001fe2a1b2c3", Ds3DirectService.AddressFromF2(f2));
        Assert.Null(Ds3UnitIdentity.Identity(new UserDevice()));
    }

    /// <summary>The press bound is 486 counts, so both rails stay refused:
    /// Ds3DirectService calls a word within 24 counts of either end AT-RAIL.</summary>
    [Theory]
    [InlineData(24, false)]
    [InlineData(25, false)]
    [InlineData(26, true)]
    [InlineData(998, true)]
    [InlineData(999, false)]
    [InlineData(1000, false)]
    public async Task APressStillRefusesTheRails(int word, bool accepted)
    {
        using var address = new PadAddress(PadA);
        var (ud, state) = Pad(0x054C, 0x0268);
        state.Gyro[1] = Ds3DirectService.YawFromWord(word);
        Assert.Equal(accepted, await new GyroCalibratorService().RecalibrateAsync(ud, new PadSetting(), 250, deliberate: true));
    }

    [Fact]
    public async Task APressKeepsTheMemsBoundOnOtherPadsAndOnTheDs3sOtherAxes()
    {
        var (dualSense, ds5) = Pad(0x054C, 0x0CE6);
        ds5.Gyro[1] = 0.16f;
        Assert.False(await new GyroCalibratorService().RecalibrateAsync(dualSense, new PadSetting(), 250, deliberate: true));
        var (ds3, state) = Pad(0x054C, 0x0268);
        state.Gyro[0] = 0.3f;
        Assert.False(await new GyroCalibratorService().RecalibrateAsync(ds3, new PadSetting(), 250, deliberate: true));
    }

    // ── the direct path's yaw sign ──

    /// <summary>DsHidMini measured four genuine pads whose raw word falls
    /// for a clockwise turn seen from above, so a rising word is a
    /// counter-clockwise turn, positive in SDL.</summary>
    [Fact]
    public void TheDirectPathReadsARisingWordAsPositiveYaw()
    {
        Assert.Equal(0f, Ds3DirectService.YawFromWord(512));
        Assert.True(Ds3DirectService.YawFromWord(612) > 0);
        Assert.True(Ds3DirectService.YawFromWord(412) < 0);
        Assert.Equal(90f * Deg, Ds3DirectService.YawFromWord(512 + 123), 4);
    }

    // ── the sibling set ──

    [Fact]
    public void SiblingSet_CarriesTheFields()
    {
        string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
        // Both loaders read the flag the engine's way (TryParseBoolPs).
        Assert.Contains("padVm.GyroSimulation = InputService.TryParseBoolPs(ps.GyroSimulation, false);", ss);
        Assert.Contains("ps.GyroSimulation = padVm.GyroSimulation ? \"1\" : \"0\";", ss);
        Assert.Contains("ps.GyroSimulationSmoothingMs = padVm.GyroSimulationSmoothingMs.ToString(ic);", ss);

        string svc = RepoText("PadForge.App", "Services", "InputService.cs");
        Assert.Contains("padVm.GyroSimulation = TryParseBoolPs(ps.GyroSimulation, false);", svc);
        Assert.Contains("ps.GyroSimulation = padVm.GyroSimulation ? \"1\" : \"0\";", svc);
        Assert.Contains("SimulateGyro = TryParseBoolPs(ps.GyroSimulation, false),", svc);
        Assert.Contains("SourceCoercion.SimulatedGyroProvider =", svc);
        Assert.Contains("_inputManager.ReadGyroSimulation;", svc);
        Assert.Contains("SourceCoercion.SimulatedGyroProvider = null;", svc);
        Assert.Contains("SourceCoercion.ApplySimulatedGyro(ud.InstanceGuidString, i, ref lp, ref ly, ref lr);", svc);
        // The readout puts the simulation on the sensor's axes before the
        // grip turns them, as the funnel does.
        Assert.True(svc.IndexOf("SourceCoercion.ApplySimulatedGyro(ud.InstanceGuidString, i, ref lp", StringComparison.Ordinal)
            < svc.IndexOf("SourceCoercion.ApplyMotionGrip(ud.InstanceGuidString, i, ref lp", StringComparison.Ordinal));
        Assert.Contains("AppendGyroStageTokens(parts, ps, PadForge.Engine.SimulatedGyro.MissingAxes(", svc);
        Assert.Contains("SimulationSmoothingSeconds = PadForge.Engine.SimulatedGyro.SmoothingSeconds(", svc);
        Assert.Contains("return GyroBiasFromPadSetting(FindUserDevice(g), ps);", svc);
        Assert.Contains("bool biasApplies = GyroCalibratorService.CalibrationApplies(ud, ps, bp, by, br);", svc);
        Assert.Contains("_inputManager?.ResetGyroSimulation();", svc);

        string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
        Assert.Contains("nameof(PadViewModel.GyroSimulation) or nameof(PadViewModel.GyroSimulationSmoothingMs) or", mw);
        Assert.Contains("RecalibrateAsync(ud, ps, deliberate: true)", mw);
        Assert.Contains("while (GyroCalibratorService.IsSampling(ps) && Environment.TickCount64 < waitUntil)", mw);
        Assert.Contains("_gyroData[1] = YawFromWord(gz);", RepoText("PadForge.App", "Common", "Input", "Ds3DirectService.cs"));
        Assert.Contains("ps.GyroCalibratedDevice = owner;", RepoText("PadForge.App", "Services", "GyroCalibratorService.cs"));
        Assert.Contains("GyroCalibratorService.UnitIdentityProvider = PadForge.Common.Input.Ds3UnitIdentity.Identity;", svc);
        Assert.Contains("GyroCalibratorService.UnitIdentityProvider = null;", svc);
        Assert.Contains("ps.GyroCalibratedDevice = Map(ps.GyroCalibratedDevice);", svc);
        Assert.Contains("Ds3UnitIdentity.Prime(ud);", RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
        string ds3 = RepoText("PadForge.App", "Common", "Input", "Ds3DirectService.cs");
        Assert.Contains("_padAddress = Ds3UnitIdentity.Normalize(_transport == Ds3Transport.Bluetooth", ds3);
        Assert.Contains("_padAddress = null;", ds3);
        Assert.Contains(": ReadUsbPadAddress(3));", ds3);
        Assert.Contains("_padAddress = Ds3UnitIdentity.Normalize(ReadUsbPadAddress());", ds3);

        string step2 = RepoText("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
        Assert.Contains("UpdateGyroSimulation(ud, inputDevice, newState, gyroTiltTimestamp);", step2);
        Assert.Contains("PruneGyroSimulation(devices);", step2);
        Assert.Equal(3, CountOf(step2, "InvalidateGyroSimulation(ud.InstanceGuid);"));
        Assert.Contains("DisconnectGyroSimulation(ud.InstanceGuid);", RepoText("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
        Assert.Contains("_gyroSimStates.Clear();", RepoText("PadForge.App", "Common", "Input", "InputManager.cs"));
        string runtime = RepoText("PadForge.App", "Common", "Input", "InputManager.GyroSimulation.cs");
        foreach (string banned in new[] { ".IsEmpty", ".Count", ".Keys", ".Values.ToArray", ".ToArray()" })
            Assert.DoesNotContain("_gyroSimStates" + banned, runtime);

        string vm = RepoText("PadForge.App", "ViewModels", "PadViewModel.cs");
        foreach (var k in new[] { "public bool GyroSimulation", "public double GyroSimulationSmoothingMs",
            "ResetGyroSimulationCommand", "ResetGyroSimulationSmoothingCommand", "ResetGyroSimulationCardCommand" })
            Assert.Contains(k, vm);

        string xaml = RepoText("PadForge.App", "Views", "PadPage.xaml");
        Assert.Contains("x:Name=\"GyroSimulationCard\" Style=\"{StaticResource CardBorder}\" Visibility=\"Collapsed\"", xaml);
        Assert.Contains("IsChecked=\"{Binding GyroSimulation, Mode=TwoWay}\"", xaml);
        Assert.Contains("Minimum=\"20\" Maximum=\"250\"", xaml);
        Assert.Contains("ResetGyroSimulationCardCommand", xaml);
        string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
        Assert.Contains("GyroSimulationCard.Visibility = simulatesGyro ? Visibility.Visible : Visibility.Collapsed;", page);
        Assert.Contains("simulatesGyro = PadForge.Engine.SimulatedGyro.MissingAxes(", page);

        string ps = RepoText("PadForge.Engine", "Data", "PadSetting.cs");
        Assert.Contains("sb.Append(GyroSimulation); sb.Append('|');", ps);
        Assert.Contains("sb.Append(GyroSimulationSmoothingMs); sb.Append('|');", ps);
        Assert.Contains("nameof(GyroSimulation), nameof(GyroSimulationSmoothingMs), nameof(GyroCalibratedDevice),", ps);
        Assert.Contains("sb.Append(GyroCalibratedDevice); sb.Append('|');", ps);

        var keys = new[] { "Pad_Gyro_Simulation_Header", "Pad_Gyro_Simulation_Description", "Settings_GyroSimulation",
            "Settings_GyroSimulation_Tooltip", "Pad_ResetGyroSimulation", "Settings_GyroSimulationSmoothing",
            "Settings_GyroSimulationSmoothing_Tooltip", "Pad_ResetGyroSimulationSmoothing", "Pad_Gyro_ResetSimulation_All_Tooltip" };
        string des = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
        foreach (var k in keys)
            Assert.Contains($"public string {k} => Get(\"{k}\");", des);
        foreach (var loc in new[] { "", ".de", ".es", ".fr", ".it", ".ja", ".ko", ".nl", ".pt-BR", ".zh-Hans" })
        {
            string resx = RepoText("PadForge.App", "Resources", "Strings", "Strings" + loc + ".resx");
            foreach (var k in keys)
                Assert.Contains($"<data name=\"{k}\"", resx);
        }
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static string RepoText(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
    }
}
