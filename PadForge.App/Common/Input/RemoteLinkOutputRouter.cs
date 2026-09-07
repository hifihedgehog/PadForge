using System;
using System.Collections.Concurrent;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Common.Input
{
    /// <summary>Routes each shared device's output through the connection that advertised it.</summary>
    internal static class RemoteLinkOutputRouter
    {
        public sealed class Target
        {
            public readonly string Fingerprint;
            public readonly byte LinkSlot;
            public readonly LinkConnectionLifetime Connection;
            public readonly RemotePeerDevice Owner;
            public readonly string DeviceId;
            internal readonly object Gate = new();
            internal byte[] Sony;
            internal (ushort, ushort, ushort, ushort, int, bool)? Vibration;
            internal byte[] Wheel;
            internal (float Hz, float Amp)? Tone;
            internal int? Player;
            internal int? Guide;
            internal long NfcDemandMs;
            internal Target(string fingerprint, byte slot, LinkConnectionLifetime connection, RemotePeerDevice owner)
            {
                Fingerprint = fingerprint;
                LinkSlot = slot;
                Connection = connection;
                Owner = owner;
                DeviceId = owner?.Info.PeerLocalDeviceId;
            }
        }

        private static readonly ConcurrentDictionary<string, Target> _byPath = new(StringComparer.Ordinal);
        public static Action<string, byte, byte[]> SendOutput { get; set; }
        public static Action<string, byte, byte[]> SendAudio { get; set; }
        public static Action<string, byte, byte[]> SendSourceDemand { get; set; }
        public static Func<LinkConnectionLifetime, byte, string, byte[], bool> SendScopedOutput { get; set; }
        public static Func<LinkConnectionLifetime, byte, string, byte[], bool> SendScopedAudio { get; set; }
        public static Func<LinkConnectionLifetime, byte, string, byte[], bool> SendScopedDemand { get; set; }
        public const byte DemandKindNfc = 1;
        public static int DeviceCount => _byPath.Count;
        public static bool IsPeerPath(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("peer://", StringComparison.Ordinal);

        public static void ShipNfcDemand(string path)
        {
            if (!_byPath.TryGetValue(path, out var target)) return;
            lock (target.Gate)
            {
                long now = Environment.TickCount64;
                if (now - target.NfcDemandMs < 1000) return;
                if (Dispatch(target, LinkMessageType.SourceDemand, new[] { DemandKindNfc })) target.NfcDemandMs = now;
            }
        }

        public static void Register(string path, string fingerprint, byte slot)
            => Register(path, fingerprint, slot, null, null);

        public static void Register(string path, string fingerprint, byte slot,
            LinkConnectionLifetime connection, RemotePeerDevice owner)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(fingerprint)) return;
            _byPath[path] = new Target(fingerprint, slot, connection, owner);
        }

        public static void Unregister(string path)
        {
            if (!string.IsNullOrEmpty(path)) _byPath.TryRemove(path, out _);
        }

        public static void Unregister(string path, RemotePeerDevice owner)
        {
            if (string.IsNullOrEmpty(path) || !_byPath.TryGetValue(path, out var target)
                || !ReferenceEquals(target.Owner, owner)) return;
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, Target>>)_byPath)
                .Remove(new System.Collections.Generic.KeyValuePair<string, Target>(path, target));
        }

        public static void Clear()
        {
            _byPath.Clear();
            lock (_ownerLock)
            {
                _outputLease.Clear();
                _peerWroteLast.Clear();
            }
        }

        public static bool ShipSonyEffect(string path, ReadOnlySpan<byte> body)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            lock (target.Gate)
            {
                if (body.Length == 0) return true;
                if (target.Sony != null && body.SequenceEqual(target.Sony)) return true;
                if (!Dispatch(target, LinkMessageType.Output, OutputEffectCodec.EncodeSonyEffect(body))) return false;
                target.Sony = body.ToArray();
                return true;
            }
        }

        public static bool ShipVibration(string path, Vibration vibration)
        {
            if (vibration == null || !_byPath.TryGetValue(path, out var target)) return false;
            lock (target.Gate) return ShipVibration(target, vibration);
        }

        private static bool ShipVibration(Target target, Vibration vibration)
        {
            int signature = vibration.HasDirectionalData || vibration.HasConditionData
                ? unchecked((int)(vibration.EffectType * 31 + (uint)vibration.SignedMagnitude * 7
                    + vibration.Direction * 13 + vibration.Period)) : 0;
            bool active = vibration.LeftMotorSpeed != 0 || vibration.RightMotorSpeed != 0
                || vibration.LeftTriggerMotorSpeed != 0 || vibration.RightTriggerMotorSpeed != 0
                || vibration.HasDirectionalData || vibration.HasConditionData;
            var key = (vibration.LeftMotorSpeed, vibration.RightMotorSpeed, vibration.LeftTriggerMotorSpeed,
                vibration.RightTriggerMotorSpeed, signature, active);
            if (!vibration.HasDirectionalData && !vibration.HasConditionData && target.Vibration == key) return true;
            if (!Dispatch(target, LinkMessageType.Output, OutputEffectCodec.EncodeVibration(vibration))) return false;
            target.Vibration = key;
            return true;
        }

        public static bool StopVibration(string path)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            lock (target.Gate)
                return target.Vibration is { Item6: true } && ShipVibration(target, new Vibration());
        }

        public static bool ShipWheel(string path, bool hasCond, bool dir, short force, short peak,
            int ac, uint effect, int period, short pc, short nc, short off, int db, int ps, int ns,
            int condGain, ushort rangeDeg, ushort ledMask, bool ledValid)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            byte[] payload = OutputEffectCodec.EncodeWheel(hasCond, dir, force, peak, ac, effect,
                period, pc, nc, off, db, ps, ns, condGain, rangeDeg, ledMask, ledValid);
            lock (target.Gate)
            {
                if (target.Wheel != null && payload.AsSpan().SequenceEqual(target.Wheel)) return true;
                if (!Dispatch(target, LinkMessageType.Output, payload)) return false;
                target.Wheel = payload;
                return true;
            }
        }

        public static bool ShipHapticTone(string path, float hz, float amplitude)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            lock (target.Gate)
            {
                if (amplitude <= 0 && target.Tone is { Amp: <= 0 }) return true;
                if (!Dispatch(target, LinkMessageType.Output, OutputEffectCodec.EncodeHapticTone(hz, amplitude))) return false;
                target.Tone = (hz, amplitude);
                return true;
            }
        }

        public static bool ShipPlayerIndex(string path, int number)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            lock (target.Gate)
            {
                if (target.Player == number) return true;
                if (!Dispatch(target, LinkMessageType.Output, OutputEffectCodec.EncodePlayerIndex(number))) return false;
                target.Player = number;
                return true;
            }
        }

        public static bool ShipGuideLed(string path, int percent)
        {
            if (!_byPath.TryGetValue(path, out var target)) return false;
            percent = Math.Clamp(percent, 0, 100);
            lock (target.Gate)
            {
                if (target.Guide == percent) return true;
                if (!Dispatch(target, LinkMessageType.Output, OutputEffectCodec.EncodeGuideLed(percent))) return false;
                target.Guide = percent;
                return true;
            }
        }

        public static bool ShipAudio(string path, byte[] pcm)
            => pcm != null && _byPath.TryGetValue(path, out var target)
                && Dispatch(target, LinkMessageType.Audio, pcm);

        private static bool Dispatch(Target target, LinkMessageType type, byte[] payload)
        {
            if (target.Connection != null)
            {
                var send = type switch
                {
                    LinkMessageType.Audio => SendScopedAudio,
                    LinkMessageType.SourceDemand => SendScopedDemand,
                    _ => SendScopedOutput
                };
                return send?.Invoke(target.Connection, target.LinkSlot, target.DeviceId, payload) == true;
            }
            var legacy = type switch
            {
                LinkMessageType.Audio => SendAudio,
                LinkMessageType.SourceDemand => SendSourceDemand,
                _ => SendOutput
            };
            if (legacy == null) return false;
            legacy(target.Fingerprint, target.LinkSlot, payload);
            return true;
        }

        // ── Owner-side output lease (#138 sole-writer guard) ─────────────────────────
        // A device physically on THIS machine can be both shared out to a peer AND mapped
        // to a local slot. Output can't merge (no sane blend of two lightbar colors or two
        // rumble commands), so exactly one source may feed the hardware. The lease
        // arbitrates with zero new protocol: a relayed output frame IS the claim.
        // OnRemoteOutputReceived stamps the LOCAL device path here per frame; while the
        // stamp is fresh the owner's local output chokepoints (PlayStationEffectWriter / Step2)
        // skip their writes, so the inbound relay is the sole writer. A fight needs both
        // sides active at once, but an active remote keeps the stamp fresh — so the remote
        // wins while active and the local pipeline resumes only after the remote falls
        // quiet (~OutputLeaseMs), when it isn't writing anyway. The realistic lend case
        // (device not also mapped locally) has no local writer, so there's no fight at all.
        // Known edge: a remote that sets a sticky state once (held lightbar) then goes
        // silent lets the lease lapse — the deduped ship sends it once — and the local
        // pipeline can repaint it. Closing that needs a map-time explicit lease (new
        // protocol); demand-expiry is the first cut. Keyed case-insensitively since the
        // stamp and the check resolve the device path from different sites.
        private static readonly ConcurrentDictionary<string, long> _outputLease = new(StringComparer.OrdinalIgnoreCase);
        private const long OutputLeaseMs = 3000;

        /// <summary>Owner: a relayed output frame arrived for this LOCAL shared device —
        /// a remote game is driving it. Refreshes the sole-writer lease.</summary>
        public static bool ClaimOutput(string localDevicePath, string peerFingerprint = null)
        {
            if (string.IsNullOrEmpty(localDevicePath)) return false;
            lock (_ownerLock)
            {
                // A frame decoded before its peer's last session dropped, and
                // claiming after the release, would re-own the device for a peer
                // that is gone, with nothing left to release it. Membership is
                // asked here, under the same lock the release takes, so a claim
                // either precedes the removal (and the release that follows it
                // cleans up) or sees no session and is refused.
                if (!string.IsNullOrEmpty(peerFingerprint) && !PeerHasSession(peerFingerprint)) return false;
                _outputLease[localDevicePath] = Environment.TickCount64;
                _peerWroteLast[localDevicePath] = peerFingerprint ?? string.Empty;
                return true;
            }
        }

        // Claim, release and takeover are one critical section (#402): a release
        // that compared a peer's record and then removed by key alone could
        // delete another peer's newer claim, and its fresh lease with it.
        private static readonly object _ownerLock = new object();

        /// <summary>Owner: whether a peer has a live session right now, answered
        /// by the link server from its connection list. Connect and drop
        /// EVENTS are not used for this: their order across threads is not the
        /// order of membership, and a late drop notice after a reconnect, or a
        /// late connect notice after a revoke, mis-marked the peer for the
        /// life of a session. Unset (no link server) means every peer counts
        /// as connected, which is the pre-#402 behavior.</summary>
        public static Func<string, bool> IsPeerConnected { get; set; }

        private static bool PeerHasSession(string peerFingerprint)
        {
            var q = IsPeerConnected;
            if (q == null) return true;
            try { return q(peerFingerprint); } catch { return false; }
        }

        // Who wrote the device's output last, and which peer (#402). A peer
        // holding an unchanged rumble ships it once (the dedup above), so its
        // lease lapses while its rumble is still meant. The owner's zero-slot
        // stop asks this, not the lease, before ending a rumble on a device
        // with no local assignments: peer-written output is the peer's to end.
        // Released when the local pipeline takes over (the lease policy: a
        // mapped local output may take the device back once the lease lapses),
        // when the peer's last session drops, when the device stops being
        // shared, and on Clear. A peer that leaves mid-rumble sent no zero,
        // and without those releases its rumble would stand forever.
        private static readonly ConcurrentDictionary<string, string> _peerWroteLast = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Owner: the local output pipeline is taking this device
        /// back (its lease lapsed and a local slot writes). The peer is no
        /// longer the last writer.</summary>
        public static void NoteLocalWrite(string localDevicePath)
        {
            if (string.IsNullOrEmpty(localDevicePath)) return;
            lock (_ownerLock) _peerWroteLast.TryRemove(localDevicePath, out _);
        }

        /// <summary>Owner: true when a peer's relayed frame was the last thing
        /// written to this LOCAL device, whether or not its lease is still fresh.</summary>
        public static bool PeerWroteLast(string localDevicePath)
        {
            if (string.IsNullOrEmpty(localDevicePath)) return false;
            lock (_ownerLock) return _peerWroteLast.ContainsKey(localDevicePath);
        }

        /// <summary>Owner: the peer's last session dropped. Every device it wrote
        /// last is released, lease and ownership, so the local pipeline's
        /// zero-slot stop may end what the peer left running. Another peer's
        /// newer claim on the same device is left alone. A release that arrives
        /// after the peer has a NEW session (the old drop's notice delivered
        /// late) does nothing: membership, not the notice, decides.</summary>
        public static void ReleasePeer(string peerFingerprint)
        {
            if (string.IsNullOrEmpty(peerFingerprint)) return;
            lock (_ownerLock)
            {
                if (PeerHasSession(peerFingerprint)) return;
                foreach (var kv in _peerWroteLast.ToArray())
                {
                    if (!string.Equals(kv.Value, peerFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                    _peerWroteLast.TryRemove(kv.Key, out _);
                    _outputLease.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>Owner: this local device is no longer shared out. Whatever a
        /// peer wrote to it is no longer the peer's to hold.</summary>
        public static void ReleaseDevice(string localDevicePath)
        {
            if (string.IsNullOrEmpty(localDevicePath)) return;
            lock (_ownerLock)
            {
                _peerWroteLast.TryRemove(localDevicePath, out _);
                _outputLease.TryRemove(localDevicePath, out _);
            }
        }

        /// <summary>Owner: true while a peer's relay holds the output lease on this LOCAL
        /// device, so the local output pipeline must skip its write (the relay is the sole
        /// writer). Always false for an unshared device.</summary>
        public static bool IsClaimedByPeer(string localDevicePath) =>
            !string.IsNullOrEmpty(localDevicePath)
            && _outputLease.TryGetValue(localDevicePath, out var t)
            && Environment.TickCount64 - t <= OutputLeaseMs;

    }
}
