namespace PadForge.Engine.Common
{
    /// <summary>
    /// Pure stepping math for a Shift Cycle activator's cursor (#119).
    /// Extracted from the input loop so the Next/Previous/wrap/Base behavior
    /// can be unit-tested without a controller.
    /// </summary>
    public static class ShiftCycleStepper
    {
        /// <summary>
        /// Advance the cursor one press.
        /// </summary>
        /// <param name="pos">Current cursor. 0 = Base; 1..N index the layers.</param>
        /// <param name="n">Number of queued layers (must be &gt;= 1).</param>
        /// <param name="previous">True steps backward (Previous), false forward (Next).</param>
        /// <param name="wrap">True loops the ends together, false clamps.</param>
        /// <param name="includeBase">True keeps Base (pos 0) as a stop in the ring;
        /// false walks the layers only and never re-enters Base after the first press.</param>
        /// <returns>The new cursor position.</returns>
        public static int Step(int pos, int n, bool previous, bool wrap, bool includeBase)
        {
            if (n < 1) return 0;

            // A cursor left over from a longer ring must not survive the
            // step. Editing an activator's CycleLayers down (three layers to
            // one) changes n without touching the stored cursor, and both the
            // layers-only Previous branch and the clamped includeBase branch
            // would then hand back a position past the end, which the caller
            // uses as layers[pos - 1]. Normalize here so Step's result is
            // always a valid stop in [0..n] for the n it was given.
            if (pos > n) pos = n;
            else if (pos < 0) pos = 0;

            if (includeBase)
            {
                // Base is a real stop in the ring [0..N].
                if (wrap)
                    return previous ? (pos + n) % (n + 1) : (pos + 1) % (n + 1);
                return previous ? System.Math.Max(pos - 1, 0)
                                : System.Math.Min(pos + 1, n);
            }

            // Layers only [1..N]. Base (pos 0) is only the pre-first-press
            // resting state, never re-entered via cycling.
            if (pos <= 0)
                return previous ? (wrap ? n : 1) : 1;
            if (previous)
                return pos > 1 ? pos - 1 : (wrap ? n : 1);
            return pos < n ? pos + 1 : (wrap ? 1 : n);
        }
    }
}
