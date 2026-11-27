using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Tests
{
    /// <summary>
    /// The OpenXR source answers each way a session can end the way the
    /// specification asks. XR_SESSION_STATE_EXITING ends the XR experience
    /// with no automatic restart (session.adoc), so the source stops as Ended.
    /// XR_SESSION_STATE_LOSS_PENDING lets the application poll xrGetSystem and
    /// create a new session (session.adoc), on the instance it has, and a
    /// session lost as it is created is replaced the same way. The
    /// instance-loss event under a session lets it create a new instance once
    /// the loss time has passed, in a loop while the runtime answers
    /// XR_ERROR_RUNTIME_UNAVAILABLE (instance.adoc). The same event with no
    /// session standing stops as Ended: a runtime with no session to end
    /// says it is closing that way, as SteamVR does, and a new instance could
    /// start it again. A lost instance or a failed poll stops as Ended too,
    /// since a new instance can start the runtime's processes and a lost one
    /// cannot be told from a runtime the user closed. On SteamVR itself none
    /// of the waiting or creating again runs, since its runtime library is
    /// the OpenVR client library PadForge's OpenVR input shares.
    ///
    /// <para>A wait or a recovery that cannot go on stops the source on a
    /// state whose status line is true, with one line in the log that says
    /// why. The new instance after a retired one is asked of the manifest
    /// that one came from. A wait is in the log once when it starts and once
    /// when it ends, each line with what was read.</para>
    ///
    /// <para>A scripted link stands in for the runtime, so these start
    /// nothing. Each script plays to its end on the test's own thread, on a
    /// clock that moves only when the loop sleeps, so nothing here waits on
    /// a second thread or on the time of day. Two tests need a second
    /// thread and hold it on a gate: the one about a Stop that gave up on
    /// its thread, and the one that runs the loop through Start, as
    /// production does. The real session's half is in
    /// OpenXrSessionScriptedRuntimeTests.</para>
    /// </summary>
    public class OpenXrSessionRecoveryTests
    {
        /// <summary>A runtime that answers from a script: each session
        /// attempt takes the next answer, and each event poll the next
        /// event. What it hands back as read is "scripted" and the answer's
        /// name.</summary>
        private sealed class ScriptedLink : IOpenXrLink
        {
            private readonly Queue<OpenXrSessionCreate> _creates = new();
            private readonly Queue<OpenXrSessionEnd> _events = new();
            private readonly Dictionary<int, OpenXrSessionEnd[]> _reads = new();

            public string RuntimeName => "Scripted Runtime";
            public string ManifestPath { get; set; } = @"C:\Scripted Runtime\scripted.json";
            public bool HasSession { get; private set; }
            public bool HasControllers => HasSession && Controllers;
            public bool SharesOpenVrClient { get; set; }
            public long InstanceLossWaitMs { get; set; }

            /// <summary>Whether a session of this link has controllers.</summary>
            public bool Controllers;

            /// <summary>The answer once the script runs out of session
            /// answers.</summary>
            public OpenXrSessionCreate ThenCreate = OpenXrSessionCreate.Created;

            /// <summary>The state a Failed answer carries, as the real link
            /// says why it failed.</summary>
            public OpenXrSourceState FailsAs = OpenXrSourceState.NotSupported;

            /// <summary>Every session is lost on its first poll.</summary>
            public bool LoseEverySession;

            /// <summary>Runs inside each ask for a session, before the
            /// answer, the way a slow native call holds the loop.</summary>
            public Action OnSessionAsk;

            public int SessionsCreated { get; private set; }
            public int SessionsDestroyed { get; private set; }
            /// <summary>How often the source asked for a session.</summary>
            public int SessionAsks { get; private set; }
            public bool Disposed { get; private set; }

            public ScriptedLink Creates(params OpenXrSessionCreate[] answers)
            {
                foreach (var a in answers) _creates.Enqueue(a);
                return this;
            }

            /// <summary>What the nth session reads from its first polls, in
            /// order. A None is a poll that reads nothing, after which the
            /// session is sampled once.</summary>
            public ScriptedLink Session(int nth, params OpenXrSessionEnd[] reads)
            {
                _reads[nth] = reads;
                return this;
            }

            /// <summary>What the next poll reads, with or without a session.</summary>
            public void Raise(OpenXrSessionEnd end) => _events.Enqueue(end);

            public OpenXrSessionCreate TryCreateSession(out OpenXrSourceState failure, out string read)
            {
                // A loop that asks without waiting would never let the
                // script's clock move, so it is stopped here.
                if (++SessionAsks > 100_000) throw new InvalidOperationException("the loop asks for a session without waiting");
                OnSessionAsk?.Invoke();
                var answer = _creates.Count > 0 ? _creates.Dequeue() : ThenCreate;
                switch (answer)
                {
                    case OpenXrSessionCreate.Created:
                        HasSession = true;
                        SessionsCreated++;
                        if (_reads.TryGetValue(SessionsCreated, out var reads))
                            foreach (var end in reads) _events.Enqueue(end);
                        failure = OpenXrSourceState.Running;
                        read = null;
                        break;
                    case OpenXrSessionCreate.HeadsetUnavailable:
                        failure = OpenXrSourceState.NoHeadset;
                        read = "scripted no headset";
                        break;
                    case OpenXrSessionCreate.SessionLost:
                        // The real link destroys what it built before it
                        // answers, so no session stands here either.
                        failure = OpenXrSourceState.Connecting;
                        read = "scripted lost session";
                        break;
                    default:
                        failure = FailsAs;
                        read = "scripted refusal";
                        break;
                }
                return answer;
            }

            public OpenXrSessionEnd PumpEvents(out bool referenceSpaceChanged, out string read)
            {
                referenceSpaceChanged = false;
                var end = LoseEverySession && HasSession ? OpenXrSessionEnd.SessionLost
                        : _events.Count > 0 ? _events.Dequeue() : OpenXrSessionEnd.None;
                read = end == OpenXrSessionEnd.None ? null : "scripted " + end;
                return end;
            }

            public void DestroySession()
            {
                HasSession = false;
                SessionsDestroyed++;
            }

            public void ReadHands(ref OpenXrHeadPose.Baseline left, ref OpenXrHeadPose.Baseline right,
                                  out OpenXrHandState leftState, out OpenXrHandState rightState)
            {
                leftState = OpenXrHandState.Empty;
                rightState = OpenXrHandState.Empty;
            }

            public bool TryLocateHead(out XrVector3f position, out XrQuaternionf orientation)
            {
                position = default;
                orientation = new XrQuaternionf { w = 1f };
                return HasSession;
            }

            public void Dispose()
            {
                HasSession = false;
                Disposed = true;
            }
        }

        /// <summary>A source whose runtime is the given script: each instance
        /// the source asks for takes the next entry, a link or a refusal.</summary>
        private sealed class Bench : IDisposable
        {
            public readonly OpenXrHeadPoseSource Source;
            public readonly List<string> Log = new();
            private readonly Queue<(ScriptedLink link, OpenXrSourceState why, bool unavailable)> _opens = new();
            private readonly List<(long at, Action act)> _due = new();
            private long _now;

            /// <summary>What each ask for an instance was handed, the state
            /// the row showed then, and the time on the script's clock.</summary>
            public readonly List<(string Manifest, bool Again, OpenXrSourceState State, long At)> Asked = new();

            /// <summary>How often the source read the runtime choice, which
            /// here is the system default.</summary>
            public int ChoiceReads { get; private set; }

            /// <summary>What the nth ask for an instance hands back as read
            /// when no instance comes of it.</summary>
            public Func<int, string> Reads;

            /// <summary>Runs inside the nth ask for an instance, before the
            /// answer.</summary>
            public Action<int> OnOpen;

            /// <summary>The states the row showed while the loop slept, each
            /// change once, in order.</summary>
            public readonly List<OpenXrSourceState> Shown = new();

            /// <summary>The longest single sleep the loop asked for.</summary>
            public int LongestSleep { get; private set; }

            /// <summary>Milliseconds on the script's clock.</summary>
            public long Now => _now;

            public int Opens => Asked.Count;

            public Bench(params (ScriptedLink link, OpenXrSourceState why, bool unavailable)[] opens)
            {
                foreach (var o in opens) _opens.Enqueue(o);
                Source = new OpenXrHeadPoseSource(
                    () => { ChoiceReads++; return null; },
                    _ => { }, line => { lock (Log) Log.Add(line); })
                {
                    RetryIntervalMs = 1,
                };
                Source.OpenLink = Open;
                // The clock moves only when the loop sleeps, by as much as
                // it asked for, so nothing waits and every wait lasts
                // exactly as long as the loop meant it to.
                Source.Ticks = () => _now;
                Source.Sleep = ms =>
                {
                    LongestSleep = Math.Max(LongestSleep, ms);
                    _now += Math.Max(1, ms);
                    if (_now > 3_600_000) throw new InvalidOperationException("the script did not end the source");
                    var state = Source.State;
                    if (Shown.Count == 0 || Shown[^1] != state) Shown.Add(state);
                    for (int i = 0; i < _due.Count; i++)
                    {
                        if (_due[i].at > _now) continue;
                        var act = _due[i].act;
                        _due.RemoveAt(i--);
                        act();
                    }
                };
            }

            private IOpenXrLink Open(string manifest, bool again, Action<string> log,
                                     out OpenXrSourceState failure, out bool unavailable, out string read)
            {
                Asked.Add((manifest, again, Source.State, _now));
                int n = Asked.Count;
                if (n > 100_000) throw new InvalidOperationException("the loop asks for an instance without waiting");
                OnOpen?.Invoke(n);
                if (_opens.Count == 0)
                {
                    failure = OpenXrSourceState.NoRuntime;
                    unavailable = false;
                    read = "the script holds no more instances";
                    return null;
                }
                var next = _opens.Dequeue();
                failure = next.why;
                unavailable = next.unavailable;
                read = next.link != null ? null : Reads?.Invoke(n) ?? "scripted no instance";
                return next.link;
            }

            /// <summary>Runs an action once the script's clock has reached a
            /// time, from inside the sleep that reaches it.</summary>
            public void At(long ms, Action act) => _due.Add((ms, act));

            /// <summary>Stops the source at a time on the script's clock, the
            /// way the feature is turned off while the loop waits.</summary>
            public void StopAt(long ms) => At(ms, Source.Stop);

            /// <summary>
            /// Plays the script to its end on this thread. The script has to
            /// end the source, by what the runtime does or by
            /// <see cref="StopAt"/>, and one that does not is stopped after
            /// an hour on its clock, as Failed with a line in the log.
            /// </summary>
            public void Run() => Source.RunOnThisThread();

            /// <summary>The lines the log holds that contain a text.</summary>
            public List<string> Lines(string containing)
            {
                lock (Log) return Log.FindAll(l => l.Contains(containing));
            }

            public void Dispose() => Source.Dispose();
        }

        private static (ScriptedLink, OpenXrSourceState, bool) Link(ScriptedLink link)
            => (link, OpenXrSourceState.Connecting, false);

        private static readonly (ScriptedLink link, OpenXrSourceState why, bool unavailable) Away
            = (null, OpenXrSourceState.NotSupported, true);

        private const OpenXrSessionEnd Quiet = OpenXrSessionEnd.None;

        [Fact]
        public void TheEndsAreToldApart()
        {
            const int changed = XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED;
            Assert.Equal(OpenXrSessionEnd.Exiting, OpenXrSession.EndOf(XR_SUCCESS, changed, XR_SESSION_STATE_EXITING));
            Assert.Equal(OpenXrSessionEnd.SessionLost, OpenXrSession.EndOf(XR_SUCCESS, changed, XR_SESSION_STATE_LOSS_PENDING));
            Assert.Equal(OpenXrSessionEnd.None, OpenXrSession.EndOf(XR_SUCCESS, changed, XR_SESSION_STATE_STOPPING));
            Assert.Equal(OpenXrSessionEnd.None, OpenXrSession.EndOf(XR_SUCCESS, changed, XR_SESSION_STATE_FOCUSED));
            Assert.Equal(OpenXrSessionEnd.InstanceLossPending,
                OpenXrSession.EndOf(XR_SUCCESS, XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING, 0));
            Assert.Equal(OpenXrSessionEnd.None,
                OpenXrSession.EndOf(XR_SUCCESS, XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING, 0));
            Assert.Equal(OpenXrSessionEnd.None, OpenXrSession.EndOf(XR_EVENT_UNAVAILABLE, 0, 0));
            Assert.Equal(OpenXrSessionEnd.InstanceLost, OpenXrSession.EndOf(XR_ERROR_INSTANCE_LOST, 0, 0));
            Assert.Equal(OpenXrSessionEnd.PollFailed, OpenXrSession.EndOf(-2, 0, 0));
        }

        /// <summary>A lost session comes back on the instance the source
        /// already has: the session is destroyed, the headset is asked for
        /// until it is back, and a new session runs. No new instance is
        /// created, so nothing here can start a runtime again.</summary>
        [Fact]
        public void ALostSessionComesBackOnTheSameInstance()
        {
            var link = new ScriptedLink()
                .Creates(OpenXrSessionCreate.Created,
                         OpenXrSessionCreate.HeadsetUnavailable,
                         OpenXrSessionCreate.HeadsetUnavailable)
                .Session(1, Quiet, OpenXrSessionEnd.SessionLost)
                .Session(2, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(2, link.SessionsCreated);
            Assert.Equal(1, link.SessionsDestroyed);
            Assert.Equal(4, link.SessionAsks);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(new[]
            {
                OpenXrSourceState.Running, OpenXrSourceState.Connecting,
                OpenXrSourceState.NoHeadset, OpenXrSourceState.Running,
            }, bench.Shown);
            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
        }

        /// <summary>A runtime that loses every new session at once is asked
        /// for the next one at the retry cadence, not in a tight loop, and the
        /// loss is logged once while no pose arrives in between.</summary>
        [Fact]
        public void ASessionLostAtOnceIsCreatedAgainAtTheCadence()
        {
            var link = new ScriptedLink { LoseEverySession = true };
            using var bench = new Bench(Link(link));
            bench.Source.RetryIntervalMs = 150;
            bench.StopAt(1000);

            bench.Run();

            // One at the start and one every 150 ms after it, the last at
            // 900 ms.
            Assert.Equal(7, link.SessionsCreated);
            Assert.Equal(1000, bench.Now);
            Assert.Single(bench.Lines("lost the session"));
            Assert.Single(bench.Lines("gave a new session"));
            Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
        }

        /// <summary>
        /// A session the runtime loses as it is created, with no grace
        /// period, is replaced like one lost while it ran. The loss is said
        /// once, the row shows a session being started, and the next ask
        /// comes one interval later on the instance the source has. The
        /// link let go of what it had built, so the loop destroys nothing.
        /// It used to stop the source for good, as a runtime that cannot
        /// supply a session.
        /// </summary>
        [Fact]
        public void ASessionLostAsItIsCreatedIsAskedForAgainAtTheCadence()
        {
            var link = new ScriptedLink()
                .Creates(OpenXrSessionCreate.SessionLost, OpenXrSessionCreate.SessionLost)
                .Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));
            bench.Source.RetryIntervalMs = 150;
            var askedAt = new List<long>();
            link.OnSessionAsk = () => askedAt.Add(bench.Now);

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(new long[] { 0, 150, 300 }, askedAt);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionsCreated);
            Assert.Equal(0, link.SessionsDestroyed);
            Assert.Equal(new[] { OpenXrSourceState.Connecting }, bench.Shown);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime lost the session (scripted lost session), so a new one is asked for every 150 ms",
                "OpenXR: Scripted Runtime gave a new session at ask 3",
                "OpenXR: Scripted Runtime ended the session (scripted Exiting), so the source stops (Ended)",
            }, bench.Log);
        }

        /// <summary>A session lost as it was created never stood. A source
        /// that then stops for good settles on the reason it is given, not
        /// on the line of a runtime that ended a session.</summary>
        [Fact]
        public void ASessionLostAsItIsCreatedDoesNotCountAsOneThatStood()
        {
            var link = new ScriptedLink()
                .Creates(OpenXrSessionCreate.SessionLost, OpenXrSessionCreate.Failed);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.NotSupported, bench.Source.State);
            Assert.Equal(2, link.SessionAsks);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime lost the session (scripted lost session), so a new one is asked for every 1 ms",
                "OpenXR: Scripted Runtime gave no session at ask 2 (scripted refusal), so the source stops (NotSupported)",
            }, bench.Log);
        }

        /// <summary>The wait after such a loss reads the instance's events
        /// like any wait with no session, so a runtime that retires its
        /// instance then ends the source, long before the interval is
        /// over.</summary>
        [Fact]
        public void AnInstanceRetiredAfterASessionLostAsItWasCreatedEndsTheSource()
        {
            var link = new ScriptedLink().Creates(OpenXrSessionCreate.SessionLost);
            link.Raise(OpenXrSessionEnd.InstanceLossPending);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));
            bench.Source.RetryIntervalMs = 60_000;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionAsks);
            Assert.True(link.Disposed);
            Assert.True(bench.Now < 100, $"the source ended at {bench.Now} ms");
            Assert.Equal("OpenXR: Scripted Runtime went away with no session standing (scripted InstanceLossPending), so the source stops (Ended)",
                         bench.Log[^1]);
        }

        /// <summary>On a runtime whose library is an OpenVR client library
        /// the same loss is final, as every loss is there: the wait for a
        /// new session would keep its instance with no session. One ask,
        /// one line, and the instance is let go.</summary>
        [Fact]
        public void ASessionLostAsItIsCreatedIsFinalOnARuntimeThatSharesItsOpenVrClient()
        {
            var link = new ScriptedLink { SharesOpenVrClient = true }.Creates(OpenXrSessionCreate.SessionLost);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(1, link.SessionAsks);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(0, bench.Now);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime lost the session as it was created, and its instance is not kept waiting for a new one (scripted lost session), so the source stops (Ended)",
            }, bench.Log);
        }

        /// <summary>While the headset is away the line says the runtime
        /// reports no headset, never that the session ended.</summary>
        [Fact]
        public void ASourceWaitingForTheHeadsetSaysNoHeadset()
        {
            var link = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable }
                .Creates(OpenXrSessionCreate.Created)
                .Session(1, Quiet, OpenXrSessionEnd.SessionLost);
            using var bench = new Bench(Link(link));
            bench.Source.RetryIntervalMs = 100;
            bench.StopAt(1000);

            bench.Run();

            // Running, then one interval at the line of a session being
            // started, then the wait for the headset for as long as it lasts.
            Assert.Equal(new[]
            {
                OpenXrSourceState.Running, OpenXrSourceState.Connecting, OpenXrSourceState.NoHeadset,
            }, bench.Shown);
            Assert.True(link.SessionAsks > 5, $"only {link.SessionAsks} asks");
            Assert.Equal(1, bench.Opens);
            Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
        }

        /// <summary>The runtime ended the XR experience. The source stops as
        /// Ended and creates nothing, as the specification asks after
        /// XR_SESSION_STATE_EXITING.</summary>
        [Fact]
        public void AnExitingSessionEndsAndOpensNothing()
        {
            var link = new ScriptedLink().Session(1, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionsCreated);
            Assert.Equal(1, link.SessionAsks);
            Assert.True(link.Disposed);
        }

        /// <summary>A lost instance or a failed poll ends the source too. A
        /// new instance could start the runtime's processes, and a lost one
        /// cannot be told from a runtime the user closed.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALostInstanceEndsWithoutANewOne(bool instanceLost)
        {
            var link = new ScriptedLink()
                .Session(1, Quiet, instanceLost ? OpenXrSessionEnd.InstanceLost : OpenXrSessionEnd.PollFailed);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionAsks);
            Assert.True(link.Disposed);
        }

        /// <summary>An instance the runtime retires, as for an update, is
        /// destroyed, and a new one is asked for after the loss time, again
        /// while the runtime answers that it is away, and the new session
        /// runs.</summary>
        [Fact]
        public void ARetiredInstanceIsCreatedAgainOnceTheRuntimeIsBack()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, Quiet, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink().Session(1, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(first), Away, Away, Link(second));

            bench.Run();

            Assert.True(first.Disposed);
            Assert.Equal(4, bench.Opens);
            Assert.Equal(1, second.SessionsCreated);
            Assert.Equal(new[]
            {
                OpenXrSourceState.Running, OpenXrSourceState.Connecting, OpenXrSourceState.Running,
            }, bench.Shown);
            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
        }

        /// <summary>A new instance is never asked for sooner than one retry
        /// interval after a retirement, whatever loss time the event gave, so
        /// a runtime that retires every instance is asked at the cadence.
        /// With a loss time already past, the ask comes exactly one interval
        /// after the retirement.</summary>
        [Fact]
        public void ARetiredInstanceWaitsAtLeastOneInterval()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 0 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink().Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(first), Link(second));
            bench.Source.RetryIntervalMs = 300;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(new long[] { 0, 300 }, bench.Asked.ConvertAll(a => a.At));
        }

        /// <summary>The new instance after a retired one is asked of the
        /// manifest the retired one was negotiated from, and the runtime
        /// choice is read once, at the start. Read again at the system
        /// default, the choice names whichever registered runtime has its
        /// library on disk at that moment. While an update has the first
        /// runtime's files off disk that is another runtime, SteamVR
        /// included, and asking SteamVR for an instance starts it.</summary>
        [Fact]
        public void ARetiredInstanceIsAskedOfTheManifestItCameFrom()
        {
            const string manifest = @"C:\Runtime X\x.json";
            var first = new ScriptedLink { ManifestPath = manifest, InstanceLossWaitMs = 3500 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink { ManifestPath = manifest }.Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(
                Link(first),
                (null, OpenXrSourceState.NoRuntime, true),
                (null, OpenXrSourceState.NotSupported, true),
                Link(second));
            bench.Source.RetryIntervalMs = 2000;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, second.SessionsCreated);
            Assert.Equal(1, bench.ChoiceReads);
            Assert.Equal(4, bench.Asked.Count);
            // The first ask carries the choice, here the system default.
            Assert.Null(bench.Asked[0].Manifest);
            Assert.False(bench.Asked[0].Again);
            for (int i = 1; i < 4; i++)
            {
                Assert.Equal(manifest, bench.Asked[i].Manifest);
                Assert.True(bench.Asked[i].Again);
            }
            // After the loss time, and then at the cadence while it is away,
            // with the row at the line of a session being started.
            Assert.Equal(new long[] { 0, 3500, 5500, 7500 }, bench.Asked.ConvertAll(a => a.At));
            Assert.Equal(OpenXrSourceState.Connecting, bench.Asked[1].State);
            Assert.Equal(OpenXrSourceState.Connecting, bench.Asked[2].State);
            Assert.Equal(OpenXrSourceState.Connecting, bench.Asked[3].State);
        }

        /// <summary>
        /// A runtime that stays away is asked every interval, and the asks
        /// read the same thing. The log gets one line when the runtime is
        /// first found away, with what was read, one more when what is read
        /// changes, and one when the new instance stands, where a line per
        /// ask was 30 a minute for as long as an update took.
        /// </summary>
        [Fact]
        public void ARuntimeThatStaysAwayIsSaidWhenItIsFoundAwayAndWhenItIsBack()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, Quiet, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink().Session(1, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(first), Away, Away, Away, Away, Away, Link(second));
            // Asks 2 to 4 read one thing, asks 5 and 6 another.
            bench.Reads = n => n <= 4 ? "xrCreateInstance answered -51" : "the manifest is not on disk";

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(7, bench.Opens);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime retires its instance (scripted InstanceLossPending), asking for a new one in 1 ms",
                "OpenXR: the runtime is away (xrCreateInstance answered -51), asking for a new instance every 1 ms",
                "OpenXR: the runtime is still away at ask 4 (the manifest is not on disk)",
                "OpenXR: Scripted Runtime gave a new instance at ask 6",
                "OpenXR: Scripted Runtime ended the session (scripted Exiting), so the source stops (Ended)",
            }, bench.Log.FindAll(line => !line.Contains("head pose")));
        }

        /// <summary>The once is per wait. A runtime that goes away a second
        /// time, after a session ran in between, is said again.</summary>
        [Fact]
        public void ASecondTimeAwayIsSaidAgain()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, Quiet, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, Quiet, OpenXrSessionEnd.InstanceLossPending);
            var third = new ScriptedLink().Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(first), Away, Away, Link(second), Away, Link(third));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(2, bench.Lines("retires its instance").Count);
            Assert.Equal(2, bench.Lines("the runtime is away (scripted no instance)").Count);
            Assert.Single(bench.Lines("gave a new instance at ask 3"));
            Assert.Single(bench.Lines("gave a new instance at ask 2"));
        }

        /// <summary>A runtime that retires every new instance at once starts
        /// a new wait every interval with no pose in between. It is asked at
        /// the cadence, and each line of the wait is written once for the
        /// whole stretch, not once per interval.</summary>
        [Fact]
        public void ARuntimeThatRetiresEveryNewInstanceIsSaidOnce()
        {
            ScriptedLink Retiring() => new ScriptedLink { InstanceLossWaitMs = 0 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            var last = new ScriptedLink().Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(Retiring()), Link(Retiring()), Link(Retiring()), Link(Retiring()), Link(last));
            bench.Source.RetryIntervalMs = 300;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(new long[] { 0, 300, 600, 900, 1200 }, bench.Asked.ConvertAll(a => a.At));
            Assert.Single(bench.Lines("retires its instance"));
            Assert.Single(bench.Lines("gave a new instance"));
        }

        /// <summary>A runtime that is back at the first ask was never away,
        /// and nothing says it was. The wait that the retirement opened is
        /// closed by the line for the new instance.</summary>
        [Fact]
        public void ARuntimeBackAtTheFirstAskIsNotSaidToHaveBeenAway()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            var second = new ScriptedLink().Session(1, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(first), Link(second));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Single(bench.Lines("retires its instance"));
            Assert.Single(bench.Lines("gave a new instance at ask 1"));
            Assert.Empty(bench.Lines("away"));
        }

        /// <summary>A link that names no manifest cannot be asked again by
        /// one. The source still does not fall back on the runtime choice:
        /// the ask goes out with no manifest, marked as its own, and the
        /// opener refuses it
        /// (OpenXrSessionScriptedRuntimeTests.AnAskAgainWithNoManifestAsksNoRuntime).</summary>
        [Fact]
        public void ARetiredInstanceWithNoManifestDoesNotReadTheChoiceAgain()
        {
            var first = new ScriptedLink { ManifestPath = null, InstanceLossWaitMs = 1 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            using var bench = new Bench(Link(first), (null, OpenXrSourceState.Ended, false));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.ChoiceReads);
            Assert.Equal(2, bench.Asked.Count);
            Assert.Null(bench.Asked[1].Manifest);
            Assert.True(bench.Asked[1].Again);
        }

        /// <summary>After a retired instance, a runtime that refuses a new one
        /// for a reason other than being away ends the source. A session had
        /// stood on it, so the line is Ended, where the line for a runtime
        /// that cannot supply a session would speak of one that had just
        /// supplied it, and one line in the log says the source stopped and
        /// what was read.</summary>
        [Fact]
        public void ARuntimeThatRefusesANewInstanceForGoodEndsTheSource()
        {
            var first = new ScriptedLink { InstanceLossWaitMs = 1 }
                .Session(1, OpenXrSessionEnd.InstanceLossPending);
            using var bench = new Bench(
                Link(first),
                (null, OpenXrSourceState.NotSupported, false),
                Link(new ScriptedLink()));
            bench.Reads = _ => "xrCreateInstance answered -2";

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(2, bench.Opens);
            Assert.True(first.Disposed);
            Assert.Equal("OpenXR: no new instance was created at ask 1 (xrCreateInstance answered -2), so the source stops (Ended)",
                         bench.Log[^1]);
        }

        /// <summary>At the start, a runtime that gives no instance settles on
        /// the reason the opener gives, with one line that holds what it
        /// read.</summary>
        [Fact]
        public void ARuntimeThatGivesNoInstanceAtTheStartSettlesOnItsReason()
        {
            using var bench = new Bench((null, OpenXrSourceState.NoRuntime, true));
            bench.Reads = _ => @"the runtime manifest C:\gone.json is not on disk";

            bench.Run();

            // Away is for the asks after a retired instance. At the start
            // the answer is final.
            Assert.Equal(OpenXrSourceState.NoRuntime, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(new[]
            {
                @"OpenXR: no instance was created (the runtime manifest C:\gone.json is not on disk), so the source stops (NoRuntime)",
            }, bench.Log);
        }

        /// <summary>A runtime with no headset at the start may find one later
        /// (system.adoc, xrGetSystem), so the source keeps its instance and
        /// asks again, and the session starts once the headset is there. The
        /// wait is in the log once when it starts and once when it ends.</summary>
        [Fact]
        public void AHeadsetSwitchedOnAfterTheStartIsPickedUp()
        {
            var link = new ScriptedLink()
                .Creates(OpenXrSessionCreate.HeadsetUnavailable,
                         OpenXrSessionCreate.HeadsetUnavailable,
                         OpenXrSessionCreate.HeadsetUnavailable)
                .Session(1, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(1, link.SessionsCreated);
            Assert.Equal(4, link.SessionAsks);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(new[] { OpenXrSourceState.NoHeadset, OpenXrSourceState.Running }, bench.Shown);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime reports no headset (scripted no headset), asking again every 1 ms",
                "OpenXR: Scripted Runtime reports a headset at ask 4, and a session stands",
            }, bench.Lines("headset"));
        }

        /// <summary>An instance retired while it waits for a headset ends the
        /// source, and no new instance is asked for. A runtime with no
        /// session to end says it is closing that way, as SteamVR does, and
        /// a new instance could start it again.</summary>
        [Fact]
        public void AnInstanceRetiredWhileWaitingForTheHeadsetEndsTheSource()
        {
            var first = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable, InstanceLossWaitMs = 1 };
            first.Raise(Quiet);
            first.Raise(Quiet);
            first.Raise(OpenXrSessionEnd.InstanceLossPending);
            using var bench = new Bench(Link(first), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.True(first.Disposed);
            Assert.Equal("OpenXR: Scripted Runtime went away with no session standing (scripted InstanceLossPending), so the source stops (Ended)",
                         bench.Log[^1]);
        }

        /// <summary>The same holds between a lost session and the next one:
        /// no session stands, so a retired instance ends the source. The
        /// event is read as the wait opens, long before the interval is
        /// over.</summary>
        [Fact]
        public void AnInstanceRetiredAfterALostSessionEndsTheSource()
        {
            var first = new ScriptedLink()
                .Session(1, Quiet, OpenXrSessionEnd.SessionLost, OpenXrSessionEnd.InstanceLossPending);
            using var bench = new Bench(Link(first), Link(new ScriptedLink()));
            bench.Source.RetryIntervalMs = 60_000;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, first.SessionsCreated);
            Assert.Equal(1, first.SessionAsks);
            Assert.True(first.Disposed);
            Assert.True(bench.Now < 100, $"the source ended at {bench.Now} ms");
            Assert.Single(bench.Lines("no session standing"));
        }

        /// <summary>A source waiting for a headset reads its instance's
        /// events all through the wait, so a runtime that closes is let go
        /// of at the next read and not when the interval ends.</summary>
        [Fact]
        public void AWaitingSourceHearsTheRuntimeCloseDuringTheWait()
        {
            var link = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable };
            using var bench = new Bench(Link(link));
            bench.Source.RetryIntervalMs = 60_000;
            // Past the read that opens the wait, so only a read inside the
            // wait can find the event.
            bench.At(150, () => link.Raise(OpenXrSessionEnd.InstanceLossPending));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.True(link.Disposed);
            // The wait reads every 100 ms, so the read at 200 ms finds it.
            Assert.Equal(200, bench.Now);
            Assert.Equal(1, link.SessionAsks);
        }

        /// <summary>SteamVR's runtime library is the OpenVR client library
        /// that PadForge's OpenVR input loads too, and that library keeps one
        /// client per process. Its instance is not kept with no session, so
        /// a missing headset is final there, as it always was: one ask, and
        /// the instance is let go.</summary>
        [Fact]
        public void ARuntimeThatSharesItsOpenVrClientIsNotKeptWaitingForAHeadset()
        {
            var link = new ScriptedLink { SharesOpenVrClient = true, ThenCreate = OpenXrSessionCreate.HeadsetUnavailable };
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.NoHeadset, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(1, link.SessionAsks);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(0, bench.Now);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime reports no headset, and its instance is not kept waiting for one (scripted no headset), so the source stops (NoHeadset)",
            }, bench.Log);
        }

        /// <summary>On that runtime neither loss recovers: one recovery keeps
        /// an instance with no session and the other creates an instance on
        /// its own. Both end the source, as they always did there.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALossOnARuntimeThatSharesItsOpenVrClientEndsTheSource(bool sessionLost)
        {
            var end = sessionLost ? OpenXrSessionEnd.SessionLost : OpenXrSessionEnd.InstanceLossPending;
            var link = new ScriptedLink { SharesOpenVrClient = true, InstanceLossWaitMs = 1 }.Session(1, Quiet, end);
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionsCreated);
            Assert.Equal(1, link.SessionAsks);
            Assert.Equal(0, link.SessionsDestroyed);
            Assert.Equal($"OpenXR: Scripted Runtime ended the session, and its instance is not kept or created again on its own (scripted {end}), so the source stops (Ended)",
                         bench.Log[^1]);
        }

        /// <summary>The OpenVR client library is told by its file name, the
        /// name OpenVR's own loader asks for (openvr_api_public.cpp) and
        /// SteamVR's OpenXR manifest names. The real session reads it off
        /// its manifest's library
        /// (OpenXrSessionScriptedRuntimeTests.TheLinkKnowsWhetherItsLibraryIsAnOpenVrClient).</summary>
        [Fact]
        public void TheOpenVrClientLibraryIsToldByItsName()
        {
            Assert.True(OpenXrSession.IsOpenVrClientLibrary(@"C:\SteamVR\bin\vrclient_x64.dll"));
            Assert.True(OpenXrSession.IsOpenVrClientLibrary(@"D:\Steam\steamapps\common\SteamVR\bin\VRCLIENT_X64.DLL"));
            Assert.True(OpenXrSession.IsOpenVrClientLibrary(@"C:\SteamVR\bin\vrclient.dll"));
            Assert.False(OpenXrSession.IsOpenVrClientLibrary(@"C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.dll"));
            Assert.False(OpenXrSession.IsOpenVrClientLibrary(@"C:\Program Files\Monado\openxr_monado.dll"));
            Assert.False(OpenXrSession.IsOpenVrClientLibrary(null));
            Assert.False(OpenXrSession.IsOpenVrClientLibrary("  "));
        }

        /// <summary>The state a source settles on when it stops for good.
        /// It is the reason until a session has stood, and Ended after one
        /// has, and it is never the line of a source still asking.</summary>
        [Fact]
        public void TheStateASourceSettlesOnKeepsItsLineTrue()
        {
            foreach (OpenXrSourceState reason in Enum.GetValues<OpenXrSourceState>())
                Assert.Equal(OpenXrSourceState.Ended, OpenXrHeadPoseSource.SettledOn(reason, stood: true));

            Assert.Equal(OpenXrSourceState.Failed, OpenXrHeadPoseSource.SettledOn(OpenXrSourceState.NoHeadset, stood: false));
            Assert.Equal(OpenXrSourceState.Failed, OpenXrHeadPoseSource.SettledOn(OpenXrSourceState.Failed, stood: false));
            Assert.Equal(OpenXrSourceState.NotSupported, OpenXrHeadPoseSource.SettledOn(OpenXrSourceState.NotSupported, stood: false));
            Assert.Equal(OpenXrSourceState.NoRuntime, OpenXrHeadPoseSource.SettledOn(OpenXrSourceState.NoRuntime, stood: false));
            Assert.Equal(OpenXrSourceState.Ended, OpenXrHeadPoseSource.SettledOn(OpenXrSourceState.Ended, stood: false));
        }

        /// <summary>A session the runtime refuses at the start settles on the
        /// reason the link gives, and one line in the log says the source
        /// stopped, with what the link read.</summary>
        [Fact]
        public void ASessionRefusedForGoodSettlesOnItsReason()
        {
            var link = new ScriptedLink().Creates(OpenXrSessionCreate.Failed);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.NotSupported, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(0, link.SessionsCreated);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime gave no session at ask 1 (scripted refusal), so the source stops (NotSupported)",
            }, bench.Log);
        }

        /// <summary>A session ran, the runtime lost it, and the next one is
        /// refused for good. The line for a runtime that cannot supply a
        /// session would speak of one that had just supplied it, so the
        /// source reads Ended, the runtime having ended that session, and the
        /// log says it stopped.</summary>
        [Theory]
        [InlineData(OpenXrSourceState.NotSupported)]
        [InlineData(OpenXrSourceState.Failed)]
        public void ARecoveryThatStopsAfterALostSessionReadsEnded(OpenXrSourceState reason)
        {
            var link = new ScriptedLink { FailsAs = reason }
                .Creates(OpenXrSessionCreate.Created, OpenXrSessionCreate.Failed)
                .Session(1, OpenXrSessionEnd.SessionLost);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(2, link.SessionAsks);
            Assert.Equal(1, link.SessionsCreated);
            Assert.Equal(1, bench.Opens);
            Assert.True(link.Disposed);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime lost the session (scripted SessionLost), so a new one is asked for every 1 ms",
                "OpenXR: Scripted Runtime gave no session at ask 1 (scripted refusal), so the source stops (Ended)",
            }, bench.Log);
        }

        /// <summary>A source that has waited for a headset and then stops
        /// for good never keeps the line of the wait: that line says the
        /// runtime reports no headset, which a row still asking shows too.
        /// It settles on the failure, and the log says it stopped.</summary>
        [Theory]
        [InlineData(OpenXrSourceState.Failed, OpenXrSourceState.Failed)]
        [InlineData(OpenXrSourceState.NoHeadset, OpenXrSourceState.Failed)]
        [InlineData(OpenXrSourceState.NotSupported, OpenXrSourceState.NotSupported)]
        [InlineData(OpenXrSourceState.Ended, OpenXrSourceState.Ended)]
        public void AHeadsetWaitThatStopsDoesNotKeepTheWaitingLine(OpenXrSourceState reason, OpenXrSourceState shown)
        {
            var link = new ScriptedLink { FailsAs = reason }.Creates(
                OpenXrSessionCreate.HeadsetUnavailable,
                OpenXrSessionCreate.HeadsetUnavailable,
                OpenXrSessionCreate.Failed);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(shown, bench.Source.State);
            Assert.Equal(3, link.SessionAsks);
            Assert.True(link.Disposed);
            Assert.Equal(new[]
            {
                "OpenXR: Scripted Runtime reports no headset (scripted no headset), asking again every 1 ms",
                $"OpenXR: Scripted Runtime gave no session at ask 3 (scripted refusal), so the source stops ({shown})",
            }, bench.Log);
        }

        /// <summary>A session the runtime ends leaves one line that says
        /// what the pump read.</summary>
        [Theory]
        [InlineData(nameof(OpenXrSessionEnd.Exiting))]
        [InlineData(nameof(OpenXrSessionEnd.InstanceLost))]
        [InlineData(nameof(OpenXrSessionEnd.PollFailed))]
        public void ASessionTheRuntimeEndsIsSaidInTheLog(string end)
        {
            var link = new ScriptedLink().Session(1, Enum.Parse<OpenXrSessionEnd>(end));
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(new[]
            {
                $"OpenXR: Scripted Runtime ended the session (scripted {end}), so the source stops (Ended)",
            }, bench.Log);
        }

        /// <summary>A session the runtime refuses to begin never runs, so
        /// the source lets it go and stops: as Failed for the first session,
        /// and as Ended when an earlier one ran and was lost.</summary>
        [Theory]
        [InlineData(false, OpenXrSourceState.Failed)]
        [InlineData(true, OpenXrSourceState.Ended)]
        public void ASessionThatIsRefusedItsBeginStopsTheSource(bool afterALostSession, OpenXrSourceState shown)
        {
            var link = new ScriptedLink();
            if (afterALostSession)
                link.Session(1, Quiet, OpenXrSessionEnd.SessionLost).Session(2, OpenXrSessionEnd.BeginRefused);
            else
                link.Session(1, OpenXrSessionEnd.BeginRefused);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(shown, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(1, bench.Opens);
            Assert.Equal($"OpenXR: Scripted Runtime refused to begin the session (scripted BeginRefused), so the source stops ({shown})",
                         bench.Log[^1]);
        }

        /// <summary>A source waiting on the runtime stops at once: its waits
        /// sleep in short slices and look at the stop between them, so
        /// Stop's join is never outlived.</summary>
        [Fact]
        public void AWaitingSourceStopsPromptly()
        {
            var link = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable };
            using var bench = new Bench(Link(link));
            bench.Source.RetryIntervalMs = 60_000;
            bench.StopAt(300);

            bench.Run();

            // The sleep that reached 300 ms was the last one, and no ask
            // followed it.
            Assert.Equal(300, bench.Now);
            Assert.Equal(1, link.SessionAsks);
            Assert.True(bench.LongestSleep <= 50, $"one sleep lasted {bench.LongestSleep} ms");
            Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
            Assert.True(link.Disposed);
        }

        /// <summary>A session with controllers shows them while it runs, and
        /// a source that is stopped shows none.</summary>
        [Fact]
        public void AStoppedSourceShowsNoControllers()
        {
            var link = new ScriptedLink { Controllers = true };
            using var bench = new Bench(Link(link));
            bool whileRunning = false;
            bench.At(20, () => whileRunning = bench.Source.HasControllers);
            bench.StopAt(60);

            bench.Run();

            Assert.True(whileRunning);
            Assert.False(bench.Source.HasControllers);
            Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
        }

        /// <summary>
        /// Stop gives up on a thread that is inside a slow native call, and
        /// that thread ends by itself when the call returns. What it writes
        /// on the way out is dropped: it used to land after the state Stop
        /// had set, and left the stopped source reading as one that waits
        /// for a headset, or as one that starts a session.
        ///
        /// <para>The loop runs on a thread of the test's own and is held on
        /// a gate inside the ask, so Stop has no thread to join, which is
        /// where a join that timed out leaves it.</para>
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WhatTheLoopWritesAfterStopGaveUpOnItIsDropped(bool insideTheAskForAnInstance)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            void Hold()
            {
                entered.Set();
                release.Wait();
            }

            ScriptedLink link;
            Bench bench;
            if (insideTheAskForAnInstance)
            {
                // The ask for a new instance after a retired one answers
                // that the runtime is away, which shows Connecting.
                link = new ScriptedLink { Controllers = true }.Session(1, OpenXrSessionEnd.InstanceLossPending);
                bench = new Bench(Link(link), Away);
                bench.OnOpen = n => { if (n == 2) Hold(); };
            }
            else
            {
                // The ask for a session answers that the headset is away,
                // which shows NoHeadset.
                link = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable, OnSessionAsk = Hold };
                bench = new Bench(Link(link));
            }

            using (bench)
            {
                var loop = new Thread(bench.Run) { IsBackground = true };
                loop.Start();
                Assert.True(entered.Wait(TimeSpan.FromMinutes(2)), "the loop never reached the ask");

                bench.Source.Stop();
                Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
                Assert.False(bench.Source.HasControllers);

                release.Set();
                Assert.True(loop.Join(TimeSpan.FromMinutes(2)), "the loop never ended");

                Assert.Equal(OpenXrSourceState.Stopped, bench.Source.State);
                Assert.False(bench.Source.HasControllers);
                Assert.True(link.Disposed);
            }
        }

        /// <summary>
        /// Start runs the loop on a thread of its own, a background thread
        /// that carries the source's name, with the row at the line of a
        /// session being started from the first moment. Stop waits for that
        /// thread to end before it returns, so the link is let go and the
        /// instance destroyed by then. A switch off that arrives while an
        /// ask is under way is honored when the ask returns: what the ask
        /// created is closed again, with a line that names it.
        ///
        /// <para>The loop is held inside the ask until the test's thread
        /// is blocked. Between letting the loop go on and reading the
        /// outcome, the one place that thread can block is Stop's wait for
        /// the loop's thread, so the loop ends inside that wait and a Stop
        /// that did not wait would return with the loop still held. Stop
        /// is given two minutes to wait in place of its two seconds, so no
        /// length of time decides anything here.</para>
        /// </summary>
        [Theory]
        [InlineData(true, "OpenXR: instance created after the feature was turned off, closing it")]
        [InlineData(false, "OpenXR: session created after the feature was turned off, closing it")]
        public void StartRunsTheLoopOnAThreadOfItsOwnAndStopWaitsForIt(bool insideTheAskForAnInstance, string line)
        {
            var test = Thread.CurrentThread;
            using var entered = new ManualResetEventSlim();
            using var stopping = new ManualResetEventSlim();
            using var giveUp = new ManualResetEventSlim();
            Thread loop = null;
            string name = null;
            bool background = false;
            void Hold()
            {
                loop = Thread.CurrentThread;
                name = loop.Name;
                background = loop.IsBackground;
                entered.Set();
                var spin = new SpinWait();
                while (!stopping.IsSet && !giveUp.IsSet) spin.SpinOnce();
                while ((test.ThreadState & ThreadState.WaitSleepJoin) == 0 && !giveUp.IsSet) spin.SpinOnce();
            }

            var link = new ScriptedLink();
            using var bench = new Bench(Link(link));
            bench.Source.StopJoinMs = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;
            if (insideTheAskForAnInstance) bench.OnOpen = _ => Hold();
            else link.OnSessionAsk = Hold;

            bool disposedAtReturn, aliveAtReturn;
            OpenXrSourceState stateAtReturn;
            try
            {
                bench.Source.Start();
                Assert.True(entered.Wait(TimeSpan.FromMinutes(2)), "Start never ran the loop");

                stopping.Set();
                bench.Source.Stop();
                disposedAtReturn = link.Disposed;
                aliveAtReturn = loop.IsAlive;
                stateAtReturn = bench.Source.State;
            }
            finally { giveUp.Set(); }

            Assert.NotSame(test, loop);
            Assert.Equal("PadForge.OpenXrHeadPose", name);
            Assert.True(background);
            Assert.Equal(OpenXrSourceState.Connecting, bench.Asked[0].State);
            Assert.True(disposedAtReturn, "Stop returned while the loop's thread still held its link");
            Assert.False(aliveAtReturn);
            Assert.Equal(OpenXrSourceState.Stopped, stateAtReturn);
            Assert.Equal(insideTheAskForAnInstance ? 0 : 1, link.SessionsCreated);
            Assert.Equal(new[] { line }, bench.Log);
        }

        /// <summary>What a source runs on in production, which every other
        /// test here replaces: the machine's millisecond clock, the
        /// thread's own sleep, a retry every two seconds, a read of the
        /// instance's events every 100 ms while it waits, and a wait of two
        /// seconds for its thread when it is stopped.</summary>
        [Fact]
        public void ASourceRunsOnTheMachinesClockUntilATestHandsItAnother()
        {
            using var source = new OpenXrHeadPoseSource(() => null, _ => { });

            long before = Environment.TickCount64;
            long read = source.Ticks();
            long after = Environment.TickCount64;
            Assert.InRange(read, before, after);

            Assert.Equal(typeof(Thread).GetMethod(nameof(Thread.Sleep), new[] { typeof(int) }), source.Sleep.Method);
            Assert.Equal(2000, source.RetryIntervalMs);
            Assert.Equal(100, source.WaitingPumpIntervalMs);
            Assert.Equal(2000, source.StopJoinMs);
            Assert.Equal(8, OpenXrHeadPoseSource.SampleIntervalMs);
        }

        /// <summary>A running session is sampled every 8 ms: the loop
        /// sleeps that long after each sample, and three samples take 24 ms
        /// on the script's clock.</summary>
        [Fact]
        public void ARunningSessionIsSampledAtItsInterval()
        {
            var link = new ScriptedLink().Session(1, Quiet, Quiet, Quiet, OpenXrSessionEnd.Exiting);
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(3, bench.Source.SampleCount);
            Assert.Equal(24, bench.Now);
            Assert.Equal(8, bench.LongestSleep);
        }

        /// <summary>A loop that throws stops the source as Failed, with
        /// what was thrown in the log, and lets its link go. Without the
        /// state the row would stay on the line of a session being
        /// started.</summary>
        [Fact]
        public void ALoopThatThrowsStopsAsFailedWithWhatItThrew()
        {
            var link = new ScriptedLink { OnSessionAsk = () => throw new InvalidOperationException("the ask broke") };
            using var bench = new Bench(Link(link));

            bench.Run();

            Assert.Equal(OpenXrSourceState.Failed, bench.Source.State);
            Assert.True(link.Disposed);
            Assert.Equal(new[] { "OpenXR: head pose source stopped: the ask broke" }, bench.Log);
        }

        /// <summary>A poll that fails while the source waits for a headset
        /// ends it like the instance-loss event does there: the runtime is
        /// gone, no session stood, and nothing asks for a new instance.</summary>
        [Theory]
        [InlineData(nameof(OpenXrSessionEnd.InstanceLost))]
        [InlineData(nameof(OpenXrSessionEnd.PollFailed))]
        public void APollThatFailsWhileWaitingForTheHeadsetEndsTheSource(string end)
        {
            var link = new ScriptedLink { ThenCreate = OpenXrSessionCreate.HeadsetUnavailable };
            link.Raise(Quiet);
            link.Raise(Enum.Parse<OpenXrSessionEnd>(end));
            using var bench = new Bench(Link(link), Link(new ScriptedLink()));
            bench.Source.RetryIntervalMs = 60_000;

            bench.Run();

            Assert.Equal(OpenXrSourceState.Ended, bench.Source.State);
            Assert.Equal(1, bench.Opens);
            Assert.Equal(1, link.SessionAsks);
            Assert.True(link.Disposed);
            Assert.Equal(100, bench.Now);
            Assert.Equal($"OpenXR: Scripted Runtime went away with no session standing (scripted {end}), so the source stops (Ended)",
                         bench.Log[^1]);
        }
    }
}
