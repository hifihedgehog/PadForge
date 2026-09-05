using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>
    /// When to ask SDL to probe a Flydigi pad's vendor interface again
    /// (discussion #395).
    ///
    /// <para>SDL's Flydigi driver probes the vendor interface once, at
    /// arrival, with a Get Info and a window of about a hundred
    /// milliseconds. The user's logs show that probe failing on two of three
    /// arrivals ("driver = NONE") while Flydigi's own service, which waits a
    /// full second before its first command, got its answer in 24 ms. When
    /// the probe fails, the pad still arrives through XInput, but a slot
    /// bound to the enhanced view stays offline with no input until the pad
    /// is unplugged and replugged. A second probe after the pad's first
    /// second is the recovery attempt. SDL re-runs its probe on every
    /// unclaimed device whenever any HIDAPI hint's string value changes,
    /// and alternating "1" and "true" changes the string and not the
    /// meaning, so a pad SDL already claims is left alone.</para>
    ///
    /// <para>Each ordinary connection has its own deadline and its own
    /// budget, keyed by SDL joystick id, which is new on every arrival. A
    /// reconnect between two enumeration passes starts fresh, and another
    /// pad arriving does not renew a budget this pad has spent. An enhanced
    /// view is matched to a connection by arrival order, not by count: SDL
    /// enumerates a pad's views together, and a view recovered by a probe
    /// arrives after that connection's own nudge, so an enhanced arrival
    /// satisfies the newest connection not yet satisfied. Counting views
    /// could not say whose view it was, and a pad whose XInput view never
    /// opened made the counts lie about another pad. A satisfied connection
    /// is done: this is startup recovery, not a standing retry.</para>
    ///
    /// <para>Pure and clock-free so the tests pin it. What the attempt
    /// counter measures is requested hint writes, and a write can be refused
    /// when an environment variable outranks the hint, so the budget is four
    /// rather than three.</para>
    /// </summary>
    internal sealed class FlydigiReprobePolicy
    {
        /// <summary>Space Station waits 1000 ms before its first command and is
        /// answered. The probe here waits a little longer.</summary>
        public const int DelayMs = 1200;
        public const int MaxAttempts = 4;

        private sealed class Connection
        {
            public long Arrived;
            public long Due;
            public int Attempts;
            public bool Satisfied;
        }

        private readonly Dictionary<uint, Connection> _ordinary = new Dictionary<uint, Connection>();

        /// <summary>True while any connection is unsatisfied with attempts left.</summary>
        public bool Armed
        {
            get
            {
                foreach (var c in _ordinary.Values)
                    if (!c.Satisfied && c.Attempts < MaxAttempts) return true;
                return false;
            }
        }

        /// <summary>Ordinary Flydigi connections PadForge has open.</summary>
        public int OrdinaryViews => _ordinary.Count;

        /// <summary>The highest attempt count among the connections the last
        /// nudge fired for, for the diagnostics line.</summary>
        public int LastAttempt { get; private set; }

        /// <summary>A Flydigi view opened. An ordinary backend under a joystick
        /// id not seen before is a new connection with its own deadline
        /// <see cref="DelayMs"/> from now and its own budget. The same id again
        /// changes nothing. A hidapi view is the enhanced view of some pad:
        /// it satisfies the newest connection that arrived no later than now
        /// and is not yet satisfied. With none to satisfy it is a pad whose
        /// ordinary view PadForge did not open, and it changes nothing.</summary>
        public void OnArrival(uint sdlInstanceId, bool onHidapi, long nowTicks)
        {
            if (onHidapi)
            {
                Connection newest = null;
                foreach (var c in _ordinary.Values)
                    if (!c.Satisfied && c.Arrived <= nowTicks && (newest == null || c.Arrived > newest.Arrived))
                        newest = c;
                if (newest != null) newest.Satisfied = true;
                return;
            }
            if (_ordinary.ContainsKey(sdlInstanceId)) return;
            _ordinary[sdlInstanceId] = new Connection { Arrived = nowTicks, Due = nowTicks + DelayMs, Attempts = 0 };
        }

        /// <summary>A view closed. Its connection's budget goes with it.</summary>
        public void OnDeparture(uint sdlInstanceId) => _ordinary.Remove(sdlInstanceId);

        /// <summary>Called every polling tick. True when a probe is due now:
        /// at least one unsatisfied connection is past its deadline with
        /// attempts left. Every such connection spends one attempt and is
        /// rescheduled.</summary>
        public bool ShouldNudge(long nowTicks)
        {
            bool any = false;
            int max = 0;
            foreach (var c in _ordinary.Values)
            {
                if (c.Satisfied || c.Attempts >= MaxAttempts || nowTicks < c.Due) continue;
                c.Attempts++;
                c.Due = nowTicks + DelayMs;
                any = true;
                if (c.Attempts > max) max = c.Attempts;
            }
            if (any) LastAttempt = max;
            return any;
        }

        /// <summary>The hint string that changes SDL's stored value without
        /// changing its boolean: SDL fires the hint callback only when the
        /// string differs, and the driver's IsEnabled reads the boolean.</summary>
        public static string NextHintValue(string current)
            => string.Equals(current, "1", StringComparison.Ordinal) ? "true" : "1";
    }
}
