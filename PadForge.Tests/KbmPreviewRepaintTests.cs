using PadForge.Engine;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The KBM preview repaints on its OWN data now.
    ///
    /// <para>PadViewModel.KbmOutputSnapshot is a plain auto-property assigned
    /// every poll by InputService. Assigning it raises no notification, so the
    /// view's dirty flag never went true for it and the preview only repainted
    /// when some unrelated view-model property changed. A mapped button
    /// engaging is exactly the case where nothing else changes, so the preview
    /// sat dead while the mapping was working correctly.</para>
    ///
    /// <para>These hold the comparison honest: every field the preview DRAWS
    /// has to be one it compares, or that input silently stops repainting.</para></summary>
    public class KbmPreviewRepaintTests
    {
        [Fact]
        public void IdenticalStateIsNotARepaint()
        {
            var a = new KbmRawState();
            Assert.True(KBMPreviewView.SamePreviewState(a, a));
        }

        [Fact]
        public void EveryKeyBankIsWatched()
        {
            var baseline = new KbmRawState();

            var k0 = baseline; k0.Keys0 = 1;
            var k1 = baseline; k1.Keys1 = 1;
            var k2 = baseline; k2.Keys2 = 1;
            var k3 = baseline; k3.Keys3 = 1;
            Assert.False(KBMPreviewView.SamePreviewState(baseline, k0));
            Assert.False(KBMPreviewView.SamePreviewState(baseline, k1));
            Assert.False(KBMPreviewView.SamePreviewState(baseline, k2));
            Assert.False(KBMPreviewView.SamePreviewState(baseline, k3));
        }

        /// <summary>Every mouse button bit, individually. Bit 0 = LMB through
        /// bit 4 = X2; a bit the comparison misses is a button that never
        /// lights.</summary>
        [Theory]
        [InlineData(0x01)]
        [InlineData(0x02)]
        [InlineData(0x04)]
        [InlineData(0x08)]
        [InlineData(0x10)]
        public void EveryMouseButtonBitIsWatched(int bit)
        {
            var a = new KbmRawState();
            var b = a;
            b.MouseButtons = (byte)bit;
            Assert.False(KBMPreviewView.SamePreviewState(a, b));
        }

        [Fact]
        public void ScrollAndMotionAreWatched()
        {
            var a = new KbmRawState();

            var s = a; s.ScrollDelta = 1;
            var x = a; x.MouseDeltaX = 1;
            var y = a; y.MouseDeltaY = -1;
            Assert.False(KBMPreviewView.SamePreviewState(a, s));
            Assert.False(KBMPreviewView.SamePreviewState(a, x));
            Assert.False(KBMPreviewView.SamePreviewState(a, y));
        }

        /// <summary>A held button must keep comparing equal, so a steady press
        /// does not repaint every frame for nothing.</summary>
        [Fact]
        public void HeldStateDoesNotChurn()
        {
            var a = new KbmRawState { MouseButtons = 0x01, Keys0 = 0x40 };
            var b = a;
            Assert.True(KBMPreviewView.SamePreviewState(a, b));
        }
    }
}
