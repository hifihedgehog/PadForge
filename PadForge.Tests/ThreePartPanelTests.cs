using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The panel a stick's card lays its three parts out with (#574, the
    /// world-class pass): the settings, the curve and the live view on one
    /// line while the card is wide, the live view beside the other two with
    /// the curve under the settings when it is not, and one under the other
    /// when it is narrow. The numbers are the stick card's own: the
    /// settings' least 300 and most 360, the curve's least 330 and most
    /// 720, the live view's 326 and 22 between.
    /// </summary>
    public class ThreePartPanelTests
    {
        private const double Lead = 300, LeadMost = 360, Middle = 330, MiddleMost = 720, End = 326, Gap = 22;

        [Theory]
        // One line from the three least widths and the two gaps: 1000.
        [InlineData(1000, "OneLine")]
        [InlineData(1400, "OneLine")]
        [InlineData(999, "EndBeside")]
        // Beside the end from the wider of the two and the end: 678.
        [InlineData(678, "EndBeside")]
        [InlineData(677, "Stacked")]
        [InlineData(200, "Stacked")]
        [InlineData(double.PositiveInfinity, "OneLine")]
        public void TheFormFollowsTheWidth(double width, string form)
            => Assert.Equal(form, ThreePartPanel.FormFor(width, Lead, Middle, End, Gap).ToString());

        [Fact]
        public void OnOneLine_TheLeadGrowsALittleAndTheMiddleTakesTheRest()
        {
            (double, double) At(double width)
                => ThreePartPanel.Widths(ThreePartPanel.Form.OneLine, width, Lead, LeadMost, Middle, MiddleMost, End, Gap);
            // At the least width each has its least.
            Assert.Equal((300.0, 330.0), At(1000));
            // 90 over: the lead takes a third, the middle the rest.
            Assert.Equal((330.0, 390.0), At(1090));
            // The lead stops at its most, and the middle takes what it left.
            Assert.Equal((360.0, 570.0), At(1300));
            // The middle stops at its most too.
            Assert.Equal((360.0, 720.0), At(2000));
            // With no bound each has its least.
            Assert.Equal((300.0, 330.0), At(double.PositiveInfinity));
        }

        [Fact]
        public void BesideTheEnd_TheTwoShareTheColumnTheEndLeaves()
        {
            (double, double) At(double width)
                => ThreePartPanel.Widths(ThreePartPanel.Form.EndBeside, width, Lead, LeadMost, Middle, MiddleMost, End, Gap);
            // A 1280 window: 910 inside the card, and a column of 562.
            Assert.Equal((360.0, 562.0), At(910));
            // The default window: 730, and a column of 382.
            Assert.Equal((360.0, 382.0), At(730));
            Assert.Equal((330.0, 330.0), At(678));
            // Stacked, each has the width up to its most.
            Assert.Equal((360.0, 530.0), ThreePartPanel.Widths(ThreePartPanel.Form.Stacked, 530, Lead, LeadMost, Middle, MiddleMost, End, Gap));
            Assert.Equal((250.0, 250.0), ThreePartPanel.Widths(ThreePartPanel.Form.Stacked, 250, Lead, LeadMost, Middle, MiddleMost, End, Gap));
        }

        private static Border Part(double height, double basis = double.NaN, double most = double.NaN, double width = double.NaN)
        {
            var part = new Border { MinHeight = height };
            if (!double.IsNaN(basis)) FlexLinePanel.SetBasis(part, basis);
            if (!double.IsNaN(most)) part.MaxWidth = most;
            if (!double.IsNaN(width))
            {
                part.Width = width;
                part.HorizontalAlignment = HorizontalAlignment.Left;
                part.VerticalAlignment = VerticalAlignment.Top;
            }
            return part;
        }

        private static Rect Place(UIElement child, UIElement within)
            => new Rect(child.TranslatePoint(new Point(0, 0), within), child.RenderSize);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the layout timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void TheThreePartsStandAsTheWidthAllows() => RunSta(() =>
        {
            var panel = new ThreePartPanel();
            Assert.Equal(22.0, panel.Gap);
            Assert.Equal(16.0, panel.LineGap);
            var lead = Part(132, Lead, LeadMost);
            var middle = Part(190, Middle, MiddleMost);
            var end = Part(300, width: End);
            foreach (var part in new[] { lead, middle, end }) panel.Children.Add(part);

            // Wide: one line, as tall as the live view.
            panel.Measure(new Size(1300, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Size(1300, 300), panel.DesiredSize);
            Assert.Equal(new Rect(0, 0, 360, 132), Place(lead, panel));
            Assert.Equal(new Rect(382, 0, 570, 190), Place(middle, panel));
            Assert.Equal(new Rect(974, 0, 326, 300), Place(end, panel));

            // A 1280 window: the curve under the settings, at their
            // column's width, and the live view against the line's end.
            panel.Measure(new Size(910, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Size(910, 132 + 16 + 190), panel.DesiredSize);
            Assert.Equal(new Rect(0, 0, 360, 132), Place(lead, panel));
            Assert.Equal(new Rect(0, 148, 562, 190), Place(middle, panel));
            Assert.Equal(new Rect(584, 0, 326, 300), Place(end, panel));

            // Narrow: one under the other.
            panel.Measure(new Size(530, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Size(530, 132 + 16 + 190 + 16 + 300), panel.DesiredSize);
            Assert.Equal(new Rect(0, 0, 360, 132), Place(lead, panel));
            Assert.Equal(new Rect(0, 148, 530, 190), Place(middle, panel));
            Assert.Equal(new Rect(0, 354, 326, 300), Place(end, panel));

            // A hidden part: what is left is a plain stack.
            end.Visibility = Visibility.Collapsed;
            panel.Measure(new Size(910, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Size(910, 132 + 16 + 190), panel.DesiredSize);
            Assert.Equal(0, Place(middle, panel).X);
            Assert.Equal(148, Place(middle, panel).Y);
        });

        /// <summary>A stick's card uses it, and a FlexLinePanel is no
        /// substitute: it wraps the last part alone under the first.</summary>
        [Fact]
        public void TheStickCardLaysItsThreePartsOutWithIt()
        {
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
            int stick = page.IndexOf("<DataTemplate DataType=\"{x:Type vm:StickConfigItem}\">", StringComparison.Ordinal);
            int more = page.IndexOf("AutomationProperties.AutomationId=\"Pad_More_Stick\"", StringComparison.Ordinal);
            Assert.True(stick > 0 && more > stick);
            string card = page.Substring(stick, more - stick);
            Assert.Contains("<controls:ThreePartPanel Gap=\"22\" LineGap=\"16\"", card);
            Assert.DoesNotContain("<controls:FlexLinePanel", card);
            Assert.Contains("controls:FlexLinePanel.Basis=\"{DynamicResource FirstRowSettingsBasis}\"", card);
            Assert.Contains("controls:FlexLinePanel.Basis=\"330\" MaxWidth=\"720\"", card);
        }
    }
}
