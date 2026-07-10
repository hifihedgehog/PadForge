using System;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// The mouse-gesture recognizer (issue #200), Logi Options+ semantics:
    /// while the per-device gesture button is held, accumulate raw
    /// displacement; at release classify the net movement. Dominant axis at
    /// or past the flick threshold fires exactly one of Left / Right / Up /
    /// Down; below the threshold the release fires Click. Fired names latch
    /// in <see cref="MouseGestureContext.FiredGesturesThisFrame"/> for the
    /// cooldown window, mirroring the touchpad lane's pulse contract.
    ///
    /// <para>Pure function of its inputs, polling-thread only, no
    /// allocation on the hot path. Deltas arrive as raw counts recovered
    /// from the already-published centered axes; the recognizer must NEVER
    /// consume RawInput deltas itself (the wrapper's consume-and-zero read
    /// owns that source).</para>
    /// </summary>
    public static class MouseGestureRecognizer
    {
        // Fired-set keys AND the descriptor suffixes. The full mapping
        // descriptors are "Mouse Gesture " + these names.
        public const string GestureLeft = "Left";
        public const string GestureRight = "Right";
        public const string GestureUp = "Up";
        public const string GestureDown = "Down";
        public const string GestureClick = "Click";

        /// <summary>The mapping-descriptor prefix for the family.</summary>
        public const string DescriptorPrefix = "Mouse Gesture ";

        public static void Update(
            MouseGestureContext ctx,
            MouseGestureSettings settings,
            bool buttonDown,
            double dxCounts,
            double dyCounts,
            long nowMs)
        {
            if (ctx == null) return;
            if (settings == null || !settings.Enabled)
            {
                // Disabled: drop any in-flight state so a re-enable starts
                // clean (touchpad lane precedent).
                if (ctx.ButtonWasDown || ctx.FiredGesturesThisFrame.Count > 0)
                    ctx.Reset();
                return;
            }

            // Cooldown expiry ends the fired pulse.
            if (ctx.CooldownUntilTimestampMs != 0 && nowMs >= ctx.CooldownUntilTimestampMs)
            {
                ctx.FiredGesturesThisFrame.Clear();
                ctx.CooldownUntilTimestampMs = 0;
            }

            if (buttonDown && !ctx.ButtonWasDown)
            {
                // Fresh gesture start: discard leftover latched fires so the
                // prior gesture cannot bleed into this one's window.
                ctx.AccumDx = 0;
                ctx.AccumDy = 0;
                ctx.FiredGesturesThisFrame.Clear();
                ctx.CooldownUntilTimestampMs = 0;
            }

            if (buttonDown)
            {
                ctx.AccumDx += dxCounts;
                ctx.AccumDy += dyCounts;
            }
            else if (ctx.ButtonWasDown)
            {
                // Release: classify net displacement. Screen convention:
                // dx > 0 right, dy > 0 down (RawInput deltas).
                double ax = Math.Abs(ctx.AccumDx);
                double ay = Math.Abs(ctx.AccumDy);
                int threshold = Math.Max(1, settings.FlickThresholdCounts);

                string fired;
                if (ax < threshold && ay < threshold)
                    fired = GestureClick;
                else if (ax >= ay)
                    fired = ctx.AccumDx < 0 ? GestureLeft : GestureRight;
                else
                    fired = ctx.AccumDy < 0 ? GestureUp : GestureDown;

                ctx.FiredGesturesThisFrame.Add(fired);
                ctx.CooldownUntilTimestampMs = nowMs + Math.Max(0, settings.CooldownMs);
                ctx.AccumDx = 0;
                ctx.AccumDy = 0;
            }

            ctx.ButtonWasDown = buttonDown;
        }
    }
}
