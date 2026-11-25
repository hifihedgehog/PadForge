using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the pipe does for scripts (#600 items 21 to 24): controls by the
    /// names scripts use, one change per control per pass, timed steps on
    /// PadForge's clock, set, named subscriptions that start with the whole
    /// state, claims, lines on both sides of an axis, the 250 ms lease that
    /// comes back at the next line, a silent script's devices going offline,
    /// and the wake sent again. Every pipe has a name of the test's own and
    /// every pass is the test's.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ScriptPipeTests
    {
        private const string Pad = "{\"buttons\":[\"A\",\"B\"],\"axes\":[{\"name\":\"Left X\"},{\"index\":2,\"name\":\"Gas\",\"rest\":0}],"
            + "\"hats\":[\"View\"],\"feedback\":[\"rumble\",\"trigfx\"]}";

        /// <summary>One pass as Step 2 runs it for these devices: the steps
        /// due land, each device is read, holds and claims go over what
        /// mappings read, and the watch hears it.</summary>
        internal static void Pass(AutomationPipeRig rig, params UserDevice[] devices)
        {
            ControlHolds.BeginPass();
            foreach (var ud in devices)
            {
                var read = ud.Device != null ? ud.Device.GetCurrentState() : ud.ReadInputState;
                // The program device's buffer is pooled: a copy is kept.
                read = read.Clone();
                ud.ReadInputState = read;
                ud.InputState = ControlHolds.Apply(ud.InstanceGuid, read).Clone();
            }
            rig.Pass(devices);
        }

        private static UserDevice RowOf(Guid guid)
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                return SettingsManager.UserDevices.Items.First(u => u.InstanceGuid == guid);
        }

        [Fact]
        public void ControlsAnswerToTheNamesScriptsUse_InEveryVerb_AndCapsSaysThem()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Names", " units=plain lease=10000");
            var caps = client.Ask("caps " + id);
            Assert.Contains(caps, c => c.StartsWith("button index=0 ", StringComparison.Ordinal) && c.EndsWith(" script=A", StringComparison.Ordinal));
            Assert.Contains(caps, c => c.StartsWith("axis index=0 ", StringComparison.Ordinal) && c.EndsWith(" script=LeftX", StringComparison.Ordinal));
            Assert.Contains(caps, c => c.StartsWith("axis index=5 ", StringComparison.Ordinal) && c.EndsWith(" script=RightTrigger", StringComparison.Ordinal));
            Assert.Contains(caps, c => c.StartsWith("hat index=0 ", StringComparison.Ordinal) && c.EndsWith(" script=DPad", StringComparison.Ordinal));

            Assert.Equal("ok", client.Final("own " + id + " B=1 leftx=-50% RightTrigger=100% DPad=Up"));
            var holds = ControlHolds.Current[pad.InstanceGuid];
            Assert.Contains(holds.Buttons, b => b.Index == 1 && b.Hold.Value == 1);
            Assert.Contains(holds.Axes, a => a.Index == 0 && a.Hold.Value == 16384);
            Assert.Contains(holds.Axes, a => a.Index == 5 && a.Hold.Value == 65535);
            Assert.Contains(holds.Hats, h => h.Index == 0 && h.Hold.Value == 0);
            // The numbered names answer too.
            Assert.Equal("ok", client.Final("own " + id + " Button3=1 Axis1=25%"));
            Assert.Contains(ControlHolds.Current[pad.InstanceGuid].Buttons, b => b.Index == 3);
            Assert.Equal("ok", client.Final("rel " + id + " B LeftX Button3"));
            Assert.DoesNotContain(ControlHolds.Current[pad.InstanceGuid].Buttons, b => b.Index == 1 || b.Index == 3);
            Assert.StartsWith("err bad-argument ", client.Final("own " + id + " Nothing=1"));
            Assert.StartsWith("err bad-argument ", client.Final("rel " + id + " Nothing"));

            // A program device by the names it declared.
            string own = client.Final("declare key=n name=\"Named\" " + Pad).Substring(6);
            Assert.Equal("@1 ok", client.Final("@1 s " + own + " A=1 LeftX=50% Gas=100% View=Right"));
            var state = rig.Host.Sessions.Devices.Find(own).Device.PeekState();
            Assert.True(state.Buttons[0]);
            Assert.Equal(49152, state.Axis[0]);
            Assert.Equal(65535, state.Axis[2]);
            Assert.Equal(9000, state.Povs[0]);
            Assert.StartsWith("@2 err bad-argument ", client.Final("@2 s " + own + " ViewUp=1"));
        }

        [Fact]
        public void ADevicesPanel_ShowsItsScriptId_AndItsControlsByTheNamesScriptsUse()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            var row = new PadForge.ViewModels.DeviceRowViewModel { InstanceGuid = pad.InstanceGuid };
            Assert.Equal(AutomationPipeRig.IdOf(pad), row.ScriptId);
            Assert.Matches("^[a-z2-7]{8}$", row.ScriptId);
            Assert.StartsWith(string.Format(PadForge.Resources.Strings.Strings.Instance.Devices_ScriptNames_Format, "A, B, X, Y, LeftShoulder").TrimEnd('.'),
                row.ScriptControlNames);
            Assert.Contains("LeftX, LeftY, LeftTrigger, RightX, RightY, RightTrigger, DPad", row.ScriptControlNames);
            Assert.Null(row.ScriptIdNote);
            row.CopyScriptIdCommand.Execute(null);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Extras_Automation_Copied, row.ScriptIdNote);
            // A device PadForge does not list has no names to show.
            Assert.Equal(string.Empty, new PadForge.ViewModels.DeviceRowViewModel { InstanceGuid = Guid.NewGuid() }.ScriptControlNames);
        }

        [Fact]
        public void ADeclaredName_ThatReadsAsAnotherControl_KeepsItsNumberedName()
        {
            var shape = ProgramDeviceDeclaration.Parse(
                "{\"buttons\":[\"b3\",\"Gyro\",\"Hat Up\",\"Fire\",\"Fire\",\"1st\"],\"axes\":[{\"name\":\"Axis 7\"}]}", out string error);
            Assert.Null(error);
            var names = ScriptNames.Declared(shape).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "Button0", "Button1", "Button2", "Fire", "Button4", "Button5", "Axis0" }, names);
        }

        [Fact]
        public void ATapSentBetweenTwoPasses_LandsOnTwoPasses_OnAProgramsDevice_AndOnAHold()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open("Taps", " lease=10000");
            string own = client.Final("declare key=t name=\"Taps\" " + Pad).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(own).Device;
            var row = RowOf(device.InstanceGuid);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);

            // The engine polls: a first pass.
            Pass(rig, row, pad);
            // A press and its release, both before the next pass.
            Assert.Equal("@1 ok", client.Final("@1 s " + own + " A=1"));
            Assert.Equal("@2 ok", client.Final("@2 s " + own + " A=0"));
            Assert.Equal("ok", client.Final("own " + id + " B=1"));
            Assert.Equal("ok", client.Final("rel " + id + " B"));
            Pass(rig, row, pad);
            Assert.True(row.ReadInputState.Buttons[0], "the press lands on the first pass");
            Assert.True(pad.InputState.Buttons[1], "the hold lands on the first pass");
            Pass(rig, row, pad);
            Assert.False(row.ReadInputState.Buttons[0], "the release lands on the next");
            Assert.False(pad.InputState.Buttons[1]);

            // A stick's two axes in one line land together.
            Assert.Equal("@3 ok", client.Final("@3 s " + own + " LeftX=100% Gas=50%"));
            Pass(rig, row, pad);
            Assert.Equal(65535, row.ReadInputState.Axis[0]);
            Assert.Equal(32768, row.ReadInputState.Axis[2]);
        }

        [Fact]
        public void TimedSteps_LandOnPadForgesClock_InTheirOrder()
        {
            long now = 1_000_000;
            ControlHolds.ClockForTest = () => now;
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var client = rig.Open("Clock", " lease=10000");
                string own = client.Final("declare key=c name=\"Clock\" " + Pad).Substring(6);
                var device = rig.Host.Sessions.Devices.Find(own).Device;
                device.ClockForTest = () => now;
                var row = RowOf(device.InstanceGuid);
                var pad = rig.AddDevice(InputDeviceType.Gamepad);
                string id = AutomationPipeRig.IdOf(pad);
                long ms = Stopwatch.Frequency / 1000;
                Pass(rig, row, pad);

                // A tap of 50 ms on a device of any kind, and one of 30 ms
                // on the script's own, both timed from now (in=).
                Assert.Equal("ok", client.Final("own " + id + " A=1"));
                Assert.Equal("ok", client.Final("rel " + id + " A in=50"));
                Assert.Equal("@1 ok", client.Final("@1 s " + own + " B=1"));
                Assert.Equal("@2 ok", client.Final("@2 s " + own + " B=0 in=30"));
                Pass(rig, row, pad);
                Assert.True(pad.InputState.Buttons[0]);
                Assert.True(row.ReadInputState.Buttons[1]);
                now += 29 * ms;
                Pass(rig, row, pad);
                Assert.True(pad.InputState.Buttons[0]);
                Assert.True(row.ReadInputState.Buttons[1]);
                now += 2 * ms;
                Pass(rig, row, pad);
                Assert.True(pad.InputState.Buttons[0], "the release waits its 50 ms");
                Assert.False(row.ReadInputState.Buttons[1], "the 30 ms step landed");
                now += 20 * ms;
                Pass(rig, row, pad);
                Assert.False(pad.InputState.Buttons[0]);

                // A sequence chains its steps (after=): B at 20 ms, then A
                // 20 ms after that, then both up 20 ms later.
                Assert.Equal("ok", client.Final("own " + id + " B=1 after=20"));
                Assert.Equal("ok", client.Final("own " + id + " A=1 after=20"));
                Assert.Equal("ok", client.Final("rel " + id + " A B after=20"));
                Pass(rig, row, pad);
                Assert.False(pad.InputState.Buttons[1]);
                now += 20 * ms;
                Pass(rig, row, pad);
                Assert.True(pad.InputState.Buttons[1]);
                Assert.False(pad.InputState.Buttons[0]);
                now += 20 * ms;
                Pass(rig, row, pad);
                Assert.True(pad.InputState.Buttons[0]);
                now += 20 * ms;
                Pass(rig, row, pad);
                Assert.False(pad.InputState.Buttons[0]);
                Assert.False(pad.InputState.Buttons[1]);
                Assert.StartsWith("err bad-argument ", client.Final("own " + id + " A=1 after=70000"));
                Assert.StartsWith("err bad-argument ", client.Final("own " + id + " A=1 after=5 in=5"));
            }
            finally
            {
                ControlHolds.ClockForTest = null;
            }
        }

        [Fact]
        public void Set_PutsAValueUntilTheDevicesOwnInputMovesIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Set", " lease=10000");
            Pass(rig, pad);
            Assert.Equal("ok", client.Final("set " + id + " LeftX=-100%"));
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Axis[0]);
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Axis[0]);
            // The stick moves: the device's own value wins from then on.
            pad.ReadInputState.Axis[0] = 40000;
            Pass(rig, pad);
            Assert.Equal(40000, pad.InputState.Axis[0]);
            pad.ReadInputState.Axis[0] = 32768;
            Pass(rig, pad);
            Assert.Equal(32768, pad.InputState.Axis[0]);
            rig.Host.Sessions.Tick();
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(rig.Host.Sessions.Snapshot().Single()));
        }

        [Fact]
        public void ANamedSubscription_StartsWithTheWholeState_ThenFollowsItsControl_AndSaysWhenTheDeviceGoes()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            pad.ReadInputState.Buttons[0] = true;
            pad.ReadInputState.Axis[0] = 65535;
            using var client = rig.Open("Follow", " units=plain");
            Assert.Equal("ok", client.Final("sub " + id + " B"));
            Assert.Equal("ok", client.Final("sub " + id + " state"));
            Pass(rig, pad);
            var first = client.Drain();
            Assert.Equal(2, first.Count(l => l.StartsWith("e state " + id + " b=", StringComparison.Ordinal)));
            // Button A is held already, and the left stick is right.
            Assert.All(first, l => Assert.Contains(" b=01", l));
            Assert.All(first, l => Assert.Contains(" a=100,", l));

            pad.ReadInputState.Buttons[1] = true;
            pad.ReadInputState.Buttons[2] = true;
            Pass(rig, pad);
            var changes = client.Drain();
            // The state follows every control, B's subscription B alone.
            Assert.Equal(2, changes.Count(l => l == "e b " + id + " 1 1"));
            Assert.Equal(1, changes.Count(l => l == "e b " + id + " 2 1"));

            rig.Pass();
            Assert.Equal(2, client.Drain().Count(l => l == "e state " + id + " offline"));
            Pass(rig, pad);
            Assert.Equal(2, client.Drain().Count(l => l.StartsWith("e state " + id + " b=", StringComparison.Ordinal)));
            Assert.StartsWith("err bad-argument ", client.Final("sub " + id + " Nothing"));
        }

        [Fact]
        public void AClaim_TakesTheControlFromGames_WhileTheScriptStillHearsIt()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Hotkeys", " units=plain lease=10000");
            Assert.Equal("ok", client.Final("sub " + id + " A claim"));
            Assert.Equal("ok", client.Final("sub " + id + " HatUp claim"));
            Assert.Equal("ok", client.Final("sub " + id + " X"));
            string line = client.Final("sub " + id + " LeftX above=80% claim");
            Assert.StartsWith("ok line=", line);
            string low = client.Final("sub " + id + " LeftX below=-80%");
            Assert.StartsWith("ok line=", low);
            Pass(rig, pad);
            client.Drain();

            pad.ReadInputState.Buttons[0] = true;
            pad.ReadInputState.Buttons[2] = true;
            pad.ReadInputState.Povs[0] = 4500; // Up and Right
            pad.ReadInputState.Axis[0] = 65535;
            Pass(rig, pad);
            Assert.False(pad.InputState.Buttons[0], "the game reads a claimed button up");
            Assert.True(pad.InputState.Buttons[2], "an unclaimed button passes");
            Assert.Equal(9000, pad.InputState.Povs[0]);
            Assert.Equal(32768, pad.InputState.Axis[0]);
            var events = client.Drain();
            Assert.Contains("e b " + id + " 0 1", events);
            Assert.Contains("e x " + id + " 0 1 " + line.Substring(8), events);

            // The low side, and the band back.
            pad.ReadInputState.Axis[0] = 0;
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Axis[0]);
            events = client.Drain();
            Assert.Contains("e x " + id + " 0 0 " + line.Substring(8), events);
            Assert.Contains("e x " + id + " 0 1 " + low.Substring(8), events);
            pad.ReadInputState.Axis[0] = 32768;
            Pass(rig, pad);
            Assert.Contains("e x " + id + " 0 0 " + low.Substring(8), client.Drain());

            // The claim lives as long as its subscription.
            Assert.Equal("ok", client.Final("unsub " + id + " A"));
            Assert.Equal("ok", client.Final("unsub " + id + " line=" + line.Substring(8)));
            pad.ReadInputState.Axis[0] = 65535;
            Pass(rig, pad);
            Assert.True(pad.InputState.Buttons[0]);
            Assert.Equal(65535, pad.InputState.Axis[0]);
            Assert.StartsWith("err bad-argument ", client.Final("sub " + id + " LeftX claim"));
        }

        [Fact]
        public void AClaim_NeedsSendInputs()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            Assert.StartsWith("err bad-argument ", client.Final("sub " + id + " A claim"));
            Assert.Equal("ok", client.Final("sub " + id + " A"));
            Assert.Null(ControlHolds.Current);
        }

        [Fact]
        public async Task AScriptsLease_Is250ms_ItsHoldsAndClaimsLetGo_AndComeBackAtItsNextLine()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Hangs");
            Assert.Equal(AutomationSession.ScriptLeaseMs, rig.Host.Sessions.Snapshot().Single().LeaseMs);
            Assert.Equal("ok", client.Final("own " + id + " B=1"));
            Assert.Equal("ok", client.Final("sub " + id + " A claim"));
            Assert.Equal(2, ControlHolds.Current[pad.InstanceGuid].Buttons.Length + ControlHolds.Current[pad.InstanceGuid].ClaimedButtons.Length);
            var clock = Stopwatch.StartNew();
            Assert.Equal("e lease lapsed", client.Event(3000));
            Assert.InRange(clock.ElapsedMilliseconds, 200, 1000);
            Assert.Null(ControlHolds.Current);
            client.Send("beat");
            await AutomationPipeRig.Until(() => ControlHolds.Current != null);
            Assert.Single(ControlHolds.Current[pad.InstanceGuid].Buttons);
            Assert.Single(ControlHolds.Current[pad.InstanceGuid].ClaimedButtons);
        }

        /// <summary>A script's holds and claims come back as one table at
        /// its next line: no table in between holds the button with the
        /// claim not back yet, which a pass would give the game as one read
        /// of the claimed button. The clock seam looks at the table each
        /// time the steps are timed.</summary>
        [Fact]
        public async Task AScriptsHoldsAndClaims_ComeBackInOneTable()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Hangs");
            Assert.Equal("ok", client.Final("own " + id + " B=1"));
            Assert.Equal("ok", client.Final("sub " + id + " A claim"));
            Assert.Equal("e lease lapsed", client.Event(3000));
            Assert.Null(ControlHolds.Current);
            var seen = new System.Collections.Concurrent.ConcurrentQueue<ControlHolds.DeviceHolds>();
            ControlHolds.ClockForTest = () =>
            {
                if (ControlHolds.Current is { } table && table.TryGetValue(pad.InstanceGuid, out var holds)) seen.Enqueue(holds);
                return Stopwatch.GetTimestamp();
            };
            try
            {
                client.Send("beat");
                await AutomationPipeRig.Until(() => ControlHolds.Current != null);
                // One more look, at the table the line left.
                ControlHolds.LandDue();
            }
            finally
            {
                ControlHolds.ClockForTest = null;
            }
            Assert.NotEmpty(seen);
            Assert.All(seen, holds =>
            {
                Assert.Single(holds.Buttons);
                Assert.Single(holds.ClaimedButtons);
            });
        }

        /// <summary>The holds and claims are one table for the whole
        /// process, so a host's sessions own theirs alone: another host's
        /// session, numbered first as this one is, ends, and that host goes,
        /// and what this session holds stands.</summary>
        [Fact]
        public async Task AnotherHostsSessionAndThatHostGoing_LeaveThisSessionsHoldsAlone()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Mine", " lease=10000");
            Assert.Equal("ok", client.Final("own " + id + " B=1"));
            Assert.Single(ControlHolds.Current[pad.InstanceGuid].Buttons);

            string otherPipe = "PadForge.Tests.Automation." + Guid.NewGuid().ToString("N");
            var other = TestConsent.AllowAll(new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                "PadForge.Tests.Control." + Guid.NewGuid().ToString("N"), automationPipeName: otherPipe));
            int mine = 0, theirNumber = 0;
            try
            {
                other.Apply(true, AutomationLevel.SendInputs);
                using (var theirs = new AutomationTestClient(otherPipe))
                {
                    Assert.StartsWith("ok padforge=", theirs.Final("hello name=\"Theirs\" kind=script lease=10000"));
                    mine = rig.Host.Sessions.Snapshot().Single().Number;
                    theirNumber = other.Sessions.Snapshot().Single().Number;
                }
                await AutomationPipeRig.Until(() => other.Sessions.Snapshot().Count == 0);
                Assert.Single(ControlHolds.Current[pad.InstanceGuid].Buttons);
            }
            finally
            {
                other.Dispose();
            }
            Assert.Single(ControlHolds.Current[pad.InstanceGuid].Buttons);
            Assert.NotEqual(mine, theirNumber);
        }

        [Fact]
        public async Task AnAssistantsLapsedHold_GoesForGood_AndItsNextCallDoesNotPressItAgain()
        {
            using var rig = new AssistantRig(AutomationLevel.SendInputs);
            var pad = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = "Pad", IsOnline = true };
            pad.LoadCapabilities(6, 16, 1, InputDeviceType.Gamepad);
            pad.InputState = pad.ReadInputState = new CustomInputState();
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(pad);
            var client = await rig.ConnectAsync();
            var held = await client.CallToolAsync("hold_control", new System.Collections.Generic.Dictionary<string, object>
                { ["device"] = AutomationDevices.IdOf(pad.InstanceGuid), ["control"] = "b3", ["value"] = true });
            Assert.False(held.IsError == true, AssistantRig.Text(held));
            var session = rig.Host.Sessions.Snapshot().Single(s => s.Kind == AutomationClientKind.Assistant);
            Assert.Equal(1, rig.Host.Sessions.HoldsOf(session));
            rig.Host.Sessions.Lapse(session);
            Assert.Null(ControlHolds.Current);
            session.Heard();
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(session));
            Assert.Null(ControlHolds.Current);
        }

        [Fact]
        public async Task ASilentScriptsDevices_GoOffline_AndComeBackWhenItSpeaks_AndAClosedPipesWait()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            rig.Host.Sessions.ScriptOfflineMs = 600;
            using var client = rig.Open("Silent", " lease=250");
            string own = client.Final("declare key=s name=\"Silent\" " + Pad).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(own).Device;
            Assert.True(device.IsAttached);
            Assert.Equal("e lease lapsed", client.Event(3000));
            Assert.True(device.IsAttached, "a lapse rests the device and keeps it online");
            await AutomationPipeRig.Until(() => !device.IsAttached, 3000);
            Assert.Contains("unregister Silent", rig.Engine.Calls);
            client.Send("beat");
            await AutomationPipeRig.Until(() => device.IsAttached, 3000);
            Assert.Equal(2, rig.Engine.Calls.Count(c => c == "register Silent"));

            // The pipe closes: the device waits (300 ms in the rig, 1,500 in
            // the app), then goes.
            Assert.Equal(1500, new ProgramDevices(() => 0).OfflineAfterMs);
            client.Dispose();
            await Task.Delay(100);
            Assert.True(device.IsAttached, "it waits for its program to come back");
            await AutomationPipeRig.Until(() => !device.IsAttached, 3000);
        }

        [Fact]
        public async Task AWake_IsSentAgain_UntilTheProgramSpeaks()
        {
            using var stream = new System.IO.MemoryStream();
            var outbox = new AutomationOutbox(stream, () => { });
            int wakes = 0;
            outbox.WakeForTest = () => Interlocked.Increment(ref wakes);
            using var stop = new CancellationTokenSource();
            var writer = outbox.RunAsync(stop.Token);
            outbox.Line("e b x 0 1");
            // The wakes come every 20 ms on the thread pool's clock, which a
            // busy pool runs late: the count is waited for, not timed.
            await AutomationPipeRig.Until(() => Volatile.Read(ref wakes) >= 3);
            int silent = Volatile.Read(ref wakes);
            Assert.InRange(silent, 3, 1 + AutomationOutbox.MostRewakes);
            outbox.Heard();
            int before = outbox.Wakes;
            outbox.Line("e b x 0 0");
            await AutomationPipeRig.Until(() => outbox.Wakes > before);
            // The program answers the wake: no wake goes again.
            outbox.Heard();
            int after = outbox.Wakes;
            await Task.Delay(200);
            Assert.Equal(after, outbox.Wakes);
            stop.Cancel();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { }
        }
    }
}
