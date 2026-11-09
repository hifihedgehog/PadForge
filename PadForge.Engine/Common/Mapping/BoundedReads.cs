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
        /// of a direction. The dead zone can be an ellipse, and the read can
        /// stop at a second, outer edge.</summary>
        Sector,

        /// <summary>One stage of a two-stage trigger pull.</summary>
        Stage,

        /// <summary>A direction of Steam's dpad: the position is past a radius
        /// and points within an angle of the direction, each with Steam's
        /// hold-back (steamclient64.dll's dpad mode).</summary>
        Wedge,

        /// <summary>A cell of Steam's four_buttons: the position is within a
        /// radius of the cell's center, with Steam's hold-back.</summary>
        Disc,

        /// <summary>A direction of Steam's dpad cross gate: the position is
        /// past a radius and its component toward the direction is past a
        /// threshold, each with Steam's hold-back.</summary>
        Hold,

        /// <summary>An edge of one of Steam's modes: the position past a
        /// radius (or, inverted, off center and inside it), with Steam's
        /// hold-back.</summary>
        Ring,

        /// <summary>One value past a threshold with Steam's hold-back: a
        /// trigger's pull, or one component of a two-axis position.</summary>
        Level,

        /// <summary>The Soft Pull of Steam's trigger mode, through its
        /// Threshold Trigger Style.</summary>
        Pull,

        /// <summary>Steam's Soft Press activator, through its style.</summary>
        Press,
    }

    /// <summary>The two-stage trigger modes that need memory of the pull,
    /// named as DS4Windows's TwoStageTriggerMode names them. Its Normal and
    /// ExclusiveButtons modes need none. In Normal, ProcessTwoStageTrigger
    /// fires each stage on its own. In ExclusiveButtons it holds the soft
    /// pull back only at a full pull, so that soft pull is a
    /// <see cref="BoundedReadKind.Between"/> read.</summary>
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

        /// <summary>Sector: the dead zone's height when it is an ellipse
        /// (0 = a circle of <see cref="DeadZone"/>), and the outer edge the
        /// read stops at, across and up and down (0 = open).</summary>
        public int DeadZoneY { get; init; }
        public int OuterX { get; init; }
        public int OuterY { get; init; }

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

        /// <summary>Wedge: the angle a direction keeps after it presses and
        /// loses before it does, degrees.</summary>
        public double Margin { get; init; }

        /// <summary>Wedge and Hold: the radius the position passes to engage
        /// and the one it stays past while engaged, percent of full travel.
        /// Disc: the distance from the center a cell presses within, and the
        /// one it releases past. Hold: the component's two thresholds are
        /// <see cref="ComponentPress"/> and <see cref="ComponentRelease"/>.</summary>
        public double PressRadius { get; init; }
        public double ReleaseRadius { get; init; }

        /// <summary>Disc: the cell's center, percent of full travel, +Y down.</summary>
        public double CenterX { get; init; }
        public double CenterY { get; init; }

        /// <summary>Hold: the component read (0 X, 1 Y), its sign, and its two
        /// thresholds, percent.</summary>
        public int ComponentAxis { get; init; }
        public int ComponentSign { get; init; }
        public double ComponentPress { get; init; }
        public double ComponentRelease { get; init; }

        /// <summary>Wedge, Disc and Hold: what gates the read. Click needs the
        /// touchpad clicked. Engaged reads the gyro at rest while the slot's
        /// gyro is off.</summary>
        public BoundedGate Gate { get; init; }

        /// <summary>Wedge and Hold: the key of the radius latch, shared by the
        /// directions of one dpad. Ring (inverted): the key of its dpad's.</summary>
        internal string EngageKey { get; init; }

        /// <summary>Ring, Level and Press: true for an inverted read, held
        /// while the value is above zero and inside the threshold. Ring: its
        /// dpad's engage radii, percent, when it lets go with its dpad.</summary>
        public bool InnerEdge { get; init; }
        public double EngagePress { get; init; }
        public double EngageRelease { get; init; }
        public bool HasEngage { get; init; }

        /// <summary>Ring: how long a change holds, milliseconds (the
        /// trigger's debounce).</summary>
        public double DebounceMs { get; init; }

        /// <summary>Pull and Press: the style (0 Simple, 1 Hair Trigger, 2 to
        /// 4 Hip Fire, 5 Hip Fire Exclusive), the threshold on Steam's 0 to
        /// 32767 scale, and whether the full pull takes part. Level and Press
        /// read a component of a pair (<see cref="ComponentAxis"/> 0 or 1),
        /// the pair's length (2), or a one-way input (-1).</summary>
        public int Style { get; init; }
        public double Threshold { get; init; }
        public bool ClickBound { get; init; }

        /// <summary>Press: the coordinator's cancel sources (steamclient64.dll
        /// 0x138668df0). The sentinel is a Full Press or Chord sibling's own
        /// read, the sibling threshold the lowest Soft Press threshold above
        /// this one's, and the Exclusive runs on the same value with its own
        /// fields. All zero or null when the activator's interruptable
        /// setting is off, since that gates the whole cancel (0x13862d451).</summary>
        internal MappingSource SentinelSource { get; init; }
        public double SiblingThreshold { get; init; }
        public bool HasExclusive { get; init; }
        public double ExclusiveThreshold { get; init; }
        public bool ExclusiveInner { get; init; }
        public bool ExclusiveClick { get; init; }
        public double ExclusiveSibling { get; init; }

        /// <summary>Pull and Press: names the machine both outputs share.</summary>
        internal string MachineKey { get; init; }
        internal MappingSource ValueSource { get; init; }
    }

    /// <summary>One of Steam's threshold styles run on a hold-back trigger
    /// (steamclient64.dll 0x1386698d0): the trigger mode's Soft Pull
    /// (CTriggerMode, update 0x1388de540) or the Soft Press activator
    /// (CSoftPressActivator, 0x13862d240). <see cref="BoundedReads.StepPulls"/>
    /// steps it once per poll.</summary>
    internal sealed class SteamThreshold
    {
        private const long GapMs = 250;
        private const double Epsilon = 1.1920929e-7;

        /// <summary>The Hair Trigger averages the last six updates. Steam
        /// updates a mode once per controller report, every 4 ms on the USB
        /// pads, so the average takes one sample each 4 ms.</summary>
        private const long SampleMs = 4;
        private const int Samples = 6;

        private readonly int _style;
        private readonly double _threshold;
        private readonly double _holdBack;
        private readonly double _range;
        private readonly double _delta;
        private readonly bool _inner;
        private readonly bool _click;
        private readonly bool _hairClick;
        private readonly bool _press;
        private readonly double _sibling;
        private readonly SteamThreshold _exclusive;
        private readonly long _windowMs;

        private double _t;
        private double _pct;
        private double _rangeNow;
        private bool _active;
        private long _pendingMs = long.MaxValue;
        private readonly double[] _samples = new double[Samples];
        private int _count;
        private int _next;
        private double _prevAvg;
        private bool _storedClick;
        private long _nextSampleMs = long.MinValue;
        private long _lastStepMs = long.MinValue;

        public bool Soft { get; private set; }
        public bool Full { get; private set; }

        /// <param name="style">0 Simple, 1 Hair Trigger, 2 to 4 Hip Fire
        /// (40, 70, 100 ms), 5 Hip Fire Exclusive (60 ms).</param>
        /// <param name="threshold">The threshold, 0 to 32767.</param>
        /// <param name="holdBack">The hold-back, percent of the range.</param>
        /// <param name="range">The trigger's range (its T stays inside it).</param>
        /// <param name="delta">How far the Hair Trigger's threshold sits
        /// above the average once the pull lets off.</param>
        /// <param name="inner">Inverted: held while above zero and inside.</param>
        /// <param name="click">The full pull cancels a waiting press, and
        /// under Hip Fire Exclusive gives way to a held one. On a Soft Press
        /// it says the step's click is a pressed Full Press or Chord
        /// sibling.</param>
        /// <param name="hairClick">The trigger mode's Hair Trigger moves its
        /// threshold with the full pull's click.</param>
        /// <param name="press">A Soft Press activator: the coordinator's
        /// cancels run before the fire, as the client orders them
        /// (0x13862d451 before 0x13862d524).</param>
        /// <param name="sibling">The lowest sibling Soft Press threshold
        /// above this one's, 0 for none: the value crossing it cancels the
        /// wait (0x138668ed4).</param>
        /// <param name="exclusive">An Exclusive sibling on the same value:
        /// fired, it cancels the wait (0x13862a580 hands the coordinator
        /// 0x7fffffff).</param>
        public SteamThreshold(int style, double threshold, double holdBack, double range, double delta,
            bool inner, bool click, bool hairClick, bool press = false, double sibling = 0,
            SteamThreshold exclusive = null)
        {
            _style = style;
            _threshold = threshold;
            _holdBack = holdBack;
            _range = range;
            _delta = delta;
            _inner = inner;
            _click = click;
            _hairClick = hairClick;
            _press = press;
            _sibling = sibling;
            _exclusive = exclusive;
            _windowMs = BoundedReads.HipFireWindowMs(style);
            Reset();
        }

        private void Reset()
        {
            _t = _threshold;
            _pct = _holdBack;
            _rangeNow = _range;
            _active = false;
            _pendingMs = long.MaxValue;
            _count = 0;
            _next = 0;
            _prevAvg = 0;
            _storedClick = false;
            _nextSampleMs = long.MinValue;
            Soft = false;
            Full = false;
        }

        /// <summary>One update. <paramref name="value"/> is the trigger's
        /// input (curved for a Soft Press), <paramref name="raw"/> the input
        /// before the curve, both on Steam's 0 to 32767 scale.</summary>
        public void Step(double value, double raw, bool click, long nowMs)
        {
            if (_lastStepMs != long.MinValue && (nowMs - _lastStepMs > GapMs || nowMs < _lastStepMs)) Reset();
            _lastStepMs = nowMs;
            // The Exclusive sibling tracks the same value on every update,
            // whatever this activator is doing.
            _exclusive?.Step(value, raw, click, nowMs);

            bool was = _active;
            double h = Math.Abs(_rangeNow * _pct) / 100.0;
            if (_inner) h = -h;
            double t = _t;
            if (t - h < 0) t = h + Epsilon;
            if (h + t > _rangeNow) t = _rangeNow - h - Epsilon;
            double edge = was ? t - h : t + h;
            _active = _inner ? value > 0 && edge > value : value > edge;

            // A press waits out the Hip Fire window, and a release drops
            // whatever waits.
            if (_active && !was) _pendingMs = nowMs + _windowMs;
            if (!_active && was)
            {
                _pendingMs = long.MaxValue;
                Soft = false;
            }
            else if (_press)
            {
                // The coordinator's cancel (0x13862d451), run before the
                // fire test (0x13862d524): a pressed Full Press or Chord
                // sibling (the click here, through its own read) or a fired
                // Exclusive sibling hands a threshold of 1 (0x138668f35),
                // else the lowest sibling Soft Press threshold above this
                // one's (0x138668ed4), and the live value crossing it drops
                // the wait. An inverted press flips the compare
                // (0x13862d4c4) and gets no sibling threshold, since the
                // inverted walk never flags one (0x138668ee7).
                bool sentinel = (_click && click) || (_exclusive != null && _exclusive.Soft);
                bool cancel = sentinel && Crossed(1, value);
                if (!cancel && _sibling > 0) cancel = Crossed(_sibling, value);
                if (cancel) _pendingMs = long.MaxValue;
            }
            if (nowMs >= _pendingMs)
            {
                _pendingMs = long.MaxValue;
                Soft = true;
            }

            if (_style == 1 && nowMs >= _nextSampleMs)
            {
                _nextSampleMs = nowMs + SampleMs;
                Hair(value, raw, click);
            }

            bool full = click;
            if (Soft && _style == 5) full = false;
            else if (!_press && _click && click) _pendingMs = long.MaxValue;
            Full = full;
        }

        /// <summary>The coordinator's compare (0x13862d4a6, and 0x13862d4c4
        /// flipped for an inverted press): the live value past the cancel
        /// threshold.</summary>
        private bool Crossed(double threshold, double value)
            => _inner ? threshold > value : value > threshold;

        /// <summary>The Hair Trigger (0x1388de7eb, 0x13862d54f): letting off
        /// the pull by more than the delta between two updates moves the
        /// threshold to just above the average with a quarter of the
        /// hold-back, so the press lets go and a small pull presses again. A
        /// pull back to the threshold restores it. In the trigger mode the
        /// click drops the threshold to the bottom while it is held, and
        /// lifts it out of reach when it lets go.</summary>
        private void Hair(double value, double raw, bool click)
        {
            _samples[_next] = value;
            _next = (_next + 1) % Samples;
            if (_count < Samples) _count++;
            double sum = 0;
            for (int i = 0; i < _count; i++) sum += _samples[i];
            double avg = sum / _count;
            if (avg > _threshold && _prevAvg > avg + _delta)
            {
                _rangeNow = _range;
                _t = avg + _delta;
                _pct = _holdBack * 0.25;
            }
            else if (_hairClick && click != _storedClick)
            {
                if (!click)
                {
                    _rangeNow = 100000;
                    _t = 100000;
                }
                else
                {
                    _t = 0;
                }
                _storedClick = click;
            }
            else if (raw <= _threshold && _prevAvg >= _threshold)
            {
                _rangeNow = _range;
                _t = _threshold;
                _pct = _holdBack;
            }
            _prevAvg = avg;
        }
    }

    /// <summary>What gates a Wedge, Disc, Hold, Ring or Level read.</summary>
    public enum BoundedGate
    {
        None,
        Click,
        Engaged,
    }

    /// <summary>Reads that one threshold cannot say: a zone with an upper
    /// edge, a sector of a stick, one stage of a two-stage pull. Each is an
    /// ordinary descriptor, so a macro entry and a row's gate carry it as
    /// they carry any other read. The numbers live in the descriptor:
    /// <c>Between {low} {high} {half}{invert} {input}</c>,
    /// <c>Sector {center} {halfWidth} {deadZone} {stick ring}</c>, where the
    /// dead zone is <c>x</c> or <c>x:y</c> for an ellipse, with
    /// <c>~x</c> or <c>~x:y</c> after it for an outer edge,
    /// <c>Stage {mode} {Soft|Full} {waitMs} {pull} {press} {input}</c>.
    /// Steam's dpad and four_buttons read a two-axis position (a stick ring, a
    /// touchpad finger ring, or <c>Gyro Tilt {inner} {range}</c>) through
    /// triggers that hold back:
    /// <c>Wedge {center} {halfWidth} {margin} {press} {release} {gate} {pair}</c>,
    /// <c>Disc {x} {y} {press} {release} {gate} {pair}</c> and
    /// <c>Hold {press} {release} {engagePress} {engageRelease} {component} {gate} {pair}</c>,
    /// where the gate is <c>-</c>, <c>click</c> or <c>engaged</c> and the
    /// component is <c>X+</c>, <c>X-</c>, <c>Y+</c> or <c>Y-</c>. An edge of
    /// a Steam mode is
    /// <c>Ring {press} {release} {out|in} {debounceMs} {engagePress|-} {engageRelease|-} {gate} {pair}</c>.
    /// One value through a hold-back trigger is
    /// <c>Level {press} {release} {out|in} {component} {gate} {input}</c>,
    /// where the component is one of the four above, <c>len</c> for the
    /// pair's length, or <c>-</c> for a one-way input such as a trigger.
    /// Steam's trigger Soft Pull is
    /// <c>Pull {style} {Soft|Full} {threshold} {click|-} {input}</c> and its
    /// Soft Press activator
    /// <c>Press {style} {Soft|Full} {threshold} {out|in} {click|-} {component} {input}</c>,
    /// the threshold on Steam's 0 to 32767 scale.</summary>
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

        /// <summary>One of Steam's hold-back triggers as a Wedge, Disc or Hold
        /// read keeps it: its state and the device poll that last set it.</summary>
        private sealed class Latch
        {
            public bool On;
            public long Poll = long.MinValue;

            /// <summary>The engage latch holds a change until this timestamp
            /// (Stopwatch ticks), Steam's 1/120 s debounce.</summary>
            public long Until;

            /// <summary>Ring (inverted): whether its dpad was engaged when the
            /// edge last read it.</summary>
            public bool WasEngaged;
        }

        private const int LatchCap = 8192;
        private static readonly ConcurrentDictionary<(Guid Device, int Slot, string Key), Latch> s_latches = new();

        /// <summary>Each device's poll count, advanced by <see cref="StepPulls"/>
        /// once per poll. A latch last set before the previous poll belonged
        /// to a read that stopped running (its layer left, its row went
        /// away), and Steam resets a mode's triggers when the mode comes back,
        /// so the latch reads as reset.</summary>
        private static readonly ConcurrentDictionary<Guid, long> s_polls = new();

        /// <summary>The latch for a read on a device and slot, reset when it
        /// went stale, or null when the read has no device to keep it on (a
        /// preview), which then reads from the reset state every time.</summary>
        private static Latch LatchFor(string deviceGuid, int slotIndex, string key, out long poll)
        {
            poll = 0;
            if (!Guid.TryParse(deviceGuid, out Guid device) || device == Guid.Empty) return null;
            poll = s_polls.TryGetValue(device, out long p) ? p : 0;
            if (s_latches.Count >= LatchCap) s_latches.Clear();
            var latch = s_latches.GetOrAdd((device, slotIndex, key), _ => new Latch());
            if (latch.Poll < poll - 1)
            {
                latch.On = false;
                latch.Until = 0;
                latch.WasEngaged = false;
            }
            return latch;
        }

        private static void Set(Latch latch, bool on, long poll)
        {
            if (latch == null) return;
            latch.On = on;
            latch.Poll = poll;
        }

        public static string Between(int low, int high, bool halfAxis, bool invert, string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Between {low} {high} {(halfAxis ? 1 : 0)}{(invert ? 1 : 0)} {input}");

        public static string Sector(double center, double halfWidth, int deadZone, string stickRing)
            => string.Create(CultureInfo.InvariantCulture,
                $"Sector {center} {halfWidth} {deadZone} {stickRing}");

        /// <summary>A sector between two ellipses: past the inner one, x
        /// across and y up and down, and short of the outer one (outerX 0 =
        /// open). Equal radii write the plain circle form.</summary>
        public static string Sector(double center, double halfWidth, int innerX, int innerY, int outerX, int outerY, string stickRing)
        {
            var c = CultureInfo.InvariantCulture;
            string Radius(int x, int y) => y == x ? x.ToString(c) : string.Create(c, $"{x}:{y}");
            string radius = Radius(innerX, innerY) + (outerX > 0 ? "~" + Radius(outerX, outerY) : "");
            return string.Create(c, $"Sector {center} {halfWidth} {radius} {stickRing}");
        }

        public static string Stage(TwoStageMode mode, bool full, int waitMs, int pull, int press, string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Stage {mode} {(full ? "Full" : "Soft")} {waitMs} {pull} {press} {input}");

        public static string Wedge(double center, double halfWidth, double margin, double press, double release,
            BoundedGate gate, string pair)
            => string.Create(CultureInfo.InvariantCulture,
                $"Wedge {N(center)} {N(halfWidth)} {N(margin)} {N(press)} {N(release)} {GateToken(gate)} {pair}");

        public static string Disc(double x, double y, double press, double release, BoundedGate gate, string pair)
            => string.Create(CultureInfo.InvariantCulture,
                $"Disc {N(x)} {N(y)} {N(press)} {N(release)} {GateToken(gate)} {pair}");

        public static string Hold(double press, double release, double engagePress, double engageRelease,
            int axis, int sign, BoundedGate gate, string pair)
            => string.Create(CultureInfo.InvariantCulture,
                $"Hold {N(press)} {N(release)} {N(engagePress)} {N(engageRelease)} {(axis == 0 ? "X" : "Y")}{(sign < 0 ? "-" : "+")} {GateToken(gate)} {pair}");

        /// <summary>An edge of a Steam mode. Outer: pressed past the press
        /// radius, held while past the release radius. Inner (inverted):
        /// pressed off center inside the press radius, held while off center
        /// inside the release radius. A change holds for the debounce. With
        /// engage radii, an inner edge lets go when its dpad disengages.</summary>
        public static string Ring(double press, double release, bool inner, double debounceMs, BoundedGate gate,
            string pair, double? engagePress = null, double? engageRelease = null)
            => string.Create(CultureInfo.InvariantCulture,
                $"Ring {N(press)} {N(release)} {(inner ? "in" : "out")} {N(debounceMs)} {(engagePress is double ep ? N(ep) : "-")} {(engageRelease is double er ? N(er) : "-")} {GateToken(gate)} {pair}");

        /// <summary>One value through a hold-back trigger: pressed past the
        /// press threshold and held while past the release threshold, percent
        /// of full travel. Inverted, it presses above zero inside the press
        /// threshold and holds above zero inside the release threshold.</summary>
        public static string Level(double press, double release, bool inner, string component, BoundedGate gate,
            string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Level {N(press)} {N(release)} {(inner ? "in" : "out")} {component} {GateToken(gate)} {input}");

        /// <summary>The Soft Pull of Steam's trigger mode (Soft) or its click
        /// under Hip Fire Exclusive (Full): edge_binding_radius as the
        /// threshold, adaptive_threshold as the style, and whether the group
        /// binds the click.</summary>
        public static string Pull(int style, bool full, double threshold, bool click, string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Pull {style} {(full ? "Full" : "Soft")} {N(threshold)} {(click ? "click" : "-")} {input}");

        /// <summary>Steam's Soft Press activator with its coordinator cancels
        /// (steamclient64.dll 0x13862d451 and 0x138668df0), which run only
        /// while the activator's interruptable setting is on, so the importer
        /// writes <paramref name="cancel"/> as "-" when it is off. The tokens
        /// are space-joined in any order. <c>fp{read}</c> embeds a Full Press
        /// or Chord sibling's own read (pressed, it hands the coordinator
        /// 0x7ffffffe, a cancel threshold of 1). <c>sib{raw}</c> is the lowest
        /// sibling Soft Press threshold above this one's, which the live value
        /// crossing cancels. <c>ex{thr}:{in|out}:{c|-}:{sib}</c> is an
        /// Exclusive sibling, run on the same value (fired, it hands
        /// 0x7fffffff), with its own inversion, whether the fp sentinel
        /// cancels it too, and its own sibling threshold.</summary>
        public static string Press(int style, bool full, double threshold, bool inner, string cancel, string component,
            string input)
            => string.Create(CultureInfo.InvariantCulture,
                $"Press {style} {(full ? "Full" : "Soft")} {N(threshold)} {(inner ? "in" : "out")} {(string.IsNullOrEmpty(cancel) ? "-" : cancel)} {component} {input}");

        /// <summary>A number as a descriptor writes it: four decimals at most,
        /// so the string a translation writes is the string it reads back.</summary>
        private static string N(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        private static string GateToken(BoundedGate gate) => gate switch
        {
            BoundedGate.Click => "click",
            BoundedGate.Engaged => "engaged",
            _ => "-",
        };

        private static bool TryGate(string token, out BoundedGate gate)
        {
            gate = token switch
            {
                "-" => BoundedGate.None,
                "click" => BoundedGate.Click,
                "engaged" => BoundedGate.Engaged,
                _ => (BoundedGate)(-1),
            };
            return (int)gate >= 0;
        }

        public static bool IsBounded(string descriptor)
            => descriptor != null
               && (descriptor.StartsWith("Between ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Sector ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Stage ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Wedge ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Disc ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Hold ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Ring ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Level ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Pull ", StringComparison.Ordinal)
                   || descriptor.StartsWith("Press ", StringComparison.Ordinal));

        /// <summary>The parsed read, or null when the descriptor is another
        /// family or is malformed.</summary>
        public static BoundedRead Parse(string descriptor)
        {
            if (!IsBounded(descriptor)) return null;
            if (s_cache.TryGetValue(descriptor, out var hit)) return hit;
            var read = ParseUncached(descriptor);
            // Full, the cache starts over: a long session's imported profiles
            // keep adding descriptors, and the ones in use come back in on
            // their next read instead of parsing on every poll.
            if (s_cache.Count >= CacheCap) s_cache.Clear();
            s_cache[descriptor] = read;
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
                    || !TryRadii(p[3], out int dead, out int deadY, out int outerX, out int outerY)
                    || !SourceCoercion.IsStickRingDescriptor(p[4]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Sector,
                    Inner = p[4],
                    Center = center,
                    HalfWidth = width,
                    DeadZone = dead,
                    DeadZoneY = deadY,
                    OuterX = outerX,
                    OuterY = outerY,
                };
            }
            if (d.StartsWith("Wedge ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 8, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 8
                    || !Num(p[1], out double center) || !Num(p[2], out double width) || !Num(p[3], out double margin)
                    || !Num(p[4], out double pressR) || !Num(p[5], out double releaseR)
                    || width <= 0 || margin < 0 || releaseR > pressR
                    || !TryGate(p[6], out var gate) || !SourceCoercion.IsPairDescriptor(p[7]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Wedge,
                    Inner = p[7],
                    Center = center,
                    HalfWidth = width,
                    Margin = margin,
                    PressRadius = pressR,
                    ReleaseRadius = releaseR,
                    Gate = gate,
                    EngageKey = string.Create(c, $"{p[4]} {p[5]} {p[6]} {p[7]}"),
                };
            }
            if (d.StartsWith("Disc ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 7
                    || !Num(p[1], out double x) || !Num(p[2], out double y)
                    || !Num(p[3], out double pressR) || !Num(p[4], out double releaseR)
                    || pressR > releaseR
                    || !TryGate(p[5], out var gate) || !SourceCoercion.IsPairDescriptor(p[6]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Disc,
                    Inner = p[6],
                    CenterX = x,
                    CenterY = y,
                    PressRadius = pressR,
                    ReleaseRadius = releaseR,
                    Gate = gate,
                };
            }
            if (d.StartsWith("Hold ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 8, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 8
                    || !Num(p[1], out double pressR) || !Num(p[2], out double releaseR)
                    || !Num(p[3], out double engagePress) || !Num(p[4], out double engageRelease)
                    || releaseR > pressR || engageRelease > engagePress
                    || p[5].Length != 2 || p[5][0] is not ('X' or 'Y') || p[5][1] is not ('+' or '-')
                    || !TryGate(p[6], out var gate) || !SourceCoercion.IsPairDescriptor(p[7]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Hold,
                    Inner = p[7],
                    ComponentAxis = p[5][0] == 'X' ? 0 : 1,
                    ComponentSign = p[5][1] == '-' ? -1 : 1,
                    ComponentPress = pressR,
                    ComponentRelease = releaseR,
                    PressRadius = engagePress,
                    ReleaseRadius = engageRelease,
                    Gate = gate,
                    EngageKey = string.Create(c, $"{p[3]} {p[4]} {p[6]} {p[7]}"),
                };
            }
            if (d.StartsWith("Ring ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
                bool inner = p.Length == 9 && p[3] == "in";
                bool engage = p.Length == 9 && (p[5] != "-" || p[6] != "-");
                double engagePress = 0, engageRelease = 0;
                if (p.Length != 9
                    || !Num(p[1], out double pressR) || !Num(p[2], out double releaseR)
                    || p[3] is not ("in" or "out")
                    || (inner ? pressR > releaseR : releaseR > pressR)
                    || !Num(p[4], out double debounce) || debounce < 0 || debounce > 1000
                    || (engage && (!Num(p[5], out engagePress) || !Num(p[6], out engageRelease)
                        || engageRelease > engagePress))
                    || !TryGate(p[7], out var gate) || !SourceCoercion.IsPairDescriptor(p[8]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Ring,
                    Inner = p[8],
                    PressRadius = pressR,
                    ReleaseRadius = releaseR,
                    InnerEdge = inner,
                    DebounceMs = debounce,
                    HasEngage = engage,
                    EngagePress = engagePress,
                    EngageRelease = engageRelease,
                    Gate = gate,
                    EngageKey = engage ? string.Create(c, $"{p[5]} {p[6]} {p[7]} {p[8]}") : null,
                };
            }
            if (d.StartsWith("Level ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 7, StringSplitOptions.RemoveEmptyEntries);
                bool inner = p.Length == 7 && p[3] == "in";
                if (p.Length != 7
                    || !Num(p[1], out double pressR) || !Num(p[2], out double releaseR)
                    || p[3] is not ("in" or "out")
                    || (inner ? pressR > releaseR : releaseR > pressR)
                    || !TryComponent(p[4], p[6], out int axis, out int sign)
                    || !TryGate(p[5], out var gate) || gate == BoundedGate.Click)
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Level,
                    Inner = p[6],
                    PressRadius = pressR,
                    ReleaseRadius = releaseR,
                    InnerEdge = inner,
                    ComponentAxis = axis,
                    ComponentSign = sign,
                    Gate = gate,
                    ValueSource = axis < 0 ? new MappingSource { Descriptor = p[6] } : null,
                };
            }
            if (d.StartsWith("Pull ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 6
                    || !int.TryParse(p[1], NumberStyles.None, c, out int style) || style > 5
                    || p[2] is not ("Soft" or "Full")
                    || !Num(p[3], out double threshold) || threshold < 0 || threshold > 32767
                    || p[4] is not ("click" or "-")
                    || IsBounded(p[5]) || SourceCoercion.IsPairDescriptor(p[5]))
                    return null;
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Pull,
                    Inner = p[5],
                    Style = style,
                    IsFull = p[2] == "Full",
                    Threshold = threshold,
                    ClickBound = p[4] == "click",
                    ComponentAxis = -1,
                    MachineKey = string.Create(c, $"Pull {p[1]} {p[3]} {p[4]} {p[5]}"),
                    ValueSource = new MappingSource { Descriptor = p[5] },
                };
            }
            if (d.StartsWith("Press ", StringComparison.Ordinal))
            {
                var p = d.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 6
                    || !int.TryParse(p[1], NumberStyles.None, c, out int style) || style > 5
                    || p[2] is not ("Soft" or "Full")
                    || !Num(p[3], out double threshold) || threshold < 0 || threshold > 32767
                    || p[4] is not ("in" or "out"))
                    return null;
                // The cancel slot: "-", the legacy "click", or any of
                // fp{read} (spaces inside the braces), sib{raw} and
                // ex{thr}:{in|out}:{c|-}:{sib}, then the component and the
                // input.
                string rest = p[5];
                string sentinel = null;
                bool legacyClick = false;
                double sibling = 0, exThr = 0, exSibling = 0;
                bool hasEx = false, exInner = false, exClick = false;
                int cancels = 0;
                while (true)
                {
                    rest = rest.TrimStart(' ');
                    if (rest.StartsWith("fp{", StringComparison.Ordinal))
                    {
                        int close = rest.IndexOf('}');
                        if (close < 0 || sentinel != null) return null;
                        sentinel = rest[3..close];
                        // The sentinel is a whole read of its own. A Press
                        // inside it would step this machine from inside its
                        // own step.
                        if (!IsBounded(sentinel) || sentinel.StartsWith("Press ", StringComparison.Ordinal))
                            return null;
                        rest = rest[(close + 1)..];
                        cancels++;
                        continue;
                    }
                    int sp = rest.IndexOf(' ');
                    if (sp < 0) return null;
                    string tok = rest[..sp];
                    if (tok.StartsWith("sib", StringComparison.Ordinal))
                    {
                        if (sibling > 0 || !Num(tok[3..], out sibling) || sibling <= 0 || sibling > 32767)
                            return null;
                        cancels++;
                    }
                    else if (tok.StartsWith("ex", StringComparison.Ordinal))
                    {
                        var ex = tok[2..].Split(':');
                        if (hasEx || ex.Length != 4
                            || !Num(ex[0], out exThr) || exThr <= 0 || exThr > 32767
                            || ex[1] is not ("in" or "out")
                            || ex[2] is not ("c" or "-")
                            || !Num(ex[3], out exSibling) || exSibling < 0 || exSibling > 32767)
                            return null;
                        hasEx = true;
                        exInner = ex[1] == "in";
                        exClick = ex[2] == "c";
                        cancels++;
                    }
                    else if (cancels == 0 && tok is "click" or "-")
                    {
                        legacyClick = tok == "click";
                        cancels = -1;
                    }
                    else break;
                    rest = rest[(sp + 1)..];
                    if (cancels == -1) break;
                }
                var tail = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (tail.Length != 2
                    || !TryComponent(tail[0], tail[1], out int axis, out int sign)
                    // The full pull belongs to a trigger: a pair has none.
                    || (legacyClick && axis >= 0))
                    return null;
                string cancel = legacyClick ? "click"
                    : cancels <= 0 ? "-"
                    : string.Join(' ', new[]
                    {
                        sentinel != null ? "fp{" + sentinel + "}" : null,
                        sibling > 0 ? "sib" + N(sibling) : null,
                        hasEx
                            ? string.Create(c, $"ex{N(exThr)}:{(exInner ? "in" : "out")}:{(exClick ? "c" : "-")}:{N(exSibling)}")
                            : null,
                    }.Where(t => t != null));
                return new BoundedRead
                {
                    Kind = BoundedReadKind.Press,
                    Inner = tail[1],
                    Style = style,
                    IsFull = p[2] == "Full",
                    Threshold = threshold,
                    InnerEdge = p[4] == "in",
                    ClickBound = legacyClick,
                    SentinelSource = sentinel != null ? new MappingSource { Descriptor = sentinel } : null,
                    SiblingThreshold = sibling,
                    HasExclusive = hasEx,
                    ExclusiveThreshold = exThr,
                    ExclusiveInner = exInner,
                    ExclusiveClick = exClick,
                    ExclusiveSibling = exSibling,
                    ComponentAxis = axis,
                    ComponentSign = sign,
                    MachineKey = string.Create(c, $"Press {p[1]} {p[3]} {p[4]} {cancel} {tail[0]} {tail[1]}"),
                    ValueSource = axis < 0 ? new MappingSource { Descriptor = tail[1] } : null,
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

        private static bool Num(string token, out double value)
            => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);

        /// <summary>A component token and the input it reads: <c>X+</c>,
        /// <c>X-</c>, <c>Y+</c> or <c>Y-</c> of a pair (axis 0 or 1),
        /// <c>len</c> for the pair's length (axis 2), or <c>-</c> for a one-way
        /// input (axis -1), which may be neither a pair nor another bounded
        /// read.</summary>
        private static bool TryComponent(string token, string input, out int axis, out int sign)
        {
            axis = -1;
            sign = 1;
            if (token == "-") return input.Length > 0 && !IsBounded(input) && !SourceCoercion.IsPairDescriptor(input);
            if (!SourceCoercion.IsPairDescriptor(input)) return false;
            if (token == "len")
            {
                axis = 2;
                return true;
            }
            if (token.Length != 2 || token[0] is not ('X' or 'Y') || token[1] is not ('+' or '-')) return false;
            axis = token[0] == 'X' ? 0 : 1;
            sign = token[1] == '-' ? -1 : 1;
            return true;
        }

        /// <summary>Steam's full pull on the controllers it reads as generic
        /// gamepads (Xbox, PlayStation and the rest): the pull at 31128 of
        /// 32767 or past it (steamclient64.dll 0x1386795c4), 95 percent.</summary>
        public const double FullPullRaw = 31128;

        /// <summary>How long a Hip Fire style waits before its press, in
        /// milliseconds: 40, 70 and 100 for styles 2 to 4 ((style - 2) * 0.03 +
        /// 0.04 s) and 60 for Hip Fire Exclusive, as both the trigger mode
        /// (0x1388de37c) and the Soft Press activator (0x1386281f9) set it.
        /// Simple and Hair Trigger press at once.</summary>
        public static int HipFireWindowMs(int style) => style switch
        {
            2 => 40,
            3 => 70,
            4 => 100,
            5 => 60,
            _ => 0,
        };

        /// <summary>A sector's radius token: <c>x</c>, <c>x:y</c>, and an
        /// optional <c>~x</c> or <c>~x:y</c> outer edge, each 0 to 100. A
        /// height equal to the width reads as a circle (0).</summary>
        private static bool TryRadii(string token, out int x, out int y, out int outerX, out int outerY)
        {
            x = y = outerX = outerY = 0;
            var c = CultureInfo.InvariantCulture;
            string[] edges = token.Split('~');
            if (edges.Length > 2) return false;
            static bool Pair(string s, CultureInfo c, out int a, out int b)
            {
                b = 0;
                string[] parts = s.Split(':');
                if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, c, out a) || a > 100) { a = 0; return false; }
                if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, c, out b) || b > 100)) return false;
                if (b == a) b = 0;
                return true;
            }
            if (!Pair(edges[0], c, out x, out y)) return false;
            if (edges.Length == 2 && (!Pair(edges[1], c, out outerX, out outerY) || outerX == 0)) return false;
            return true;
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
                    return SourceCoercion.ReadStickSector(state, read.Inner, read.Center, read.HalfWidth, read.DeadZone,
                        read.DeadZoneY, read.OuterX, read.OuterY);
                case BoundedReadKind.Wedge:
                    return ReadWedge(state, read, descriptor, slotIndex, deviceGuid);
                case BoundedReadKind.Disc:
                    return ReadDisc(state, read, descriptor, slotIndex, deviceGuid);
                case BoundedReadKind.Hold:
                    return ReadHold(state, read, descriptor, slotIndex, deviceGuid);
                case BoundedReadKind.Ring:
                    return ReadRing(state, read, descriptor, slotIndex, deviceGuid);
                case BoundedReadKind.Level:
                    return ReadLevel(state, read, descriptor, slotIndex, deviceGuid);
                case BoundedReadKind.Pull:
                case BoundedReadKind.Press:
                    var machine = MachineFor(read, deviceGuid);
                    return read.IsFull ? machine.Full : machine.Soft;
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
            s_polls.AddOrUpdate(device, 1, static (_, n) => n + 1);
            if (state == null) return;
            if (s_pulls.TryGetValue(device, out var pulls))
            {
                for (int i = 0; i < pulls.Length; i++)
                {
                    var e = pulls[i];
                    e.Pull.Step(e.Mode, e.WaitMs,
                        Held(state, e.PulledSource, -1, deviceGuid),
                        Held(state, e.FullSource, -1, deviceGuid),
                        nowMs);
                }
            }
            if (s_machines.TryGetValue(device, out var machines))
            {
                for (int i = 0; i < machines.Length; i++) StepMachine(machines[i], state, deviceGuid, nowMs);
            }
        }

        private sealed class MachineEntry
        {
            public MachineEntry(BoundedRead read)
            {
                Key = read.MachineKey;
                Press = read.Kind == BoundedReadKind.Press;
                Input = read.Inner;
                ValueSource = read.ValueSource;
                SentinelSource = read.SentinelSource;
                ClickBound = read.ClickBound;
                ComponentAxis = read.ComponentAxis;
                ComponentSign = read.ComponentSign;
                Machine = Press
                    // The Soft Press activator (0x138627c50): 5 percent of
                    // activation_threshold's 32255 maximum as the hold-back,
                    // and a Hair Trigger delta of 150.
                    ? new SteamThreshold(read.Style, read.Threshold, 5, 32255, 150, read.InnerEdge,
                        read.ClickBound || read.SentinelSource != null, hairClick: false, press: true,
                        sibling: read.SiblingThreshold,
                        exclusive: read.HasExclusive
                            ? new SteamThreshold(5, read.ExclusiveThreshold, 5, 32255, 150, read.ExclusiveInner,
                                read.ExclusiveClick, hairClick: false, press: true, sibling: read.ExclusiveSibling)
                            : null)
                    // The trigger mode (0x1388de2c6): 7.5 percent of 32767,
                    // and a Hair Trigger delta of a third of it (0x1388de2f1).
                    : new SteamThreshold(read.Style, read.Threshold, 7.5, 32767, 7.5 / 3.0 * 32767.0 / 100.0, false,
                        read.ClickBound, hairClick: true);
            }

            public readonly string Key;
            public readonly bool Press;
            public readonly string Input;
            public readonly MappingSource ValueSource;
            public readonly MappingSource SentinelSource;
            public readonly bool ClickBound;
            public readonly int ComponentAxis;
            public readonly int ComponentSign;
            public readonly SteamThreshold Machine;
        }

        /// <summary>The threshold machines a read has asked for, per device,
        /// kept as <see cref="s_pulls"/> keeps the two-stage pulls.</summary>
        private static readonly ConcurrentDictionary<Guid, MachineEntry[]> s_machines = new();

        /// <summary>One update of a machine: the input on Steam's 0 to 32767
        /// scale, the full pull at 31128 or past it, and for a Soft Press a pull
        /// or a component curved by a power of 0.75 (0x13862d2f9, kinds below 8
        /// of the activator's table at 0x1392dc500), a length read straight.</summary>
        private static void StepMachine(MachineEntry e, CustomInputState state, string deviceGuid, long nowMs)
        {
            double raw;
            if (e.ComponentAxis < 0)
            {
                raw = SourceCoercion.EvaluateForTriggerTarget(state, e.ValueSource, -1, deviceGuid);
            }
            else
            {
                SourceCoercion.TryReadPair(state, e.Input, -1, deviceGuid, out double x, out double y);
                raw = e.ComponentAxis == 2
                    ? Math.Sqrt(x * x + y * y)
                    : e.ComponentSign * (e.ComponentAxis == 0 ? x : y);
            }
            raw = Math.Clamp(raw, 0, 1) * 32767.0;
            double value = e.Press && e.ComponentAxis != 2 ? Math.Pow(raw / 32767.0, 0.75) * 32767.0 : raw;
            // A Soft Press's click is a pressed Full Press or Chord sibling:
            // its own read where the importer embedded one, else the raw
            // full pull under the legacy click token. The trigger mode's
            // Pull keeps the raw full pull.
            bool click = e.Press
                ? e.SentinelSource != null
                    ? Held(state, e.SentinelSource, -1, deviceGuid)
                    : e.ClickBound && e.ComponentAxis < 0 && raw >= FullPullRaw
                : e.ComponentAxis < 0 && raw >= FullPullRaw;
            e.Machine.Step(value, raw, click, nowMs);
        }

        private static SteamThreshold MachineFor(BoundedRead read, string deviceGuid)
        {
            Guid.TryParse(deviceGuid, out Guid device);
            if (s_machines.TryGetValue(device, out var machines))
            {
                for (int i = 0; i < machines.Length; i++)
                    if (string.Equals(machines[i].Key, read.MachineKey, StringComparison.Ordinal)) return machines[i].Machine;
            }
            lock (s_pullsLock)
            {
                machines = s_machines.TryGetValue(device, out var current) ? current : Array.Empty<MachineEntry>();
                for (int i = 0; i < machines.Length; i++)
                    if (string.Equals(machines[i].Key, read.MachineKey, StringComparison.Ordinal)) return machines[i].Machine;
                var grown = new MachineEntry[machines.Length + 1];
                Array.Copy(machines, grown, machines.Length);
                grown[machines.Length] = new MachineEntry(read);
                s_machines[device] = grown;
                return grown[machines.Length].Machine;
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

        /// <summary>The read's position in percent of full travel, +Y down. A
        /// pair with no reading, and the gyro while the slot's gyro is off,
        /// read at rest, as Steam's dpad reads them.</summary>
        private static (double X, double Y) Position(CustomInputState state, BoundedRead read, int slotIndex, string deviceGuid)
        {
            if (read.Gate == BoundedGate.Engaged && slotIndex >= 0
                && !(SourceCoercion.AimEngageStateProvider?.Invoke(slotIndex) ?? true))
                return (0, 0);
            if (!SourceCoercion.TryReadPair(state, read.Inner, slotIndex, deviceGuid, out double x, out double y))
                return (0, 0);
            return (x * 100.0, y * 100.0);
        }

        /// <summary>Steam's dpad engage trigger (steamclient64.dll 0x1386698d0,
        /// seeded with the dead zone at 0x1388b23dd): once past the press
        /// radius the position stays engaged while it is past the release
        /// radius, and a change holds for 1/120 s (the trigger's debounce,
        /// set at 0x1388b2451). One latch per dpad, which every direction of
        /// it reads.</summary>
        private static bool Engaged(BoundedRead read, double length, string deviceGuid, int slotIndex)
            => Engaged(read.EngageKey, read.PressRadius, read.ReleaseRadius, length, deviceGuid, slotIndex);

        private static bool Engaged(string key, double press, double release, double length, string deviceGuid, int slotIndex)
        {
            var latch = LatchFor(deviceGuid, slotIndex, key, out long poll);
            bool was = latch?.On ?? false;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (latch != null && now < latch.Until)
            {
                Set(latch, was, poll);
                return was;
            }
            bool on = was ? length > release : length > press;
            if (latch != null && on != was) latch.Until = now + System.Diagnostics.Stopwatch.Frequency / 120;
            Set(latch, on, poll);
            return on;
        }

        /// <summary>An edge of a Steam mode: its own trigger, stepped with the
        /// position's length every update and taking no click. The dpad's
        /// (0x1388b319e) has 10 percent of hold-back and the 1/120 s debounce,
        /// the joystick modes' (0x1388c8f34) 3 percent and none, the flick
        /// stick's (0x1388b4c93) 10 percent and none. An outer edge presses
        /// past the press radius and holds while past the release radius. An
        /// inverted edge presses off center inside the press radius and holds
        /// while off center inside the release radius (0x1386698d0 with its
        /// invert flag). The dpad lets its inverted edge go, and resets its
        /// trigger, in the update where its engage trigger lets go. Steam's
        /// controller dead zone reads a resting stick as centered, so a stick
        /// counts as off center past the stick ring's rest floor.</summary>
        private static bool ReadRing(CustomInputState state, BoundedRead read, string descriptor, int slotIndex, string deviceGuid)
        {
            var (x, y) = Position(state, read, slotIndex, deviceGuid);
            double length = Math.Sqrt(x * x + y * y);
            var latch = LatchFor(deviceGuid, slotIndex, descriptor, out long poll);
            bool was = latch?.On ?? false;
            if (read.InnerEdge && read.HasEngage)
            {
                bool engaged = Engaged(read.EngageKey, read.EngagePress, read.EngageRelease, length, deviceGuid, slotIndex);
                bool letGo = latch != null && latch.WasEngaged && !engaged;
                if (latch != null) latch.WasEngaged = engaged;
                if (letGo)
                {
                    latch.Until = 0;
                    Set(latch, false, poll);
                    return false;
                }
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (latch != null && now < latch.Until)
            {
                Set(latch, was, poll);
                return was;
            }
            double floor = SourceCoercion.IsStickRingDescriptor(read.Inner)
                ? SourceCoercion.StickRingInnerFloorPercent
                : 0;
            bool on = read.InnerEdge
                ? length > floor && (was ? length < read.ReleaseRadius : length < read.PressRadius)
                : was ? length > read.ReleaseRadius : length > read.PressRadius;
            if (latch != null && on != was && read.DebounceMs > 0)
                latch.Until = now + (long)(read.DebounceMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            Set(latch, on, poll);
            return on;
        }

        /// <summary>One value through a hold-back trigger: a one-way input
        /// such as a trigger's pull, or a pair's component toward one way (zero
        /// the other way) or its length, all in percent. Steam's Soft Pull
        /// digital input, the trigger past 1600 (CTriggerSource 0x1386730c7),
        /// and the gyro modes' tilt left and right (0x1388bb32d, 0x1388b0f0d)
        /// read this way.</summary>
        private static bool ReadLevel(CustomInputState state, BoundedRead read, string descriptor, int slotIndex, string deviceGuid)
        {
            double v;
            if (read.ComponentAxis < 0)
            {
                bool off = read.Gate == BoundedGate.Engaged && slotIndex >= 0
                    && !(SourceCoercion.AimEngageStateProvider?.Invoke(slotIndex) ?? true);
                v = off ? 0 : SourceCoercion.EvaluateForTriggerTarget(state, read.ValueSource, slotIndex, deviceGuid) * 100.0;
            }
            else
            {
                var (x, y) = Position(state, read, slotIndex, deviceGuid);
                v = read.ComponentAxis == 2
                    ? Math.Sqrt(x * x + y * y)
                    : Math.Max(0, read.ComponentSign * (read.ComponentAxis == 0 ? x : y));
            }
            var latch = LatchFor(deviceGuid, slotIndex, descriptor, out long poll);
            bool was = latch?.On ?? false;
            bool on = read.InnerEdge
                ? v > 0 && (was ? v < read.ReleaseRadius : v < read.PressRadius)
                : was ? v > read.ReleaseRadius : v > read.PressRadius;
            Set(latch, on, poll);
            return on;
        }

        /// <summary>One direction of Steam's dpad (0x1388b2d30 and 0x1388b3390):
        /// engaged, clicked when the gate asks for it, and the position's
        /// angle inside the direction's span. A direction that pressed keeps
        /// the margin past each edge, one that did not loses it, and the
        /// angle off the direction folds to (-270, 90] as Steam folds it.</summary>
        private static bool ReadWedge(CustomInputState state, BoundedRead read, string descriptor, int slotIndex, string deviceGuid)
        {
            var (x, y) = Position(state, read, slotIndex, deviceGuid);
            bool engaged = Engaged(read, Math.Sqrt(x * x + y * y), deviceGuid, slotIndex);
            var latch = LatchFor(deviceGuid, slotIndex, descriptor, out long poll);
            bool was = latch?.On ?? false;
            bool on = false;
            if (engaged && (read.Gate != BoundedGate.Click || SourceCoercion.IsPairClicked(state, read.Inner, slotIndex, deviceGuid)))
            {
                double angle = Math.Atan2(-y, x) * 180.0 / Math.PI;
                if (angle < 0) angle += 360.0;
                double off = angle - read.Center;
                if (off > 90.0) off -= 360.0;
                double edge = was ? read.HalfWidth + read.Margin : read.HalfWidth - read.Margin;
                on = off > -edge && off < edge;
            }
            Set(latch, on, poll);
            return on;
        }

        /// <summary>One cell of Steam's four_buttons (0x1388b6ad0): a position
        /// at rest, or a missing click the gate asks for, presses nothing and
        /// leaves the cell's trigger as it was. Otherwise the cell's trigger
        /// releases past the release distance from its center and, once
        /// released, presses again only within the press distance. A trigger
        /// starts unreleased, as Steam's does.</summary>
        private static bool ReadDisc(CustomInputState state, BoundedRead read, string descriptor, int slotIndex, string deviceGuid)
        {
            var (x, y) = Position(state, read, slotIndex, deviceGuid);
            var latch = LatchFor(deviceGuid, slotIndex, descriptor, out long poll);
            bool released = latch?.On ?? false;
            if ((x == 0 && y == 0)
                || (read.Gate == BoundedGate.Click && !SourceCoercion.IsPairClicked(state, read.Inner, slotIndex, deviceGuid)))
            {
                // Steam skips the cell's trigger here and it keeps its state.
                Set(latch, released, poll);
                return false;
            }
            double dx = x - read.CenterX, dy = y - read.CenterY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            released = released ? distance > read.PressRadius : distance > read.ReleaseRadius;
            Set(latch, released, poll);
            return !released;
        }

        /// <summary>One direction of Steam's dpad cross gate (0x1388b3390,
        /// layout 3): engaged, clicked when the gate asks for it, and the
        /// component's own trigger on. The component trigger moves only
        /// while the dpad is engaged, as Steam steps it only then.</summary>
        private static bool ReadHold(CustomInputState state, BoundedRead read, string descriptor, int slotIndex, string deviceGuid)
        {
            var (x, y) = Position(state, read, slotIndex, deviceGuid);
            bool engaged = Engaged(read, Math.Sqrt(x * x + y * y), deviceGuid, slotIndex);
            var latch = LatchFor(deviceGuid, slotIndex, descriptor, out long poll);
            bool was = latch?.On ?? false;
            if (!engaged || (read.Gate == BoundedGate.Click && !SourceCoercion.IsPairClicked(state, read.Inner, slotIndex, deviceGuid)))
            {
                // Steam does not step the component's trigger here, and it
                // keeps its state for when the dpad engages again.
                Set(latch, was, poll);
                return false;
            }
            double component = read.ComponentSign * (read.ComponentAxis == 0 ? x : y);
            bool on = was ? component > read.ComponentRelease : component > read.ComponentPress;
            Set(latch, on, poll);
            return on;
        }
    }
}
