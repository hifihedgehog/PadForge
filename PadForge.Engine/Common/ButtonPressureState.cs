namespace PadForge.Engine.Common
{
    /// <summary>
    /// How hard ten buttons are pressed, 0 (released) to 255 (fully pressed),
    /// in the order of <see cref="PadForge.Engine.Data.MappingSetMigrator.PressureTargets"/>:
    /// cross, circle, square, triangle, L1, R1, then the D-pad (discussion
    /// #476). HIDMaestro's DualShock 3 (SIXAXIS): Full preset sends them in
    /// its pressure fields. A value type, so the poll thread copies it
    /// without allocating.
    /// </summary>
    public struct ButtonPressureState
    {
        public const int Count = 10;

        public byte ButtonA, ButtonB, ButtonX, ButtonY;
        public byte LeftShoulder, RightShoulder;
        public byte DPadUp, DPadDown, DPadLeft, DPadRight;

        public byte this[int index]
        {
            readonly get => index switch
            {
                0 => ButtonA,
                1 => ButtonB,
                2 => ButtonX,
                3 => ButtonY,
                4 => LeftShoulder,
                5 => RightShoulder,
                6 => DPadUp,
                7 => DPadDown,
                8 => DPadLeft,
                9 => DPadRight,
                _ => 0,
            };
            set
            {
                switch (index)
                {
                    case 0: ButtonA = value; break;
                    case 1: ButtonB = value; break;
                    case 2: ButtonX = value; break;
                    case 3: ButtonY = value; break;
                    case 4: LeftShoulder = value; break;
                    case 5: RightShoulder = value; break;
                    case 6: DPadUp = value; break;
                    case 7: DPadDown = value; break;
                    case 8: DPadLeft = value; break;
                    case 9: DPadRight = value; break;
                }
            }
        }

        /// <summary>The harder press of each pair, the rule triggers combine
        /// by across a slot's devices.</summary>
        public static ButtonPressureState Max(in ButtonPressureState a, in ButtonPressureState b)
        {
            var r = a;
            for (int i = 0; i < Count; i++)
                if (b[i] > r[i]) r[i] = b[i];
            return r;
        }
    }
}
