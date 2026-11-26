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
    /// Rate independence (#600): a pass's deltas are spread over the pass
    /// they came in, from the previous add to this one, so the speed is the
    /// counts that fall inside the last <see cref="WindowMs"/> over the
    /// window, whatever the passes' length. Counting every entry newer than
    /// the window whole read a 60 Hz loop's two 16.7 ms passes as 33 ms of
    /// counts over 25 (1.33 times true). The oldest entry is the only one
    /// that can stand across the window's start, and only its part inside
    /// is counted. The ring holds every pass of a window at 10,000 passes a
    /// second, so none is dropped (a 64-entry ring dropped the oldest past
    /// 64 non-zero passes, 0.64 times true at 10,000). At 1 ms passes every
    /// entry lies whole inside, so the numbers are the old ones.
    /// <see cref="WindowSums"/> keeps whole entries, the impulse lane's
    /// contract.
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

        // Ring sized for the window at the fastest polling rate, 10,000
        // passes a second (250 passes), with room to spare. Power of two
        // so the index mask is a single AND.
        private const int Capacity = 512;
        private const int Mask = Capacity - 1;

        private readonly long[] _ticks = new long[Capacity];
        private readonly long[] _starts = new long[Capacity];
        private readonly float[] _dx = new float[Capacity];
        private readonly float[] _dy = new float[Capacity];
        private readonly float[] _dz = new float[Capacity];
        private int _head;   // next write slot
        private int _count;  // live entries
        private float _sumX, _sumY, _sumZ;
        private long _prevAddTicks;
        // The last pass Add took: where it began, and the share a stall
        // longer than the window scaled its deltas by.
        private long _passStart;
        private float _passShare = 1f;

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

            // The pass these deltas came in: from the previous add to now.
            long start = nowTicks - gap;

            // A lump that accumulated across a stall longer than the window
            // is pre-scaled to its average share of one window, so the
            // fixed-window divisor below reads it as the stall's true
            // average velocity rather than a spike. What is left of it
            // stands for the window's length.
            float share = 1f;
            if (gap > s_windowTicks)
            {
                share = (float)s_windowTicks / gap;
                start = nowTicks - s_windowTicks;
            }
            _passStart = start;
            _passShare = share;

            if (dx == 0f && dy == 0f && dz == 0f)
                return;
            Push(nowTicks, start, dx * share, dy * share, dz * share);
        }

        /// <summary>Adds more deltas to the pass the last <see cref="Add"/>
        /// took, for a reader that folds several devices' counts into one
        /// pass: the window then reads as if one Add had carried them
        /// all.</summary>
        public void AddToPass(float dx, float dy, float dz)
        {
            if (_prevAddTicks == 0 || (dx == 0f && dy == 0f && dz == 0f)) return;
            dx *= _passShare;
            dy *= _passShare;
            dz *= _passShare;
            int last = (_head - 1) & Mask;
            if (_count > 0 && _ticks[last] == _prevAddTicks)
            {
                _dx[last] += dx;
                _dy[last] += dy;
                _dz[last] += dz;
                _sumX += dx;
                _sumY += dy;
                _sumZ += dz;
                return;
            }
            Push(_prevAddTicks, _passStart, dx, dy, dz);
        }

        private void Push(long nowTicks, long start, float dx, float dy, float dz)
        {
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
            _starts[_head] = start;
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
        /// <see cref="Add"/> for the same tick: the counts inside the
        /// window, each entry's spread over its own pass, over the
        /// window.</summary>
        public void CountsPerSecond(out float x, out float y, out float z)
        {
            float sx = _sumX, sy = _sumY, sz = _sumZ;
            if (_count > 0)
            {
                // Only the oldest entry can begin before the window does.
                int tail = (_head - _count) & Mask;
                long limit = _prevAddTicks - s_windowTicks;
                long begin = _starts[tail], end = _ticks[tail];
                if (begin < limit && end > begin)
                {
                    float outside = (float)(limit - begin) / (end - begin);
                    sx -= _dx[tail] * outside;
                    sy -= _dy[tail] * outside;
                    sz -= _dz[tail] * outside;
                }
            }
            x = sx / s_windowSeconds;
            y = sy / s_windowSeconds;
            z = sz / s_windowSeconds;
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
