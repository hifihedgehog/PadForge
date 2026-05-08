namespace PadForge.Engine
{
    /// <summary>
    /// Per-slot HID descriptor shape for the Extended (custom DirectInput)
    /// virtual controller path. Replaces the v2 ExtendedDeviceConfig struct that
    /// lived inside ExtendedVirtualController. The Step 3 → Step 5 pipeline reads
    /// these counts to translate per-axis/button/POV mappings into the
    /// corresponding raw HID report indices.
    /// </summary>
    public struct CustomControllerLayout
    {
        /// <summary>Total number of axis report fields (sticks*2 + triggers).</summary>
        public int Axes;

        /// <summary>Total number of button report fields.</summary>
        public int Buttons;

        /// <summary>Total number of POV (hat) report fields.</summary>
        public int Povs;

        /// <summary>Number of thumbsticks (each consumes 2 of the Axes count).</summary>
        public int Sticks;

        /// <summary>Number of triggers (each consumes 1 of the Axes count).</summary>
        public int Triggers;

        /// <summary>
        /// Returns <c>true</c> when <paramref name="axisIndex"/> in this layout
        /// holds a trigger axis (Z / Rz on the wire), <c>false</c> when it holds
        /// a stick axis (X / Y / Rx / Ry).
        ///
        /// <para>The interleaved layout produced by
        /// <c>ExtendedSlotConfig.ComputeAxisLayout</c> packs groups of
        /// <c>(stickX, stickY, trigger)</c> while both are available, then the
        /// trailing sticks (when sticks &gt; triggers) sequentially in pairs,
        /// then trailing triggers (when triggers &gt; sticks) one at a time.
        /// A trigger therefore lands either at position <c>3*g + 2</c> within
        /// the first <c>min(Sticks, Triggers) * 3</c> indices, or at
        /// <c>min*3 + (sticks-min)*2 + k</c> for the trailing-trigger tail.</para>
        ///
        /// <para>Sticks and triggers need different rest-state and combine
        /// rules, so several call sites in the Extended pipeline (mapping,
        /// deadzone application, multi-device merge) need to distinguish
        /// trigger slots from stick slots. Centralising the formula here
        /// keeps those call sites in agreement; a layout edit only changes
        /// this one method.</para>
        /// </summary>
        public bool IsTriggerSlot(int axisIndex)
        {
            if (axisIndex < 0) return false;

            int interleave = System.Math.Min(Sticks, Triggers);

            // Interleaved zone: every third axis (offset 2 within the group)
            // is a trigger.
            if (axisIndex < interleave * 3)
                return (axisIndex % 3) == 2;

            // Trailing-stick zone: when sticks > triggers, sticks-triggers
            // pairs of (X, Y) follow the interleave block. None are triggers.
            int trailingStickEnd = interleave * 3 + System.Math.Max(0, Sticks - interleave) * 2;
            if (axisIndex < trailingStickEnd)
                return false;

            // Trailing-trigger zone: when triggers > sticks, the remaining
            // triggers pack one index at a time after every stick is placed.
            int trailingTriggerCount = System.Math.Max(0, Triggers - interleave);
            return axisIndex < trailingStickEnd + trailingTriggerCount;
        }
    }
}
