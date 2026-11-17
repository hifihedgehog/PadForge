namespace PadForge.Common.Input
{
    /// <summary>
    /// How many times Windows has said a device came, went or changed driver
    /// since PadForge started: each WM_DEVICECHANGE the main window hears. A
    /// thread that looks for a device can look again when this moves, rather
    /// than walking the system's devices on a timer.
    /// </summary>
    internal static class DeviceChanges
    {
        private static int s_count;

        /// <summary>The changes so far.</summary>
        internal static int Count => System.Threading.Volatile.Read(ref s_count);

        /// <summary>Windows said a device changed.</summary>
        internal static void Changed() => System.Threading.Interlocked.Increment(ref s_count);
    }
}
