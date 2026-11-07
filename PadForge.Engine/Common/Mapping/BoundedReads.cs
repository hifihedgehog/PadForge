using System;
using System.Collections.Concurrent;
using System.Globalization;
using PadForge.Engine.Data;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>What a <see cref="BoundedRead"/> tests.</summary>
    public enum BoundedReadKind
    {
        /// <summary>The input is past one threshold and short of another.</summary>
        Between,

        /// <summary>A stick is past its dead zone and points within an angle
        /// of a direction.</summary>
        Sector,

        /// <summary>One stage of a two-stage trigger pull.</summary>
        Stage,
    }

    /// <summary>The two-stage trigger modes that need memory of the pull,
    /// named as DS4Windows's TwoStageTriggerMode names them. Its Normal and
    /// ExclusiveButtons modes need none: in both, ProcessTwoStageTrigger
    /// starts the soft pull engaged and holds it back only at a full pull, so
    /// the soft pull is a <see cref="BoundedReadKind.Between"/> read.</summary>
    public enum TwoStageMode
    {
        HairTrigger,
        HipFire,
        HipFireExclusive,
    }

    /// <summary>One trigger's pull through a two-stage mode, the state machine
    /// of DS4Windows's Mapping.ProcessTwoStageTrigger and its
    /// TwoStageTriggerMappingData. <see cref="BoundedReads.StepPulls"/> steps
    /// it once per poll.</summary>
    public sealed class TwoStagePull
    {
        private enum Engaged { Both, SoftOnly, FullOnly }

        /// <summary>The input manager steps a pull every poll, and every 50 ms
        /// while it idles. A longer gap means the engine stopped or the device
        /// went away, and what the pull remembers no longer holds.</summary>
        private const long GapMs = 250;

        private bool _timing;
        private long _startMs;
        private bool _active;
        private bool _fullSeen;
        private Engaged _engaged = Engaged.Both;
        private long _lastStepMs = long.MinValue;

        public bool Soft { get; private set; }
        public bool Full { get; private set; }

        public void Step(TwoStageMode mode, int waitMs, bool pulled, bool full, long nowMs)
        {
            if (_lastStepMs != long.MinValue && nowMs - _lastStepMs > GapMs) Reset();
            _lastStepMs = nowMs;
            bool soft = false, fullOut = false;

            if (mode == TwoStageMode.HairTrigger)
            {
                if (full)
                {
                    // A full pull fires both, and from here the soft pull
                    // stays out until the trigger comes all the way back.
                    soft = true;
                    fullOut = true;
                    _fullSeen = true;
                    _active = true;
                }
                else if (pulled && !_fullSeen)
                {
                    soft = true;
                    _active = true;
                }
                else if (!pulled && _active)
                {
                    Reset();
                }
            }
            else
            {
                bool exclusive = mode == TwoStageMode.HipFireExclusive;
                if (pulled && !_timing)
                {
                    Reset();
                    _timing = true;
                    _startMs = nowMs;
                }
                else if (pulled && !_active)
                {
                    if (nowMs - _startMs >= waitMs)
                    {
                        // What the trigger reads when the wait ends decides
                        // the whole pull.
                        _active = true;
                        if (full)
                        {
                            fullOut = true;
                            _engaged = Engaged.FullOnly;
                        }
                        else
                        {
                            soft = true;
                            _engaged = exclusive ? Engaged.SoftOnly : Engaged.Both;
                        }
                    }
                }
                else if (_active)
                {
                    if (!pulled)
                    {
                        Reset();
                    }
                    else
                    {
                        soft = _engaged != Engaged.FullOnly;
                        fullOut = full && _engaged != Engaged.SoftOnly;
                    }
                }
                else if (_timing)
                {
                    Reset();
                }
            }

            Soft = soft;
            Full = fullOut;
        }

        private void Reset()
        {
            _timing = false;
            _active = false;
            _fullSeen = false;
            _engaged = Engaged.Both;
        }
    }

    /// <summary>One parsed bounded read.</summary>
    public sealed class BoundedRead
    {
        public BoundedReadKind Kind { get; init; }

        /// <summary>The input it reads, as written.</summary>
        public string Inner { get; init; }

        /// <summary>Between: the half-axis read and its direction.</summary>
        public bool HalfAxis { get; init; }
        public bool Invert { get; init; }

        /// <summary>Between: the thresholds, percent.</summary>
        public int Low { get; init; }
        public int High { get; init; }

        /// <summary>Sector: degrees counterclockwise from right, so 90 is up,
        /// the bearing of DS4Windows's DS4State.calculateStickAngles.</summary>
        public double Center { get; init; }
        public double HalfWidth { get; init; }
        public int DeadZone { get; init; }

        /// <summary>Stage: the mode, which stage, the wait before a pull
        /// counts, the percent that counts as pulled, and the percent this
        /// stage's output reads as pressed.</summary>
        public TwoStageMode Mode { get; init; }
        public bool IsFull { get; init; }
        public int WaitMs { get; init; }
        public int Pull { get; init; }
        public int Press { get; init; }

        /// <summary>Stage: names the trigger's pull, the same for both
        /// stages and every press, so all of a trigger's readers share one
        /// pull as DS4Windows's bindings share one per trigger.</summary>
        internal string PullKey { get; init; }

        internal MappingSource LowSource { get; init; }
        internal MappingSource HighSource { get; init; }
        internal MappingSource PressSource { get; init; }
    }

    /// <summary>Reads that one threshold cannot say: a zone with an upper
    /// edge, a sector of a stick, one stage of a two-stage pull. Each is an
    /// ordinary descriptor, so a macro entry and a row's gate carry it as
    /// they carry any other read. The numbers live in the descriptor:
    /// <c>Between {low} {high} {half}{invert} {input}</c>,
    /// <c>Sector {center} {halfWidth} {deadZone} {stick ring}</c>,
    /// <c>Stage {mode} {Soft|Full} {waitMs} {pull} {press} {input}</c>.</summary>
    public static class BoundedReads
    {
        /// <summary>DS4Windows's full pull is a raw 255. A read past this
        /// percent is the closest an analog read comes to it.</summary>
        public const int FullPullPercent = 99;

        private const int CacheCap = 256;
        private static readonly ConcurrentDictionary<string, BoundedRead> s_cache = new();

        private sealed class PullEntry
        {
            public PullEntry(BoundedRead read)
            {
                Key = read.PullKey;
                Mode = read.Mode;
                WaitMs = read.WaitMs;
                PulledSource = read.LowSource;
                FullSource = read.HighSource;
            }

            public readonly string Key;
            public readonly TwoStageMode Mode;
            public readonly int WaitMs;
            public readonly MappingSource PulledSource;
            public readonly MappingSource FullSource;
            public readonly TwoStagePull Pull = new();
        }

        /// <summary>The pulls a read has asked for, per device. Each array is
        /// replaced whole when a pull joins it, so the poll thread walks it
        /// with no lock and no allocation.</summary>
        private static readonly ConcurrentDictionary<Guid, PullEntry[]> s_pulls = new();
        private static readonly object s_pullsLock = new();

        public static string Between(int low, int high, bool halfAxis, bool invert, string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Between {low} {high} {(halfAxis ? 1 : 0)}{(invert ? 1 : 0)} {input}");

        public static string Sector(double center, double halfWidth, int deadZone, string stickRing)
            => string.Create(CultureInfo.InvariantCulture,
                $"Sector {center} {halfWidth} {deadZone} {stickRing}");

        public static string Stage(TwoStageMode mode, bool full, int waitMs, int pull, int press, string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Stage {mode} {(full ? "Full" : "Soft")} {waitMs} {pull} {press} {input}");

        public static bool IsBounded(string descriptor)
            => descriptor != null
               && (descriptor.StartsWith("Between ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Sector ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Stage ", StringComparison.Ordinal));

        /// <summary>The parsed read, or null when the descriptor is another
        /// family or is malformed.</summary>
        public static BoundedRead Parse(string descriptor)
        {
            if (!IsBounded(descriptor)) return null;
            if (s_cache.TryGetValue(descriptor, out var hit)) return hit;
            var read = ParseUncached(descriptor);
            if (s_cache.Count < CacheCap) s_cache[descriptor] = read;
            return read;
        }

        /// <summary>The input a bounded read takes, else the descriptor
        /// itself. It is what the any-device gate tests a device for.</summary>
        public static string InnerOf(string descriptor) => Parse(descriptor)?.Inner ?? descriptor;

        private static BoundedRead ParseUncached(string d)
        {
            var c = CultureInfo.InvariantCulture;
            if (d.StartsWith("Between ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 5
                    || !int.TryParse(p[1], NumberStyles.None, c, out int low)
                    || !int.TryParse(p[2], NumberStyles.None, c, out int high)
                    || low is < 1 or > 100 || high is < 1 or > 100 || low >= high
                    || p[3].Length != 2
                    // One bounded read inside another would read itself
                    // without end on a file written to do that.
                    || IsBounded(p[4]))
                    return null;
                bool half = p[3][0] == '1', invert = p[3][1] == '1';
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Between,
                    Inner = p[4],
                    HalfAxis = half,
                    Invert = invert,
                    Low = low,
                    High = high,
                    LowSource = Source(p[4], low, half, invert),
                    HighSource = Source(p[4], high, half, invert),
                };
            }
            if (d.StartsWith("Sector ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 5
                    || !double.TryParse(p[1], NumberStyles.Float, c, out double center)
                    || !double.TryParse(p[2], NumberStyles.Float, c, out double width)
                    || !int.TryParse(p[3], NumberStyles.None, c, out int dead)
                    || !SourceCoercion.IsStickRingDescriptor(p[4]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Sector,
                    Inner = p[4],
                    Center = center,
                    HalfWidth = width,
                    DeadZone = dead,
                };
            }
            var s = d.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
            if (s.Length != 7
                || !Enum.TryParse(s[1], ignoreCase: false, out TwoStageMode mode)
                || !Enum.IsDefined(mode)
                || s[2] is not ("Soft" or "Full")
                || !int.TryParse(s[3], NumberStyles.None, c, out int wait)
                || !int.TryParse(s[4], NumberStyles.None, c, out int pull)
                || !int.TryParse(s[5], NumberStyles.None, c, out int press)
                || pull is < 1 or > 100 || press is < 1 or > 100
                || IsBounded(s[6]))
                return null;
            return new BoundedRead
            {
                Kind = BoundedReadKind.Stage,
                Inner = s[6],
                Mode = mode,
                IsFull = s[2] == "Full",
                WaitMs = wait,
                Pull = pull,
                Press = press,
                PullKey = string.Create(c, $"{mode} {wait} {pull} {s[6]}"),
                LowSource = Source(s[6], pull, false, false),
                HighSource = Source(s[6], FullPullPercent, false, false),
                PressSource = Source(s[6], press, false, false),
            };
        }

        private static MappingSource Source(string input, int percent, bool half, bool invert) => new()
        {
            Kind = "Direct",
            Descriptor = input,
            DeadZone = percent,
            HalfAxis = half,
            Invert = invert,
        };

        /// <summary>The read, on the device whose state is given.</summary>
        internal static bool Read(CustomInputState state, string descriptor, int slotIndex, string deviceGuid)
        {
            var read = Parse(descriptor);
            if (read == null) return false;
            switch (read.Kind)
            {
                case BoundedReadKind.Between:
                    return Held(state, read.LowSource, slotIndex, deviceGuid)
                           && !Held(state, read.HighSource, slotIndex, deviceGuid);
                case BoundedReadKind.Sector:
                    return SourceCoercion.ReadStickSector(state, read.Inner, read.Center, read.HalfWidth, read.DeadZone);
                default:
                    var pull = PullFor(read, deviceGuid);
                    return read.IsFull
                        ? pull.Full
                        : pull.Soft && Held(state, read.PressSource, slotIndex, deviceGuid);
            }
        }

        /// <summary>Steps every two-stage pull a read has asked for on this
        /// device. The input manager calls it once per device per poll, before
        /// any row or macro reads, so a pull sees every release whether or not
        /// anything reads it that poll, as DS4Windows steps its triggers on
        /// every report.</summary>
        public static void StepPulls(Guid device, string deviceGuid, CustomInputState state, long nowMs)
        {
            if (state == null || !s_pulls.TryGetValue(device, out var pulls)) return;
            for (int i = 0; i < pulls.Length; i++)
            {
                var e = pulls[i];
                e.Pull.Step(e.Mode, e.WaitMs,
                    Held(state, e.PulledSource, -1, deviceGuid),
                    Held(state, e.FullSource, -1, deviceGuid),
                    nowMs);
            }
        }

        private static TwoStagePull PullFor(BoundedRead read, string deviceGuid)
        {
            Guid.TryParse(deviceGuid, out Guid device);
            if (s_pulls.TryGetValue(device, out var pulls))
            {
                for (int i = 0; i < pulls.Length; i++)
                    if (string.Equals(pulls[i].Key, read.PullKey, StringComparison.Ordinal)) return pulls[i].Pull;
            }
            lock (s_pullsLock)
            {
                pulls = s_pulls.TryGetValue(device, out var current) ? current : Array.Empty<PullEntry>();
                for (int i = 0; i < pulls.Length; i++)
                    if (string.Equals(pulls[i].Key, read.PullKey, StringComparison.Ordinal)) return pulls[i].Pull;
                var grown = new PullEntry[pulls.Length + 1];
                Array.Copy(pulls, grown, pulls.Length);
                grown[pulls.Length] = new PullEntry(read);
                s_pulls[device] = grown;
                return grown[pulls.Length].Pull;
            }
        }

        private static bool Held(CustomInputState state, MappingSource src, int slotIndex, string deviceGuid)
            => SourceCoercion.EvaluateForButtonTarget(state, src, 50, slotIndex, deviceGuid);
    }
}
