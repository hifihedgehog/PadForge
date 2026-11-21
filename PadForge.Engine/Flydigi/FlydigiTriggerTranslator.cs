using System;

namespace PadForge.Engine.Flydigi
{
    /// <summary>A bow played from the PC on an Apex 5: resistance through
    /// the draw, swapped for a band that breaks once the trigger passes
    /// <see cref="RuptureAxis"/>, and the draw again once it falls back to
    /// <see cref="RearmAxis"/> (ApexSenseBridge BowEmulation).</summary>
    public readonly record struct FlydigiBow(
        FlydigiTriggerCommand Draw, FlydigiTriggerCommand Rupture, byte RuptureAxis, byte RearmAxis);

    /// <summary>A DualSense effect whose ten zones differ in strength, played
    /// from the PC one zone at a time as the trigger moves
    /// (ZoneEmulation). <see cref="Frequency"/> above 0 is a vibration.</summary>
    public sealed class FlydigiZones
    {
        public readonly byte[] Strength = new byte[10];
        public byte Frequency;
    }

    /// <summary>A Galloping or Machine effect played from the PC as a
    /// rhythm (PeriodicEmulation).</summary>
    public struct FlydigiRhythm
    {
        public bool Machine;
        public int FirstZone, LastZone;
        public int FirstFoot, SecondFoot;
        public int AmplitudeA, AmplitudeB;
        public int PeriodTenths;
        public byte Frequency;
    }

    /// <summary>The zone a trigger sits in, with a margin before it falls
    /// back to a lower one, so a trigger held at a boundary does not send
    /// an effect change on every sample (ZoneTracker).</summary>
    public struct FlydigiZoneTracker
    {
        public int Zone;

        public FlydigiZoneTracker(byte axis) => Zone = FlydigiTriggerTranslator.ZoneForAxis(axis);

        public int Update(byte axis, int hysteresis)
        {
            int raised = FlydigiTriggerTranslator.ZoneForAxis(axis);
            if (raised > Zone) Zone = raised;
            else
            {
                int lowered = FlydigiTriggerTranslator.ZoneForAxis(Math.Min(255, axis + hysteresis));
                if (lowered < Zone) Zone = lowered;
            }
            return Zone;
        }
    }

    /// <summary>
    /// A DualSense trigger effect block as the Apex 4 and Apex 5 can play
    /// it (#586): the 11 bytes a game, a game mod, a macro or the Adaptive
    /// Triggers group hands PadForge, turned into one of the pad's five
    /// modes.
    ///
    /// <para>A port of ApexSenseBridge's AdaptiveTriggerTranslation.cpp,
    /// function for function, which took its table from openflydigi's
    /// transcription of Space Station's own DualSense mode. The pad has one
    /// effect per trigger, so three kinds of block are played from the PC
    /// as the trigger moves: a bow that breaks at its end zone
    /// (<see cref="DescribeBow"/>), zones of differing strength
    /// (<see cref="DescribeZones"/>), and the Galloping and Machine rhythms
    /// (<see cref="DescribeRhythm"/>). Pure, with the clock and the
    /// trigger's position as arguments.</para>
    /// </summary>
    public static class FlydigiTriggerTranslator
    {
        public const int MaximumStroke = 192;
        public const int ZoneHysteresis = 6;
        public const int VibrationZoneHysteresis = 30;
        public const int MinimumEmulatedVibrationFrequency = 25;
        public const int MaximumGallopingFrequency = 8;
        public const int BottomTriggerPosition = 235;
        public const int MinimumMachinePeriodTenths = 4;

        // productionZoneUnits: the force and vibration a zone strength of 1
        // to 8 becomes, and the rhythm figures.
        private static readonly byte[] ForceTable = { 1, 16, 32, 64, 96, 128, 160, 192 };
        private static readonly byte[] VibrationTable = { 1, 15, 25, 35, 45, 55, 65, 80 };
        private const double GallopingBeatSlots = 0.6;
        private const double GallopingMinimumBeatSeconds = 0.15;
        private const double GallopingBottomMinimumBeatSeconds = 0.04;
        private const int MachineAmplitudeFloor = 40;
        private const int MachineBottomAmplitudeFloor = 0;
        private const byte GallopingAmplitude = 60;
        private const byte GallopingCarrier = 30;

        private static FlydigiTriggerCommand Make(FlydigiTriggerSide side, FlydigiTriggerMode mode,
            int p0 = 0, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0)
            => new(side, mode, (byte)p0, (byte)p1, (byte)p2, (byte)p3, (byte)p4);

        /// <summary>Where zone 0 to 10 starts on the pad's 0 to 192
        /// stroke.</summary>
        public static int StrokeForZone(int zone) => (zone * MaximumStroke + 5) / 10;

        private static int AxisForStroke(int stroke) => Math.Min(255, stroke * 255 / MaximumStroke);

        private static int StrokeAtOrAfterAxis(int axis) => (axis * MaximumStroke + 254) / 255;

        /// <summary>The zone, 0 to 9, a trigger at <paramref name="axis"/>
        /// of 255 sits in.</summary>
        public static int ZoneForAxis(int axis)
        {
            for (int zone = 9; zone > 0; zone--)
                if (axis >= AxisForStroke(StrokeForZone(zone))) return zone;
            return 0;
        }

        private static bool LimitedForce(byte strength, out byte force)
        {
            const int limitedMaximum = 10, nativeForceCeiling = 64;
            force = 0;
            if (strength > limitedMaximum) return false;
            force = (byte)((strength * nativeForceCeiling + limitedMaximum / 2) / limitedMaximum);
            return true;
        }

        private static void ScanZones(int mask, out int first, out int last, out int active)
        {
            first = 10;
            last = 0;
            active = 0;
            for (int zone = 0; zone < 10; zone++)
            {
                if ((mask & (1 << zone)) == 0) continue;
                first = Math.Min(first, zone);
                last = zone;
                active++;
            }
        }

        private static int Mask(ReadOnlySpan<byte> effect) => effect[1] | (effect[2] << 8);

        private static uint PackedStrengths(ReadOnlySpan<byte> effect)
            => (uint)(effect[3] | (effect[4] << 8) | (effect[5] << 16) | (effect[6] << 24));

        // Single-zone masks widen to a one-zone span, and sparse masks use
        // first to last.
        private static FlydigiTriggerCommand MakeBreak(FlydigiTriggerSide side, int first, int last, int strength)
        {
            if (first == last)
            {
                last = Math.Min(first + 1, 9);
                first = last - 1;
            }
            int start = StrokeForZone(first);
            int length = StrokeForZone(last) - start;
            return Make(side, FlydigiTriggerMode.Break, start, length, strength * 8);
        }

        private static FlydigiTriggerCommand MakeBow(FlydigiTriggerSide side, int first, int last,
            int drawStrength, int snapForce)
        {
            const int lastZone = 9, strongSnap = 7;
            int start = StrokeForZone(first);
            int resistance = drawStrength * 8;
            if (first >= last || last >= lastZone || snapForce < strongSnap)
                return Make(side, FlydigiTriggerMode.Race, start, resistance);
            return Make(side, FlydigiTriggerMode.Break, start, StrokeForZone(last) - start, resistance);
        }

        private static bool TranslateNative(FlydigiTriggerSide side, ReadOnlySpan<byte> effect, out FlydigiTriggerCommand command)
        {
            command = FlydigiTriggerCommand.Off(side);
            int mask = Mask(effect);
            if ((mask & ~0x03FF) != 0) return false;
            if (mask == 0) return true;

            ScanZones(mask, out int first, out int last, out _);
            int start = StrokeForZone(first);
            switch (effect[0])
            {
                case 0x22:
                    command = MakeBow(side, first, last, (effect[3] & 0x07) + 1, ((effect[3] >> 3) & 0x07) + 1);
                    return true;
                case 0x25:
                    command = MakeBreak(side, first, last, (effect[3] & 0x07) + 1);
                    return true;
                case 0x23:
                    if (effect[4] != 0) command = Make(side, FlydigiTriggerMode.Rattle, start, 1, 60, effect[4]);
                    return true;
                case 0x27:
                {
                    if (effect[4] == 0) return true;
                    int average = ((effect[3] & 0x07) + ((effect[3] >> 3) & 0x07)) / 2 + 1;
                    command = Make(side, FlydigiTriggerMode.Rattle, start, 1, average * 10, effect[4]);
                    return true;
                }
            }

            uint packed = PackedStrengths(effect);
            Span<int> strengths = stackalloc int[10];
            strengths.Clear();
            int peak = 0;
            for (int zone = 0; zone < 10; zone++)
            {
                if ((mask & (1 << zone)) == 0) continue;
                strengths[zone] = (int)((packed >> (zone * 3)) & 0x07) + 1;
                peak = Math.Max(peak, strengths[zone]);
            }

            if (effect[0] == 0x21)
            {
                command = Make(side, FlydigiTriggerMode.Race, start, peak * 8);
                return true;
            }

            int frequency = effect[9];
            if (frequency == 0) return true;
            // Start where the effect reaches half its peak and average from
            // there, not the peak.
            int from = first;
            while (strengths[from] * 2 < peak) from++;
            int sum = 0, count = 0;
            for (int zone = from; zone < 10; zone++)
            {
                if (strengths[zone] == 0) continue;
                sum += strengths[zone];
                count++;
            }
            int amplitude = (sum * 10 + count / 2) / count;
            if (frequency < 10) amplitude /= 2;
            command = Make(side, FlydigiTriggerMode.Rattle, StrokeForZone(from), 1, Math.Max(amplitude, 1), frequency);
            return true;
        }

        /// <summary>The pad's effect for an 11-byte DualSense block
        /// (translateAdaptiveTrigger). False for a block the table has no
        /// entry for, and the trigger then keeps what it had.</summary>
        public static bool Translate(FlydigiTriggerSide side, ReadOnlySpan<byte> effect, out FlydigiTriggerCommand command)
        {
            command = FlydigiTriggerCommand.Off(side);
            if (effect.Length < 11) return false;
            byte type = effect[0];
            if (type == 0x01)
            {
                command = Make(side, FlydigiTriggerMode.Race, effect[1], effect[2]);
                return true;
            }
            if (type == 0x11)
            {
                if (!LimitedForce(effect[2], out byte force)) return false;
                if (force != 0) command = Make(side, FlydigiTriggerMode.Race, effect[1], force);
                return true;
            }
            if (type == 0x02 || type == 0x12)
            {
                if (effect[2] < effect[1]) return false;
                int length = Math.Max(effect[2] - effect[1], 1);
                if (type == 0x02)
                {
                    command = Make(side, FlydigiTriggerMode.Break, effect[1], length, effect[3]);
                    return true;
                }
                if (!LimitedForce(effect[3], out byte force)) return false;
                if (force != 0) command = Make(side, FlydigiTriggerMode.Break, effect[1], length, force);
                return true;
            }
            if (type == 0x05) return true;
            if (type == 0x06)
            {
                command = Make(side, FlydigiTriggerMode.Rattle, effect[3], effect[2], effect[2], effect[1]);
                return true;
            }
            if ((type >= 0x21 && type <= 0x23) || type == 0x25 || type == 0x26 || type == 0x27)
                return TranslateNative(side, effect, out command);
            return false;
        }

        /// <summary>A Bow whose draw spans four zones or more, ends before
        /// the last zone and snaps at strength 7 or 8 (describeBowEmulation).</summary>
        public static bool DescribeBow(FlydigiTriggerSide side, ReadOnlySpan<byte> effect, out FlydigiBow bow)
        {
            const int lastZone = 9, strongSnap = 7, minimumDrawZones = 4, ruptureAxisPercent = 85, rearmMargin = 10;
            bow = default;
            if (effect.Length < 11 || effect[0] != 0x22) return false;
            int mask = Mask(effect);
            if (mask == 0 || (mask & ~0x03FF) != 0) return false;
            ScanZones(mask, out int first, out int last, out _);
            int drawStrength = (effect[3] & 0x07) + 1;
            int snapForce = ((effect[3] >> 3) & 0x07) + 1;
            if (first >= last || last >= lastZone || snapForce < strongSnap || last - first < minimumDrawZones)
                return false;
            int start = StrokeForZone(first);
            int end = StrokeForZone(last);
            int resistance = drawStrength * 8;
            var draw = Make(side, FlydigiTriggerMode.Race, start, resistance);
            byte ruptureAxis = (byte)(AxisForStroke(end) * ruptureAxisPercent / 100);
            int ruptureStart = Math.Clamp(StrokeAtOrAfterAxis(ruptureAxis), start, end - 1);
            var rupture = Make(side, FlydigiTriggerMode.Break, ruptureStart, end - ruptureStart, resistance);
            int startAxis = AxisForStroke(start);
            byte rearmAxis = (byte)(startAxis > 2 * rearmMargin ? startAxis - rearmMargin : rearmMargin);
            bow = new FlydigiBow(draw, rupture, ruptureAxis, rearmAxis);
            return true;
        }

        /// <summary>A Feedback (0x21) or Vibration (0x26) block whose zones
        /// differ in strength, or stop before the end of the pull
        /// (describeZoneEmulation). A vibration slower than 25 Hz is left
        /// to the static table.</summary>
        public static bool DescribeZones(ReadOnlySpan<byte> effect, out FlydigiZones zones)
        {
            const int lastZone = 9;
            zones = null;
            if (effect.Length < 11 || (effect[0] != 0x21 && effect[0] != 0x26)) return false;
            bool vibration = effect[0] == 0x26;
            if (vibration && effect[9] < MinimumEmulatedVibrationFrequency) return false;
            int mask = Mask(effect);
            if (mask == 0 || (mask & ~0x03FF) != 0) return false;
            ScanZones(mask, out int first, out int last, out int active);
            uint packed = PackedStrengths(effect);
            var result = new FlydigiZones { Frequency = vibration ? effect[9] : (byte)0 };
            bool graded = false;
            for (int zone = 0; zone < 10; zone++)
            {
                if ((mask & (1 << zone)) == 0) continue;
                result.Strength[zone] = (byte)(((packed >> (zone * 3)) & 0x07) + 1);
                graded |= result.Strength[zone] != result.Strength[first];
            }
            bool contiguous = active == last - first + 1;
            if (!graded && contiguous && last == lastZone) return false;
            zones = result;
            return true;
        }

        /// <summary>The effect for the zone the trigger sits in
        /// (renderZoneCommand).</summary>
        public static FlydigiTriggerCommand RenderZone(FlydigiTriggerSide side, FlydigiZones zones, int zone)
        {
            const int startMargin = 5;
            int target = Math.Min(zone, 9);
            while (target < 10 && zones.Strength[target] == 0) target++;
            if (target == 10) return FlydigiTriggerCommand.Off(side);
            int strength = zones.Strength[target];
            int runStart = target;
            if (zones.Frequency != 0)
                while (runStart > 0 && zones.Strength[runStart - 1] != 0) runStart--;
            else
                while (runStart > 0 && zones.Strength[runStart - 1] == strength) runStart--;
            int boundary = StrokeForZone(runStart);
            int start = boundary > startMargin ? boundary - startMargin : 0;
            if (zones.Frequency != 0)
            {
                int amplitude = VibrationTable[(strength - 1) % VibrationTable.Length];
                return Make(side, FlydigiTriggerMode.Rattle, start, 1, Math.Clamp(amplitude, 1, 255), zones.Frequency);
            }
            return Make(side, FlydigiTriggerMode.Race, start, ForceTable[(strength - 1) % ForceTable.Length]);
        }

        /// <summary>A Galloping block slow enough to play beat by beat, or
        /// a Machine block whose two amplitudes differ
        /// (describePeriodicEmulation).</summary>
        public static bool DescribeRhythm(ReadOnlySpan<byte> effect, out FlydigiRhythm rhythm)
        {
            rhythm = default;
            if (effect.Length < 11 || (effect[0] != 0x23 && effect[0] != 0x27)) return false;
            int mask = Mask(effect);
            if (mask == 0 || (mask & ~0x03FF) != 0) return false;
            ScanZones(mask, out int first, out int last, out _);
            if (first >= last || effect[4] == 0) return false;
            rhythm.FirstZone = first;
            rhythm.LastZone = last;
            rhythm.Frequency = effect[4];
            if (effect[0] == 0x23)
            {
                rhythm.SecondFoot = effect[3] & 0x07;
                rhythm.FirstFoot = (effect[3] >> 3) & 0x07;
                return rhythm.FirstFoot < rhythm.SecondFoot && effect[4] <= MaximumGallopingFrequency;
            }
            rhythm.Machine = true;
            rhythm.AmplitudeA = effect[3] & 0x07;
            rhythm.AmplitudeB = (effect[3] >> 3) & 0x07;
            rhythm.PeriodTenths = effect[5];
            return rhythm.AmplitudeA != rhythm.AmplitudeB && rhythm.PeriodTenths >= MinimumMachinePeriodTenths;
        }

        /// <summary>The rhythm's effect <paramref name="seconds"/> into it,
        /// with the trigger at <paramref name="position"/> of 255
        /// (renderPeriodicCommand with periodicUnitsForPosition).</summary>
        public static FlydigiTriggerCommand RenderRhythm(FlydigiTriggerSide side, in FlydigiRhythm rhythm,
            double seconds, bool inRange, int position)
        {
            const int startMargin = 5, cycleSlots = 8;
            const double silenceLevel = 0.35;
            if (!inRange || seconds < 0.0) return FlydigiTriggerCommand.Off(side);
            bool bottom = position >= BottomTriggerPosition;
            int boundary = StrokeForZone(rhythm.FirstZone);
            int start = boundary > startMargin ? boundary - startMargin : 0;
            if (!rhythm.Machine)
            {
                double cycleSeconds = 1.0 / rhythm.Frequency;
                double slotSeconds = cycleSeconds / cycleSlots;
                double cycle = seconds * rhythm.Frequency;
                double at = (cycle - Math.Floor(cycle)) * cycleSeconds;
                double width = Math.Max(GallopingBeatSlots * slotSeconds,
                    bottom ? GallopingBottomMinimumBeatSeconds : GallopingMinimumBeatSeconds);
                bool Beat(int foot)
                {
                    double begin = foot * slotSeconds;
                    return (at >= begin && at < begin + width)
                        || (at + cycleSeconds >= begin && at + cycleSeconds < begin + width);
                }
                if (!Beat(rhythm.FirstFoot) && !Beat(rhythm.SecondFoot)) return FlydigiTriggerCommand.Off(side);
                return Make(side, FlydigiTriggerMode.Rattle, start, 1, GallopingAmplitude, GallopingCarrier);
            }
            double period = rhythm.PeriodTenths / 10.0;
            double phase = seconds / period - Math.Floor(seconds / period);
            double mix = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * phase);
            double level = rhythm.AmplitudeA + (rhythm.AmplitudeB - (double)rhythm.AmplitudeA) * mix;
            if (level < silenceLevel) return FlydigiTriggerCommand.Off(side);
            int lower = Math.Min((int)level, 7);
            int upper = Math.Min(lower + 1, 7);
            double amplitude = VibrationTable[lower] + (VibrationTable[upper] - VibrationTable[lower]) * (level - lower);
            long rounded = Math.Max((long)Math.Round(amplitude, MidpointRounding.AwayFromZero),
                bottom ? MachineBottomAmplitudeFloor : MachineAmplitudeFloor);
            return Make(side, FlydigiTriggerMode.Rattle, start, 1, (int)Math.Clamp(rounded, 1, 255), rhythm.Frequency);
        }
    }
}
