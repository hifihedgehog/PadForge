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
    /// reconnect between two enumeration passes starts fresh, another pad
    /// arriving does not renew a budget this pad has spent, and a
    /// connection whose census once said every pad had its view is done:
    /// this is startup recovery, not a standing retry. The census compares
    /// views, not pads: every Flydigi V2 pad PadForge has an XInput view of
    /// counts once, and every V2 pad with its enhanced view has one hidapi
    /// view, so fewer hidapi views than XInput views means a pad is missing
    /// its enhanced view. The counts correspond only for pads PadForge has
    /// an XInput view of, which is the stated coverage.</para>
    ///
    /// <para>Pure and clock-free so the tests pin it. What the attempt
    /// counter measures is requested hint writes: a write can be rejected by
    /// SDL, or its notification lost to a concurrent re-evaluation, so the
    /// budget is four and not three.</para>
    /// </summary>
    internal sealed class FlydigiReprobePolicy
    {
        /// <summary>Space Station waits 1000 ms before its first command and is
        /// answered. The probe here waits a little longer.</summary>
        public const int DelayMs = 1200;
        public const int MaxAttempts = 4;

        private sealed class Connection
        {
            public long Due;
            public int Attempts;
        }

        private readonly Dictionary<uint, Connection> _ordinary = new Dictionary<uint, Connection>();

        /// <summary>True while any ordinary connection still has attempts left.</summary>
        public bool Armed
        {
            get
            {
                foreach (var c in _ordinary.Values)
                    if (c.Attempts < MaxAttempts) return true;
                return false;
            }
        }

        /// <summary>Ordinary Flydigi connections PadForge has open.</summary>
        public int OrdinaryViews => _ordinary.Count;

        /// <summary>The highest attempt count among the connections the last
        /// nudge fired for, for the diagnostics line.</summary>
        public int LastAttempt { get; private set; }

        /// <summary>A Flydigi view opened. A hidapi view arms nothing: the
        /// tick's census decides whether every pad has one. Any other backend
        /// under a joystick id not seen before is a new connection with its
        /// own deadline <see cref="DelayMs"/> from now and its own budget.
        /// The same id again changes nothing.</summary>
        public void OnArrival(uint sdlInstanceId, bool onHidapi, long nowTicks)
        {
            if (onHidapi) return;
            if (_ordinary.ContainsKey(sdlInstanceId)) return;
            _ordinary[sdlInstanceId] = new Connection { Due = nowTicks + DelayMs, Attempts = 0 };
        }

        /// <summary>A view closed. Its connection's budget goes with it.</summary>
        public void OnDeparture(uint sdlInstanceId) => _ordinary.Remove(sdlInstanceId);

        /// <summary>Called every polling tick with the census. True when a
        /// probe is due now: at least one V2 pad is present on XInput, fewer
        /// pads have their enhanced view than are present, and at least one
        /// connection is past its deadline with attempts left. Every due
        /// connection spends one attempt and is rescheduled. Every pad having
        /// its view ends recovery for every present connection.</summary>
        public bool ShouldNudge(long nowTicks, int xinputViews, int enhancedViews)
        {
            if (xinputViews == 0 || enhancedViews >= xinputViews) { Disarm(); return false; }
            bool any = false;
            int max = 0;
            foreach (var c in _ordinary.Values)
            {
                if (c.Attempts >= MaxAttempts || nowTicks < c.Due) continue;
                c.Attempts++;
                c.Due = nowTicks + DelayMs;
                any = true;
                if (c.Attempts > max) max = c.Attempts;
            }
            if (any) LastAttempt = max;
            return any;
        }

        /// <summary>Ends recovery for every present connection. Their ids stay
        /// known so a repeated arrival of the same id arms nothing.</summary>
        public void Disarm()
        {
            foreach (var c in _ordinary.Values) c.Attempts = MaxAttempts;
        }

        /// <summary>The hint string that changes SDL's stored value without
        /// changing its boolean: SDL fires the hint callback only when the
        /// string differs, and the driver's IsEnabled reads the boolean.</summary>
        public static string NextHintValue(string current)
            => string.Equals(current, "1", StringComparison.Ordinal) ? "true" : "1";
    }
}
