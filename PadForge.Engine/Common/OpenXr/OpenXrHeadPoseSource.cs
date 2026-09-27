using System;
using System.Runtime.InteropServices;
using System.Threading;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>What the source is currently doing, for the status line.</summary>
    public enum OpenXrSourceState
    {
        Stopped = 0,
        NoRuntime,          // nothing registered, or the manifest names a missing library
        NoHeadset,          // runtime is there, no head-mounted system
        NotSupported,       // the runtime cannot do a session without graphics
        Connecting,
        Running,
        Failed,
    }

    /// <summary>
    /// A background OpenXR client that reads the headset pose and nothing
    /// else (issue #403).
    ///
    /// <para>The session is headless, which is what
    /// <see cref="OpenXrInterop.XR_MND_HEADLESS_EXTENSION_NAME"/> exists for:
    /// no graphics binding, no swapchain, no submitted frames, so the game
    /// keeps the headset display to itself. Without that extension a session
    /// needs a graphics binding, and this refuses to start rather than
    /// create one, because a background process rendering into a headset is
    /// the failure this design is avoiding.</para>
    ///
    /// <para>Poses are located against a stable reference space rather than
    /// VIEW, since VIEW is the headset and locating it against itself always
    /// reads as the origin.</para>
    ///
    /// <para>Timing is frame-free. The extension permits omitting the frame
    /// calls, and a client that waited on frames would be pacing itself
    /// against the compositor that the game is also driving.</para>
    /// </summary>
    public sealed class OpenXrHeadPoseSource : IDisposable
    {
        /// <summary>How often the pose is sampled. Head movement mapped to a
        /// stick does not need the headset's display rate, and a background
        /// client that polled at 90 Hz would spend most of it resampling a
        /// pose that had not changed.</summary>
        public const int SampleIntervalMs = 8;

        private readonly Action<string> _log;
        private readonly Func<string> _runtimeChoice;
        private readonly Action<double[]> _publish;
        private readonly Action<OpenXrHandState, OpenXrHandState> _publishHands;
        private readonly Func<int> _recenterRequests;
        private int _lastRecenter;

        /// <summary>True once the runtime has accepted a controller profile.
        /// A headset with no controllers is an ordinary case, so this being
        /// false is not a failure.</summary>
        public bool HasControllers => _hasControllers;
        private volatile bool _hasControllers;

        private Thread _thread;
        private volatile bool _running;
        // Set by Stop and never cleared. The create path cannot be
        // interrupted, so it checks this the moment it returns.
        private volatile bool _canceled;
        private volatile OpenXrSourceState _state = OpenXrSourceState.Stopped;
        private volatile string _runtimeName = string.Empty;
        private long _samples;

        public OpenXrSourceState State => _state;

        /// <summary>The runtime actually negotiated with, which is not
        /// always the one a user expects when several are installed.</summary>
        public string RuntimeName => _runtimeName;

        public long SampleCount => Interlocked.Read(ref _samples);

        /// <param name="runtimeChoice">Manifest path to use, or null for the
        /// machine's default.</param>
        /// <param name="publish">Receives a pose in <see cref="HeadPose"/>'s
        /// convention. Called from the sampling thread.</param>
        public OpenXrHeadPoseSource(Func<string> runtimeChoice, Action<double[]> publish,
                                    Action<string> log = null,
                                    Action<OpenXrHandState, OpenXrHandState> publishHands = null,
                                    Func<int> recenterRequests = null)
        {
            _recenterRequests = recenterRequests ?? (() => 0);
            _runtimeChoice = runtimeChoice ?? (() => null);
            _publish = publish ?? (_ => { });
            _publishHands = publishHands ?? ((_, _) => { });
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _state = OpenXrSourceState.Connecting;
            _thread = new Thread(RunLoop)
            {
                Name = "PadForge.OpenXrHeadPose",
                IsBackground = true,
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _canceled = true;
            var t = _thread;
            _thread = null;
            // Bounded: every native call in the loop is a locate or a poll,
            // neither of which blocks on the compositor.
            try { t?.Join(2000); } catch (ThreadStateException) { }
            _state = OpenXrSourceState.Stopped;
        }

        public void Dispose() => Stop();

        private void RunLoop()
        {
            OpenXrSession session = null;
            try
            {
                session = OpenXrSession.TryCreate(_runtimeChoice(), _log, out var why);
                if (session == null)
                {
                    _state = why;
                    return;
                }
                // Creating the session can cold-start the runtime's own
                // processes, which takes far longer than Stop's join. If the
                // feature was switched off while that ran, tear the session
                // down here rather than leaving a live one behind on a
                // machine whose owner just said no.
                if (_canceled)
                {
                    _log("OpenXR: session created after the feature was turned off, closing it");
                    return;
                }
                _runtimeName = session.RuntimeName;
                _state = OpenXrSourceState.Running;

                _hasControllers = session.HasControllers;

                var baseline = default(OpenXrHeadPose.Baseline);
                // Each hand keeps its own neutral. A controller put down and
                // picked up again is not where it was, and neither hand's
                // loss should move the other's zero.
                var leftBaseline = default(OpenXrHeadPose.Baseline);
                var rightBaseline = default(OpenXrHeadPose.Baseline);
                var pose = new double[HeadPose.PoseCount];

                while (_running)
                {
                    if (!session.PumpEvents(out bool lost, out bool spaceChanged))
                        break;
                    if (lost) break;

                    // A reference space change moves the origin under us, so
                    // the captured neutral no longer describes where the user
                    // is. Recapturing beats carrying a baseline that now
                    // points somewhere else in the room.
                    if (spaceChanged)
                    {
                        baseline.Clear();
                        leftBaseline.Clear();
                        rightBaseline.Clear();
                    }

                    // A user asking to recenter is saying "this posture is my
                    // neutral". Dropping the captured one makes the next
                    // sample become it.
                    int requested = _recenterRequests();
                    if (requested != _lastRecenter)
                    {
                        _lastRecenter = requested;
                        baseline.Clear();
                        leftBaseline.Clear();
                        rightBaseline.Clear();
                        _log("OpenXR: neutral recaptured on request");
                    }

                    if (session.HasControllers)
                    {
                        session.ReadHands(ref leftBaseline, ref rightBaseline,
                                          out var leftHand, out var rightHand);
                        if (!leftHand.PoseValid) leftBaseline.Clear();
                        if (!rightHand.PoseValid) rightBaseline.Clear();
                        _publishHands(leftHand, rightHand);
                    }

                    if (session.TryLocateHead(out var position, out var orientation))
                    {
                        if (OpenXrHeadPose.TryFillPose(
                                position.x, position.y, position.z,
                                orientation.x, orientation.y, orientation.z, orientation.w,
                                ref baseline, pose))
                        {
                            long n = Interlocked.Increment(ref _samples);
                            _publish(pose);
                            if ((n <= 64 && (n & (n - 1)) == 0) || (n & 8191) == 0)
                                _log($"OpenXR: head pose #{n} via {_runtimeName} " +
                                     $"yaw={pose[HeadPose.Yaw]:F1} pitch={pose[HeadPose.Pitch]:F1} " +
                                     $"x={pose[HeadPose.TX]:F1} y={pose[HeadPose.TY]:F1} z={pose[HeadPose.TZ]:F1}");
                        }
                    }
                    else
                    {
                        // Tracking loss. The device row's own silence timer
                        // returns the axes to center, and the neutral is
                        // recaptured because a user who took the headset off
                        // is not standing where they were.
                        baseline.Clear();
                    }

                    Thread.Sleep(SampleIntervalMs);
                }
            }
            catch (Exception ex)
            {
                _state = OpenXrSourceState.Failed;
                _log("OpenXR: head pose source stopped: " + ex.Message);
            }
            finally
            {
                try { session?.Dispose(); } catch (Exception) { }
                if (_state == OpenXrSourceState.Running) _state = OpenXrSourceState.Stopped;
                // The thread is done, so the object is startable again. This
                // used to stay true on every self-exit, and Start's guard then
                // returned without doing anything for the life of the object.
                _running = false;
            }
        }
    }
}
