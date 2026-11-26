using System;

namespace PadForge.Engine.Recording
{
    /// <summary>
    /// What one virtual controller was handed, as a problem recording holds
    /// it (#600): every lane a controller of any type takes, since a slot's
    /// type can change while a recording runs. A lane the slot's type does
    /// not use stays at rest.
    ///
    /// <para>The mouse lanes that carry exact counts of one pass
    /// (<see cref="FlickX"/>, the gyro, touch and coast pairs) hold the last
    /// pass that had any. A reader that plays a file back takes them from
    /// the lines, each of which is one pass.</para>
    /// </summary>
    public sealed class RecordedOutputState
    {
        public const int RawAxisCount = 8;
        public const int RawButtonWords = 4;
        public const int RawHatCount = 4;
        public const int KeyWords = 4;
        public const int MidiCount = 128;

        /// <summary>The slot's <see cref="VirtualControllerType"/>, or -1
        /// before any line said.</summary>
        public int Type = -1;

        // The gamepad lane.
        public ushort Buttons, LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
        /// <summary>Bit 0 Share, 1 the microphone button, 2 and 3 the left
        /// and right back paddles, 4 and 5 the left and right function
        /// buttons.</summary>
        public int Extra;

        // The raw lane of Extended and Nintendo slots.
        public readonly short[] RawAxes = new short[RawAxisCount];
        public readonly uint[] RawButtons = new uint[RawButtonWords];
        public readonly int[] RawHats = { -1, -1, -1, -1 };

        // The keyboard and mouse lane.
        public readonly ulong[] Keys = new ulong[KeyWords];
        public int MouseButtons;
        public short MoveX, MoveY, Scroll, ScrollH;
        public float AbsoluteX, AbsoluteY;
        /// <summary>Bit 0 any axis, bit 1 X and bit 2 Y of the absolute
        /// pointer are in use.</summary>
        public int AbsoluteFlags;
        public int FlickX;
        public float GyroX, GyroY, TouchX, TouchY, CoastX, CoastY;

        // The MIDI lane.
        public readonly byte[] MidiCc = new byte[MidiCount];
        public readonly bool[] MidiNotes = new bool[MidiCount];

        // The VR lane.
        public VrHandRaw Left, Right;

        // The touchpad and motion a PlayStation or Extended slot carries.
        public bool TouchDown0, TouchDown1, TouchClick;
        public float TouchX0, TouchY0, TouchX1, TouchY1;
        public readonly float[] Gyro = new float[3];
        public readonly float[] Accel = new float[3];

        /// <summary>Back to rest: what a full frame starts from.</summary>
        public void Reset()
        {
            Type = -1;
            Buttons = LeftTrigger = RightTrigger = 0;
            LeftX = LeftY = RightX = RightY = 0;
            Extra = 0;
            Array.Clear(RawAxes);
            Array.Clear(RawButtons);
            for (int i = 0; i < RawHats.Length; i++) RawHats[i] = -1;
            Array.Clear(Keys);
            MouseButtons = 0;
            MoveX = MoveY = Scroll = ScrollH = 0;
            AbsoluteX = AbsoluteY = 0f;
            AbsoluteFlags = 0;
            FlickX = 0;
            GyroX = GyroY = TouchX = TouchY = CoastX = CoastY = 0f;
            Array.Clear(MidiCc);
            Array.Clear(MidiNotes);
            Left = default;
            Right = default;
            TouchDown0 = TouchDown1 = TouchClick = false;
            TouchX0 = TouchY0 = TouchX1 = TouchY1 = 0f;
            Array.Clear(Gyro);
            Array.Clear(Accel);
        }

        public void CopyInto(RecordedOutputState dst)
        {
            dst.Type = Type;
            dst.Buttons = Buttons; dst.LeftTrigger = LeftTrigger; dst.RightTrigger = RightTrigger;
            dst.LeftX = LeftX; dst.LeftY = LeftY; dst.RightX = RightX; dst.RightY = RightY;
            dst.Extra = Extra;
            Array.Copy(RawAxes, dst.RawAxes, RawAxisCount);
            Array.Copy(RawButtons, dst.RawButtons, RawButtonWords);
            Array.Copy(RawHats, dst.RawHats, RawHatCount);
            Array.Copy(Keys, dst.Keys, KeyWords);
            dst.MouseButtons = MouseButtons;
            dst.MoveX = MoveX; dst.MoveY = MoveY; dst.Scroll = Scroll; dst.ScrollH = ScrollH;
            dst.AbsoluteX = AbsoluteX; dst.AbsoluteY = AbsoluteY; dst.AbsoluteFlags = AbsoluteFlags;
            dst.FlickX = FlickX;
            dst.GyroX = GyroX; dst.GyroY = GyroY; dst.TouchX = TouchX; dst.TouchY = TouchY;
            dst.CoastX = CoastX; dst.CoastY = CoastY;
            Array.Copy(MidiCc, dst.MidiCc, MidiCount);
            Array.Copy(MidiNotes, dst.MidiNotes, MidiCount);
            dst.Left = Left; dst.Right = Right;
            dst.TouchDown0 = TouchDown0; dst.TouchDown1 = TouchDown1; dst.TouchClick = TouchClick;
            dst.TouchX0 = TouchX0; dst.TouchY0 = TouchY0; dst.TouchX1 = TouchX1; dst.TouchY1 = TouchY1;
            Array.Copy(Gyro, dst.Gyro, 3);
            Array.Copy(Accel, dst.Accel, 3);
        }
    }
}
