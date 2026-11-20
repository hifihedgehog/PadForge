using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A HIDMaestro submit on a profile with a protocol host can wait for the
    /// driver to deliver the host's reply, up to 200 ms a time
    /// (HIDMaestro eb2f2ce0, HMController.AwaitHostReply and
    /// ProtocolHost.AwaitLocked, ProtocolHost.DeliveryTimeoutMs), and the
    /// host's own clock holds the submit lock while it waits
    /// (ProtocolHost.Tick). Step 5 submits on the poll thread, so the frame
    /// goes to a submit lane there: the poll thread hands it over and goes
    /// on, and the lane's thread sends the newest frame in order.
    /// </summary>
    public class HMaestroSubmitLaneTests
    {
        private static HMProfile Profile(string id) =>
            HMaestroProfileCatalog.AllProfiles.First(p => p.Id == id);

        /// <summary>A connected controller with no device: its frames go to
        /// <paramref name="target"/>.</summary>
        private static HMaestroVirtualController Connected(string profileId, IHMaestroSubmitTarget target)
        {
            var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(HMContext));
            var vc = new HMaestroVirtualController(shell, Profile(profileId), VirtualControllerType.Extended)
            {
                CreateControllerSeam = (p, key) => null,
                SubmitTargetSeam = () => target,
            };
            vc.Connect();
            return vc;
        }

        /// <summary>Records each frame and the thread it arrived on. While
        /// held, a submit waits inside, as HIDMaestro's does for a reply the
        /// driver has not delivered.</summary>
        private sealed class HeldTarget : IHMaestroSubmitTarget
        {
            public readonly ManualResetEventSlim Gate = new(true);
            public readonly SemaphoreSlim Entered = new(0);
            public readonly List<byte[]> Raw = new();
            public readonly List<HMGamepadState> States = new();
            public readonly List<int> Threads = new();
            public Exception Throw;

            private void Arrive()
            {
                Entered.Release();
                Gate.Wait();
                if (Throw is { } ex) { Throw = null; throw ex; }
            }

            public int Count { get { lock (this) return Raw.Count + States.Count; } }

            public void SubmitState(in HMGamepadState state)
            {
                Arrive();
                var copy = state;
                copy.Axes = state.Axes == null ? null : new Dictionary<HMAxis, float>(state.Axes);
                lock (this) { States.Add(copy); Threads.Add(Environment.CurrentManagedThreadId); }
            }

            public void SubmitRawReport(ReadOnlySpan<byte> report)
            {
                Arrive();
                var copy = report.ToArray();
                lock (this) { Raw.Add(copy); Threads.Add(Environment.CurrentManagedThreadId); }
            }

            public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) => SubmitRawReport(report);

            public bool WaitForCount(int n, int ms)
            {
                var until = Environment.TickCount64 + ms;
                while (Count < n)
                {
                    if (Environment.TickCount64 > until) return false;
                    Thread.Sleep(1);
                }
                return true;
            }
        }

        /// <summary>A thread standing in for the poll thread.</summary>
        private sealed class PollThread
        {
            private readonly Thread _thread;
            private Exception _fault;

            public PollThread(Action body)
            {
                _thread = new Thread(() =>
                {
                    try { body(); }
                    catch (Exception ex) { _fault = ex; }
                }) { IsBackground = true, Name = "test poll thread" };
                _thread.Start();
            }

            public void AssertReturnedWithin(int ms, string message)
            {
                Assert.True(_thread.Join(ms), message);
                if (_fault != null) throw new Xunit.Sdk.XunitException("the poll thread threw: " + _fault);
            }
        }

        private static PollThread OnPollThread(Action body) => new(body);

        private static byte[] SInputFrame(byte marker)
        {
            var f = new byte[SInputReportPacker.ReportSize - 1];
            f[0] = marker;
            f[^1] = marker;
            return f;
        }

        /// <summary>The sibling set: every profile with a protocol of its own
        /// can wait in its submit, SubmitState included, since the host's
        /// clock holds the submit lock while it waits (ProtocolHost.Tick).
        /// A profile with no protocol cannot, and keeps its submit on the
        /// poll thread.</summary>
        [Theory]
        [InlineData("sinput", true)]
        [InlineData("joypad-steam-input", true)]
        [InlineData("switch-pro", true)]
        [InlineData("switch-pro-usb", true)]
        [InlineData("dualshock-3-full", true)]
        [InlineData("xbox-one-s-full", true)]
        [InlineData("xbox-360-wired", false)]
        [InlineData("dualsense", false)]
        [InlineData(HMaestroProfileCatalog.CustomProfileId, false)]
        public void EveryProfileWithAProtocolTakesTheLane(string id, bool canWait)
        {
            Assert.Equal(canWait, HMaestroSubmitLane.SubmitCanWait(Profile(id)));
        }

        /// <summary>A profile with no protocol sends from the poll thread, as
        /// it always has.</summary>
        [Fact]
        public void AProfileWithNoProtocolSubmitsOnThePollThread()
        {
            var target = new HeldTarget();
            var vc = Connected("xbox-360-wired", target);
            try
            {
                int pollId = -1;
                var poll = OnPollThread(() =>
                {
                    pollId = Environment.CurrentManagedThreadId;
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 1234 });
                });
                poll.AssertReturnedWithin(5000, "the submit did not return");
                lock (target)
                {
                    Assert.Single(target.States);
                    Assert.Equal(pollId, target.Threads[0]);
                }
            }
            finally { vc.Disconnect(); }
        }

        /// <summary>The state path of the sibling set. The poll thread
        /// rewrites the controller's axis dictionary on every pass, so the
        /// lane keeps its own copy of a frame's axes: the frame in flight
        /// carries the stick it was handed with, not the next pass's.</summary>
        [Fact]
        public void AHeldStateSubmitKeepsItsOwnAxes()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Connected("joypad-steam-input", target);
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = short.MaxValue });
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = short.MinValue });
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 0 });
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Assert.True(target.WaitForCount(2, 5000), "the newest frame was not sent once the submit let go");
                Thread.Sleep(50);
                lock (target)
                {
                    Assert.Equal(2, target.States.Count);
                    // Full right on the first frame, centered on the newest.
                    Assert.Contains(1f, target.States[0].Axes.Values);
                    Assert.DoesNotContain(1f, target.States[1].Axes.Values);
                }
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }

        /// <summary>A submit that throws on the lane's thread throws again on
        /// the poll thread, from the next hand-over, where Step 5 reports it
        /// as it reported a submit that threw there.</summary>
        [Fact]
        public void ASubmitThatThrowsOnTheLaneThrowsFromTheNextHandOver()
        {
            var target = new HeldTarget { Throw = new InvalidOperationException("refused") };
            var vc = Connected("sinput", target);
            try
            {
                Exception seen = null;
                var poll = OnPollThread(() =>
                {
                    long until = Environment.TickCount64 + 5000;
                    while (seen == null && Environment.TickCount64 < until)
                    {
                        try { vc.SubmitRawReport(SInputFrame(1)); }
                        catch (Exception ex) { seen = ex; }
                        Thread.Sleep(1);
                    }
                });
                poll.AssertReturnedWithin(10000, "the poll thread did not return");
                Assert.IsType<InvalidOperationException>(seen);
                Assert.Equal("refused", seen.Message);
            }
            finally { vc.Disconnect(); }
        }

        /// <summary>The finding: on the SInput persona the submit held inside
        /// HIDMaestro held the poll thread with it. The poll thread now hands
        /// the frame over and returns while the submit is held, and once it
        /// lets go the lane sends the newest frame after the one in flight,
        /// never an older one after a newer.</summary>
        [Fact]
        public void TheSInputSubmitReturnsWhileTheSdkSubmitIsHeld()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Connected("sinput", target);
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitRawReport(SInputFrame(1));
                    // The first frame is inside the held submit.
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    vc.SubmitRawReport(SInputFrame(2));
                    vc.SubmitRawReport(SInputFrame(3));
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Assert.True(target.WaitForCount(2, 5000), "the newest frame was not sent once the submit let go");
                Thread.Sleep(50);
                lock (target)
                {
                    Assert.Equal(2, target.Raw.Count);
                    Assert.Equal(SInputFrame(1), target.Raw[0]);
                    Assert.Equal(SInputFrame(3), target.Raw[1]);
                }
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }
    }
}
