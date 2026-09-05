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
    /// <para>Connections are told apart by SDL instance id, which is new on
    /// every arrival, so a reconnect between two enumeration passes starts
    /// a fresh deadline and a fresh budget instead of inheriting the old
    /// ones. The census compares views, not pads: every physical Flydigi
    /// pad has one XInput view, and a pad with its enhanced view has one
    /// hidapi view, so fewer hidapi views than XInput views means a pad is
    /// missing its enhanced view, whichever pad it is.</para>
    ///
    /// <para>Pure and clock-free so the tests pin it. The caller feeds the
    /// tick and the census. What the attempt counter measures is requested
    /// hint writes: a write can be rejected by SDL, or its notification lost
    /// to a concurrent re-evaluation, so the budget is four and not three.</para>
    /// </summary>
    internal sealed class FlydigiReprobePolicy
    {
        /// <summary>Space Station waits 1000 ms before its first command and is
        /// answered. The probe here waits a little longer.</summary>
        public const int DelayMs = 1200;
        public const int MaxAttempts = 4;

        private readonly HashSet<uint> _ordinaryIds = new HashSet<uint>();
        private bool _armed;
        private int _attempts;
        private long _dueTicks;

        /// <summary>Attempts made since the policy was last armed.</summary>
        public int Attempts => _attempts;
        public bool Armed => _armed;
        /// <summary>Flydigi views off the hidapi backend that PadForge has open.</summary>
        public int OrdinaryViews => _ordinaryIds.Count;

        /// <summary>A Flydigi view opened. A hidapi view arms nothing: the
        /// tick's census decides whether every pad has one. Any other backend
        /// under an SDL instance id not seen before is a new connection and
        /// arms a probe <see cref="DelayMs"/> from now with a fresh budget.
        /// The same id again (another view of the same connection) changes
        /// nothing.</summary>
        public void OnArrival(uint sdlInstanceId, bool onHidapi, long nowTicks)
        {
            if (onHidapi) return;
            if (!_ordinaryIds.Add(sdlInstanceId)) return;
            _armed = true;
            _attempts = 0;
            _dueTicks = nowTicks + DelayMs;
        }

        /// <summary>A view closed. When the last ordinary Flydigi view goes,
        /// there is nothing left to probe for.</summary>
        public void OnDeparture(uint sdlInstanceId)
        {
            if (_ordinaryIds.Remove(sdlInstanceId) && _ordinaryIds.Count == 0)
                Disarm();
        }

        /// <summary>Called every polling tick with the census. True when a
        /// probe is due now: at least one Flydigi pad is present on XInput,
        /// fewer pads have their enhanced view than are present, the delay
        /// has passed, and attempts remain. The next attempt is scheduled
        /// before returning. Every pad having its view disarms.</summary>
        public bool ShouldNudge(long nowTicks, int xinputViews, int enhancedViews)
        {
            if (xinputViews == 0 || enhancedViews >= xinputViews) { Disarm(); return false; }
            if (!_armed || nowTicks < _dueTicks) return false;
            if (_attempts >= MaxAttempts) { Disarm(); return false; }
            _attempts++;
            _dueTicks = nowTicks + DelayMs;
            return true;
        }

        public void Disarm()
        {
            _armed = false;
            _attempts = 0;
        }

        /// <summary>The hint string that changes SDL's stored value without
        /// changing its boolean: SDL fires the hint callback only when the
        /// string differs, and the driver's IsEnabled reads the boolean.</summary>
        public static string NextHintValue(string current)
            => string.Equals(current, "1", StringComparison.Ordinal) ? "true" : "1";
    }
}
