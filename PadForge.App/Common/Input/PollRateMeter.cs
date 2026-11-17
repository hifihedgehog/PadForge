namespace PadForge.Common.Input
{
    /// <summary>
    /// The rate the poll loop's passes run at, as Home and the heartbeat show
    /// it: the passes over the schedule's own time, a second at a time.
    ///
    /// <para>Each pass is counted at the time it was due (PollSchedule.Due),
    /// not the time it ran. Due times keep exact time, where the time a pass
    /// ends wobbles by its wake's lateness, up to half a millisecond at each
    /// end of a window, and a loop at 1000 Hz read 999 or 1001 now and then.
    /// The schedule starts again from the moment it is in only after a stall,
    /// so passes a stall dropped still show as a lower rate.</para>
    /// </summary>
    internal sealed class PollRateMeter
    {
        private long _windowDue;
        private int _passes;
        private bool _open;

        /// <summary>Forgets the window, after a rest or a stop: the next pass
        /// starts a new one.</summary>
        internal void Reset() => _open = false;

        /// <summary>Counts a pass that was due at <paramref name="due"/>, in
        /// clock ticks, <paramref name="frequency"/> of them a second. Once a
        /// second of the schedule has passed since the window's first pass,
        /// it gives the rate and starts the next window at this pass.</summary>
        internal bool TryPass(long due, long frequency, out double rate)
        {
            rate = 0;
            if (!_open)
            {
                _open = true;
                _windowDue = due;
                _passes = 0;
                return false;
            }
            _passes++;
            long span = due - _windowDue;
            if (span < frequency) return false;
            rate = _passes * (double)frequency / span;
            _windowDue = due;
            _passes = 0;
            return true;
        }
    }
}
