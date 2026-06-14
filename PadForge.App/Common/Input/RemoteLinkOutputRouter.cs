using System;
using System.Collections.Concurrent;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Consumer-side hub for the reverse output relay (issue #138). A shared device
    /// is a <c>RemotePeerDevice</c> with a "peer://" path: the consumer's output
    /// pipeline computes the full, config-applied output for it and then fails at the
    /// final hardware write (CreateFileW on "peer://" fails; the SDL handle is Zero).
    /// Each write chokepoint hands its config-baked payload here instead; this maps
    /// the device path to its owner and ships a transport-agnostic semantic frame.
    /// The owner re-encodes it for the real hardware.
    ///
    /// <para>The mapping is fixed at connect time (peer:// path -&gt; owner fingerprint +
    /// link slot), so there is no per-frame route lookup beyond a dictionary read, and
    /// nothing here runs when no device is shared.</para>
    /// </summary>
    internal static class RemoteLinkOutputRouter
    {
        public readonly struct Target
        {
            public readonly string Fingerprint;
            public readonly byte LinkSlot;
            public Target(string fingerprint, byte linkSlot) { Fingerprint = fingerprint; LinkSlot = linkSlot; }
        }

        // peer:// device path -> owner target. Concurrent: registered/cleared on the
        // socket DeviceConnected/Disconnected thread, read on the polling/effect threads.
        private static readonly ConcurrentDictionary<string, Target> _byPath = new(StringComparer.Ordinal);

        // Exact-repeat dedup per path+channel so a static effect (held lightbar / steady
        // force) is sent once and a quiet device costs no bandwidth. Square-wave values
        // still forward (each distinct value differs from the last).
        private static readonly ConcurrentDictionary<string, byte[]> _lastSony = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, (ushort, ushort, ushort, ushort, int)> _lastVib = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte[]> _lastWheel = new(StringComparer.Ordinal);

        /// <summary>Wired by InputService to LinkServer.PushOutputEffect / PushAudio.</summary>
        public static Action<string, byte, byte[]> SendOutput { get; set; }
        public static Action<string, byte, byte[]> SendAudio { get; set; }

        // Diagnostics (#138 reverse-channel bring-up).
        public static long SonyCaptured, VibrationCaptured, WheelCaptured, AudioCaptured, Sent;
        public static int DeviceCount => _byPath.Count;

        public static bool IsPeerPath(string devicePath) =>
            !string.IsNullOrEmpty(devicePath) && devicePath.StartsWith("peer://", StringComparison.Ordinal);

        public static void Register(string devicePath, string fingerprint, byte linkSlot)
        {
            if (string.IsNullOrEmpty(devicePath) || string.IsNullOrEmpty(fingerprint)) return;
            _byPath[devicePath] = new Target(fingerprint, linkSlot);
            RemoteLinkDiag.Log($"router register {devicePath} -> {fingerprint.Substring(0, Math.Min(8, fingerprint.Length))} slot={linkSlot} (total {_byPath.Count})");
        }

        public static void Unregister(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return;
            _byPath.TryRemove(devicePath, out _);
            _lastSony.TryRemove(devicePath, out _);
            _lastVib.TryRemove(devicePath, out _);
            _lastWheel.TryRemove(devicePath, out _);
        }

        public static void Clear()
        {
            _byPath.Clear();
            _lastSony.Clear(); _lastVib.Clear(); _lastWheel.Clear();
        }

        // ── Ship: Sony effect (47/31-byte USB-shape body) ───────────────────

        /// <summary>True when the path is a shared device and the effect was shipped
        /// (so the caller skips its local write).</summary>
        public static bool ShipSonyEffect(string devicePath, ReadOnlySpan<byte> effectBody)
        {
            if (!_byPath.TryGetValue(devicePath, out var t)) return false;
            long n = System.Threading.Interlocked.Increment(ref SonyCaptured);
            if (effectBody.Length == 0) return true;
            if (_lastSony.TryGetValue(devicePath, out var prev) && prev.AsSpan().SequenceEqual(effectBody))
                return true;
            _lastSony[devicePath] = effectBody.ToArray();
            byte[] blob = OutputEffectCodec.EncodeSonyEffect(effectBody);
            Dispatch(t, blob);
            if (n == 1 || n % 240 == 0) RemoteLinkDiag.Log($"ship sony {devicePath} len={effectBody.Length} n={n}");
            return true;
        }

        // ── Ship: full Vibration (rumble + impulse + directional + condition) ─

        public static bool ShipVibration(string devicePath, Vibration v)
        {
            if (v == null || !_byPath.TryGetValue(devicePath, out var t)) return false;
            long n = System.Threading.Interlocked.Increment(ref VibrationCaptured);
            // Cheap dedup on the common scalar case (directional frames always ship).
            int dirHash = v.HasDirectionalData || v.HasConditionData
                ? unchecked((int)(v.EffectType * 31 + (uint)v.SignedMagnitude * 7 + v.Direction * 13 + v.Period))
                : 0;
            var key = (v.LeftMotorSpeed, v.RightMotorSpeed, v.LeftTriggerMotorSpeed, v.RightTriggerMotorSpeed, dirHash);
            if (!v.HasDirectionalData && !v.HasConditionData
                && _lastVib.TryGetValue(devicePath, out var pv) && pv.Equals(key))
                return true;
            _lastVib[devicePath] = key;
            byte[] blob = OutputEffectCodec.EncodeVibration(v);
            Dispatch(t, blob);
            if (n == 1 || n % 240 == 0) RemoteLinkDiag.Log($"ship vib {devicePath} ({v.LeftMotorSpeed},{v.RightMotorSpeed},{v.LeftTriggerMotorSpeed},{v.RightTriggerMotorSpeed}) dir={v.HasDirectionalData} cond={v.HasConditionData} n={n}");
            return true;
        }

        // ── Ship: wheel FFB (semantic; owner re-encodes per vendor) ─────────

        public static bool ShipWheel(string devicePath,
            bool hasCond, bool dir, short force, short peak, int ac, uint effect, int period,
            short pc, short nc, short off, int db, int ps, int ns, int condGain,
            ushort rangeDeg, ushort ledMask, bool ledValid)
        {
            if (!_byPath.TryGetValue(devicePath, out var t)) return false;
            long n = System.Threading.Interlocked.Increment(ref WheelCaptured);
            byte[] blob = OutputEffectCodec.EncodeWheel(hasCond, dir, force, peak, ac, effect, period,
                pc, nc, off, db, ps, ns, condGain, rangeDeg, ledMask, ledValid);
            if (_lastWheel.TryGetValue(devicePath, out var prev) && prev.AsSpan().SequenceEqual(blob))
                return true;
            _lastWheel[devicePath] = blob;
            Dispatch(t, blob);
            if (n == 1 || n % 240 == 0) RemoteLinkDiag.Log($"ship wheel {devicePath} force={force} range={rangeDeg} led={ledMask:X} n={n}");
            return true;
        }

        // ── Ship: speaker PCM (out of band on the Audio datagram) ───────────

        public static bool ShipAudio(string devicePath, byte[] pcmBlock)
        {
            if (pcmBlock == null || !_byPath.TryGetValue(devicePath, out var t)) return false;
            var send = SendAudio;
            if (send == null) return false;
            System.Threading.Interlocked.Increment(ref AudioCaptured);
            send(t.Fingerprint, t.LinkSlot, pcmBlock);
            return true;
        }

        private static void Dispatch(Target t, byte[] blob)
        {
            var send = SendOutput;
            if (send == null) return;
            send(t.Fingerprint, t.LinkSlot, blob);
            System.Threading.Interlocked.Increment(ref Sent);
        }
    }
}
