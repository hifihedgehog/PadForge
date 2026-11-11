using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mapping table sizes its columns once, to the widest cell of every
    /// row, and locks them. The pass has to build the rows that are not in
    /// view to measure them. It asked for that through the attached
    /// VirtualizingPanel.IsVirtualizing, which a grid whose own
    /// EnableRowVirtualization is set ignores, so it measured the rows in
    /// view and no others. On 2026-11-11 the list opened with 11 of its 22
    /// rows built, none of them an axis row, and the Options column locked
    /// too narrow for an axis row's Acceleration (#574).
    /// </summary>
    public class MappingColumnFitTests
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

        /// <summary>One row: its cell is this wide.</summary>
        private sealed class Row
        {
            public Row(double wide) { Wide = wide; }
            public double Wide { get; }
        }

        /// <summary>A grid set up as the mapping grid's markup sets it, in a
        /// view that holds a few rows, with one column whose cell is as wide
        /// as its row says.</summary>
        private static (DataGrid grid, DataGridTemplateColumn column) Grid(double[] widths)
        {
            var grid = new DataGrid
            {
                EnableRowVirtualization = true,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Width = 500,
                Height = 150,
            };
            VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            var cell = new FrameworkElementFactory(typeof(Border));
            cell.SetBinding(FrameworkElement.WidthProperty, new Binding(nameof(Row.Wide)));
            cell.SetValue(FrameworkElement.HeightProperty, 20.0);
            var column = new DataGridTemplateColumn
            {
                Header = "W",
                Width = DataGridLength.Auto,
                CellTemplate = new DataTemplate { VisualTree = cell },
            };
            grid.Columns.Add(column);
            grid.ItemsSource = widths.Select(w => new Row(w)).ToList();
            return (grid, column);
        }

        /// <summary>Shows the grid in a window off the screen, as
        /// AccessibilityPeerTests hosts its controls, so its rows are built
        /// by a real layout. Runs the body, then closes the window.</summary>
        private static void InAWindow(DataGrid grid, Action body)
        {
            var window = new Window
            {
                Width = 600, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = grid,
            };
            try
            {
                window.Show();
                grid.UpdateLayout();
                body();
            }
            finally
            {
                window.Close();
            }
        }

        [Fact]
        public void TheFitMeasuresARowThatIsNotInView()
        {
            RunSta(() =>
            {
                // 199 narrow rows, and the wide one last, far out of view.
                var widths = Enumerable.Repeat(30.0, 199).Append(300.0).ToArray();
                var (grid, column) = Grid(widths);
                InAWindow(grid, () =>
                {
                    Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));
                    Assert.Null(grid.ItemContainerGenerator.ContainerFromIndex(199));

                    PadPage.AutoFitFlexibleColumns(grid);

                    Assert.Equal(DataGridLengthUnitType.Pixel, column.Width.UnitType);
                    // The wide row's 300 and the pass's 12 of cell chrome.
                    Assert.InRange(column.Width.Value, 312, 330);
                    // The grid's own setting is back as it was.
                    Assert.True(grid.EnableRowVirtualization);
                });
            });
        }

        [Fact]
        public void TheFitLeavesAGridThatBuildsEveryRowAsItWas()
        {
            RunSta(() =>
            {
                var widths = Enumerable.Repeat(30.0, 19).Append(120.0).ToArray();
                var (grid, column) = Grid(widths);
                grid.EnableRowVirtualization = false;
                InAWindow(grid, () =>
                {
                    PadPage.AutoFitFlexibleColumns(grid);

                    Assert.InRange(column.Width.Value, 132, 150);
                    Assert.False(grid.EnableRowVirtualization);
                });
            });
        }

        /// <summary>What the first test stands on: the mapping grid's markup
        /// sets the grid's own switch, and the pass turns that switch and not
        /// the attached one the grid ignores.</summary>
        [Fact]
        public void TheMappingGridSetsItsOwnSwitch_AndThePassTurnsThatOne()
        {
            var page = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var table = page.Descendants().Single(e => (string)e.Attribute(x + "Name") == "MappingDataGrid");
            Assert.Equal("True", (string)table.Attribute("EnableRowVirtualization"));

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Matches(@"bool wasVirtualizing = grid\.EnableRowVirtualization;\s*grid\.EnableRowVirtualization = false;\s*grid\.UpdateLayout\(\);", code);
            Assert.Matches(@"finally\s*\{\s*grid\.EnableRowVirtualization = wasVirtualizing;\s*\}", code);
            Assert.DoesNotContain("VirtualizingPanel.SetIsVirtualizing(", code);
        }
    }
}
