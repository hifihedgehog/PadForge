using System;

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
    /// the probe fails, the pad still arrives through XInput, but the slot is
    /// bound to the enhanced view, so the pad shows as offline in PadForge
    /// with no input at all until it is unplugged and replugged. A second
    /// probe after the pad's first second is the fix, and SDL re-runs its
    /// probe on every unclaimed device whenever any HIDAPI hint's string
    /// value changes. Alternating "1" and "true" changes the string and not
    /// the meaning, so a pad SDL already claims is left alone.</para>
    ///
    /// <para>Pure and clock-free so the tests pin it. The caller feeds the
    /// tick and the census: whether any Flydigi pad is present and whether
    /// any of them is on the hidapi backend.</para>
    /// </summary>
    internal sealed class FlydigiReprobePolicy
    {
        /// <summary>Space Station waits 1000 ms before its first command and is
        /// answered. The probe here waits a little longer.</summary>
        public const int DelayMs = 1200;
        /// <summary>A pad that refuses three times is not a timing problem.</summary>
        public const int MaxAttempts = 3;

        private bool _armed;
        private int _attempts;
        private long _dueTicks;

        /// <summary>Attempts made since the policy was last armed.</summary>
        public int Attempts => _attempts;
        public bool Armed => _armed;

        /// <summary>A Flydigi pad arrived. An arrival on the hidapi backend
        /// is the enhanced view itself and disarms. Any other backend arms a
        /// probe <see cref="DelayMs"/> from now, unless one is already armed.</summary>
        public void OnArrival(bool onHidapi, long nowTicks)
        {
            if (onHidapi) { Disarm(); return; }
            if (_armed) return;
            _armed = true;
            _attempts = 0;
            _dueTicks = nowTicks + DelayMs;
        }

        /// <summary>Called every tick with the census. True when a probe is
        /// due now: a Flydigi pad is present, none of its views is the
        /// enhanced one, the delay has passed, and attempts remain. The next
        /// attempt is scheduled before returning.</summary>
        public bool ShouldNudge(long nowTicks, bool anyFlydigiPresent, bool enhancedPresent)
        {
            if (!anyFlydigiPresent || enhancedPresent) { Disarm(); return false; }
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
