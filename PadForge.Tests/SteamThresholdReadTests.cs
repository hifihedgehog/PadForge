using System;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The edge, one-value and threshold-style reads a Steam import
    /// takes, as steamclient64.dll runs them: the edges of its modes, the
    /// trigger's Soft Pull button, the trigger mode's Soft Pull through its
    /// Threshold Trigger Style, and the Soft Press activator.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class SteamThresholdReadTests
    {
        private readonly Guid _device = Guid.NewGuid();
        private readonly CustomInputState _state = new();
        private long _now;

        private const string Stick = "Gamepad LeftStickRing";
        private const string Pad = "Touchpad 0 Finger 0 Ring";
        private const string Trigger = "Gamepad LeftTrigger";

        public SteamThresholdReadTests()
        {
            for (int i = 0; i < _state.Axis.Length; i++) _state.Axis[i] = 32768;
            _state.Axis[2] = 0;
        }

        /// <summary>One device poll (a millisecond), then the read.</summary>
        private bool Read(string descriptor)
        {
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            return SourceCoercion.EvaluateForButtonTarget(_state, new MappingSource { Descriptor = descriptor }, 50, 0,
                _device.ToString());
        }

        /// <summary>The trigger held at a pull for some polls, and the last read.</summary>
        private bool Hold(string descriptor, double percent, int polls)
        {
            _state.Axis[2] = (int)Math.Round(percent / 100 * 65535);
            bool on = false;
            for (int i = 0; i < polls; i++) on = Read(descriptor);
            return on;
        }

        private void StickAt(double degrees, double percent)
        {
            double r = degrees * Math.PI / 180.0;
            _state.Axis[0] = (int)Math.Round(32768 + 32767 * percent / 100 * Math.Cos(r));
            _state.Axis[1] = (int)Math.Round(32768 - 32767 * percent / 100 * Math.Sin(r));
        }

        private void Finger(float x, float y)
        {
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            _state.Touchpads = new[] { pad };
        }

        // ─── Ring ──────────────────────────────────────────────────────

        [Fact]
        public void Ring_JoystickEdge_HasNoDebounce()
        {
            // The joystick modes' default edge: 25000 with 3 percent of
            // hold-back, pressed past 79.30 and held to 73.30.
            string edge = BoundedReads.Ring(25983.01 / 32767 * 100, 24016.99 / 32767 * 100, false, 0,
                BoundedGate.None, Stick);
            StickAt(90, 78);
            Assert.False(Read(edge));
            StickAt(90, 80);
            Assert.True(Read(edge));
            StickAt(90, 74);
            Assert.True(Read(edge));
            StickAt(90, 72);
            Assert.False(Read(edge));
            StickAt(90, 80);
            Assert.True(Read(edge));
        }

        [Fact]
        public void Ring_DpadEdge_HoldsAChangeFor120thOfASecond()
        {
            string edge = BoundedReads.Ring(86.3, 66.3, false, 1000.0 / 120, BoundedGate.None, Stick);
            StickAt(0, 90);
            Assert.True(Read(edge));
            StickAt(0, 10);
            Assert.True(Read(edge));
            Thread.Sleep(15);
            Assert.False(Read(edge));
        }

        [Fact]
        public void Ring_InnerEdge_OnAStick_ReadsPastTheRestFloor()
        {
            string edge = BoundedReads.Ring(50, 60, true, 0, BoundedGate.None, Stick);
            StickAt(0, 3);
            Assert.False(Read(edge));
            StickAt(0, 30);
            Assert.True(Read(edge));
            StickAt(0, 55);
            Assert.True(Read(edge));
            StickAt(0, 65);
            Assert.False(Read(edge));
        }

        [Fact]
        public void Ring_InnerEdge_WithEngageRadii_LetsGoWhenTheDpadDisengages()
        {
            // The dpad's inverted edge inside 64.56 percent, its dead zone
            // engaging past 31.77 and letting go at 29.27.
            string edge = BoundedReads.Ring(64.5628, 84.5628, true, 1000.0 / 120, BoundedGate.None, Pad,
                31.7685, 29.2685);
            Finger(0.5f, 0.5f - 0.20f);
            Assert.True(Read(edge));
            Thread.Sleep(15);
            Finger(0.5f, 0.5f - 0.10f);
            Assert.False(Read(edge));
            Thread.Sleep(15);
            // Off center but disengaged: an un-engaged dpad's inverted edge
            // presses again as soon as its trigger is stepped.
            Assert.True(Read(edge));
        }

        [Fact]
        public void Ring_Parse_TakesTheDebounceAndAnOptionalEngage()
        {
            Assert.NotNull(BoundedReads.Parse("Ring 79.3 73.3 out 0 - - - Gamepad LeftStickRing"));
            Assert.NotNull(BoundedReads.Parse("Ring 64.56 84.56 in 8.3333 31.77 29.27 - Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Ring 79.3 73.3 out 0 31.77 - - Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Ring 79.3 73.3 out -1 - - - Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Ring 73.3 79.3 out 0 - - - Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Ring 79.3 73.3 out 0 - - - Gamepad LeftStickX"));
        }

        // ─── Level ─────────────────────────────────────────────────────

        [Fact]
        public void Level_SoftPullButton_IsThePullPast1600()
        {
            double soft = 1600.0 / 32767 * 100;
            string read = BoundedReads.Level(soft, soft, false, "-", BoundedGate.None, Trigger);
            Assert.False(Hold(read, 4.5, 1));
            Assert.True(Hold(read, 5.2, 1));
            Assert.False(Hold(read, 4.5, 1));
        }

        [Fact]
        public void Level_Component_HoldsBackAndReadsOneWay()
        {
            string east = BoundedReads.Level(7.3537, 4.8537, false, "X+", BoundedGate.None, Stick);
            StickAt(0, 7);
            Assert.False(Read(east));
            StickAt(0, 8);
            Assert.True(Read(east));
            StickAt(0, 6);
            Assert.True(Read(east));
            StickAt(0, 4);
            Assert.False(Read(east));
            StickAt(180, 50);
            Assert.False(Read(east));
        }

        [Fact]
        public void Level_Inner_IsHeldAboveZeroAndInside()
        {
            string inner = BoundedReads.Level(10, 20, true, "-", BoundedGate.None, Trigger);
            Assert.False(Hold(inner, 0, 1));
            Assert.True(Hold(inner, 5, 1));
            Assert.True(Hold(inner, 15, 1));
            Assert.False(Hold(inner, 25, 1));
            Assert.False(Hold(inner, 15, 1));
        }

        [Fact]
        public void Level_Parse_RejectsAComponentOnAOneWayInput_AndAClickGate()
        {
            Assert.Null(BoundedReads.Parse("Level 7 5 out X+ - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Level 7 5 out - - Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Level 7 5 out X+ click Gamepad LeftStickRing"));
            Assert.Null(BoundedReads.Parse("Level 5 7 out - - Gamepad LeftTrigger"));
            Assert.NotNull(BoundedReads.Parse("Level 7 5 out len - Gamepad LeftStickRing"));
        }

        // ─── Pull: the trigger mode's Soft Pull ────────────────────────

        [Fact]
        public void Pull_Simple_PressesPast38Percent_AndHoldsTo23()
        {
            // edge_binding_radius 10000 with 7.5 percent of hold-back: pressed
            // past 12457.5 (38.02 percent), held while past 7542.5 (23.02).
            string pull = BoundedReads.Pull(0, false, 10000, false, Trigger);
            Assert.False(Hold(pull, 0, 2));
            Assert.False(Hold(pull, 37, 2));
            Assert.True(Hold(pull, 39, 2));
            Assert.True(Hold(pull, 24, 2));
            Assert.False(Hold(pull, 22, 2));
        }

        [Fact]
        public void Pull_HairTrigger_LetsGoOnALetOff_AndPressesAgainOnASmallPull()
        {
            string pull = BoundedReads.Pull(1, false, 10000, false, Trigger);
            Assert.False(Hold(pull, 0, 2));
            Assert.True(Hold(pull, 80, 40));
            // Letting off to 60 percent releases it, well above the 23 percent
            // a Simple threshold would hold to.
            Assert.False(Hold(pull, 60, 40));
            // A small pull past the settled average presses again.
            Assert.True(Hold(pull, 66, 8));
        }

        [Fact]
        public void Pull_HairTrigger_ReleasesWithTheClick_AndRearmsBelowTheThreshold()
        {
            // The trigger mode's Hair Trigger drops its threshold to the bottom
            // while the click is held, and lifts it out of reach when the
            // click lets go, until the pull comes back below the threshold.
            string pull = BoundedReads.Pull(1, false, 10000, false, Trigger);
            Assert.False(Hold(pull, 0, 2));
            Assert.True(Hold(pull, 100, 40));
            Assert.False(Hold(pull, 90, 40));
            Assert.False(Hold(pull, 80, 40));
            Assert.False(Hold(pull, 20, 40));
            Assert.True(Hold(pull, 50, 8));
        }

        [Fact]
        public void Pull_HipFire_AQuickFullPullSkipsTheSoftPull_WhenTheClickIsBound()
        {
            // Hip Fire Normal waits 70 ms. The click (95 percent) inside the
            // window cancels the wait.
            string bound = BoundedReads.Pull(3, false, 10000, true, Trigger);
            Assert.False(Hold(bound, 0, 2));
            Assert.False(Hold(bound, 100, 150));
            Assert.False(Hold(bound, 60, 150));
            Assert.False(Hold(bound, 0, 2));
            // A slow pull waits the window out and presses.
            Assert.False(Hold(bound, 60, 50));
            Assert.True(Hold(bound, 60, 30));

            // With no click bound, Steam runs no click part, so the wait ends
            // in a press even at a full pull.
            string unbound = BoundedReads.Pull(3, false, 10000, false, Trigger);
            Assert.False(Hold(unbound, 0, 2));
            Assert.False(Hold(unbound, 100, 50));
            Assert.True(Hold(unbound, 100, 30));
        }

        [Fact]
        public void Pull_HipFireExclusive_TheClickGivesWayToAHeldSoftPull()
        {
            string soft = BoundedReads.Pull(5, false, 10000, true, Trigger);
            string full = BoundedReads.Pull(5, true, 10000, true, Trigger);
            Assert.False(Hold(soft, 0, 2));
            Assert.False(Hold(full, 0, 1));
            Assert.True(Hold(soft, 60, 80));
            Assert.False(Hold(full, 100, 30));
            Assert.True(Hold(soft, 100, 1));
            // Released and pulled fast: the click, and no soft pull.
            Assert.False(Hold(soft, 0, 5));
            Assert.True(Hold(full, 100, 5));
            Assert.False(Hold(soft, 100, 100));
        }

        [Fact]
        public void Pull_Parse_ChecksItsTokens()
        {
            Assert.NotNull(BoundedReads.Parse("Pull 1 Soft 10000 - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Pull 6 Soft 10000 - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Pull 1 Half 10000 - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Pull 1 Soft 40000 - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Pull 1 Soft 10000 - Gamepad LeftStickRing"));
        }

        // ─── Press: the Soft Press activator ───────────────────────────

        [Fact]
        public void Press_OnATrigger_CurvesThePull()
        {
            // activation_threshold 4096 with 5 percent of 32255 as hold-back,
            // on the pull to the power 0.75: pressed past 9.73 percent of the
            // pull, held to 3.21.
            string press = BoundedReads.Press(0, false, 4096, false, "-", "-", Trigger);
            Assert.False(Hold(press, 0, 2));
            Assert.False(Hold(press, 9, 2));
            Assert.True(Hold(press, 10.5, 2));
            Assert.True(Hold(press, 4, 2));
            Assert.False(Hold(press, 3, 2));
        }

        [Fact]
        public void Press_OnAComponent_ReadsTheCurvedWay_AndALengthStraight()
        {
            string north = BoundedReads.Press(0, false, 4096, false, "-", "Y-", Pad);
            string edge = BoundedReads.Press(0, false, 4096, false, "-", "len", Pad);
            Finger(0.5f, 0.5f);
            Assert.False(Read(north));
            Assert.False(Read(edge));
            // 12 percent up: curved 20.4 percent, past the 17.4 to press. A
            // length of 12 percent is short of it.
            Finger(0.5f, 0.5f - 0.06f);
            Assert.True(Read(north));
            Assert.False(Read(edge));
            Finger(0.5f, 0.5f + 0.06f);
            Assert.False(Read(north));
        }

        // ─── Press: the coordinator's cancels (0x13862d451, 0x138668df0) ──

        [Fact]
        public void Press_HipFire_ASiblingThresholdAboveItCancelsTheWait()
        {
            // Two Soft Presses on one input: the coordinator hands the lower
            // one the lowest sibling threshold above its own, and the value
            // crossing it inside the window cancels the wait.
            string press = BoundedReads.Press(2, false, 4096, false, "sib12000", "-", Trigger);
            Assert.True(Hold(press, 15, 45));
            Assert.False(Hold(press, 0, 2));
            Assert.False(Hold(press, 50, 45));
        }

        [Fact]
        public void Press_HipFire_AFullPressSibling_CancelsThroughItsOwnRead()
        {
            // With no Full Press or Chord sibling the coordinator returns
            // nothing, so a full pull inside the window cancels nothing.
            string alone = BoundedReads.Press(2, false, 4096, false, "-", "-", Trigger);
            Assert.True(Hold(alone, 100, 45));
            Assert.False(Hold(alone, 0, 2));
            // With one, the sentinel is the sibling's own read.
            string press = BoundedReads.Press(2, false, 4096, false,
                "fp{" + BoundedReads.Level(95, 95, false, "-", BoundedGate.None, Trigger) + "}", "-", Trigger);
            Assert.False(Hold(press, 100, 45));
            Assert.False(Hold(press, 0, 2));
            Assert.True(Hold(press, 50, 45));
        }

        [Fact]
        public void Press_HipFire_AFiredExclusiveSiblingCancels()
        {
            // The Exclusive sibling runs on the same value: it fires after its
            // own 60 ms window, and a waiting press then cancels (the
            // coordinator returns 1 for a pressed Exclusive). Style 3 waits
            // 70 ms, so the Exclusive lands inside the window.
            string press = BoundedReads.Press(3, false, 4096, false, "ex20000:out:-:0", "-", Trigger);
            Assert.False(Hold(press, 80, 85));
            Assert.False(Hold(press, 0, 2));
            Assert.True(Hold(press, 15, 85));
        }

        [Fact]
        public void Press_Simple_StackedPresses_CancelOnTheirPressTick()
        {
            // The client cancels before it fires, so even a Simple press dies
            // on its own press tick when the value lands past the sibling.
            string press = BoundedReads.Press(0, false, 4096, false, "sib12000", "-", Trigger);
            Assert.False(Hold(press, 50, 3));
            Assert.False(Hold(press, 0, 2));
            Assert.True(Hold(press, 15, 2));
        }

        [Fact]
        public void Press_Parse_TakesTheCancelTokens_AndRefusesANestedPress()
        {
            Assert.NotNull(BoundedReads.Parse(
                "Press 2 Soft 4096 out fp{Level 95 95 out - - Gamepad LeftTrigger} sib12000 ex20000:out:c:25000 - Gamepad LeftTrigger"));
            Assert.NotNull(BoundedReads.Parse("Press 2 Soft 4096 out click - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse(
                "Press 2 Soft 4096 out fp{Press 0 Soft 4096 out - - Gamepad LeftTrigger} - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Press 2 Soft 4096 out sibx - Gamepad LeftTrigger"));
            Assert.Null(BoundedReads.Parse("Press 2 Soft 4096 out ex20000:out:c - Gamepad LeftTrigger"));
            // The Soft and Full outputs of one activator share one machine,
            // cancel tokens included.
            var soft = BoundedReads.Parse("Press 5 Soft 4096 out sib12000 - Gamepad LeftTrigger");
            var full = BoundedReads.Parse("Press 5 Full 4096 out sib12000 - Gamepad LeftTrigger");
            Assert.Equal(soft.MachineKey, full.MachineKey);
        }
    }
}
