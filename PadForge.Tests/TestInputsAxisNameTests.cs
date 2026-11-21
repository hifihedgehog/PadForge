using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The axis names in the Devices panel's Test Inputs show whole while
    /// the row has room for them. The names stood in a column 56 wide, so a
    /// head tracker read "Head Pi..." beside "Head Yaw" and "Head Roll"
    /// (seen in the 6.0.0 screenshots), and the German, Russian and
    /// Malayalam names were cut the same way. The bars of one device start
    /// at one line.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class TestInputsAxisNameTests
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
        public void EveryKindsAxisNamesShowWhole_AndTheirBarsStartTogether()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var vm = new DevicesViewModel();
                var row = new DeviceRowViewModel
                {
                    InstanceGuid = Guid.NewGuid(), DeviceName = "Head Tracker (OpenTrack)", ProductName = "Head Tracker (OpenTrack)",
                    DeviceTypeKey = "HeadTracker", DevicePath = "headtracker://opentrack", IsOnline = true,
                    KindGlyph = PadForge.Common.DeviceTypeGlyph.For(PadForge.Engine.InputDeviceType.Gamepad),
                    KindShape = PadForge.Common.DeviceTypeGlyph.ShapeFor(PadForge.Engine.InputDeviceType.Gamepad),
                };
                vm.Devices.Add(row);
                vm.RefreshCounts();
                var page = new DevicesPage { DataContext = vm };
                var window = new Window
                {
                    Width = 1300, Height = 1100, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = page,
                };
                try
                {
                    window.Show();
                    vm.SelectedDevice = row;
                    var kinds = new (string Kind, Action Build)[]
                    {
                        ("head tracker", () => vm.RebuildRawStateCollections(Enumerable.Range(0, 6).ToArray(), Array.Empty<int>(), 0, isHeadTracker: true)),
                        ("Falcon", () => vm.RebuildRawStateCollections(Enumerable.Range(0, 3).ToArray(), Array.Empty<int>(), 0, isFalcon: true)),
                        ("joystick", () => vm.RebuildRawStateCollections(new[] { 0, 1, 2, 5, 6, 10 }, Array.Empty<int>(), 0)),
                    };
                    foreach (string language in new[] { "en", "de", "ru", "ml" })
                    {
                        Strings.ChangeCulture(CultureInfo.GetCultureInfo(language));
                        foreach (var (kind, build) in kinds)
                        {
                            build();
                            vm.HasRawData = true;
                            window.UpdateLayout();
                            PageRenderDump.Settle(dispatcher);
                            window.UpdateLayout();
                            string where = $"{kind} in {language}";

                            var list = Descendants<ItemsControl>(page).Single(c => ReferenceEquals(c.ItemsSource, vm.RawAxes));
                            Assert.True(Shows(list), $"{where}: control, the axis list does not show");
                            var bars = Descendants<ProgressBar>(list).ToList();
                            var names = vm.RawAxes.Select(a => a.Name).ToList();
                            var blocks = Descendants<TextBlock>(list).Where(t => names.Contains(t.Text)).ToList();
                            Assert.Equal(names.Count, blocks.Count);
                            Assert.Equal(names.Count, bars.Count);
                            foreach (var block in blocks) UnitLabelLayoutTests.AssertOneLineWhole(block, where);
                            var starts = bars.Select(b => Math.Round(b.TransformToAncestor(page).Transform(new Point()).X, 2)).Distinct().ToList();
                            Assert.True(starts.Count == 1, $"{where}: the bars start at {string.Join(", ", starts)}");
                            Assert.True(bars[0].ActualWidth >= 100, $"{where}: the bars are {bars[0].ActualWidth:F1} wide");
                        }
                    }
                }
                finally
                {
                    Strings.ChangeCulture(before);
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The axis name test timed out.");
        }

        /// <summary>The other readouts of Test Inputs that name what they
        /// measure: an analog keyboard's key chips (84 wide) and a Bliss-Box
        /// DualShock 2's pressure chips (142 wide). A chip as wide as its
        /// box cut "Right Shift" and "Numpad Enter" beside their percent.
        /// A chip is as wide as its name needs, and never narrower than its
        /// box.</summary>
        [Fact]
        public void TheKeyAndPressureChipsShowEveryName()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var vm = new DevicesViewModel();
                var row = new DeviceRowViewModel
                {
                    InstanceGuid = Guid.NewGuid(), DeviceName = "Razer Huntsman V3 Pro", ProductName = "Razer Huntsman V3 Pro",
                    DeviceTypeKey = "Keyboard", DevicePath = "analog://razer", IsOnline = true,
                    KindGlyph = PadForge.Common.DeviceTypeGlyph.For(PadForge.Engine.InputDeviceType.Keyboard),
                    KindShape = PadForge.Common.DeviceTypeGlyph.ShapeFor(PadForge.Engine.InputDeviceType.Keyboard),
                };
                vm.Devices.Add(row);
                vm.RefreshCounts();
                var page = new DevicesPage { DataContext = vm };
                var window = new Window
                {
                    Width = 1300, Height = 1100, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = page,
                };
                // Every key of a full board and the keys past it with names
                // of their own.
                var codes = Enumerable.Range(0x04, 0x65 - 0x04 + 1)
                    .Concat(new[] { 0x67, 0x87, 0x88, 0x89, 0x7F, 0x80, 0x81 })
                    .Concat(Enumerable.Range(0xE0, 8)).ToArray();
                try
                {
                    window.Show();
                    vm.SelectedDevice = row;
                    foreach (string language in new[] { "en", "de", "ru", "ml" })
                    {
                        Strings.ChangeCulture(CultureInfo.GetCultureInfo(language));
                        vm.RebuildRawStateCollections(Array.Empty<int>(), Array.Empty<int>(), 0, analogKeyOrder: codes);
                        foreach (int code in codes)
                            vm.AnalogKeys.Add(new AnalogKeyDisplayItem
                            {
                                Code = code, Rank = vm.AnalogKeys.Count,
                                Name = PadForge.Common.MappingDisplayResolver.AnalogKeyDisplayName(code), Depth = 1,
                            });
                        vm.HasAnalogKeys = true;
                        vm.HasRawData = true;
                        window.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        window.UpdateLayout();
                        var keys = Descendants<ItemsControl>(page).Single(c => ReferenceEquals(c.ItemsSource, vm.AnalogKeys));
                        Assert.True(Shows(keys), $"{language}: control, the key chips do not show");
                        var names = vm.AnalogKeys.Select(k => k.Name).ToHashSet();
                        var blocks = Descendants<TextBlock>(keys).Where(t => names.Contains(t.Text)).ToList();
                        Assert.True(blocks.Count >= codes.Length, $"{language}: {blocks.Count} key names drawn");
                        foreach (var block in blocks) UnitLabelLayoutTests.AssertOneLineWhole(block, "key chip in " + language);

                        vm.UpdateBlissBoxPressure(Enumerable.Repeat((byte)255, 12).ToArray());
                        window.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        window.UpdateLayout();
                        var pressure = Descendants<ItemsControl>(page).Single(c => ReferenceEquals(c.ItemsSource, vm.BlissBoxPressure));
                        Assert.True(Shows(pressure), $"{language}: control, the pressure chips do not show");
                        var pressed = vm.BlissBoxPressure.Select(k => k.Name).ToHashSet();
                        var named = Descendants<TextBlock>(pressure).Where(t => pressed.Contains(t.Text)).ToList();
                        Assert.Equal(12, named.Count);
                        foreach (var block in named) UnitLabelLayoutTests.AssertOneLineWhole(block, "pressure chip in " + language);
                        vm.UpdateBlissBoxPressure(null);
                    }
                }
                finally
                {
                    Strings.ChangeCulture(before);
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The chip name test timed out.");
        }
    }
}
