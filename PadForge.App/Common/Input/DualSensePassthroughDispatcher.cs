using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Per-virtual-DualSense-slot dispatcher for game-driven DS5 effect
    /// output reports (Sony Report ID 0x02 USB / 0x31 BT). The HM
    /// <c>OutputReceived</c> callback runs on the polling thread and must
    /// not block; it rents a buffer from <see cref="ArrayPool{T}"/>,
    /// copies the payload, and writes a single channel record. A
    /// dedicated worker Task drains the channel and forwards each packet
    /// via <c>SDL_SendGamepadEffect</c> to every assigned physical
    /// DualSense / DualSense Edge.
    ///
    /// <para>Why decoupled: games drive adaptive trigger output reports at
    /// 30-60 Hz during sustained input (Returnal sustained-fire is the
    /// canonical example per HIDMaestro's characterization). HM's 64-slot
    /// ring polled at 8 ms absorbs the input cadence fine, but a synchronous
    /// SDL USB write per packet inside the OutputReceived callback can stack
    /// a few ms each, especially over BT, and approach HM's 512 ms stall
    /// threshold under coalesced spikes. The existing rumble path is safe
    /// by accident — its callback only writes scalars into a state buffer
    /// and Step 5's vibrate-push thread does the actual SDL call on its
    /// own cadence. The new pass-through path needs analogous decoupling
    /// explicitly.</para>
    ///
    /// <para>Edge ↔ Standard size routing: when the captured payload comes
    /// from an Edge virtual (63 bytes for USB) and the assigned physical
    /// is a standard DualSense (47-byte report), the Edge tail bytes are
    /// truncated. SDL accepts short messages. When the captured payload
    /// is from a standard virtual (47 bytes) and the assigned physical is
    /// Edge, the message is forwarded as-is — Edge's report descriptor
    /// declares 63 bytes but tolerates short writes.</para>
    /// </summary>
    internal sealed class DualSensePassthroughDispatcher : IDisposable
    {
        // Sony VID/PIDs we'll forward to.
        private const ushort SonyVid = 0x054C;
        private const ushort PidStandard = 0x0CE6;
        private const ushort PidEdge = 0x0DF2;
        private const int StandardPayloadSize = 47;

        // Bounded channel keeps memory pressure predictable under runaway
        // game cadence. DropOldest matches the recipe's optional coalescing
        // policy: trigger / lightbar / audio / mute are all *state*, not
        // event sequences, so the latest applied state is what matters.
        // 32 slots is generous for 30-60 Hz writes against an 8 ms HM ring
        // and a typical sub-millisecond SDL write.
        private const int ChannelCapacity = 32;

        private readonly Channel<Ds5Effect> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task _worker;
        private readonly int _padIndex;
        private volatile bool _disposed;

        public DualSensePassthroughDispatcher(int padIndex)
        {
            _padIndex = padIndex;
            _channel = Channel.CreateBounded<Ds5Effect>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        /// <summary>Starts the background worker. Idempotent — second call is a no-op.</summary>
        public void Start()
        {
            if (_worker != null) return;
            _worker = Task.Run(() => DispatchLoopAsync(_cts.Token));
        }

        /// <summary>HM polling thread enqueues here. Returns immediately
        /// after a buffer rent, copy, and channel write — no blocking
        /// I/O.  When the channel is full the oldest queued packet is
        /// dropped (its buffer returned to the pool inside the worker
        /// when it would have been consumed) since trigger / lightbar
        /// state is last-writer-wins.</summary>
        public void Enqueue(byte reportId, ReadOnlySpan<byte> payload)
        {
            if (_disposed) return;
            if (payload.IsEmpty) return;

            // Rent at least payload.Length; ArrayPool may return a larger buffer.
            byte[] buf = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buf);

            var effect = new Ds5Effect(buf, payload.Length, reportId);
            if (!_channel.Writer.TryWrite(effect))
            {
                // Channel completed (Dispose race) — return the rented buffer.
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _channel.Writer.TryComplete(); } catch { }
            try { _cts.Cancel(); } catch { }

            // Worker drains and returns rented buffers; give it a brief
            // window to complete cleanly.  The OutputReceived subscription
            // must be unsubscribed BEFORE Dispose to stop new enqueues —
            // HMaestroVirtualController owns that ordering.
            try { _worker?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        private async Task DispatchLoopAsync(CancellationToken ct)
        {
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var effect))
                    {
                        try
                        {
                            DispatchOne(effect);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(effect.Buffer);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* Dispose path */ }
            catch
            {
                // Last-resort guard so a transient SDL error doesn't kill
                // the worker. Per-packet errors are already swallowed in
                // DispatchOne; this catches anything that escapes.
            }
        }

        private void DispatchOne(in Ds5Effect effect)
        {
            // Resolve assigned DualSense physicals on every packet. Lookup
            // is a small linear scan over UserSettings entries with
            // MapTo == padIndex.  Caching via a slot flag is a Commit 1.5
            // optimization if profiling shows it matters.
            var targets = ResolveAssignedDualSenseHandles(_padIndex);
            if (targets == null || targets.Count == 0) return;

            foreach (var target in targets)
            {
                int forwardLen = effect.Length;

                // Edge → Standard size routing: when our captured payload
                // is the 63-byte Edge form but the target is a 47-byte
                // standard DualSense, truncate.  Edge tail bytes are
                // profile/paddle-specific and meaningless on standard.
                if (target.IsEdge == false && forwardLen > StandardPayloadSize)
                    forwardLen = StandardPayloadSize;

                try
                {
                    SDL_SendGamepadEffect(target.GamepadHandle, effect.Buffer, 0, forwardLen);
                }
                catch
                {
                    // Per-packet error — DualSense disconnected mid-write,
                    // SDL handle gone stale, etc.  Drop and continue.
                }
            }
        }

        /// <summary>Returns the SDL gamepad handles for every physical
        /// DualSense / DualSense Edge currently mapped to
        /// <paramref name="padIndex"/>. Returns an empty list when none
        /// are mapped or all are offline.  Uses
        /// <see cref="SettingsManager.UserSettings"/> +
        /// <see cref="SettingsManager.UserDevices"/> as the resolution
        /// path; safe to call from any thread because both collections
        /// guard via SyncRoot internally.</summary>
        private static List<DualSenseTarget> ResolveAssignedDualSenseHandles(int padIndex)
        {
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null) return null;

            // Snapshot the InstanceGuid set under settings' lock.
            var guids = new List<Guid>(4);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != padIndex) continue;
                    if (us.InstanceGuid == Guid.Empty) continue;
                    guids.Add(us.InstanceGuid);
                }
            }
            if (guids.Count == 0) return null;

            var result = new List<DualSenseTarget>(guids.Count);
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null || !ud.IsOnline) continue;
                    if (ud.VendorId != SonyVid) continue;
                    bool isStandard = ud.ProdId == PidStandard;
                    bool isEdge = ud.ProdId == PidEdge;
                    if (!isStandard && !isEdge) continue;
                    if (!guids.Contains(ud.InstanceGuid)) continue;

                    IntPtr handle = ud.Device?.GamepadHandle ?? IntPtr.Zero;
                    if (handle == IntPtr.Zero) continue;

                    result.Add(new DualSenseTarget(handle, isEdge));
                }
            }
            return result.Count == 0 ? null : result;
        }

        /// <summary>Per-target tuple used during dispatch.  IsEdge gates
        /// the size-routing decision (truncate Edge → Standard).</summary>
        private readonly record struct DualSenseTarget(IntPtr GamepadHandle, bool IsEdge);

        /// <summary>Channel record carrying a rented buffer plus the
        /// payload length and originating Report ID.  The buffer is owned
        /// by the worker after enqueue; the worker returns it to the
        /// pool after dispatch.</summary>
        private readonly record struct Ds5Effect(byte[] Buffer, int Length, byte ReportId);

        /// <summary>Returns true when at least one DualSense / DualSense
        /// Edge is currently mapped + online for
        /// <paramref name="padIndex"/>.  Used by the rumble-pipeline
        /// gating in <c>HMaestroVirtualController</c> to skip the
        /// existing Sony rumble write when pass-through is active (rumble
        /// bytes are already inside the DS5 effect message and would
        /// otherwise double-fire).</summary>
        public static bool HasAssignedDualSense(int padIndex)
        {
            var targets = ResolveAssignedDualSenseHandles(padIndex);
            return targets != null && targets.Count > 0;
        }
    }
}
