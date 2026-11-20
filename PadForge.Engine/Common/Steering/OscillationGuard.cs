using System;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// Watches a wheel for an oscillation of its own (#588) and takes
    /// the PC's force down when it sees one.
    ///
    /// <para>A wheel driven by a late force swings back and forth at a
    /// steady pace, each swing as wide as the last or wider. A wheel
    /// that settles does so in a swing or two, each smaller than the
    /// last, and a hand's back and forth seldom keeps its width and its
    /// pace for long. The guard counts half cycles, reversal to
    /// reversal, that keep a pace, and reads three patterns:</para>
    ///
    /// <para>Steady: <see cref="FastHalfCycles"/> half cycles in a row
    /// at a fast pace, or <see cref="SlowHalfCycles"/> at a slow one,
    /// which a hand can also make, every one within
    /// <see cref="Evenness"/> of the run's first full swing in width
    /// and of its first in time. A wheel swinging by itself repeats
    /// itself that closely, and a hand's weave seldom does. It takes
    /// the factor one step down <see cref="Steps"/>.</para>
    ///
    /// <para>Growing: the swing has reached <see cref="Growth"/> times
    /// the run's first full swing without ever shrinking, over at least
    /// <see cref="GrowingHalfCycles"/> half cycles. Violent:
    /// <see cref="ViolentHalfCycles"/> wide half cycles in a row at
    /// <see cref="ViolentSpeed"/> or more. Either takes the factor
    /// straight to the ladder's lowest step above zero, since the wheel
    /// is already swinging wide.</para>
    ///
    /// <para>The ladder's last step is zero: the PC's force is off.
    /// Nothing brings the factor back but a reset, since a wheel that
    /// swung under a force swings again under the same force.</para>
    /// </summary>
    public sealed class OscillationGuard
    {
        /// <summary>Reversals narrower than this, in radians, are sensor
        /// noise.</summary>
        public const double LeastSwing = 0.4 * Math.PI / 180.0;

        /// <summary>A half cycle between these many milliseconds is 1
        /// to 25 Hz.</summary>
        public const int ShortestHalfMs = 20, LongestHalfMs = 500;

        /// <summary>A half cycle longer than this is slow: under 3.3 Hz.</summary>
        public const int SlowHalfMs = 150;

        public const int FastHalfCycles = 8, SlowHalfCycles = 10, GrowingHalfCycles = 4, ViolentHalfCycles = 3;

        /// <summary>How far a steady run's half cycles may differ from
        /// its first full swing in width and from its first in time.</summary>
        public const double Evenness = 0.15;

        /// <summary>How far past the run's first full swing a swing is
        /// to count as grown.</summary>
        public const double Growth = 1.5;

        /// <summary>The mean speed of a violent half cycle, in radians
        /// a second: 400 degrees a second.</summary>
        public const double ViolentSpeed = 400.0 * Math.PI / 180.0;

        /// <summary>The least width of a violent half cycle, in radians:
        /// 30 degrees.</summary>
        public const double ViolentSwing = 30.0 * Math.PI / 180.0;

        /// <summary>The factor after each run seen.</summary>
        public static readonly double[] Steps = { 0.5, 0.25, 0.1, 0.0 };

        private double _extreme, _previousExtreme;
        private long _extremeTick, _previousTick;
        private int _direction;
        private bool _has;

        private int _run, _even, _violent, _seen;
        private double _fullSwing, _lastSwing;
        private long _fullHalf;
        private bool _neverShrank;

        /// <summary>The share of its force the PC may still render: 1
        /// until an oscillation is seen, 0 once the PC's force is off.</summary>
        public double Factor { get; private set; } = 1.0;

        public void Reset()
        {
            Factor = 1.0;
            _seen = 0;
            EndRun();
            _direction = 0;
            _has = false;
        }

        private void EndRun()
        {
            _run = 0;
            _even = 0;
            _violent = 0;
            _fullSwing = 0;
            _lastSwing = 0;
        }

        /// <summary>One sample: the angle in radians and a millisecond
        /// clock.</summary>
        public void Add(double angle, long tickMs)
        {
            if (!_has)
            {
                _has = true;
                _extreme = _previousExtreme = angle;
                _extremeTick = _previousTick = tickMs;
                return;
            }
            if (_direction == 0)
            {
                // No way yet: the first move of the least swing sets it.
                if (Math.Abs(angle - _previousExtreme) < LeastSwing) return;
                _direction = angle > _previousExtreme ? 1 : -1;
                _extreme = angle;
                _extremeTick = tickMs;
                return;
            }
            // Follow the angle while it keeps going the same way.
            if ((angle - _extreme) * _direction >= 0)
            {
                _extreme = angle;
                _extremeTick = tickMs;
                return;
            }
            // A turn back by the least swing is a reversal, and the extreme before it ends a half cycle.
            if (Math.Abs(angle - _extreme) < LeastSwing) return;
            HalfCycle(Math.Abs(_extreme - _previousExtreme), _extremeTick - _previousTick);
            _previousExtreme = _extreme;
            _previousTick = _extremeTick;
            _extreme = angle;
            _extremeTick = tickMs;
            _direction = -_direction;
        }

        private void HalfCycle(double swing, long half)
        {
            if (half < ShortestHalfMs || half > LongestHalfMs || swing < LeastSwing)
            {
                EndRun();
                return;
            }
            // A run's first half cycle may start anywhere between two extremes, so it only opens the run. The
            // second is the first full one, and the rest are held against it: a run keeps its pace, no half
            // cycle under half or over twice the full one's time, and its width, no swing under 0.7 of it.
            if (_run >= 2 && (half * 2 < _fullHalf || half > _fullHalf * 2 || swing < 0.7 * _fullSwing))
                EndRun();

            if (_run == 0)
            {
                _neverShrank = true;
            }
            else if (_run == 1)
            {
                _fullSwing = swing;
                _fullHalf = half;
                _even = 1;
            }
            else
            {
                if (swing < _lastSwing) _neverShrank = false;
                // Even: within the evenness of the first full half cycle in width and in time. One that is not
                // starts the count again.
                bool even = Math.Abs(swing - _fullSwing) <= Evenness * _fullSwing
                    && Math.Abs(half - _fullHalf) <= Evenness * _fullHalf;
                _even = even ? _even + 1 : 0;
            }
            _run++;
            _lastSwing = swing;
            _violent = swing >= ViolentSwing && swing / (half / 1000.0) >= ViolentSpeed ? _violent + 1 : 0;

            bool grown = _run > GrowingHalfCycles && _neverShrank && swing >= Growth * _fullSwing;
            bool violent = _violent >= ViolentHalfCycles;
            bool steady = _run >= 2 && _even >= (_fullHalf > SlowHalfMs ? SlowHalfCycles : FastHalfCycles);
            if (!grown && !violent && !steady) return;

            // Growing and violent runs go straight to the lowest step above zero.
            if (grown || violent) _seen = Math.Max(_seen, Steps.Length - 2);
            Factor = Steps[Math.Min(_seen, Steps.Length - 1)];
            _seen++;
            EndRun();
        }
    }
}
