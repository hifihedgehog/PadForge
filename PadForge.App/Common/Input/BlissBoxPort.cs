using System;
using System.Threading;
using PadForge.Engine.Common.BlissBox;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One Bliss-Box port's API sidecar (issue #469), beside the SDL row
    /// that reads its joystick: a second, shared handle on the port's HID
    /// collection and the worker that runs every feature report over it.
    /// The Padix converter's shape (#440): SDL for input, PadForge's own
    /// writes for what the joystick cannot carry.
    ///
    /// <para>The worker owns the channel. It opens it, runs the session's
    /// steps until the port is disposed, and reopens it once a second while
    /// it will not open or after three info reads in a row fail, telling both
    /// motors their level again once it is back. When the port is disposed
    /// it stops both motors before it lets go. Other threads set what they
    /// want on the <see cref="Session"/> and wake the worker.</para>
    /// </summary>
    internal sealed class BlissBoxPort : IDisposable
    {
        private const int ReopenIntervalMs = 1000;
        private const int FailedReadsBeforeReopen = 3;
        private const int JoinMs = 3000;

        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly HidTransport _transport = new();
        private volatile bool _stop;

        public BlissBoxPort(string path, ushort productId, Guid instanceGuid)
        {
            Path = path;
            ProductId = productId;
            InstanceGuid = instanceGuid;
            Session = new BlissBoxSession(_transport, BlissBoxProtocol.PlayerOf(productId));
            Session.InfoChanged += _ => Changed?.Invoke(this);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Bliss-Box port " + Session.Player,
            };
        }

        /// <summary>Starts the worker, once the owner has subscribed to
        /// <see cref="Changed"/>, so the first open is never missed.</summary>
        public void Start() => _thread.Start();

        /// <summary>The HID interface path SDL reports for the port's row.</summary>
        public string Path { get; }
        public ushort ProductId { get; }

        /// <summary>The SDL row this port rides beside.</summary>
        public Guid InstanceGuid { get; }

        public BlissBoxSession Session { get; }

        public bool IsOpen => _transport.Channel != null;

        /// <summary>Raised on the worker when the channel opens or closes, or
        /// report 17 changes.</summary>
        public event Action<BlissBoxPort> Changed;

        public void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Run()
        {
            long nextOpen = 0;
            while (!_stop)
            {
                if (_transport.Channel == null)
                {
                    if (Environment.TickCount64 >= nextOpen)
                    {
                        nextOpen = Environment.TickCount64 + ReopenIntervalMs;
                        var channel = AnalogKeyboardHidChannel.OpenShared(Path);
                        if (channel != null)
                        {
                            _transport.Channel = channel;
                            Changed?.Invoke(this);
                        }
                    }
                    if (_transport.Channel == null)
                    {
                        _wake.WaitOne(ReopenIntervalMs);
                        continue;
                    }
                }

                int wait;
                try { wait = Session.Step(); }
                catch { wait = ReopenIntervalMs; }
                if (Session.FailedInfoReads >= FailedReadsBeforeReopen)
                {
                    CloseChannel();
                    continue;
                }
                _wake.WaitOne(Math.Max(1, wait));
            }

            try { if (_transport.Channel != null) Session.StopMotors(); } catch { }
            // Closing the channel also ends the jobs still queued.
            CloseChannel();
            _wake.Dispose();
        }

        private void CloseChannel()
        {
            var channel = _transport.Channel;
            _transport.Channel = null;
            if (channel != null)
            {
                try { channel.Close(); } catch { }
            }
            Session.Forget();
            Changed?.Invoke(this);
        }

        /// <summary>Stops the worker, which stops the motors and closes the
        /// channel. A job in progress ends before its next message to the
        /// controller, and one queued from now on ends at once.</summary>
        public void Dispose()
        {
            if (_stop) return;
            // The stop request goes first: a job queued once the worker has
            // seen the stop and ended the queue finds the request and ends
            // itself (BlissBoxSession.Enqueue).
            Session.RequestStop();
            _stop = true;
            Wake();
            if (_thread.ThreadState == ThreadState.Unstarted)
            {
                Session.CancelJobs();
                _wake.Dispose();
                return;
            }
            if (Thread.CurrentThread != _thread) _thread.Join(JoinMs);
        }

        /// <summary>The analog keyboards' overlapped channel, swapped in and
        /// out by the worker as it opens and closes.</summary>
        private sealed class HidTransport : IBlissBoxTransport
        {
            private volatile AnalogKeyboardHidChannel _channel;

            public AnalogKeyboardHidChannel Channel
            {
                get => _channel;
                set => _channel = value;
            }

            public bool SetFeature(byte[] report) => _channel is { } channel && channel.SetFeature(report);

            public int GetFeature(byte[] buffer) => _channel is { } channel ? channel.GetFeature(buffer) : -1;

            public int FeatureLength => _channel?.FeatureLength ?? 0;
        }
    }
}
