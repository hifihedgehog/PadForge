namespace PadForge.Common.Input
{
    /// <summary>
    /// When a thread that walks the system's devices should walk them again:
    /// on every ask for <see cref="SettleMs"/> after Windows said a device
    /// changed (<see cref="DeviceChanges"/>), counted from the last change
    /// when several come, and once every <see cref="RescanMs"/> all the same,
    /// for a change no window heard. A device can bring an interface, a
    /// failed start or a Raw Input entry after the last notice Windows sends
    /// for it, so a single walk at the notice can miss what a walk at the
    /// thread's own interval a few seconds later finds. A walk asks Windows
    /// for every device of a class and reads each one's ids, which costs a
    /// burst of garbage for the collector. Run on a timer with nothing
    /// changed, such walks were most of what the program threw away while it
    /// sat still. Used from one thread.
    /// </summary>
    internal sealed class DeviceScanGate
    {
        /// <summary>How long after a device change every ask walks. A headset
        /// tracker node can fail its start after the node add Windows
        /// announced, and the threads that sweep every 3 s need three looks
        /// inside the window to catch such a late arrival as their timer
        /// did.</summary>
        internal const long SettleMs = 10000;

        private bool _walked;
        private int _changes;
        private long _walkedAt;
        private long _settleUntil = long.MinValue;

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
            if (_walked && changes != _changes) _settleUntil = nowMs + SettleMs;
            if (_walked && changes == _changes && nowMs >= _settleUntil && nowMs - _walkedAt < RescanMs) return false;
            _walked = true;
            _changes = changes;
            _walkedAt = nowMs;
            return true;
        }
    }
}
