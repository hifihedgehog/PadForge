using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The label that heads the controller page's device pills stands at
    /// their first line. With six devices the pills took two lines and
    /// "Driven by" stood centered between them (seen in the 6.0.0
    /// screenshots, pad-forcefeedback-falcon). With one line it stands at
    /// that line's middle, as before.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DrivenByLabelTests
    {
        private static Rect On(FrameworkElement ground, FrameworkElement part)
            => part.TransformToAncestor(ground).TransformBounds(new Rect(part.RenderSize));

        [Fact]
        public void TheLabelStandsAtThePillsFirstLine()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0) { OutputType = PadForge.Engine.VirtualControllerType.Xbox };
                var page = new PadPage { DataContext = vm };
                var window = new Window
                {
                    Width = 1300, Height = 900, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = page,
                };
                try
                {
                    window.Show();
                    var label = (TextBlock)page.FindName("DrivenByLabel");
                    var list = (ListBox)page.FindName("DrivenByList");
                    foreach (int count in new[] { 1, 6 })
                    {
                        vm.MappedDevices.Clear();
                        foreach (string name in new[] { "DualSense Wireless Controller", "Logitech G29 Driving Force Racing Wheel",
                                     "Logitech G502 HERO Gaming Mouse", "Novint Falcon", "SteelSeries Rival 710 Gaming Mouse",
                                     "Xbox Series X GIP Controller" }.Take(count))
                            vm.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { Name = name, IsOnline = false });
                        window.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        window.UpdateLayout();

                        var pills = Enumerable.Range(0, list.Items.Count)
                            .Select(i => (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
                        Assert.All(pills, Assert.NotNull);
                        var lines = pills.Select(p => Math.Round(On(page, p).Top, 1)).Distinct().ToList();
                        if (count == 6) Assert.True(lines.Count >= 2, $"control: six pills take {lines.Count} line at this width");
                        var first = On(page, pills[0]);
                        var l = On(page, label);
                        double labelMiddle = (l.Top + l.Bottom) / 2, lineMiddle = (first.Top + first.Bottom) / 2;
                        Assert.True(Math.Abs(labelMiddle - lineMiddle) <= 1,
                            $"{count} devices: the label's middle is at {labelMiddle:F1}, the first line's at {lineMiddle:F1}");
                    }
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The Driven by test timed out.");
        }
    }
}
