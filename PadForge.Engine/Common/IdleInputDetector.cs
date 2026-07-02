namespace PadForge.Engine.Common
{
    /// <summary>
    /// Pure idle test for the #162 idle disconnect countdown, the DS4Windows
    /// <c>isDS4Idle()</c> shape generalized to PadForge's normalized state.
    /// A device is idle when no button is pressed, no POV is deflected, no
    /// touchpad finger is down, both sticks sit inside a slop band around
    /// center, and the triggers sit near rest. Motion sensors are
    /// deliberately ignored, as DS4Windows ignores them: gyro noise never
    /// settles and would defeat the countdown forever.
    /// </summary>
    public static class IdleInputDetector
    {
        /// <summary>Stick slop around the 32767 center. DS4Windows uses 64 of
        /// a 128 half-range (DS4Device.cs isDS4Idle, slop = 64); the same
        /// fraction of our 32767 half-range is 16384.</summary>
        public const int StickSlop = 16384;

        /// <summary>Trigger slop above 0 rest. DS4Windows treats any nonzero
        /// trigger as active; a small band absorbs worn-pot jitter on
        /// hardware that never reads a clean zero.</summary>
        public const int TriggerSlop = 1024;

        /// <summary>Axis-delta slop for the generic (unknown-layout) test.</summary>
        public const int DeltaSlop = 1024;

        /// <summary>Absolute idle test for gamepad-typed devices, whose axis
        /// layout is the auto-map convention: sticks on axes 0/1/3/4
        /// (centered 32767), triggers on axes 2/5 (rest 0).</summary>
        public static bool IsGamepadIdle(CustomInputState s)
        {
            if (s == null) return true;

            for (int i = 0; i < s.Buttons.Length; i++)
                if (s.Buttons[i]) return false;

            for (int i = 0; i < s.Povs.Length; i++)
                if (s.Povs[i] >= 0) return false;

            if (!StickCentered(s.Axis[0]) || !StickCentered(s.Axis[1])
                || !StickCentered(s.Axis[3]) || !StickCentered(s.Axis[4]))
                return false;

            if (s.Axis[2] > TriggerSlop || s.Axis[5] > TriggerSlop)
                return false;

            if (AnyFingerDown(s)) return false;

            return true;
        }

        /// <summary>Change-detection idle test for devices whose axis layout
        /// and rest positions are unknown (raw joysticks, wheels, remotes).
        /// Idle means "nothing moved since the previous poll" within a small
        /// slop. Known limit, stated: an axis held rock-steady off-rest reads
        /// idle. The gamepad path above does not have this limit.</summary>
        public static bool IsUnchanged(CustomInputState current, CustomInputState previous)
        {
            if (current == null || previous == null) return true;

            for (int i = 0; i < current.Buttons.Length && i < previous.Buttons.Length; i++)
                if (current.Buttons[i] != previous.Buttons[i]) return false;

            for (int i = 0; i < current.Povs.Length && i < previous.Povs.Length; i++)
                if (current.Povs[i] != previous.Povs[i]) return false;

            for (int i = 0; i < current.Axis.Length && i < previous.Axis.Length; i++)
                if (System.Math.Abs(current.Axis[i] - previous.Axis[i]) > DeltaSlop) return false;

            for (int i = 0; i < current.Sliders.Length && i < previous.Sliders.Length; i++)
                if (System.Math.Abs(current.Sliders[i] - previous.Sliders[i]) > DeltaSlop) return false;

            if (AnyFingerDown(current)) return false;

            return true;
        }

        // DS4Windows treats LX <= 127-slop or LX >= 128+slop as active
        // (strictly outside the band is centered), scaled to 16-bit.
        private static bool StickCentered(int axis) =>
            axis > 32767 - StickSlop && axis < 32768 + StickSlop;

        private static bool AnyFingerDown(CustomInputState s)
        {
            if (s.Touchpads == null) return false;
            for (int p = 0; p < s.Touchpads.Length; p++)
            {
                var pad = s.Touchpads[p];
                if (pad?.FingerDown == null) continue;
                for (int f = 0; f < pad.FingerDown.Length; f++)
                    if (pad.FingerDown[f]) return true;
                if (pad.Clicked) return true;
            }
            return false;
        }
    }
}
