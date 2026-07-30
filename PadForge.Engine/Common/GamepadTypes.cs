namespace PadForge.Engine
{
    /// <summary>
    /// Minimal Gamepad struct matching XInput XINPUT_GAMEPAD layout.
    /// Used as the output of the mapping pipeline (Step 3 → Step 4 → Step 5).
    ///
    /// Lives in the Engine assembly so both Engine (UserSetting.OutputState) and
    /// App (InputManager, PadViewModel) can reference it.
    /// </summary>
    public struct Gamepad
    {
        public ushort Buttons;
        public ushort LeftTrigger;
        public ushort RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;

        /// <summary>Xbox Series Share button. Outside the 16-bit Buttons
        /// mask because all 16 XInput-equivalent bits are taken; HM exposes
        /// it as <c>HMButton.Share</c> (bit 12) on Xbox Series profiles.</summary>
        public bool Share;

        // Button flag constants
        public const ushort DPAD_UP = 0x0001;
        public const ushort DPAD_DOWN = 0x0002;
        public const ushort DPAD_LEFT = 0x0004;
        public const ushort DPAD_RIGHT = 0x0008;
        public const ushort START = 0x0010;
        public const ushort BACK = 0x0020;
        public const ushort LEFT_THUMB = 0x0040;
        public const ushort RIGHT_THUMB = 0x0080;
        public const ushort LEFT_SHOULDER = 0x0100;
        public const ushort RIGHT_SHOULDER = 0x0200;
        public const ushort GUIDE = 0x0400;
        public const ushort TOUCHPAD = 0x0800;  // PlayStation slots only, used by macros
        public const ushort A = 0x1000;
        public const ushort B = 0x2000;
        public const ushort X = 0x4000;
        public const ushort Y = 0x8000;

        /// <summary>Returns true if the specified button flag is set.</summary>
        public bool IsButtonPressed(ushort flag) => (Buttons & flag) != 0;

        /// <summary>Sets or clears a button flag.</summary>
        public void SetButton(ushort flag, bool pressed)
        {
            if (pressed)
                Buttons |= flag;
            else
                Buttons &= (ushort)~flag;
        }

        /// <summary>Resets all fields to zero.</summary>
        public void Clear()
        {
            Buttons = 0;
            LeftTrigger = 0;
            RightTrigger = 0;
            ThumbLX = 0;
            ThumbLY = 0;
            ThumbRX = 0;
            ThumbRY = 0;
            Share = false;
        }
    }

    /// <summary>
    /// Touchpad state for virtual PlayStation output via DS4_REPORT_EX.
    /// Coordinates are normalized 0-1, matching SDL3 touchpad API output.
    /// </summary>
    public struct TouchpadState
    {
        /// <summary>Finger 0 X position (0-1 normalized, left to right).</summary>
        public float X0;
        /// <summary>Finger 0 Y position (0-1 normalized, top to bottom).</summary>
        public float Y0;
        /// <summary>Finger 1 X position.</summary>
        public float X1;
        /// <summary>Finger 1 Y position.</summary>
        public float Y1;
        /// <summary>Finger 0 contact state.</summary>
        public bool Down0;
        /// <summary>Finger 1 contact state.</summary>
        public bool Down1;
        /// <summary>Touchpad click button.</summary>
        public bool Click;
        /// <summary>Increments on each finger down/up transition (for DS4_TOUCH encoding).</summary>
        public byte PacketCounter;
    }

    /// <summary>
    /// Raw Extended output state for custom (non-gamepad) configurations.
    /// Bypasses the fixed Gamepad struct to support arbitrary axis/button/POV counts.
    /// Axes are signed short range (-32768..32767), matching JoystickPositionV2 expectations.
    /// </summary>
    public struct RawHidState
    {
        /// <summary>Up to 8 axes (short range). Index = axis number.</summary>
        public short[] Axes;

        /// <summary>Button state as 4 × 32-bit words = 128 buttons max.</summary>
        public uint[] Buttons;

        /// <summary>Up to 4 POV hat switches. -1 = centered, 0-35900 = direction in hundredths of degrees.</summary>
        public int[] Povs;

        /// <summary>Pre-tuning snapshot of <see cref="Axes"/>, taken before
        /// center offset / boundary reshape / deadzone / curve so the
        /// calibration capture and the preview's cold dot read the frame the
        /// samples were recorded in. Runtime-only and intentionally absent
        /// from every wire/persistence mirror: null when the producer did
        /// not populate it, in which case consumers fall back to Axes.</summary>
        public short[] HardwareAxes;

        /// <summary>Creates a zeroed RawHidState with the specified capacities.</summary>
        public static RawHidState Create(int nAxes, int nButtons, int nPovs)
        {
            return new RawHidState
            {
                Axes = new short[Math.Min(nAxes, 8)],
                Buttons = new uint[(Math.Min(nButtons, 128) + 31) / 32],
                Povs = new int[Math.Min(nPovs, 4)]
            };
        }

        /// <summary>Sets the specified button (0-based index).</summary>
        public void SetButton(int index, bool pressed)
        {
            if (Buttons == null || index < 0) return;
            int word = index / 32;
            int bit = index % 32;
            if (word >= Buttons.Length) return;
            if (pressed)
                Buttons[word] |= (uint)(1 << bit);
            else
                Buttons[word] &= ~(uint)(1 << bit);
        }

        /// <summary>Returns true if the specified button is pressed.</summary>
        public bool IsButtonPressed(int index)
        {
            if (Buttons == null || index < 0) return false;
            int word = index / 32;
            int bit = index % 32;
            if (word >= Buttons.Length) return false;
            return (Buttons[word] & (uint)(1 << bit)) != 0;
        }

        /// <summary>Resets all axes to 0, buttons to 0, POVs to centered (-1).
        /// HardwareAxes is the pre-tuning mirror of Axes and clears with it:
        /// leaving it behind meant a cleared state still carried the last
        /// stick sample, which the Pad page reads back as live input.</summary>
        public void Clear()
        {
            if (Axes != null) Array.Clear(Axes, 0, Axes.Length);
            if (HardwareAxes != null) Array.Clear(HardwareAxes, 0, HardwareAxes.Length);
            if (Buttons != null) Array.Clear(Buttons, 0, Buttons.Length);
            if (Povs != null)
                for (int i = 0; i < Povs.Length; i++)
                    Povs[i] = -1;
        }
    }

    /// <summary>
    /// Raw keyboard + mouse output state for the KeyboardMouse virtual controller type.
    /// Key states are packed into 4 × 64-bit words = 256 virtual key codes.
    /// Mouse axes are signed short range for delta movement per frame.
    /// </summary>
    public struct KbmRawState
    {
        /// <summary>256 virtual key states packed into 4 ulongs.</summary>
        public ulong Keys0, Keys1, Keys2, Keys3;

        /// <summary>Mouse X delta (signed, pixels per frame).</summary>
        public short MouseDeltaX;

        /// <summary>Mouse Y delta (signed, pixels per frame).</summary>
        public short MouseDeltaY;

        /// <summary>Mouse scroll delta (signed, positive = up).</summary>
        public short ScrollDelta;

        /// <summary>Mouse button states: bit 0 = LMB, bit 1 = RMB, bit 2 = MMB, bit 3 = X1, bit 4 = X2.</summary>
        public byte MouseButtons;

        /// <summary>Mouse X delta before center offset + deadzone (for stick preview).</summary>
        public short PreDzMouseDeltaX;

        /// <summary>Mouse Y delta before center offset + deadzone (for stick preview).</summary>
        public short PreDzMouseDeltaY;

        /// <summary>Scroll delta before deadzone (for stick preview).</summary>
        public short PreDzScrollDelta;

        /// <summary>Horizontal scroll delta, positive = scroll right (issue
        /// #154, the office-mouse tilt wheel). Same signed-axis semantics as
        /// <see cref="ScrollDelta"/>; the KBM virtual controller sends it as
        /// MOUSEEVENTF_HWHEEL.</summary>
        public short ScrollDeltaH;
        public short PreDzScrollDeltaH;

        /// <summary>Absolute pointer aim, normalized [-1..+1] screen-aligned
        /// (issue #146: Wii IR pointing). When <see cref="MouseAbsValid"/> is
        /// true the KBM virtual controller positions the OS cursor here
        /// (Touchmote MouseSimulator SetCursorPos idiom) instead of integrating
        /// MouseDelta velocity. Valid only while the camera sees the sensor
        /// bar; on sight loss the cursor holds its last position.</summary>
        public float MouseAbsX;
        public float MouseAbsY;
        public bool MouseAbsValid;

        /// <summary>Per-axis validity for the absolute pointer. A mixed
        /// mapping (IR on one mouse axis, a stick on the other) drives only
        /// one absolute coordinate; the consumer must not recenter the
        /// un-driven axis from its 0f default. <see cref="MouseAbsValid"/>
        /// stays the any-axis OR for the mode/freeze checks.</summary>
        public bool MouseAbsXValid;
        public bool MouseAbsYValid;

        /// <summary>Flick stick mouse X counts for this frame (#225):
        /// EXACT relative counts, already calibrated by the source's
        /// counts-per-360, positive = rightward turn. The KBM virtual
        /// controller forwards these to the injector 1:1, bypassing the
        /// <see cref="MouseDeltaX"/> velocity lane's sensitivity scale and
        /// short clamp (a 180-degree flick at 14400 counts/360 needs ~7200
        /// counts inside ~100 ms, far past what the velocity lane can
        /// represent).</summary>
        public int MouseFlickX;

        /// <summary>Gyro mouse motion in exact mouse counts for this poll,
        /// its own lane beside MouseFlickX and for the same reason: the
        /// value is calibrated counts, not a [-1..+1] deflection. Fractional
        /// because the sub-count part is what small rotations live in; the
        /// KBM controller carries the remainder. See
        /// SourceCoercion.ReadGyroMouseCounts.</summary>
        public float MouseGyroX;
        public float MouseGyroY;

        /// <summary>Touchpad mouse motion in exact counts for this poll, the
        /// gyro lane's twin. The deflection lane recomputed a delta once per
        /// poll from a position that only changes on a device report, so
        /// three polls in four read zero and the fourth carried a burst that
        /// was then clamped and rationed. See
        /// SourceCoercion.ReadTouchpadMouseCounts.</summary>
        public float MouseTouchX;
        public float MouseTouchY;

        public bool GetKey(byte vk)
        {
            int word = vk / 64;
            int bit = vk % 64;
            return word switch
            {
                0 => (Keys0 & (1UL << bit)) != 0,
                1 => (Keys1 & (1UL << bit)) != 0,
                2 => (Keys2 & (1UL << bit)) != 0,
                3 => (Keys3 & (1UL << bit)) != 0,
                _ => false
            };
        }

        public void SetKey(byte vk, bool pressed)
        {
            int word = vk / 64;
            int bit = vk % 64;
            ulong mask = 1UL << bit;
            switch (word)
            {
                case 0: if (pressed) Keys0 |= mask; else Keys0 &= ~mask; break;
                case 1: if (pressed) Keys1 |= mask; else Keys1 &= ~mask; break;
                case 2: if (pressed) Keys2 |= mask; else Keys2 &= ~mask; break;
                case 3: if (pressed) Keys3 |= mask; else Keys3 &= ~mask; break;
            }
        }

        public bool GetMouseButton(int index) => (MouseButtons & (1 << index)) != 0;

        public void SetMouseButton(int index, bool pressed)
        {
            if (pressed) MouseButtons |= (byte)(1 << index);
            else MouseButtons &= (byte)~(1 << index);
        }

        public void Clear()
        {
            Keys0 = Keys1 = Keys2 = Keys3 = 0;
            MouseDeltaX = MouseDeltaY = ScrollDelta = 0;
            ScrollDeltaH = PreDzScrollDeltaH = 0;
            MouseButtons = 0;
            PreDzMouseDeltaX = PreDzMouseDeltaY = PreDzScrollDelta = 0;
            MouseAbsX = MouseAbsY = 0f;
            MouseAbsValid = false;
            MouseAbsXValid = MouseAbsYValid = false;
            MouseFlickX = 0;
            MouseGyroX = MouseGyroY = 0f;
            MouseTouchX = MouseTouchY = 0f;
        }

        /// <summary>
        /// Combines two KBM states. Keys and mouse buttons are OR'd.
        /// Mouse deltas take the largest magnitude value.
        /// </summary>
        public static KbmRawState Combine(KbmRawState a, KbmRawState b)
        {
            return new KbmRawState
            {
                Keys0 = a.Keys0 | b.Keys0,
                Keys1 = a.Keys1 | b.Keys1,
                Keys2 = a.Keys2 | b.Keys2,
                Keys3 = a.Keys3 | b.Keys3,
                MouseDeltaX = Math.Abs(a.MouseDeltaX) >= Math.Abs(b.MouseDeltaX) ? a.MouseDeltaX : b.MouseDeltaX,
                MouseDeltaY = Math.Abs(a.MouseDeltaY) >= Math.Abs(b.MouseDeltaY) ? a.MouseDeltaY : b.MouseDeltaY,
                // Flick counts: max-abs like the deltas. Every device pass in
                // a frame replays the SAME per-row counts (TickFlickStick's
                // frame-sequence guard), so max-abs merges duplicates without
                // double-counting.
                MouseFlickX = Math.Abs(a.MouseFlickX) >= Math.Abs(b.MouseFlickX) ? a.MouseFlickX : b.MouseFlickX,
                // Counts SUM rather than taking the larger: two gyros aimed
                // at one slot each contribute their real motion, the way two
                // hands on one controller would.
                MouseGyroX = a.MouseGyroX + b.MouseGyroX,
                MouseGyroY = a.MouseGyroY + b.MouseGyroY,
                MouseTouchX = a.MouseTouchX + b.MouseTouchX,
                MouseTouchY = a.MouseTouchY + b.MouseTouchY,
                ScrollDelta = Math.Abs(a.ScrollDelta) >= Math.Abs(b.ScrollDelta) ? a.ScrollDelta : b.ScrollDelta,
                MouseButtons = (byte)(a.MouseButtons | b.MouseButtons),
                PreDzMouseDeltaX = Math.Abs(a.PreDzMouseDeltaX) >= Math.Abs(b.PreDzMouseDeltaX) ? a.PreDzMouseDeltaX : b.PreDzMouseDeltaX,
                PreDzMouseDeltaY = Math.Abs(a.PreDzMouseDeltaY) >= Math.Abs(b.PreDzMouseDeltaY) ? a.PreDzMouseDeltaY : b.PreDzMouseDeltaY,
                PreDzScrollDelta = Math.Abs(a.PreDzScrollDelta) >= Math.Abs(b.PreDzScrollDelta) ? a.PreDzScrollDelta : b.PreDzScrollDelta,
                ScrollDeltaH = Math.Abs(a.ScrollDeltaH) >= Math.Abs(b.ScrollDeltaH) ? a.ScrollDeltaH : b.ScrollDeltaH,
                PreDzScrollDeltaH = Math.Abs(a.PreDzScrollDeltaH) >= Math.Abs(b.PreDzScrollDeltaH) ? a.PreDzScrollDeltaH : b.PreDzScrollDeltaH,
                // Absolute pointer: whichever side is tracking wins (only one
                // IR-pointing device feeds a slot in practice). Per axis, so
                // a mixed mapping's un-driven coordinate never masks the
                // other device's tracked one.
                MouseAbsX = a.MouseAbsXValid ? a.MouseAbsX : b.MouseAbsX,
                MouseAbsY = a.MouseAbsYValid ? a.MouseAbsY : b.MouseAbsY,
                MouseAbsXValid = a.MouseAbsXValid || b.MouseAbsXValid,
                MouseAbsYValid = a.MouseAbsYValid || b.MouseAbsYValid,
                MouseAbsValid = a.MouseAbsValid || b.MouseAbsValid
            };
        }
    }

    /// <summary>
    /// Raw MIDI output state with dynamic CC and note counts.
    /// CC values are 0-127 (MIDI range). Notes are boolean (on/off).
    /// </summary>
    public struct MidiRawState
    {
        public byte[] CcValues;
        public bool[] Notes;

        public static MidiRawState Create(int ccCount, int noteCount)
        {
            return new MidiRawState
            {
                CcValues = new byte[ccCount],
                Notes = new bool[noteCount]
            };
        }

        public void Clear()
        {
            if (CcValues != null)
                for (int i = 0; i < CcValues.Length; i++)
                    CcValues[i] = 64; // center value
            if (Notes != null)
                for (int i = 0; i < Notes.Length; i++)
                    Notes[i] = false;
        }

        /// <summary>
        /// Combines two MIDI raw states. CCs take the value furthest from center; notes are OR'd.
        /// </summary>
        public static MidiRawState Combine(MidiRawState a, MidiRawState b)
            => CombineInto(a, b, default);

        /// <summary>Combine writing into a caller-owned <paramref name="dest"/>,
        /// so a repeated combine can reuse one buffer instead of allocating a
        /// byte[] and a bool[] per call. Pass default to allocate, which is
        /// what the two-argument overload does.
        ///
        /// <para>MidiRawState is a STRUCT holding array references, so the
        /// "allocate one" sentinel is default (null arrays), and writing through
        /// result.CcValues mutates the arrays the caller owns, which is the
        /// point.</para>
        ///
        /// <para>dest MAY be the same instance as <paramref name="a"/>: both
        /// loops read index i from a and b and then write index i of the
        /// result, with no cross-index reads, so an in-place destination cannot
        /// disturb a value still to be read. dest must NOT be a device's
        /// published state, nor anything another slot or thread reads. The
        /// caller owns it.</para></summary>
        public static MidiRawState CombineInto(MidiRawState a, MidiRawState b, MidiRawState dest)
        {
            int ccCount = a.CcValues?.Length ?? b.CcValues?.Length ?? 0;
            int noteCount = a.Notes?.Length ?? b.Notes?.Length ?? 0;
            var result = (dest.CcValues != null && dest.CcValues.Length == ccCount
                          && dest.Notes != null && dest.Notes.Length == noteCount)
                ? dest
                : Create(ccCount, noteCount);

            for (int i = 0; i < ccCount; i++)
            {
                byte va = (a.CcValues != null && i < a.CcValues.Length) ? a.CcValues[i] : (byte)64;
                byte vb = (b.CcValues != null && i < b.CcValues.Length) ? b.CcValues[i] : (byte)64;
                int distA = Math.Abs(va - 64);
                int distB = Math.Abs(vb - 64);
                result.CcValues[i] = distA >= distB ? va : vb;
            }

            for (int i = 0; i < noteCount; i++)
            {
                bool na = a.Notes != null && i < a.Notes.Length && a.Notes[i];
                bool nb = b.Notes != null && i < b.Notes.Length && b.Notes[i];
                result.Notes[i] = na || nb;
            }

            return result;
        }
    }
}
