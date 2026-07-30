using System;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Per-(slot, device, pad) travel accumulator for swipe-haptic ticks
    /// (discussion #219). One instance per key, owned by the polling
    /// thread; no cross-thread access.
    /// </summary>
    public sealed class SwipeHapticsState
    {
        /// <summary>Contact ID the accumulator is tracking per finger
        /// slot. -1 = slot idle. A changed ID means a new finger landed
        /// in the slot, which reseeds instead of ticking on the jump.</summary>
        public int[] ContactIds = Array.Empty<int>();

        /// <summary>Last seen position per finger slot (normalized 0..1).</summary>
        public float[] LastX = Array.Empty<float>();
        public float[] LastY = Array.Empty<float>();

        /// <summary>Accumulated travel (normalized pad units) per finger
        /// slot since the last tick / seed.</summary>
        public float[] Travel = Array.Empty<float>();

        public void EnsureCapacity(int fingers)
        {
            if (ContactIds.Length >= fingers) return;
            var ids = new int[fingers];
            var lx = new float[fingers];
            var ly = new float[fingers];
            var tr = new float[fingers];
            Array.Copy(ContactIds, ids, ContactIds.Length);
            Array.Copy(LastX, lx, LastX.Length);
            Array.Copy(LastY, ly, LastY.Length);
            Array.Copy(Travel, tr, Travel.Length);
            for (int i = ContactIds.Length; i < fingers; i++) ids[i] = -1;
            ContactIds = ids; LastX = lx; LastY = ly; Travel = tr;
        }

        public void Reset()
        {
            for (int i = 0; i < ContactIds.Length; i++)
            {
                ContactIds[i] = -1;
                Travel[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Distance-detent evaluator for swipe-haptic ticks. Semantics mirror
    /// SteamlessController's trackpad-haptics block (ControllerManager.cpp
    /// :316-395), the working Steam-Input-feel implementation for the
    /// Steam Controller 2026:
    /// <list type="bullet">
    /// <item>Euclidean finger travel accumulates per frame; one tick per
    /// <see cref="DefaultTickDistance"/> of travel, several per frame
    /// possible (their while-loop, :371-374).</item>
    /// <item>The first frame of a touch seeds the baseline and never
    /// ticks (:356-365, gated on wasTouching :367).</item>
    /// <item>A pad click suppresses ticks and reseeds the accumulator so
    /// click travel doesn't bleed into a spurious move tick (:340-351,
    /// the !clicked gate :367).</item>
    /// <item>A lifted finger drops its accumulator; the next touch
    /// starts fresh.</item>
    /// </list>
    /// Divergence from the reference, deliberate: their single-finger pads
    /// generalize here to one accumulator per finger slot (DS4/DualSense
    /// track two fingers), and a same-slot contact-ID change reseeds like
    /// a fresh touch (PadForge's TouchpadInputState tracks contact
    /// identity; the reference has no equivalent).
    /// </summary>
    public static class SwipeHapticsEvaluator
    {
        /// <summary>Travel per tick in normalized pad units.
        /// SteamlessController fires one tick per 5000 raw units
        /// (TRACKPAD_HAPTIC_TICK_DISTANCE, ControllerManager.cpp:318) on
        /// the pad's int16 axis span of 65536, i.e. ~0.076 of the span.
        /// Neither reference exposes this as a user knob, so it stays a
        /// constant.</summary>
        public const float DefaultTickDistance = 5000f / 65536f;

        /// <summary>Advances the accumulator with one pad snapshot.
        /// Returns the number of ticks earned this frame (0 almost
        /// always, 1+ while a finger is moving).</summary>
        public static int Update(SwipeHapticsState st, TouchpadInputState pad,
            float tickDistance = DefaultTickDistance)
        {
            if (st == null || pad == null || pad.MaxFingers <= 0) return 0;
            if (tickDistance <= 0f) tickDistance = DefaultTickDistance;
            st.EnsureCapacity(pad.MaxFingers);

            int ticks = 0;
            for (int f = 0; f < pad.MaxFingers; f++)
            {
                if (!pad.FingerDown[f])
                {
                    // Lift: drop the accumulator; the next touch reseeds.
                    st.ContactIds[f] = -1;
                    st.Travel[f] = 0f;
                    continue;
                }

                float x = pad.FingerX[f];
                float y = pad.FingerY[f];
                int cid = pad.FingerContactId[f];

                if (st.ContactIds[f] != cid)
                {
                    // First frame of a (possibly slot-reused) contact:
                    // seed only. A new touch never ticks by itself.
                    st.ContactIds[f] = cid;
                    st.LastX[f] = x;
                    st.LastY[f] = y;
                    st.Travel[f] = 0f;
                    continue;
                }

                if (pad.Clicked)
                {
                    // Click held: follow the finger but never tick, and
                    // reseed so click travel doesn't convert to a move
                    // tick on release.
                    st.LastX[f] = x;
                    st.LastY[f] = y;
                    st.Travel[f] = 0f;
                    continue;
                }

                float dx = x - st.LastX[f];
                float dy = y - st.LastY[f];
                st.LastX[f] = x;
                st.LastY[f] = y;
                st.Travel[f] += MathF.Sqrt(dx * dx + dy * dy);
                while (st.Travel[f] >= tickDistance)
                {
                    st.Travel[f] -= tickDistance;
                    ticks++;
                }
            }
            return ticks;
        }
    }
}
