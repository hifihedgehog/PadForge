using System.Collections.Generic;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// Per-(slot, device) mouse-gesture recognizer state (issue #200).
    /// Owned by the polling thread: created lazily by the InputManager walk,
    /// mutated only by <see cref="MouseGestureRecognizer.Update"/>.
    /// </summary>
    public sealed class MouseGestureContext
    {
        /// <summary>Gesture button state last tick, for edge detection.</summary>
        public bool ButtonWasDown;

        /// <summary>Net displacement accumulated while the gesture button is
        /// held, in raw mouse counts (screen convention: +X right, +Y down).</summary>
        public double AccumDx;
        public double AccumDy;

        /// <summary>End of the current fired pulse. 0 = no pulse pending.
        /// While now is before this timestamp the fired set stays asserted so
        /// the 1 kHz mapping read and the 30 Hz recorder both catch it.</summary>
        public long CooldownUntilTimestampMs;

        /// <summary>Gesture names currently asserted ("Left", "Right", "Up",
        /// "Down", "Click"). Same latch-through-cooldown contract as
        /// <see cref="Touchpad.TouchpadGestureContext.FiredGesturesThisFrame"/>:
        /// NOT cleared per tick, cleared at cooldown expiry or fresh-gesture
        /// start. Read by SourceCoercion's bool coercion and the macro
        /// trigger evaluator.</summary>
        public HashSet<string> FiredGesturesThisFrame = new HashSet<string>();

        public void Reset()
        {
            ButtonWasDown = false;
            AccumDx = 0;
            AccumDy = 0;
            CooldownUntilTimestampMs = 0;
            FiredGesturesThisFrame.Clear();
        }
    }
}
