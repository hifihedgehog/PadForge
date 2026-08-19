using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Keep Controller Awake (#270, discussion #263, @HaraDaya): games cut
    /// vibration and switch prompts on mouse/keyboard input; a held idle
    /// deflection on one output axis keeps them treating the controller as
    /// active. The reporter proved the mechanism with a 25% anti-deadzone;
    /// this feature applies the same values at the OUTPUT layer so the
    /// mapping pipeline stays untouched. These pin the injection math, the
    /// pass-through contract, persistence, and the content gate.
    /// </summary>
    public class KeepAwakeTests
    {
        private static MappingSet Cfg(bool enabled = true, string axis = "", int pct = 0)
            => new() { KeepAwakeEnabled = enabled, KeepAwakeAxis = axis, KeepAwakeDeflection = pct };

        // ── Motion (#263, @HaraDaya) ──
        //
        // Prey (2017), measured on hardware: "as long as there's movement on
        // the stick, vibration is sent. If you hold it in one place it stops
        // working." A constant hold cannot satisfy a game that gates on
        // CHANGE rather than on magnitude, at any percentage. So the level
        // sweeps, and these pin the shape of that sweep.

        [Fact]
        public void MotionOff_HoldsAConstant_SoTheClockCannotMatter()
        {
            short a = InputManager.KeepAwakeLevel(25, motion: false, nowMs: 0);
            short b = InputManager.KeepAwakeLevel(25, motion: false, nowMs: 137);
            short c = InputManager.KeepAwakeLevel(25, motion: false, nowMs: 9_999_999);
            Assert.Equal(a, b);
            Assert.Equal(a, c);
            Assert.Equal((short)(32767 * 25 / 100), a);
        }

        [Fact]
        public void MotionOn_ActuallyMoves()
        {
            // The whole point. Two instants a quarter period apart must differ,
            // or a game gating on change sees nothing and this feature is
            // decoration.
            short lo = InputManager.KeepAwakeLevel(25, motion: true, nowMs: 0);
            short hi = InputManager.KeepAwakeLevel(
                25, motion: true, nowMs: InputManager.KeepAwakeMotionPeriodMs / 2);
            Assert.NotEqual(lo, hi);
            Assert.True(hi > lo);
        }

        [Fact]
        public void TheSweepIsCentredOnTheConfiguredPercent()
        {
            // Sweeping UP from the user's number would push the stick further
            // than they asked and risk crossing a game's dead zone into real
            // character movement, which is the thing this feature exists to
            // avoid. Sweeping DOWN would dip under the threshold the game uses
            // to call the controller active. So the number they set is the
            // average, sitting midway between the two extremes.
            int level = 32767 * 25 / 100;
            short lo = InputManager.KeepAwakeLevel(25, motion: true, nowMs: 0);
            short hi = InputManager.KeepAwakeLevel(
                25, motion: true, nowMs: InputManager.KeepAwakeMotionPeriodMs / 2);

            Assert.True(lo < level, "bottom of the sweep sits below the set level");
            Assert.True(hi > level, "top of the sweep sits above it");
            Assert.InRange((lo + hi) / 2, level - 1, level + 1);
        }

        [Fact]
        public void TheSweepStaysWithinAQuarterOfTheHeldLevel()
        {
            // Narrow on purpose. Wide enough that a game samples a clearly
            // changing axis, tight enough that it never becomes input.
            int level = 32767 * 25 / 100;
            int min = int.MaxValue, max = int.MinValue;
            for (long t = 0; t < InputManager.KeepAwakeMotionPeriodMs * 3; t++)
            {
                int v = InputManager.KeepAwakeLevel(25, motion: true, nowMs: t);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            Assert.True(max - min <= level / 4 + 1, $"sweep width {max - min}");
            Assert.True(min > 0, "never crosses zero, which would read as released");
        }

        [Fact]
        public void TheSweepRepeats_AndSurvivesAClockThatWraps()
        {
            long period = InputManager.KeepAwakeMotionPeriodMs;
            Assert.Equal(
                InputManager.KeepAwakeLevel(30, motion: true, nowMs: 123),
                InputManager.KeepAwakeLevel(30, motion: true, nowMs: 123 + period * 7));

            // Environment.TickCount64 is monotonic in practice, but a negative
            // input must not produce a negative index into the sweep.
            short v = InputManager.KeepAwakeLevel(30, motion: true, nowMs: -137);
            Assert.True(v > 0);
        }

        [Fact]
        public void MotionRespectsTheClampsTheStaticHoldUses()
        {
            // 90% is the ceiling, and the top of a sweep there must still be a
            // legal axis value rather than wrapping negative.
            for (long t = 0; t < InputManager.KeepAwakeMotionPeriodMs; t += 7)
            {
                short v = InputManager.KeepAwakeLevel(90, motion: true, nowMs: t);
                Assert.InRange(v, (short)1, short.MaxValue);
            }
        }

        [Fact]
        public void RealInputStillWins_WithMotionOn()
        {
            // The pass-through contract is unchanged: anything the player is
            // actually doing sits above the sweep and goes out untouched.
            var gp = new Gamepad { ThumbLX = 30000 };
            var ms = Cfg(pct: 25);
            ms.KeepAwakeMotion = true;
            InputManager.ApplyKeepAwake(ms, ref gp, nowMs: 250);
            Assert.Equal(30000, gp.ThumbLX);
        }

        // ── Injection math ──

        [Fact]
        public void Disabled_TouchesNothing()
        {
            var gp = new Gamepad { ThumbLX = 0, ThumbLY = 0 };
            InputManager.ApplyKeepAwake(Cfg(enabled: false), ref gp);
            Assert.Equal(0, gp.ThumbLX);
            Assert.Equal(0, gp.ThumbLY);
        }

        [Fact]
        public void DefaultConfig_HoldsLeftStickXAtTwentyFivePercent()
        {
            // Unset axis = LX, unset deflection = 25%, the reporter's
            // proven pair.
            var gp = new Gamepad();
            InputManager.ApplyKeepAwake(Cfg(), ref gp);
            Assert.Equal((short)(32767 * 25 / 100), gp.ThumbLX);
            Assert.Equal(0, gp.ThumbLY);
            Assert.Equal(0, gp.ThumbRX);
            Assert.Equal(0, gp.ThumbRY);
        }

        [Theory]
        [InlineData("LX")]
        [InlineData("LY")]
        [InlineData("RX")]
        [InlineData("RY")]
        public void AxisSelection_HoldsExactlyTheChosenAxis(string axis)
        {
            var gp = new Gamepad();
            InputManager.ApplyKeepAwake(Cfg(axis: axis, pct: 40), ref gp);
            short level = (short)(32767 * 40 / 100);
            Assert.Equal(axis == "LX" ? level : (short)0, gp.ThumbLX);
            Assert.Equal(axis == "LY" ? level : (short)0, gp.ThumbLY);
            Assert.Equal(axis == "RX" ? level : (short)0, gp.ThumbRX);
            Assert.Equal(axis == "RY" ? level : (short)0, gp.ThumbRY);
        }

        /// <summary>The self-cancelling contract, tightened by @HaraDaya's
        /// 2026-08-19 report: ANY real stick input past the rest margin
        /// passes through byte-identical, in both directions, INCLUDING
        /// values below the held level. The first cut swallowed sub-level
        /// input and replaced it with the positive hold, so a small left
        /// nudge became a full rightward push, the side-to-side jumping in
        /// the report's capture.</summary>
        [Theory]
        [InlineData(20000)]
        [InlineData(-20000)]
        [InlineData(32767)]
        [InlineData(-32768)]
        [InlineData(5000)]    // above rest, below the 25% level: was swallowed
        [InlineData(-5000)]   // was sign-flipped into +8191
        public void RealInputPastRest_PassesThroughUntouched(int real)
        {
            var gp = new Gamepad { ThumbLX = (short)real };
            InputManager.ApplyKeepAwake(Cfg(pct: 25), ref gp);
            Assert.Equal((short)real, gp.ThumbLX);
        }

        /// <summary>THE reported bug (discussion #263, 2026-08-19): stick
        /// pushed straight up, so the held axis itself reads centered. The
        /// old per-axis gate injected the X hold anyway, bending forward
        /// movement sideways as an inverse deadzone. The hold must consider
        /// the whole stick: any paired-axis deflection releases it.</summary>
        [Theory]
        [InlineData(32767)]
        [InlineData(-32768)]
        [InlineData(9000)]
        public void PairedAxisInUse_ReleasesTheHold(int ly)
        {
            var gp = new Gamepad { ThumbLX = 0, ThumbLY = (short)ly };
            InputManager.ApplyKeepAwake(Cfg(pct: 25), ref gp);
            Assert.Equal(0, gp.ThumbLX);
            Assert.Equal((short)ly, gp.ThumbLY);
        }

        [Fact]
        public void RightStickHold_PairsWithTheRightStick()
        {
            // Axis RX held, right stick pushed up: hold releases. The left
            // stick's state is irrelevant to a right-stick hold.
            var gp = new Gamepad { ThumbRY = 30000, ThumbLX = 0 };
            InputManager.ApplyKeepAwake(Cfg(axis: "RX", pct: 25), ref gp);
            Assert.Equal(0, gp.ThumbRX);

            var gp2 = new Gamepad { ThumbLY = 30000 };
            InputManager.ApplyKeepAwake(Cfg(axis: "RX", pct: 25), ref gp2);
            Assert.Equal((short)(32767 * 25 / 100), gp2.ThumbRX);
        }

        /// <summary>Inside the rest margin the hold engages, INCLUDING
        /// against small drift on either axis of the pair: the axis reads a
        /// stable positive level rather than flapping around rest.</summary>
        [Theory]
        [InlineData(0, 0)]
        [InlineData(500, 0)]
        [InlineData(-500, 0)]
        [InlineData(0, 500)]
        [InlineData(-500, -500)]
        public void RestAndDriftWithinTheMargin_ReadTheHeldLevel(int lx, int ly)
        {
            var gp = new Gamepad { ThumbLX = (short)lx, ThumbLY = (short)ly };
            InputManager.ApplyKeepAwake(Cfg(pct: 25), ref gp);
            Assert.Equal((short)(32767 * 25 / 100), gp.ThumbLX);
            Assert.Equal((short)ly, gp.ThumbLY);
        }

        [Fact]
        public void Deflection_IsClampedToNinetyPercent()
        {
            var gp = new Gamepad();
            InputManager.ApplyKeepAwake(Cfg(pct: 500), ref gp);
            Assert.Equal((short)(32767 * 90 / 100), gp.ThumbLX);
        }

        // ── Persistence + the content gate ──

        [Fact]
        public void KeepAwakeFields_SurviveTheXmlRoundTrip()
        {
            var set = new MappingSet
            {
                KeepAwakeEnabled = true,
                KeepAwakeAxis = "RY",
                KeepAwakeDeflection = 33,
            };
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(MappingSet));
            using var mem = new System.IO.MemoryStream();
            ser.Serialize(mem, set);
            mem.Position = 0;
            var back = (MappingSet)ser.Deserialize(mem);
            Assert.True(back.KeepAwakeEnabled);
            Assert.Equal("RY", back.KeepAwakeAxis);
            Assert.Equal(33, back.KeepAwakeDeflection);
        }

        /// <summary>The HasAuthoredContent append rule: a slot whose only
        /// authoring is the keep-awake config must not be discarded on
        /// cold load (the exact failure the gate's own doc comment
        /// records for menus and rumble-audio before it).</summary>
        [Theory]
        [InlineData(true, "", 0)]
        [InlineData(false, "RX", 0)]
        [InlineData(false, "", 30)]
        public void KeepAwakeOnlySet_CountsAsAuthoredContent(bool enabled, string axis, int pct)
        {
            var set = Cfg(enabled, axis, pct);
            Assert.True(set.HasAuthoredContent);
        }

        [Fact]
        public void UntouchedSet_StillReadsUnauthored()
        {
            Assert.False(new MappingSet().HasAuthoredContent);
        }
    }
}
