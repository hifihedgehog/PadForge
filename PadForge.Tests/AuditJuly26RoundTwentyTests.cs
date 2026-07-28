using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twenty.
    ///
    /// <para>Picked by measurement rather than hunch: a sweep of every
    /// Engine type over 12 KB against its reference count in the test
    /// project put ForceFeedbackState at 48 KB with ZERO test references,
    /// the largest genuinely pure untested type left. Round fourteen had
    /// already proved this subsystem harbours real defects, and unlike
    /// PrecisionTouchpadReader it had never been hardened by a prior
    /// audit.</para>
    ///
    /// <para>ONE DEFECT FOUND AND FIXED: the periodic-waveform clock used
    /// Environment.TickCount, a 32-bit counter that wraps NEGATIVE after
    /// 24.9 days of uptime. C# takes the sign of the dividend through %,
    /// so the phase became (-1, 0] on any long-running machine while every
    /// waveform assumes [0, 1). Triangle and both sawtooths then returned
    /// up to 3.0 instead of 1.0, tripling the steering force until the
    /// caller's clamp saturated it, and square stopped alternating and
    /// held +1, turning a buzz into a steady pull. Only a reboot cleared
    /// it. Fixed by moving both clock sites to TickCount64.</para></summary>
    public class AuditJuly26RoundTwentyTests
    {
        // ── Waveform shapes ──────────────────────────────────────────

        [Theory]
        [InlineData(0.00, 1.0)]
        [InlineData(0.25, 1.0)]
        [InlineData(0.50, -1.0)]
        [InlineData(0.75, -1.0)]
        public void Square(double phase, double expected)
            => Assert.Equal(expected, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.Square, phase), 6);

        [Theory]
        [InlineData(0.00, 0.0)]
        [InlineData(0.25, 1.0)]
        [InlineData(0.50, 0.0)]
        [InlineData(0.75, -1.0)]
        public void Sine(double phase, double expected)
            => Assert.Equal(expected, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.Sine, phase), 6);

        [Theory]
        [InlineData(0.00, 1.0)]
        [InlineData(0.25, 0.0)]
        [InlineData(0.50, -1.0)]
        [InlineData(0.75, 0.0)]
        public void Triangle(double phase, double expected)
            => Assert.Equal(expected, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.Triangle, phase), 6);

        [Theory]
        [InlineData(0.00, -1.0)]
        [InlineData(0.50, 0.0)]
        [InlineData(1.00, 1.0)]
        public void SawUp(double phase, double expected)
            => Assert.Equal(expected, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.SawUp, phase), 6);

        [Theory]
        [InlineData(0.00, 1.0)]
        [InlineData(0.50, 0.0)]
        [InlineData(1.00, -1.0)]
        public void SawDown(double phase, double expected)
            => Assert.Equal(expected, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.SawDown, phase), 6);

        /// <summary>THE CONTRACT, and note its exact scope: every waveform is
        /// a -1..+1 multiplier across the DEFINED phase domain, [0, 1], which
        /// is what this sweeps. The caller multiplies the gain-scaled peak by
        /// this, so a value outside the range is directly a force larger than
        /// the effect asked for.
        ///
        /// <para>It said "across the whole phase domain" and swept only [0, 1],
        /// which its own neighbour below disproves: the function has no phase
        /// normalization, so an out-of-domain input leaves the range. Those two
        /// tests contradicted each other, and the overclaim is the one that was
        /// wrong. The function is NOT hardened here on purpose. Since the
        /// caller moved to TickCount64 no reachable input is out of domain, and
        /// changing working code for an unreachable hazard is how audit rounds
        /// inject regressions. The guard belongs here only if a caller ever
        /// gains the ability to produce one.</para></summary>
        [Theory]
        [InlineData(FfbEffectTypes.Square)]
        [InlineData(FfbEffectTypes.Sine)]
        [InlineData(FfbEffectTypes.Triangle)]
        [InlineData(FfbEffectTypes.SawUp)]
        [InlineData(FfbEffectTypes.SawDown)]
        public void EveryWaveformStaysInRange_AcrossThePhaseDomain(uint effectType)
        {
            for (int i = 0; i <= 1000; i++)
            {
                double phase = i / 1000.0;
                double v = ForceFeedbackState.PeriodicWaveformAtPhase(effectType, phase);
                Assert.InRange(v, -1.0, 1.0);
            }
        }

        /// <summary>WHY THE FIX MATTERS, expressed as arithmetic rather
        /// than as a claim. This is the phase the wrapped 32-bit clock
        /// produced: negative, because C# takes the sign of the dividend.
        /// Triangle triples its output, and both sawtooths leave the range
        /// in opposite directions. TickCount64 cannot reach these inputs,
        /// which is the entire point of the change.</summary>
        [Fact]
        public void NegativePhase_BreaksTheRange_WhichIsWhatTheWrapCaused()
        {
            const double wrapped = -0.5;   // e.g. ticks -1050 % period 100

            Assert.Equal(3.0, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.Triangle, wrapped), 6);
            Assert.Equal(-2.0, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.SawUp, wrapped), 6);
            Assert.Equal(2.0, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.SawDown, wrapped), 6);
            // Square stops alternating: every negative phase is below 0.5.
            Assert.Equal(1.0, ForceFeedbackState.PeriodicWaveformAtPhase(
                FfbEffectTypes.Square, wrapped), 6);
        }

        // ── Effect-type classification ───────────────────────────────

        /// <summary>Only the four periodic families are periodic. Constant
        /// and ramp keep a steady peak, and the condition effects are not
        /// waveforms at all, so misclassifying either would apply
        /// oscillation to a force meant to be steady.</summary>
        [Theory]
        [InlineData(FfbEffectTypes.Square, true)]
        [InlineData(FfbEffectTypes.Sine, true)]
        [InlineData(FfbEffectTypes.Triangle, true)]
        [InlineData(FfbEffectTypes.SawUp, true)]
        [InlineData(FfbEffectTypes.SawDown, true)]
        [InlineData(FfbEffectTypes.None, false)]
        [InlineData(FfbEffectTypes.Const, false)]
        [InlineData(FfbEffectTypes.Ramp, false)]
        [InlineData(FfbEffectTypes.Spring, false)]
        [InlineData(FfbEffectTypes.Damper, false)]
        [InlineData(FfbEffectTypes.Inertia, false)]
        [InlineData(FfbEffectTypes.Friction, false)]
        public void IsPeriodicEffect_ClassifiesEveryType(uint effectType, bool expected)
            => Assert.Equal(expected, ForceFeedbackState.IsPeriodicEffect(effectType));

        // ── Steering projection ──────────────────────────────────────

        private static Vibration Directional(short mag, ushort direction, byte gain = 255)
            => new Vibration
            {
                HasDirectionalData = true,
                SignedMagnitude = mag,
                Direction = direction,
                DeviceGain = gain,
            };

        [Fact]
        public void NoDirectionalData_ProjectsToZero()
        {
            Assert.Equal(0, ForceFeedbackState.ComputeWheelSteeringPeak(new Vibration(), 100));
            Assert.Equal(0, ForceFeedbackState.ComputeWheelSteeringPeak(null, 100));
        }

        /// <summary>Direction is the HID polar convention: 0 is North, and
        /// the wheel is the East-West axis, so a due-North force projects
        /// to nothing on the steering axis while due-East is full scale.
        /// Direction 8192 of 32767 is a quarter turn.</summary>
        [Fact]
        public void SteeringProjectionFollowsThePolarConvention()
        {
            short north = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 0), 100);
            short east = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 100);
            short west = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 24576), 100);

            Assert.InRange(north, -50, 50);          // ~0
            Assert.InRange(east, 32000, 32767);      // ~full positive
            Assert.InRange(west, -32767, -32000);    // ~full negative
        }

        /// <summary>Overall gain scales linearly and zero gain silences the
        /// effect entirely.</summary>
        [Fact]
        public void OverallGainScalesTheProjection()
        {
            short full = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 100);
            short half = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 50);
            short none = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 0);

            Assert.Equal(0, none);
            Assert.InRange(half, (short)(full / 2 - 200), (short)(full / 2 + 200));
        }

        /// <summary>Gain above 100 is clamped rather than amplifying, so a
        /// bad config cannot drive the wheel past full scale.</summary>
        [Fact]
        public void OverallGainIsClampedAtOneHundred()
        {
            short at100 = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 100);
            short at999 = ForceFeedbackState.ComputeWheelSteeringPeak(Directional(10000, 8192), 999);
            Assert.Equal(at100, at999);
        }

        // ── Rumble translation ───────────────────────────────────────

        [Fact]
        public void NoRumble_TranslatesToZero()
        {
            Assert.Equal(0, ForceFeedbackState.ComputeWheelRumbleLevel(new Vibration(), 100));
            Assert.Equal(0, ForceFeedbackState.ComputeWheelRumbleLevel(null, 100));
        }

        /// <summary>The impulse-trigger motors count as rumble: Xbox racing
        /// titles route engine and road feel through them, and a wheel with
        /// no motors must still feel it.</summary>
        [Fact]
        public void TriggerMotorsAloneStillProduceRumble()
        {
            var v = new Vibration { LeftTriggerMotorSpeed = 40000, DeviceGain = 255 };
            // Sample across WALL-CLOCK time, not loop iterations. The level is
            // mag * sin(phase) with phase = TickCount64 % 120 / 120, and
            // TickCount64 does not advance during a tight 400-iteration loop:
            // every iteration saw ONE tick, so whenever that tick landed on a
            // sine zero crossing (% 120 == 0 or 60, about 1.7% of ticks) all
            // 400 samples truncated to 0 and the test failed. That is the
            // intermittent this suite carried since round 20; the production
            // code is right, a zero at an exact zero crossing is what a sine
            // does (named by TRX hammer, round 34).
            bool nonZeroSeen = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!nonZeroSeen && sw.ElapsedMilliseconds < 500)
            {
                if (ForceFeedbackState.ComputeWheelRumbleLevel(v, 100) != 0) nonZeroSeen = true;
                else System.Threading.Thread.Sleep(1);
            }
            Assert.True(nonZeroSeen, "trigger-only rumble never produced a wheel level");
        }

        [Fact]
        public void ZeroOverallGain_SilencesRumble()
        {
            var v = new Vibration { LeftMotorSpeed = 65535, RightMotorSpeed = 65535, DeviceGain = 255 };
            Assert.Equal(0, ForceFeedbackState.ComputeWheelRumbleLevel(v, 0));
        }
    }
}
