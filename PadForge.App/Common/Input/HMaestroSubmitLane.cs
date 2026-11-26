using System;
using System.Collections.Generic;
using System.Threading;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>Where a virtual controller's frames go: HIDMaestro's
    /// controller in the app, a recorder in the tests.</summary>
    internal interface IHMaestroSubmitTarget
    {
        void SubmitState(in HMGamepadState state);
        void SubmitRawReport(ReadOnlySpan<byte> report);
        void SubmitRawExtendedReport(ReadOnlySpan<byte> report);
    }

    /// <summary>The app's target: one HIDMaestro controller.</summary>
    internal sealed class HMControllerSubmitTarget : IHMaestroSubmitTarget
    {
        private readonly HMController _controller;

        public HMControllerSubmitTarget(HMController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        public void SubmitState(in HMGamepadState state) => _controller.SubmitState(in state);
        public void SubmitRawReport(ReadOnlySpan<byte> report) => _controller.SubmitRawReport(report);
        public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) => _controller.SubmitRawExtendedReport(report);
    }

    /// <summary>
    /// The target a virtual controller's connect made, behind a latch for
    /// the frame HIDMaestro refuses with NotSupportedException: one longer
    /// than the driver serving the controller carries whole, 80 bytes on a
    /// UMDF2 driver older than 2.4.0 (SharedMemoryIO.WriteInputFrame,
    /// ThrowExtendedTooLong), refused before the section is touched. The
    /// next frame of that controller is refused the same way, so the first
    /// refusal is thrown once, with the preset named, and the submits stop
    /// until the controller is made again.
    /// </summary>
    internal sealed class HMaestroRefusalLatch : IHMaestroSubmitTarget
    {
        private readonly IHMaestroSubmitTarget _inner;
        private readonly string _profileId;
        private volatile bool _refused;

        public HMaestroRefusalLatch(IHMaestroSubmitTarget inner, string profileId)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _profileId = profileId;
        }

        /// <summary>True once a frame was refused.</summary>
        public bool Refused => _refused;

        public void SubmitState(in HMGamepadState state)
        {
            if (_refused) return;
            try { _inner.SubmitState(in state); }
            catch (NotSupportedException ex) { throw Refuse(ex); }
        }

        public void SubmitRawReport(ReadOnlySpan<byte> report)
        {
            if (_refused) return;
            try { _inner.SubmitRawReport(report); }
            catch (NotSupportedException ex) { throw Refuse(ex); }
        }

        public void SubmitRawExtendedReport(ReadOnlySpan<byte> report)
        {
            if (_refused) return;
            try { _inner.SubmitRawExtendedReport(report); }
            catch (NotSupportedException ex) { throw Refuse(ex); }
        }

        private NotSupportedException Refuse(NotSupportedException ex)
        {
            _refused = true;
            return new NotSupportedException(
                $"HIDMaestro refused a frame of preset '{_profileId}', and nothing more is sent to this controller until it is made again. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sends a virtual controller's frames on a thread of its own, for a
    /// profile whose HIDMaestro submit can wait (<see cref="SubmitCanWait"/>),
    /// and for a controller HIDMaestro delivers every report of
    /// (HMController.DeliversEveryReport), whose submit waits up to 200 ms
    /// for the driver to deliver the frame before and then holds frames a
    /// millisecond apart (HMController.AwaitPreviousDeliveryCore).
    ///
    /// <para>On a profile with a protocol host, HIDMaestro holds a frame until
    /// the driver has delivered the host's last reply, up to 200 ms
    /// (HIDMaestro eb2f2ce0, HMController.AwaitHostReply into
    /// ProtocolHost.AwaitReplyForFrame and AwaitLocked,
    /// ProtocolHost.DeliveryTimeoutMs). The host's own clock and its reply
    /// pump wait the same way while they hold the controller's submit lock
    /// (ProtocolHost.Tick and PumpLocked), so every submit on such a profile
    /// can wait, SubmitState as much as a raw frame. The wait runs long only
    /// while Windows is not reading the device, as when it starts. The poll
    /// thread must not wait with it, so it hands the frame to this lane and
    /// goes on, as a Keyboard + Mouse slot hands its keys to
    /// KbmHidKeyboard's sender.</para>
    ///
    /// <para>For a protocol host's profile the lane keeps one frame
    /// waiting, the newest, as HIDMaestro's input section keeps one for the
    /// driver: a gamepad's state replaces the state before it. A controller
    /// HIDMaestro delivers every report of gets every report to HID class in
    /// order instead of the latest one (HMController.DeliversEveryReport),
    /// so its lane keeps an ordered queue (<see cref="Ordered"/>): a button
    /// that goes down and up while the lane waits on the frame before still
    /// reaches the device. Either way frames go out in the order they were
    /// handed over, and a frame never goes out after a newer one. A submit
    /// that throws on the lane's thread throws again from the next
    /// hand-over, on the poll thread, which reports it as it reported a
    /// submit that threw there.</para>
    /// </summary>
    internal sealed class HMaestroSubmitLane : IHMaestroSubmitTarget, IDisposable
    {
        /// <summary>True when the profile runs a protocol of its own, which
        /// HIDMaestro hosts in the SDK behind the input section
        /// (HMController's constructor builds a ProtocolHost when the
        /// profile's protocol is set), so a submit can wait for the host's
        /// reply. The protocol group of the profile's device model names
        /// where its protocol came from, and "none" when it has none
        /// (HMProfile.DeviceModelSource, ModelResolver.Resolve). The sinput
        /// and joypad-steam-input personas, the Nintendo personas, the
        /// DualShock 3 and xbox-one-s-full that build their own reports, and
        /// the 2026 Steam Controller are among them.</summary>
        internal static bool SubmitCanWait(HMProfile profile)
        {
            if (profile == null) return false;
            var sources = profile.DeviceModelSource;
            return sources != null
                && sources.TryGetValue("protocol", out var source)
                && !string.IsNullOrEmpty(source)
                && !string.Equals(source, "none", StringComparison.Ordinal);
        }

        private enum Kind : byte { None, State, Raw, RawExtended }

        /// <summary>One frame: its kind and its content, in buffers of its
        /// own. Frames trade places between the thread that hands them over
        /// and the lane's thread, so neither allocates once each byte buffer
        /// has held the longest frame.</summary>
        private sealed class Frame
        {
            public Kind Kind;
            public HMGamepadState State;
            public readonly Dictionary<HMAxis, float> Axes = new();
            public byte[] Bytes = Array.Empty<byte>();
            public int Length;
        }

        private readonly IHMaestroSubmitTarget _target;
        private readonly object _lock = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        // Guarded by _lock: the frame waiting for the lane's thread.
        private Frame _waiting = new();
        // The lane's thread's own: the frame it is sending.
        private Frame _sending = new();
        private volatile bool _stop;
        private Exception _fault;

        /// <summary>True when the lane keeps every frame in order, for a
        /// controller HIDMaestro delivers every report of; false when it
        /// keeps the newest alone.</summary>
        internal bool Ordered { get; }

        /// <summary>True when the ordered lane may let a state that changes
        /// no button, hat or finger take the place of the state at the
        /// queue's end. False for a descriptor with relative input
        /// (HMaestroProfileCatalog.DeclaresRelativeInput), where each report
        /// is a movement and every state is kept.</summary>
        internal bool MergesStates { get; }

        // Guarded by _lock, the ordered lane's: a ring of frames, the
        // oldest at _head, made once with the lane so a hand-over allocates
        // nothing.
        private readonly Frame[] _ring;
        private int _head, _count;

        /// <summary>Frames waiting past this many collapse to the newest of
        /// each kind, as KbmHidKeyboard's do (MaxQueuedFrames): Windows has
        /// stopped reading the device.</summary>
        internal const int MaxQueuedFrames = KbmHidKeyboard.MaxQueuedFrames;

        public HMaestroSubmitLane(IHMaestroSubmitTarget target, string name, bool ordered = false, bool mergeStates = true)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            Ordered = ordered;
            MergesStates = ordered && mergeStates;
            if (ordered)
            {
                _ring = new Frame[MaxQueuedFrames];
                for (int i = 0; i < _ring.Length; i++) _ring[i] = new Frame();
            }
            _thread = new Thread(Run)
            {
                IsBackground = true,
                // The poll thread's priority: the frames are its output.
                Priority = ThreadPriority.AboveNormal,
                Name = "PadForge HIDMaestro submit " + name,
            };
            _thread.Start();
        }

        public void SubmitState(in HMGamepadState state)
        {
            lock (_lock)
            {
                var f = Ordered ? NextForState(in state) : _waiting;
                f.Kind = Kind.State;
                f.State = state;
                f.Axes.Clear();
                if (state.Axes is { } axes)
                {
                    foreach (var axis in axes) f.Axes[axis.Key] = axis.Value;
                    f.State.Axes = f.Axes;
                }
                else f.State.Axes = null;
                Wake();
            }
            Handed();
        }

        public void SubmitRawReport(ReadOnlySpan<byte> report) => HandRaw(report, Kind.Raw);

        public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) => HandRaw(report, Kind.RawExtended);

        private void HandRaw(ReadOnlySpan<byte> report, Kind kind)
        {
            lock (_lock)
            {
                var f = Ordered ? Append() : _waiting;
                if (f.Bytes.Length < report.Length) f.Bytes = new byte[report.Length];
                report.CopyTo(f.Bytes);
                f.Length = report.Length;
                f.State = default;
                f.Kind = kind;
                Wake();
            }
            Handed();
        }

        /// <summary>The ordered lane's place for a state. A state that
        /// changes no button, hat or finger of the state waiting at the
        /// queue's end takes its place: its axes, motion and pressures are
        /// a gamepad's state, the newest of which is the one to send, and the
        /// device reports once a millisecond at most
        /// (HMController.AwaitPreviousDeliveryCore), where a poll past 1000
        /// a second moves a stick on every pass. Every other state is a frame
        /// of its own, and so is every state on a lane that keeps them all
        /// (<see cref="MergesStates"/>). Caller holds _lock.</summary>
        private Frame NextForState(in HMGamepadState state)
        {
            if (MergesStates && _count > 0)
            {
                var tail = _ring[(_head + _count - 1) % _ring.Length];
                if (tail.Kind == Kind.State && SameEdges(in tail.State, in state)) return tail;
            }
            return Append();
        }

        /// <summary>Whether two states hold the same buttons, hat and
        /// fingers: the parts of a state whose every change is an edge the
        /// host must see.</summary>
        private static bool SameEdges(in HMGamepadState a, in HMGamepadState b)
            => a.Buttons == b.Buttons
            && a.Hat == b.Hat
            && a.HatDegrees == b.HatDegrees
            && a.HatHundredths == b.HatHundredths
            && a.HatRaw == b.HatRaw
            && a.TouchpadFinger0Active == b.TouchpadFinger0Active
            && a.TouchpadFinger1Active == b.TouchpadFinger1Active
            && a.TouchpadFinger0Id == b.TouchpadFinger0Id
            && a.TouchpadFinger1Id == b.TouchpadFinger1Id;

        /// <summary>The ordered lane's next free place at the queue's end. A
        /// full queue first collapses to the newest frame of each kind, in
        /// order, as KbmHidKeyboard.Enqueue does. The frames trade places in
        /// the ring, so nothing is allocated. Caller holds _lock.</summary>
        private Frame Append()
        {
            int size = _ring.Length;
            if (_count == size)
            {
                int kept = 0;
                for (int i = 0; i < _count; i++)
                {
                    int at = (_head + i) % size;
                    var kind = _ring[at].Kind;
                    bool newer = false;
                    for (int j = i + 1; j < _count && !newer; j++)
                        newer = _ring[(_head + j) % size].Kind == kind;
                    if (newer) continue;
                    int to = (_head + kept) % size;
                    (_ring[to], _ring[at]) = (_ring[at], _ring[to]);
                    kept++;
                }
                _count = kept;
            }
            var f = _ring[(_head + _count) % size];
            _count++;
            return f;
        }

        /// <summary>The lane's thread: takes the next frame to send, which
        /// trades places with the one it sent last. False when none waits.
        /// Caller holds _lock.</summary>
        private bool TakeNext(out Frame f)
        {
            if (Ordered)
            {
                if (_count == 0) { f = null; return false; }
                f = _ring[_head];
                _ring[_head] = _sending;
                _sending = f;
                _head = (_head + 1) % _ring.Length;
                _count--;
                return true;
            }
            if (_waiting.Kind == Kind.None) { f = null; return false; }
            f = _waiting;
            _waiting = _sending;
            _waiting.Kind = Kind.None;
            _sending = f;
            return true;
        }

        /// <summary>Wakes the lane's thread. The caller holds _lock, under
        /// which <see cref="Dispose"/> closes the event once the lane has
        /// stopped, so a hand-over from a pass that read the lane before the
        /// controller let go of it wakes nothing.</summary>
        private void Wake()
        {
            if (!_stop) _wake.Set();
        }

        /// <summary>Throws what the lane's last submit threw.</summary>
        private void Handed()
        {
            var fault = Interlocked.Exchange(ref _fault, null);
            if (fault != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
        }

        private void Run()
        {
            while (true)
            {
                _wake.WaitOne();
                // Every frame waiting goes out before the next wait: one wake
                // can stand for several frames of the ordered queue.
                while (true)
                {
                    if (_stop) return;
                    Frame f;
                    lock (_lock)
                    {
                        if (!TakeNext(out f)) break;
                    }
                    try
                    {
                        switch (f.Kind)
                        {
                            case Kind.State:
                                _target.SubmitState(in f.State);
                                break;
                            case Kind.Raw:
                                _target.SubmitRawReport(new ReadOnlySpan<byte>(f.Bytes, 0, f.Length));
                                break;
                            case Kind.RawExtended:
                                _target.SubmitRawExtendedReport(new ReadOnlySpan<byte>(f.Bytes, 0, f.Length));
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // A controller disposed under a frame in flight throws
                        // here as the lane stops, and that is no fault.
                        if (!_stop) Volatile.Write(ref _fault, ex);
                    }
                }
            }
        }

        /// <summary>Stops the lane. A frame still waiting is dropped, the
        /// device is going. Waits a bounded time for a frame in flight, which
        /// the controller's own dispose waits for too, since it takes the same
        /// submit lock. The wake event is closed once the lane's thread has
        /// stopped, as KbmHidKeyboard closes its own. A thread still inside a
        /// submit past the wait waits on it once more on its way out, so it
        /// is left to the finalizer then.</summary>
        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            lock (_lock) _wake.Set();
            if (Thread.CurrentThread != _thread && _thread.Join(1000))
                lock (_lock) _wake.Dispose();
        }
    }
}
