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

        // Feature commands only. They are EVENTS (audio test start and stop,
        // calibration actions) where order and count both matter, so they
        // queue rather than coalesce, and the mode is Wait so a full channel
        // makes TryWrite return FALSE and the producer can return its rented
        // buffer. See the note on the state latch below for why the state
        // lane no longer uses a channel at all.
        private const int ChannelCapacity = 64;

        private readonly Channel<Ds5Effect> _channel;

        // ── The state latch ──
        //
        // Effect payloads are STATE: each one replaces the one before it, so
        // exactly one is ever worth holding. This used to be a 64-deep bounded
        // channel with FullMode.DropWrite, on the stated belief that TryWrite
        // returns false when full so the producer could return its pooled
        // buffer. It does not. Every Drop mode returns TRUE and discards the
        // item silently, so every discarded packet leaked its ArrayPool rental.
        //
        // Measured, not theorised: a trace from a title driving this lane at
        // 18,000 packets per second (#300) showed enq around 18,000 with
        // coalesced plus written around 7,550, and drop=0 with the depth pinned
        // at 64. The missing ~10,500 per second were leaking their buffers, at
        // which point ArrayPool simply allocates more and the GC pays for it.
        //
        // A single slot removes the whole class: the producer swaps its payload
        // in and immediately returns whatever it displaced, so nothing is ever
        // silently dropped and nothing can leak, however fast the game writes.
        private readonly object _latchLock = new();
        private Ds5Effect _latest;
        private bool _hasLatest;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly CancellationTokenSource _cts = new();
        private Task _worker;
        private readonly int _padIndex;
        private volatile bool _disposed;

        public DualSensePassthroughDispatcher(int padIndex)
        {
            _padIndex = padIndex;
            _channel = Channel.CreateBounded<Ds5Effect>(new BoundedChannelOptions(ChannelCapacity)
            {
                // Wait, so TryWrite reports a full channel instead of eating
                // the item: the producer needs that answer to return its
                // rented buffer.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        /// <summary>Starts the background worker. Idempotent: a second call
        /// is a no-op.</summary>
        public void Start()
        {
            if (_worker != null) return;
            _worker = Task.Run(() => DispatchLoopAsync(_cts.Token));
        }

        // ── Effect-lane heartbeat (PADFORGE_DIAG only) ──
        //
        // The field reports this exists for: adaptive triggers keep acting
        // after the game closes, and the lag grows over a session. Both are
        // invisible from the outside, because a stuck trigger and a trigger
        // being re-written every tick feel similar and neither says who is
        // writing. One line per second answers it directly.
        //
        //   enq       packets the GAME produced this second. 0 means the game
        //             stopped writing, which is the whole question after exit.
        //   drop      enqueues refused because the channel was full. Sustained
        //             drops mean the pad cannot keep up with the game.
        //   wr        packets actually written to the physical pad.
        //   depth     packets still queued right now. A depth that climbs and
        //             stays high IS the growing delay, measured rather than
        //             inferred.
        //   wmax      worst single write in ms this second. A Bluetooth link
        //             under audio load shows up here first.
        //   sinceEnq  ms since the last game packet. The decisive field after
        //             a game exits: if wr keeps counting while sinceEnq climbs,
        //             the backlog is still draining; if BOTH are quiet and the
        //             pad still moves, the writer is somewhere else entirely
        //             (UserEffectsDispatcher's own 30 Hz effect pass), which is
        //             a different bug from this lane.
        private long _hbEnq, _hbDrop, _hbWr, _hbLastLog, _hbLastEnqTicks, _hbCoalesced, _hbDup;
        private double _hbWorstWriteMs;

        /// <summary>Floor on the gap between forwarded state packets, so the
        /// pad's link sees a bounded rate no matter how fast the game writes.
        ///
        /// <para>Every payload on this lane is STATE, not an event: a trigger
        /// program, a lightbar colour, rumble levels. Each one supersedes the
        /// last, so sending the newest at a cap is indistinguishable at the
        /// pad from sending all of them, and 8 ms is far finer than any of it
        /// is perceived at.</para>
        ///
        /// <para>What it prevents: this dispatcher forwarded one packet per
        /// packet the game wrote, and the design assumption written at the top
        /// of this file is 30-60 Hz. A title that writes faster than the
        /// physical link can carry does not back up in our channel, which
        /// drains into SDL quickly. It backs up BELOW us in the OS Bluetooth
        /// write path, which is deep enough to hold a minute and keeps
        /// draining after the game exits. That is a growing delay no buffer of
        /// ours can bound, reported on GTA V Enhanced (#300) while three other
        /// titles showed nothing. Capping the outgoing rate removes the
        /// mechanism instead of trying to size a queue against it.</para>
        ///
        /// <para>Games inside the documented cadence are unaffected: at 60 Hz
        /// packets arrive ~16 ms apart, so every one is sent the moment it
        /// arrives and nothing is ever coalesced.</para></summary>
        /// <summary>Bluetooth floor. A DualSense BT link delivers on its
        /// connection interval, so writing faster than this does not reach the
        /// pad sooner, it queues below us. That queue is what produced the
        /// original runaway delay, so this stays.</summary>
        private const long MinWriteIntervalBtMs = 8;    // 125 Hz

        /// <summary>USB floor. No connection interval to respect, and the
        /// field trace measured writes at 0.0 to 0.3 ms, so a finer floor costs
        /// almost nothing and preserves four times the detail. Reported by
        /// Jobima1st (#300): with the delay gone, what remains is skipping,
        /// which is this number.</summary>
        private const long MinWriteIntervalUsbMs = 2;   // 500 Hz

        private long _lastWriteTicks;

        /// <summary>Transport of the targets seen on the last dispatch. Written
        /// by the worker inside DispatchOne, read by the worker when pacing the
        /// next write, so no synchronisation is needed.</summary>
        private bool _lastTargetsWereBt = true;

        private long MinWriteIntervalMs => _lastTargetsWereBt
            ? MinWriteIntervalBtMs : MinWriteIntervalUsbMs;

        /// <summary>The payload most recently written, kept so an identical
        /// one can be skipped entirely. Every payload on this lane is state, so
        /// re-sending an unchanged one changes nothing on the pad and only
        /// consumes the budget a real change needs. The measured title emits up
        /// to 19,600 packets a second, and if most are repeats then skipping
        /// them is free detail for the ones that are not.</summary>
        private byte[] _lastSent;
        private int _lastSentLength;

        /// <summary>Forwards one packet and records the write cost.</summary>
        private void WriteOne(in Ds5Effect effect)
        {
            // An unchanged state payload is a write that cannot alter anything
            // on the pad. Features are events and always go out.
            if (!effect.IsFeature)
            {
                if (_lastSentLength == effect.Length
                    && _lastSent != null
                    && effect.Buffer.AsSpan(0, effect.Length)
                        .SequenceEqual(_lastSent.AsSpan(0, _lastSentLength)))
                {
                    ArrayPool<byte>.Shared.Return(effect.Buffer);
                    if (SdlDiagLog.IsMirroring) Interlocked.Increment(ref _hbDup);
                    return;
                }
                if (_lastSent == null || _lastSent.Length < effect.Length)
                    _lastSent = new byte[effect.Length];
                effect.Buffer.AsSpan(0, effect.Length).CopyTo(_lastSent);
                _lastSentLength = effect.Length;
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                DispatchOne(effect);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(effect.Buffer);
            }
            if (!SdlDiagLog.IsMirroring) return;
            Interlocked.Increment(ref _hbWr);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > _hbWorstWriteMs) _hbWorstWriteMs = ms;
        }

        private void HeartbeatNoteEnqueue(bool accepted)
        {
            if (!SdlDiagLog.IsMirroring) return;
            if (accepted) { Interlocked.Increment(ref _hbEnq); Interlocked.Exchange(ref _hbLastEnqTicks, Environment.TickCount64); }
            else Interlocked.Increment(ref _hbDrop);
        }

        private void HeartbeatTick()
        {
            if (!SdlDiagLog.IsMirroring) return;
            long now = Environment.TickCount64;
            if (_hbLastLog == 0) { _hbLastLog = now; return; }
            if (now - _hbLastLog < 1000) return;

            long lastEnq = Interlocked.Read(ref _hbLastEnqTicks);
            // Depth is 0 or 1 by construction now (one state slot), plus
            // whatever vendor commands are queued. It stays in the line as the
            // proof of that: a depth pinned at a queue length was the shape of
            // the leak this replaced.
            int depth = (_hasLatest ? 1 : 0)
                + (_channel.Reader.CanCount ? _channel.Reader.Count : 0);
            SdlDiagLog.WriteLine(
                "DS5EFFECT slot=" + _padIndex
                + " enq=" + Interlocked.Exchange(ref _hbEnq, 0)
                + " drop=" + Interlocked.Exchange(ref _hbDrop, 0)
                + " wr=" + Interlocked.Exchange(ref _hbWr, 0)
                + " coal=" + Interlocked.Exchange(ref _hbCoalesced, 0)
                + " dup=" + Interlocked.Exchange(ref _hbDup, 0)
                + " bt=" + (_lastTargetsWereBt ? 1 : 0)
                + " depth=" + depth
                + " wmax=" + _hbWorstWriteMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " sinceEnq=" + (lastEnq == 0 ? -1 : now - lastEnq));
            _hbLastLog = now;
            _hbWorstWriteMs = 0;
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
            Ds5Effect superseded = default;
            bool hadSuperseded;
            lock (_latchLock)
            {
                hadSuperseded = _hasLatest;
                if (hadSuperseded) superseded = _latest;
                _latest = effect;
                _hasLatest = true;
            }
            if (hadSuperseded)
            {
                // Returned HERE, the instant it is displaced. This is the step
                // the channel could never perform, because a silently dropped
                // item is never handed back to anyone.
                ArrayPool<byte>.Shared.Return(superseded.Buffer);
                if (SdlDiagLog.IsMirroring) Interlocked.Increment(ref _hbCoalesced);
            }
            HeartbeatNoteEnqueue(accepted: true);
            if (_signal.CurrentCount == 0)
            {
                try { _signal.Release(); } catch (SemaphoreFullException) { }
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
                // Full (Wait mode reports it) or completed on a Dispose race.
                ArrayPool<byte>.Shared.Return(buf);
                HeartbeatNoteEnqueue(accepted: false);
                return;
            }
            HeartbeatNoteEnqueue(accepted: true);
            if (_signal.CurrentCount == 0)
            {
                try { _signal.Release(); } catch (SemaphoreFullException) { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _channel.Writer.TryComplete(); } catch { }
            try { _cts.Cancel(); } catch { }
            try { _signal.Release(); } catch { }

            // Worker drains and returns rented buffers; give it a brief
            // window to complete cleanly.  The OutputReceived subscription
            // must be unsubscribed BEFORE Dispose to stop new enqueues —
            // HMaestroVirtualController owns that ordering.
            try { _worker?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }

            // Hand every rental back. The worker may have exited before
            // draining, and the latch holds at most one payload.
            lock (_latchLock)
            {
                if (_hasLatest)
                {
                    ArrayPool<byte>.Shared.Return(_latest.Buffer);
                    _hasLatest = false;
                }
            }
            while (_channel.Reader.TryRead(out var leftover))
                ArrayPool<byte>.Shared.Return(leftover.Buffer);

            try { _cts.Dispose(); } catch { }
            try { _signal.Dispose(); } catch { }
        }

        private async Task DispatchLoopAsync(CancellationToken ct)
        {
            var reader = _channel.Reader;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Wake on a producer signal, or once a second so the
                    // heartbeat still beats while the game writes nothing. Its
                    // silence then means the WORKER stopped, which is a
                    // different finding from an idle lane.
                    try { await _signal.WaitAsync(1000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    // Vendor commands first, in order, never coalesced and
                    // never rate limited.
                    while (reader.TryRead(out var feature))
                        WriteOne(feature);

                    // Then the newest state, at most one per
                    // MinWriteIntervalMs. Taking the latch clears it, so a
                    // payload is written exactly once and anything that arrives
                    // while we pace has already displaced its predecessor at
                    // the producer.
                    Ds5Effect pending = default;
                    bool havePending;
                    lock (_latchLock)
                    {
                        havePending = _hasLatest;
                        if (havePending) { pending = _latest; _hasLatest = false; }
                    }

                    if (havePending)
                    {
                        long sinceMs = _lastWriteTicks == 0 ? long.MaxValue
                            : (System.Diagnostics.Stopwatch.GetTimestamp() - _lastWriteTicks)
                              * 1000 / System.Diagnostics.Stopwatch.Frequency;
                        if (sinceMs < MinWriteIntervalMs)
                        {
                            try { await Task.Delay((int)(MinWriteIntervalMs - sinceMs), ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { ArrayPool<byte>.Shared.Return(pending.Buffer); break; }
                        }
                        WriteOne(pending);
                        _lastWriteTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    HeartbeatTick();
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

            // Pace the NEXT write against the transport actually in use. A
            // mixed set takes the Bluetooth floor, since the slowest link sets
            // the rate the pad set can absorb.
            bool anyBt = false;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i].IsBt) { anyBt = true; break; }
            _lastTargetsWereBt = anyBt;

            // Feature lane: Sony vendor test command (SetFeature 0x80 —
            // firmware sine generator, speaker/headphone routing,
            // calibration actions). SDL_SendGamepadEffect only carries
            // output reports, so these go out via HidD_SetFeature on the
            // device path; BT targets get the 0x53-seeded feature CRC.
            if (effect.IsFeature)
            {
                // Sony vendor audio test (deviceId 6 = AUDIO): while the
                // firmware waveout runs, the tester owns the pad's audio
                // plane — see AudioPassthroughService.SetVendorAudioTest.
                // The routing byte is sticky, so the effects dispatcher
                // must rewrite it NOW (restore on start, re-assert on
                // end); its timer only runs for lightbar animation, hence
                // the explicit dispatch nudge.
                //
                // Activity STARTS on either the route-config command
                // (action 4, BUILTIN_MIC_CALIB_DATA_VERIFY — the reference
                // tester sends it ~20 ms before the tone, which gives the
                // routing restore a head start over the waveout onset) or
                // a WAVEOUT_CTRL on (action 2, param != 0). It ENDS only
                // on WAVEOUT_CTRL off (action 2, param == 0) — or the
                // 60 s expiry for abandoned tests.
                if (effect.Length >= 3 && effect.Buffer[0] == 6
                    && (effect.Buffer[1] == 2 || effect.Buffer[1] == 4))
                {
                    bool testOn = effect.Buffer[1] == 4 || effect.Buffer[2] != 0;
                    foreach (var t in targets)
                    {
                        AudioPassthroughService.SetVendorAudioTest(t.DeviceGuid, testOn);

                        // Replay the tester's most recently MASKED audio
                        // state. The tester mutes the OTHER output before
                        // each waveout (speaker volume 0 for the headphone
                        // test) because the firmware's wave-route latches:
                        // the headphone test's route-config carries
                        // params[2]=0 for the speaker slot and zero means
                        // "unchanged", so after any speaker test the
                        // headphone tone ALSO plays on the speaker — at
                        // whatever volume the pad holds. Our mask had eaten
                        // that mute (and the asserts pinned volume high),
                        // which is exactly the bleed-after-speaker-test the
                        // field trace captured.
                        if (testOn && _maskedAudioStash.TryGetValue(t.DeviceGuid, out var stash))
                        {
                            var replay = new byte[StandardPayloadSize];
                            lock (stash)
                            {
                                replay[0] = stash[0];   // valid_flag0 audio bits (union)
                                replay[1] = stash[1];   // valid_flag1 audio_flags2 bit
                                replay[4] = stash[2];   // headphone volume
                                replay[5] = stash[3];   // speaker volume
                                replay[6] = stash[4];   // mic volume
                                replay[7] = stash[5];   // audio_flags
                                replay[37] = stash[6];  // audio_flags2
                            }
                            try { SDL_SendGamepadEffect(t.GamepadHandle, replay, 0, StandardPayloadSize); }
                            catch { }
                        }
                    }
                    UserEffectsDispatcher.NotifySoundRoutingChanged(_padIndex);
                }

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
                bool wantsPath = AudioPassthroughService.WantsSpeakerPath(target.DeviceGuid);
                bool hasAudioBits = effect.Length > 7
                    && (((effect.Buffer[0] & 0xF0) != 0) || ((effect.Buffer[1] & 0x80) != 0));
                if (wantsPath)
                {
                    // Stash the audio surface we're about to strip when the
                    // packet actually carries one (any audio valid bit set),
                    // so a vendor-test activation can replay the writer's
                    // intended audio state.
                    if (hasAudioBits)
                    {
                        // ACCUMULATE, don't overwrite: the reference tester
                        // splits one volume update into two packets (vf0 =
                        // 0xA0 carrying the speaker volume, then vf0 = 0x90
                        // carrying the headphone volume). Overwriting kept
                        // only the last packet's valid bits, so the replay
                        // shipped spkVol=0 WITHOUT its valid bit and the
                        // firmware ignored the mute — the cold-start bleed.
                        // Each field updates only when its valid bit is set;
                        // the replay carries the union.
                        var stash = _maskedAudioStash.GetOrAdd(target.DeviceGuid, _ => new byte[7]);
                        lock (stash)
                        {
                            byte vf0 = (byte)(effect.Buffer[0] & 0xF0);
                            byte vf1 = (byte)(effect.Buffer[1] & 0x80);
                            stash[0] |= vf0;
                            stash[1] |= vf1;
                            if ((vf0 & 0x10) != 0) stash[2] = effect.Buffer[4]; // headphone vol
                            if ((vf0 & 0x20) != 0) stash[3] = effect.Buffer[5]; // speaker vol
                            if ((vf0 & 0x40) != 0) stash[4] = effect.Buffer[6]; // mic vol
                            if ((vf0 & 0x80) != 0) stash[5] = effect.Buffer[7]; // audio_flags
                            if (vf1 != 0 && effect.Length > 37) stash[6] = effect.Buffer[37]; // audio_flags2
                        }
                    }

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
        /// PlayStationEffectWriter.ApplyAudioControl2 pokes (USB report byte 38 =
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

        /// <summary>Per-target stash of the most recently MASKED audio
        /// surface (valid_flag0 audio bits, valid_flag1 audio_flags2 bit,
        /// volume/audio_flags bytes [4..7], audio_flags2 [37]). The
        /// reference tester writes its audio state — speaker volume 0 for
        /// the headphone test, 85 for the speaker test — ~20 ms BEFORE
        /// the test commands, while the mirror still owns the pad and the
        /// mask strips it. Replaying the stash at test activation hands
        /// the tester exactly the audio state it asked for; without this,
        /// the firmware's cross-output leak plays the headphone tone
        /// through a speaker still sitting at the mirror's volume.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte[]> _maskedAudioStash = new();


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
