using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Engine coverage for the #9 abstract "Gamepad ..." descriptor family: the
    /// device-agnostic alias namespace that canonicalizes through SourceCoercion
    /// to the per-device Button / Axis / POV read against the SDL gamepad-normalized
    /// CustomInputState layout (SdlDeviceWrapper.GetGamepadState). The family is a
    /// thin alias layer, so these tests prove the alias table, the canonical
    /// classification, the per-device coercion, and the descriptor-grammar-collision
    /// survival (the "1k lens": every member survives the persisted round-trip and
    /// is never touched by the legacy I/H prefix strip because it leads with 'G').
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GamepadDescriptorTests
    {
        private static MappingSource Src(string descriptor) => new() { Descriptor = descriptor };

        private static CustomInputState CenteredState()
        {
            var s = new CustomInputState();
            // Raw 0 on an axis reads as full negative deflection; center the six
            // standard gamepad axes so an untouched stick reads 0.
            for (int i = 0; i < 6; i++) s.Axis[i] = 32768;
            return s;
        }

        [Theory]
        [InlineData("Gamepad ButtonA", "Button 0")]
        [InlineData("Gamepad ButtonB", "Button 1")]
        [InlineData("Gamepad ButtonY", "Button 3")]
        [InlineData("Gamepad LeftShoulder", "Button 4")]
        [InlineData("Gamepad ButtonGuide", "Button 10")]
        [InlineData("Gamepad Paddle1", "Button 12")]
        [InlineData("Gamepad Paddle4", "Button 15")]
        [InlineData("Gamepad LeftStickX", "Axis 0")]
        [InlineData("Gamepad LeftStickY", "Axis 1")]
        [InlineData("Gamepad LeftTrigger", "Axis 2")]
        [InlineData("Gamepad RightStickX", "Axis 3")]
        [InlineData("Gamepad RightTrigger", "Axis 5")]
        [InlineData("Gamepad DPadUp", "POV 0 Up")]
        [InlineData("Gamepad DPadRight", "POV 0 Right")]
        public void ResolveGamepadAlias_MapsMemberToCanonical(string descriptor, string canonical)
        {
            Assert.Equal(canonical, SourceCoercion.ResolveGamepadAlias(descriptor));
        }

        [Fact]
        public void ResolveGamepadAlias_ReturnsNullForNonFamily()
        {
            Assert.Null(SourceCoercion.ResolveGamepadAlias("Button 0"));
            Assert.Null(SourceCoercion.ResolveGamepadAlias("Gamepad Bogus"));
            Assert.Null(SourceCoercion.ResolveGamepadAlias(""));
            Assert.Null(SourceCoercion.ResolveGamepadAlias(null));
        }

        [Theory]
        [InlineData("Gamepad ButtonA", SourceCoercion.SourceType.Button)]
        [InlineData("Gamepad LeftStickX", SourceCoercion.SourceType.Axis)]
        [InlineData("Gamepad LeftTrigger", SourceCoercion.SourceType.Axis)]
        [InlineData("Gamepad DPadUp", SourceCoercion.SourceType.PovDirection)]
        public void ClassifyDescriptor_SeesCanonicalType(string descriptor, SourceCoercion.SourceType expected)
        {
            Assert.Equal(expected, SourceCoercion.ClassifyDescriptor(descriptor));
        }

        [Fact]
        public void GamepadButton_ReadsNormalizedButton()
        {
            var s = CenteredState();
            s.Buttons[0] = true; // A / South
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, Src("Gamepad ButtonA"), 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, Src("Gamepad ButtonB"), 50));
        }

        [Fact]
        public void GamepadStick_ReadsNormalizedAxisBipolar()
        {
            var s = CenteredState();
            s.Axis[0] = 65535; // LeftStickX full right
            Assert.Equal(1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, Src("Gamepad LeftStickX")), 3);
            s.Axis[0] = 0;     // full left
            Assert.Equal(-1.0f, SourceCoercion.EvaluateForBipolarAxisTarget(s, Src("Gamepad LeftStickX")), 3);
        }

        [Fact]
        public void GamepadTrigger_ReadsNormalizedTrigger()
        {
            var s = CenteredState();
            s.Axis[2] = 65535; // LeftTrigger full pull
            Assert.Equal(1.0f, SourceCoercion.EvaluateForTriggerTarget(s, Src("Gamepad LeftTrigger")), 3);
            s.Axis[2] = 0;
            Assert.Equal(0f, SourceCoercion.EvaluateForTriggerTarget(s, Src("Gamepad LeftTrigger")), 3);
        }

        [Fact]
        public void GamepadDpad_ReadsSynthesizedPov()
        {
            var s = CenteredState();
            s.Povs[0] = 0; // Up (0 centidegrees)
            Assert.True(SourceCoercion.EvaluateForPovDirectionTarget(s, Src("Gamepad DPadUp"), 50));
            Assert.False(SourceCoercion.EvaluateForPovDirectionTarget(s, Src("Gamepad DPadDown"), 50));
        }

        // Per-device resolution: the SAME abstract descriptor resolves against
        // whichever device's normalized state it is evaluated against, so two
        // controllers each drive their own A button through one shared mapping.
        [Fact]
        public void GamepadDescriptor_ResolvesPerDeviceState()
        {
            var src = Src("Gamepad ButtonA");
            var deviceOne = CenteredState(); deviceOne.Buttons[0] = true;
            var deviceTwo = CenteredState(); deviceTwo.Buttons[0] = false;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(deviceOne, src, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(deviceTwo, src, 50));
        }

        // The descriptor-grammar-collision guard (the I/H prefix "1k lens"): every
        // "Gamepad ..." member survives an XML persist round-trip verbatim, keeps
        // its canonical resolution, and is never a target of the legacy I/H
        // invert/half prefix strip (which only fires on 'I'/'H'-leading names).
        [Fact]
        public void EveryGamepadDescriptor_SurvivesPersistedRoundTrip()
        {
            var ser = new XmlSerializer(typeof(MappingSource));
            foreach (var (member, canonical) in SourceCoercion.GamepadAliasTable)
            {
                string descriptor = "Gamepad " + member;
                var src = new MappingSource { Descriptor = descriptor };

                string xml;
                using (var sw = new StringWriter()) { ser.Serialize(sw, src); xml = sw.ToString(); }
                MappingSource restored;
                using (var sr = new StringReader(xml)) { restored = (MappingSource)ser.Deserialize(sr); }

                Assert.Equal(descriptor, restored.Descriptor);
                Assert.False(descriptor.StartsWith("I") || descriptor.StartsWith("H"));
                Assert.Equal(canonical, SourceCoercion.ResolveGamepadAlias(restored.Descriptor));
                // The memberwise-clone copy leg (profile switch / slot reassign).
                Assert.Equal(descriptor, src.Clone().Descriptor);
            }
        }

        [Fact]
        public void GamepadAliasTable_CoversTheFullDocumentedSurface()
        {
            // 25 members: 4 face + 2 shoulders + Back/Start + 2 stick clicks +
            // Guide + 4 paddles + 4 dpad + 4 sticks + 2 triggers.
            Assert.Equal(25, SourceCoercion.GamepadAliasTable.Length);
        }

        // ── Stick deflection rings (translator v17) ────────────────────

        private static CustomInputState RingState(double x01, double y01, bool right = true)
        {
            var s = CenteredState();
            int xi = right ? 3 : 0;
            s.Axis[xi] = 32768 + (int)(x01 * 32767);
            s.Axis[xi + 1] = 32768 + (int)(y01 * 32767);
            return s;
        }

        [Fact]
        public void StickRing_ClassifiesAsRing_AndNeverAsAlias()
        {
            Assert.Equal(SourceCoercion.SourceType.StickRing,
                SourceCoercion.ClassifyDescriptor("Gamepad LeftStickRing"));
            Assert.Equal(SourceCoercion.SourceType.StickRing,
                SourceCoercion.ClassifyDescriptor("Gamepad RightStickRing"));
            // Not an alias-table member: the canonicalizer must leave the
            // spelling intact so the ring read keys on it.
            Assert.Null(SourceCoercion.ResolveGamepadAlias("Gamepad LeftStickRing"));
            Assert.True(SourceCoercion.TryGetStickRingAxes("Gamepad RightStickRing",
                out string rx, out string ry));
            Assert.Equal("Axis 3", rx);
            Assert.Equal("Axis 4", ry);
        }

        [Fact]
        public void StickRing_OuterRing_EngagesAtRadius()
        {
            var src = new MappingSource
            {
                Descriptor = "Gamepad RightStickRing",
                HalfAxis = true,
                DeadZone = 75, // ring radius percent (edge_binding_radius scale)
            };
            Assert.False(SourceCoercion.EvaluateForButtonTarget(RingState(0, 0), src, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(RingState(0.5, 0), src, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(RingState(0.9, 0), src, 50));
            // The read is the PAIR magnitude: a diagonal each axis of which
            // sits below the radius still crosses it radially.
            Assert.True(SourceCoercion.EvaluateForButtonTarget(RingState(0.6, 0.6), src, 50));
        }

        [Fact]
        public void StickRing_InnerRing_InvertFlips_AndRestFloorGates()
        {
            var src = new MappingSource
            {
                Descriptor = "Gamepad RightStickRing",
                HalfAxis = true,
                Invert = true, // inner ring: inside the radius
                DeadZone = 75,
            };
            // Centered stick sits inside every radius but must NOT fire
            // (the rest floor), or an inner-ring key would be held forever.
            Assert.False(SourceCoercion.EvaluateForButtonTarget(RingState(0, 0), src, 50));
            Assert.True(SourceCoercion.EvaluateForButtonTarget(RingState(0.3, 0), src, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(RingState(0.9, 0), src, 50));
            // Invert is the inner/outer selector, consumed inside the read.
            Assert.True(SourceCoercion.InvertConsumedByHalfAxisRead(src));
        }

        [Fact]
        public void StickRing_LeftAndRight_ReadTheirOwnPairs()
        {
            var left = new MappingSource
            { Descriptor = "Gamepad LeftStickRing", HalfAxis = true, DeadZone = 75 };
            var right = new MappingSource
            { Descriptor = "Gamepad RightStickRing", HalfAxis = true, DeadZone = 75 };
            var s = RingState(0.9, 0, right: false); // left stick deflected
            Assert.True(SourceCoercion.EvaluateForButtonTarget(s, left, 50));
            Assert.False(SourceCoercion.EvaluateForButtonTarget(s, right, 50));
        }

        [Fact]
        public void StickRing_ScalarReads_AreTheUnsignedMagnitude()
        {
            var src = new MappingSource
            { Descriptor = "Gamepad RightStickRing", HalfAxis = true, Invert = true, DeadZone = 75 };
            var s = RingState(0.6, 0);
            // Invert never becomes a sign: the inner selector is consumed
            // by the read on every target class.
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(s, src), 2);
            Assert.Equal(0.6f, SourceCoercion.EvaluateForTriggerTarget(s, src), 2);
        }
    }
}
