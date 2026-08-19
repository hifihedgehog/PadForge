using System;
using System.Diagnostics;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Sliding-window velocity estimator for relative (delta) input lanes:
    /// physical mouse motion, mouse scroll, and the Joy-Con 2 optical mouse.
    ///
    /// Why this exists (#331): these lanes used to publish the PER-POLL delta
    /// as the axis value. A mouse reports at its own rate (an office mouse at
    /// 125 Hz, a gaming mouse at 1000 Hz) while the engine polls at another
    /// (1 ms default, settable to 16 ms), so with a 125 Hz mouse at 1 kHz
    /// polling, seven of every eight polls read ZERO and the mapped stick
    /// became a center/spike comb at the mouse's report rate. That comb is
    /// the "stuttering and skipping frames" a game sees when the virtual
    /// stick drives its camera. The per-poll form also made the scale depend
    /// on the polling interval: at 4 ms the same hand motion produced 4x the
    /// deflection, which punished exactly the low-end machines that benefit
    /// from a longer interval.
    ///
    /// The estimator keeps deltas from the last <see cref="WindowMs"/>
    /// milliseconds and reports counts per second over that fixed window (a
    /// boxcar average). Scale parity is the caller's job: multiplying the
    /// returned counts/second by (old-per-poll-scale / 1000) makes a steady
    /// 1000 Hz mouse at the default 1 ms interval numerically IDENTICAL to
    /// the old code, and preserves the time integral of deflection at every
    /// report rate, so net camera speed through any mapping is unchanged.
    ///
    /// Stall behavior: when the gap since the previous add exceeds the
    /// window, the deltas that piled up during the stall arrive as one lump.
    /// The lump is scaled by window/gap before entering the ring, so it
    /// reads as its true average velocity over the stall instead of a
    /// one-window spike.
    ///
    /// Single-threaded by contract: the polling thread is the sole caller,
    /// like the wrappers that own instances of this class.
    /// </summary>
    public sealed class RelativeVelocityWindow
    {
        /// <summary>Window length. 25 ms holds at least three reports from a
        /// 125 Hz mouse (so the comb fully closes) while adding only a
        /// ~1.5-frame release tail at 60 Hz. Onset is unaffected: a delta
        /// enters the window on the poll it arrives.</summary>
        public const int WindowMs = 25;

        // Ring sized for the window at the fastest polling interval (1 ms)
        // plus slack. Power of two so the index mask is a single AND.
        private const int Capacity = 64;
        private const int Mask = Capacity - 1;

        private readonly long[] _ticks = new long[Capacity];
        private readonly float[] _dx = new float[Capacity];
        private readonly float[] _dy = new float[Capacity];
        private readonly float[] _dz = new float[Capacity];
        private int _head;   // next write slot
        private int _count;  // live entries
        private float _sumX, _sumY, _sumZ;
        private long _prevAddTicks;

        private static readonly long s_windowTicks =
            Stopwatch.Frequency * WindowMs / 1000;

        /// <summary>Adds this poll's consumed deltas and evicts entries older
        /// than the window. Zero deltas cost eviction only (they carry no
        /// sum), so an idle mouse causes no ring churn.</summary>
        public void Add(long nowTicks, float dx, float dy, float dz)
        {
            // Evict expired entries from the tail.
            long limit = nowTicks - s_windowTicks;
            while (_count > 0)
            {
                int tail = (_head - _count) & Mask;
                if (_ticks[tail] > limit)
                    break;
                _sumX -= _dx[tail];
                _sumY -= _dy[tail];
                _sumZ -= _dz[tail];
                _count--;
            }

            long gap = _prevAddTicks == 0 ? 0 : nowTicks - _prevAddTicks;
            _prevAddTicks = nowTicks;

            if (dx == 0f && dy == 0f && dz == 0f)
                return;

            // A lump that accumulated across a stall longer than the window
            // is pre-scaled to its average share of one window, so the
            // fixed-window divisor below reads it as the stall's true
            // average velocity rather than a spike.
            if (gap > s_windowTicks)
            {
                float share = (float)s_windowTicks / gap;
                dx *= share;
                dy *= share;
                dz *= share;
            }

            if (_count == Capacity)
            {
                // Ring full (cannot happen at supported intervals, defensive):
                // drop the oldest so sums stay consistent.
                int tail = (_head - _count) & Mask;
                _sumX -= _dx[tail];
                _sumY -= _dy[tail];
                _sumZ -= _dz[tail];
                _count--;
            }

            _ticks[_head] = nowTicks;
            _dx[_head] = dx;
            _dy[_head] = dy;
            _dz[_head] = dz;
            _head = (_head + 1) & Mask;
            _count++;
            _sumX += dx;
            _sumY += dy;
            _sumZ += dz;
        }

        private static readonly float s_windowSeconds = WindowMs / 1000f;

        /// <summary>Counts per second over the window, computed after
        /// <see cref="Add"/> for the same tick.</summary>
        public void CountsPerSecond(out float x, out float y, out float z)
        {
            x = _sumX / s_windowSeconds;
            y = _sumY / s_windowSeconds;
            z = _sumZ / s_windowSeconds;
        }

        /// <summary>Raw sums over the window, computed after <see cref="Add"/>
        /// for the same tick. For impulse lanes (scroll notches): a discrete
        /// event is not a rate, so dividing it across the window collapses
        /// its peak by the window factor (a 120-count notch averaged over
        /// 25 ms reads 25x weaker than the same notch published per-poll,
        /// which killed every thresholded scroll mapping). The sum keeps the
        /// old per-poll peak exactly, holds it for the window so a slow
        /// consumer cannot miss it, and merges notches that land within one
        /// window, so faster scrolling still reads stronger.</summary>
        public void WindowSums(out float x, out float y, out float z)
        {
            x = _sumX;
            y = _sumY;
            z = _sumZ;
        }

        /// <summary>Drops all state (device re-open, stream restart).</summary>
        public void Reset()
        {
            _head = 0;
            _count = 0;
            _sumX = _sumY = _sumZ = 0f;
            _prevAddTicks = 0;
        }
    }
}
