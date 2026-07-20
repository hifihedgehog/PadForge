using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Per-touchpad finger snapshot read from a physical touchpad surface
    /// (SDL gamepad touchpad, Windows Precision Touchpad, the built-in
    /// overlay, etc.). Supports up to N fingers per pad (PTP max 5; SDL
    /// gamepad pads typically 1-2) and tracks contact identity across
    /// finger-up / finger-down events so the gesture engine can
    /// distinguish "same finger continuing" from "finger lifted then a
    /// new one landed in the same slot."
    ///
    /// <para>Distinct from the DS4-output <see cref="TouchpadState"/>
    /// struct in <c>GamepadTypes.cs</c>, which carries the virtual DS4
    /// touchpad report bound for ViGEmBus / DS4_REPORT_EX. This type is
    /// physical-input state; that one is virtual-output state.</para>
    ///
    /// <para>X / Y / Pressure are normalized 0..1 in touchpad space.</para>
    /// </summary>
    public sealed class TouchpadInputState
    {
        /// <summary>Number of finger slots this pad exposes. Set at
        /// device-open time and held constant across the pad's lifetime.</summary>
        public int MaxFingers;

        /// <summary>Canonical touchpad-click button for this pad. Mirrors
        /// <c>state.Buttons[16]</c> for the primary pad (per the touchpad-
        /// click-as-button refactor); multi-touchpad devices write the
        /// second pad's click here from MISC2.</summary>
        public bool Clicked;

        /// <summary>Per-slot X position (normalized 0..1). 0 when slot empty.</summary>
        public float[] FingerX;

        /// <summary>Per-slot Y position (normalized 0..1). 0 when slot empty.</summary>
        public float[] FingerY;

        /// <summary>Per-slot pressure (normalized 0..1). 0 when slot empty.</summary>
        public float[] FingerPressure;

        /// <summary>Per-slot down state. True while a finger is in contact
        /// with the touchpad surface for this slot.</summary>
        public bool[] FingerDown;

        /// <summary>Per-slot contact identifier — a monotonic counter that
        /// increments each time the slot transitions from up to down.
        /// -1 when the slot is currently up. Lets the gesture engine
        /// detect "finger lifted and a new one landed" vs "same finger
        /// continuing" within a single slot index. Synthesized by
        /// <see cref="SdlDeviceWrapper"/>; native HID contact IDs used
        /// directly when available (Windows Precision Touchpad).</summary>
        public int[] FingerContactId;

        public TouchpadInputState() : this(0) { }

        public TouchpadInputState(int maxFingers)
        {
            MaxFingers = maxFingers;
            FingerX = new float[maxFingers];
            FingerY = new float[maxFingers];
            FingerPressure = new float[maxFingers];
            FingerDown = new bool[maxFingers];
            FingerContactId = new int[maxFingers];
            for (int i = 0; i < maxFingers; i++)
                FingerContactId[i] = -1;
        }

        public TouchpadInputState Clone()
        {
            var c = new TouchpadInputState(MaxFingers);
            CopyInto(c);
            return c;
        }

        /// <summary>Deep-copies this pad's state into <paramref name="dst"/>,
        /// reallocating its finger arrays only on a shape change. The single
        /// field list shared with <see cref="Clone"/> (which delegates here)
        /// keeps the two mirrors from drifting.</summary>
        public void CopyInto(TouchpadInputState dst)
        {
            if (dst.MaxFingers != MaxFingers)
            {
                dst.MaxFingers = MaxFingers;
                dst.FingerX = new float[MaxFingers];
                dst.FingerY = new float[MaxFingers];
                dst.FingerPressure = new float[MaxFingers];
                dst.FingerDown = new bool[MaxFingers];
                dst.FingerContactId = new int[MaxFingers];
            }
            dst.Clicked = Clicked;
            if (MaxFingers > 0)
            {
                Array.Copy(FingerX, dst.FingerX, MaxFingers);
                Array.Copy(FingerY, dst.FingerY, MaxFingers);
                Array.Copy(FingerPressure, dst.FingerPressure, MaxFingers);
                Array.Copy(FingerDown, dst.FingerDown, MaxFingers);
                Array.Copy(FingerContactId, dst.FingerContactId, MaxFingers);
            }
        }

        /// <summary>Returns this pad to the fresh-constructed state without
        /// dropping its arrays (buffer-reuse support).</summary>
        public void ResetForReuse()
        {
            Clicked = false;
            if (MaxFingers > 0)
            {
                Array.Clear(FingerX, 0, MaxFingers);
                Array.Clear(FingerY, 0, MaxFingers);
                Array.Clear(FingerPressure, 0, MaxFingers);
                Array.Clear(FingerDown, 0, MaxFingers);
                // -1 = unassigned, matching the ctor: 0 is a VALID HID
                // contact id, so a zeroed reset would alias finger 0.
                for (int i = 0; i < MaxFingers; i++)
                    FingerContactId[i] = -1;
            }
        }
    }
}
