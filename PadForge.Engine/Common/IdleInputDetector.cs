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
        /// (centered 32767), triggers on axes 2/5 (rest 0). Extra axes past 5
        /// (#193, e.g. DS3 pressure) and sliders have unknown rest positions,
        /// so when <paramref name="previous"/> is supplied they get the same
        /// change-detection test as the generic path, with the same documented
        /// limit: an extra axis held rock-steady off-rest reads idle.</summary>
        public static bool IsGamepadIdle(CustomInputState s, CustomInputState previous = null)
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

            if (previous != null)
            {
                for (int i = 6; i < s.Axis.Length && i < previous.Axis.Length; i++)
                    if (System.Math.Abs(s.Axis[i] - previous.Axis[i]) > DeltaSlop) return false;

                for (int i = 0; i < s.Sliders.Length && i < previous.Sliders.Length; i++)
                    if (System.Math.Abs(s.Sliders[i] - previous.Sliders[i]) > DeltaSlop) return false;
            }

            if (AnyFingerDown(s)) return false;

            if (PointerOrMouseActive(s)) return false;

            // An NFC tag held on the reader is a deliberate user action, so
            // it keeps a Bluetooth controller alive rather than letting it
            // idle-disconnect mid-tap (Codex #9).
            if (s.NfcTag != null)
                for (int i = 0; i < s.NfcTag.Length; i++)
                    if (s.NfcTag[i]) return false;

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

            if (MidiChanged(current.Midi, previous.Midi)) return false;

            if (AnyFingerDown(current)) return false;

            if (PointerOrMouseActive(current)) return false;

            return true;
        }

        /// <summary>MIDI (#128) is a real input family on this generic path: a
        /// Bluetooth MIDI controller played only through notes / CC (no buttons,
        /// axes, or touch) would otherwise read "unchanged" every poll and get
        /// disconnected mid-performance by the #162 countdown. A held note reads
        /// idle by the same documented limit as a held axis; active play does not.</summary>
        private static bool MidiChanged(MidiInputState current, MidiInputState previous)
        {
            if (current == null || previous == null) return false;

            if (current.Notes != null && previous.Notes != null)
                for (int i = 0; i < current.Notes.Length && i < previous.Notes.Length; i++)
                    if (current.Notes[i] != previous.Notes[i]) return true;

            if (current.Cc != null && previous.Cc != null)
                for (int i = 0; i < current.Cc.Length && i < previous.Cc.Length; i++)
                    if (current.Cc[i] != previous.Cc[i]) return true;

            if (System.Math.Abs(current.PitchBend - previous.PitchBend) > DeltaSlop) return true;

            // CcUp / CcDown are this-poll edge flags: any set means activity now.
            if (AnyTrue(current.CcUp) || AnyTrue(current.CcDown)) return true;

            return false;
        }

        private static bool AnyTrue(bool[] a)
        {
            if (a == null) return false;
            for (int i = 0; i < a.Length; i++) if (a[i]) return true;
            return false;
        }

        /// <summary>The post-3.5.0 pointer/mouse families count as activity:
        /// a user aiming only the Wii IR pointer (#146) or moving a Joy-Con 2
        /// as a mouse (#154) must not read as idle and get disconnected
        /// mid-use by the #162 countdown. IR is gated on Detected (dots in
        /// view), the mouse deltas are exactly 0 when the sensor is still.
        /// MouseRawDX/DY (#200) join the same family: raw counts are exactly
        /// 0 when the physical mouse is still.
        /// JoyConIrIntensity stays excluded like the motion sensors: it is a
        /// passive ambient-light scalar that never settles to a rest value.</summary>
        private static bool PointerOrMouseActive(CustomInputState s) =>
            s.Ir.Detected || s.JoyCon2MouseDX != 0f || s.JoyCon2MouseDY != 0f
            || s.MouseRawDX != 0 || s.MouseRawDY != 0;

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
