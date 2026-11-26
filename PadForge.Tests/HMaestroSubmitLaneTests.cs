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

        /// <summary>A controller HIDMaestro delivers every report of, an
        /// "other" profile whose descriptor is a mouse or a keyboard among
        /// them (ControllerProfile.DeliversEveryReport), waits in each submit
        /// for the driver to deliver the frame before, up to 200 ms
        /// (HMController.AwaitPreviousDeliveryCore). Its frames go out on the
        /// lane as well, whatever its protocol, and in order.</summary>
        [Fact]
        public void AControllerThatDeliversEveryReportSubmitsOnTheLane()
        {
            var target = new HeldTarget();
            var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(HMContext));
            var vc = new HMaestroVirtualController(shell, Profile("xbox-360-wired"), VirtualControllerType.Extended)
            {
                CreateControllerSeam = (p, key) => null,
                SubmitTargetSeam = () => target,
                DeliversEveryReportSeam = () => true,
            };
            vc.Connect();
            try
            {
                int pollId = -1;
                var poll = OnPollThread(() =>
                {
                    pollId = Environment.CurrentManagedThreadId;
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 1234 });
                });
                poll.AssertReturnedWithin(5000, "the submit did not return");
                Assert.True(target.WaitForCount(1, 5000), "the frame was never sent");
                lock (target) Assert.NotEqual(pollId, target.Threads[0]);
            }
            finally { vc.Disconnect(); }
        }

        /// <summary>A controller HIDMaestro delivers every report of with a
        /// held submit, standing for the driver's delivery wait
        /// (HMController.AwaitPreviousDeliveryCore).</summary>
        private static HMaestroVirtualController Delivering(IHMaestroSubmitTarget target, string profileId = "xbox-360-wired")
        {
            var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(HMContext));
            var vc = new HMaestroVirtualController(shell, Profile(profileId), VirtualControllerType.Extended)
            {
                CreateControllerSeam = (p, key) => null,
                SubmitTargetSeam = () => target,
                DeliversEveryReportSeam = () => true,
            };
            vc.Connect();
            return vc;
        }

        /// <summary>HIDMaestro sends every report of such a controller to HID
        /// class in order (HMController.DeliversEveryReport, HMController.cs
        /// 1802-1810 at v2.4.0), so a button that goes down and up while the
        /// lane waits on the frame before still reaches the device, down and
        /// then up, each frame with its own stick.</summary>
        [Fact]
        public void ADownAndAnUpInsideOneDeliveryWaitBothReachTheDevice()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Delivering(target);
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 1000 });
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    vc.SubmitGamepadState(new Gamepad { Buttons = Gamepad.A, ThumbLX = 2000 });
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 3000 });
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Assert.True(target.WaitForCount(3, 5000), "a frame handed over during the wait never reached the device");
                Thread.Sleep(50);
                lock (target)
                {
                    Assert.Equal(3, target.States.Count);
                    Assert.Equal(new[] { false, true, false },
                        target.States.Select(st => (st.Buttons & HMButton.A) != 0).ToArray());
                    var sticks = target.States.Select(st => st.Axes.Values.Max()).ToArray();
                    Assert.True(sticks[0] < sticks[1] && sticks[1] < sticks[2], "a frame went out with another frame's stick");
                }
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }

        /// <summary>The device reports once a millisecond at most
        /// (HMController.s_minFrameTicks), and a poll past 1000 a second
        /// moves a stick on every pass. A frame that changes no button, hat
        /// or finger takes the place of the frame waiting at the queue's end,
        /// as a gamepad's state does, so the queue holds the edges alone and
        /// the device gets the newest stick.</summary>
        [Fact]
        public void AStickThatMovesWhileTheDeviceWaitsSendsItsNewestPlace()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Delivering(target);
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 100 });
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    for (int i = 1; i <= 50; i++) vc.SubmitGamepadState(new Gamepad { ThumbLX = (short)(100 + i * 100) });
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Assert.True(target.WaitForCount(2, 5000), "the newest stick never reached the device");
                Thread.Sleep(50);
                lock (target) Assert.Equal(2, target.States.Count);
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }

        /// <summary>A descriptor whose input is relative, a mouse's X and Y
        /// (Input with the Relative flag, HID 1.11 section 6.2.2.5), is read
        /// by the host one report at a time as a movement: HIDMaestro packs
        /// each state's axes into those fields as they are
        /// (HidReportBuilder.BuildReportInto keeps no Relative flag). Two
        /// motion-only states inside one wait are two movements, and both
        /// reach the device in order. An imported mouse is such an Extended
        /// profile, "other" with a mouse collection (HMDeviceExtractor). The
        /// shipped mouse profile here carries the same descriptor.</summary>
        [Fact]
        public void TwoMovementsInsideOneWaitOnARelativeDescriptorBothReachTheDevice()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Delivering(target, "logitech-g502-hero");
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 1000 });
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 2000 });
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 3000 });
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Assert.True(target.WaitForCount(3, 5000), "a movement handed over during the wait never reached the device");
                Thread.Sleep(50);
                lock (target) Assert.Equal(3, target.States.Count);
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }

        /// <summary>The check behind it: a mouse's descriptor declares
        /// relative input, a gamepad's does not, and a constant Input with
        /// the Relative bit set is padding, not a movement.</summary>
        [Fact]
        public void RelativeInputIsReadFromTheDescriptor()
        {
            Assert.True(HMaestroProfileCatalog.DeclaresRelativeInput(Profile("logitech-g502-hero")));
            Assert.False(HMaestroProfileCatalog.DeclaresRelativeInput(Profile("xbox-360-wired")));
            // Input (Data, Var, Rel), then Input (Const, Var, Rel).
            Assert.True(HMaestroProfileCatalog.DeclaresRelativeInput(new byte[] { 0x81, 0x06 }));
            Assert.False(HMaestroProfileCatalog.DeclaresRelativeInput(new byte[] { 0x81, 0x07 }));
            Assert.False(HMaestroProfileCatalog.DeclaresRelativeInput(new byte[] { 0x81, 0x02 }));
            Assert.False(HMaestroProfileCatalog.DeclaresRelativeInput((byte[])null));
        }

        /// <summary>The queue is bounded as KbmHidKeyboard's is
        /// (MaxQueuedFrames): while Windows reads nothing, a backlog past it
        /// collapses to the newest frame, and the poll thread never waits.</summary>
        [Fact]
        public void ABacklogPastTheBoundCollapsesToTheNewestFrame()
        {
            var target = new HeldTarget();
            target.Gate.Reset();
            var vc = Delivering(target);
            int handed = KbmHidKeyboard.MaxQueuedFrames * 2 + 1;
            try
            {
                var poll = OnPollThread(() =>
                {
                    vc.SubmitGamepadState(new Gamepad { ThumbLX = 1 });
                    if (!target.Entered.Wait(5000)) throw new TimeoutException("the first frame never reached the submit");
                    for (int i = 0; i < handed; i++)
                        vc.SubmitGamepadState(new Gamepad { Buttons = (i & 1) == 0 ? Gamepad.A : (ushort)0, ThumbLX = (short)(2 + i) });
                });
                poll.AssertReturnedWithin(2000, "the poll thread waited on the held submit");

                target.Gate.Set();
                Thread.Sleep(500);
                lock (target)
                {
                    Assert.InRange(target.States.Count, 2, KbmHidKeyboard.MaxQueuedFrames + 1);
                    // The newest frame went out last: the last one handed over
                    // was a press.
                    Assert.True((target.States[^1].Buttons & HMButton.A) != 0);
                }
            }
            finally
            {
                target.Gate.Set();
                vc.Disconnect();
            }
        }

        /// <summary>Refuses every frame as HIDMaestro refuses one the driver
        /// serving the controller cannot carry (SharedMemoryIO.WriteInputFrame,
        /// ThrowExtendedTooLong).</summary>
        private sealed class RefusingTarget : IHMaestroSubmitTarget
        {
            public int Calls;

            private void Refuse()
            {
                Interlocked.Increment(ref Calls);
                throw new NotSupportedException("This input report is 120 bytes. The driver serving this controller carries a report of at most 80 bytes whole.");
            }

            public void SubmitState(in HMGamepadState state) => Refuse();
            public void SubmitRawReport(ReadOnlySpan<byte> report) => Refuse();
            public void SubmitRawExtendedReport(ReadOnlySpan<byte> report) => Refuse();
        }

        /// <summary>A frame the driver refuses is refused for the life of
        /// the controller, so Step 5 hears of it once, with the preset named,
        /// and nothing more is handed to HIDMaestro until the controller is
        /// made again.</summary>
        [Fact]
        public void AFrameTheDriverRefusesIsReportedOnceAndStopsTheSubmits()
        {
            var target = new RefusingTarget();
            var vc = Connected("xbox-360-wired", target);
            try
            {
                int throws = 0;
                NotSupportedException first = null;
                for (int i = 0; i < 100; i++)
                {
                    try { vc.SubmitGamepadState(new Gamepad { ThumbLX = (short)(i * 100 + 1) }); }
                    catch (NotSupportedException ex) { throws++; first ??= ex; }
                }
                Assert.Equal(1, throws);
                Assert.Equal(1, Volatile.Read(ref target.Calls));
                Assert.Contains("xbox-360-wired", first.Message, StringComparison.Ordinal);
                Assert.Contains("120 bytes", first.Message, StringComparison.Ordinal);
                Assert.True(vc.SubmitsRefused);
            }
            finally { vc.Disconnect(); }
        }

        /// <summary>A lane's dispose closes its wake event once its thread
        /// has stopped (the twin of KbmHidKeyboard.Dispose), and a hand-over
        /// that read the lane before the controller let go of it does not
        /// throw on the closed event.</summary>
        [Fact]
        public void ADisposedLaneClosesItsWakeEvent()
        {
            var lane = new HMaestroSubmitLane(new HeldTarget(), "test");
            var wake = (AutoResetEvent)typeof(HMaestroSubmitLane)
                .GetField("_wake", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(lane);
            lane.Dispose();
            Assert.True(wake.SafeWaitHandle.IsClosed);
            lane.SubmitRawReport(new byte[] { 1, 2, 3 });
            lane.SubmitState(new HMGamepadState());
        }
    }
}
