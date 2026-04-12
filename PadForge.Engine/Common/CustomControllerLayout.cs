namespace PadForge.Engine
{
    /// <summary>
    /// Per-slot HID descriptor shape for the Extended (custom DirectInput)
    /// virtual controller path. Replaces the v2 VJoyDeviceConfig struct that
    /// lived inside VJoyVirtualController. The Step 3 → Step 5 pipeline reads
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
    }
}
