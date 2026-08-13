using System;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Instance ids for devices SDL did not open: web pads, MIDI endpoints, NFC
    /// readers, headset motion sources, remote peer devices.
    ///
    /// Two properties matter, and the hand-rolled <c>identity.GetHashCode()</c>
    /// each of these used had neither.
    ///
    /// SEPARATE FROM SDL'S RANGE. Step 1 looks devices up by instance id across
    /// the WHOLE device list, so a synthetic id that lands on a real joystick's
    /// id makes the reconcile pass act on the wrong device. SDL hands out small
    /// ascending integers, so every synthetic id is placed in the top half of
    /// the range, which SDL never reaches.
    ///
    /// STABLE. String.GetHashCode is randomised per process in .NET Core, so
    /// the same MIDI endpoint or web client got a different id on every launch.
    /// FNV-1a is a fixed function of the bytes, so the same identity always
    /// yields the same id, in this run and the next.
    /// </summary>
    public static class SyntheticInstanceId
    {
        /// <summary>First id in the reserved band. SDL's ids ascend from 1 and
        /// never approach this.</summary>
        public const uint ReservedBase = 0x8000_0000u;

        /// <summary>A stable id in the reserved band for an identity string
        /// (device path, endpoint id, client id).</summary>
        public static uint From(string identity)
        {
            // FNV-1a, 32-bit.
            uint hash = 2166136261u;
            if (!string.IsNullOrEmpty(identity))
            {
                var bytes = Encoding.UTF8.GetBytes(identity);
                foreach (byte b in bytes)
                {
                    hash ^= b;
                    hash *= 16777619u;
                }
            }
            return ReservedBase | (hash & 0x7FFF_FFFFu);
        }

        /// <summary>True when an id came from here rather than from SDL.</summary>
        public static bool IsSynthetic(uint id) => (id & ReservedBase) != 0;
    }
}
