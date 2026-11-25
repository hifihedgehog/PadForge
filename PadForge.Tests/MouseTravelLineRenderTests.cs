using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #597: the dials of a source's line, drawn. A "Mouse Travel" input
    /// shows its settings there and only the ones that apply, and any other
    /// input shows none of them.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class MouseTravelLineRenderTests
    {
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var below in Descendants<T>(child)) yield return below;
            }
        }

        private static bool Shows(DependencyObject part)
        {
            for (var at = part; at != null; at = VisualTreeHelper.GetParent(at))
                if (at is UIElement { Visibility: not Visibility.Visible }) return false;
            return true;
        }

        [Fact]
        public void TheDials_ShowTheTravelSettings_OnlyForThisInput_AndOnlyTheOnesThatApply()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0);
                var page = new PadPage { DataContext = vm };
                var template = (DataTemplate)page.FindResource("SourceDials");
                var source = new MappingSourceItem { Kind = "Direct", Descriptor = "Mouse Position X" };
                var line = new ContentControl { ContentTemplate = template, Content = source };
                var host = new UserControl { Content = line, DataContext = vm, Resources = page.Resources };
                var window = new Window
                {
                    Width = 1600, Height = 400, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = host,
                };
                var s = Strings.Instance;
                HashSet<string> Drawn()
                {
                    window.UpdateLayout();
                    PageRenderDump.Settle(dispatcher);
                    window.UpdateLayout();
                    return Descendants<TextBlock>(line).Where(t => Shows(t) && t.ActualWidth > 0).Select(t => t.Text).ToHashSet();
                }
                try
                {
                    window.Show();

                    // Mouse Position: its own sensitivity, the Steer and the
                    // Smoothing every mouse source takes (#600), and none of
                    // the held position's settings.
                    var drawn = Drawn();
                    Assert.Contains(s.Mapping_MouseCursorSensitivity, drawn);
                    Assert.Contains(s.Mapping_MouseTravelSteer, drawn);
                    Assert.Contains(s.Mapping_MouseTravelSmoothing, drawn);
                    foreach (string label in new[]
                             {
                                 s.Mapping_MouseTravelCounts, s.Mapping_MouseTravelCentering, s.Mapping_MouseTravelReturnTime,
                                 s.Mapping_MouseTravelSteerInput, s.Mapping_MousePositionAnchor,
                             })
                        Assert.DoesNotContain(label, drawn);
                    // Under a mode that reads a Steer input, the anchor shows.
                    source.ParamTravelSteer = MappingSource.TravelSteerHeldCenter;
                    drawn = Drawn();
                    Assert.Contains(s.Mapping_MouseTravelSteerInput, drawn);
                    Assert.Contains(s.Mapping_MousePositionAnchor, drawn);
                    source.ParamTravelSteer = MappingSource.TravelSteerAlways;

                    // Mouse Travel with every setting at its default: the four that always apply.
                    source.Descriptor = "Mouse Travel X";
                    drawn = Drawn();
                    Assert.DoesNotContain(s.Mapping_MouseCursorSensitivity, drawn);
                    foreach (string label in new[]
                             {
                                 s.Mapping_MouseTravelCounts, s.Mapping_MouseTravelCentering,
                                 s.Mapping_MouseTravelSteer, s.Mapping_MouseTravelSmoothing,
                             })
                        Assert.Contains(label, drawn);
                    Assert.DoesNotContain(s.Mapping_MouseTravelReturnTime, drawn);
                    Assert.DoesNotContain(s.Mapping_MouseTravelSteerInput, drawn);
                    // The counts box holds the counts, and its unit follows.
                    Assert.Contains(s.Mapping_MouseTravelCounts_Unit, drawn);

                    // Spring brings Return Time, While Held the Steer Input.
                    source.ParamTravelCentering = MappingSource.TravelCenteringSpring;
                    source.ParamTravelSteer = MappingSource.TravelSteerHeld;
                    drawn = Drawn();
                    Assert.Contains(s.Mapping_MouseTravelReturnTime, drawn);
                    Assert.Contains(s.Mapping_MouseTravelSteerInput, drawn);
                    var boxes = Descendants<TextBox>(line).Where(Shows).Select(b => b.Text).ToList();
                    Assert.Contains(MouseTravelLine.FormatCounts(2000), boxes);
                    Assert.Contains("0.75", boxes);
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The Mouse Travel line test timed out.");
        }
    }
}
