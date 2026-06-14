using System;
using System.Collections.Generic;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Consumer-side forwarder for the Remote Link reverse feedback channel
    /// (issue #138 M2). When a game on THIS PC produces output (rumble, adaptive
    /// triggers, lightbar, mic/player LED) for a virtual controller whose input
    /// source is a remote peer's device, that output has nowhere to go locally —
    /// the device is not physically here. This router captures the output at the
    /// virtual-controller boundary and ships it to the device's owner, who drives
    /// the real hardware.
    ///
    /// <para>InputService owns the mapping knowledge and pushes a route table
    /// (VC pad slot -&gt; the remote targets mapped to it) via <see cref="SetRoutes"/>
    /// whenever a peer device connects/disconnects or the user remaps. The capture
    /// taps in <c>HMaestroVirtualController</c>'s output handlers call
    /// <see cref="OnLocalSonyEffect"/> / <see cref="OnLocalRumble"/> on the
    /// HIDMaestro decode thread, so the work here is a dictionary lookup plus a
    /// non-blocking UDP send (no allocation when nothing is shared).</para>
    /// </summary>
    internal static class RemoteLinkOutputRouter
    {
        /// <summary>One forwarding target: the owning peer's fingerprint and the
        /// device's link slot (its index in the owner's exposed list).</summary>
        public readonly struct Target
        {
            public readonly string Fingerprint;
            public readonly byte LinkSlot;
            public Target(string fingerprint, byte linkSlot) { Fingerprint = fingerprint; LinkSlot = linkSlot; }
        }

        // VC pad slot -> targets. Replaced wholesale by SetRoutes; readers take a
        // local reference so a concurrent swap is safe without locking.
        private static volatile Dictionary<int, List<Target>> _routes = new();
        private static volatile bool _hasRoutes;

        // Per-slot last forwarded value, so a game that re-sends an identical packet
        // every frame doesn't flood the link. Alternating square-wave rumble still
        // forwards (each distinct value differs from the last); only exact repeats drop.
        private static readonly Dictionary<int, (ushort l, ushort r, ushort lt, ushort rt)> _lastRumble = new();
        private static readonly Dictionary<int, byte[]> _lastEffect = new();
        private static readonly object _dedupLock = new();

        /// <summary>Wired by InputService to <c>LinkServer.PushOutputEffect</c>.</summary>
        public static Action<string, byte, byte[]> Send { get; set; }

        // Diagnostics (#138 M2 reverse-channel bring-up).
        public static long SonyCaptured, RumbleCaptured, Sent;
        public static int RouteCount => _routes.Count;

        /// <summary>Replace the route table. Pass an empty/null map to stop forwarding.</summary>
        public static void SetRoutes(Dictionary<int, List<Target>> routes)
        {
            _routes = routes ?? new Dictionary<int, List<Target>>();
            _hasRoutes = _routes.Count > 0;
            if (!_hasRoutes)
                lock (_dedupLock) { _lastRumble.Clear(); _lastEffect.Clear(); }
            var keys = string.Join(",", _routes.Keys);
            RemoteLinkDiag.Log($"router SetRoutes: {_routes.Count} slot(s) [{keys}]");
        }

        public static void Clear() => SetRoutes(null);

        /// <summary>Forward a DualSense effect report body (rumble + AT + lightbar +
        /// mic/player LED) captured for the given VC pad slot.</summary>
        public static void OnLocalSonyEffect(int padSlot, ReadOnlySpan<byte> effectPayload)
        {
            long n = System.Threading.Interlocked.Increment(ref SonyCaptured);
            if (effectPayload.Length == 0) return;
            var routes = _routes;
            if (!routes.TryGetValue(padSlot, out var targets) || targets.Count == 0)
            {
                if (n == 1 || n % 240 == 0)
                    RemoteLinkDiag.Log($"capture sony slot={padSlot} but NO route (routes={routes.Count}, hasRoutes={_hasRoutes}) n={n}");
                return;
            }
            var send = Send;
            if (send == null) { RemoteLinkDiag.Log("capture sony: Send is null"); return; }

            // Drop exact repeats (static lightbar / held AT re-sent every frame).
            lock (_dedupLock)
            {
                if (_lastEffect.TryGetValue(padSlot, out var prev) && prev.AsSpan().SequenceEqual(effectPayload))
                    return;
                _lastEffect[padSlot] = effectPayload.ToArray();
            }

            byte[] blob = Engine.RemoteLink.OutputEffectCodec.EncodeSonyEffect(effectPayload);
            foreach (var t in targets) { send(t.Fingerprint, t.LinkSlot, blob); System.Threading.Interlocked.Increment(ref Sent); }
            if (n == 1 || n % 240 == 0)
                RemoteLinkDiag.Log($"capture sony slot={padSlot} -> sent to {targets.Count} target(s) len={effectPayload.Length} n={n}");
        }

        /// <summary>Forward the four XInput motor magnitudes captured for the given
        /// VC pad slot (non-Sony pads).</summary>
        public static void OnLocalRumble(int padSlot, ushort left, ushort right, ushort leftTrigger, ushort rightTrigger)
        {
            long n = System.Threading.Interlocked.Increment(ref RumbleCaptured);
            var routes = _routes;
            if (!routes.TryGetValue(padSlot, out var targets) || targets.Count == 0)
            {
                if (n == 1 || n % 240 == 0)
                    RemoteLinkDiag.Log($"capture rumble slot={padSlot} but NO route (routes={routes.Count}) n={n}");
                return;
            }
            var send = Send;
            if (send == null) { RemoteLinkDiag.Log("capture rumble: Send is null"); return; }

            lock (_dedupLock)
            {
                var cur = (left, right, leftTrigger, rightTrigger);
                if (_lastRumble.TryGetValue(padSlot, out var prev) && prev.Equals(cur))
                    return;
                _lastRumble[padSlot] = cur;
            }

            byte[] blob = Engine.RemoteLink.OutputEffectCodec.EncodeRumble(left, right, leftTrigger, rightTrigger);
            foreach (var t in targets) { send(t.Fingerprint, t.LinkSlot, blob); System.Threading.Interlocked.Increment(ref Sent); }
            if (n == 1 || n % 240 == 0)
                RemoteLinkDiag.Log($"capture rumble slot={padSlot} -> sent ({left},{right},{leftTrigger},{rightTrigger}) to {targets.Count} n={n}");
        }
    }
}
