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
        // game cadence. 64 slots is generous for 30-60 Hz writes against
        // an 8 ms HM ring and a typical sub-millisecond SDL write.
        //
        // FullMode = DropWrite (not DropOldest): with DropOldest the
        // channel silently dequeues the oldest entry on overflow without
        // surfacing it to the reader, which means the rented ArrayPool
        // buffer attached to that entry was never returned — a per-overflow
        // permanent leak. DropWrite makes TryWrite return false on
        // overflow so the existing Enqueue catch returns the buffer
        // immediately. The semantic difference for state-based writes
        // (newest dropped instead of oldest) is irrelevant: under sustained
        // pressure either policy drops some packets, and the next state
        // write still arrives at the controller within milliseconds.
        private const int ChannelCapacity = 64;

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
                FullMode = BoundedChannelFullMode.DropWrite,
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
        /// after a buffer rent, copy, and channel write — no blocking I/O.
        /// On overflow (DropWrite mode) <c>TryWrite</c> returns false and
        /// the rented buffer is returned to the pool so it doesn't leak.
        /// On Dispose race the same branch handles it.</summary>
        public void Enqueue(byte reportId, ReadOnlySpan<byte> payload)
        {
            if (_disposed) return;
            if (payload.IsEmpty) return;

            // Rent at least payload.Length; ArrayPool may return a larger buffer.
            byte[] buf = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buf);

            var effect = new Ds5Effect(buf, payload.Length, reportId, IsFeature: false);
            if (!_channel.Writer.TryWrite(effect))
            {
                // Channel full (DropWrite) or completed (Dispose race) —
                // either way, return the rented buffer.
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>Enqueues a Sony vendor test command (SetFeature 0x80
        /// body, report-ID-stripped as published by the driver) for
        /// forwarding to the assigned physical DualSenses via
        /// HidD_SetFeature. Same rent-copy-enqueue contract as
        /// <see cref="Enqueue"/>.</summary>
        public void EnqueueFeature(byte reportId, ReadOnlySpan<byte> payload)
        {
            if (_disposed) return;
            if (payload.IsEmpty) return;

            byte[] buf = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buf);

            var effect = new Ds5Effect(buf, payload.Length, reportId, IsFeature: true);
            if (!_channel.Writer.TryWrite(effect))
            {
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

            // Feature lane: Sony vendor test command (SetFeature 0x80 —
            // firmware sine generator, speaker/headphone routing,
            // calibration actions). SDL_SendGamepadEffect only carries
            // output reports, so these go out via HidD_SetFeature on the
            // device path; BT targets get the 0x53-seeded feature CRC.
            if (effect.IsFeature)
            {
                byte[] report = new byte[effect.Length + 1];
                report[0] = effect.ReportId;
                Array.Copy(effect.Buffer, 0, report, 1, effect.Length);
                foreach (var target in targets)
                {
                    if (string.IsNullOrEmpty(target.DevicePath)) continue;
                    try { RawHidOutput.SetFeature(target.DevicePath, report, target.IsBt); }
                    catch { }
                }
                return;
            }

            // Lazily-built copy of the payload with the audio-control
            // surface masked, shared across targets that need it.
            byte[] sanitized = null;

            foreach (var target in targets)
            {
                int forwardLen = effect.Length;

                // Edge → Standard size routing: when our captured payload
                // is the 63-byte Edge form but the target is a 47-byte
                // standard DualSense, truncate.  Edge tail bytes are
                // profile/paddle-specific and meaningless on standard.
                if (target.IsEdge == false && forwardLen > StandardPayloadSize)
                    forwardLen = StandardPayloadSize;

                // Game audio-control bytes forward by default. While
                // PadForge owns the device's audio session (mirror or a
                // sound macro — i.e. an active sink), mask the audio
                // fields so the game's packet can't clobber the speaker
                // routing / volume / pre-gain mid-stream. Rumble,
                // triggers, lightbar, and LEDs forward untouched either
                // way; the game gets the pad back when the sink drops.
                byte[] buf = effect.Buffer;
                if (AudioPassthroughService.WantsSpeakerPath(target.DeviceGuid))
                {
                    if (sanitized == null)
                    {
                        sanitized = new byte[effect.Length];
                        Array.Copy(effect.Buffer, sanitized, effect.Length);
                        MaskAudioControl(sanitized);
                    }
                    buf = sanitized;
                }

                try
                {
                    SDL_SendGamepadEffect(target.GamepadHandle, buf, 0, forwardLen);
                }
                catch
                {
                    // Per-packet error — DualSense disconnected mid-write,
                    // SDL handle gone stale, etc.  Drop and continue.
                }
            }
        }

        /// <summary>Clears the audio-control surface of a report-ID-stripped
        /// DS5 effect payload (the 47-byte <c>DS5EffectsState_t</c> form,
        /// common prefix shared by the 63-byte Edge form): valid_flag0 bits
        /// 4-7 (headphone / speaker / mic volume, audio control), the four
        /// volume / audio_flags bytes at [4..7], and the
        /// valid_flag1-bit-7-gated audio_flags2 pre-gain at common+37.
        /// Offsets per dualsensectl's packed output struct — same layout
        /// SonyEffectWriter.ApplyAudioControl2 pokes (USB report byte 38 =
        /// payload byte 37).</summary>
        private static void MaskAudioControl(byte[] p)
        {
            if (p.Length < 8) return;
            p[0] &= 0x0F;   // valid_flag0: drop headphone/speaker/mic volume + audio control
            p[1] &= 0x7F;   // valid_flag1: drop audio_flags2 (pre-gain) gate
            p[4] = 0;       // headphone volume
            p[5] = 0;       // speaker volume
            p[6] = 0;       // mic volume
            p[7] = 0;       // audio_flags (output routing)
            if (p.Length > 37) p[37] = 0; // audio_flags2 (speaker pre-gain)
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

                    string path = ud.DevicePath ?? string.Empty;
                    bool isBt = path.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0;
                    result.Add(new DualSenseTarget(handle, isEdge, ud.InstanceGuid, path, isBt));
                }
            }
            return result.Count == 0 ? null : result;
        }

        /// <summary>Per-target tuple used during dispatch.  IsEdge gates
        /// the size-routing decision (truncate Edge → Standard); DeviceGuid
        /// keys the audio-session ownership check; DevicePath + IsBt serve
        /// the feature-report lane (HidD_SetFeature + BT CRC).</summary>
        private readonly record struct DualSenseTarget(IntPtr GamepadHandle, bool IsEdge, Guid DeviceGuid, string DevicePath, bool IsBt);

        /// <summary>Channel record carrying a rented buffer plus the
        /// payload length and originating Report ID.  The buffer is owned
        /// by the worker after enqueue; the worker returns it to the
        /// pool after dispatch.  IsFeature routes the packet down the
        /// HidD_SetFeature lane instead of SDL_SendGamepadEffect.</summary>
        private readonly record struct Ds5Effect(byte[] Buffer, int Length, byte ReportId, bool IsFeature);

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

        /// <summary>Returns true when the specific device identified by
        /// <paramref name="deviceGuid"/> is a passthrough target for
        /// <paramref name="padIndex"/>. Used by
        /// <c>SlotRumbleForDeviceProvider</c> to zero out rumble bytes for
        /// the device that the passthrough dispatcher is also writing —
        /// avoiding the dispatcher / passthrough double-fire on real
        /// DualSenses while still letting non-DualSense Sony devices on
        /// the same slot (DS4) and non-Sony devices (Xbox via Step 2)
        /// receive rumble normally.</summary>
        public static bool IsPassthroughTarget(int padIndex, Guid deviceGuid)
        {
            if (deviceGuid == Guid.Empty) return false;

            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null) return false;

            // Cheap path: must be mapped to the slot.
            bool mapped = false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != padIndex) continue;
                    if (us.InstanceGuid != deviceGuid) continue;
                    mapped = true;
                    break;
                }
            }
            if (!mapped) return false;

            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null) continue;
                    if (ud.InstanceGuid != deviceGuid) continue;
                    if (!ud.IsOnline) return false;
                    if (ud.VendorId != SonyVid) return false;
                    return ud.ProdId == PidStandard || ud.ProdId == PidEdge;
                }
            }
            return false;
        }
    }
}
