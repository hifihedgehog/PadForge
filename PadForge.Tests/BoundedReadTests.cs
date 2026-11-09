using System;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>#561: the reads one threshold cannot say. A reWASD zone holds
    /// its mapping only while the input is inside it, a DS4Windows key reads a
    /// stick by its angle, and a DS4Windows two-stage trigger remembers the
    /// pull, each as ProcessTwoStageTrigger and GetBoolActionMapping in
    /// Mapping.cs do.</summary>
    public class BoundedReadTests
    {
        private static CustomInputState Centered()
        {
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            return state;
        }

        private static bool Read(CustomInputState state, string descriptor, string device = "")
            => SourceCoercion.EvaluateForButtonTarget(state, new MappingSource { Descriptor = descriptor }, 50, 0, device);

        private static void Trigger(CustomInputState state, int percent) => state.Axis[2] = 65535 * percent / 100;

        // ── Between ─────────────────────────────────────────────────────

        [Fact]
        public void Between_ReadsFromTheLowEdge_ToTheHighEdge()
        {
            string zone = BoundedReads.Between(30, 66, false, false, "Gamepad LeftTrigger");
            var state = new CustomInputState();
            Trigger(state, 20);
            Assert.False(Read(state, zone));
            Trigger(state, 40);
            Assert.True(Read(state, zone));
            Trigger(state, 80);
            Assert.False(Read(state, zone));
        }

        [Fact]
        public void Between_OnAHalfAxis_ReadsOnlyItsHalf()
        {
            string up = BoundedReads.Between(30, 66, true, true, "Gamepad LeftStickY");
            var state = Centered();
            state.Axis[1] = 32768 - 32767 * 45 / 100;
            Assert.True(Read(state, up));
            state.Axis[1] = 32768 + 32767 * 45 / 100;
            Assert.False(Read(state, up));
        }

        [Fact]
        public void Between_OnAStickRing_ReadsTheDistanceFromCenter()
        {
            string ring = BoundedReads.Between(30, 66, false, false, "Gamepad LeftStickRing");
            var state = Centered();
            state.Axis[0] = 32768 - 32767 * 45 / 100;
            Assert.True(Read(state, ring));
            state.Axis[0] = 32768 - 32767 * 90 / 100;
            Assert.False(Read(state, ring));
        }

        // ── Sector ──────────────────────────────────────────────────────

        private static void Point(CustomInputState state, double degrees, double distance)
        {
            double r = degrees * Math.PI / 180.0;
            state.Axis[0] = (int)Math.Round(32768 + 32767 * distance * Math.Cos(r));
            state.Axis[1] = (int)Math.Round(32768 - 32767 * distance * Math.Sin(r));
        }

        [Theory]
        [InlineData(90, 30, true)]
        [InlineData(90, 150, true)]
        [InlineData(90, 160, false)]
        [InlineData(90, 270, false)]
        [InlineData(0, 300, true)]
        [InlineData(0, 280, false)]
        [InlineData(270, 210, true)]
        public void Sector_ReadsWithin67AndAHalfDegrees(double center, double angle, bool reads)
        {
            string sector = BoundedReads.Sector(center, 67.5, 10, "Gamepad LeftStickRing");
            var state = Centered();
            Point(state, angle, 0.8);
            Assert.Equal(reads, Read(state, sector));
        }

        [Fact]
        public void Sector_ReadsNothingInsideItsDeadZone()
        {
            string sector = BoundedReads.Sector(90, 67.5, 20, "Gamepad LeftStickRing");
            var state = Centered();
            Point(state, 90, 0.15);
            Assert.False(Read(state, sector));
            Point(state, 90, 0.25);
            Assert.True(Read(state, sector));
        }

        [Fact]
        public void Sector_AnEllipticDeadZone_ReadsByTheRadiusTowardThePush()
        {
            // 30 across and 50 up and down: reWASD's zone test takes
            // rx ry / hypot(rx sin, ry cos) toward the push.
            string sector = BoundedReads.Sector(0, 180, 30, 50, 0, 0, "Gamepad LeftStickRing");
            Assert.Equal("Sector 0 180 30:50 Gamepad LeftStickRing", sector);
            var state = Centered();
            Point(state, 90, 0.45);
            Assert.False(Read(state, sector));
            Point(state, 90, 0.55);
            Assert.True(Read(state, sector));
            Point(state, 0, 0.35);
            Assert.True(Read(state, sector));
            Point(state, 0, 0.25);
            Assert.False(Read(state, sector));
            // Toward 45 degrees the radius is 30 x 50 / hypot(21.2, 35.4), about 36.
            Point(state, 45, 0.34);
            Assert.False(Read(state, sector));
            Point(state, 45, 0.38);
            Assert.True(Read(state, sector));
        }

        [Fact]
        public void Sector_StopsAtItsOuterEdge()
        {
            string sector = BoundedReads.Sector(90, 67.5, 30, 30, 66, 80, "Gamepad LeftStickRing");
            Assert.Equal("Sector 90 67.5 30~66:80 Gamepad LeftStickRing", sector);
            var state = Centered();
            Point(state, 90, 0.75);
            Assert.True(Read(state, sector));
            Point(state, 90, 0.85);
            Assert.False(Read(state, sector));
            Point(state, 30, 0.70);
            Assert.False(Read(state, sector));
        }

        [Theory]
        [InlineData("Sector 90 67.5 30 Gamepad LeftStickRing", 30, 0, 0, 0)]
        [InlineData("Sector 90 67.5 30:30 Gamepad LeftStickRing", 30, 0, 0, 0)]
        [InlineData("Sector 90 67.5 30:50~66 Gamepad LeftStickRing", 30, 50, 66, 0)]
        [InlineData("Sector 90 67.5 30~66:80 Gamepad LeftStickRing", 30, 0, 66, 80)]
        public void Sector_ReadsItsRadii(string descriptor, int x, int y, int outerX, int outerY)
        {
            var read = BoundedReads.Parse(descriptor);
            Assert.Equal((x, y, outerX, outerY), (read.DeadZone, read.DeadZoneY, read.OuterX, read.OuterY));
        }

        [Fact]
        public void AFullTurnSector_IsLabeledAsAZoneOfTheRing()
        {
            string label = MappingDisplayResolver.ResolveDescriptorText(BoundedReads.Sector(0, 180, 30, 50, 66, 66, "Gamepad LeftStickRing"), null);
            Assert.Contains("30", label);
            Assert.Contains("66", label);
        }

        // ── Parsing ─────────────────────────────────────────────────────

        [Theory]
        [InlineData("Sector 90 67.5 30:50:60 Gamepad LeftStickRing")]
        [InlineData("Sector 90 67.5 30~0 Gamepad LeftStickRing")]
        [InlineData("Sector 90 67.5 30~66~80 Gamepad LeftStickRing")]
        [InlineData("Sector 90 67.5 101 Gamepad LeftStickRing")]
        [InlineData("Sector 90 67.5 30: Gamepad LeftStickRing")]
        [InlineData("Between 66 30 00 Gamepad LeftTrigger")]
        [InlineData("Between 0 30 00 Gamepad LeftTrigger")]
        [InlineData("Between 30 66 0 Gamepad LeftTrigger")]
        [InlineData("Between 30 66 00 Between 10 20 00 Gamepad LeftTrigger")]
        [InlineData("Sector 90 67.5 10 Gamepad LeftTrigger")]
        [InlineData("Stage Sometimes Soft 0 1 39 Gamepad LeftTrigger")]
        [InlineData("Stage HipFire Half 0 1 39 Gamepad LeftTrigger")]
        [InlineData("Stage HipFire Soft 0 1 39 Stage HipFire Soft 0 1 39 Gamepad LeftTrigger")]
        public void AMalformedOrNestedRead_ReadsNothing(string descriptor)
        {
            Assert.Null(BoundedReads.Parse(descriptor));
            var state = new CustomInputState();
            Trigger(state, 50);
            Assert.False(Read(state, descriptor));
        }

        [Fact]
        public void TheAnyDeviceGate_TestsTheInputInside()
        {
            Assert.Equal(1, SourceCoercion.NumberedAxesRead(BoundedReads.Between(30, 66, false, false, "Gamepad LeftTrigger"), out int first, out _));
            Assert.Equal(2, first);
            Assert.Equal(2, SourceCoercion.NumberedAxesRead(BoundedReads.Sector(90, 67.5, 10, "Gamepad LeftStickRing"), out first, out int second));
            Assert.Equal(0, first);
            Assert.Equal(1, second);
        }

        [Fact]
        public void Labels_NameTheInputAndItsNumbers()
        {
            string zone = MappingDisplayResolver.ResolveDescriptorText(BoundedReads.Between(30, 66, false, false, "Gamepad LeftTrigger"), null);
            Assert.Contains("30", zone);
            Assert.Contains("66", zone);
            string stage = MappingDisplayResolver.ResolveDescriptorText(BoundedReads.Stage(TwoStageMode.HipFire, true, 120, 1, 99, "Gamepad LeftTrigger"), null);
            Assert.Contains("120", stage);
        }

        // ── Two-stage pulls ─────────────────────────────────────────────

        [Fact]
        public void HairTrigger_FiresTheSoftPullFirst_BothAtAFullPull_AndNoSoftAfterIt()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HairTrigger, 0, true, false, 0);
            Assert.True(pull.Soft);
            Assert.False(pull.Full);
            pull.Step(TwoStageMode.HairTrigger, 0, true, true, 1);
            Assert.True(pull.Soft);
            Assert.True(pull.Full);
            // Backing off from a full pull fires neither until the trigger is back.
            pull.Step(TwoStageMode.HairTrigger, 0, true, false, 2);
            Assert.False(pull.Soft);
            Assert.False(pull.Full);
            pull.Step(TwoStageMode.HairTrigger, 0, false, false, 3);
            pull.Step(TwoStageMode.HairTrigger, 0, true, false, 4);
            Assert.True(pull.Soft);
        }

        [Fact]
        public void HipFire_WaitsBeforeAnything_ThenTheSoftPullKeepsBoth()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HipFire, 100, true, false, 0);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 50);
            Assert.False(pull.Soft);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 100);
            Assert.True(pull.Soft);
            pull.Step(TwoStageMode.HipFire, 100, true, true, 101);
            Assert.True(pull.Soft);
            Assert.True(pull.Full);
            pull.Step(TwoStageMode.HipFire, 100, false, false, 102);
            Assert.False(pull.Soft);
        }

        [Fact]
        public void HipFire_AFullPullBeforeTheWaitEnds_IsOnlyTheFullPull()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HipFire, 100, true, true, 0);
            pull.Step(TwoStageMode.HipFire, 100, true, true, 100);
            Assert.True(pull.Full);
            Assert.False(pull.Soft);
            // Backing off keeps the pull full-only until the release.
            pull.Step(TwoStageMode.HipFire, 100, true, false, 101);
            Assert.False(pull.Soft);
            Assert.False(pull.Full);
        }

        [Fact]
        public void HipFireExclusive_ASoftStartNeverReachesTheFullPull()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HipFireExclusive, 100, true, false, 0);
            pull.Step(TwoStageMode.HipFireExclusive, 100, true, false, 100);
            Assert.True(pull.Soft);
            pull.Step(TwoStageMode.HipFireExclusive, 100, true, true, 101);
            Assert.True(pull.Soft);
            Assert.False(pull.Full);
        }

        [Fact]
        public void HipFire_LetGoBeforeTheWaitEnds_FiresNothing()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HipFire, 100, true, false, 0);
            pull.Step(TwoStageMode.HipFire, 100, false, false, 40);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 60);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 120);
            Assert.False(pull.Soft);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 160);
            Assert.True(pull.Soft);
        }

        [Fact]
        public void APullNotSteppedForAWhile_StartsOver()
        {
            var pull = new TwoStagePull();
            pull.Step(TwoStageMode.HipFire, 100, true, false, 0);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 100);
            Assert.True(pull.Soft);
            pull.Step(TwoStageMode.HipFire, 100, true, false, 1000);
            Assert.False(pull.Soft);
        }

        [Fact]
        public void AStageRead_SharesOnePullPerTrigger_SteppedOncePerPoll()
        {
            var device = Guid.NewGuid();
            string guid = device.ToString();
            string soft = BoundedReads.Stage(TwoStageMode.HipFire, false, 100, 1, 39, "Gamepad LeftTrigger");
            string full = BoundedReads.Stage(TwoStageMode.HipFire, true, 100, 1, 39, "Gamepad LeftTrigger");
            var state = new CustomInputState();
            // The first read asks for the pull, and nothing reads before a step.
            Assert.False(Read(state, soft, guid));
            Assert.False(Read(state, full, guid));

            Trigger(state, 50);
            BoundedReads.StepPulls(device, guid, state, 1000);
            BoundedReads.StepPulls(device, guid, state, 1110);
            Assert.True(Read(state, soft, guid));
            Assert.False(Read(state, full, guid));
            Trigger(state, 100);
            BoundedReads.StepPulls(device, guid, state, 1111);
            Assert.True(Read(state, soft, guid));
            Assert.True(Read(state, full, guid));
            // The soft pull's own read still needs the trigger past its press.
            Trigger(state, 20);
            BoundedReads.StepPulls(device, guid, state, 1112);
            Assert.False(Read(state, soft, guid));
        }

        /// <summary>The parse cache is process-wide. Once it held 256
        /// descriptors it took no more, so every read of a later profile's
        /// zone parsed and allocated on the poll thread. Full, it starts over,
        /// and the descriptor in use is cached again.</summary>
        [Fact]
        public void TheParseCache_KeepsCachingPastItsCap()
        {
            for (int i = 0; i < 300; i++)
                Assert.NotNull(BoundedReads.Parse(BoundedReads.Between(10, 20, false, false, "Axis " + i)));

            string d = BoundedReads.Between(10, 20, false, false, "Axis 999");
            Assert.Same(BoundedReads.Parse(d), BoundedReads.Parse(d));
        }

        /// <summary>A sector is named by the nearest of the eight directions.
        /// Only 0, 90 and 180 had names, so a sector at 45 or 315 was called
        /// Down.</summary>
        [Theory]
        [InlineData(0, "Right")]
        [InlineData(45, "UpRight")]
        [InlineData(90, "Up")]
        [InlineData(135, "UpLeft")]
        [InlineData(180, "Left")]
        [InlineData(225, "DownLeft")]
        [InlineData(270, "Down")]
        [InlineData(315, "DownRight")]
        [InlineData(350, "Right")]
        public void ASector_IsNamedByItsNearestDirection(double center, string direction)
        {
            string text = MappingDisplayResolver.ResolveDescriptorText(
                BoundedReads.Sector(center, 22.5, 10, "Gamepad LeftStickRing"), null);
            Assert.Contains(MappingDisplayResolver.ResolvePovDirection(direction), text);
        }
    }

    /// <summary>A zone, a stick sector or a two-stage pull reads one input. A
    /// layer activator held on that input, or a macro consuming it, holds a row
    /// reading it back, and the row's bounded read of the same input is held
    /// back with it.</summary>
    [Collection("SettingsManagerStatics")]
    public class BoundedReadSuppressionTests
    {
        [Fact]
        public void ABoundedReadOfAnActivatorsInput_IsHeldBackWithIt()
        {
            const int slot = 13;
            PadForge.Common.Input.InputManager.ClearAllShiftRuntime();
            try
            {
                var ms = new MappingSet();
                ms.ShiftActivators.Add(new ShiftActivator
                {
                    DeviceGuid = "", Descriptor = "Axis 5", Mode = "Hold",
                    LayerMask = "Aim", LayerName = "Aim", Kind = "Axis", DelayMs = 0,
                });
                var state = new CustomInputState();
                state.Axis[5] = 65535;
                Assert.Equal("Aim", PadForge.Common.Input.InputManager.ResolveActiveLayerMask(slot, ms, state, ""));

                // Positive control: the plain read of the input is held back.
                Assert.True(PadForge.Common.Input.InputManager.IsSourceSuppressedPostpone(slot, "", "Axis 5"));
                Assert.True(PadForge.Common.Input.InputManager.IsSourceSuppressedPostpone(slot, "",
                    BoundedReads.Between(30, 66, false, false, "Axis 5")));
                Assert.True(PadForge.Common.Input.InputManager.IsSourceSuppressedPostpone(slot, "",
                    BoundedReads.Stage(TwoStageMode.HipFire, true, 100, 10, 99, "Gamepad RightTrigger")));
                // Another input stays live.
                Assert.False(PadForge.Common.Input.InputManager.IsSourceSuppressedPostpone(slot, "",
                    BoundedReads.Between(30, 66, false, false, "Axis 2")));
            }
            finally
            {
                PadForge.Common.Input.InputManager.ClearAllShiftRuntime();
            }
        }
    }
}
