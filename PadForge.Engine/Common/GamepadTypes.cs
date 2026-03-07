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
    /// Represents the raw XInput state as returned by XInputGetStateEx.
    /// Layout matches XINPUT_STATE (packet number + Gamepad).
    /// </summary>
    public struct XInputState
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
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
}
