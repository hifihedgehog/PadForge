namespace PadForge.Engine
{
    /// <summary>
    /// Motion facts for the DualShock 3 and Sixaxis (054C:0268) that both of
    /// PadForge's paths share: DsHidMini's SXS mode through the SDL fork's
    /// sixaxis driver, and the BthPS3 or WinUSB path through Ds3DirectService.
    /// A Remote Link copy keeps the same identity. The pad has one gyro axis,
    /// yaw, read as a 10-bit word centered on 512, and both paths publish
    /// pitch and roll as zero.
    /// </summary>
    public static class DualShock3Motion
    {
        public const ushort VendorId = 0x054C;
        public const ushort ProductId = 0x0268;

        public static bool Is(ushort vendorId, ushort productId)
            => vendorId == VendorId && productId == ProductId;

        /// <summary>Yaw scale in rad/s per count: 123 counts per 90 deg/s,
        /// schmaldeo DS4Windows DS4Sixaxis.cs:450, the scale the SDL fork's
        /// sixaxis driver uses too. DsHidMini's four-pad hand measurement
        /// puts it at about 1.4 counts per deg/s, within 15% (docs/MOTION.md,
        /// Units).</summary>
        public const float GyroRadPerCount = (90.0f / 123.0f) * ((float)System.Math.PI / 180.0f);

        /// <summary>A yaw word resting within this many counts of 0 or 1023
        /// belongs to a dead or saturated part: one direction cannot
        /// register, and no calibration recovers it.</summary>
        public const int GyroRailMargin = 24;

        /// <summary>The largest resting offset, in counts from the 512
        /// center, that a press of Calibrate Gyro accepts. Words within
        /// <see cref="GyroRailMargin"/> of either end are a dead or
        /// saturated part, which leaves 998 the highest usable word, 486
        /// above center. The same bound below center refuses word 25 as
        /// well, so the bound holds whichever way a path signs the word.</summary>
        public const int MaxRestingYawCounts = 1023 - GyroRailMargin - 1 - 512;

        /// <summary><see cref="MaxRestingYawCounts"/> in rad/s, about 6.2.
        /// The pad keeps a factory zero in EEPROM page 0xA0, which
        /// sixaxis.sys and DsHidMini 3.15.0 and later apply and
        /// Ds3DirectService does not, and units on record rest at words 724
        /// and 727, 2.7 rad/s while still.</summary>
        public const float MaxRestingYawBias = MaxRestingYawCounts * GyroRadPerCount;
    }
}
