using System.Collections.Generic;

namespace PadForge.Engine
{
    /// <summary>
    /// Single source of truth for "is this a Microsoft Xbox One+ controller
    /// that accepts the 9-byte impulse-trigger HID output report shape."
    /// Used by <see cref="SdlDeviceWrapper"/> to force-enable
    /// <c>HasRumbleTriggers</c> on these devices (irrespective of what the
    /// SDL backend property reports), and by
    /// <c>PadForge.Common.Input.XboxImpulseHidWriter</c> to gate the raw
    /// HID write path.
    ///
    /// Xbox 360 PIDs (e.g. 0x028E) are deliberately excluded — those
    /// controllers don't have trigger motors. Kept here so the two
    /// consumers can't drift; modifying the set is a one-place edit.
    /// </summary>
    public static class XboxControllerIdentity
    {
        public const ushort MicrosoftVid = 0x045E;

        private static readonly HashSet<ushort> ImpulseTriggerPids = new()
        {
            0x02D1, // Xbox One (Wired, original 2013)
            0x02DD, // Xbox One (Wired, 2015 firmware)
            0x02E0, // Xbox One S (Bluetooth)
            0x02E3, // Xbox Elite (Wired)
            0x02EA, // Xbox One S (Wireless via Xbox Wireless Adapter)
            0x02FD, // Xbox One S (Bluetooth, alternate firmware)
            0x02FF, // Xbox One Elite (Wired)
            0x0B00, // Xbox Elite Series 2 (Wired)
            0x0B05, // Xbox Elite Series 2 (Bluetooth)
            0x0B12, // Xbox Series X|S (Wireless via Xbox Wireless Adapter)
            0x0B13, // Xbox Series X|S (Bluetooth)
        };

        public static bool IsImpulseTriggerDevice(ushort vid, ushort pid)
            => vid == MicrosoftVid && ImpulseTriggerPids.Contains(pid);
    }
}
