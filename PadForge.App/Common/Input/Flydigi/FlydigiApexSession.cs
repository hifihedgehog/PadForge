using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Flydigi;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;

namespace PadForge.Common.Input.Flydigi
{
    /// <summary>
    /// PadForge's own connection to one Flydigi Apex 4, Apex 5 or Apex 6
    /// Pro (#586): two handles on the pad's vendor collection beside SDL's,
    /// the identity the pad answered with, and one thread that writes
    /// everything PadForge sends it.
    ///
    /// <para>One writer per pad, the shape of
    /// <see cref="XboxImpulseHidWriter"/> and of ApexSenseBridge's
    /// Apex5Device, whose rules it keeps. The newest value wins for each
    /// trigger and for rumble, so a value replaced before it could go out
    /// is never sent (Apex5Device.cpp writerLoop). An Apex 5 on its
    /// receiver gets its writes 10 ms apart, and rumble waits 30 ms after
    /// a trigger write (asyncWritePolicy). An Apex 4 gets a trigger write
    /// no sooner than 25 ms after any write and two rumbles 10 ms apart
    /// (Apex4WritePacing.h). It acknowledges nothing, so each change is
    /// sent again after 100 and 500 ms, and since each change stops its
    /// sticks for about 50 ms, changes are rationed while repeats are not
    /// (flydigi-apex4-linux triggers.py, measured on the pad).</para>
    ///
    /// <para>The Apex 6 Pro's motors are voice coils, so it gets a stream:
    /// one report every 8 ms with eight samples for the triggers and each
    /// grip (<see cref="StreamLoop"/>, ApexSenseBridge
    /// Apex6HapticBridge.cpp run and renderBlock at its 1 kHz rate with its
    /// default mix of two triggers). The stream starts with the first
    /// effect and the motors go back to the pad after three quiet
    /// seconds.</para>
    ///
    /// <para>A command and the replies it waits for hold
    /// <see cref="_io"/>, so the lighting family's exchanges and this
    /// thread's writes never interleave inside one command.</para>
    /// </summary>
    internal sealed class FlydigiApexSession : IDisposable
    {
        /// <summary>Opens a collection for this session. Tests answer
        /// from a script.</summary>
        internal static Func<string, IHidReportChannel> Opener = path => HidReportChannel.Open(path);

        /// <summary>The millisecond clock.</summary>
        internal static Func<long> Clock = () => Environment.TickCount64;

        public string Path { get; }
        public FlydigiApexIdentity Identity { get; }

        /// <summary>The force-trigger engine of an Apex 4 or 5. Null for
        /// an Apex 6 Pro, whose effects are rendered.</summary>
        public FlydigiTriggerEngine Triggers { get; }

        private readonly IHidReportChannel _out, _in;
        private readonly object _io = new();
        private readonly byte[] _read;
        private volatile bool _disposed, _failed;
        private int _writeFailures;
        private Thread _thread;

        private FlydigiApexSession(string path, FlydigiApexIdentity identity, IHidReportChannel output, IHidReportChannel input)
        {
            Path = path;
            Identity = identity;
            _out = output;
            _in = input;
            _read = new byte[Math.Max(64, input.InputReportLength)];
            _policy = identity.Model == FlydigiApexModel.Apex4 ? Policy.ForApex4
                : identity.Wired ? Policy.ForApex5Wired : Policy.ForApex5Receiver;
            if (identity.HasForceTriggers)
                Triggers = new FlydigiTriggerEngine(identity.Model == FlydigiApexModel.Apex5, QueueTrigger);
        }

        /// <summary>A write or an exchange failed ten times running. The
        /// service lets the session go and opens another when the pad is
        /// back.</summary>
        public bool Failed => _failed;

        // ── Open and identity ────────────────────────────────────────

        /// <summary>Opens the pad at <paramref name="path"/> and asks what
        /// it is. Null when the collection does not open, the pad does not
        /// answer, or the answer names a pad this code does not drive. Up
        /// to about five seconds, so the service calls it on its own
        /// thread.
        ///
        /// <para>The attempts are the reference's (Apex5Device.cpp
        /// verifyIdentity): an Apex 5 is asked three times and given 600
        /// ms each, since another program's reader can take the first
        /// answer. An Apex 4 is asked three times, 1.5 s apart, and given
        /// 100 ms each. An Apex 6 is asked once.</para></summary>
        public static FlydigiApexSession Open(string path, FlydigiApexModel candidate, Func<bool> cancel = null)
        {
            if (string.IsNullOrEmpty(path) || candidate == FlydigiApexModel.None) return null;
            IHidReportChannel output = null, input = null;
            try
            {
                output = Opener(path);
                input = output == null ? null : Opener(path);
                if (output == null || input == null || output.FeatureOnly || input.FeatureOnly
                    || input.InputReportLength == 0 || output.OutputReportLength == 0)
                {
                    output?.Dispose();
                    input?.Dispose();
                    return null;
                }

                byte[] request = candidate switch
                {
                    FlydigiApexModel.Apex4 => Proto.Apex4IdentityRequest(),
                    FlydigiApexModel.Apex5 => Proto.Apex5IdentityRequest(),
                    _ => Proto.Apex6IdentityRequest(),
                };
                int attempts = candidate == FlydigiApexModel.Apex6Pro ? 1 : 3;
                int waitMs = candidate == FlydigiApexModel.Apex4 ? 100 : 600;
                var buffer = new byte[Math.Max(64, input.InputReportLength)];
                FlydigiApexIdentity identity = null;
                for (int attempt = 0; attempt < attempts && identity == null; attempt++)
                {
                    if (cancel != null && cancel()) break;
                    if (attempt > 0 && candidate == FlydigiApexModel.Apex4)
                        for (int waited = 0; waited < 1500 && !(cancel != null && cancel()); waited += 50) Thread.Sleep(50);
                    Drain(input, buffer);
                    if (!output.WriteOutput(request)) continue;
                    long deadline = Clock() + waitMs;
                    while (identity == null)
                    {
                        long left = deadline - Clock();
                        if (left <= 0) break;
                        int got = input.ReadInput(buffer, (int)left);
                        if (got < 0) break;
                        if (got == 0) continue;
                        identity = Proto.ParseIdentity(candidate, buffer.AsSpan(0, got));
                    }
                }
                // An Apex 6 Pro without both voice-coil bits takes no stream.
                if (identity != null && identity.Model == FlydigiApexModel.Apex6Pro && !identity.HasVoiceCoils)
                    identity = null;
                if (identity == null)
                {
                    output.Dispose();
                    input.Dispose();
                    return null;
                }
                var session = new FlydigiApexSession(path, identity, output, input);
                session.Start();
                return session;
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine("FLYDIGI open failed: " + ex.GetType().Name);
                try { output?.Dispose(); } catch { }
                try { input?.Dispose(); } catch { }
                return null;
            }
        }

        private void Start()
        {
            _thread = new Thread(Identity.Model == FlydigiApexModel.Apex6Pro ? StreamLoop : WriterLoop)
            {
                IsBackground = true,
                Name = "FlydigiApex",
                Priority = Identity.Model == FlydigiApexModel.Apex6Pro ? ThreadPriority.AboveNormal : ThreadPriority.Normal,
            };
            _thread.Start();
        }

        private static void Drain(IHidReportChannel input, byte[] buffer)
        {
            // Bounded, as the reference bounds it: a pad that streams
            // motion would otherwise never run dry.
            for (int i = 0; i < 128; i++)
                if (input.ReadInput(buffer, 0) <= 0) break;
        }

        // ── Exchanges, for the lighting family ───────────────────────

        /// <summary>Runs <paramref name="body"/> with the pad to itself:
        /// nothing else is written until it returns. False when the
        /// session is closed.</summary>
        public bool Exclusive(Func<FlydigiApexSession, bool> body)
        {
            if (_disposed || _failed) return false;
            lock (_io)
            {
                if (_disposed) return false;
                return body(this);
            }
        }

        /// <summary>Writes a report. Only inside <see cref="Exclusive"/>.
        /// The writer keeps its own writes as far from this one as from
        /// each other (Apex5Device.cpp recordVendorWrite).</summary>
        public bool Write(byte[] report)
        {
            bool ok = WriteLocked(report);
            lock (_queue)
            {
                _lastWriteAt = Clock();
                _lastWasRumble = false;
                _wroteAnything = true;
            }
            return ok;
        }

        /// <summary>Inside <see cref="Exclusive"/>, ahead of an exchange's
        /// first write: waits until the pad's last write is as old as its
        /// writes are kept apart, 10 ms for an Apex 5 on its receiver and
        /// 25 ms for an Apex 4 (Apex5Device.cpp waitForAsyncWriteSpacing,
        /// Apex4WritePacing.h).</summary>
        public void Spaced()
        {
            if (Identity.Model == FlydigiApexModel.Apex6Pro) return;
            long spacing = _policy.Apex4 ? Apex4TriggerSpacingMs : _policy.SpacingMs;
            if (spacing <= 0) return;
            long ready;
            lock (_queue) ready = _wroteAnything ? _lastWriteAt + spacing : 0;
            long left = ready - Clock();
            if (left > 0) Thread.Sleep((int)Math.Min(left, spacing));
        }

        /// <summary>Writes <paramref name="request"/> and waits up to
        /// <paramref name="timeoutMs"/> for the report
        /// <paramref name="isReply"/> accepts, which it also reads. Input
        /// that arrived before the request is discarded first. Only
        /// inside <see cref="Exclusive"/> (Apex5Device.cpp
        /// exchangeCommand).</summary>
        public bool Request(byte[] request, int timeoutMs, Func<byte[], int, bool> isReply)
        {
            Drain(_in, _read);
            if (!Write(request)) return false;
            return Await(timeoutMs, isReply);
        }

        /// <summary>How long two of an Apex 6 Pro's RAM exchanges are kept
        /// apart: the pad gives no answer to a request that follows the one
        /// before too closely (Apex5Device.cpp apex6RamExchange).</summary>
        internal const int RamSpacingMs = 220;

        /// <summary>How long a command's reply is waited for
        /// (exchangeCommand kReplyTimeout).</summary>
        internal const int ReplyMs = 750;

        private long _ramExchangedAt;

        /// <summary>One exchange with an Apex 6 Pro's RAM: the request,
        /// and the 28 bytes of the reply whose command and mode match, or
        /// null. The pad is held for this one exchange and no longer, so
        /// the stream's reports go out between two of them, and
        /// <paramref name="wait"/> sits out the spacing with the pad
        /// free.</summary>
        public byte[] RamExchange(byte[] request, byte command, byte mode, Action<int> wait)
        {
            long last = Volatile.Read(ref _ramExchangedAt);
            if (last != 0)
            {
                long since = Clock() - last;
                if (since < RamSpacingMs) (wait ?? Thread.Sleep)((int)(RamSpacingMs - since));
            }
            byte[] payload = null;
            bool answered = Exclusive(pad => pad.Request(request, ReplyMs, (report, length) =>
            {
                var reply = Proto.Apex6RamReply(report.AsSpan(0, length), command, mode);
                if (reply.IsEmpty) return false;
                payload = reply.ToArray();
                return true;
            }));
            Volatile.Write(ref _ramExchangedAt, Clock());
            return answered ? payload : null;
        }

        private int _apex4Slot = -1;

        /// <summary>The profile slot an Apex 4 has active, asked once per
        /// session and before any block is read: a read moves what the pad
        /// answers to this (flydigi-space-station-mac docs/protocol.md
        /// section 10). A pad that gives no usable answer within a second
        /// is taken to be on slot 0, as that program takes it
        /// (ControllerModel.swift refresh).</summary>
        public byte Apex4Slot()
        {
            int known = Volatile.Read(ref _apex4Slot);
            if (known >= 0) return (byte)known;
            int slot = -1;
            Exclusive(pad =>
            {
                pad.Spaced();
                return pad.Request(Proto.Apex4CurrentSlotRequest(), 1000, (report, length) =>
                {
                    int read = Proto.ParseApex4CurrentSlot(report.AsSpan(0, length));
                    if (read < 0) return false;
                    slot = read;
                    return true;
                });
            });
            known = slot is >= 0 and < 4 ? slot : 0;
            Volatile.Write(ref _apex4Slot, known);
            return (byte)known;
        }

        /// <summary>Waits for a report <paramref name="isReply"/>
        /// accepts, with nothing written first.</summary>
        public bool Await(int timeoutMs, Func<byte[], int, bool> isReply)
        {
            long deadline = Clock() + timeoutMs;
            for (int count = 0; count < 4096; count++)
            {
                long left = deadline - Clock();
                if (left <= 0) return false;
                int got = _in.ReadInput(_read, (int)Math.Max(1, left));
                if (got < 0) return false;
                if (got > 0 && isReply(_read, got)) return true;
            }
            return false;
        }

        private bool WriteLocked(byte[] report)
        {
            bool ok;
            try { ok = _out.WriteOutput(report); }
            catch { ok = false; }
            if (ok) _writeFailures = 0;
            else if (++_writeFailures >= 10 && !_failed)
            {
                _failed = true;
                SdlDiagLog.WriteLine("FLYDIGI session failed: ten writes refused in a row");
            }
            return ok;
        }

        private bool WriteNow(byte[] report)
        {
            lock (_io) return !_disposed && WriteLocked(report);
        }

        // ── Apex 4 and 5: the writer ─────────────────────────────────

        private readonly record struct Policy(int SpacingMs, int RumbleHoldoffMs, bool RumbleStopFirst, bool Apex4)
        {
            public static readonly Policy ForApex4 = new(10, 0, true, true);
            public static readonly Policy ForApex5Receiver = new(10, 30, false, false);
            public static readonly Policy ForApex5Wired = new(0, 0, false, false);
        }

        private readonly Policy _policy;
        private readonly object _queue = new();
        private FlydigiTriggerCommand? _wantLeft, _wantRight;
        private (byte Low, byte High)? _wantRumble;
        private long _rumblePendingSince;
        private int _nextSlot;
        private long _lastWriteAt, _lastTriggerWriteAt;
        private bool _lastWasRumble, _wroteAnything;
        private (byte Low, byte High) _lastRumble;

        // Apex 4 only: what the pad was last sent, when, and the repeats owed.
        private const long Apex4TriggerSpacingMs = 25, Apex4RumbleSpacingMs = 10;
        private const long Apex4ModeGapMs = 100, Apex4ParamIntervalMs = 500, Apex4ParamFastMs = 150;
        private static readonly long[] Apex4RepeatAfterMs = { 100, 500 };
        private const long RumbleMaximumWaitMs = 100;
        private readonly FlydigiTriggerCommand?[] _applied = new FlydigiTriggerCommand?[2];
        private readonly long[] _changedAt = new long[2];
        private readonly List<long>[] _repeats = { new List<long>(2), new List<long>(2) };
        private long _lastChangeAt;

        private void QueueTrigger(FlydigiTriggerCommand command)
        {
            lock (_queue)
            {
                if (command.Side == FlydigiTriggerSide.Left) _wantLeft = command;
                else _wantRight = command;
                Monitor.PulseAll(_queue);
            }
        }

        /// <summary>The grips' rumble, each 0 to 255. True when this
        /// session writes it: always on an Apex 4 or 5, and on an Apex 6
        /// Pro while its stream runs, where the pad's own rumble is
        /// off.</summary>
        public bool SetRumble(byte low, byte high)
        {
            if (_disposed || _failed) return false;
            if (Identity.Model == FlydigiApexModel.Apex6Pro)
            {
                lock (_stream)
                {
                    if (!_streaming) return false;
                    _rumbleLow = low;
                    _rumbleHigh = high;
                    if ((low | high) != 0) WakeStream();
                    return true;
                }
            }
            lock (_queue)
            {
                if (_wantRumble == null)
                {
                    if (_wroteAnything && _lastRumble == (low, high)) return true;
                    _rumblePendingSince = Clock();
                }
                _wantRumble = (low, high);
                Monitor.PulseAll(_queue);
            }
            return true;
        }

        /// <summary>The triggers' positions, 0 to 255, as the pad reports
        /// them.</summary>
        public void SetPositions(byte left, byte right)
        {
            if (Triggers != null)
            {
                Triggers.UpdatePositions(left, right);
                return;
            }
            lock (_stream)
            {
                _positionLeft = left;
                _positionRight = right;
            }
        }

        /// <summary>Scales the trigger effects' force, 0 to 100.</summary>
        public void SetStrength(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            if (Triggers != null) Triggers.StrengthPercent = percent;
            else lock (_stream) _triggerStrength = percent;
        }

        /// <summary>The 11-byte DualSense effect one trigger should show.
        /// <paramref name="ours"/> says it is PadForge's own setting, so
        /// its type 0 releases the trigger.</summary>
        public void SetTriggerBlock(FlydigiTriggerSide side, ReadOnlySpan<byte> block, bool ours)
        {
            if (_disposed || _failed || block.Length < 11) return;
            long now = Clock();
            if (Triggers != null)
            {
                Triggers.Set(side, block, ours, now);
                // A rhythm whose first command equals the one before sends
                // nothing, and the writer still has to start stepping it.
                if (Triggers.RhythmArmed)
                    lock (_queue) Monitor.PulseAll(_queue);
                return;
            }
            SetStreamTrigger(side, block, now);
        }

        /// <summary>Releases both triggers and stops the rumble.</summary>
        public void Quiet()
        {
            if (_disposed) return;
            if (Triggers != null)
            {
                Triggers.Clear(FlydigiTriggerSide.Left);
                Triggers.Clear(FlydigiTriggerSide.Right);
                SetRumble(0, 0);
                return;
            }
            lock (_stream)
            {
                _leftEffect = Apex6TriggerEffect.None;
                _rightEffect = Apex6TriggerEffect.None;
                _pendingClear[0] = _pendingClear[1] = false;
                _rumbleLow = _rumbleHigh = 0;
                _pcm.Clear();
            }
        }

        private void WriterLoop()
        {
            try
            {
                while (!_disposed && !_failed)
                {
                    byte[] report = null;
                    bool isRumble = false;
                    int side = -1;
                    bool isRepeat = false;
                    FlydigiTriggerCommand? command = null;
                    lock (_queue)
                    {
                        long now = Clock();
                        long wait = Pick(now, out report, out isRumble, out side, out isRepeat, out command);
                        if (report == null)
                        {
                            // Nothing is due. A rhythm is stepped every 2 ms.
                            bool rhythm = Triggers != null && Triggers.RhythmArmed;
                            if (rhythm) Monitor.Wait(_queue, wait < 0 ? 2 : (int)Math.Clamp(wait, 1, 2));
                            else if (wait < 0) Monitor.Wait(_queue);
                            else Monitor.Wait(_queue, (int)Math.Max(1, wait));
                        }
                    }
                    if (_disposed) break;
                    if (report == null)
                    {
                        if (Triggers != null && Triggers.RhythmArmed) Triggers.StepRhythm(Clock());
                        continue;
                    }
                    bool ok = WriteNow(report);
                    long wrote = Clock();
                    lock (_queue)
                    {
                        _lastWriteAt = wrote;
                        _lastWasRumble = isRumble;
                        _wroteAnything = true;
                        if (!isRumble) _lastTriggerWriteAt = wrote;
                        if (!ok)
                        {
                            // Owed again, unless a newer value came meanwhile.
                            if (isRumble)
                            {
                                if (_wantRumble == null)
                                {
                                    _wantRumble = _lastRumble;
                                    _rumblePendingSince = wrote;
                                }
                            }
                            else if (!isRepeat && side >= 0)
                            {
                                _applied[side] = null;
                                if (side == 0) _wantLeft ??= command; else _wantRight ??= command;
                            }
                        }
                    }
                    if (Triggers != null && Triggers.RhythmArmed) Triggers.StepRhythm(Clock());
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                SdlDiagLog.WriteLine("FLYDIGI writer stopped: " + ex.GetType().Name);
            }
        }

        /// <summary>What to write now. A report, or null with the wait
        /// until something can go out, -1 for no bound. Called under
        /// <see cref="_queue"/>.</summary>
        private long Pick(long now, out byte[] report, out bool isRumble, out int side, out bool isRepeat,
            out FlydigiTriggerCommand? command)
        {
            report = null;
            isRumble = false;
            side = -1;
            isRepeat = false;
            command = null;
            long wait = -1;
            void Shorten(long ms) { if (ms >= 0 && (wait < 0 || ms < wait)) wait = ms; }

            bool anyTrigger = _wantLeft.HasValue || _wantRight.HasValue;
            bool rumbleStop = _policy.RumbleStopFirst && _wantRumble.HasValue
                && _wantRumble.Value.Low == 0 && _wantRumble.Value.High == 0;
            bool rumbleFirst = rumbleStop || !anyTrigger || now - _rumblePendingSince > RumbleMaximumWaitMs;
            long holdoffEnds = Math.Min(_lastTriggerWriteAt + _policy.RumbleHoldoffMs, _rumblePendingSince + RumbleMaximumWaitMs);
            bool rumbleHeld = _wantRumble.HasValue && _wroteAnything && now < holdoffEnds;
            if (rumbleHeld) Shorten(holdoffEnds - now);

            long ReadyAt(bool rumble)
            {
                if (!_wroteAnything) return 0;
                long spacing = _policy.SpacingMs;
                if (_policy.Apex4)
                    spacing = rumble && _lastWasRumble ? Apex4RumbleSpacingMs : Apex4TriggerSpacingMs;
                return _lastWriteAt + spacing;
            }

            int first = rumbleStop && !rumbleHeld ? 2 : _nextSlot;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int slot = (first + attempt) % 3;
                if (slot < 2)
                {
                    var want = slot == 0 ? _wantLeft : _wantRight;
                    if (!want.HasValue) continue;
                    if (_policy.Apex4)
                    {
                        long allowedIn = Apex4ChangeAllowedIn(slot, want.Value, now);
                        if (allowedIn > 0) { Shorten(allowedIn); continue; }
                    }
                    long ready = ReadyAt(rumble: false);
                    if (now < ready) { Shorten(ready - now); return wait; }
                    if (slot == 0) _wantLeft = null; else _wantRight = null;
                    report = _policy.Apex4 ? Proto.Apex4Trigger(want.Value) : Proto.Apex5Trigger(want.Value);
                    side = slot;
                    command = want;
                    if (_policy.Apex4)
                    {
                        _applied[slot] = want;
                        _changedAt[slot] = now;
                        _lastChangeAt = now;
                        _repeats[slot].Clear();
                        foreach (long after in Apex4RepeatAfterMs) _repeats[slot].Add(now + after);
                    }
                    _nextSlot = (slot + 1) % 3;
                    return 0;
                }
                if (!_wantRumble.HasValue || !rumbleFirst || rumbleHeld) continue;
                {
                    long ready = ReadyAt(rumble: true);
                    if (now < ready) { Shorten(ready - now); return wait; }
                    var rumble = _wantRumble.Value;
                    _wantRumble = null;
                    _lastRumble = rumble;
                    report = _policy.Apex4 ? Proto.Apex4Rumble(rumble.Low, rumble.High) : Proto.Apex5Rumble(rumble.Low, rumble.High);
                    isRumble = true;
                    _nextSlot = 0;
                    return 0;
                }
            }

            // An Apex 4 repeat of what a trigger was last sent.
            if (_policy.Apex4)
            {
                for (int slot = 0; slot < 2; slot++)
                {
                    if (_repeats[slot].Count == 0 || !_applied[slot].HasValue) continue;
                    long due = _repeats[slot][0];
                    if (now < due) { Shorten(due - now); continue; }
                    long ready = ReadyAt(rumble: false);
                    if (now < ready) { Shorten(ready - now); continue; }
                    _repeats[slot].RemoveAt(0);
                    report = Proto.Apex4Trigger(_applied[slot].Value);
                    side = slot;
                    isRepeat = true;
                    return 0;
                }
            }
            return wait;
        }

        /// <summary>How long an Apex 4 trigger's change has to wait, 0
        /// for now. A change of mode goes out at once, with no two changes
        /// closer than 100 ms. A change of parameters waits 150 ms after
        /// the trigger's last change when a parameter moved by more than a
        /// quarter, and 500 ms otherwise (flydigi-apex4-linux triggers.py
        /// _change_allowed).</summary>
        private long Apex4ChangeAllowedIn(int slot, FlydigiTriggerCommand wanted, long now)
        {
            long sinceAny = now - _lastChangeAt;
            if (_wroteAnything && _lastChangeAt != 0 && sinceAny < Apex4ModeGapMs) return Apex4ModeGapMs - sinceAny;
            var applied = _applied[slot];
            if (!applied.HasValue || applied.Value.Mode != wanted.Mode) return 0;
            long since = now - _changedAt[slot];
            if (since >= Apex4ParamIntervalMs) return 0;
            bool big = Apex4BigChange(applied.Value, wanted);
            if (big) return since >= Apex4ParamFastMs ? 0 : Apex4ParamFastMs - since;
            return Apex4ParamIntervalMs - since;
        }

        internal static bool Apex4BigChange(FlydigiTriggerCommand a, FlydigiTriggerCommand b)
        {
            static bool Moved(byte x, byte y) => Math.Abs(x - y) > Math.Max(0.25 * Math.Max(x, y), 4);
            return Moved(a.P0, b.P0) || Moved(a.P1, b.P1) || Moved(a.P2, b.P2) || Moved(a.P3, b.P3) || Moved(a.P4, b.P4);
        }

        // ── Apex 6 Pro: the stream ───────────────────────────────────

        private const int FrameMs = 8;
        private const int IdleEnterFrames = 8;
        private const int TriggerReleaseFrames = 4;
        private const long TriggerClearHoldMs = 100;
        private const int EnableDelayMs = 94;
        private const long IdleKeepaliveMs = 50;
        private const long MaximumPcmAgeMs = 24;
        private const int PcmQueueCapacity = 3;
        /// <summary>How long the stream stays quiet before the motors go
        /// back to the pad.</summary>
        internal const long HandBackAfterQuietMs = 3000;

        private readonly object _stream = new();
        private Apex6TriggerEffect _leftEffect = Apex6TriggerEffect.None, _rightEffect = Apex6TriggerEffect.None;
        private readonly Apex6TriggerState _leftState = new(), _rightState = new();
        private readonly bool[] _pendingClear = new bool[2];
        private readonly long[] _pendingClearAt = new long[2];
        private readonly long[] _feedbackSeenAt = { long.MinValue / 2, long.MinValue / 2 };
        private byte _positionLeft, _positionRight;
        private byte _rumbleLow, _rumbleHigh;
        private int _triggerStrength = 100;
        private bool _streaming, _gripPcmWanted, _wake;
        private readonly Queue<(sbyte[] Left, sbyte[] Right, long At)> _pcm = new();

        /// <summary>True while the stream runs, so the grips take samples
        /// and the pad's own rumble is off.</summary>
        public bool Streaming { get { lock (_stream) return _streaming; } }

        /// <summary>Whether the grips are wanted for haptic samples, which
        /// keeps the stream up while it is quiet.</summary>
        public void SetGripPcmWanted(bool wanted)
        {
            if (Identity.Model != FlydigiApexModel.Apex6Pro) return;
            lock (_stream)
            {
                _gripPcmWanted = wanted;
                if (wanted) WakeStream();
            }
        }

        /// <summary>Eight 1 ms samples for each grip, -127 to 127. The
        /// newest three blocks are kept (kWaveformQueueCapacity).</summary>
        public void SubmitGripPcm(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
        {
            if (Identity.Model != FlydigiApexModel.Apex6Pro || _disposed || _failed) return;
            if (left.Length < Proto.Apex6SamplesPerReport || right.Length < Proto.Apex6SamplesPerReport) return;
            var l = left.Slice(0, Proto.Apex6SamplesPerReport).ToArray();
            var r = right.Slice(0, Proto.Apex6SamplesPerReport).ToArray();
            lock (_stream)
            {
                if (_pcm.Count == PcmQueueCapacity) _pcm.Dequeue();
                _pcm.Enqueue((l, r, Clock()));
                WakeStream();
            }
        }

        /// <summary>The Apex 6 Pro's side of <see cref="SetTriggerBlock"/>
        /// (Apex6HapticBridge.cpp handleTrigger). A clear waits 100 ms, so
        /// a game that sets and clears an effect sixteen times a second
        /// does not cut a burst. A vibration within half a second of a
        /// resistance on the same trigger is that resistance's click.</summary>
        private void SetStreamTrigger(FlydigiTriggerSide side, ReadOnlySpan<byte> block, long now)
        {
            var decoded = Apex6TriggerRenderer.Decode(side, block);
            var next = decoded ?? Apex6TriggerEffect.None;
            int index = side == FlydigiTriggerSide.Left ? 0 : 1;
            lock (_stream)
            {
                if (decoded != null && next.Kind == Apex6TriggerKind.Feedback) _feedbackSeenAt[index] = now;
                else if (decoded != null && next.Kind == Apex6TriggerKind.Vibration
                    && now - _feedbackSeenAt[index] < Apex6TriggerRenderer.DetentFeedbackWindowMs)
                    next.Detent = true;

                var target = index == 0 ? _leftEffect : _rightEffect;
                if (decoded != null && next.Kind == Apex6TriggerKind.Off && target.Kind != Apex6TriggerKind.Off)
                {
                    if (!_pendingClear[index])
                    {
                        _pendingClear[index] = true;
                        _pendingClearAt[index] = now + TriggerClearHoldMs;
                    }
                    return;
                }
                _pendingClear[index] = false;
                if (next.SameAs(target)) return;
                if (index == 0) _leftEffect = next; else _rightEffect = next;
                var state = index == 0 ? _leftState : _rightState;
                state.Retarget();
                state.DetentPending = next.Detent;
                if (next.Kind != Apex6TriggerKind.Off) WakeStream();
            }
        }

        /// <summary>Under <see cref="_stream"/>: ends the stream thread's
        /// quiet wait, and is remembered when the thread is not waiting
        /// yet (wakeLocked).</summary>
        private void WakeStream()
        {
            _wake = true;
            Monitor.PulseAll(_stream);
        }

        private bool StreamHasWork()
            => _leftEffect.Kind != Apex6TriggerKind.Off || _rightEffect.Kind != Apex6TriggerKind.Off
            || _pendingClear[0] || _pendingClear[1] || _gripPcmWanted || _pcm.Count > 0
            || (_streaming && (_rumbleLow | _rumbleHigh) != 0);

        private void StreamLoop()
        {
            IntPtr timer = CreateHighResTimer();
            var mixer = new Apex6TriggerMixer();
            var left = new sbyte[Proto.Apex6SamplesPerReport];
            var right = new sbyte[Proto.Apex6SamplesPerReport];
            var trigger = new sbyte[Proto.Apex6SamplesPerReport];
            var gripLeft = new sbyte[Proto.Apex6SamplesPerReport];
            var gripRight = new sbyte[Proto.Apex6SamplesPerReport];
            double gripPhaseLeft = 0, gripPhaseRight = 0;
            long sampleOffset = 0;
            var target = Proto.Apex6Route.Both;
            int silentStreak = 0, releaseFrames = 0;
            long quietSince = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double deadline = 0;
            try
            {
                while (!_disposed && !_failed)
                {
                    lock (_stream)
                    {
                        if (!_streaming)
                        {
                            while (!_disposed && !StreamHasWork()) Monitor.Wait(_stream);
                            if (_disposed) break;
                        }
                    }
                    if (!Streaming)
                    {
                        // Both triggers and both grips onto the stream, then
                        // the pause the reference gives the pad (start).
                        if (!WriteNow(Proto.Apex6TriggerStream(Proto.Apex6Route.Both))
                            || !WriteNow(Proto.Apex6GripStream(true)))
                        {
                            Thread.Sleep(250);
                            continue;
                        }
                        Thread.Sleep(EnableDelayMs);
                        lock (_stream) _streaming = true;
                        target = Proto.Apex6Route.Both;
                        mixer.Reset();
                        silentStreak = 0;
                        releaseFrames = 0;
                        quietSince = 0;
                        deadline = clock.Elapsed.TotalMilliseconds;
                        SdlDiagLog.WriteLine("FLYDIGI apex6 stream on");
                    }

                    deadline += FrameMs;
                    long now = Clock();
                    bool pcmActive = false, work;
                    byte rumbleLow, rumbleHigh;
                    int strength;
                    lock (_stream)
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            if (!_pendingClear[i] || now < _pendingClearAt[i]) continue;
                            _pendingClear[i] = false;
                            if (i == 0) _leftEffect = Apex6TriggerEffect.None; else _rightEffect = Apex6TriggerEffect.None;
                            (i == 0 ? _leftState : _rightState).Retarget();
                        }
                        Apex6TriggerRenderer.Render(_leftEffect, _positionLeft, _leftState, sampleOffset, left);
                        Apex6TriggerRenderer.Render(_rightEffect, _positionRight, _rightState, sampleOffset, right);
                        sampleOffset += Proto.Apex6SamplesPerReport;
                        while (_pcm.Count > 0 && now - _pcm.Peek().At > MaximumPcmAgeMs) _pcm.Dequeue();
                        if (_pcm.Count > 0)
                        {
                            var block = _pcm.Dequeue();
                            block.Left.CopyTo(gripLeft, 0);
                            block.Right.CopyTo(gripRight, 0);
                            pcmActive = true;
                        }
                        rumbleLow = _rumbleLow;
                        rumbleHigh = _rumbleHigh;
                        strength = _triggerStrength;
                        work = StreamHasWork();
                        _wake = false;
                    }

                    if (strength < 100)
                        for (int i = 0; i < left.Length; i++)
                        {
                            left[i] = (sbyte)(left[i] * strength / 100);
                            right[i] = (sbyte)(right[i] * strength / 100);
                        }
                    // A grip with no samples of its own plays the rumble as
                    // a carrier, 65 Hz for the low motor and 150 Hz for the
                    // high one (renderBlock).
                    bool leftPcm = pcmActive && AnyNonZero(gripLeft), rightPcm = pcmActive && AnyNonZero(gripRight);
                    for (int i = 0; i < gripLeft.Length; i++)
                    {
                        if (!leftPcm) gripLeft[i] = rumbleLow != 0 ? Carrier(ref gripPhaseLeft, 65.0, rumbleLow) : (sbyte)0;
                        if (!rightPcm) gripRight[i] = rumbleHigh != 0 ? Carrier(ref gripPhaseRight, 150.0, rumbleHigh) : (sbyte)0;
                    }

                    var route = mixer.Mix(left, right, trigger);
                    bool triggerOn = route != Proto.Apex6Route.Mute;
                    if (triggerOn && route != target)
                    {
                        // The pad is told which trigger the column is for.
                        bool ok = route == Proto.Apex6Route.Both
                            ? WriteNow(Proto.Apex6TriggerStream(Proto.Apex6Route.Both))
                            : WriteNow(Proto.Apex6TriggerStream(route))
                              && WriteNow(Proto.Apex6TriggerOff(route == Proto.Apex6Route.Left ? Proto.Apex6Route.Right : Proto.Apex6Route.Left));
                        if (ok) target = route;
                    }
                    if (triggerOn) releaseFrames = TriggerReleaseFrames;
                    else if (releaseFrames > 0)
                    {
                        releaseFrames--;
                        triggerOn = true;
                        route = Proto.Apex6Route.Both;
                    }

                    bool silent = !triggerOn && !AnyNonZero(gripLeft) && !AnyNonZero(gripRight);
                    silentStreak = silent ? silentStreak + 1 : 0;
                    if (!silent || work) quietSince = 0;
                    else if (quietSince == 0) quietSince = now;

                    if (silent && !work && quietSince != 0 && now - quietSince >= HandBackAfterQuietMs)
                    {
                        HandMotorsBack();
                        continue;
                    }
                    if (silentStreak >= IdleEnterFrames && silent)
                    {
                        // Quiet: one neutral report every 50 ms keeps the
                        // stream, and anything new ends the wait at once.
                        bool woken;
                        lock (_stream)
                        {
                            if (!_wake) Monitor.Wait(_stream, (int)IdleKeepaliveMs);
                            woken = _wake;
                        }
                        if (_disposed) break;
                        deadline = clock.Elapsed.TotalMilliseconds;
                        if (woken) { silentStreak = 0; continue; }
                        WriteNow(Proto.Apex6Stream(trigger, gripLeft, gripRight, Proto.Apex6Route.Mute, false, true, true));
                        continue;
                    }

                    WriteNow(Proto.Apex6Stream(trigger, gripLeft, gripRight, route, triggerOn, true, true));
                    double waitMs = deadline - clock.Elapsed.TotalMilliseconds;
                    if (waitMs > 0) HighResWait(timer, waitMs);
                    else if (waitMs < -FrameMs) deadline = clock.Elapsed.TotalMilliseconds;
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                SdlDiagLog.WriteLine("FLYDIGI stream stopped: " + ex.GetType().Name);
            }
            finally
            {
                if (timer != IntPtr.Zero) CloseHandle(timer);
            }
        }

        /// <summary>Three neutral reports, then both triggers and both
        /// grips off the stream (Apex5Device.cpp disableApex6Haptics).</summary>
        private void HandMotorsBack()
        {
            bool was;
            lock (_stream)
            {
                was = _streaming;
                _streaming = false;
                _rumbleLow = _rumbleHigh = 0;
            }
            if (!was) return;
            var neutral = new sbyte[Proto.Apex6SamplesPerReport];
            for (int i = 0; i < 3; i++)
            {
                WriteNow(Proto.Apex6Stream(neutral, neutral, neutral, Proto.Apex6Route.Mute, false, true, true));
                Thread.Sleep(FrameMs);
            }
            WriteNow(Proto.Apex6TriggerOff(Proto.Apex6Route.Both));
            WriteNow(Proto.Apex6GripStream(false));
            SdlDiagLog.WriteLine("FLYDIGI apex6 stream off");
        }

        private static bool AnyNonZero(sbyte[] samples)
        {
            foreach (sbyte s in samples)
                if (s != 0) return true;
            return false;
        }

        /// <summary>One sample of a rumble carrier: a level of 255 is a
        /// sine of 112 (Apex6HapticBridge.cpp sineSample).</summary>
        private static sbyte Carrier(ref double phase, double hertz, byte level)
        {
            double strength = level / 255.0 * 112.0;
            int value = (int)Math.Round(Math.Sin(phase * 2.0 * Math.PI) * strength, MidpointRounding.AwayFromZero);
            phase += hertz / 1000.0;
            phase -= Math.Floor(phase);
            return (sbyte)Math.Clamp(value, -112, 112);
        }

        // ── Close ────────────────────────────────────────────────────

        /// <summary>What a crash or a stop sends without the session's
        /// thread: both triggers released, the rumble stopped, and an Apex
        /// 6 Pro's motors handed back. Each write is bounded by the
        /// channel's own timeout.</summary>
        public void QuietNow()
        {
            if (_disposed) return;
            try
            {
                switch (Identity.Model)
                {
                    case FlydigiApexModel.Apex5:
                        WriteNow(Proto.Apex5Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Left)));
                        WriteNow(Proto.Apex5Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Right)));
                        WriteNow(Proto.Apex5Rumble(0, 0));
                        break;
                    case FlydigiApexModel.Apex4:
                        // Twice, 25 ms apart: the pad acknowledges nothing.
                        for (int pass = 0; pass < 2; pass++)
                        {
                            WriteNow(Proto.Apex4Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Left)));
                            Thread.Sleep((int)Apex4TriggerSpacingMs);
                            WriteNow(Proto.Apex4Trigger(FlydigiTriggerCommand.Off(FlydigiTriggerSide.Right)));
                            Thread.Sleep((int)Apex4TriggerSpacingMs);
                        }
                        WriteNow(Proto.Apex4Rumble(0, 0));
                        break;
                    case FlydigiApexModel.Apex6Pro:
                        HandMotorsBack();
                        break;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Quiet();
            QuietNow();
            _disposed = true;
            lock (_queue) Monitor.PulseAll(_queue);
            lock (_stream) Monitor.PulseAll(_stream);
            try { _thread?.Join(500); } catch { }
            lock (_io)
            {
                try { _out.Dispose(); } catch { }
                try { _in.Dispose(); } catch { }
            }
        }

        // ── A timer finer than the scheduler's tick ──────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr argument, bool resume);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr CreateHighResTimer()
        {
            const uint highResolution = 0x2, access = 0x0002 | 0x00100000;
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, highResolution, access);
            return timer != IntPtr.Zero ? timer : CreateWaitableTimerExW(IntPtr.Zero, null, 0, access);
        }

        private static void HighResWait(IntPtr timer, double ms)
        {
            if (timer == IntPtr.Zero) { Thread.Sleep((int)Math.Max(1, ms)); return; }
            long due = -(long)(ms * 10000.0);
            if (due == 0) due = -1;
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(timer, 100);
            else Thread.Sleep((int)Math.Max(1, ms));
        }
    }
}
