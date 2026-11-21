using System;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using PadForge.Models2D;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Lighting group's lightbar picture showed the base art alone, which
    /// leaves out the L2 and R2 triggers and the stick caps. Buttons' picture, in its
    /// 2D view, draws those as their own layers at rest
    /// (ControllerModel2DView.BuildModel), and the lightbar picture now adds
    /// the same layers from the same layout tables.
    /// </summary>
    public class LightbarPreviewTests
    {
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheLightbarPicture_DrawsTheTriggersAndStickCaps_WhereThePreviewTabDoesAtRest(bool isDs4) => RunSta(() =>
        {
            var scene = new Canvas();
            PadPage.AddLightbarRestParts(scene, isDs4);

            var expected = (isDs4 ? DS4Layout.Overlays : DualSenseLayout.Overlays)
                .Where(o => o.ElementType is OverlayElementType.TriggerBase or OverlayElementType.StickRing)
                .ToList();
            Assert.Equal(2, expected.Count(o => o.ElementType == OverlayElementType.TriggerBase));
            Assert.Equal(2, expected.Count(o => o.ElementType == OverlayElementType.StickRing));

            var parts = scene.Children.OfType<Image>().ToList();
            Assert.Equal(expected.Count, parts.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                var ov = expected[i];
                var part = parts[i];
                Assert.NotNull(part.Source);
                Assert.Equal(ov.X, Canvas.GetLeft(part));
                Assert.Equal(ov.Y, Canvas.GetTop(part));
                Assert.Equal(ov.Width, part.Width);
                Assert.Equal(ov.Height, part.Height);
                // Behind the body for a trigger, in front of it for a stick cap.
                Assert.Equal(ov.ElementType == OverlayElementType.TriggerBase ? 0 : 2, Panel.GetZIndex(part));
            }
        });
    }
}
