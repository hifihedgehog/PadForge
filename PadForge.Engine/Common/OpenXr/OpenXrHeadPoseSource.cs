using System;
using System.Collections.Generic;
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
        NoHeadset,          // the runtime reports its headset away. The source asks again, except on SteamVR, where the one answer is final
        NotSupported,       // the runtime cannot do a session without graphics
        Connecting,         // starting a session, at the start or again after the runtime lost one or retired its instance
        Running,
        Failed,             // a call failed or the setup threw, and the diagnostics log says which
        Ended,              // the runtime ended the session or the instance was lost, or the recovery of either stopped, and nothing reopens it
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
        // Held by every write of the state and by Stop's cancel, so nothing
        // the loop writes can land once Stop has canceled it (Show).
        private readonly object _stateGate = new();
        private volatile OpenXrSourceState _state = OpenXrSourceState.Stopped;
        private volatile string _runtimeName = string.Empty;
        private long _samples;

        // What the loop has written about its waits since the last pose
        // (SayOnce). Only the loop's thread touches these.
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private long _saidAt = -1;

        public OpenXrSourceState State => _state;

        /// <summary>The runtime actually negotiated with, which is not
        /// always the one a user expects when several are installed.</summary>
        public string RuntimeName => _runtimeName;

        public long SampleCount => Interlocked.Read(ref _samples);

        /// <summary>How often a waiting source asks the runtime again: for the
        /// headset while the runtime reports it away, and for a new instance
        /// while the runtime is away after retiring the last one. The
        /// specification asks for "some reasonable cadence" (session.adoc,
        /// XR_SESSION_STATE_LOSS_PENDING). The tests shorten it.</summary>
        internal int RetryIntervalMs { get; set; } = 2000;

        /// <summary>How often a source with no session reads its instance's
        /// events while it waits out a retry interval. A runtime that closes
        /// says so through the instance, and the instance is destroyed then,
        /// not at the end of the interval. SteamVR's client library shows
        /// what a late answer can cost: it raises "Aborting due to ignoring
        /// Quit event" for a client that still polls it five seconds, as
        /// that limit starts out, after the server went away
        /// (vrclient_x64.dll, SteamVR 2.17.10).</summary>
        internal int WaitingPumpIntervalMs { get; set; } = 100;

        /// <summary>The millisecond clock the waits run on and the sleep they
        /// take. The tests hand in a clock that moves when the loop sleeps,
        /// so a wait there passes at once and lasts exactly as long as it
        /// asked for.</summary>
        internal Func<long> Ticks { get; set; } = () => Environment.TickCount64;
        internal Action<int> Sleep { get; set; } = Thread.Sleep;

        /// <summary>How long <see cref="Stop"/> waits for the loop's
        /// thread. The test of that wait gives it a bound no loaded PC
        /// reaches, so its outcome never turns on how long the thread
        /// takes.</summary>
        internal int StopJoinMs { get; set; } = 2000;

        internal delegate IOpenXrLink LinkOpener(string manifestPath, bool again, Action<string> log,
            out OpenXrSourceState failure, out bool runtimeUnavailable, out string read);

        /// <summary>Negotiates with the runtime and creates an instance.
        /// <c>again</c> marks an ask the source makes on its own after a
        /// retired instance, of the manifest that instance came from, and
        /// <c>read</c> is what was read when no instance came of the ask.
        /// The log it is handed writes a line once until a pose arrives
        /// (<see cref="SayFromLink"/>). Production goes through
        /// <see cref="OpenXrSession.TryCreate"/>, and the tests script a
        /// runtime here.</summary>
        internal LinkOpener OpenLink { get; set; } = OpenXrSession.TryCreate;

        /// <param name="runtimeChoice">Manifest path to use, or null for the
        /// machine's default. Read once, when the source starts.</param>
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

        /// <summary>Runs the loop on the calling thread, until it ends by
        /// itself or is stopped from inside it. The tests play a scripted
        /// runtime through it, with no second thread to wait for.</summary>
        internal void RunOnThisThread()
        {
            if (_running) return;
            _running = true;
            _state = OpenXrSourceState.Connecting;
            RunLoop();
        }

        /// <summary>
        /// Stops the source. From here on it reads
        /// <see cref="OpenXrSourceState.Stopped"/> with no controllers,
        /// whatever its thread still does.
        ///
        /// <para>The join is bounded, and a thread inside a slow native call
        /// outlives it. That thread ends by itself when the call returns,
        /// and what it writes on the way out is dropped
        /// (<see cref="Show"/>): it used to land after the state set here,
        /// and left a stopped source reading as one that waits for a headset
        /// or starts a session.</para>
        /// </summary>
        public void Stop()
        {
            _running = false;
            lock (_stateGate) _canceled = true;
            var t = _thread;
            _thread = null;
            // Bounded: the loop's waits sleep in 50 ms slices, and its native
            // calls are locates, polls and, while it waits for a headset,
            // xrGetSystem, none of which blocks on the compositor. Creating an
            // instance or a session can take longer, and the loop checks
            // _canceled the moment either returns.
            try { t?.Join(StopJoinMs); } catch (ThreadStateException) { }
            lock (_stateGate)
            {
                _state = OpenXrSourceState.Stopped;
                _hasControllers = false;
            }
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Shows a state, with the controllers a running session has. Once
        /// <see cref="Stop"/> has canceled the loop nothing is shown: the
        /// cancel and every write share one gate, so a write is either done
        /// before Stop sets its own state or never made.
        /// </summary>
        private void Show(OpenXrSourceState state, bool controllers = false)
        {
            lock (_stateGate)
            {
                if (_canceled) return;
                _state = state;
                _hasControllers = controllers;
            }
        }

        /// <summary>
        /// Opens the runtime, keeps a session on it, and reads poses until the
        /// source is stopped or the runtime ends the experience.
        ///
        /// <para>The OpenXR specification separates how a session can end,
        /// and each gets its own answer here:</para>
        /// <list type="bullet">
        /// <item>XR_SESSION_STATE_EXITING asks the application to end its XR
        /// experience and not restart it automatically (session.adoc). The
        /// source stops as <see cref="OpenXrSourceState.Ended"/>, and turning
        /// the input off and on reconnects.</item>
        /// <item>XR_SESSION_STATE_LOSS_PENDING: the application should
        /// destroy the session, and may poll xrGetSystem at some reasonable
        /// cadence and create a new session (session.adoc). The instance
        /// stays, so nothing here can start a runtime again. A session
        /// lost with no grace period answers XR_ERROR_SESSION_LOST to the
        /// call that finds it, and it is replaced the same way, whether
        /// that call is xrBeginSession or xrCreateReferenceSpace
        /// (<see cref="OpenXrSession.EndOfBegin"/>,
        /// <see cref="OpenXrSessionCreate.SessionLost"/>).</item>
        /// <item>XrEventDataInstanceLossPending under a session: the
        /// application destroys the instance, waits past the loss time, and
        /// may re-try xrCreateInstance in a loop while the runtime answers
        /// XR_ERROR_RUNTIME_UNAVAILABLE (instance.adoc). The specification
        /// names an update of the runtime as the usual cause. The new
        /// instance is asked of the manifest the retired one came from, and
        /// of no other runtime. A manifest or a library that is off disk
        /// then reads as the runtime being away, which is how the Khronos
        /// loader reports it (<see cref="OpenXrSession.TryCreate"/>). The
        /// same event with no session standing ends the source as
        /// <see cref="OpenXrSourceState.Ended"/>, since a runtime with no
        /// session to end says it is closing that way
        /// (<see cref="WaitWithoutSession"/>).</item>
        /// <item>A lost instance (XR_ERROR_INSTANCE_LOST) or any other failed
        /// poll ends as <see cref="OpenXrSourceState.Ended"/>. The
        /// specification lets an application create everything again then
        /// (instance.adoc, The Instance Lost Error), but a new instance can
        /// start the runtime's processes (OpenXrRuntimeProbeTests: running
        /// the suite once launched SteamVR on a machine whose owner had just
        /// closed it), and a lost instance cannot be told from a runtime the
        /// user closed. The Khronos sample's event handling, as ALVR's
        /// engine carries it, draws the same line: it restarts on the two
        /// loss events, and stops with no restart on EXITING and on a call
        /// that failed (openxr_program.cpp PollEvents, alxr_engine.cpp
        /// alxr_process_frame2).</item>
        /// <item>A session the runtime refuses to begin never runs, so it is
        /// let go and the source stops
        /// (<see cref="OpenXrSession.EndOfBegin"/>).</item>
        /// </list>
        ///
        /// <para>A runtime that reports its headset temporarily unavailable
        /// (XR_ERROR_FORM_FACTOR_UNAVAILABLE) may report it present on a later
        /// xrGetSystem (system.adoc), so the source asks again at the same
        /// cadence, at the start as after a lost session.</para>
        ///
        /// <para>A wait or a recovery that cannot go on stops the source for
        /// good, on a state whose status line stays true
        /// (<see cref="SettledOn"/>).</para>
        ///
        /// <para>The log gets one line when a wait starts and one when it
        /// ends, each with what was read: the call and its result, the
        /// event, or the file that was missing. The asks in between write
        /// nothing (<see cref="SayOnce"/>, <see cref="Settle"/>). What the
        /// link writes about the instances and sessions it makes goes
        /// through the same rule, so it adds no line per ask either
        /// (<see cref="SayFromLink"/>).</para>
        ///
        /// <para>None of this waiting or creating again runs on SteamVR, whose
        /// runtime library is an OpenVR client library
        /// (<see cref="OpenXrSession.IsOpenVrClientLibrary"/>). There a
        /// missing headset is final and every loss ends as
        /// <see cref="OpenXrSourceState.Ended"/>, the way both always
        /// did.</para>
        /// </summary>
        private void RunLoop()
        {
            IOpenXrLink link = null;
            _said.Clear();
            _saidAt = -1;
            try
            {
                // Negative until the runtime retires an instance. Then the
                // wait before asking for a new one, and while the runtime is
                // away, the wait before asking again.
                long instanceDueMs = -1;
                // The manifest the retired instance's runtime was negotiated
                // from. The new instance is asked of that manifest and the
                // runtime choice is not read a second time. At the system
                // default the choice names the first registered runtime whose
                // library is on disk at that moment, and while an update has
                // this runtime's files off disk that is another runtime,
                // SteamVR included, which a request for an instance starts.
                string retired = null;
                // How many sessions have stood. A stop for good after one
                // has reads as Ended (SettledOn).
                int sessions = 0;
                // The asks for a session since one last stood, and the asks
                // for a new instance since the last one was retired.
                int sessionAsks = 0, instanceAsks = 0;
                // A wait for the headset is open, a lost session is being
                // replaced, and an ask for a new instance has found the
                // runtime away.
                bool waiting = false, replacing = false, away = false;
                while (_running)
                {
                    if (link == null)
                    {
                        if (instanceDueMs > 0 && !WaitWhileRunning(instanceDueMs)) break;
                        bool again = instanceDueMs >= 0;
                        if (again) instanceAsks++;
                        link = OpenLink(again ? retired : _runtimeChoice(), again, SayFromLink,
                                        out var why, out bool unavailable, out string read);
                        if (link == null)
                        {
                            if (again && unavailable)
                            {
                                // Said once for each thing read, so an answer
                                // that changes is written and one that
                                // repeats is not.
                                SayOnce("away " + read, away
                                    ? $"OpenXR: the runtime is still away at ask {Number(instanceAsks)} ({read})"
                                    : $"OpenXR: the runtime is away ({read}), asking for a new instance every {Number(RetryIntervalMs)} ms");
                                away = true;
                                Show(OpenXrSourceState.Connecting);
                                instanceDueMs = RetryIntervalMs;
                                continue;
                            }
                            Settle(again ? $"no new instance was created at ask {Number(instanceAsks)}"
                                         : "no instance was created",
                                   read, SettledOn(why, sessions > 0));
                            return;
                        }
                        if (again)
                            SayOnce("instance", $"OpenXR: {link.RuntimeName} gave a new instance at ask {Number(instanceAsks)}");
                        instanceDueMs = -1;
                        instanceAsks = 0;
                        sessionAsks = 0;
                        away = false;
                        // Creating the instance can cold-start the runtime's
                        // own processes, which takes far longer than Stop's
                        // join. If the feature was switched off while that
                        // ran, tear it down here rather than leaving a live
                        // one behind on a machine whose owner just said no.
                        // Only an instance stands at this point, and the
                        // line says so. It used to speak of a session.
                        if (_canceled)
                        {
                            _log("OpenXR: instance created after the feature was turned off, closing it");
                            return;
                        }
                        _runtimeName = link.RuntimeName;
                    }

                    if (!link.HasSession)
                    {
                        sessionAsks++;
                        var made = link.TryCreateSession(out var failure, out string read);
                        if (made == OpenXrSessionCreate.Failed)
                        {
                            Settle($"{link.RuntimeName} gave no session at ask {Number(sessionAsks)}", read,
                                   SettledOn(failure, sessions > 0));
                            return;
                        }
                        if (made == OpenXrSessionCreate.HeadsetUnavailable)
                        {
                            if (link.SharesOpenVrClient)
                            {
                                // Final for this runtime, as it always was:
                                // its instance is not kept with no session
                                // (OpenXrSession.IsOpenVrClientLibrary).
                                Settle($"{link.RuntimeName} reports no headset, and its instance is not kept waiting for one",
                                       read, OpenXrSourceState.NoHeadset);
                                return;
                            }
                            Show(OpenXrSourceState.NoHeadset);
                            waiting = true;
                            SayOnce("no headset", $"OpenXR: {link.RuntimeName} reports no headset ({read}), asking again every {Number(RetryIntervalMs)} ms");
                            if (!WaitWithoutSession(link, out var gone, out string goneRead)) break;
                            if (gone != OpenXrSessionEnd.None)
                            {
                                EndWithoutSession(link, goneRead);
                                return;
                            }
                            continue;
                        }
                        if (made == OpenXrSessionCreate.SessionLost)
                        {
                            // Lost as it was created, and the link has let
                            // go of what it built. Final for a runtime
                            // whose instance is not kept with no session,
                            // as every loss is there
                            // (OpenXrSession.IsOpenVrClientLibrary).
                            if (link.SharesOpenVrClient)
                            {
                                Settle($"{link.RuntimeName} lost the session as it was created, and its instance is not kept waiting for a new one",
                                       read, OpenXrSourceState.Ended);
                                return;
                            }
                            replacing = true;
                            if (!AwaitNewSession(link, read)) break;
                            continue;
                        }
                        // The wait is over. It is said as the wait for the
                        // headset when there was one, and otherwise as the
                        // lost session's replacement.
                        if (waiting)
                            SayOnce("headset", $"OpenXR: {link.RuntimeName} reports a headset at ask {Number(sessionAsks)}, and a session stands");
                        else if (replacing)
                            SayOnce("session", $"OpenXR: {link.RuntimeName} gave a new session at ask {Number(sessionAsks)}");
                        waiting = replacing = false;
                        sessionAsks = 0;
                        sessions++;
                        if (_canceled)
                        {
                            _log("OpenXR: session created after the feature was turned off, closing it");
                            return;
                        }
                    }

                    Show(OpenXrSourceState.Running, link.HasControllers);
                    var end = Sample(link, out string endRead);
                    if (end == OpenXrSessionEnd.None) break;
                    if (end == OpenXrSessionEnd.BeginRefused)
                    {
                        // The session never ran. An earlier one that did
                        // makes this a recovery that stopped.
                        Settle($"{link.RuntimeName} refused to begin the session", endRead,
                               SettledOn(OpenXrSourceState.Failed, sessions > 1));
                        break;
                    }
                    if (link.SharesOpenVrClient
                        && (end == OpenXrSessionEnd.SessionLost || end == OpenXrSessionEnd.InstanceLossPending))
                    {
                        // Neither recovery runs for this runtime: one keeps
                        // an instance with no session and the other creates
                        // an instance on its own
                        // (OpenXrSession.IsOpenVrClientLibrary).
                        Settle($"{link.RuntimeName} ended the session, and its instance is not kept or created again on its own",
                               endRead, OpenXrSourceState.Ended);
                        break;
                    }
                    if (end == OpenXrSessionEnd.SessionLost)
                    {
                        replacing = true;
                        if (!AwaitNewSession(link, endRead)) break;
                        continue;
                    }
                    if (end == OpenXrSessionEnd.InstanceLossPending)
                    {
                        // Under a session, on a runtime that is its own
                        // client. With no session the same event ends the
                        // source (WaitWithoutSession).
                        instanceDueMs = RetireInstance(ref link, endRead, out retired);
                        continue;
                    }
                    // Exiting, a lost instance or a failed poll. The status
                    // line used to read "Starting the OpenXR session" from
                    // here on.
                    Settle($"{link.RuntimeName} ended the session", endRead, OpenXrSourceState.Ended);
                    break;
                }
            }
            catch (Exception ex)
            {
                Show(OpenXrSourceState.Failed);
                _log("OpenXR: head pose source stopped: " + ex.Message);
            }
            finally
            {
                try { link?.Dispose(); } catch (Exception) { }
                lock (_stateGate)
                {
                    if (_state == OpenXrSourceState.Running) _state = OpenXrSourceState.Stopped;
                }
                // The thread is done, so the object is startable again. This
                // used to stay true on every self-exit, and Start's guard then
                // returned without doing anything for the life of the object.
                _running = false;
            }
        }

        /// <summary>
        /// Writes a line of a wait or a recovery once, until a pose arrives.
        ///
        /// <para>A wait is said when it starts and when it ends. The asks in
        /// between read the same thing, and a line for each was 30 a minute
        /// for as long as a runtime stayed away. A runtime that loses every
        /// new session at once, or retires every new instance, starts a new
        /// wait every interval with no pose in between, and each kind of
        /// line is written once for that whole stretch. Once a pose has
        /// arrived the next wait is said again.</para>
        /// </summary>
        private void SayOnce(string key, string line)
        {
            long delivered = Interlocked.Read(ref _samples);
            if (delivered != _saidAt)
            {
                _said.Clear();
                _saidAt = delivered;
            }
            if (_said.Add(key)) _log(line);
        }

        /// <summary>
        /// The log a link is handed. A link writes about each instance and
        /// each session it makes: its controllers, a session that runs, an
        /// event queue that overflows. Those lines are held to the rule of
        /// the source's own (<see cref="SayOnce"/>), each keyed by its
        /// text, so a runtime that loses every new session or retires
        /// every new instance adds none per ask. Written straight to the
        /// log they were two lines per session, 60 a minute at the
        /// cadence.
        /// </summary>
        private void SayFromLink(string line) => SayOnce(line, line);

        /// <summary>
        /// The state a source that stops for good settles on, so the status
        /// line stays true. It is the reason, with two exceptions. Once a
        /// session has stood the state is
        /// <see cref="OpenXrSourceState.Ended"/>: the runtime ended that
        /// session and nothing reopens it, where the line for a runtime that
        /// cannot supply a session, or for none installed, would speak of a
        /// runtime that had just supplied one. And a reason of
        /// <see cref="OpenXrSourceState.NoHeadset"/> reads as
        /// <see cref="OpenXrSourceState.Failed"/>: that state is the line of
        /// a source still asking, and a wait that stopped used to keep it,
        /// with the row reading the same as while the asking went on.
        /// </summary>
        internal static OpenXrSourceState SettledOn(OpenXrSourceState reason, bool stood)
        {
            if (stood) return OpenXrSourceState.Ended;
            return reason == OpenXrSourceState.NoHeadset ? OpenXrSourceState.Failed : reason;
        }

        /// <summary>Stops the source for good on a state, with the one line
        /// that says why: what the runtime did not give or did, what was
        /// read, and the state the row is left on.</summary>
        private void Settle(string what, string read, OpenXrSourceState state)
        {
            _log($"OpenXR: {what} ({read}), so the source stops ({state})");
            Show(state);
        }

        /// <summary>Destroys an instance the runtime retires, and returns the
        /// wait before asking for a new one: until the loss time has passed,
        /// and never less than one retry interval, so a runtime that retires
        /// every new instance is asked at the cadence and no faster.
        /// <paramref name="manifest"/> is the manifest the new instance is
        /// asked of, the one this instance came from.</summary>
        private long RetireInstance(ref IOpenXrLink link, string read, out string manifest)
        {
            long wait = Math.Max(RetryIntervalMs, link.InstanceLossWaitMs);
            manifest = link.ManifestPath;
            SayOnce("retired", $"OpenXR: {link.RuntimeName} retires its instance ({read}), asking for a new one in {Number(wait)} ms");
            try { link.Dispose(); } catch (Exception) { }
            link = null;
            Show(OpenXrSourceState.Connecting);
            return wait;
        }

        /// <summary>
        /// Waits out one retry interval with no session standing, and reads
        /// the instance's events on the way. False once the source is
        /// stopped. <paramref name="gone"/> is what the runtime ended, with
        /// what was read in <paramref name="read"/>, and anything but None
        /// ends the source, the instance-loss event included.
        ///
        /// <para>With no session the runtime has no session to move to
        /// XR_SESSION_STATE_EXITING, so it says it is closing through the
        /// instance. SteamVR shows the pattern: on its quit event it asks
        /// each session to exit, and an instance with no session gets
        /// XrEventDataInstanceLossPending with a loss time an hour out (read
        /// from the vrclient_x64.dll of SteamVR 2.17.10, the VREvent_Quit
        /// branch of the function that turns OpenVR events into OpenXR
        /// ones). Asking SteamVR for an instance starts SteamVR
        /// (OpenXrRuntimeProbeTests), so a runtime that does both would be
        /// reopened by a new instance right after the user closed it.
        /// SteamVR itself never waits here
        /// (<see cref="OpenXrSession.IsOpenVrClientLibrary"/>).</para>
        /// </summary>
        private bool WaitWithoutSession(IOpenXrLink link, out OpenXrSessionEnd gone, out string read)
        {
            long until = Ticks() + Math.Max(0, RetryIntervalMs);
            while (true)
            {
                gone = link.PumpEvents(out _, out read);
                if (gone != OpenXrSessionEnd.None) return true;
                long left = until - Ticks();
                if (left <= 0) return true;
                if (!WaitWhileRunning(Math.Min(left, Math.Max(1, WaitingPumpIntervalMs)))) return false;
            }
        }

        /// <summary>
        /// Lets a lost session go and waits out one retry interval before
        /// the next one is asked for, whether the pump read the loss or the
        /// session was lost as it was created. The wait keeps a runtime
        /// that loses every new session at once to one new session per
        /// interval, the cadence the headset is asked for. False when the
        /// loop is to end: the source was stopped, or the runtime went away
        /// during the wait, which ends the source
        /// (<see cref="EndWithoutSession"/>).
        /// </summary>
        private bool AwaitNewSession(IOpenXrLink link, string read)
        {
            SayOnce("lost", $"OpenXR: {link.RuntimeName} lost the session ({read}), so a new one is asked for every {Number(RetryIntervalMs)} ms");
            if (link.HasSession) link.DestroySession();
            Show(OpenXrSourceState.Connecting);
            if (!WaitWithoutSession(link, out var gone, out string goneRead)) return false;
            if (gone == OpenXrSessionEnd.None) return true;
            EndWithoutSession(link, goneRead);
            return false;
        }

        /// <summary>Ends the source for a runtime that went away while no
        /// session stood, and asks for no new instance.</summary>
        private void EndWithoutSession(IOpenXrLink link, string read)
            => Settle($"{link.RuntimeName} went away with no session standing", read, OpenXrSourceState.Ended);

        /// <summary>Sleeps in short slices, so a wait never outlives Stop's
        /// join. False once the source is stopped.</summary>
        private bool WaitWhileRunning(long ms)
        {
            long until = Ticks() + Math.Max(0, ms);
            while (_running)
            {
                long left = until - Ticks();
                if (left <= 0) return true;
                Sleep((int)Math.Min(left, 50));
            }
            return false;
        }

        /// <summary>Reads the headset and the controllers on one session
        /// until the source is stopped, which returns
        /// <see cref="OpenXrSessionEnd.None"/>, or the event pump reports an
        /// end, with what it read in <paramref name="read"/>. The neutrals
        /// belong to the session, so a new session captures its own.</summary>
        private OpenXrSessionEnd Sample(IOpenXrLink session, out string read)
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            // Each hand keeps its own neutral. A controller put down and
            // picked up again is not where it was, and neither hand's loss
            // should move the other's zero.
            var leftBaseline = default(OpenXrHeadPose.Baseline);
            var rightBaseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];

            while (_running)
            {
                var end = session.PumpEvents(out bool spaceChanged, out read);
                if (end != OpenXrSessionEnd.None) return end;

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
                            _log($"OpenXR: head pose #{Number(n)} via {_runtimeName} " +
                                 $"yaw={Number(pose[HeadPose.Yaw], "F1")} pitch={Number(pose[HeadPose.Pitch], "F1")} " +
                                 $"x={Number(pose[HeadPose.TX], "F1")} y={Number(pose[HeadPose.TY], "F1")} " +
                                 $"z={Number(pose[HeadPose.TZ], "F1")}");
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

                Sleep(SampleIntervalMs);
            }
            read = null;
            return OpenXrSessionEnd.None;
        }
    }
}
