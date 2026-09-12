using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>Bench diagnostics for the two writers that share a physical
    /// DualSense: the effect pass-through lane and the 30 Hz Sony pass. Off
    /// unless the PADFORGE_DS5_WRITE_TRACE environment variable is 1 at
    /// launch, so a normal session pays one static read and nothing else.
    /// Every physical write logs its valid flags, motors and trigger modes
    /// with the write's result, and each DualSense's accelerometer is
    /// summarized ten times a second so a rumble tail after a release is
    /// measurable without a hand on the pad (#434). Lines are queued and
    /// written by one background thread: the callers are the polling
    /// thread, the dispatch path and the virtual pad's output callback,
    /// and the diagnostics file append holds a lock around synchronous
    /// disk I/O. Each line carries its own event time as "at=" because
    /// the ring stamps the drain time.</summary>
    internal static class Ds5WriteTrace
    {
        public static readonly bool Enabled = ReadSwitch();

        private const int MaxQueued = 20_000;
        private static readonly ConcurrentQueue<string> s_queue = new();
        private static int s_queued;
        private static int s_dropped;
        private static Thread s_drain;

        private static bool ReadSwitch()
        {
            try { return Environment.GetEnvironmentVariable("PADFORGE_DS5_WRITE_TRACE") == "1"; }
            catch { return false; }
        }

        private sealed class Limiter
        {
            public long Second;
            public int Count;
            public int Dropped;
        }

        private static readonly Dictionary<string, Limiter> s_limits = new();

        /// <summary>Queues one diagnostics line regardless of the bench
        /// switch, for callers on timing-sensitive threads (the virtual
        /// pad's output callback), at most <paramref name="perSecond"/>
        /// lines a second per category. Overflow is counted and reported
        /// as "{category} dropped=N" at the next second boundary, by the
        /// next call or by the drain thread, so a dropped final line is
        /// never silent. The diagnostics ring stamps the line when it is
        /// drained, so the event time rides on the line itself as
        /// "at=".</summary>
        internal static void EnqueueDiag(string category, string line, int perSecond)
        {
            try
            {
                lock (s_limits)
                {
                    // Clock read under the lock, so two callers cannot
                    // disagree about which second a line belongs to and
                    // reopen a spent quota.
                    long second = Environment.TickCount64 / 1000;
                    if (!s_limits.TryGetValue(category, out var lim))
                    {
                        lim = new Limiter { Second = second };
                        s_limits[category] = lim;
                    }
                    FlushLimiterLocked(category, lim, second);
                    if (++lim.Count > perSecond)
                    {
                        lim.Dropped++;
                        return;
                    }
                }
                Enqueue(line);
            }
            catch { }
        }

        /// <summary>Caller holds s_limits. When the clock has moved past
        /// the limiter's second, reports that second's overflow and opens
        /// the new one. Epochs only advance.</summary>
        private static void FlushLimiterLocked(string category, Limiter lim, long second)
        {
            if (second <= lim.Second) return;
            if (lim.Dropped > 0) Enqueue(category + " dropped=" + lim.Dropped);
            lim.Second = second;
            lim.Count = 0;
            lim.Dropped = 0;
        }

        private static void Enqueue(string line)
        {
            if (Interlocked.Increment(ref s_queued) > MaxQueued)
            {
                Interlocked.Decrement(ref s_queued);
                Interlocked.Increment(ref s_dropped);
                return;
            }
            s_queue.Enqueue(line + " at=" + DateTime.Now.ToString("HH:mm:ss.fff"));
            if (Volatile.Read(ref s_drain) == null) StartDrain();
        }

        private static void StartDrain()
        {
            var t = new Thread(Drain)
            {
                IsBackground = true,
                Name = "PadForge DS5 write trace",
                Priority = ThreadPriority.BelowNormal,
            };
            if (Interlocked.CompareExchange(ref s_drain, t, null) == null) t.Start();
        }

        private const int DrainBatch = 500;

        private static void Drain()
        {
            while (true)
            {
                try
                {
                    // Bounded batch: a stream that never empties must not
                    // starve the limiter flush below.
                    int n = 0;
                    while (n < DrainBatch && s_queue.TryDequeue(out var line))
                    {
                        Interlocked.Decrement(ref s_queued);
                        Engine.SdlDiagLog.WriteLine(line);
                        n++;
                    }
                    int dropped = Interlocked.Exchange(ref s_dropped, 0);
                    if (dropped > 0) Engine.SdlDiagLog.WriteLine("DS5W dropped=" + dropped);
                    // A category whose stream went quiet still reports the
                    // overflow of its last busy second.
                    lock (s_limits)
                    {
                        long second = Environment.TickCount64 / 1000;
                        foreach (var kv in s_limits)
                            FlushLimiterLocked(kv.Key, kv.Value, second);
                    }
                    if (n < DrainBatch) Thread.Sleep(20);
                }
                catch
                {
                    Thread.Sleep(20);
                }
            }
        }

        /// <summary>Logs one report-ID-stripped DualSense effect payload
        /// (USB 47-byte layout: valid flags at 0 and 1, motors at 2 and 3,
        /// trigger modes at 10 and 21, valid_flag2 at 38).</summary>
        public static void Log(string source, byte[] payload, int offset, int length)
        {
            if (!Enabled || payload == null) return;
            try
            {
                byte At(int i) => i < length && offset + i < payload.Length ? payload[offset + i] : (byte)0;
                Enqueue(
                    $"DS5W src={source} vf0={At(0):X2} vf1={At(1):X2} vf2={At(38):X2} mR={At(2)} mL={At(3)} trR={At(10):X2} trL={At(21):X2} len={length}");
            }
            catch { }
        }

        private sealed class Sense
        {
            public readonly float[] Ring = new float[64];
            public int Count, Head;
            public long LastLog;
        }

        private static readonly Dictionary<Guid, Sense> s_sense = new();

        /// <summary>Accumulates one accelerometer sample and queues the
        /// window's mean and standard deviation of the magnitude every
        /// 100 ms. Rumble shows as a standard deviation well above the
        /// resting sensor noise.</summary>
        public static void Sample(Guid device, string name, float[] accel)
        {
            if (!Enabled || accel == null || accel.Length < 3) return;
            try
            {
                float mag = MathF.Sqrt(accel[0] * accel[0] + accel[1] * accel[1] + accel[2] * accel[2]);
                Sense s;
                lock (s_sense)
                {
                    if (!s_sense.TryGetValue(device, out s)) { s = new Sense(); s_sense[device] = s; }
                }
                string line = null;
                lock (s)
                {
                    s.Ring[s.Head] = mag;
                    s.Head = (s.Head + 1) % s.Ring.Length;
                    if (s.Count < s.Ring.Length) s.Count++;
                    long now = Environment.TickCount64;
                    if (now - s.LastLog >= 100)
                    {
                        s.LastLog = now;
                        double sum = 0, sq = 0;
                        for (int i = 0; i < s.Count; i++) { sum += s.Ring[i]; sq += s.Ring[i] * s.Ring[i]; }
                        double mean = sum / s.Count;
                        double var = Math.Max(0, sq / s.Count - mean * mean);
                        line = $"RUMBLESENSE name={name} n={s.Count} mean={mean:F3} std={Math.Sqrt(var):F4}";
                    }
                }
                if (line != null) Enqueue(line);
            }
            catch { }
        }
    }
}
