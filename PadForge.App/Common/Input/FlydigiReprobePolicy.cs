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
    /// <para>The identity is the vendor interface's own HID path. SDL's raw
    /// HID enumeration says which vendor interfaces are present, SDL names
    /// the enhanced joystick it creates for one by that same path, so an
    /// interface is claimed exactly when an attached enhanced wrapper carries
    /// its path and unclaimed otherwise. Two identical pads have two paths,
    /// and a pad PadForge has no joystick view of is still seen.</para>
    ///
    /// <para>Generations come from Flydigi's own signals, never from SDL's
    /// system-wide HID change counter, which the caller uses only to decide
    /// WHEN to enumerate. A claim goes stale when no attached enhanced
    /// wrapper carries its path any more while the path is still present:
    /// that connection ended, and the interface starts over. A joystick id
    /// never seen before among the ORDINARY Flydigi wrappers is a connection
    /// event, and every unclaimed interface gets a fresh budget with a
    /// deadline at least one delay away. Enhanced wrapper ids do not count:
    /// SDL recreates the enhanced joystick on an availability change with no
    /// physical arrival. Known ids are never pruned, since PadForge can
    /// close and reopen a wrapper on a still-connected joystick under the
    /// same id, and that is not an arrival either.</para>
    ///
    /// <para>An interface absent from an enumeration keeps its record and its
    /// spent budget through one real absence (the change counter moved), and
    /// is forgotten on the second. Without a counter move an absence is a
    /// flake and changes nothing. While any Flydigi wrapper is detached and
    /// awaiting cleanup the pad is in flux, and every deadline is pushed
    /// out.</para>
    ///
    /// <para>What a nudge does is SDL's business: it re-probes EVERY unclaimed
    /// HIDAPI device on the system, so one pad's nudge can probe a second pad
    /// before that pad's own deadline, and an exhausted pad is probed again
    /// by another's nudge, and a second pad's physical reconnect renews the
    /// first pad's budget. The budget bounds requests, not probes. Accepted.
    /// Pure and clock-free so the tests pin it.</para>
    /// </summary>
    internal sealed class FlydigiReprobePolicy
    {
        /// <summary>Space Station waits 1000 ms before its first command and is
        /// answered. The probe here waits a little longer. The caller also
        /// observes no more often than this while retrying.</summary>
        public const int DelayMs = 1200;
        /// <summary>Requested hint writes per interface per connection. A write
        /// can be refused when an environment variable outranks the hint.</summary>
        public const int MaxAttempts = 4;

        private sealed class Interface
        {
            public long Due;
            public int Attempts;
            public bool Claimed;
            public int RealAbsences;
        }

        private readonly Dictionary<string, Interface> _interfaces =
            new Dictionary<string, Interface>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<uint> _knownOrdinaryIds = new HashSet<uint>();

        /// <summary>Interfaces currently tracked, claimed or not.</summary>
        public int Tracked => _interfaces.Count;

        /// <summary>True while any unclaimed interface still has attempts left,
        /// which is when the caller keeps observing on the delay cadence.</summary>
        public bool Armed
        {
            get
            {
                foreach (var i in _interfaces.Values)
                    if (!i.Claimed && i.Attempts < MaxAttempts) return true;
                return false;
            }
        }

        /// <summary>The highest attempt count among the interfaces the last
        /// observation fired for, for the diagnostics line.</summary>
        public int LastAttempt { get; private set; }

        /// <summary>One observation. <paramref name="presentPaths"/> is a
        /// successful enumeration (a failed one is not an observation).
        /// <paramref name="claimedPaths"/> are the attached enhanced wrappers'
        /// paths. <paramref name="attachedOrdinaryIds"/> are the joystick ids
        /// of every attached Flydigi wrapper that is not enhanced.
        /// <paramref name="inFlux"/> says a Flydigi wrapper is detached and
        /// awaiting cleanup. <paramref name="absencesAreReal"/> says SDL's
        /// change counter moved since the last observation. Returns the
        /// unclaimed paths whose probe is due now, each of which spends one
        /// attempt. Empty means no nudge.</summary>
        public IReadOnlyList<string> Observe(long nowTicks, IReadOnlyList<string> presentPaths,
            IReadOnlyList<string> claimedPaths, IReadOnlyCollection<uint> attachedOrdinaryIds,
            bool inFlux, bool absencesAreReal)
        {
            var present = new HashSet<string>(presentPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var claimed = new HashSet<string>(claimedPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (absencesAreReal)
            {
                var gone = new List<string>();
                foreach (var kv in _interfaces)
                {
                    if (present.Contains(kv.Key)) continue;
                    if (++kv.Value.RealAbsences >= 2) gone.Add(kv.Key);
                }
                foreach (var path in gone) _interfaces.Remove(path);
            }

            // A new ordinary joystick id is a connection event: some pad arrived
            // or came back. Unclaimed interfaces start their budget over, with a
            // deadline at least one delay away.
            bool connectionEvent = false;
            if (attachedOrdinaryIds != null)
                foreach (var id in attachedOrdinaryIds)
                    if (_knownOrdinaryIds.Add(id)) connectionEvent = true;

            var due = new List<string>();
            int max = 0;
            foreach (var path in present)
            {
                if (!_interfaces.TryGetValue(path, out var i))
                {
                    i = new Interface { Due = nowTicks + DelayMs };
                    _interfaces[path] = i;
                }
                i.RealAbsences = 0;
                if (claimed.Contains(path)) { i.Claimed = true; continue; }
                if (i.Claimed)
                {
                    // The enhanced wrapper that claimed this path is gone while the
                    // path is still here: that connection ended, this is the next.
                    i.Claimed = false;
                    i.Attempts = 0;
                    i.Due = nowTicks + DelayMs;
                    continue;
                }
                if (connectionEvent)
                {
                    i.Attempts = 0;
                    i.Due = Math.Max(i.Due, nowTicks + DelayMs);
                    continue;
                }
                if (inFlux) { i.Due = Math.Max(i.Due, nowTicks + DelayMs); continue; }
                if (i.Attempts >= MaxAttempts || nowTicks < i.Due) continue;
                i.Attempts++;
                i.Due = nowTicks + DelayMs;
                due.Add(path);
                if (i.Attempts > max) max = i.Attempts;
            }
            if (due.Count > 0) LastAttempt = max;
            return due;
        }

        /// <summary>The hint string that changes SDL's stored value without
        /// changing its boolean: SDL fires the hint callback only when the
        /// string differs, and the driver's IsEnabled reads the boolean.</summary>
        public static string NextHintValue(string current)
            => string.Equals(current, "1", StringComparison.Ordinal) ? "true" : "1";
    }
}
