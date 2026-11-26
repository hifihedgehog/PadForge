using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The holds programs put over a device's controls, and a program
    /// device's touchpad click, as the audit of 2026-11-26 found them: a
    /// pass on which a step lands allocates nothing, and a declared button
    /// 16 is the program's own.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261126HoldsTests
    {
        private const int Owner = 7001;

        /// <summary>Runs <paramref name="send"/> before each of 1200 passes
        /// on a clock the test moves one millisecond a pass, and gives the
        /// bytes the passes allocated after the first 200.</summary>
        private static long PassBytes(Guid device, Action<int> send)
        {
            long now = 1_000_000_000;
            long tick = System.Diagnostics.Stopwatch.Frequency / 1000;
            ControlHolds.Publish(null);
            ControlHolds.ClockForTest = () => now;
            try
            {
                var state = new CustomInputState();
                long bytes = 0;
                for (int i = 0; i < 1200; i++)
                {
                    send(i);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    ControlHolds.BeginPass();
                    ControlHolds.Apply(device, state);
                    long made = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (i >= 200) bytes += made;
                    now += tick;
                }
                return bytes;
            }
            finally
            {
                ControlHolds.ClockForTest = null;
                ControlHolds.Publish(null);
            }
        }

        /// <summary>A program streams a held axis, a new value each pass:
        /// the pass that lands each step makes nothing.</summary>
        [Fact]
        public void APassThatLandsAHeldValue_AllocatesNothing()
        {
            var device = Guid.NewGuid();
            var holds = new ControlHolds.Hold[1200];
            for (int i = 0; i < holds.Length; i++) holds[i] = new ControlHolds.Hold(i * 50);
            Assert.Equal(0, PassBytes(device, i => ControlHolds.Send(Owner, device, ControlHolds.HoldAxis, 1, holds[i])));
        }

        /// <summary>A program holds a button and lets it go on alternate
        /// passes: after the first press and release, the passes make
        /// nothing.</summary>
        [Fact]
        public void APassThatHoldsAndLetsGoInTurn_AllocatesNothing()
        {
            var device = Guid.NewGuid();
            var holds = new ControlHolds.Hold[1200];
            for (int i = 0; i < holds.Length; i++) holds[i] = new ControlHolds.Hold(1);
            Assert.Equal(0, PassBytes(device, i => ControlHolds.Send(Owner, device, ControlHolds.HoldButton, 3, i % 2 == 0 ? holds[i] : null)));
        }

        /// <summary>A program holds a stick and taps a button beside it on
        /// alternate passes: the device moves between two sets of controls,
        /// and after each was seen the passes make nothing.</summary>
        [Fact]
        public void APassThatTapsAButtonBesideAHeldStick_AllocatesNothing()
        {
            var device = Guid.NewGuid();
            var axes = new ControlHolds.Hold[1200];
            var taps = new ControlHolds.Hold[1200];
            for (int i = 0; i < axes.Length; i++)
            {
                axes[i] = new ControlHolds.Hold(i * 40);
                taps[i] = new ControlHolds.Hold(1);
            }
            Assert.Equal(0, PassBytes(device, i =>
            {
                ControlHolds.Send(Owner, device, ControlHolds.HoldAxis, 0, axes[i]);
                ControlHolds.Send(Owner, device, ControlHolds.HoldButton, 5, i % 2 == 0 ? taps[i] : null);
            }));
        }

        /// <summary>Two owners claim buttons 3 and 4 of one device, and on
        /// one pass the second lets 4 go and claims 3: the table is made
        /// again, and button 4 reads as the device has it.</summary>
        [Fact]
        public void AClaimThatMovesToAnotherOwnersButton_LetsTheOldOneGo()
        {
            var device = Guid.NewGuid();
            long now = 3_000_000_000;
            ControlHolds.Publish(null);
            ControlHolds.ClockForTest = () => now;
            try
            {
                var state = new CustomInputState();
                state.Buttons[3] = true;
                state.Buttons[4] = true;
                ControlHolds.Send(Owner, device, ControlHolds.ClaimButton, 3, true);
                ControlHolds.Send(Owner + 1, device, ControlHolds.ClaimButton, 4, true);
                ControlHolds.BeginPass();
                var read = ControlHolds.Apply(device, state);
                Assert.False(read.Buttons[3]);
                Assert.False(read.Buttons[4]);
                now += System.Diagnostics.Stopwatch.Frequency / 1000;
                ControlHolds.Send(Owner + 1, device, ControlHolds.ClaimButton, 4, null);
                ControlHolds.Send(Owner + 1, device, ControlHolds.ClaimButton, 3, true);
                ControlHolds.BeginPass();
                read = ControlHolds.Apply(device, state);
                Assert.False(read.Buttons[3]);
                Assert.True(read.Buttons[4], "button 4 is no one's claim now");
            }
            finally
            {
                ControlHolds.ClockForTest = null;
                ControlHolds.Publish(null);
            }
        }

        /// <summary>The value streamed lands in what mappings read, a pass
        /// at a time, and a let-go takes the hold away.</summary>
        [Fact]
        public void AStreamedValue_IsWhatMappingsRead()
        {
            var device = Guid.NewGuid();
            long now = 2_000_000_000;
            ControlHolds.Publish(null);
            ControlHolds.ClockForTest = () => now;
            try
            {
                var state = new CustomInputState();
                foreach (int value in new[] { 100, 200, 300 })
                {
                    ControlHolds.Send(Owner, device, ControlHolds.HoldAxis, 1, new ControlHolds.Hold(value));
                    ControlHolds.BeginPass();
                    Assert.Equal(value, ControlHolds.Apply(device, state).Axis[1]);
                    now += System.Diagnostics.Stopwatch.Frequency / 1000;
                }
                ControlHolds.Send(Owner, device, ControlHolds.HoldAxis, 1, null);
                ControlHolds.BeginPass();
                Assert.Null(ControlHolds.Current);
                Assert.Equal(0, ControlHolds.Apply(device, state).Axis[1]);
            }
            finally
            {
                ControlHolds.ClockForTest = null;
                ControlHolds.Publish(null);
            }
        }

        // ── A program device's button 16 and its touchpad's click ──

        private static ProgramDevice Pad(bool declaresSixteen)
        {
            var buttons = new System.Collections.Generic.List<(int, string)>();
            for (int i = 0; i < (declaresSixteen ? 17 : 16); i++) buttons.Add((i, "B" + i));
            return new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://test", "Pad",
                new ProgramDeviceShape { Buttons = buttons, TouchpadFingers = new[] { 2 } });
        }

        private static CustomInputState After(ProgramDevice device, Action<ProgramDeviceChange> fill)
        {
            var change = new ProgramDeviceChange();
            fill(change);
            device.Apply(change);
            return device.GetCurrentState();
        }

        /// <summary>A program that declared button 16 keeps it up through a
        /// click of its first pad, and Touchpad 0 Click reads the
        /// click.</summary>
        [Fact]
        public void ADeclaredButtonSixteen_StaysUpThroughAClick()
        {
            var pad = Pad(declaresSixteen: true);
            var clicked = After(pad, c => c.Clicks.Add((0, true)));
            Assert.False(clicked.Buttons[16]);
            Assert.True(SourceCoercion.ReadTouchpadBool(clicked, "Touchpad 0 Click"));

            var pressed = After(pad, c => { c.Clicks.Add((0, false)); c.Buttons.Add((16, true)); });
            Assert.True(pressed.Buttons[16]);
            Assert.False(SourceCoercion.ReadTouchpadBool(pressed, "Touchpad 0 Click"));
        }

        /// <summary>A device that declares no button 16 carries its first
        /// pad's click on button 16, where every other touchpad device
        /// carries it.</summary>
        [Fact]
        public void APadWithoutButtonSixteen_CarriesTheClickThere()
        {
            var pad = Pad(declaresSixteen: false);
            var clicked = After(pad, c => c.Clicks.Add((0, true)));
            Assert.True(clicked.Buttons[16]);
            Assert.True(SourceCoercion.ReadTouchpadBool(clicked, "Touchpad 0 Click"));
        }

        /// <summary>Where a state keeps its first pad's click goes with every
        /// copy and every frame sent to a Remote Link peer, and a reused
        /// buffer forgets it.</summary>
        [Fact]
        public void OwnButtonSixteen_GoesWithTheCopyAndTheWire()
        {
            var state = new CustomInputState { Touchpads = new[] { new TouchpadInputState(2) }, OwnButtonSixteen = true };
            state.Touchpads[0].Clicked = true;
            state.Buttons[16] = false;
            var copy = state.Clone();
            Assert.True(copy.OwnButtonSixteen);
            Assert.True(copy.FirstPadClicked);
            copy.ResetForReuse();
            Assert.False(copy.OwnButtonSixteen);

            var sent = PadForge.Engine.RemoteLink.CustomInputStateCodec.Decode(
                PadForge.Engine.RemoteLink.CustomInputStateCodec.Encode(state, default));
            Assert.True(sent.OwnButtonSixteen);
            Assert.True(sent.FirstPadClicked);
            state.OwnButtonSixteen = false;
            var plain = PadForge.Engine.RemoteLink.CustomInputStateCodec.CreateNeutral();
            plain.OwnButtonSixteen = true;
            PadForge.Engine.RemoteLink.CustomInputStateCodec.DecodeInto(
                PadForge.Engine.RemoteLink.CustomInputStateCodec.Encode(state, default), plain);
            Assert.False(plain.OwnButtonSixteen);
        }

        /// <summary>A recorded web pad, 17 buttons and a touchpad whose click
        /// rides button 16 alone (WebControllerDevice), replays with its
        /// click read from button 16: Replay leaves button 16 out of a
        /// replayed touchpad device's shape, and only a recorded program's
        /// device keeps it as a button of its own.</summary>
        [Fact]
        public void AReplayedWebPad_ClicksWhereItWasRecorded()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge.Tests.ReplayWeb." + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                const string head = "PadForge problem recording, format 1\n== Devices ==\n";
                const string tail = "\n== Events ==\n0.000000 I D1 full online a0=1\n";
                string web = System.IO.Path.Combine(dir, "web.log");
                System.IO.File.WriteAllText(web, head + "D1 name=\"Web Controller 1\" type=Gamepad axes=6 buttons=17 hats=1 touchpads=1 fingers=2" + tail);
                var file = PadForge.Services.Plugins.ReplayFile.Read(web, out string error);
                Assert.Null(error);
                var shape = file.Devices.Single().Shape;
                Assert.DoesNotContain(shape.Buttons, b => b.Index == 16);
                Assert.Contains(shape.Buttons, b => b.Index == 15);
                var pad = new ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://replay/web", "Web", shape);
                var frame = new CustomInputState { Touchpads = new[] { new TouchpadInputState(2) } };
                frame.Buttons[16] = true;
                pad.Publish(frame);
                var read = pad.GetCurrentState();
                Assert.True(read.FirstPadClicked);
                Assert.True(SourceCoercion.ReadTouchpadBool(read, "Touchpad 0 Click"));

                string program = System.IO.Path.Combine(dir, "program.log");
                System.IO.File.WriteAllText(program, head + "D1 name=\"Rig\" type=Program axes=2 buttons=17 touchpads=1 fingers=2" + tail);
                var owned = PadForge.Services.Plugins.ReplayFile.Read(program, out error).Devices.Single().Shape;
                Assert.Contains(owned.Buttons, b => b.Index == 16);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        /// <summary>A replayed DualSense, whose button 16 and first pad's
        /// click are one control in its recording, reads the same through
        /// both names.</summary>
        [Fact]
        public void AReplayedDualSense_ReadsTheClickThroughBothNames()
        {
            var pad = Pad(declaresSixteen: true);
            var frame = new CustomInputState { Touchpads = new[] { new TouchpadInputState(2) } };
            frame.Buttons[16] = true;
            frame.Touchpads[0].Clicked = true;
            pad.Publish(frame);
            var read = pad.GetCurrentState();
            Assert.True(read.Buttons[16]);
            Assert.True(SourceCoercion.ReadTouchpadBool(read, "Touchpad 0 Click"));
        }
    }
}
