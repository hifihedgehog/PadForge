using System;
using System.Threading;

namespace PadForge.Common.Input.ScreenColor
{
    /// <summary>What one try at the next desktop image came to.</summary>
    internal enum ScreenGrabResult
    {
        /// <summary>A new picture.</summary>
        Frame,
        /// <summary>Nothing changed on the display.</summary>
        NoChange,
        /// <summary>The capture ended: a mode change, a UAC prompt, a device
        /// that went away. Build it again.</summary>
        Lost,
    }

    /// <summary>One way of reading one display. Used from one thread.</summary>
    internal interface IScreenGrabber : IDisposable
    {
        /// <summary>The kind's name, for the log.</summary>
        string Kind { get; }

        /// <summary>How long the last picture took to read back.</summary>
        long LastWorkMs { get; }

        ScreenGrabResult Grab(int timeoutMs, out ScreenFrame frame);
    }

    /// <summary>What a display's capture is doing, for the Lighting tab.</summary>
    internal enum ScreenCaptureState
    {
        /// <summary>Nothing has read it lately.</summary>
        Stopped,
        Capturing,
        /// <summary>Windows holds the desktop for now: a UAC prompt, the lock
        /// screen, a mode change. The light keeps its last color.</summary>
        Waiting,
        /// <summary>Neither way of reading the screen runs on this PC now.</summary>
        Unavailable,
    }

    /// <summary>
    /// The thread that reads one display (#559). It runs while something reads
    /// its pictures, and it retires when nothing has for
    /// <see cref="IdleStopMs"/>. A stopped engine, a changed profile and a macro
    /// on a device that is not selected all end it with no hook of their own,
    /// where the audio detector's start and stop hooks see only the selected
    /// device.
    /// </summary>
    internal sealed class ScreenCaptureWorker
    {
        internal const int IdleStopMs = 2000;

        /// <summary>About 30 pictures a second at the most, the top of Aurora's
        /// range (AmbilightLayerHandler.GetIntervalFromFps).</summary>
        internal const int FrameMs = 33;

        /// <summary>Reading back gets at most a quarter of the time, for a PC
        /// where a picture costs more than a frame's share.</summary>
        internal const int WorkShare = 4;

        internal const int GrabTimeoutMs = 100;
        internal const int RetryMinMs = 250;
        internal const int RetryMaxMs = 8000;

        private readonly Func<string, IScreenGrabber> _factory;
        private readonly Func<long> _clock;
        private readonly Func<ScreenCaptureWorker, bool> _retire;
        private IScreenGrabber _grabber;
        private ScreenFrame _latest;
        private long _lastRead;
        private int _retryMs = RetryMinMs;
        private int _state = (int)ScreenCaptureState.Capturing;
        private string _lastWhy;

        public string Display { get; }

        public ScreenFrame Latest => Volatile.Read(ref _latest);

        public ScreenCaptureState State => (ScreenCaptureState)Volatile.Read(ref _state);

        internal ScreenCaptureWorker(string display, Func<string, IScreenGrabber> factory, Func<long> clock, Func<ScreenCaptureWorker, bool> retire)
        {
            Display = display ?? string.Empty;
            _factory = factory;
            _clock = clock;
            _retire = retire;
            _lastRead = clock();
        }

        /// <summary>Says something still reads this display.</summary>
        public void Touch() => Volatile.Write(ref _lastRead, _clock());

        internal bool IsIdle => _clock() - Volatile.Read(ref _lastRead) > IdleStopMs;

        internal void Start()
        {
            var thread = new Thread(Run)
            {
                Name = "PadForge Screen Color",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    int wait = Step();
                    if (wait < 0) return;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally
            {
                Drop();
            }
        }

        /// <summary>One pass. The milliseconds to wait before the next, or -1
        /// when this worker has retired.</summary>
        internal int Step()
        {
            if (IsIdle && _retire(this))
            {
                Drop();
                Volatile.Write(ref _state, (int)ScreenCaptureState.Stopped);
                PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' stopped: nothing reads it.");
                return -1;
            }

            if (_grabber == null)
            {
                try
                {
                    _grabber = _factory(Display);
                    Volatile.Write(ref _state, (int)ScreenCaptureState.Capturing);
                    _retryMs = RetryMinMs;
                    _lastWhy = null;
                    PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' started through {_grabber.Kind}.");
                }
                catch (ScreenGrabberWaiting e)
                {
                    return NextRetry(ScreenCaptureState.Waiting, e.Message);
                }
                catch (Exception e)
                {
                    return NextRetry(ScreenCaptureState.Unavailable, e.Message);
                }
            }

            ScreenGrabResult result;
            ScreenFrame frame;
            try
            {
                result = _grabber.Grab(GrabTimeoutMs, out frame);
            }
            catch (Exception e)
            {
                Drop();
                return NextRetry(ScreenCaptureState.Waiting, e.Message);
            }
            switch (result)
            {
                case ScreenGrabResult.Frame:
                {
                    Volatile.Write(ref _latest, frame);
                    Volatile.Write(ref _state, (int)ScreenCaptureState.Capturing);
                    _retryMs = RetryMinMs;
                    long work = _grabber.LastWorkMs;
                    return (int)Math.Max(FrameMs - work, work * (WorkShare - 1));
                }
                case ScreenGrabResult.NoChange:
                    return 0;
                default:
                    Drop();
                    return NextRetry(ScreenCaptureState.Waiting, "The capture ended.");
            }
        }

        private int NextRetry(ScreenCaptureState state, string why)
        {
            Volatile.Write(ref _state, (int)state);
            if (why != _lastWhy)
            {
                _lastWhy = why;
                PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' {state}: {why}");
            }
            int wait = _retryMs;
            _retryMs = Math.Min(_retryMs * 2, RetryMaxMs);
            return wait;
        }

        private void Drop()
        {
            var grabber = _grabber;
            _grabber = null;
            if (grabber == null) return;
            try { grabber.Dispose(); }
            catch (Exception e) { PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture close failed: {e.Message}"); }
        }
    }
}
