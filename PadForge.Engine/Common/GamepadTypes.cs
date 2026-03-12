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
        }
    }

    /// <summary>
    /// Raw vJoy output state for custom (non-gamepad) configurations.
    /// Bypasses the fixed Gamepad struct to support arbitrary axis/button/POV counts.
    /// Axes are signed short range (-32768..32767), matching JoystickPositionV2 expectations.
    /// </summary>
    public struct VJoyRawState
    {
        /// <summary>Up to 8 axes (short range). Index = axis number.</summary>
        public short[] Axes;

        /// <summary>Button state as 4 × 32-bit words = 128 buttons max.</summary>
        public uint[] Buttons;

        /// <summary>Up to 4 POV hat switches. -1 = centered, 0-35900 = direction in hundredths of degrees.</summary>
        public int[] Povs;

        /// <summary>Creates a zeroed VJoyRawState with the specified capacities.</summary>
        public static VJoyRawState Create(int nAxes, int nButtons, int nPovs)
        {
            return new VJoyRawState
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

        /// <summary>Resets all axes to 0, buttons to 0, POVs to centered (-1).</summary>
        public void Clear()
        {
            if (Axes != null) Array.Clear(Axes, 0, Axes.Length);
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
            MouseButtons = 0;
            PreDzMouseDeltaX = PreDzMouseDeltaY = PreDzScrollDelta = 0;
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
                ScrollDelta = Math.Abs(a.ScrollDelta) >= Math.Abs(b.ScrollDelta) ? a.ScrollDelta : b.ScrollDelta,
                MouseButtons = (byte)(a.MouseButtons | b.MouseButtons),
                PreDzMouseDeltaX = Math.Abs(a.PreDzMouseDeltaX) >= Math.Abs(b.PreDzMouseDeltaX) ? a.PreDzMouseDeltaX : b.PreDzMouseDeltaX,
                PreDzMouseDeltaY = Math.Abs(a.PreDzMouseDeltaY) >= Math.Abs(b.PreDzMouseDeltaY) ? a.PreDzMouseDeltaY : b.PreDzMouseDeltaY,
                PreDzScrollDelta = Math.Abs(a.PreDzScrollDelta) >= Math.Abs(b.PreDzScrollDelta) ? a.PreDzScrollDelta : b.PreDzScrollDelta
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
        {
            int ccCount = a.CcValues?.Length ?? b.CcValues?.Length ?? 0;
            int noteCount = a.Notes?.Length ?? b.Notes?.Length ?? 0;
            var result = Create(ccCount, noteCount);

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
