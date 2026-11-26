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
            // A hat's direction on its own (#600 item 21): it goes into the
            // hat as the change lands, beside the hat's other directions.
            // In one change the whole hat comes first and its directions
            // over it, whatever order the words came in.
            Assert.Equal("@2 ok", client.Final("@2 s " + own + " ViewUp=1"));
            Assert.Equal(4500, rig.Host.Sessions.Devices.Find(own).Device.PeekState().Povs[0]);
            Assert.Equal("@3 ok", client.Final("@3 s " + own + " ViewRight=0"));
            Assert.Equal(0, rig.Host.Sessions.Devices.Find(own).Device.PeekState().Povs[0]);
            Assert.Equal("@4 ok", client.Final("@4 s " + own + " ViewLeft=true View=Down"));
            Assert.Equal(22500, rig.Host.Sessions.Devices.Find(own).Device.PeekState().Povs[0]);
            Assert.StartsWith("@5 err bad-argument ", client.Final("@5 s " + own + " ViewUp=maybe"));
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

        /// <summary>A declared name that reads as a hat's direction, a
        /// button "DPad Up" beside a hat "DPad", keeps its numbered name:
        /// DPadUp is the hat's up, and the button stays reachable by its
        /// own name. The list keeps its order, buttons first.</summary>
        [Fact]
        public void ADeclaredName_ThatReadsAsAHatsDirection_KeepsItsNumberedName()
        {
            var shape = ProgramDeviceDeclaration.Parse(
                "{\"buttons\":[\"DPad Up\",\"Fire\"],\"axes\":[{\"name\":\"DPad Left\"}],\"hats\":[\"DPad\"]}", out string error);
            Assert.Null(error);
            var controls = ScriptNames.Declared(shape);
            Assert.Equal(new[] { "Button0", "Fire", "Axis0", "DPad" }, controls.Select(c => c.Name).ToArray());
            Assert.True(ScriptNames.TryResolve("Button0", controls, out char kind, out int index));
            Assert.Equal(('b', 0), (kind, index));
            Assert.True(ScriptNames.TryDirection("DPadUp", controls, out int hat, out _));
            Assert.Equal(0, hat);
        }

        /// <summary>A press and its release sent between two passes land on
        /// two passes, on a program's own device and on a hold. The clocks
        /// stand still between the test's passes, as a running engine's 1 ms
        /// passes leave no time for a device to count as unread. On the real
        /// clock, a busy PC that slowed the lines past 100 ms had the device
        /// count as unread, and it took both changes at once.</summary>
        [Fact]
        public void ATapSentBetweenTwoPasses_LandsOnTwoPasses_OnAProgramsDevice_AndOnAHold()
        {
            long now = Stopwatch.GetTimestamp();
            ControlHolds.ClockForTest = () => now;
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                using var client = rig.Open("Taps", " lease=10000");
                string own = client.Final("declare key=t name=\"Taps\" " + Pad).Substring(6);
                var device = rig.Host.Sessions.Devices.Find(own).Device;
                device.ClockForTest = () => now;
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
            finally
            {
                ControlHolds.ClockForTest = null;
            }
        }

        /// <summary>A tap or a sequence on a device PadForge has but that is
        /// not connected is refused when PadForge handles its line, in a
        /// read's words and code (#600 item 21): a timed step, an own or a
        /// set with in= or after=, answers <c>err offline</c>. A hold waits
        /// for the device, and a release is never refused, so a tap whose
        /// press was refused leaves nothing held. Connected again, the same
        /// tap is taken.</summary>
        [Fact]
        public void ATimedStep_OnADeviceThatIsNotConnected_IsRefused_AndAHoldOrAReleaseIsNot()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad, online: false);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Offline", " units=plain lease=10000");
            const string refused = "err offline the device is not connected";
            Assert.Equal(refused, client.Final("own " + id + " A=1 in=0"));
            Assert.Equal(refused, client.Final("own " + id + " A=1 after=0"));
            Assert.Equal(refused, client.Final("set " + id + " A=1 in=0"));
            Assert.Equal("ok", client.Final("rel " + id + " A in=50"));
            Assert.Equal("ok", client.Final("own " + id + " B=1"));
            Assert.Equal("ok", client.Final("set " + id + " X=1"));
            Pass(rig, pad);
            Assert.False(pad.InputState.Buttons[0], "a refused press holds nothing");
            Assert.True(pad.InputState.Buttons[1], "a hold waits for the device");
            Assert.True(pad.InputState.Buttons[2], "a set waits for the device");

            pad.IsOnline = true;
            Assert.Equal("ok", client.Final("own " + id + " A=1 in=0"));
            Assert.Equal("ok", client.Final("rel " + id + " A in=50"));
            Pass(rig, pad);
            Assert.True(pad.InputState.Buttons[0], "connected, the tap's press is taken");
        }

        /// <summary>A device that goes in the moment after a program comes to
        /// follow devices, before PadForge's next pass, is told gone at that
        /// pass, and one that comes in that moment is told come (#600 item
        /// 21): PadForge's first look at which devices are online starts from
        /// those online when the program came, so no change in between is
        /// lost.</summary>
        [Fact]
        public void ADeviceThatGoesBeforeTheFirstPassAfterASubscription_IsToldGone()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            var mouse = rig.AddDevice(InputDeviceType.Mouse, online: false);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("sub devices"));
            pad.IsOnline = false;
            mouse.IsOnline = true;
            rig.Pass(mouse);
            Assert.Equal("e dev " + AutomationPipeRig.IdOf(pad) + " offline", client.Event(2000));
            Assert.Equal("e dev " + AutomationPipeRig.IdOf(mouse) + " online", client.Event(2000));
            rig.Pass(mouse);
            Assert.Empty(client.Drain());
        }

        /// <summary>Timed steps land on PadForge's clock in their order. The clock
        /// is the whole process's. It starts at the real one's reading, so the
        /// leases stamped meanwhile stay near real times, and a step ages
        /// every live lease by its length. No test outside this collection
        /// keeps a session that holds a control or owns a device, which is all
        /// a lease lets go of.</summary>
        [Fact]
        public void TimedSteps_LandOnPadForgesClock_InTheirOrder()
        {
            long now = Stopwatch.GetTimestamp();
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

        /// <summary>
        /// A hat's direction is held on its own, as a button is (#600 items 3
        /// and 21): 1 presses it and 0 holds it released, whatever the device
        /// does, and the hat's other directions stay as they are, held where
        /// a program holds them and the device's own where none does. The
        /// whole hat, held or the device's own, is what the held directions go
        /// over, so the order the holds came in changes nothing, and each is
        /// given back on its own. Up and Down together cancel, as Left and
        /// Right do. A hat keeps its exact angle until a held direction
        /// changes which directions it holds.
        /// </summary>
        [Fact]
        public void AHatsDirection_IsHeldOnItsOwn_OverTheHatsOtherDirections()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            pad.ReadInputState.Povs[0] = -1;
            using var client = rig.Open("Directions", " units=plain lease=10000");
            Pass(rig, pad);

            // Up alone, then the device's own Right beside it.
            Assert.Equal("ok", client.Final("own " + id + " DPadUp=1"));
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Povs[0]);
            pad.ReadInputState.Povs[0] = 9000;
            Pass(rig, pad);
            Assert.Equal(4500, pad.InputState.Povs[0]);
            // Right held released, under its first hat's other name: the
            // device's Right is left out.
            Assert.Equal("ok", client.Final("own " + id + " HatRight=false"));
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Povs[0]);
            // Up held and the device's Down cancel.
            pad.ReadInputState.Povs[0] = 18000;
            Pass(rig, pad);
            Assert.Equal(-1, pad.InputState.Povs[0]);
            // Given back, each by its own name: the device's own again.
            Assert.Equal("ok", client.Final("rel " + id + " DPadUp DPadRight"));
            Pass(rig, pad);
            Assert.Equal(18000, pad.InputState.Povs[0]);

            // The whole hat held, and a direction over it, in either order.
            Assert.Equal("ok", client.Final("own " + id + " DPadRight=1"));
            Assert.Equal("ok", client.Final("own " + id + " DPad=Up"));
            Pass(rig, pad);
            Assert.Equal(4500, pad.InputState.Povs[0]);
            Assert.Equal("ok", client.Final("rel " + id + " all"));
            Pass(rig, pad);
            Assert.Equal(18000, pad.InputState.Povs[0]);
            Assert.Equal("ok", client.Final("own " + id + " DPadRight=1 DPad=Up"));
            Pass(rig, pad);
            Assert.Equal(4500, pad.InputState.Povs[0]);
            // The whole hat given back alone: the device's own Down, and
            // Right still held over it.
            Assert.Equal("ok", client.Final("rel " + id + " DPad"));
            Pass(rig, pad);
            Assert.Equal(13500, pad.InputState.Povs[0]);
            Assert.Equal("ok", client.Final("rel " + id + " all"));
            Pass(rig, pad);

            // A hat at 30 degrees keeps its angle while the held directions
            // change nothing of it: it holds Up and Right already, and Left
            // is not pressed.
            pad.ReadInputState.Povs[0] = 3000;
            Assert.Equal("ok", client.Final("own " + id + " DPadUp=1 DPadLeft=0"));
            Pass(rig, pad);
            Assert.Equal(3000, pad.InputState.Povs[0]);
            Assert.Equal("ok", client.Final("own " + id + " DPadRight=0"));
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Povs[0]);
            Assert.Equal("ok", client.Final("rel " + id + " all"));
            Pass(rig, pad);

            // set: Up stays until the device's own Up changes. The device's
            // Right does not change Up, so Up stays beside it.
            pad.ReadInputState.Povs[0] = -1;
            Pass(rig, pad);
            Assert.Equal("ok", client.Final("set " + id + " DPadUp=1"));
            Pass(rig, pad);
            Assert.Equal(0, pad.InputState.Povs[0]);
            pad.ReadInputState.Povs[0] = 9000;
            Pass(rig, pad);
            Assert.Equal(4500, pad.InputState.Povs[0]);
            pad.ReadInputState.Povs[0] = 0;
            Pass(rig, pad);
            pad.ReadInputState.Povs[0] = -1;
            Pass(rig, pad);
            Assert.Equal(-1, pad.InputState.Povs[0]);
            rig.Host.Sessions.Tick();
            Assert.Equal(0, rig.Host.Sessions.HoldsOf(rig.Host.Sessions.Snapshot().Single()));
            // The let-go lands at a pass, before the holds below, which a
            // second change of the same direction in one pass would wait a
            // pass for.
            Pass(rig, pad);

            // Another program: a hat and its directions overlap, two
            // directions do not.
            Assert.Equal("ok", client.Final("own " + id + " DPadRight=1"));
            using var other = rig.Open("Other", " units=plain lease=10000");
            Assert.StartsWith("err busy ", other.Final("own " + id + " DPad=Up"));
            Assert.StartsWith("err busy ", other.Final("own " + id + " HatRight=0"));
            Assert.Equal("ok", other.Final("own " + id + " DPadUp=1"));
            Pass(rig, pad);
            Assert.Equal(4500, pad.InputState.Povs[0]);
            Assert.Equal("ok", other.Final("rel " + id + " all"));
            Assert.Equal("ok", client.Final("own " + id + " DPad=Down"));
            Assert.StartsWith("err busy ", other.Final("own " + id + " DPadUp=1"));
            Assert.StartsWith("err bad-argument ", client.Final("own " + id + " DPadUp=maybe"));
            Assert.StartsWith("err bad-argument ", client.Final("rel " + id + " DPadNowhere"));
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
            Assert.StartsWith("err not-allowed ", client.Final("sub " + id + " A claim"));
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

        /// <summary>
        /// A script's holds and claims let go 250 ms after its last line,
        /// decided in the pass that applies them, on the clock that times
        /// that pass (#600 item 24): in force at the pass 249 ms after the
        /// last line, let go at the pass 251 ms after it, and back at the
        /// first pass after its next line. The test steps that clock and runs
        /// the passes itself. The sessions' own clock never moves the lease:
        /// it reads the same clock, which stands still here between steps.
        /// The clock is the whole process's. It starts at the real one's
        /// reading, so the leases stamped meanwhile stay near real times, and
        /// a step ages every live lease by its length. No test outside this
        /// collection keeps a session that holds a control or owns a device,
        /// which is all a lease lets go of.
        /// </summary>
        [Fact]
        public void AScriptsLease_IsDecidedInThePass_OnTheClockThatAppliesItsHolds()
        {
            long now = Stopwatch.GetTimestamp();
            ControlHolds.ClockForTest = () => Volatile.Read(ref now);
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad);
                string id = AutomationPipeRig.IdOf(pad);
                long ms = Stopwatch.Frequency / 1000;
                using var client = rig.Open("Lease");
                Assert.Equal(AutomationSession.ScriptLeaseMs, rig.Host.Sessions.Snapshot().Single().LeaseMs);
                Assert.Equal("ok", client.Final("own " + id + " B=1"));
                Assert.Equal("ok", client.Final("sub " + id + " A claim"));
                pad.ReadInputState.Buttons[0] = true;
                Pass(rig, pad);
                Assert.True(pad.InputState.Buttons[1], "B is held");
                Assert.False(pad.InputState.Buttons[0], "A is claimed");

                Interlocked.Add(ref now, 249 * ms);
                Pass(rig, pad);
                Assert.True(pad.InputState.Buttons[1], "B is held 249 ms after the last line");
                Assert.False(pad.InputState.Buttons[0], "A is claimed 249 ms after the last line");

                Interlocked.Add(ref now, 2 * ms);
                Pass(rig, pad);
                Assert.False(pad.InputState.Buttons[1], "B lets go at the pass 251 ms after the last line");
                Assert.True(pad.InputState.Buttons[0], "A is the game's at the pass 251 ms after the last line");
                Assert.Null(ControlHolds.Current);

                // The script's next line: both are back at the next pass.
                Assert.StartsWith("ok", client.Final("devices"));
                Pass(rig, pad);
                Assert.True(pad.InputState.Buttons[1], "B is held again");
                Assert.False(pad.InputState.Buttons[0], "A is claimed again");
            }
            finally
            {
                ControlHolds.ClockForTest = null;
            }
        }

        /// <summary>Every pass judges the leases (#600 item 24), and a pass
        /// with a program's holds and its lease in force makes nothing: a
        /// read of each lease, and the holds over the device from the
        /// table's own buffer.</summary>
        [Fact]
        public void APassThatJudgesTheLeases_AllocatesNothing()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Quiet", " lease=10000");
            Assert.Equal("ok", client.Final("own " + id + " B=1 DPadUp=1"));
            Assert.Equal("ok", client.Final("sub " + id + " A claim"));
            var state = new CustomInputState();
            state.Povs[0] = -1;
            for (int i = 0; i < 50; i++)
            {
                ControlHolds.BeginPass();
                ControlHolds.Apply(pad.InstanceGuid, state);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                ControlHolds.BeginPass();
                ControlHolds.Apply(pad.InstanceGuid, state);
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            var read = ControlHolds.Apply(pad.InstanceGuid, state);
            Assert.True(read.Buttons[1]);
            Assert.Equal(0, read.Povs[0]);
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

        /// <summary>A script the sessions' clock let lapse, while no pass
        /// runs, has its holds and claims back before its next line is
        /// answered: the line puts them back itself, with no pass and no
        /// clock after it. The test steps the passes' clock. The clock
        /// is the whole process's. It starts at the real one's reading, so the
        /// leases stamped meanwhile stay near real times, and a step ages
        /// every live lease by its length. No test outside this collection
        /// keeps a session that holds a control or owns a device, which is all
        /// a lease lets go of.</summary>
        [Fact]
        public void AScriptTheClockLetLapse_HasItsHoldsAndClaimsBack_BeforeItsNextLineIsAnswered()
        {
            long now = Stopwatch.GetTimestamp();
            ControlHolds.ClockForTest = () => Volatile.Read(ref now);
            try
            {
                using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
                var pad = rig.AddDevice(InputDeviceType.Gamepad);
                string id = AutomationPipeRig.IdOf(pad);
                using var client = rig.Open("Back");
                Assert.Equal("ok", client.Final("own " + id + " B=1"));
                Assert.Equal("ok", client.Final("sub " + id + " A claim"));
                Interlocked.Add(ref now, 251 * (Stopwatch.Frequency / 1000));
                rig.Host.Sessions.Tick();
                Assert.Equal("e lease lapsed", client.Event(3000));
                Assert.Null(ControlHolds.Current);

                // A beat, which sends nothing to the table and is answered at
                // once.
                Assert.Equal("@1 ok", client.Final("@1 beat"));
                var holds = ControlHolds.Current?[pad.InstanceGuid];
                Assert.NotNull(holds);
                Assert.Single(holds.Buttons);
                Assert.Single(holds.ClaimedButtons);
            }
            finally
            {
                ControlHolds.ClockForTest = null;
            }
        }

        /// <summary>An assistant's hold lets go in the pass at its lease, as
        /// a script's does, and goes for good: its next call, which comes
        /// before the sessions' clock looks, does not bring it back. The
        /// test steps the passes' clock. The clock
        /// is the whole process's. It starts at the real one's reading, so the
        /// leases stamped meanwhile stay near real times, and a step ages
        /// every live lease by its length. No test outside this collection
        /// keeps a session that holds a control or owns a device, which is all
        /// a lease lets go of.</summary>
        [Fact]
        public async Task AnAssistantsHold_LetsGoInThePassAtItsLease_AndItsNextCallDoesNotBringItBack()
        {
            long now = Stopwatch.GetTimestamp();
            ControlHolds.ClockForTest = () => Volatile.Read(ref now);
            try
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
                ControlHolds.BeginPass();
                Assert.True(ControlHolds.Apply(pad.InstanceGuid, new CustomInputState()).Buttons[3]);

                Interlocked.Add(ref now, (session.LeaseMs + 1) * (Stopwatch.Frequency / 1000));
                ControlHolds.BeginPass();
                Assert.False(ControlHolds.Apply(pad.InstanceGuid, new CustomInputState()).Buttons[3], "the pass lets it go at the lease");
                session.Heard();
                ControlHolds.BeginPass();
                Assert.False(ControlHolds.Apply(pad.InstanceGuid, new CustomInputState()).Buttons[3], "the next call does not press it again");
                Assert.Equal(0, rig.Host.Sessions.HoldsOf(session));
            }
            finally
            {
                ControlHolds.ClockForTest = null;
            }
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
