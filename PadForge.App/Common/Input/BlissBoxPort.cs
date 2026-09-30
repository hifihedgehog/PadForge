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
    /// motors their level again once it is back. A job queued while the
    /// channel is closed ends as closed, since no controller is there to
    /// answer it. When the port is disposed, it stops both motors before it
    /// lets go, opening the channel once more if it is down. Other threads set what they want on the
    /// <see cref="Session"/> and wake the worker.</para>
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
        private volatile bool _exited;

        public BlissBoxPort(string path, ushort productId, Guid instanceGuid, uint sdlInstanceId)
        {
            Path = path;
            ProductId = productId;
            InstanceGuid = instanceGuid;
            SdlInstanceId = sdlInstanceId;
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

        /// <summary>The SDL connection the row read when the port opened. A
        /// row that reconnects gets a new port, which starts with its motors
        /// at rest, as the adapter does.</summary>
        public uint SdlInstanceId { get; }

        public BlissBoxSession Session { get; }

        public bool IsOpen => _transport.Channel != null;

        /// <summary>The worker has sent its last write and closed the channel,
        /// or never started. <see cref="Dispose"/> waits for it up to 3 s,
        /// and this stays false past that until the worker is done.</summary>
        public bool Exited => _exited;

        /// <summary>Set on the UI thread once a player change on this port
        /// went through. The adapter comes back as a new device, so this
        /// port's actions and any show on it end, and none writes back the
        /// row's choices the change dropped.</summary>
        public bool Replaced { get; set; }

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
                        Session.CancelJobs();
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

            // A port that closes while its channel is down opens it once more
            // for the stop: an adapter that stayed up may still run the last
            // level, and 3.0's Dreamcast driver never counts one down
            // (0x2858).
            if (_transport.Channel == null)
            {
                try { _transport.Channel = AnalogKeyboardHidChannel.OpenShared(Path); } catch { }
            }
            try { if (_transport.Channel != null) Session.StopMotors(); } catch { }
            // Closing the channel also ends the jobs still queued.
            CloseChannel();
            _wake.Dispose();
            _exited = true;
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
        /// channel. A job in progress ends at its next step, and one queued
        /// from now on ends at once.</summary>
        public void Dispose()
        {
            if (_stop) return;
            // The stop request goes first: a job queued once the worker has
            // seen the stop and ended the queue finds the request and ends
            // itself (BlissBoxSession.Enqueue).
            Session.RequestStop();
            _stop = true;
            Wake();
            // A background thread that never started reports Background as
            // well, so the flag is tested, not the whole state.
            if ((_thread.ThreadState & ThreadState.Unstarted) != 0)
            {
                Session.CancelJobs();
                _wake.Dispose();
                _exited = true;
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

            public bool SetFeature(byte[] report, int timeoutMs)
                => _channel is { } channel && channel.SetFeature(report, timeoutMs);

            public int GetFeature(byte[] buffer) => _channel is { } channel ? channel.GetFeature(buffer) : -1;

            public int FeatureLength => _channel?.FeatureLength ?? 0;
        }
    }
}
