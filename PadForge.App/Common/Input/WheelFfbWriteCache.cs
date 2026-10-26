using System;
using System.Collections.Concurrent;

namespace PadForge.Common.Input
{
    internal enum WheelWriteDecision { Write, Unchanged, RetryBackoff }

    /// <summary>Only successful deliveries are considered applied. A failed
    /// delivery retries even when the requested signature stays unchanged.</summary>
    internal sealed class WheelFfbWriteCache<T> where T : IEquatable<T>
    {
        internal const int RetryDelayMs = 250;
        private readonly record struct Entry(T Signature, bool Applied, long RetryAt, bool WasStop);
        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        internal WheelWriteDecision Decide(string path, T signature, long now, bool urgentStop = false)
        {
            if (!entries.TryGetValue(path, out var e)) return WheelWriteDecision.Write;
            if (e.Applied && e.Signature.Equals(signature)) return WheelWriteDecision.Unchanged;
            // A newly requested stop bypasses backoff once. Repeated failed
            // stops still wait, so an unavailable wheel cannot stall every poll.
            if (!e.Applied && now < e.RetryAt && !(urgentStop && !e.WasStop))
                return WheelWriteDecision.RetryBackoff;
            return WheelWriteDecision.Write;
        }

        internal void Record(string path, T signature, bool success, long now, bool isStop = false)
            => entries[path] = new Entry(signature, success, success ? 0 : now + RetryDelayMs, isStop);

        internal bool ContainsKey(string path) => entries.ContainsKey(path);
        internal bool TryGetApplied(string path, out T signature)
        {
            bool found = entries.TryGetValue(path, out var e) && e.Applied;
            signature = e.Signature;
            return found;
        }
        internal bool CanRetry(string path, long now) => !entries.TryGetValue(path, out var e) || e.Applied || now >= e.RetryAt;
        internal void Defer(string path, long now)
        {
            if (entries.TryGetValue(path, out var e))
                entries[path] = e with { Applied = false, RetryAt = now + RetryDelayMs, WasStop = true };
        }
        internal bool TryRemove(string path, out T signature)
        {
            bool removed = entries.TryRemove(path, out var e);
            signature = e.Signature;
            return removed;
        }
    }
}
