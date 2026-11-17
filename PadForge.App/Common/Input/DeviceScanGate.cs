namespace PadForge.Common.Input
{
    /// <summary>
    /// When a thread that walks the system's devices should walk them again:
    /// after Windows said a device changed (<see cref="DeviceChanges"/>), and
    /// once every <see cref="RescanMs"/> all the same, for a change no window
    /// heard. A walk asks Windows for every device of a class and reads each
    /// one's ids, which costs a burst of garbage for the collector. Run on a
    /// timer with nothing changed, such walks were most of what the program
    /// threw away while it sat still. Used from one thread.
    /// </summary>
    internal sealed class DeviceScanGate
    {
        private bool _walked;
        private int _changes;
        private long _walkedAt;

        /// <param name="rescanMs">The longest time between two walks.</param>
        internal DeviceScanGate(long rescanMs) => RescanMs = rescanMs;

        /// <summary>The longest time between two walks.</summary>
        internal long RescanMs { get; }

        /// <summary>Whether to walk now. A yes counts as the walk.</summary>
        internal bool Due(long nowMs) => Due(nowMs, DeviceChanges.Count);

        /// <summary>Whether to walk now, given the changes Windows has
        /// reported so far. A yes counts as the walk.</summary>
        internal bool Due(long nowMs, int changes)
        {
            if (_walked && changes == _changes && nowMs - _walkedAt < RescanMs) return false;
            _walked = true;
            _changes = changes;
            _walkedAt = nowMs;
            return true;
        }
    }
}
