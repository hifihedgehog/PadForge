using System.Collections.Generic;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// Per-(slot, device) mouse-gesture recognizer state (issue #200).
    /// Each of the five mouse buttons runs its own independent gesture
    /// session, so "hold X1 and flick left" and "hold X2 and flick left"
    /// are distinct one-shot inputs. Session index 5 is the Custom
    /// activation (discussion #216): a recorded cross-device input held
    /// past the button threshold arms it exactly like a mouse button.
    /// Owned by the polling thread: created lazily by the InputManager
    /// walk, mutated only by <see cref="MouseGestureRecognizer.Update"/>.
    /// </summary>
    public sealed class MouseGestureContext
    {
        /// <summary>Physical mouse buttons (Left/Middle/Right/X1/X2).
        /// Only these indices may be armed from the mouse's own button
        /// state; a sixth-plus physical mouse button must never bleed
        /// into the Custom session below.</summary>
        public const int MouseButtonCount = 5;

        /// <summary>The Custom activation's session index (discussion
        /// #216). Armed exclusively from the settings' recorded
        /// cross-device descriptor, never from the mouse itself.</summary>
        public const int CustomButtonIndex = 5;

        public const int ButtonCount = 6;

        /// <summary>Per-button held state last tick, for edge detection.</summary>
        public readonly bool[] ButtonWasDown = new bool[ButtonCount];

        /// <summary>Per-button net displacement accumulated while that button
        /// is held, in raw mouse counts (screen convention: +X right,
        /// +Y down).</summary>
        public readonly double[] AccumDx = new double[ButtonCount];
        public readonly double[] AccumDy = new double[ButtonCount];

        /// <summary>Per-button end of the current fired pulse. 0 = none.
        /// While now is before this timestamp that button's fired keys stay
        /// asserted so the 1 kHz mapping read and the 30 Hz recorder both
        /// catch them.</summary>
        public readonly long[] CooldownUntilTimestampMs = new long[ButtonCount];

        /// <summary>Fired gesture keys currently asserted, in the
        /// "{buttonIndex} {Gesture}" form ("3 Left"). Same
        /// latch-through-cooldown contract as the touchpad lane: NOT cleared
        /// per tick; a button's keys clear at that button's cooldown expiry
        /// or fresh press. Read by SourceCoercion's coercion paths and the
        /// macro trigger evaluator.</summary>
        public HashSet<string> FiredGesturesThisFrame = new HashSet<string>();

        public void Reset()
        {
            for (int b = 0; b < ButtonCount; b++)
            {
                ButtonWasDown[b] = false;
                AccumDx[b] = 0;
                AccumDy[b] = 0;
                CooldownUntilTimestampMs[b] = 0;
            }
            FiredGesturesThisFrame.Clear();
        }
    }
}
