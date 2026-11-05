using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Rumble on TouchSense mice bound to WinUSB (#494): the Logitech iFeel
    /// Mouse and its siblings (<see cref="TouchSenseProtocol.Mice"/>). One
    /// thread plays every bound mouse. A mouse buzzes at the strongest
    /// level among the rows linked to it, as a HID++ unit does
    /// (<see cref="HidppBackend"/>), and it has one motor, so that level is
    /// already the stronger of the virtual controller's two.
    ///
    /// <para>The device side is hypothesis under test: no TouchSense mouse
    /// is on the bench and none was written to, so the frames rest on the
    /// 2001 capture of the vendor's driver stack (tactile-cvs
    /// tactile/support/logs/ifeel.data,v), Dan Moore's write-up (iFeel
    /// index.html), Joshua Bobruk's Linux driver (tactile-ifeel
    /// iFeel/ifeel.c), the LibUsbDotNet program in
    /// Logitech-iFeel-Vibration-POC and the vendor's 2003 driver
    /// (LMouFrc.dll of MouseWare 9.80, touchsense-vendor-drivers).</para>
    ///
    /// <para>Units. A bound mouse is no HID collection, so it is in no
    /// walk the host hands over: each <see cref="Rescan"/> is the cue to
    /// look for the bound mice again (<see cref="TouchSenseBinding.FindBound"/>),
    /// and a mouse is a unit, keyed by its WinUSB interface path, for as
    /// long as that look finds it.</para>
    ///
    /// <para>Frames (<see cref="TouchSenseProtocol"/>). A level that rises
    /// from silence goes out at once. One that changes while the motor
    /// buzzes goes out once <see cref="TouchSenseProtocol.MinGapMs"/> have
    /// passed since the last frame, with no stop frame between two buzz
    /// frames, as the vendor's driver sends a changed frame (LMouFrc.dll
    /// 0x10008406 to 0x1000852A). One that holds goes out again every
    /// <see cref="TouchSenseProtocol.ResendMs"/>, so the motor never
    /// reaches the end of its count. A level that falls silent, a row
    /// that leaves the link table and a level the engine zeroed at a
    /// silence edge each get the stop frame on the worker's next pass,
    /// which comes within <see cref="ActiveTickMs"/> while anything
    /// plays. Each frame asks for 300 ms of pulses, so a PadForge that is
    /// gone leaves a third of a second of buzz at most.</para>
    ///
    /// <para>One handle. WinUSB lets one handle open a device, and the
    /// mouse's row reads its pointer through it, so the worker opens
    /// nothing: it takes the channel the row opened
    /// (<see cref="TouchSenseChannels"/>), and only when a frame has to
    /// go out, so a mouse nobody rumbles costs no write. A frame that
    /// fails closes that channel, lists the unit asleep and asks the host
    /// for a walk once. A mouse with no channel open when a frame is due
    /// is listed asleep too, with nothing to close and no walk to ask
    /// for. The mouse is tried again every <see cref="ReopenMs"/>, and at
    /// once when a channel opens. It leaves the list only when the look
    /// no longer finds it.</para>
    ///
    /// <para>Stop. The worker's last frames to every mouse it wrote to
    /// are the stop frame and then F3, the vendor driver's own last frame
    /// (LMouFrc.dll 0x10007F0A to 0x10007F31), on the channel open then.
    /// The handle closes after that, with the mouse's row. A mouse whose
    /// last write failed gets nothing: that write closed its channel.</para>
    /// </summary>
    internal sealed class TouchSenseBackend : IDirectUnitBackend
    {
        /// <summary>The worker's cadence while a row linked to a mouse
        /// holds a level above zero, and otherwise, the HID++ worker's
        /// two (HidppBackend.ActiveTickMs). A level that starts wakes the
        /// worker through <see cref="PeripheralOutputs.HapticsChanged"/>.
        /// One that changes or ends raises nothing, so the tick is what
        /// finds it.</summary>
        internal const int ActiveTickMs = 10;
        internal const int IdleTickMs = 100;

        /// <summary>How long a mouse whose write failed waits before it is
        /// tried again, the HID++ worker's wait for a slot that did not
        /// answer (HidppBackend.FirstScanMs). A look that failed is made
        /// again after the same time.</summary>
        internal const int ReopenMs = 5000;

        /// <summary>How long a new worker waits for the one a timed-out
        /// Stop left in a transfer, so the stop frames of the old never
        /// land between the buzz frames of the new.</summary>
        internal const int PredecessorJoinMs = 5000;

        /// <summary>The last worker to come out of that wait.</summary>
        private static Thread s_pastItsWait;

        /// <summary>How long Stop waits for the worker's last frames.</summary>
        internal const int StopJoinMs = 3000;

        /// <summary>One bound mouse as the worker tracks it. Worker thread
        /// only.</summary>
        private sealed class Mouse
        {
            public string Key;
            public ushort VendorId, ProductId;
            public Guid Container;

            /// <summary>The channel the last frame went out on, null until
            /// this run sends the mouse its first frame.</summary>
            public ITouchSenseChannel Channel;
            public bool Asleep;
            public long RetryAt;

            /// <summary>A buzz frame is playing, at this strength, and the
            /// last frame of any kind went out at this tick.</summary>
            public bool Buzzing;
            public byte Strength;
            public long SentAt;
        }

        private readonly TouchSenseChannels _channels;
        private readonly Func<string, ITouchSenseChannel> _channelOf;
        private readonly Func<IReadOnlyList<TouchSenseMouse>> _find;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<long> _ticks;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly bool _keepAlive;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;
        private Thread _thread;
        private volatile WorkerRun _run;

        /// <summary>The run the crash path told to play nothing more.</summary>
        private volatile WorkerRun _muted;
        private int _disposed;

        /// <summary>1 when a walk came in since the last look.</summary>
        private int _lookAsked;

        /// <summary>1 when a channel opened since the last pass, so a
        /// sleeping mouse is tried without waiting out its interval.</summary>
        private int _retryNow;
        private int _passes;
        private int _waitMs;

        /// <summary>1 from before a pass reads the levels until what plays
        /// is recorded, and the mice a buzz frame plays on as of the last
        /// pass, for the crash path's wait (<see cref="WaitForSilence"/>).</summary>
        private int _passing;
        private int _buzzing;

        /// <summary>The newest worker thread of any instance, which the
        /// next one waits for.</summary>
        private static Thread s_lastWorker;

        public TouchSenseBackend()
            : this(TouchSenseChannels.Shared) { }

        /// <summary>Test seam: the table whose openings wake the worker,
        /// the lookup of a mouse's open channel, the look for the bound
        /// mice (null when the look failed), the container lookup, the
        /// clock every interval is measured on, the two ticks, the vendor's
        /// keep-alive, which no build sends by default, and the waits for a
        /// predecessor worker and for this one at Stop.</summary>
        internal TouchSenseBackend(TouchSenseChannels channels, Func<string, ITouchSenseChannel> channelOf = null,
            Func<IReadOnlyList<TouchSenseMouse>> find = null, Func<string, Guid> containerOf = null,
            Func<long> ticks = null, int activeTickMs = ActiveTickMs, int idleTickMs = IdleTickMs,
            bool keepAlive = false, int predecessorJoinMs = PredecessorJoinMs, int stopJoinMs = StopJoinMs)
        {
            _channels = channels;
            _channelOf = channelOf ?? (channels != null ? channels.Find : _ => null);
            _find = find ?? TouchSenseBinding.FindBound;
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _ticks = ticks ?? (() => Environment.TickCount64);
            _activeTickMs = activeTickMs;
            _idleTickMs = idleTickMs;
            _keepAlive = keepAlive;
            _predecessorJoinMs = predecessorJoinMs;
            _stopJoinMs = stopJoinMs;
        }

        public OutputFamily Family => OutputFamily.TouchSenseUnit;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>Passes the worker has started, for a test that waits
        /// on passes and never on time.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        /// <summary>The wait the worker asked for at the end of its last
        /// pass, so the test of its cadence reads the tick it chose.</summary>
        internal int WaitMs => Volatile.Read(ref _waitMs);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>Brings the worker's next pass forward, for a test that
        /// steps its clock.</summary>
        internal void Nudge() => Wake();

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            _run = run;
            _thread = new Thread(() => Worker(run)) { IsBackground = true, Name = "PeripheralTouchSense" };
            PeripheralOutputs.HapticsChanged += Wake;
            if (_channels != null) _channels.Opened += ChannelOpened;
            _thread.Start();
        }

        public void Stop()
        {
            PeripheralOutputs.HapticsChanged -= Wake;
            if (_channels != null) _channels.Opened -= ChannelOpened;
            if (_thread == null) return;
            _run.Stop = true;
            Wake();
            try { _thread.Join(_stopJoinMs); } catch { }
            _thread = null;
            _run = null;
            // A stopped backend lists nothing, also when the join gave up
            // on a worker that is still inside a transfer.
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
        }

        /// <summary>The host walked the present HID collections. A bound
        /// mouse is in none of them, so the list is not kept: the call is
        /// the cue to look for the bound mice again, on the worker.</summary>
        public void Rescan(IReadOnlyList<HidCollectionInfo> present)
        {
            Interlocked.Exchange(ref _lookAsked, 1);
            Wake();
        }

        /// <summary>A mouse's channel opened: a mouse that sleeps is tried
        /// on the worker's next pass.</summary>
        internal void ChannelOpened()
        {
            Interlocked.Exchange(ref _retryNow, 1);
            Wake();
        }

        /// <summary>The crash path's first step. This family claims
        /// nothing, so its reading of "claim nothing more" is to play
        /// nothing more: the run buzzes no mouse from here on, whatever
        /// level arrives, and the stop frame goes to any that still
        /// buzzes.</summary>
        public void SignalStop()
        {
            var run = _run;
            if (run == null) return;
            _muted = run;
            Wake();
        }

        /// <summary>The crash path's second step, which hands lit devices
        /// back through a handle of their own. A buzzing mouse has no
        /// claim to hand back, its stop frame goes out inside
        /// <see cref="WaitForSilence"/>, and WinUSB gives its device no
        /// second handle to write through, so there is nothing to do.</summary>
        public void ReleaseClaimedNow(long deadline) { }

        /// <summary>For an abnormal exit, after the engine zeroed every
        /// level: waits up to <paramref name="waitMs"/> for the worker to
        /// send each buzzing mouse its stop frame. A pass that read its
        /// level before the engine zeroed it still sends the buzz frame,
        /// so the wait holds while a pass is in flight and ends only once
        /// no pass is and no mouse buzzes, the test the GameSense worker's
        /// wait makes (GameSenseBackend.WaitForSilence). A worker that
        /// does not get there leaves the motor to the end of its count,
        /// 300 ms at most.</summary>
        public void WaitForSilence(int waitMs)
        {
            if (!WorkerAlive) return;
            Wake();
            SpinWait.SpinUntil(() => !WorkerAlive
                || (Volatile.Read(ref _passing) == 0 && Volatile.Read(ref _buzzing) == 0), Math.Max(0, waitMs));
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Worker(WorkerRun run)
        {
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL TouchSense previous worker still running after {_predecessorJoinMs} ms, starting anyway");
            Volatile.Write(ref s_pastItsWait, Thread.CurrentThread);
            var mice = new Dictionary<string, Mouse>(StringComparer.OrdinalIgnoreCase);
            try
            {
                bool scanned = false;
                long lookAgainAt = long.MaxValue;
                while (!run.Stop)
                {
                    Interlocked.Increment(ref _passes);
                    long now = _ticks();
                    bool changed = false, ask = false;
                    if (Interlocked.Exchange(ref _lookAsked, 0) != 0 || now >= lookAgainAt)
                    {
                        if (Look(mice, now, ref changed))
                        {
                            // The first look that works is itself news:
                            // the rows stop waiting for this backend.
                            changed |= !scanned;
                            scanned = true;
                            lookAgainAt = long.MaxValue;
                        }
                        else
                        {
                            lookAgainAt = now + ReopenMs;
                        }
                    }

                    Interlocked.Exchange(ref _passing, 1);
                    bool active = Play(run, mice, now, ref changed, ref ask);
                    // A pass that a Stop cut into can end after the next
                    // Start, when the Stop gave up on a frame that took too
                    // long. What the backend lists and asks for from here
                    // on is the next run's, so this one says nothing more.
                    if (run.Stop) break;
                    int buzzing = 0;
                    foreach (var mouse in mice.Values)
                        if (mouse.Buzzing) buzzing++;
                    Volatile.Write(ref _buzzing, buzzing);
                    Volatile.Write(ref _passing, 0);

                    if (changed && scanned) Publish(mice);
                    if (ask)
                    {
                        try { RescanRequested?.Invoke(); } catch { }
                    }

                    int wait = active ? _activeTickMs : _idleTickMs;
                    Volatile.Write(ref _waitMs, wait);
                    // A Dispose whose Stop gave up its join releases the
                    // wake under a worker still in a transfer, which ends here.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL TouchSense worker fault: " + ex.GetType().Name);
            }
            finally
            {
                // A later worker that came out of its wait is playing by now. This
                // worker's last frame asked for 300 ms of pulses, and a stop would land
                // between the new frames. One still waiting waits for these.
                if (ReferenceEquals(Volatile.Read(ref s_pastItsWait), Thread.CurrentThread)) HandBack(mice);
                // A worker a Stop gave up on can end after the next Start,
                // once the new worker's wait for it ran out. What the
                // backend shows then is the new run's to keep.
                var current = _run;
                if (current == null || ReferenceEquals(current, run))
                {
                    Volatile.Write(ref _buzzing, 0);
                    Volatile.Write(ref _passing, 0);
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                }
            }
        }

        /// <summary>Looks for the bound mice. A mouse the look finds for
        /// the first time becomes a unit, one it no longer finds leaves
        /// the list, and one whose container was not known yet is asked
        /// again. False when the look itself failed: what was known
        /// stands, so one failed SetupAPI call never reads as every mouse
        /// unplugged.</summary>
        private bool Look(Dictionary<string, Mouse> mice, long now, ref bool changed)
        {
            IReadOnlyList<TouchSenseMouse> found;
            try { found = _find(); }
            catch { found = null; }
            if (found == null) return false;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bound in found)
            {
                if (string.IsNullOrEmpty(bound.Path) || !present.Add(bound.Path)) continue;
                if (!mice.TryGetValue(bound.Path, out var mouse))
                {
                    mouse = new Mouse
                    {
                        Key = bound.Path, VendorId = bound.VendorId, ProductId = bound.ProductId, SentAt = now,
                    };
                    mice[bound.Path] = mouse;
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"PERIPHERAL TouchSense found {bound.VendorId:X4}:{bound.ProductId:X4} at {bound.Path}");
                }
                if (mouse.Container != Guid.Empty) continue;
                Guid container;
                try { container = _containerOf(bound.Path); }
                catch { container = Guid.Empty; }
                // A failed lookup is not kept: without a container the
                // unit matches no row, so the next look asks again.
                if (container == Guid.Empty) continue;
                mouse.Container = container;
                changed = true;
            }

            List<string> gone = null;
            foreach (var pair in mice)
                if (!present.Contains(pair.Key)) (gone ??= new List<string>()).Add(pair.Key);
            if (gone == null) return true;
            foreach (var key in gone)
            {
                var mouse = mice[key];
                // Unplugged, or its driver went back to Windows: its
                // channel is closed or about to be. A stop goes to one
                // that still takes it.
                if (mouse.Buzzing && mouse.Channel != null && mouse.Channel.IsOpen)
                {
                    try { mouse.Channel.Send(TouchSenseProtocol.Stop()); } catch { }
                }
                mice.Remove(key);
                changed = true;
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL TouchSense gone " + key);
            }
            return true;
        }

        /// <summary>One pass of frames over every mouse. True while a row
        /// linked to a mouse holds a level above zero, which keeps the
        /// worker on its fast tick: a level under the motor's cut plays
        /// nothing yet, and when it climbs past the cut it raises no
        /// <see cref="PeripheralOutputs.HapticsChanged"/>.</summary>
        private bool Play(WorkerRun run, Dictionary<string, Mouse> mice, long now, ref bool changed, ref bool ask)
        {
            var links = PeripheralOutputs.Links;
            bool muted = ReferenceEquals(_muted, run);
            bool retry = Interlocked.Exchange(ref _retryNow, 0) != 0;
            bool active = false;
            foreach (var mouse in mice.Values)
            {
                if (mouse.Asleep)
                {
                    if (!retry && now < mouse.RetryAt) continue;
                    if (ChannelOf(mouse.Key) == null)
                    {
                        // Another mouse's channel opening does not put
                        // this one's own interval off.
                        if (now >= mouse.RetryAt) mouse.RetryAt = now + ReopenMs;
                        continue;
                    }
                    // Listed awake again. The linker links its rows on the
                    // host's next pass, and the level in force then plays.
                    mouse.Asleep = false;
                    mouse.SentAt = now;
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL TouchSense answers again " + mouse.Key);
                }

                var path = new OutputPath(OutputFamily.TouchSenseUnit, mouse.Key);
                bool linked = links.ByPath.TryGetValue(path, out var devices);
                float amplitude = 0f;
                if (linked && !muted)
                {
                    foreach (var device in devices)
                    {
                        var row = links.For(device);
                        if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                        float a = PeripheralOutputs.AmplitudeOf(device);
                        if (a > amplitude) amplitude = a;
                    }
                }
                if (amplitude > 0f) active = true;

                byte strength = TouchSenseProtocol.StrengthFor(amplitude, mouse.ProductId);
                byte[] frame = null;
                bool buzz = false;
                if (strength == 0)
                {
                    // Silence: the stop at once, where a buzz frame would
                    // run out its count. With the keep-alive on, the
                    // vendor's frame after a second without one.
                    if (mouse.Buzzing)
                        frame = TouchSenseProtocol.Stop();
                    else if (_keepAlive && linked && now - mouse.SentAt >= TouchSenseProtocol.KeepAliveMs)
                        frame = TouchSenseProtocol.KeepAlive();
                }
                else if (!mouse.Buzzing
                         || now - mouse.SentAt >= (strength != mouse.Strength
                             ? TouchSenseProtocol.MinGapMs
                             : TouchSenseProtocol.ResendMs))
                {
                    // A stopping worker starts no buzz: its last frames
                    // are the stop and F3.
                    if (run.Stop) break;
                    frame = TouchSenseProtocol.Buzz(strength, TouchSenseProtocol.DelayMs, TouchSenseProtocol.Count);
                    buzz = true;
                }
                if (frame == null) continue;

                // The channel the last frame went out on, or the one that
                // is open now when that one was closed since: the row may
                // have opened the mouse again.
                var channel = mouse.Channel;
                if (channel == null || !channel.IsOpen) channel = mouse.Channel = ChannelOf(mouse.Key);
                bool sent = false;
                if (channel != null)
                {
                    try { sent = channel.Send(frame); }
                    catch { sent = false; }
                }
                if (!sent)
                {
                    // The asleep rule: the unit stays listed and reads
                    // asleep. A frame that failed closes its channel,
                    // which retires the mouse's row, and the host is
                    // asked for a walk, since the mouse may have left.
                    // With no channel open nothing was written: the row
                    // has not opened the mouse yet, or closed it, and the
                    // unit sleeps until one opens.
                    mouse.Channel = null;
                    if (channel != null)
                    {
                        try { channel.Dispose(); } catch { }
                        ask = true;
                    }
                    mouse.Asleep = true;
                    mouse.RetryAt = now + ReopenMs;
                    mouse.Buzzing = false;
                    changed = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(channel != null
                        ? "PERIPHERAL TouchSense write failed, closing " + mouse.Key
                        : "PERIPHERAL TouchSense has no open channel for " + mouse.Key);
                    continue;
                }
                mouse.SentAt = now;
                mouse.Buzzing = buzz;
                if (buzz) mouse.Strength = strength;
            }
            return active;
        }

        private ITouchSenseChannel ChannelOf(string key)
        {
            try
            {
                var channel = _channelOf(key);
                return channel != null && channel.IsOpen ? channel : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The worker's last frames, to every mouse this run
        /// wrote to and whose last write went through: the stop, then F3,
        /// on the channel that is open now, which is a new one when the
        /// row opened the mouse again since. A mouse that takes no stop
        /// gets no F3.</summary>
        private void HandBack(Dictionary<string, Mouse> mice)
        {
            foreach (var mouse in mice.Values)
            {
                var channel = mouse.Channel;
                mouse.Channel = null;
                mouse.Buzzing = false;
                if (channel == null) continue;
                if (!channel.IsOpen) channel = ChannelOf(mouse.Key);
                if (channel == null) continue;
                try
                {
                    if (channel.Send(TouchSenseProtocol.Stop())) channel.Send(TouchSenseProtocol.StopAll());
                }
                catch
                {
                }
            }
        }

        /// <summary>One new snapshot, then the notice. The units are
        /// listed by key, so two snapshots of the same mice are equal.</summary>
        private void Publish(Dictionary<string, Mouse> mice)
        {
            var units = new List<DirectUnit>(mice.Count);
            foreach (var mouse in mice.Values)
            {
                units.Add(new DirectUnit(OutputFamily.TouchSenseUnit, mouse.Key, mouse.Container,
                    mouse.VendorId, mouse.ProductId)
                {
                    Name = TouchSenseProtocol.NameOf(mouse.VendorId, mouse.ProductId),
                    Asleep = mouse.Asleep,
                });
            }
            units.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(units));
            try { SnapshotChanged?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
