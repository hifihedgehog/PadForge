using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A plain descriptor, "Button N", "Axis N", "Slider N" or "POV N" and a
    /// direction, goes straight to the type-index read in SourceCoercion's
    /// three readers, past every family test. That holds only while no
    /// family claims one. These tests hold every family test to rejecting
    /// them, and every read of one to the answer the family chain gives: the
    /// same descriptor with two spaces after its first word is not plain, so
    /// it takes the chain and parses to the same input.
    /// </summary>
    public class PlainReadTests
    {
        private static IEnumerable<string> PlainDescriptors()
        {
            for (int i = 0; i < CustomInputState.MaxButtons; i += 7) yield return "Button " + i;
            yield return "Button 255";
            yield return "Button 999";
            for (int i = 0; i <= CustomInputState.MaxAxis; i++) yield return "Axis " + i;
            for (int i = 0; i <= CustomInputState.MaxSliders; i++) yield return "Slider " + i;
            for (int p = 0; p <= CustomInputState.MaxPovs; p++)
                foreach (var dir in new[] { "Up", "Down", "Left", "Right" })
                    yield return "POV " + p + " " + dir;
        }

        /// <summary>The same input spelled with two spaces after the first
        /// word, which the parse reads alike and the plain shape does not
        /// admit.</summary>
        private static string ChainSpelling(string plain)
        {
            int space = plain.IndexOf(' ');
            return plain.Substring(0, space) + " " + plain.Substring(space);
        }

        [Theory]
        [InlineData("Button 0", true)]
        [InlineData("Button 127", true)]
        [InlineData("Button 999", true)]
        [InlineData("Axis 12", true)]
        [InlineData("Slider 3", true)]
        [InlineData("POV 0 Up", true)]
        [InlineData("POV 3 Right", true)]
        [InlineData("POV 1 Down", true)]
        [InlineData("POV 2 Left", true)]
        [InlineData("Button  0", false)]
        [InlineData("button 0", false)]
        [InlineData("Button 0 ", false)]
        [InlineData("Button", false)]
        [InlineData("Button ", false)]
        [InlineData("Button x", false)]
        [InlineData("Button 1234", false)]
        [InlineData("Button -1", false)]
        [InlineData("Buttons 0", false)]
        [InlineData("Axis 0 Ring", false)]
        [InlineData("POV 0", false)]
        [InlineData("POV 0 ", false)]
        [InlineData("POV 0 up", false)]
        [InlineData("POV 0 Upward", false)]
        [InlineData("POV 0 Any", false)]
        [InlineData("POV  0 Up", false)]
        [InlineData("Gamepad ButtonA", false)]
        [InlineData("Analog Key 4", false)]
        [InlineData("Press Button 0 50", false)]
        public void ThePlainShapeIsExactlyTheWordsAndNumbers(string descriptor, bool plain)
            => Assert.Equal(plain, SourceCoercion.IsPlainTypeIndexGrammar(descriptor));

        [Fact]
        public void NoFamilyTestClaimsAPlainDescriptor()
        {
            var families = new (string Name, Func<string, bool> Claims)[]
            {
                ("always on", s => s == SourceCoercion.AlwaysOnDescriptor),
                ("gyro engaged", s => s == SourceCoercion.GyroEngagedDescriptor),
                ("bounded", BoundedReads.IsBounded),
                ("mouse gesture", SourceCoercion.IsMouseGestureDescriptor),
                ("menu item", SourceCoercion.IsMenuItemDescriptor),
                ("rumble", SourceCoercion.IsRumbleDescriptor),
                ("touchpad gesture", SourceCoercion.IsTouchpadGestureDescriptor),
                ("touchpad", s => s.StartsWith("Touchpad ", StringComparison.Ordinal)),
                ("midi", s => s.StartsWith("Midi ", StringComparison.Ordinal)),
                ("analog key", s => s.StartsWith(SourceCoercion.AnalogKeyPrefix, StringComparison.Ordinal)),
                ("gravity tilt", SourceCoercion.IsGravityTiltFamilyDescriptor),
                ("motion lean", SourceCoercion.IsMotionLeanDescriptor),
                ("motion lean aux", SourceCoercion.IsMotionLeanAuxDescriptor),
                ("motion shake", SourceCoercion.IsMotionShakeDescriptor),
                ("motion shake aux", SourceCoercion.IsMotionShakeAuxDescriptor),
                ("gyro", s => s.StartsWith("Gyro ", StringComparison.Ordinal)),
                ("mouse position", s => s.StartsWith("Mouse Position ", StringComparison.Ordinal)),
                ("ir", s => s == "IR Offscreen" || s == "IR Brightness" || s.StartsWith("IR Pointer ", StringComparison.Ordinal)),
                ("balance", s => s.StartsWith("Balance ", StringComparison.Ordinal)),
                ("ring-con", SourceCoercion.IsRingConDescriptor),
                ("mouse motion", s => s.StartsWith("Mouse Motion ", StringComparison.Ordinal)),
                ("stick ring", SourceCoercion.IsStickRingDescriptor),
                ("capsense", SourceCoercion.IsCapSenseDescriptor),
                ("nfc tag", SourceCoercion.IsNfcTagDescriptor),
                ("voice phrase", SourceCoercion.IsVoicePhraseDescriptor),
                ("gamepad alias", SourceCoercion.IsGamepadAliasDescriptor),
            };
            foreach (var descriptor in PlainDescriptors())
            {
                Assert.True(SourceCoercion.IsPlainTypeIndexGrammar(descriptor), descriptor);
                foreach (var (name, claims) in families)
                    Assert.False(claims(descriptor), name + " claims " + descriptor);
            }
        }

        private static IEnumerable<MappingSource> Variants(string descriptor)
        {
            foreach (bool invert in new[] { false, true })
            foreach (bool half in new[] { false, true })
            foreach (bool either in new[] { false, true })
            foreach (int deadZone in new[] { 0, 25, 50, 80 })
            foreach (double sensitivity in new[] { 1.0, 0.5, 2.0 })
                yield return new MappingSource
                {
                    Kind = "Direct", Descriptor = descriptor, Invert = invert, HalfAxis = half,
                    Bidirectional = either, DeadZone = deadZone, Sensitivity = sensitivity,
                };
            // The stick and curve shaping a full-axis read carries.
            foreach (int shape in new[] { 1, 2 })
                yield return new MappingSource
                {
                    Kind = "Direct", Descriptor = descriptor, ParamStickDeadZoneShape = shape,
                    ParamStickDeadzone = 0.2, ParamStickDeadZoneInner = 0.1,
                };
            yield return new MappingSource
            {
                Kind = "Direct", Descriptor = descriptor, ParamCurveExponent = 2,
                ParamRangeOuter = 0.8, ParamAntiDeadzone = 0.1, ParamAccel = 0.5,
            };
        }

        private static MappingSource Twin(MappingSource src, string descriptor)
        {
            var twin = src.Clone();
            twin.Descriptor = descriptor;
            return twin;
        }

        private static CustomInputState RandomState(Random random)
        {
            var state = new CustomInputState();
            int[] edges = { 0, 1, 32767, 32768, 32769, 65534, 65535 };
            for (int i = 0; i < state.Axis.Length; i++)
                state.Axis[i] = random.Next(4) == 0 ? edges[random.Next(edges.Length)] : random.Next(65536);
            for (int i = 0; i < state.Sliders.Length; i++)
                state.Sliders[i] = random.Next(4) == 0 ? edges[random.Next(edges.Length)] : random.Next(65536);
            for (int i = 0; i < state.Buttons.Length; i++)
                state.Buttons[i] = random.Next(2) == 0;
            for (int i = 0; i < state.Povs.Length; i++)
                state.Povs[i] = random.Next(10) switch { 0 => -1, int k => (k - 1) * 4500 };
            return state;
        }

        [Fact]
        public void APlainReadAnswersAsTheFamilyChainDoes()
        {
            var random = new Random(4751);
            var states = new List<CustomInputState>();
            for (int i = 0; i < 24; i++) states.Add(RandomState(random));
            int reads = 0;
            foreach (var descriptor in PlainDescriptors())
            {
                string chain = ChainSpelling(descriptor);
                Assert.False(SourceCoercion.IsPlainTypeIndexGrammar(chain), chain);
                foreach (var plain in Variants(descriptor))
                {
                    // One source each, so a read that keeps state per
                    // source keeps it apart.
                    var viaChain = Twin(plain, chain);
                    foreach (var state in states)
                    {
                        Assert.Equal(
                            SourceCoercion.EvaluateForButtonTarget(state, viaChain, 50),
                            SourceCoercion.EvaluateForButtonTarget(state, plain, 50));
                        Assert.Equal(
                            SourceCoercion.EvaluateForBipolarAxisTarget(state, viaChain),
                            SourceCoercion.EvaluateForBipolarAxisTarget(state, plain));
                        Assert.Equal(
                            SourceCoercion.EvaluateForTriggerTarget(state, viaChain),
                            SourceCoercion.EvaluateForTriggerTarget(state, plain));
                        reads += 3;
                    }
                }
            }
            Assert.True(reads > 100_000, reads + " reads");
        }

        [Fact]
        public void AGamepadNameReadsAsThePlainInputItNames()
        {
            var random = new Random(9214);
            foreach (var (alias, chain) in new[]
            {
                ("Gamepad ButtonA", "Button  0"),
                ("Gamepad LeftStickX", "Axis  0"),
                ("Gamepad RightTrigger", "Axis  5"),
            })
            {
                var viaAlias = new MappingSource { Kind = "Direct", Descriptor = alias };
                var viaChain = new MappingSource { Kind = "Direct", Descriptor = chain };
                for (int i = 0; i < 64; i++)
                {
                    var state = RandomState(random);
                    Assert.Equal(SourceCoercion.EvaluateForButtonTarget(state, viaChain, 50),
                        SourceCoercion.EvaluateForButtonTarget(state, viaAlias, 50));
                    Assert.Equal(SourceCoercion.EvaluateForBipolarAxisTarget(state, viaChain),
                        SourceCoercion.EvaluateForBipolarAxisTarget(state, viaAlias));
                    Assert.Equal(SourceCoercion.EvaluateForTriggerTarget(state, viaChain),
                        SourceCoercion.EvaluateForTriggerTarget(state, viaAlias));
                }
            }
        }

        [Fact]
        public void APlainReadAllocatesNothing()
        {
            var state = RandomState(new Random(3));
            var sources = new[]
            {
                new MappingSource { Kind = "Direct", Descriptor = "Button 3" },
                new MappingSource { Kind = "Direct", Descriptor = "Axis 1", HalfAxis = true },
                new MappingSource { Kind = "Direct", Descriptor = "POV 0 Left" },
                new MappingSource { Kind = "Direct", Descriptor = "Slider 0" },
            };
            void ReadAll()
            {
                foreach (var src in sources)
                {
                    SourceCoercion.EvaluateForButtonTarget(state, src, 50);
                    SourceCoercion.EvaluateForBipolarAxisTarget(state, src);
                    SourceCoercion.EvaluateForTriggerTarget(state, src);
                }
            }
            ReadAll();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) ReadAll();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
