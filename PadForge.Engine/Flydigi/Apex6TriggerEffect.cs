using System;

namespace PadForge.Engine.Flydigi
{
    /// <summary>What a DualSense trigger effect means, for a trigger that
    /// can only vibrate (ApexSenseBridge Apex6TriggerType).</summary>
    public enum Apex6TriggerKind : byte
    {
        Off,
        /// <summary>Zone resistance: a carrier whose level follows the zone
        /// strengths.</summary>
        Feedback,
        /// <summary>A weapon's break point: one burst as the trigger
        /// crosses it.</summary>
        Weapon,
        /// <summary>Zone vibration at the effect's own frequency.</summary>
        Vibration,
        /// <summary>One of the older effect types, as the Apex 5's command
        /// for it.</summary>
        Legacy,
        /// <summary>A draw that builds, then a burst at the release.</summary>
        Bow,
        /// <summary>A pulse train or a swell while the trigger sits in a
        /// range.</summary>
        Rhythm,
    }

    /// <summary>One burst: a sine that rises, holds and falls, in samples
    /// of 1 ms (Apex6ImpactShape, its segment form left out with the
    /// weapon presets that use it).</summary>
    public struct Apex6Burst
    {
        public double Frequency;
        public int AttackSamples, HoldSamples, ReleaseSamples;

        public static Apex6Burst Default => new()
        {
            Frequency = Apex6TriggerRenderer.BurstFrequency,
            AttackSamples = 1,
            HoldSamples = 40,
            ReleaseSamples = 10,
        };

        public int TotalSamples => AttackSamples + HoldSamples + ReleaseSamples;
    }

    /// <summary>A rhythm: pulses at one or two offsets in a period, or a
    /// carrier whose level swells between two values over it
    /// (Apex6RhythmShape).</summary>
    public struct Apex6Rhythm
    {
        public Apex6Burst Pulse;
        public double LevelA, LevelB;
        public int PeriodSamples;
        public int FirstPulseOffset;
        /// <summary>-1 for none.</summary>
        public int SecondPulseOffset;
        public byte Drive;
        public byte ActiveFrom, ActiveTo;
        public bool Modulated;
    }

    /// <summary>A decoded effect (Apex6TriggerEffect).</summary>
    public sealed class Apex6TriggerEffect
    {
        public Apex6TriggerKind Kind;
        public readonly byte[] ZoneStrengths = new byte[10];
        public byte StartZone, EndZone;
        public byte Strength, Frequency, SnapStrength;
        public Apex6Rhythm Rhythm;
        public FlydigiTriggerCommand Legacy;
        /// <summary>A vibration that follows a resistance within half a
        /// second gets one burst as the trigger reaches its start, the
        /// click a game builds from the two.</summary>
        public bool Detent;

        public static readonly Apex6TriggerEffect None = new();

        public bool SameAs(Apex6TriggerEffect other)
        {
            if (other == null || Kind != other.Kind) return false;
            if (StartZone != other.StartZone || EndZone != other.EndZone || Strength != other.Strength
                || Frequency != other.Frequency || SnapStrength != other.SnapStrength || Detent != other.Detent
                || Legacy != other.Legacy)
                return false;
            if (!ZoneStrengths.AsSpan().SequenceEqual(other.ZoneStrengths)) return false;
            ref readonly var a = ref Rhythm;
            ref readonly var b = ref other.Rhythm;
            return a.LevelA == b.LevelA && a.LevelB == b.LevelB && a.PeriodSamples == b.PeriodSamples
                && a.FirstPulseOffset == b.FirstPulseOffset && a.SecondPulseOffset == b.SecondPulseOffset
                && a.Drive == b.Drive && a.ActiveFrom == b.ActiveFrom && a.ActiveTo == b.ActiveTo
                && a.Modulated == b.Modulated && a.Pulse.Frequency == b.Pulse.Frequency
                && a.Pulse.AttackSamples == b.Pulse.AttackSamples && a.Pulse.HoldSamples == b.Pulse.HoldSamples
                && a.Pulse.ReleaseSamples == b.Pulse.ReleaseSamples;
        }
    }

    /// <summary>What one trigger's renderer carries from block to block
    /// (Apex6TriggerRenderState).</summary>
    public sealed class Apex6TriggerState
    {
        public double Phase, Amplitude;
        public Apex6Burst BurstShape;
        public int TrainElapsed;
        public int BurstElapsed, BurstLength;
        public byte PreviousPosition, BurstDrive;
        public bool HasPreviousPosition, HasAmplitude, Armed = true, BurstInFrame, TrainActive, DetentPending;

        public void Reset()
        {
            Phase = 0; Amplitude = 0; BurstShape = default; TrainElapsed = 0;
            BurstElapsed = 0; BurstLength = 0; PreviousPosition = 0; BurstDrive = 0;
            HasPreviousPosition = false; HasAmplitude = false; Armed = true;
            BurstInFrame = false; TrainActive = false; DetentPending = false;
        }

        /// <summary>For a new effect on a trigger: the phase, a burst in
        /// flight and the position memory stay, so a new effect neither
        /// clicks nor forgets where the trigger was (retarget).</summary>
        public void Retarget()
        {
            bool keepAmplitude = HasAmplitude && Amplitude > 0.0;
            TrainElapsed = 0;
            BurstInFrame = false;
            TrainActive = false;
            DetentPending = false;
            HasAmplitude = keepAmplitude;
            if (!keepAmplitude) Amplitude = 0;
        }
    }

    /// <summary>
    /// DualSense trigger effects as 1 kHz waveforms for the Apex 6 Pro's
    /// trigger voice coils (#586). The triggers cannot resist, so each
    /// effect becomes a vibration that follows what the effect means: a
    /// burst where a weapon breaks, a draw that builds to a release for a
    /// bow, a carrier that follows the zone strengths, a rhythm for
    /// Galloping and Machine.
    ///
    /// <para>A port of ApexSenseBridge's Apex6TriggerEffect.cpp,
    /// decodeApex6TriggerEffect and renderApex6TriggerEffect with the
    /// constants of Apex6TriggerEffect.h, tuned there by feel on the pad.
    /// Its twelve weapon presets and its test effects belong to its own
    /// trigger test and are left out. Pure.</para>
    /// </summary>
    public static class Apex6TriggerRenderer
    {
        public const int MaximumDrive = 127;
        public const int NativeMaximumDrive = 96;
        public const double CarrierFrequency = 85.0;
        public const double BurstFrequency = 95.0;
        public const int PulseTrainMaximumFrequency = 30;
        private const int DetentBaseDrive = 56, DetentDrivePerStrength = 8, DetentMaximumDrive = 100;
        private const int DetentHoldSamples = 14, DetentReleaseSamples = 10;
        private const double DetentRearmMargin = 20.0, DetentMinimumPosition = 10.0;
        private const double ContinuousReleasePosition = 8.0;
        private const int PulseTrainBaseDrive = 32;
        private const double PulseTrainDriveStep = 19.0;
        private const int BurstMinimumDrive = 70, BurstMaximumDrive = 127, BurstFullForce = 48, ForcePerStrength = 8;
        private const double FeedbackDrivePerLevel = 8.0, VibrationDrivePerLevel = 10.0;
        private const double BowDrivePerLevel = 6.0, MachineDrivePerLevel = 12.0, GallopDrive = 64.0;
        private const int GallopHoldSamples = 20, GallopReleaseSamples = 8, GallopSlots = 8;
        private const double OnsetWidth = 13.0;
        private const int RearmMargin = 4, LockRearmMargin = 8, RhythmReleaseMargin = 6;
        private const int MaximumBurstDrive = 255;
        private const double DefaultLegacyFrequency = 85.0;

        /// <summary>How long after a resistance a vibration counts as its
        /// click (kApex6TriggerDetentFeedbackWindowMilliseconds).</summary>
        public const int DetentFeedbackWindowMs = 500;

        /// <summary>The position, of 255, where a zone starts.</summary>
        public static byte PositionFor(int zone) => (byte)((zone * 256 + 9) / 10);

        private static double AxisForStroke(int stroke) => Math.Min(255, stroke * 255 / 192);

        private static double ZoneLevel(byte[] strengths, double position)
        {
            double coordinate = position * (10.0 / 256.0);
            int zone = (int)Math.Clamp(coordinate, 0.0, 10 - 0.001);
            double level = strengths[zone];
            if (level == 0.0) return 0.0;
            double offset = coordinate - (zone + 0.5);
            if (offset < 0.0)
            {
                if (zone == 0) return level;
                double before = strengths[zone - 1];
                double edge = before > 0.0 ? (level + before) / 2.0 : 0.0;
                return edge + (level - edge) * (offset + 0.5) / 0.5;
            }
            if (zone + 1 >= 10) return level;
            double after = strengths[zone + 1];
            double edgeAfter = after > 0.0 ? (level + after) / 2.0 : 0.0;
            return level + (edgeAfter - level) * offset / 0.5;
        }

        private static double Onset(double position, double start)
            => Math.Clamp((position - start) / OnsetWidth, 0.0, 1.0);

        private static int BurstDriveForForce(int force)
        {
            if (force == 0) return 0;
            double fraction = Math.Clamp((force - (double)ForcePerStrength) / (BurstFullForce - ForcePerStrength), 0.0, 1.0);
            return (int)Math.Round(BurstMinimumDrive + (BurstMaximumDrive - BurstMinimumDrive) * fraction,
                MidpointRounding.AwayFromZero);
        }

        private static double BurstValue(in Apex6Burst shape, int index)
        {
            int attack = shape.AttackSamples, hold = shape.HoldSamples;
            double level;
            if (index < attack) level = Math.Min(1.0, (index + 1) / (double)attack);
            else if (index < attack + hold) level = 1.0;
            else if (index < shape.TotalSamples)
                level = 1.0 - (index - attack - hold + 1) / (double)shape.ReleaseSamples;
            else return 0.0;
            return Math.Sin(2.0 * Math.PI * shape.Frequency * index / 1000.0) * level;
        }

        private static sbyte DriveSample(double value)
            => (sbyte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), -MaximumDrive, MaximumDrive);

        private static void StartBurst(Apex6TriggerState state, in Apex6Burst shape, int drive)
        {
            int total = shape.TotalSamples;
            if (drive == 0 || total == 0) return;
            state.BurstShape = shape;
            state.BurstDrive = (byte)Math.Min(drive, MaximumBurstDrive);
            state.BurstElapsed = 0;
            state.BurstLength = total;
        }

        private static bool CrossedBreakPoint(Apex6TriggerState state, double position, double rearm, double trigger)
        {
            if (position <= rearm) state.Armed = true;
            if (!state.HasPreviousPosition || !state.Armed || state.PreviousPosition >= trigger || position < trigger)
                return false;
            state.Armed = false;
            return true;
        }

        private static sbyte RhythmSample(in Apex6Rhythm shape, int elapsed)
        {
            if (shape.PeriodSamples == 0) return 0;
            int cycle = elapsed % shape.PeriodSamples;
            double value = 0.0;
            if (shape.Modulated)
            {
                double mix = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * cycle / shape.PeriodSamples);
                double drive = shape.LevelA + (shape.LevelB - shape.LevelA) * mix;
                value = Math.Sin(2.0 * Math.PI * shape.Pulse.Frequency * (elapsed % 1000000) / 1000.0) * drive;
            }
            else
            {
                int total = shape.Pulse.TotalSamples;
                if (cycle >= shape.FirstPulseOffset && cycle - shape.FirstPulseOffset < total)
                    value = BurstValue(shape.Pulse, cycle - shape.FirstPulseOffset) * shape.Drive;
                if (shape.SecondPulseOffset >= 0 && cycle >= shape.SecondPulseOffset && cycle - shape.SecondPulseOffset < total)
                    value = BurstValue(shape.Pulse, cycle - shape.SecondPulseOffset) * shape.Drive;
            }
            return DriveSample(value);
        }

        private static Apex6TriggerEffect RhythmFrom(in FlydigiRhythm periodic)
        {
            var result = new Apex6TriggerEffect { Kind = Apex6TriggerKind.Rhythm };
            ref var rhythm = ref result.Rhythm;
            rhythm.Pulse = Apex6Burst.Default;
            rhythm.SecondPulseOffset = -1;
            rhythm.ActiveFrom = PositionFor(periodic.FirstZone);
            rhythm.ActiveTo = periodic.LastZone + 1 >= 10 ? (byte)255 : (byte)(PositionFor(periodic.LastZone + 1) - 1);
            if (!periodic.Machine)
            {
                int period = Math.Max(1, (int)Math.Round(1000.0 / periodic.Frequency, MidpointRounding.AwayFromZero));
                int slot = period / GallopSlots;
                rhythm.PeriodSamples = period;
                rhythm.FirstPulseOffset = periodic.FirstFoot * slot;
                rhythm.SecondPulseOffset = periodic.SecondFoot * slot;
                rhythm.Pulse.HoldSamples = GallopHoldSamples;
                rhythm.Pulse.ReleaseSamples = GallopReleaseSamples;
                rhythm.Drive = (byte)GallopDrive;
                return result;
            }
            rhythm.Modulated = true;
            rhythm.PeriodSamples = Math.Min(periodic.PeriodTenths * 100, 65535);
            rhythm.Pulse.Frequency = periodic.Frequency;
            rhythm.LevelA = periodic.AmplitudeA * MachineDrivePerLevel;
            rhythm.LevelB = periodic.AmplitudeB * MachineDrivePerLevel;
            return result;
        }

        /// <summary>The effect an 11-byte DualSense block means
        /// (decodeApex6TriggerEffect). Null for a block that is malformed
        /// or of a type with no rendering. <see cref="Apex6TriggerEffect.None"/>
        /// for a block that asks for no effect.</summary>
        public static Apex6TriggerEffect Decode(FlydigiTriggerSide side, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 11) return null;
            if (bytes[0] == 0 || bytes[0] == 0x05) return Apex6TriggerEffect.None;

            if (bytes[0] == 0x21 || bytes[0] == 0x26 || bytes[0] == 0x25 || bytes[0] == 0x22)
            {
                int mask = bytes[1] | (bytes[2] << 8);
                if ((mask & ~0x03FF) != 0) return null;
                if (mask == 0 || (bytes[0] == 0x26 && bytes[9] == 0)) return Apex6TriggerEffect.None;
                int first = 10, last = 0, active = 0;
                for (int zone = 0; zone < 10; zone++)
                {
                    if ((mask & (1 << zone)) == 0) continue;
                    first = Math.Min(first, zone);
                    last = zone;
                    active++;
                }
                var result = new Apex6TriggerEffect { StartZone = (byte)first, EndZone = (byte)last };
                if (bytes[0] == 0x25 || bytes[0] == 0x22)
                {
                    bool bow = bytes[0] == 0x22;
                    int forcePair = bytes[3] | (bytes[4] << 8);
                    if (active != 2 || first >= last || (bow && (last > 8 || (forcePair & ~0x3F) != 0))) return null;
                    result.Kind = bow ? Apex6TriggerKind.Bow : Apex6TriggerKind.Weapon;
                    result.Strength = (byte)((bytes[3] & 0x07) + 1);
                    if (bow) result.SnapStrength = (byte)(((forcePair >> 3) & 0x07) + 1);
                    return result;
                }

                uint packed = (uint)(bytes[3] | (bytes[4] << 8) | (bytes[5] << 16) | (bytes[6] << 24));
                int strongest = 0;
                for (int zone = 0; zone < 10; zone++)
                {
                    if ((mask & (1 << zone)) == 0) continue;
                    result.ZoneStrengths[zone] = (byte)(((packed >> (zone * 3)) & 0x07) + 1);
                    strongest = Math.Max(strongest, result.ZoneStrengths[zone]);
                }
                if (bytes[0] == 0x26 && bytes[9] <= PulseTrainMaximumFrequency)
                {
                    // A slow vibration is felt as separate pulses.
                    var train = new Apex6TriggerEffect { Kind = Apex6TriggerKind.Rhythm };
                    int period = Math.Max(1, (int)Math.Round(1000.0 / bytes[9], MidpointRounding.AwayFromZero));
                    var pulse = Apex6Burst.Default;
                    int fit = period * 7 / 10;
                    if (pulse.TotalSamples > fit)
                    {
                        int tail = pulse.AttackSamples + pulse.ReleaseSamples;
                        pulse.HoldSamples = fit > tail + 4 ? fit - tail : 4;
                    }
                    train.Rhythm.Pulse = pulse;
                    train.Rhythm.SecondPulseOffset = -1;
                    train.Rhythm.PeriodSamples = Math.Max(period, pulse.TotalSamples);
                    train.Rhythm.Drive = (byte)Math.Min(MaximumDrive,
                        (long)Math.Round(PulseTrainBaseDrive + PulseTrainDriveStep * (strongest - 1.0), MidpointRounding.AwayFromZero));
                    train.Rhythm.ActiveFrom = PositionFor(first);
                    train.Rhythm.ActiveTo = 255;
                    return train;
                }
                result.Kind = bytes[0] == 0x21 ? Apex6TriggerKind.Feedback : Apex6TriggerKind.Vibration;
                result.Frequency = bytes[0] == 0x26 ? bytes[9] : (byte)0;
                return result;
            }

            if ((bytes[0] == 0x23 || bytes[0] == 0x27) && FlydigiTriggerTranslator.DescribeRhythm(bytes, out var periodic))
                return RhythmFrom(periodic);

            if (!FlydigiTriggerTranslator.Translate(side, bytes, out var translated)) return null;
            if (translated.IsOff) return Apex6TriggerEffect.None;
            return new Apex6TriggerEffect { Kind = Apex6TriggerKind.Legacy, Legacy = translated };
        }

        /// <summary>Renders eight 1 ms samples of <paramref name="effect"/>
        /// with the trigger at <paramref name="position"/> of 255
        /// (renderApex6TriggerEffect). <paramref name="sampleOffset"/> is
        /// the stream's sample count, which keeps both triggers' carriers
        /// on one clock.</summary>
        public static void Render(Apex6TriggerEffect effect, byte position, Apex6TriggerState state,
            long sampleOffset, Span<sbyte> output)
        {
            double amplitude = 0.0, frequency = CarrierFrequency;
            bool continuous = false;
            state.BurstInFrame = false;
            double current = position;

            switch (effect.Kind)
            {
                case Apex6TriggerKind.Feedback:
                    amplitude = FeedbackDrivePerLevel * ZoneLevel(effect.ZoneStrengths, current);
                    continuous = true;
                    break;

                case Apex6TriggerKind.Vibration:
                    continuous = true;
                    if (effect.Detent)
                    {
                        double detentStart = Math.Max(DetentMinimumPosition, PositionFor(effect.StartZone));
                        bool installedWhilePressed = state.DetentPending && current >= DetentMinimumPosition;
                        if (installedWhilePressed) state.DetentPending = false;
                        if (CrossedBreakPoint(state, current, Math.Max(0.0, detentStart - DetentRearmMargin), detentStart)
                            || installedWhilePressed)
                        {
                            int strongest = 0;
                            foreach (byte strength in effect.ZoneStrengths) strongest = Math.Max(strongest, strength);
                            var shape = Apex6Burst.Default;
                            shape.HoldSamples = DetentHoldSamples;
                            shape.ReleaseSamples = DetentReleaseSamples;
                            StartBurst(state, shape,
                                Math.Min(DetentMaximumDrive, DetentBaseDrive + DetentDrivePerStrength * strongest));
                        }
                    }
                    if (effect.Frequency == 0) break;
                    amplitude = VibrationDrivePerLevel * ZoneLevel(effect.ZoneStrengths, current);
                    frequency = effect.Frequency;
                    break;

                case Apex6TriggerKind.Weapon:
                {
                    double start = PositionFor(effect.StartZone), end = PositionFor(effect.EndZone);
                    if (CrossedBreakPoint(state, current, start + RearmMargin, end))
                        StartBurst(state, Apex6Burst.Default, BurstDriveForForce(effect.Strength * ForcePerStrength));
                    break;
                }

                case Apex6TriggerKind.Bow:
                {
                    double start = PositionFor(effect.StartZone), end = PositionFor(effect.EndZone);
                    if (CrossedBreakPoint(state, current, start + RearmMargin, end))
                        StartBurst(state, Apex6Burst.Default, BurstDriveForForce(effect.SnapStrength * ForcePerStrength));
                    if (state.Armed && current < end && end > start)
                    {
                        double draw = Math.Clamp((current - start) / (end - start), 0.0, 1.0);
                        amplitude = effect.Strength * BowDrivePerLevel * draw;
                    }
                    break;
                }

                case Apex6TriggerKind.Rhythm:
                {
                    double from = effect.Rhythm.ActiveFrom, to = effect.Rhythm.ActiveTo;
                    bool inside = current >= from && current <= to;
                    bool staying = current + RhythmReleaseMargin >= from && current <= to + RhythmReleaseMargin;
                    if (!state.TrainActive && inside)
                    {
                        state.TrainActive = true;
                        state.TrainElapsed = 0;
                    }
                    else if (state.TrainActive && !staying) state.TrainActive = false;
                    break;
                }

                case Apex6TriggerKind.Legacy:
                {
                    var command = effect.Legacy;
                    double start = AxisForStroke(command.P0);
                    switch (command.Mode)
                    {
                        case FlydigiTriggerMode.Race:
                            amplitude = Math.Min((int)command.P1, NativeMaximumDrive) * Onset(current, start);
                            break;
                        case FlydigiTriggerMode.Break:
                        {
                            double end = Math.Max(AxisForStroke(command.P0 + command.P1), start + 1.0);
                            if (CrossedBreakPoint(state, current, start + RearmMargin, end))
                                StartBurst(state, Apex6Burst.Default, BurstDriveForForce(command.P2));
                            break;
                        }
                        case FlydigiTriggerMode.Rattle:
                        case FlydigiTriggerMode.Vibration:
                            amplitude = Math.Min((int)command.P2, NativeMaximumDrive) * Onset(current, start);
                            frequency = command.P3 == 0 ? DefaultLegacyFrequency : command.P3;
                            break;
                        case FlydigiTriggerMode.Lock:
                        {
                            double rearm = start > LockRearmMargin ? start - LockRearmMargin : 0.0;
                            if (CrossedBreakPoint(state, current, rearm, start))
                                StartBurst(state, Apex6Burst.Default, NativeMaximumDrive);
                            break;
                        }
                    }
                    break;
                }
            }

            state.PreviousPosition = position;
            state.HasPreviousPosition = true;
            if (continuous) state.Phase = (sampleOffset % 1000) * frequency / 1000.0 % 1.0;
            if (continuous && current < ContinuousReleasePosition) amplitude = 0.0;
            double target = double.IsFinite(amplitude) ? Math.Clamp(amplitude, 0.0, NativeMaximumDrive) : 0.0;
            double rampStart = state.HasAmplitude ? state.Amplitude : target;
            for (int i = 0; i < output.Length; i++)
            {
                if (state.BurstLength != 0)
                {
                    double value = BurstValue(state.BurstShape, state.BurstElapsed) * state.BurstDrive;
                    state.BurstInFrame = true;
                    if (++state.BurstElapsed >= state.BurstLength)
                    {
                        state.BurstElapsed = 0;
                        state.BurstLength = 0;
                    }
                    output[i] = DriveSample(value);
                    rampStart = 0.0;
                    continue;
                }
                if (effect.Kind == Apex6TriggerKind.Rhythm && state.TrainActive)
                {
                    output[i] = RhythmSample(effect.Rhythm, state.TrainElapsed++);
                    rampStart = 0.0;
                    continue;
                }
                double level = rampStart + (target - rampStart) * (i + 1) / output.Length;
                output[i] = DriveSample(Math.Sin(state.Phase * 2.0 * Math.PI) * level);
                state.Phase += frequency / 1000.0;
                state.Phase -= Math.Floor(state.Phase);
            }
            state.Amplitude = state.BurstInFrame ? 0.0 : target;
            state.HasAmplitude = true;
        }
    }

    /// <summary>
    /// Which trigger the Apex 6 Pro's one trigger column plays (#586). The
    /// pad drives both triggers from one signal. One trigger's effect goes
    /// to that trigger. Two different effects at once are summed and sent
    /// to both, and that holds for half a second after one falls quiet, so
    /// the pad is not told to switch triggers between two bursts.
    ///
    /// <para>ApexSenseBridge's Apex6DualArbiter under its default policy,
    /// Mix: its entry window of 3 blocks and its release of 63.</para>
    /// </summary>
    public sealed class Apex6TriggerMixer
    {
        private const long EntryWindowBlocks = 3;
        private const long ReleaseBlocks = 63;

        private long _block, _leftSeen, _rightSeen;
        private bool _episode;

        public void Reset()
        {
            _block = 0;
            _leftSeen = 0;
            _rightSeen = 0;
            _episode = false;
        }

        /// <summary>Mixes one block of both triggers into
        /// <paramref name="output"/> and answers the route for it, Mute
        /// when both are silent.</summary>
        public FlydigiApexProtocol.Apex6Route Mix(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right, Span<sbyte> output)
        {
            bool leftActive = AnyNonZero(left), rightActive = AnyNonZero(right);
            bool identical = leftActive && rightActive && left.SequenceEqual(right);
            long stamp = ++_block;
            if (leftActive) _leftSeen = stamp;
            if (rightActive) _rightSeen = stamp;
            long Since(long seen) => seen == 0 ? long.MaxValue : stamp - seen;

            if (_episode && (identical || Since(_leftSeen) > ReleaseBlocks || Since(_rightSeen) > ReleaseBlocks))
                _episode = false;
            if (!_episode && !identical && (leftActive || rightActive)
                && Since(_leftSeen) <= EntryWindowBlocks && Since(_rightSeen) <= EntryWindowBlocks)
                _episode = true;

            output.Clear();
            if (!leftActive && !rightActive) return FlydigiApexProtocol.Apex6Route.Mute;
            if (_episode && !identical)
            {
                for (int i = 0; i < output.Length; i++)
                    output[i] = (sbyte)Math.Clamp(left[i] + right[i], -Apex6TriggerRenderer.MaximumDrive, Apex6TriggerRenderer.MaximumDrive);
                return FlydigiApexProtocol.Apex6Route.Both;
            }
            if (identical)
            {
                left.CopyTo(output);
                return FlydigiApexProtocol.Apex6Route.Both;
            }
            if (rightActive && !leftActive)
            {
                right.CopyTo(output);
                return FlydigiApexProtocol.Apex6Route.Right;
            }
            left.CopyTo(output);
            return FlydigiApexProtocol.Apex6Route.Left;
        }

        private static bool AnyNonZero(ReadOnlySpan<sbyte> samples)
        {
            foreach (sbyte s in samples)
                if (s != 0) return true;
            return false;
        }
    }
}
