using System;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// A PlayStation digital pad polled through a Bliss-Box's native channel
    /// (issue #469), for the four directions the 3.x firmware's hat cannot
    /// carry together.
    ///
    /// <para>The poll is the pad's own: 0x01 (address the pad), 0x42 (read)
    /// and three bytes to clock the answer out, the start of the API Tool's
    /// talk.cs default for PlayStation pads. The pad answers its ID (0x41 for
    /// a digital pad or dance mat), 0x5A, then two button bytes, active low,
    /// in the psx-spx order the Linux psxpad-spi driver reads: the first
    /// holds Select, L3, R3, Start, Up, Right, Down, Left from bit 0. The
    /// reply is searched for the ID and 0x5A, so the decode holds whether or
    /// not the adapter returns the byte the pad drives while it receives
    /// 0x01.</para>
    /// </summary>
    public static class BlissBoxPsx
    {
        public const byte DigitalId = 0x41;
        public const byte Ready = 0x5A;

        public static byte[] PollMessage() => new byte[] { 0x01, 0x42, 0x00, 0x00, 0x00 };

        /// <summary>The four directions from a poll reply, in the firmware's
        /// arrow order: bit 0 up, 1 down, 2 left, 3 right.</summary>
        public static bool TryDecodeArrows(ReadOnlySpan<byte> reply, out byte arrows)
        {
            arrows = 0;
            for (int i = 0; i + 3 < reply.Length; i++)
            {
                if (reply[i] != DigitalId || reply[i + 1] != Ready) continue;
                byte first = (byte)~reply[i + 2];
                if ((first & 0x10) != 0) arrows |= 0x01; // up
                if ((first & 0x40) != 0) arrows |= 0x02; // down
                if ((first & 0x80) != 0) arrows |= 0x04; // left
                if ((first & 0x20) != 0) arrows |= 0x08; // right
                return true;
            }
            return false;
        }
    }
}
