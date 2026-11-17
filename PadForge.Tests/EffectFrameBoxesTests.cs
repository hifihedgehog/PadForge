using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A DualSense frame's byte fields are shared boxes, and the frame's
    /// dictionary is sized for its fields. The dispatcher builds one frame per
    /// pad 30 times a second, and a dictionary grown twice and a box per field
    /// were most of each frame's garbage (measured 2026-11-16).
    /// </summary>
    public class EffectFrameBoxesTests
    {
        [Fact]
        public void ADualSenseFrameSharesItsBoxesAndNeverGrows()
        {
            var fields = Ds5EffectSynthesizer.BuildFields(null);
            int bytes = 0;
            foreach (var pair in fields)
            {
                if (pair.Value is not byte value) continue;
                Assert.Same(BoxedBytes.Of(value), pair.Value);
                bytes++;
            }
            Assert.True(bytes >= 13, $"only {bytes} byte fields");

            Ds5EffectSynthesizer.BuildFields(null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ds5EffectSynthesizer.BuildFields(null);
            long spent = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(spent < 900, $"{spent} bytes for one frame");
        }

        [Fact]
        public void EveryByteHasOneBox()
        {
            for (int i = 0; i < 256; i++)
            {
                object box = BoxedBytes.Of((byte)i);
                Assert.Equal((byte)i, Assert.IsType<byte>(box));
                Assert.Same(box, BoxedBytes.Of((byte)i));
            }
        }
    }
}
