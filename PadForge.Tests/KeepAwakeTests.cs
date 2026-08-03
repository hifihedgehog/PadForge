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

        /// <summary>The self-cancelling contract: real output at or above
        /// the held level passes through byte-identical, in BOTH
        /// directions. This is what separates the feature from the
        /// reporter's anti-deadzone workaround, which reshaped every real
        /// input.</summary>
        [Theory]
        [InlineData(20000)]
        [InlineData(-20000)]
        [InlineData(32767)]
        [InlineData(-32768)]
        public void RealInputAtOrAboveTheLevel_PassesThroughUntouched(int real)
        {
            var gp = new Gamepad { ThumbLX = (short)real };
            InputManager.ApplyKeepAwake(Cfg(pct: 25), ref gp);
            Assert.Equal((short)real, gp.ThumbLX);
        }

        /// <summary>Below the level the hold takes over, INCLUDING against
        /// small negative drift: the axis reads a stable positive level
        /// rather than flapping around rest.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(500)]
        [InlineData(-500)]
        public void RestAndDriftBelowTheLevel_ReadTheHeldLevel(int real)
        {
            var gp = new Gamepad { ThumbLX = (short)real };
            InputManager.ApplyKeepAwake(Cfg(pct: 25), ref gp);
            Assert.Equal((short)(32767 * 25 / 100), gp.ThumbLX);
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
