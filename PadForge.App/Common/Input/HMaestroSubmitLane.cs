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
    /// Sends a virtual controller's frames on a thread of its own, for a
    /// profile whose HIDMaestro submit can wait (<see cref="SubmitCanWait"/>).
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
    /// <para>The lane keeps one frame waiting, the newest, as HIDMaestro's
    /// input section keeps one for the driver: a gamepad's state replaces
    /// the state before it. Frames go out in the order they were handed
    /// over, and a frame never goes out after a newer one. A submit that
    /// throws on the lane's thread throws again from the next hand-over, on
    /// the poll thread, which reports it as it reported a submit that threw
    /// there.</para>
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
        /// own. Two of them trade places, so neither thread allocates once
        /// the byte buffer has held the longest frame.</summary>
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

        public HMaestroSubmitLane(IHMaestroSubmitTarget target, string name)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
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
                var f = _waiting;
                f.Kind = Kind.State;
                f.State = state;
                f.Axes.Clear();
                if (state.Axes is { } axes)
                {
                    foreach (var axis in axes) f.Axes[axis.Key] = axis.Value;
                    f.State.Axes = f.Axes;
                }
                else f.State.Axes = null;
            }
            Handed();
        }

        public void SubmitRawReport(ReadOnlySpan<byte> report) => HandRaw(report, Kind.Raw);

        public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) => HandRaw(report, Kind.RawExtended);

        private void HandRaw(ReadOnlySpan<byte> report, Kind kind)
        {
            lock (_lock)
            {
                var f = _waiting;
                if (f.Bytes.Length < report.Length) f.Bytes = new byte[report.Length];
                report.CopyTo(f.Bytes);
                f.Length = report.Length;
                f.State = default;
                f.Kind = kind;
            }
            Handed();
        }

        /// <summary>Wakes the lane's thread, and throws what its last submit
        /// threw.</summary>
        private void Handed()
        {
            _wake.Set();
            var fault = Interlocked.Exchange(ref _fault, null);
            if (fault != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
        }

        private void Run()
        {
            while (true)
            {
                _wake.WaitOne();
                if (_stop) return;
                Frame f;
                lock (_lock)
                {
                    if (_waiting.Kind == Kind.None) continue;
                    f = _waiting;
                    _waiting = _sending;
                    _waiting.Kind = Kind.None;
                    _sending = f;
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

        /// <summary>Stops the lane. A frame still waiting is dropped, the
        /// device is going. Waits a bounded time for a frame in flight, which
        /// the controller's own dispose waits for too, since it takes the same
        /// submit lock.</summary>
        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _wake.Set();
            if (Thread.CurrentThread != _thread) _thread.Join(1000);
        }
    }
}
