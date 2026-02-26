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
        public byte LeftTrigger;
        public byte RightTrigger;
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
        public const ushort SHARE = 0x0800;
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
}
