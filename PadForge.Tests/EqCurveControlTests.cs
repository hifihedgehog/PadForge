using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The graphic EQ's curve (#347): its grid labels are laid out at the
    /// control's own scale and take the theme's text color as the theme
    /// changes.
    /// </summary>
    public class EqCurveControlTests
    {
        private static void Sta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class CountingCurve : EqCurveControl
        {
            public int Renders;

            protected override void OnRender(DrawingContext dc)
            {
                Renders++;
                base.OnRender(dc);
            }
        }

        /// <summary>A label's text is laid out for the pixels per DIP of the
        /// display the control is on, never a fixed 125 percent.</summary>
        [Fact]
        public void ALabel_IsLaidOutAtTheControlsOwnScale()
        {
            Sta(() =>
            {
                var curve = new EqCurveControl();
                var text = typeof(EqCurveControl).GetMethod("Text", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(text);
                var label = (FormattedText)text.Invoke(text.IsStatic ? null : curve, new object[] { "20", Brushes.Gray, 12.0 });
                Assert.Equal(VisualTreeHelper.GetDpi(curve).PixelsPerDip, label.PixelsPerDip);
            });
        }

        /// <summary>A theme change swaps the text brush the labels take, and the
        /// curve draws again with the new one, with no band edited.</summary>
        [Fact]
        public void AThemeChange_DrawsTheLabelsAgain()
        {
            Sta(() =>
            {
                var curve = new CountingCurve();
                var host = new Grid { Width = 400, Height = 200 };
                host.Resources["TextFillColorTertiaryBrush"] = Brushes.Red;
                host.Children.Add(curve);
                host.Measure(new Size(400, 200));
                host.Arrange(new Rect(0, 0, 400, 200));
                host.UpdateLayout();
                int before = curve.Renders;
                Assert.True(before > 0);

                host.Resources["TextFillColorTertiaryBrush"] = Brushes.Blue;
                host.UpdateLayout();
                Assert.True(curve.Renders > before, "the curve did not draw again on the theme's new brush");
            });
        }
    }
}
