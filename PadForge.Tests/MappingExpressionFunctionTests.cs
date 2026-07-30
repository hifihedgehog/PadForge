using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <c>pow</c>, <c>hypot</c> and <c>deadzone</c> exist so the transforms the
    /// engine already applies internally can be written in a combine
    /// expression, where the user can see and edit them, instead of riding
    /// per-source fields no card binds. Each one closes a specific gap:
    /// response curve exponent, stick-pair radial magnitude, and the
    /// inner-radius / outer-range pair.
    /// </summary>
    public class MappingExpressionFunctionTests
    {
        private static float Eval(string formula, params float[] sources)
        {
            var c = MappingExpression.Compile(formula);
            Assert.True(c.IsValid, $"'{formula}' failed to compile");
            return (float)c.Evaluate(sources, null);
        }

        [Fact]
        public void Pow_RaisesToTheExponent()
        {
            Assert.Equal(0.25f, Eval("pow(s[0],2)", 0.5f), 4);
            Assert.Equal(0.125f, Eval("pow(s[0],3)", 0.5f), 4);
            // A curve exponent of 1 is the identity, which is what makes it
            // safe as a default.
            Assert.Equal(0.5f, Eval("pow(s[0],1)", 0.5f), 4);
        }

        [Fact]
        public void Hypot_IsThePairMagnitude()
        {
            Assert.Equal(5f, Eval("hypot(s[0],s[1])", 3f, 4f), 4);
            Assert.Equal(1f, Eval("hypot(s[0],s[1])", 1f, 0f), 4);
            // Sign-blind, because a radius has no sign.
            Assert.Equal(5f, Eval("hypot(s[0],s[1])", -3f, -4f), 4);
        }

        [Fact]
        public void Deadzone_RescalesFromInnerToOuter()
        {
            // Inside the inner radius reads dead.
            Assert.Equal(0f, Eval("deadzone(s[0],0.2,0.9)", 0.1f), 4);
            Assert.Equal(0f, Eval("deadzone(s[0],0.2,0.9)", 0.2f), 4);
            // Just past it reads just off zero, not a jump to full.
            Assert.Equal(0.1429f, Eval("deadzone(s[0],0.2,0.9)", 0.3f), 3);
            // At the outer edge and beyond reads full, clamped.
            Assert.Equal(1f, Eval("deadzone(s[0],0.2,0.9)", 0.9f), 4);
            Assert.Equal(1f, Eval("deadzone(s[0],0.2,0.9)", 1.0f), 4);
        }

        [Fact]
        public void Deadzone_PreservesSign()
        {
            Assert.Equal(-1f, Eval("deadzone(s[0],0.2,0.9)", -0.9f), 4);
            Assert.Equal(0f, Eval("deadzone(s[0],0.2,0.9)", -0.1f), 4);
        }

        [Fact]
        public void Deadzone_PassesThroughOnDegenerateBounds()
        {
            // outer <= inner would divide by zero or invert the scale. Passing
            // the input through beats emitting a silent 0 or an infinity.
            Assert.Equal(0.5f, Eval("deadzone(s[0],0.9,0.9)", 0.5f), 4);
            Assert.Equal(0.5f, Eval("deadzone(s[0],0.9,0.2)", 0.5f), 4);
        }

        [Fact]
        public void PositiveControl_TheHarnessActuallyDrivesTheSources()
        {
            // Without this, every assertion above could pass on an evaluator
            // that ignored its inputs and returned 0.
            Assert.Equal(0.75f, Eval("s[0]", 0.75f), 4);
            Assert.Equal(0.25f, Eval("s[1]", 0.75f, 0.25f), 4);
        }

        [Fact]
        public void AllThreeAreRegistered_SoTheParserAcceptsThem()
        {
            // The evaluator having a case is not enough: an unregistered name
            // is rejected at compile, which is how the round-35 guard works.
            foreach (var f in new[] { "pow(s[0],2)", "hypot(s[0],s[1])", "deadzone(s[0],0.1,0.9)" })
                Assert.True(MappingExpression.Compile(f).IsValid, f + " is not registered");
        }
    }
}
