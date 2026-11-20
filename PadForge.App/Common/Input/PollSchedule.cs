using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// When the poll loop's passes are due. The passes stand on one schedule,
    /// a whole number of them a second, and the loop's thread sleeps on a
    /// timer between them. It never spins: a processor core held busy to land
    /// a pass on the microsecond costs more than the lateness it removes.
    ///
    /// <para>Windows wakes a sleeping thread on its clock's tick, half a
    /// millisecond apart at the finest (<see cref="InputManager.TimerGrid"/>).
    /// A pass due between two ticks runs at the second, so a rate above the
    /// ticks' own has several passes due at each wake, and they run back to
    /// back. The rate holds, and the passes come in groups.</para>
    ///
    /// <para>Two bounds keep a slow moment from turning into a busy one.
    /// A loop further behind than <see cref="StallBound"/>, after a stall or
    /// a resume from sleep, drops the passes it missed and starts its
    /// schedule again from that moment. And passes run back to back for no
    /// longer than one tick's time (<see cref="BurstBudget"/>) after a wake.
    /// Once that time has gone, in owed passes or in one long pass, the loop
    /// starts again and sleeps a whole interval. It used to run on with no
    /// gap and starve the interface thread (#331), and a loop whose every
    /// pass outlasted the stall bound never slept.</para>
    ///
    /// <para>Arithmetic over <see cref="System.Diagnostics.Stopwatch"/> ticks
    /// only, so a test drives it with a clock of its own.</para>
    /// </summary>
    internal sealed class PollSchedule
    {
        private readonly long _frequency;
        private long _due;
        private long _whole, _remainder, _carry;
        private int _hz;
        private long _burstStart;

        /// <summary>How long owed passes may run back to back after a wake:
        /// the time between two ticks of the clock.</summary>
        internal long BurstBudget { get; }

        /// <summary>How far behind the schedule may fall and still be caught
        /// up: two milliseconds, or four passes where that is longer.</summary>
        internal long StallBound { get; private set; }

        /// <param name="frequency">The clock's ticks in a second.</param>
        /// <param name="grid">The time between two wakes Windows can give,
        /// in the clock's ticks.</param>
        /// <param name="hz">Passes a second.</param>
        /// <param name="now">The clock now. The first pass is due now.</param>
        internal PollSchedule(long frequency, long grid, int hz, long now)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
            _frequency = frequency;
            BurstBudget = Math.Max(1, grid);
            SetRate(hz);
            _due = now;
            _burstStart = now;
        }

        /// <summary>Passes a second.</summary>
        internal int Hz => _hz;

        /// <summary>When the pass about to run was due. The loop's meter
        /// takes how late each pass starts from it.</summary>
        internal long Due => _due;

        /// <summary>Changes the rate. The pass that is due next stays due
        /// when it was, and the ones after it follow at the new rate.</summary>
        internal void SetRate(int hz)
        {
            if (hz < 1) throw new ArgumentOutOfRangeException(nameof(hz));
            if (hz == _hz) return;
            _hz = hz;
            // A second is not a whole number of ticks for every rate, so the
            // part of a tick left over is carried and paid out a tick at a
            // time. 144 passes are then exactly a second apart, where a
            // rounded interval would drift.
            _whole = _frequency / hz;
            _remainder = _frequency % hz;
            _carry = 0;
            StallBound = Math.Max(_frequency / 500, 4 * _whole);
        }

        /// <summary>Asked after each pass: 0 to run the next pass now, or the
        /// time to sleep until.</summary>
        internal long Next(long now)
        {
            _due += _whole;
            _carry += _remainder;
            if (_carry >= _hz)
            {
                _carry -= _hz;
                _due++;
            }

            long behind = now - _due;
            if (behind < 0) return _due;
            if (behind > StallBound)
            {
                // A stall or a resume from sleep: the passes it missed are
                // dropped, and the next one is due now.
                _due = now;
                _carry = 0;
            }
            if (now - _burstStart < BurstBudget) return 0;
            // A tick's time has gone since the wake, in passes back to back
            // or in one long pass: the loop sleeps a whole interval first.
            Restart(now);
            _due = now + _whole;
            return _due;
        }

        /// <summary>The thread woke from the sleep <see cref="Next"/> asked
        /// for, or came back from a branch that paced itself.</summary>
        internal void Woke(long now) => _burstStart = now;

        /// <summary>Starts the schedule again: the next pass is due now.</summary>
        internal void Restart(long now)
        {
            _due = now;
            _carry = 0;
            _burstStart = now;
        }
    }
}
