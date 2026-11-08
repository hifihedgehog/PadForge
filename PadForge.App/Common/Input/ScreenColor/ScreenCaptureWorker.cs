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

        /// <summary>The GDI name of the display this grabber reads, which is the
        /// primary when the asked-for one was not attached, or null when it
        /// cannot say.</summary>
        string Output { get; }

        ScreenGrabResult Grab(int timeoutMs, out ScreenFrame frame);
    }

    /// <summary>What a display's capture is doing, for the Lighting tab.</summary>
    internal enum ScreenCaptureState
    {
        /// <summary>Nothing has read it lately.</summary>
        Stopped,
        Capturing,
        /// <summary>Windows holds the desktop for now: a build was refused with
        /// the secure desktop up (a UAC prompt, the lock screen) or the display in
        /// a mode change. The light keeps its last color.</summary>
        Waiting,
        /// <summary>Neither way of reading the screen runs on this PC now, or a
        /// capture keeps ending with no picture (<see cref="ScreenCaptureWorker.EndsBeforeUnavailable"/>).</summary>
        Unavailable,
        /// <summary>The capture ended (a mode or resolution change, an HDR switch,
        /// a display that went away) and is being built again. The light keeps its
        /// last color, and the Lighting tab says nothing until the build answers.</summary>
        Restarting,
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
        /// <summary>The longest wait while no way of reading the screen runs on
        /// this PC.</summary>
        internal const int RetryMaxMs = 8000;

        /// <summary>The longest wait while Windows holds the desktop, Prismatik's
        /// interval for the secure desktop (Lightpack DDuplGrabber.cpp,
        /// ACCESSDENIED_DESKTOP_RETRY_INTERVAL): a lock screen or a UAC prompt
        /// can end at any moment, and the light stays on its last color until
        /// the next try.</summary>
        internal const int WaitRetryMaxMs = 1000;

        /// <summary>How often a capture that fell back to the primary display
        /// looks for the display it was asked for.</summary>
        internal const int ReturnCheckMs = 1000;

        /// <summary>How many times in a row a capture may end with no picture
        /// between its builds before the Lighting tab says the screen cannot be
        /// read. One display change ends a capture once (ACCESS_LOST), and a
        /// rebuilt one can end once more while the switch settles (an HDR switch
        /// also moves the DXGI factory, ScreenModeWatch). A third end in a row is
        /// a capture that fails, about two seconds in with the waits from 250 ms.</summary>
        internal const int EndsBeforeUnavailable = 3;

        private readonly Func<string, IScreenGrabber> _factory;
        private readonly Func<long> _clock;
        private readonly Func<ScreenCaptureWorker, bool> _retire;
        private readonly Func<string, bool> _attached;
        private long _returnCheckAt;
        private string _lastOutput;
        private IScreenGrabber _grabber;
        private ScreenFrame _latest;
        private long _lastRead;
        private int _retryMs = RetryMinMs;
        private int _endsWithoutPicture;
        private int _state = (int)ScreenCaptureState.Capturing;
        private string _lastWhy;

        public string Display { get; }

        public ScreenFrame Latest => Volatile.Read(ref _latest);

        public ScreenCaptureState State => (ScreenCaptureState)Volatile.Read(ref _state);

        /// <param name="attached">Whether a capture built now would read a display
        /// by its GDI name, and not fall back to the primary. Null never looks.</param>
        internal ScreenCaptureWorker(string display, Func<string, IScreenGrabber> factory, Func<long> clock, Func<ScreenCaptureWorker, bool> retire,
            Func<string, bool> attached = null)
        {
            Display = display ?? string.Empty;
            _factory = factory;
            _clock = clock;
            _retire = retire;
            _attached = attached;
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
                    // The wait starts over at a picture, not at a build: a capture
                    // that builds and ends again keeps its growing wait.
                    _lastWhy = null;
                    _lastOutput = _grabber.Output;
                    PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' started through {_grabber.Kind} on '{_grabber.Output}'.");
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

            // A capture asked for one display reads the primary while that one is
            // gone (ScreenDxgi.FindOutput), and nothing on the primary's capture
            // ends when the display comes back. So it is looked for, as Prismatik
            // compares its screens before every grab (Lightpack GrabberBase.cpp,
            // isReallocationNeeded).
            if (_attached != null && Display.Length > 0 && _grabber.Output != null
                && !string.Equals(_grabber.Output, Display, StringComparison.OrdinalIgnoreCase))
            {
                long now = _clock();
                if (now >= _returnCheckAt)
                {
                    _returnCheckAt = now + ReturnCheckMs;
                    if (_attached(Display))
                    {
                        Drop();
                        PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' goes back to its display from {_lastOutput}.");
                        return 0;
                    }
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
                return CaptureEnded(e.Message);
            }
            switch (result)
            {
                case ScreenGrabResult.Frame:
                {
                    Volatile.Write(ref _latest, frame);
                    Volatile.Write(ref _state, (int)ScreenCaptureState.Capturing);
                    _retryMs = RetryMinMs;
                    _endsWithoutPicture = 0;
                    long work = _grabber.LastWorkMs;
                    return (int)Math.Max(FrameMs - work, work * (WorkShare - 1));
                }
                case ScreenGrabResult.NoChange:
                    return 0;
                default:
                    Drop();
                    return CaptureEnded("The capture ended.");
            }
        }

        /// <summary>
        /// A capture that ended, whatever ended it. Neither grabber can tell a
        /// desktop Windows holds from a mode change at this point: Desktop
        /// Duplication answers ACCESS_LOST to both (Microsoft,
        /// IDXGIOutputDuplication::AcquireNextFrame), and only the rebuild's
        /// DuplicateOutput answers E_ACCESSDENIED for the secure desktop. So no
        /// line shows while the capture is built again, and the build's answer
        /// sets it: Capturing, Waiting for a desktop Windows holds, Unavailable
        /// for the rest. Prismatik treats ACCESS_LOST the same, as a frame not
        /// ready and a reallocation, with nothing shown (Lightpack
        /// DDuplGrabber.cpp:612-619, GrabManager.cpp:710-714). A capture that
        /// keeps ending says the screen cannot be read
        /// (<see cref="EndsBeforeUnavailable"/>).
        /// </summary>
        private int CaptureEnded(string why)
        {
            _endsWithoutPicture++;
            return NextRetry(_endsWithoutPicture >= EndsBeforeUnavailable ? ScreenCaptureState.Unavailable : ScreenCaptureState.Restarting, why);
        }

        private int NextRetry(ScreenCaptureState state, string why)
        {
            Volatile.Write(ref _state, (int)state);
            if (why != _lastWhy)
            {
                _lastWhy = why;
                PadForge.Engine.SdlDiagLog.WriteLine($"SCREEN capture of '{Display}' {state}: {why}");
            }
            // Each wait doubles the last, up to the state's own limit: a desktop
            // Windows holds and a capture that ended come back at any moment, a
            // PC that cannot read the screen does not.
            int max = state is ScreenCaptureState.Waiting or ScreenCaptureState.Restarting ? WaitRetryMaxMs : RetryMaxMs;
            int wait = Math.Min(_retryMs, max);
            _retryMs = Math.Min(_retryMs * 2, max);
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
