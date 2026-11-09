using System;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The reads a Steam dpad and four_buttons import takes, as
    /// steamclient64.dll's dpad mode (0x1388b2d30, 0x1388b3390) and
    /// four_buttons mode (0x1388b6ad0) run them: a radius to engage with a
    /// hold-back, the direction's angle with a margin, a cell's distance from
    /// its center, a cross gate's component, and the gates.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class SteamDpadReadTests : IDisposable
    {
        private readonly Func<int, bool> _engage = SourceCoercion.AimEngageStateProvider;
        private readonly Guid _device = Guid.NewGuid();
        private readonly CustomInputState _state = new();
        private long _now;

        // Steam's defaults: dead zone 10000 with 409.6 of hold-back, overlap
        // 4000 (67.5 degrees with a 2.7-degree margin), four_buttons at
        // 20000 with 18000 cells and 655.34 of hold-back.
        private const double Press = (10000 + 409.6) / 32767 * 100;
        private const double Release = (10000 - 409.6) / 32767 * 100;
        private const double CellCenter = 20000.0 / 32767 * 100;
        private const double CellPress = (18000 - 655.34) / 32767 * 100;
        private const double CellRelease = (18000 + 655.34) / 32767 * 100;
        private const string Ring = "Gamepad LeftStickRing";

        public SteamDpadReadTests()
        {
            for (int i = 0; i < _state.Axis.Length; i++) _state.Axis[i] = 32768;
        }

        public void Dispose() => SourceCoercion.AimEngageStateProvider = _engage;

        /// <summary>One device poll, then the read, as the input manager runs
        /// them.</summary>
        private bool Read(string descriptor)
        {
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            return SourceCoercion.EvaluateForButtonTarget(_state, new MappingSource { Descriptor = descriptor }, 50, 0,
                _device.ToString());
        }

        private bool ReadAgain(string descriptor)
            => SourceCoercion.EvaluateForButtonTarget(_state, new MappingSource { Descriptor = descriptor }, 50, 0,
                _device.ToString());

        /// <summary>The stick at a bearing (degrees counterclockwise from
        /// right) and a distance in percent.</summary>
        private void Stick(double degrees, double percent)
        {
            double r = degrees * Math.PI / 180.0;
            _state.Axis[0] = (int)Math.Round(32768 + 32767 * percent / 100 * Math.Cos(r));
            _state.Axis[1] = (int)Math.Round(32768 - 32767 * percent / 100 * Math.Sin(r));
        }

        private static string North(BoundedGate gate = BoundedGate.None, string pair = Ring)
            => BoundedReads.Wedge(90, 67.5, 2.7, Press, Release, gate, pair);

        [Fact]
        public void Wedge_EngagesPastThePressRadius_AndHoldsToTheReleaseRadius()
        {
            Stick(90, 31);
            Assert.False(Read(North()));
            Stick(90, 33);
            Assert.True(Read(North()));
            Thread.Sleep(15);
            Stick(90, 30);
            Assert.True(Read(North()));
            Stick(90, 28);
            Assert.False(Read(North()));
            Thread.Sleep(15);
            Stick(90, 31);
            Assert.False(Read(North()));
        }

        [Fact]
        public void Wedge_HoldsAChangeFor120thOfASecond()
        {
            Stick(90, 33);
            Assert.True(Read(North()));
            Stick(90, 10);
            Assert.True(Read(North()));
            Thread.Sleep(15);
            Assert.False(Read(North()));
        }

        [Theory]
        [InlineData(90, true)]
        [InlineData(150, true)]
        [InlineData(155, false)]
        [InlineData(30, true)]
        [InlineData(25, false)]
        [InlineData(270, false)]
        public void Wedge_ReadsTheDirectionWithinItsSpan_LessTheMargin(double bearing, bool reads)
        {
            Stick(bearing, 80);
            Assert.Equal(reads, Read(North()));
        }

        [Fact]
        public void Wedge_KeepsTheMarginPastTheEdge_OnceItPressed()
        {
            Stick(90 + 60, 80);
            Assert.True(Read(North()));
            Stick(90 + 66, 80);
            Assert.True(Read(North()));
            Stick(90 + 71, 80);
            Assert.False(Read(North()));
            Stick(90 + 66, 80);
            Assert.False(Read(North()));
        }

        [Fact]
        public void Wedge_East_ReadsAPushJustBelowTheAxis()
        {
            // Steam folds the angle off the direction into (-270, 90], so a
            // push at 350 degrees is 10 degrees off east, not 350.
            string east = BoundedReads.Wedge(0, 67.5, 2.7, Press, Release, BoundedGate.None, Ring);
            Stick(-10, 80);
            Assert.True(Read(east));
        }

        [Fact]
        public void Wedge_DiagonalPressesBothNeighbors_AsSteamsOverlapDoes()
        {
            string east = BoundedReads.Wedge(0, 67.5, 2.7, Press, Release, BoundedGate.None, Ring);
            Stick(45, 80);
            Assert.True(Read(North()));
            Assert.True(ReadAgain(east));
        }

        [Fact]
        public void Wedge_OnANumericLane_ReadsFullOrNothing()
        {
            // A chord's Custom row ANDs the direction with its partner, so the
            // read must answer on the axis lanes too.
            Stick(90, 80);
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            var src = new MappingSource { Descriptor = North() };
            Assert.Equal(1f, SourceCoercion.EvaluateForBipolarAxisTarget(_state, src, 0, false, _device.ToString()));
            Assert.Equal(1f, SourceCoercion.EvaluateForTriggerTarget(_state, src, 0, _device.ToString()));
            Stick(270, 80);
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(_state, src, 0, false, _device.ToString()));
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(_state, src, 0, _device.ToString()));
        }

        [Fact]
        public void Wedge_ReadTwiceInOnePoll_AnswersTheSame()
        {
            Stick(90, 33);
            Assert.True(Read(North()));
            Assert.True(ReadAgain(North()));
        }

        [Fact]
        public void Wedge_ALatchLeftUnreadAPoll_StartsOverAsSteamsReset()
        {
            Stick(90, 33);
            Assert.True(Read(North()));
            Thread.Sleep(15);
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            BoundedReads.StepPulls(_device, _device.ToString(), _state, _now++);
            Stick(90, 30);
            Assert.False(Read(North()));
        }

        [Fact]
        public void Wedge_EngagedGate_ReadsTheInputAtRestWhileTheGyroIsOff()
        {
            bool engaged = false;
            SourceCoercion.AimEngageStateProvider = slot => engaged;
            Stick(90, 80);
            Assert.False(Read(North(BoundedGate.Engaged)));
            engaged = true;
            Thread.Sleep(15);
            Assert.True(Read(North(BoundedGate.Engaged)));
        }

        private void Finger(float x, float y, bool click)
        {
            var pad = new TouchpadInputState(2);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            pad.Clicked = click;
            _state.Touchpads = new[] { pad };
            _state.Buttons[16] = click;
        }

        [Fact]
        public void Wedge_OnATrackpad_ReadsTheFingerFromTheCenter_AndTheClickGate()
        {
            const string pad = "Touchpad 0 Finger 0 Ring";
            Finger(0.5f, 0.1f, click: false);
            Assert.True(Read(North(BoundedGate.None, pad)));
            Assert.False(ReadAgain(North(BoundedGate.Click, pad)));
            Finger(0.5f, 0.1f, click: true);
            Assert.True(Read(North(BoundedGate.Click, pad)));
            _state.Touchpads[0].FingerDown[0] = false;
            Thread.Sleep(15);
            Assert.False(Read(North(BoundedGate.None, pad)));
        }

        private static string NorthCell(BoundedGate gate = BoundedGate.None)
            => BoundedReads.Disc(0, -CellCenter, CellPress, CellRelease, gate, Ring);

        [Fact]
        public void Disc_PressesWithinTheCell_AndReleasesPastIt()
        {
            // The north cell's center is 61.04 percent up. Full up is 38.96
            // from it, 30 percent right is 68 from it, 6 percent up is 55.04
            // (between the 52.93 press and the 56.93 release distances), and
            // 9 percent up is 52.04.
            Stick(90, 100);
            Assert.True(Read(NorthCell()));
            Stick(0, 30);
            Assert.False(Read(NorthCell()));
            Stick(90, 6);
            Assert.False(Read(NorthCell()));
            Stick(90, 9);
            Assert.True(Read(NorthCell()));
            Stick(90, 6);
            Assert.True(Read(NorthCell()));
        }

        [Fact]
        public void Disc_AFullDiagonal_PressesNoCell_AsSteamsCellsLeaveIt()
        {
            string east = BoundedReads.Disc(CellCenter, 0, CellPress, CellRelease, BoundedGate.None, Ring);
            Stick(45, 100);
            Assert.False(Read(NorthCell()));
            Assert.False(ReadAgain(east));
        }

        [Fact]
        public void Disc_AtRest_PressesNothing_AndKeepsTheCellsTrigger()
        {
            Stick(90, 100);
            Assert.True(Read(NorthCell()));
            Stick(0, 30);
            Assert.False(Read(NorthCell()));
            Stick(0, 0);
            Assert.False(Read(NorthCell()));
            // The trigger stayed released through the rest, so between the
            // two distances the cell still reads released.
            Stick(90, 6);
            Assert.False(Read(NorthCell()));
            Stick(90, 9);
            Assert.True(Read(NorthCell()));
        }

        [Fact]
        public void Disc_APressedCell_StaysPressedThroughARest()
        {
            // At rest Steam skips the cell's trigger, so a pressed cell comes
            // back pressed: 7 percent up is 54.04 from the north cell, between
            // the two distances.
            Stick(90, 100);
            Assert.True(Read(NorthCell()));
            Stick(0, 0);
            Assert.False(Read(NorthCell()));
            Stick(90, 7);
            Assert.True(Read(NorthCell()));
        }

        [Fact]
        public void Disc_ClickGate_NeedsTheClick()
        {
            string cell = BoundedReads.Disc(0, -CellCenter, CellPress, CellRelease, BoundedGate.Click,
                "Touchpad 0 Finger 0 Ring");
            Finger(0.5f, 0.0f, click: false);
            Assert.False(Read(cell));
            Finger(0.5f, 0.0f, click: true);
            Assert.True(Read(cell));
        }

        private static string CrossNorth()
            => BoundedReads.Hold(20, 0, Press, Release, 1, -1, BoundedGate.None, Ring);

        [Fact]
        public void Hold_NeedsTheDpadEngaged_AndTheComponentPast20Percent()
        {
            Stick(90, 25);
            Assert.False(Read(CrossNorth()));
            Thread.Sleep(15);
            Stick(90, 35);
            Assert.True(Read(CrossNorth()));
            // Mostly east: engaged, the northern component 12 percent, and the
            // component's trigger, once on, holds down to zero.
            Stick(20, 35);
            Assert.True(Read(CrossNorth()));
            Stick(-10, 35);
            Assert.False(Read(CrossNorth()));
        }

        [Fact]
        public void Pair_GyroTilt_Parses_AndAWrongPairDoesNot()
        {
            Assert.True(SourceCoercion.IsPairDescriptor("Gyro Tilt 27.4664 90"));
            Assert.True(SourceCoercion.IsPairDescriptor("Touchpad 1 Finger 0 Ring Left"));
            Assert.False(SourceCoercion.IsPairDescriptor("Gyro Tilt 90 27"));
            Assert.Null(BoundedReads.Parse("Wedge 90 67.5 2.7 31 29 - Gamepad LeftStickX"));
            Assert.Null(BoundedReads.Parse("Wedge 90 67.5 2.7 29 31 - Gamepad LeftStickRing"));
            Assert.NotNull(BoundedReads.Parse(North()));
        }
    }
}
