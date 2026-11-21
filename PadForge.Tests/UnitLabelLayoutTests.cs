using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PadForge.Controls;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A unit beside a number box stands on one line, whole, at the box's
    /// side: in English and in three long languages, at the owner's window
    /// and at a narrow one. The Devices panel's Idle Disconnect row wrapped
    /// its unit "minutes (0 = never)" to two lines beside the box (seen in
    /// the 6.0.0 screenshots). The board draws "0 means never" under the
    /// label and "min" beside the box. On the Settings page the two
    /// timeouts' units were the part of the row that gave way, so in
    /// Malayalam at the least window the unit wrapped beside its box.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class UnitLabelLayoutTests
    {
        private static readonly string[] Languages = { "en", "de", "ru", "ml" };

        private static Rect On(FrameworkElement ground, FrameworkElement part)
            => part.TransformToAncestor(ground).TransformBounds(new Rect(part.RenderSize));

        /// <summary>The size a text takes on one line in its block's face,
        /// size and line height.</summary>
        internal static Size OneLine(TextBlock block)
        {
            var probe = new TextBlock
            {
                Text = block.Text,
                FontFamily = block.FontFamily,
                FontSize = block.FontSize,
                FontWeight = block.FontWeight,
                FontStyle = block.FontStyle,
                FontStretch = block.FontStretch,
                LineHeight = block.LineHeight,
                LineStackingStrategy = block.LineStackingStrategy,
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize;
        }

        /// <summary>A block laid out on one line and given the whole width
        /// its text takes there. A block that does not wrap keeps its full
        /// width when its row hands it less and is clipped to the room it
        /// was given, so the room is read from its layout slot.</summary>
        internal static void AssertOneLineWhole(TextBlock block, string where)
        {
            var one = OneLine(block);
            Assert.True(block.ActualHeight <= one.Height + 0.5,
                $"{where}: \"{block.Text}\" is {block.ActualHeight:F1} tall, one line is {one.Height:F1}");
            double room = LayoutInformation.GetLayoutSlot(block).Width - block.Margin.Left - block.Margin.Right;
            Assert.True(Math.Min(room, block.ActualWidth) >= one.Width - 0.5,
                $"{where}: \"{block.Text}\" has {Math.Min(room, block.ActualWidth):F1} of the {one.Width:F1} its line takes");
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var below in Descendants<T>(child)) yield return below;
            }
        }

        [Fact]
        public void TheIdleDisconnectUnitStaysOnOneLineBesideItsBox()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var vm = new DevicesViewModel();
                var row = new DeviceRowViewModel
                {
                    InstanceGuid = Guid.NewGuid(), DeviceName = "DualSense Wireless Controller",
                    ProductName = "DualSense Wireless Controller", DeviceTypeKey = "Gamepad",
                    DevicePath = "XInput#0", IsOnline = true,
                    KindGlyph = PadForge.Common.DeviceTypeGlyph.For(PadForge.Engine.InputDeviceType.Gamepad),
                    KindShape = PadForge.Common.DeviceTypeGlyph.ShapeFor(PadForge.Engine.InputDeviceType.Gamepad),
                    ShowIdleDisconnect = true,
                };
                vm.Devices.Add(row);
                vm.RefreshCounts();
                var page = new DevicesPage { DataContext = vm };
                var window = new Window
                {
                    Width = 1300, Height = 1000, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, Content = page,
                };
                try
                {
                    window.Show();
                    vm.SelectedDevice = row;
                    // The panel beside the list (372 wide) and, in a narrow
                    // window, the panel under it.
                    foreach (double width in new[] { 1300.0, 760.0 })
                    {
                        window.Width = width;
                        foreach (string language in Languages)
                        {
                            Strings.ChangeCulture(CultureInfo.GetCultureInfo(language));
                            window.UpdateLayout();
                            PageRenderDump.Settle(dispatcher);
                            window.UpdateLayout();
                            string where = $"{language} at {width}";

                            var idleRow = (SettingRow)page.FindName("IdleRow");
                            var box = (FrameworkElement)page.FindName("IdleDisconnectBox");
                            var unit = (TextBlock)page.FindName("IdleUnitText");
                            var label = (TextBlock)page.FindName("IdleLabel");
                            Assert.True(idleRow.ActualWidth > 300, $"{where}: control, the row is {idleRow.ActualWidth:F1} wide");

                            AssertOneLineWhole(unit, where);
                            var b = On(page, box);
                            var u = On(page, unit);
                            var reset = On(page, idleRow.Children.OfType<SettingResetButton>().Single());
                            Assert.True(u.Left >= b.Right, $"{where}: the unit starts at {u.Left:F1}, inside the box ending {b.Right:F1}");
                            Assert.True(u.Right <= reset.Left + 0.5, $"{where}: the unit runs to {u.Right:F1}, past the reset at {reset.Left:F1}");
                            double middle = (u.Top + u.Bottom) / 2;
                            Assert.True(middle > b.Top && middle < b.Bottom, $"{where}: the unit is not on the box's line");

                            // The label keeps one line where the board's
                            // panel has room for it: English at 372.
                            if (language == "en") AssertOneLineWhole(label, where);

                            // The board's "0 means never" stands under the label.
                            var never = (TextBlock)page.FindName("IdleNeverText");
                            Assert.NotNull(never);
                            Assert.Equal(Strings.Instance.Devices_IdleDisconnectNever, never.Text);
                            Assert.True(On(page, never).Top >= On(page, label).Bottom - 0.5, $"{where}: 0 means never is not under the label");
                            Assert.Equal(Strings.Instance.Common_Min, unit.Text);
                        }
                    }
                }
                finally
                {
                    Strings.ChangeCulture(before);
                    window.Close();
                }
            }, TimeSpan.FromMinutes(3), "The idle row test timed out.");
        }

        /// <summary>The Settings page's two timeouts write their unit and
        /// "0 = never" beside the box. Each stays whole on one line at the
        /// window's least width, where the label wraps first.</summary>
        [Fact]
        public void TheSettingsTimeoutUnitsStayWholeAtTheNarrowestWindow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var before = CultureInfo.CurrentUICulture;
                var vm = new SettingsViewModel { SelectedSettingsGroup = SettingsGroup.Input };
                var page = new SettingsPage { DataContext = vm };
                var more = (Expander)page.FindName("BatteryMore");
                bool wasOpen = more.IsExpanded;
                more.IsExpanded = true;
                try
                {
                    foreach (double width in new[] { 1176.0, 636.0 })
                    {
                        foreach (string language in Languages)
                        {
                            Strings.ChangeCulture(CultureInfo.GetCultureInfo(language));
                            PageRenderDump.Flush(page);
                            page.Measure(new Size(width, 4000));
                            page.Arrange(new Rect(0, 0, width, 4000));
                            page.UpdateLayout();
                            string where = $"{language} at {width}";
                            var units = Descendants<TextBlock>((FrameworkElement)page.FindName("InputGroup"))
                                .Where(t => t.Text == Strings.Instance.Settings_SecondsZeroNever || t.Text == Strings.Instance.Common_MinutesZeroNever)
                                .ToList();
                            Assert.Equal(2, units.Count);
                            foreach (var unit in units) AssertOneLineWhole(unit, where);
                        }
                    }
                }
                finally
                {
                    more.IsExpanded = wasOpen;
                    Strings.ChangeCulture(before);
                }
            }, TimeSpan.FromMinutes(3), "The Settings units test timed out.");
        }
    }
}
