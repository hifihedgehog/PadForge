using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #597: the Mouse Travel input. A mouse's raw counts are added up and
    /// held as a position from -1 to 1. These hold the wheel's math (exact
    /// in time, no count lost, the lock, the four kinds of centering, the
    /// steer input, Lock at Speed and Smoothing), how a row reads the
    /// position as a stick, a trigger and a button, and the saved form.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseTravelTests : IDisposable
    {
        private const string Target = "LeftThumbAxisX";

        private readonly Func<(double speed, double grip, bool lockAtSpeed)> _savedRoad = SourceCoercion.MouseTravelRoadProvider;
        private readonly Func<int, double> _savedForce = SourceCoercion.MouseTravelForceProvider;

        public MouseTravelTests()
        {
            SourceCoercion.MouseTravelRoadProvider = null;
            SourceCoercion.MouseTravelForceProvider = null;
        }

        public void Dispose()
        {
            SourceCoercion.MouseTravelRoadProvider = _savedRoad;
            SourceCoercion.MouseTravelForceProvider = _savedForce;
        }

        // ── Builders ──

        private static MappingSource Travel(string axis = "X") => new()
        {
            Kind = "Direct",
            Descriptor = "Mouse Travel " + axis,
        };

        private static CustomInputState Mouse(int dx = 0, int dy = 0, bool steerDown = false)
        {
            var s = new CustomInputState { MouseRawDX = dx, MouseRawDY = dy };
            s.Buttons[2] = steerDown;
            return s;
        }

        /// <summary>One pass of the wheel itself: a new frame, the mouse's
        /// counts and the pass's length. The answer is the wheel's own
        /// number, before a row narrows it to the output's precision.</summary>
        private static double Tick(SourceKindRuntime rt, MappingSource src, int counts, double dt)
        {
            rt.FrameSeq++;
            return rt.TickMouseTravel(0, Target, 0, src, Mouse(counts), dt, steerHeld: false);
        }

        /// <summary>One pass of the row as a stick's axis, through the
        /// evaluator a mapping row reads with, so the row's steer input is
        /// read off the mouse's own right button.</summary>
        private static double Row(SourceKindRuntime rt, MappingSource src, int counts, double dt,
            bool steerDown = false)
        {
            rt.FrameSeq++;
            return SourceEvaluator.EvaluateForBipolarAxisTarget(
                Mouse(counts, 0, steerDown), src, 0, Target, 0, rt, dt);
        }

        /// <summary>The ticks in a second that every rate below divides:
        /// 60, 250, 1,000 and 10,000 passes a second, and a mouse that
        /// reports 1,000 times a second.</summary>
        private const int TicksPerSecond = 30000;

        public static IEnumerable<object[]> Rates() => new[]
        {
            new object[] { 60 }, new object[] { 250 }, new object[] { 1000 }, new object[] { 10000 },
        };

        /// <summary>Runs a mouse's reports through the wheel at a rate and
        /// answers where it ends. A report is the counts the mouse sends at
        /// one of its 1,000 reports a second, and a pass takes the reports
        /// that came since the pass before it, as Raw Input hands them to
        /// the engine.</summary>
        private static double Run(MappingSource src, int hz, int[] reports, double seconds)
        {
            var rt = new SourceKindRuntime();
            int ticksPerPass = TicksPerSecond / hz;
            const int ticksPerReport = TicksPerSecond / 1000;
            int passes = (int)Math.Round(seconds * hz);
            int nextReport = 0;
            double last = 0;
            for (int p = 1; p <= passes; p++)
            {
                int now = p * ticksPerPass;
                int counts = 0;
                while (nextReport < reports.Length && (nextReport + 1) * ticksPerReport <= now)
                    counts += reports[nextReport++];
                last = Tick(rt, src, counts, 1.0 / hz);
            }
            return last;
        }

        /// <summary>A mouse swept one way for half a second and then left
        /// alone: two counts a report, a thousand reports a second.</summary>
        private static int[] Sweep()
        {
            var reports = new int[500];
            Array.Fill(reports, 2);
            return reports;
        }

        // ── Exact in time ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void TheSameCountsGiveTheSameWheelAtEveryRate(int hz)
        {
            // A hand that moves 600 counts right and 150 back, in uneven
            // reports. Held, the wheel is the sum of the counts and nothing
            // else, so every rate answers the same number to the last bit.
            var reports = new int[400];
            for (int i = 0; i < 300; i++) reports[i] = i % 3 == 0 ? 4 : 1;      // 100 * 4 + 200 * 1 = 600
            for (int i = 300; i < 400; i++) reports[i] = i % 2 == 0 ? -3 : 0;   // 50 * -3 = -150
            double expected = (600 - 150) / MappingSource.DefaultTravelCounts;
            Assert.Equal(expected, Run(Travel(), hz, reports, 1.0));
        }

        [Fact]
        public void TheSpringGivesTheSameWheelAtEveryRate_WithinOneReportOfTravel()
        {
            // With the spring on, a pass takes its counts as arriving evenly
            // through it, and a mouse's reports arrive at points in time. So
            // two rates differ by no more than a report's worth of travel,
            // here 2 counts of 2,000, a thousandth of the lock.
            var src = Travel();
            src.ParamTravelCentering = MappingSource.TravelCenteringSpring;
            const double oneReport = 2 / MappingSource.DefaultTravelCounts;

            // At the end of the sweep, and half a second after it.
            foreach (double seconds in new[] { 0.5, 1.0 })
            {
                double at1000 = Run(src, 1000, Sweep(), seconds);
                foreach (int hz in new[] { 60, 250, 10000 })
                    Assert.InRange(Run(src, hz, Sweep(), seconds), at1000 - oneReport, at1000 + oneReport);
            }

            // What it holds against: 2,000 counts a second into a spring of
            // tau = 0.75 s / 3 settles toward r * tau / L = 0.25, and half
            // a second in it has come 1 - e^-2 of the way.
            double swept = Run(src, 1000, Sweep(), 0.5);
            Assert.InRange(swept, 0.25 * (1 - Math.Exp(-2)) - oneReport, 0.25 * (1 - Math.Exp(-2)) + oneReport);
        }

        [Fact]
        public void NoCountIsLost_HoweverSmallThePasses()
        {
            // Fifty thousand passes of one, two and three counts, which
            // add up to 30,000 and stay inside the lock all the way. A
            // position kept as a fraction of the lock would round at each
            // step. Kept in counts, the sum is whole and exact.
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelCounts = 50000;
            long sum = 0;
            double last = 0;
            for (int i = 0; i < 50000; i++)
            {
                int counts = i % 5 == 0 ? -3 : (i % 2 == 0 ? 2 : 1);
                sum += counts;
                last = Tick(rt, src, counts, 0.0001);
            }
            Assert.Equal(30000, sum);
            Assert.Equal(sum / 50000.0, last);
        }

        [Fact]
        public void TheLockKeepsNothingPastIt_SoAMoveBackSteersAtOnce()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            // 5,000 counts into a lock at 2,000.
            Assert.Equal(1.0, Tick(rt, src, 5000, 0.001));
            // The first hundred counts back are a twentieth of the lock.
            Assert.Equal(0.95, Tick(rt, src, -100, 0.001));
            Assert.Equal(-1.0, Tick(rt, src, -9000, 0.001));
            Assert.Equal(-0.5, Tick(rt, src, 1000, 0.001));
        }

        [Fact]
        public void AMouseMovesOnlyTheAxisItsInputNames()
        {
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            var state = Mouse(dx: 200, dy: -500);
            Assert.Equal(0.1, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, Travel("X"), state, 0.001, false));
            Assert.Equal(-0.25, rt.TickMouseTravel(0, "LeftThumbAxisY", 0, Travel("Y"), state, 0.001, false));
        }

        // ── Spring ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void TheSpringComes95PercentBackInTheReturnTime(int hz)
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelCentering = MappingSource.TravelCenteringSpring;
            src.ParamTravelReturnTime = 1.5;
            // The first pass has no time in it, so it only places the wheel.
            double start = Tick(rt, src, 1600, 0);
            Assert.Equal(0.8, start);
            double now = start;
            int passes = (int)Math.Round(1.5 * hz);
            for (int i = 0; i < passes; i++) now = Tick(rt, src, 0, 1.0 / hz);
            // e^-3 of it is left: 4.98%.
            Assert.InRange(now / start, 0.0497, 0.0499);
        }

        [Fact]
        public void CenteringOffLeavesTheWheelWhereTheHandLeftIt()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            Tick(rt, src, 700, 0.001);
            double held = 0;
            for (int i = 0; i < 5000; i++) held = Tick(rt, src, 0, 0.001);
            Assert.Equal(0.35, held);
        }

        // ── Steer ──

        private static MappingSource Steered(string steer, string centering)
        {
            var src = Travel();
            src.ParamTravelSteer = steer;
            src.ParamTravelCentering = centering;
            src.GateDescriptor = "Button 2";
            return src;
        }

        [Fact]
        public void WhileHeld_TheMouseSteersOnlyWhileTheInputIsDown_AndTheWheelStays()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerHeld, MappingSource.TravelCenteringOff);
            Assert.Equal(0.0, Row(rt, src, 400, 0.001));                        // not held: the mouse looks around
            Assert.Equal(0.2, Row(rt, src, 400, 0.001, steerDown: true), 6);    // held: it steers
            Assert.Equal(0.3, Row(rt, src, 200, 0.001, steerDown: true), 6);
            // Let go with centering off, the wheel stays where it is.
            double left = 0;
            for (int i = 0; i < 2000; i++) left = Row(rt, src, 50, 0.001);
            Assert.Equal(0.3, left, 6);
        }

        [Fact]
        public void WhileHeld_WithASpring_TheWheelHoldsInTheHandAndComesBackOutOfIt()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerHeld, MappingSource.TravelCenteringSpring);
            Row(rt, src, 1000, 0.001, steerDown: true);
            double held = 0;
            for (int i = 0; i < 1000; i++) held = Row(rt, src, 0, 0.001, steerDown: true);
            Assert.Equal(0.5, held, 6);                                         // a held wheel has no spring on it

            double released = 0;
            for (int i = 0; i < 750; i++) released = Row(rt, src, 30, 0.001);   // let go: the mouse moves nothing
            Assert.InRange(released / 0.5, 0.0497, 0.0499);                     // and the Return Time brings it back
        }

        [Fact]
        public void Toggle_EachPressTurnsSteeringOnOrOff()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerToggle, MappingSource.TravelCenteringOff);
            Assert.Equal(0.0, Row(rt, src, 100, 0.001));                        // off until a press
            Assert.Equal(0.05, Row(rt, src, 100, 0.001, steerDown: true), 6);   // the press turns it on
            Assert.Equal(0.10, Row(rt, src, 100, 0.001, steerDown: true), 6);   // holding it changes nothing
            Assert.Equal(0.15, Row(rt, src, 100, 0.001), 6);                    // nor does letting go
            Assert.Equal(0.15, Row(rt, src, 100, 0.001, steerDown: true), 6);   // the next press turns it off
            Assert.Equal(0.15, Row(rt, src, 100, 0.001), 6);
            Assert.Equal(0.20, Row(rt, src, 100, 0.001, steerDown: true), 6);   // and the one after that, on
        }

        [Fact]
        public void Toggle_WithASpring_TheWheelComesBackWhileSteeringIsOff()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerToggle, MappingSource.TravelCenteringSpring);
            Row(rt, src, 0, 0.001);
            Row(rt, src, 1000, 0.001, steerDown: true);
            double on = 0;
            for (int i = 0; i < 500; i++) on = Row(rt, src, 0, 0.001);
            Assert.Equal(0.5, on, 6);                                           // on, the wheel holds

            Row(rt, src, 0, 0, steerDown: true);                                // off
            double off = 0;
            for (int i = 0; i < 750; i++) off = Row(rt, src, 0, 0.001);
            Assert.InRange(off / 0.5, 0.0497, 0.0499);
        }

        [Fact]
        public void APressAlreadyDownWhenTheRowIsFirstReadTurnsNothingOn()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerToggle, MappingSource.TravelCenteringOff);
            Assert.Equal(0.0, Row(rt, src, 100, 0.001, steerDown: true));
            Assert.Equal(0.0, Row(rt, src, 100, 0.001, steerDown: true));
            Assert.Equal(0.0, Row(rt, src, 100, 0.001));
            Assert.Equal(0.05, Row(rt, src, 100, 0.001, steerDown: true), 6);
        }

        [Fact]
        public void WithNoInputPicked_TheMouseAlwaysSteers_WhateverSteerSays()
        {
            foreach (string steer in new[] { MappingSource.TravelSteerAlways, MappingSource.TravelSteerHeld, MappingSource.TravelSteerToggle })
            {
                var src = Travel();
                src.ParamTravelSteer = steer;
                Assert.Equal(0.1, Row(new SourceKindRuntime(), src, 200, 0.001), 6);
            }
        }

        /// <summary>A gate means on this source what it means on every
        /// other: the source counts only while its gate reads pressed. A
        /// file that holds one beside Always (a hand edit, an import) is
        /// read as While Held, and the editor shows it so.</summary>
        [Fact]
        public void AnInputBesideAlways_HoldsTheMouseAsWhileHeldDoes()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerAlways, MappingSource.TravelCenteringOff);
            Assert.Equal(0.0, Row(rt, src, 400, 0.001));
            Assert.Equal(0.1, Row(rt, src, 200, 0.001, steerDown: true), 6);
            Assert.Equal(0.1, Row(rt, src, 400, 0.001), 6);
        }

        [Fact]
        public void TheGateOfAnyOtherInput_StillReadsNothingWhileItIsOpen()
        {
            var rt = new SourceKindRuntime();
            rt.FrameSeq++;
            var axis = new MappingSource { Kind = "Direct", Descriptor = "Axis 0", GateDescriptor = "Button 2" };
            var state = Mouse();
            state.Axis[0] = 65535;
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(state, axis, 0, Target, 0, rt, 0.001));
            state.Buttons[2] = true;
            Assert.Equal(1f, SourceEvaluator.EvaluateForBipolarAxisTarget(state, axis, 0, Target, 0, rt, 0.001), 3);
        }

        // ── From Telemetry ──

        /// <summary>What is left of a wheel a quarter second after it is
        /// let go, as a share of where it stood.</summary>
        private static double TelemetryLeft(double kmh, double grip, double start = 0.02, double seconds = 0.25)
        {
            SourceCoercion.MouseTravelRoadProvider = () => (kmh / 3.6, grip, false);
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
            double placed = Tick(rt, src, (int)Math.Round(start * MappingSource.DefaultTravelCounts), 0);
            double now = placed;
            int passes = (int)Math.Round(seconds * 1000);
            for (int i = 0; i < passes; i++) now = Tick(rt, src, 0, 0.001);
            return now / placed;
        }

        [Fact]
        public void FromTelemetry_HoldsStillAtAStandstill()
        {
            Assert.Equal(1.0, TelemetryLeft(kmh: 0, grip: 1.0));
        }

        [Fact]
        public void FromTelemetry_ReturnsFasterWithSpeed()
        {
            double slow = TelemetryLeft(kmh: 20, grip: 1.0);
            double town = TelemetryLeft(kmh: 50, grip: 1.0);
            double road = TelemetryLeft(kmh: 100, grip: 1.0);
            // What is left of the wheel falls as the car goes faster.
            Assert.True(1.0 > slow && slow > town && town > road,
                $"left after a quarter second: {slow:0.000} at 20, {town:0.000} at 50, {road:0.000} at 100 km/h");
        }

        /// <summary>#600 item 41: near center the reference car is the
        /// spring, so the Return Time is the time there. Further out a car's
        /// steering pulls softer than a spring would, so the wheel comes back
        /// more slowly from far out than the spring brings it.</summary>
        [Fact]
        public void FromTelemetry_At100KmhOnADryRoad_IsTheSpringNearCenter_AndSofterFurtherOut()
        {
            var spring = Travel();
            spring.ParamTravelCentering = MappingSource.TravelCenteringSpring;
            var rt = new SourceKindRuntime();
            double placed = Tick(rt, spring, 1, 0), plain = placed;
            for (int i = 0; i < 250; i++) plain = Tick(rt, spring, 0, 0.001);

            // A wheel let go one count from center: the car's pull is the
            // spring's within a hair.
            Assert.Equal(plain / placed, TelemetryLeft(kmh: 100, grip: 1.0, start: 1.0 / MappingSource.DefaultTravelCounts), 3);
            // Half a lock out, more is left after the same time than the
            // spring leaves: the pull is softer there.
            Assert.True(TelemetryLeft(kmh: 100, grip: 1.0, start: 0.5) > plain / placed + 0.01);

            // The reference car's own return from 40 counts out.
            plain = TelemetryLeft(kmh: 100, grip: 1.0, start: 40.0 / MappingSource.DefaultTravelCounts)
                * Tick(new SourceKindRuntime(), Travel(), 40, 0);

            // No game sending: the car is taken at the reference.
            SourceCoercion.MouseTravelRoadProvider = () => (-1, 0, false);
            var none = Travel();
            none.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
            var silent = new SourceKindRuntime();
            double left = Tick(silent, none, 40, 0);
            for (int i = 0; i < 250; i++) left = Tick(silent, none, 0, 0.001);
            Assert.Equal(plain, left, 12);

            // And with no provider at all.
            SourceCoercion.MouseTravelRoadProvider = null;
            var unwired = new SourceKindRuntime();
            left = Tick(unwired, none, 40, 0);
            for (int i = 0; i < 250; i++) left = Tick(unwired, none, 0, 0.001);
            Assert.Equal(plain, left, 12);
        }

        [Fact]
        public void FromTelemetry_IsLightWhereTheTiresSlide()
        {
            // Ice against a dry road at the same speed and the same wheel.
            double dry = TelemetryLeft(kmh: 100, grip: 1.0, start: 0.2);
            double ice = TelemetryLeft(kmh: 100, grip: RoadGrip.Ice, start: 0.2);
            Assert.True(ice > dry, $"left after a quarter second: {ice:0.000} on ice, {dry:0.000} on a dry road");
        }

        /// <summary>#600 item 41: the car's shape survives. At the
        /// reference the answer is 1 at center and falls as the wheel goes
        /// out, the same either way, a faster car pulls harder at every
        /// angle, and nothing pulls at rest.</summary>
        [Fact]
        public void TheTires_AnswerOneAtCenterOfTheReference_SofterFurtherOut_NothingAtRest_AndNeverLessThanNothing()
        {
            var tires = new TireReturn();
            Assert.Equal(1.0, tires.Scale(0.0, TireReturn.ReferenceSpeed, RoadGrip.Dry), 12);
            double last = 1.0 + 1e-9;
            foreach (double position in new[] { 0.001, 0.05, 0.3, 0.6, 1.0 })
            {
                double scale = tires.Scale(position, TireReturn.ReferenceSpeed, RoadGrip.Dry);
                Assert.True(scale <= last, $"{scale} at {position} after {last}");
                Assert.Equal(scale, tires.Scale(-position, TireReturn.ReferenceSpeed, RoadGrip.Dry), 12);
                last = scale;
            }
            Assert.True(tires.Scale(0.6, TireReturn.ReferenceSpeed, RoadGrip.Dry) < 0.9);
            // Firmer with speed where a car is steered at speed, near
            // center. Far out a fast car's front tires are past their peak,
            // where the pull lightens.
            foreach (double position in new[] { 0.0, 0.01, 0.05 })
                Assert.True(tires.Scale(position, 40.0, RoadGrip.Dry) > tires.Scale(position, 15.0, RoadGrip.Dry), position.ToString());
            Assert.Equal(0.0, tires.Scale(0.3, 0, RoadGrip.Dry));
            Assert.Equal(0.0, tires.Scale(0.3, double.NaN, RoadGrip.Dry));
            for (double speed = 0.05; speed < 90; speed += 1.7)
                foreach (double grip in new[] { 0.0, RoadGrip.Ice, RoadGrip.Wet, 1.0, 9.0, double.NaN })
                    foreach (double position in new[] { 0.0, 0.01, 0.25, 1.0 })
                    {
                        double scale = tires.Scale(position, speed, grip);
                        Assert.True(scale >= 0 && double.IsFinite(scale), $"{scale} at {speed} m/s, grip {grip}, wheel {position}");
                    }
            // Under the model's least speed the pull goes on falling to rest.
            double crawl = tires.Scale(0.05, 0.25, RoadGrip.Dry), walk = tires.Scale(0.05, 0.5, RoadGrip.Dry);
            Assert.True(crawl > 0 && crawl < walk, $"{crawl} at a crawl, {walk} at a walk");
        }

        // ── From the Game's Force ──

        [Fact]
        public void FromTheGamesForce_AForceTurnsTheWheel_AndNoForceLeavesIt()
        {
            double force = 0;
            SourceCoercion.MouseTravelForceProvider = slot => slot == 0 ? force : 0;
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelCentering = MappingSource.TravelCenteringGameForce;

            Tick(rt, src, 1000, 0);
            double still = 0;
            for (int i = 0; i < 500; i++) still = Tick(rt, src, 0, 0.001);
            Assert.Equal(0.5, still);                                            // no force: the wheel stays

            // A force from the positive side pushes toward lower values. Half
            // of full force for two tenths of a second is a tenth of the lock.
            force = 0.5;
            double pushed = 0;
            for (int i = 0; i < 200; i++) pushed = Tick(rt, src, 0, 0.001);
            Assert.Equal(0.4, pushed, 9);

            force = -1.0;                                                        // and one from the other side, higher
            for (int i = 0; i < 100; i++) pushed = Tick(rt, src, 0, 0.001);
            Assert.Equal(0.5, pushed, 9);

            // The hand still steers against it, and the lock still holds.
            force = 0;
            Assert.Equal(0.6, Tick(rt, src, 200, 0.001), 9);
            force = -1.0;
            for (int i = 0; i < 2000; i++) pushed = Tick(rt, src, 0, 0.001);
            Assert.Equal(1.0, pushed);
        }

        [Fact]
        public void FromTheGamesForce_WithNoGameWired_HoldsTheWheel()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelCentering = MappingSource.TravelCenteringGameForce;
            Tick(rt, src, 600, 0);
            double held = 0;
            for (int i = 0; i < 300; i++) held = Tick(rt, src, 0, 0.001);
            Assert.Equal(0.3, held);
        }

        [Fact]
        public void FromTheGamesForce_WhileHeld_OnlyTurnsAWheelTheHandLetGo()
        {
            SourceCoercion.MouseTravelForceProvider = _ => 1.0;
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerHeld, MappingSource.TravelCenteringGameForce);
            Row(rt, src, 1000, 0.001, steerDown: true);
            double held = 0;
            for (int i = 0; i < 100; i++) held = Row(rt, src, 0, 0.001, steerDown: true);
            Assert.Equal(0.5, held, 6);
            double released = 0;
            for (int i = 0; i < 100; i++) released = Row(rt, src, 0, 0.001);
            Assert.Equal(0.4, released, 6);
        }

        // ── Lock at Speed ──

        [Theory]
        [InlineData(200.0, 50.0, 0.5)]        // the setting is what is left at 200 km/h
        [InlineData(100.0, 50.0, 2.0 / 3.0)]  // 1 / (1 + k v) with k = 1 / 200 for 50%
        [InlineData(0.0, 50.0, 1.0)]          // none taken at rest
        [InlineData(200.0, 20.0, 0.2)]
        [InlineData(200.0, 100.0, 1.0)]       // 100% leaves the lock alone at any speed
        public void LockAtSpeed_ScalesTheOutputWithSpeed(double kmh, double setting, double left)
        {
            SourceCoercion.MouseTravelRoadProvider = () => (kmh / 3.6, 1.0, true);
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelLockAtSpeed = setting;
            Assert.Equal(0.8 * left, Tick(rt, src, 1600, 0.001), 9);
            // The wheel itself is where the mouse put it: the lock is a full
            // 2,000 counts away at any speed.
            Assert.Equal(1.0 * left, Tick(rt, src, 400, 0.001), 9);
        }

        [Fact]
        public void LockAtSpeed_TakesNothingWhileGameTelemetryIsOff_OrNoGameSendsASpeed()
        {
            var src = Travel();
            src.ParamTravelLockAtSpeed = 50;
            SourceCoercion.MouseTravelRoadProvider = () => (200 / 3.6, 1.0, false);
            Assert.Equal(0.8, Tick(new SourceKindRuntime(), src, 1600, 0.001));
            SourceCoercion.MouseTravelRoadProvider = () => (-1, 0, true);
            Assert.Equal(0.8, Tick(new SourceKindRuntime(), src, 1600, 0.001));
            SourceCoercion.MouseTravelRoadProvider = null;
            Assert.Equal(0.8, Tick(new SourceKindRuntime(), src, 1600, 0.001));
        }

        [Fact]
        public void TheRoadIsAskedForOnlyByASourceThatUsesIt_AndOnceAFrame()
        {
            int asked = 0;
            SourceCoercion.MouseTravelRoadProvider = () => { asked++; return (30, 1.0, true); };
            var rt = new SourceKindRuntime();
            Tick(rt, Travel(), 100, 0.001);                                       // held, lock at 100%
            var spring = Travel();
            spring.ParamTravelCentering = MappingSource.TravelCenteringSpring;
            Tick(rt, spring, 100, 0.001);
            Assert.Equal(0, asked);

            var telemetry = Travel();
            telemetry.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
            var locked = Travel();
            locked.ParamTravelLockAtSpeed = 60;
            rt.FrameSeq++;
            var state = Mouse(10);
            rt.TickMouseTravel(0, "LeftThumbAxisX", 0, telemetry, state, 0.001, false);
            rt.TickMouseTravel(0, "RightThumbAxisX", 0, locked, state, 0.001, false);
            Assert.Equal(1, asked);
            rt.FrameSeq++;
            rt.TickMouseTravel(0, "RightThumbAxisX", 0, locked, state, 0.001, false);
            Assert.Equal(2, asked);
        }

        // ── Smoothing ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void Smoothing_FollowsTheWheelByItsTimeConstant_AtEveryRate(int hz)
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelSmoothingMs = 50;
            // The wheel jumps to 0.5 in a pass with no time in it, and the
            // output has not moved yet.
            Assert.Equal(0.0, Tick(rt, src, 1000, 0));
            double now = 0;
            int passes = (int)Math.Round(0.1 * hz);
            for (int i = 0; i < passes; i++) now = Tick(rt, src, 0, 1.0 / hz);
            // Two time constants later it is 1 - e^-2 of the way there.
            Assert.Equal(0.5 * (1 - Math.Exp(-2)), now, 9);
        }

        // ── Center Mouse Steering ──

        [Fact]
        public void CenterMouseTravel_PutsEveryWheelOfTheSlotStraight()
        {
            var rt = new SourceKindRuntime();
            var x = Travel("X");
            var y = Travel("Y");
            y.ParamTravelSmoothingMs = 20;
            rt.FrameSeq++;
            var state = Mouse(dx: 900, dy: 400);
            Assert.Equal(0.45, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, x, state, 0.001, false));
            Assert.True(rt.TickMouseTravel(0, "LeftThumbAxisY", 0, y, state, 0.001, false) > 0);

            rt.CenterMouseTravel();

            // A read later in the same frame answers center: the frame's
            // counts were spent with the wheel.
            Assert.Equal(0.0, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, x, state, 0.001, false));

            rt.FrameSeq++;
            var rest = Mouse();
            Assert.Equal(0.0, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, x, rest, 0.001, false));
            Assert.Equal(0.0, rt.TickMouseTravel(0, "LeftThumbAxisY", 0, y, rest, 0.001, false));
            // And the mouse steers from there.
            rt.FrameSeq++;
            Assert.Equal(0.05, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, x, Mouse(dx: 100), 0.001, false));
        }

        [Fact]
        public void CenterMouseTravel_LeavesAToggleAsItsInputSetIt()
        {
            var rt = new SourceKindRuntime();
            var src = Steered(MappingSource.TravelSteerToggle, MappingSource.TravelCenteringOff);
            Row(rt, src, 0, 0.001);
            Row(rt, src, 400, 0.001, steerDown: true);
            rt.CenterMouseTravel();
            Assert.Equal(0.1, Row(rt, src, 200, 0.001), 6);                     // still steering
        }

        // ── Frames ──

        [Fact]
        public void ARowReadTwiceInAFrame_CountsItsMouseOnce()
        {
            // The gamepad pass reads every row on the button lane, and the
            // row's own lane reads it again in the same frame.
            var rt = new SourceKindRuntime();
            var src = Travel();
            rt.FrameSeq++;
            var state = Mouse(dx: 1200);
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, 0, Target, 0, rt, 0.001));
            Assert.Equal(0.6, SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, 0, Target, 0, rt, 0.001), 6);
            Assert.Equal(0.6, SourceEvaluator.EvaluateForTriggerTarget(state, src, 0, Target, 0, rt, 0.001), 6);
            rt.FrameSeq++;
            Assert.Equal(0.6, SourceEvaluator.EvaluateForBipolarAxisTarget(Mouse(), src, 0, Target, 0, rt, 0.001), 6);
        }

        [Fact]
        public void AnAnyDeviceSource_AddsUpEveryMouseOfTheFrame()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            rt.FrameSeq++;
            var first = Mouse(dx: 300);
            var second = Mouse(dx: 500);
            var pad = Mouse();
            rt.TickMouseTravel(0, Target, 0, src, first, 0.001, false);
            rt.TickMouseTravel(0, Target, 0, src, pad, 0.001, false);
            Assert.Equal(0.4, rt.TickMouseTravel(0, Target, 0, src, second, 0.001, false));
            // A device read again adds nothing.
            Assert.Equal(0.4, rt.TickMouseTravel(0, Target, 0, src, first, 0.001, false));
        }

        [Fact]
        public void AnAnyDeviceSource_SteersWhileAnyDeviceHoldsTheInput_AndFlipsOnceAFrame()
        {
            var rt = new SourceKindRuntime();
            var held = Steered(MappingSource.TravelSteerHeld, MappingSource.TravelCenteringOff);
            rt.FrameSeq++;
            // The mouse that moves is read before the one whose button is down.
            rt.TickMouseTravel(0, Target, 0, held, Mouse(dx: 400), 0.001, steerHeld: false);
            Assert.Equal(0.2, rt.TickMouseTravel(0, Target, 0, held, Mouse(), 0.001, steerHeld: true));

            var toggle = Steered(MappingSource.TravelSteerToggle, MappingSource.TravelCenteringOff);
            rt.FrameSeq++;
            rt.TickMouseTravel(0, "RightThumbAxisX", 0, toggle, Mouse(), 0.001, false);
            rt.FrameSeq++;
            // Two devices press together: one flip, on.
            rt.TickMouseTravel(0, "RightThumbAxisX", 0, toggle, Mouse(dx: 100), 0.001, steerHeld: true);
            Assert.Equal(0.1, rt.TickMouseTravel(0, "RightThumbAxisX", 0, toggle, Mouse(dx: 100), 0.001, steerHeld: true));
        }

        [Fact]
        public void AFrameWithNoRead_StartsTheWheelAtCenterAgain()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            Assert.Equal(0.4, Tick(rt, src, 800, 0.001));
            // The row goes unread for a frame (its shift layer closed).
            rt.FrameSeq++;
            Assert.Equal(0.05, Tick(rt, src, 100, 0.001));
        }

        [Fact]
        public void ARowsWheelIsItsOwn_BySlotTargetAndPlace()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            rt.FrameSeq++;
            var state = Mouse(dx: 400);
            rt.TickMouseTravel(0, "LeftThumbAxisX", 0, src, state, 0.001, false);
            rt.FrameSeq++;
            // Another target, another place in the row and another slot each
            // start from center. The same key goes on from where it was.
            Assert.Equal(0.2, rt.TickMouseTravel(0, "RightThumbAxisX", 0, src, state, 0.001, false));
            Assert.Equal(0.2, rt.TickMouseTravel(0, "LeftThumbAxisX", 1, src, state, 0.001, false));
            Assert.Equal(0.2, rt.TickMouseTravel(1, "LeftThumbAxisX", 0, src, state, 0.001, false));
            Assert.Equal(0.4, rt.TickMouseTravel(0, "LeftThumbAxisX", 0, src, state, 0.001, false));
        }

        [Fact]
        public void ResetForRowAndSlot_AndClear_DropTheWheel()
        {
            foreach (var drop in new Action<SourceKindRuntime>[]
                     {
                         rt => rt.ResetForRow(0, Target), rt => rt.ResetForSlot(0), rt => rt.Clear(),
                     })
            {
                var rt = new SourceKindRuntime();
                var src = Travel();
                Tick(rt, src, 800, 0.001);
                drop(rt);
                Assert.Equal(0.0, Tick(rt, src, 0, 0.001));
            }
        }

        [Fact]
        public void WithNoRuntime_TheInputReadsAtRest()
        {
            // A preview, and every read that has no row: an activator, a
            // macro's trigger, a menu's stick.
            var state = Mouse(dx: 5000, steerDown: true);
            var src = Travel();
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, 0, Target, 0, null, 0.001));
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(state, src, 0, Target, 0, null, 0.001));
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, 0, Target, 0, null, 0.001));
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0));
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(state, src, 0));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0));
            Assert.Equal(SourceCoercion.SourceType.MouseTravel, SourceCoercion.ClassifyDescriptor("Mouse Travel X"));
            Assert.Equal(SourceCoercion.SourceType.MouseTravel, SourceCoercion.ClassifyDescriptor("Mouse Travel Y"));
        }

        // ── Steering Lock Feedback ──

        [Fact]
        public void HittingTheLock_ReportsItsEdges_AsAWindingStickDoes()
        {
            var rt = new SourceKindRuntime();
            var src = Travel();
            Tick(rt, src, 1000, 0.001);
            Assert.Equal(SourceKindRuntime.LockEdge.None, rt.TryGetLockEdgeTransition(0, Target, 0, out _));
            Assert.Equal(0.5, rt.GetLockApproach(0, Target, 0), 6);

            Tick(rt, src, -4000, 0.001);
            Assert.Equal(SourceKindRuntime.LockEdge.Enter, rt.TryGetLockEdgeTransition(0, Target, 0, out bool left));
            Assert.True(left);
            Assert.Equal(1.0, rt.GetLockApproach(0, Target, 0), 6);
            // An edge is read once.
            Assert.Equal(SourceKindRuntime.LockEdge.None, rt.TryGetLockEdgeTransition(0, Target, 0, out _));

            Tick(rt, src, 100, 0.001);
            Assert.Equal(SourceKindRuntime.LockEdge.Exit, rt.TryGetLockEdgeTransition(0, Target, 0, out _));
        }

        [Fact]
        public void TheLockIsTheWheels_EvenWhereSpeedLeavesLessOfIt()
        {
            SourceCoercion.MouseTravelRoadProvider = () => (200 / 3.6, 1.0, true);
            var rt = new SourceKindRuntime();
            var src = Travel();
            src.ParamTravelLockAtSpeed = 50;
            Assert.Equal(0.5, Tick(rt, src, 3000, 0.001), 9);
            Assert.Equal(SourceKindRuntime.LockEdge.Enter, rt.TryGetLockEdgeTransition(0, Target, 0, out bool left));
            Assert.False(left);
        }

        // ── A row's three reads ──

        [Fact]
        public void OnAStick_InvertTurnsTheWheelAround_AndHalfReadsOneSide()
        {
            var plain = Travel();
            Assert.Equal(0.3f, SourceCoercion.MouseTravelAsBipolar(0.3, plain), 6);
            Assert.Equal(-0.3f, SourceCoercion.MouseTravelAsBipolar(-0.3, plain), 6);

            var inverted = Travel();
            inverted.Invert = true;
            Assert.Equal(-0.3f, SourceCoercion.MouseTravelAsBipolar(0.3, inverted), 6);

            var right = Travel();
            right.HalfAxis = true;
            Assert.Equal(0.3f, SourceCoercion.MouseTravelAsBipolar(0.3, right), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsBipolar(-0.3, right));

            var left = Travel();
            left.HalfAxis = true;
            left.Invert = true;
            Assert.Equal(0.3f, SourceCoercion.MouseTravelAsBipolar(-0.3, left), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsBipolar(0.3, left));
            left.InvertOutput = true;
            Assert.Equal(-0.3f, SourceCoercion.MouseTravelAsBipolar(-0.3, left), 6);

            var either = Travel();
            either.HalfAxis = true;
            either.Bidirectional = true;
            Assert.Equal(0.3f, SourceCoercion.MouseTravelAsBipolar(-0.3, either), 6);
            // Invert is spoken for under Half, which Flip Output's card asks.
            Assert.True(SourceCoercion.InvertConsumedByHalfAxisRead(left));
            Assert.False(SourceCoercion.InvertConsumedByHalfAxisRead(plain));
        }

        [Fact]
        public void OnAStick_TheSourcesOwnShapingApplies()
        {
            var curved = Travel();
            curved.ParamCurveExponent = 2;
            Assert.Equal(0.25f, SourceCoercion.MouseTravelAsBipolar(0.5, curved), 6);
            Assert.Equal(-0.25f, SourceCoercion.MouseTravelAsBipolar(-0.5, curved), 6);

            var ranged = Travel();
            ranged.ParamRangeOuter = 0.5;
            Assert.Equal(1f, SourceCoercion.MouseTravelAsBipolar(0.5, ranged), 6);

            var floored = Travel();
            floored.ParamAntiDeadzone = 0.2;
            Assert.Equal(0.2f + 0.8f * 0.5f, SourceCoercion.MouseTravelAsBipolar(0.5, floored), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsBipolar(0, floored));

            // A deadzone shape reads this axis alone: the wheel has no partner.
            foreach (int shape in new[] { 1, 2 })
            {
                var dead = Travel();
                dead.ParamStickDeadZoneShape = shape;
                dead.ParamStickDeadZoneInner = 0.2;
                Assert.Equal(0f, SourceCoercion.MouseTravelAsBipolar(0.15, dead));
                Assert.Equal(0.5f, SourceCoercion.MouseTravelAsBipolar(0.6, dead), 6);
            }
        }

        [Fact]
        public void OnATrigger_TheWheelsDistancePulls_AndHalfPullsOneWay()
        {
            var plain = Travel();
            Assert.Equal(0.4f, SourceCoercion.MouseTravelAsTrigger(-0.4, plain), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsTrigger(0, plain));

            var inverted = Travel();
            inverted.Invert = true;
            Assert.Equal(0.6f, SourceCoercion.MouseTravelAsTrigger(0.4, inverted), 6);

            // Forward for the throttle and back for the brake, off one mouse.
            var forward = Travel("Y");
            forward.HalfAxis = true;
            forward.Invert = true;
            var back = Travel("Y");
            back.HalfAxis = true;
            Assert.Equal(0.7f, SourceCoercion.MouseTravelAsTrigger(-0.7, forward), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsTrigger(-0.7, back));
            Assert.Equal(0.7f, SourceCoercion.MouseTravelAsTrigger(0.7, back), 6);
            Assert.Equal(0f, SourceCoercion.MouseTravelAsTrigger(0.7, forward));
        }

        [Fact]
        public void OnAButton_TheWheelPressesPastTheDeadzone()
        {
            var plain = Travel();
            Assert.False(SourceCoercion.MouseTravelAsButton(0.4, plain, 50));
            Assert.True(SourceCoercion.MouseTravelAsButton(0.6, plain, 50));
            Assert.True(SourceCoercion.MouseTravelAsButton(-0.6, plain, 50));

            var near = Travel();
            near.DeadZone = 20;
            Assert.True(SourceCoercion.MouseTravelAsButton(0.25, near, 50));

            var left = Travel();
            left.HalfAxis = true;
            left.Invert = true;
            Assert.True(SourceCoercion.MouseTravelAsButton(-0.6, left, 50));
            Assert.False(SourceCoercion.MouseTravelAsButton(0.6, left, 50));
            // The deadzone has a control on a button row.
            Assert.True(SourceCoercion.IsThresholdedButtonFamily("Mouse Travel X"));
        }

        [Fact]
        public void AToggleOrRapidTriggerModeReadsTheWheelToo()
        {
            // Toggle latches the wheel's press. Rapid Trigger acts on inputs
            // with a press depth, which a wheel is not, so it reads Direct.
            var rt = new SourceKindRuntime();
            var toggle = Travel();
            toggle.Kind = "Toggle";
            rt.FrameSeq++;
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(Mouse(), toggle, 50, 0, "ButtonA", 0, rt, 0.001));
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(Mouse(dx: 1500), toggle, 50, 0, "ButtonA", 0, rt, 0.001));
            rt.FrameSeq++;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(Mouse(dx: -1500), toggle, 50, 0, "ButtonA", 0, rt, 0.001));

            var rapid = Travel();
            rapid.Kind = "RapidTrigger";
            Assert.True(SourceEvaluator.IsMouseTravel(rapid));
            rt.FrameSeq++;
            var state = Mouse(dx: 1000);
            Assert.Equal(0.5, SourceEvaluator.EvaluateForBipolarAxisTarget(state, rapid, 0, Target, 0, rt, 0.001), 6);
            Assert.Equal(0.5, SourceEvaluator.EvaluateForTriggerTarget(state, rapid, 0, Target, 0, rt, 0.001), 6);
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, rapid, 40, 0, Target, 0, rt, 0.001));

            // A mode that reads keys of its own leaves the input unread.
            var ramp = Travel();
            ramp.Kind = "Ramped";
            Assert.False(SourceEvaluator.IsMouseTravel(ramp));
            // An empty kind is Direct on every lane.
            var bare = Travel();
            bare.Kind = "";
            rt.FrameSeq++;
            Assert.Equal(0.5, SourceEvaluator.EvaluateForBipolarAxisTarget(Mouse(dx: 1000), bare, 0, "RightThumbAxisX", 0, rt, 0.001), 6);
        }

        // ── The saved form ──

        private static string Xml(MappingSource source)
        {
            var writer = new StringWriter();
            new XmlSerializer(typeof(MappingSource)).Serialize(writer, source);
            return writer.ToString();
        }

        private static MappingSource FromXml(string xml)
            => (MappingSource)new XmlSerializer(typeof(MappingSource)).Deserialize(new StringReader(xml));

        [Fact]
        public void AnUntouchedSourceWritesNoneOfTheNewFields()
        {
            Assert.DoesNotContain("ParamTravel", Xml(new MappingSource { Descriptor = "Axis 0" }));
            Assert.DoesNotContain("ParamTravel", Xml(Travel()));
        }

        [Fact]
        public void TheSavedFormRoundTrips_AndACopyCarriesIt()
        {
            var src = Travel("Y");
            src.ParamTravelCounts = 12345;
            src.ParamTravelCentering = MappingSource.TravelCenteringGameForce;
            src.ParamTravelReturnTime = 2.25;
            src.ParamTravelSteer = MappingSource.TravelSteerToggle;
            src.ParamTravelSmoothingMs = 35;
            src.ParamTravelLockAtSpeed = 60;
            src.GateDescriptor = "Button 2";

            string xml = Xml(src);
            foreach (string field in new[]
                     {
                         "ParamTravelCounts=\"12345\"", "ParamTravelCentering=\"GameForce\"", "ParamTravelReturnTime=\"2.25\"",
                         "ParamTravelSteer=\"Toggle\"", "ParamTravelSmoothingMs=\"35\"", "ParamTravelLockAtSpeed=\"60\"",
                     })
                Assert.Contains(field, xml);

            foreach (var back in new[] { FromXml(xml), src.Clone() })
            {
                Assert.Equal("Mouse Travel Y", back.Descriptor);
                Assert.Equal(12345, back.ParamTravelCounts);
                Assert.Equal(MappingSource.TravelCenteringGameForce, back.ParamTravelCentering);
                Assert.Equal(2.25, back.ParamTravelReturnTime);
                Assert.Equal(MappingSource.TravelSteerToggle, back.ParamTravelSteer);
                Assert.Equal(35, back.ParamTravelSmoothingMs);
                Assert.Equal(60, back.ParamTravelLockAtSpeed);
                Assert.Equal("Button 2", back.GateDescriptor);
            }
        }

        [Fact]
        public void AFileFromBeforeTheFieldsLoadsWithTheDefaults()
        {
            var old = FromXml("<MappingSource Kind=\"Direct\" DeviceGuid=\"\" Descriptor=\"Mouse Travel X\" />");
            Assert.Equal(2000, old.ParamTravelCounts);
            Assert.Equal("Off", old.ParamTravelCentering);
            Assert.Equal(0.75, old.ParamTravelReturnTime);
            Assert.Equal("Always", old.ParamTravelSteer);
            Assert.Equal(0, old.ParamTravelSmoothingMs);
            Assert.Equal(100, old.ParamTravelLockAtSpeed);
        }

        [Fact]
        public void AHandEditedValueReadsInsideItsRange()
        {
            var odd = FromXml("<MappingSource Descriptor=\"Mouse Travel X\" ParamTravelCounts=\"-5\" ParamTravelCentering=\"Sideways\""
                + " ParamTravelReturnTime=\"NaN\" ParamTravelSteer=\"\" ParamTravelSmoothingMs=\"900\" ParamTravelLockAtSpeed=\"3\" />");
            Assert.Equal(2000, MappingSource.EffectiveTravelCounts(odd.ParamTravelCounts));
            Assert.Equal("Off", MappingSource.EffectiveTravelCentering(odd.ParamTravelCentering));
            Assert.Equal(0.75, MappingSource.EffectiveTravelReturnTime(odd.ParamTravelReturnTime));
            Assert.Equal("Always", MappingSource.EffectiveTravelSteer(odd.ParamTravelSteer));
            Assert.Equal(100, MappingSource.EffectiveTravelSmoothingMs(odd.ParamTravelSmoothingMs));
            Assert.Equal(20, MappingSource.EffectiveTravelLockAtSpeed(odd.ParamTravelLockAtSpeed));
            Assert.Equal(100, MappingSource.EffectiveTravelCounts(1));
            Assert.Equal(50000, MappingSource.EffectiveTravelCounts(1e9));
            // A Return Time of 0 centers at once (#600), so the floor is 0.
            Assert.Equal(0.001, MappingSource.EffectiveTravelReturnTime(0.001));
            Assert.Equal(0, MappingSource.EffectiveTravelReturnTime(0));
            Assert.Equal(0.75, MappingSource.EffectiveTravelReturnTime(-1));
            Assert.Equal(5, MappingSource.EffectiveTravelReturnTime(60));
            // The engine reads the same: a wheel 100 counts wide, not 1.
            var narrow = Travel();
            narrow.ParamTravelCounts = 1;
            Assert.Equal(0.5, Tick(new SourceKindRuntime(), narrow, 50, 0.001));
            // A value a later build wrote is kept in the file as it was.
            Assert.Contains("ParamTravelCentering=\"Sideways\"", Xml(odd));
        }
    }
}
