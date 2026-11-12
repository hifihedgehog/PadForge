using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mapping table has no columns (#574): a row is its line with its
    /// options under it. WPF's DataGrid looks a column up by index in the
    /// code a click or a key reaches through a cell, a row header or its
    /// own row details part, and with no columns that lookup throws. On
    /// 2026-11-11 a left click inside an open row brought PadForge down with
    /// ArgumentOutOfRangeException from DataGrid.ColumnFromDisplayIndex,
    /// called by DataGridDetailsPresenter.OnAnyMouseLeftButtonDown. These
    /// tests click and press keys in a table of the page's shape, built
    /// around the page's own part for a row's body.
    /// </summary>
    public class MappingTableInputTests
    {
        public sealed class Row
        {
            public string Label { get; set; }

            public string Group { get; set; }
        }

        private const string StockPart = "<DataGridDetailsPresenter Grid.Row=\"1\" Visibility=\"{TemplateBinding DetailsVisibility}\"/>";

        private static string PageXaml()
            => File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "Views", "PadPage.xaml")));

        /// <summary>The part of the page's row template that shows an open
        /// row's body, as the page writes it.</summary>
        private static string PagesBodyPart()
        {
            Match part = Regex.Match(PageXaml(), @"<\w+ x:Name=""RowBody""[^>]*/>");
            Assert.True(part.Success, "the mapping row's body part was not found; re-anchor this test");
            return part.Value;
        }

        /// <summary>A table of the page's shape: no columns, a row template
        /// of a line with a body under it, the body shown while the row is
        /// selected.</summary>
        private static DataGrid Table(string bodyPart, bool grouped)
        {
            var grid = (DataGrid)XamlReader.Parse(
                "<DataGrid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" AutoGenerateColumns=\"False\"" +
                " CanUserAddRows=\"False\" CanUserDeleteRows=\"False\" HeadersVisibility=\"None\" GridLinesVisibility=\"None\"" +
                " SelectionMode=\"Single\" IsReadOnly=\"True\" EnableRowVirtualization=\"True\"" +
                " VirtualizingPanel.IsVirtualizingWhenGrouping=\"True\" RowDetailsVisibilityMode=\"VisibleWhenSelected\">" +
                "<DataGrid.RowStyle><Style TargetType=\"DataGridRow\"><Setter Property=\"Template\"><Setter.Value>" +
                "<ControlTemplate TargetType=\"DataGridRow\"><Grid>" +
                "<Grid.RowDefinitions><RowDefinition Height=\"Auto\"/><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions>" +
                "<TextBlock x:Name=\"Line\" Text=\"{Binding Label}\" Height=\"30\"/>" +
                bodyPart +
                "</Grid></ControlTemplate></Setter.Value></Setter></Style></DataGrid.RowStyle>" +
                "<DataGrid.RowDetailsTemplate><DataTemplate><StackPanel>" +
                "<TextBlock Text=\"options\" Height=\"20\"/><TextBox Text=\"value\" Height=\"24\"/>" +
                "</StackPanel></DataTemplate></DataGrid.RowDetailsTemplate>" +
                "<DataGrid.GroupStyle><GroupStyle/></DataGrid.GroupStyle></DataGrid>");
            var rows = Enumerable.Range(0, 40)
                .Select(i => new Row { Label = "row " + i, Group = "group " + (i / 10) })
                .ToList();
            var view = new ListCollectionView(rows);
            if (grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.Group)));
            grid.ItemsSource = view;
            return grid;
        }

        private static Window Host(UIElement content) => new Window
        {
            Width = 300, Height = 300, ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000, Content = content,
        };

        private static void Pump()
            => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] keys);

        private static IEnumerable<DependencyObject> Under(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject deeper in Under(child)) yield return deeper;
            }
        }

        /// <summary>A left press and release that rise from an element, as
        /// the mouse sends them. Every element on the way up raises its own
        /// MouseLeftButtonDown from the press, which is the event WPF's row
        /// details part answers.</summary>
        private static void Click(UIElement on)
        {
            on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
            on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
        }

        /// <summary>Runs the body on a table in a window with its fourth
        /// row open, handing it the table, that row's container and the
        /// row's item.</summary>
        private static void WithAnOpenRow(string bodyPart, bool grouped, Action<DataGrid, DataGridRow, Row> body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    DataGrid grid = Table(bodyPart, grouped);
                    Window window = Host(grid);
                    try
                    {
                        window.Show();
                        // Keyboard.Modifiers is this thread's key table, which
                        // this writes and nothing else reads.
                        Assert.True(SetKeyboardState(new byte[256]));
                        var open = grid.Items.Cast<Row>().ElementAt(3);
                        grid.SelectedItem = open;
                        window.UpdateLayout();
                        Pump();
                        Assert.Empty(grid.Columns);
                        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(open);
                        Assert.NotNull(row);
                        Assert.Equal(Visibility.Visible, row.DetailsVisibility);
                        body(grid, row, open);
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the table run timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A click inside an open row, on a label of its body or
        /// on the body's ground, leaves the row open and throws nothing. So
        /// does a click on the row's own line and on a closed row's line,
        /// which the page answers by its own handler.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AClickInsideAnOpenRowLeavesItOpen(bool grouped)
            => WithAnOpenRow(PagesBodyPart(), grouped, (grid, row, open) =>
            {
                var label = Under(row).OfType<TextBlock>().Single(t => t.Text == "options");
                var ground = (UIElement)VisualTreeHelper.GetParent(label);
                var line = Under(row).OfType<TextBlock>().Single(t => t.Name == "Line");
                Assert.True(label.IsVisible, "the open row's body is not shown");
                // The body is the row's item under the table's details
                // template.
                Assert.Same(open, label.DataContext);
                Assert.IsType<StackPanel>(ground);

                foreach (UIElement target in new[] { label, ground, line })
                {
                    Click(target);
                    Pump();
                    Assert.Same(open, grid.SelectedItem);
                    Assert.Equal(Visibility.Visible, row.DetailsVisibility);
                }

                var closed = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[1]);
                Assert.NotNull(closed);
                Click(Under(closed).OfType<TextBlock>().Single(t => t.Name == "Line"));
                Pump();
                Assert.Same(open, grid.SelectedItem);
                Assert.Equal(Visibility.Collapsed, closed.DetailsVisibility);
            });

        /// <summary>The keys WPF's table answers for its cells: pressed on
        /// the open row, in its text box and on the table, they throw
        /// nothing and leave the same row open.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheTablesKeysLeaveTheOpenRowAlone(bool grouped)
            => WithAnOpenRow(PagesBodyPart(), grouped, (grid, row, open) =>
            {
                PresentationSource source = PresentationSource.FromVisual(grid);
                Assert.NotNull(source);
                Key[] keys =
                {
                    Key.Up, Key.Down, Key.Left, Key.Right, Key.Tab, Key.Enter, Key.Space, Key.Home, Key.End,
                    Key.PageUp, Key.PageDown, Key.Escape, Key.F2, Key.Delete,
                };
                foreach (Key key in keys)
                {
                    // A page key scrolls, so the open row is brought back
                    // and found again before each key.
                    grid.ScrollIntoView(open);
                    grid.UpdateLayout();
                    Pump();
                    var current = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(open);
                    Assert.NotNull(current);
                    var box = Under(current).OfType<TextBox>().Single();
                    foreach (UIElement from in new UIElement[] { current, box, grid })
                    {
                        from.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                        from.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
                        Pump();
                        Assert.Same(open, grid.SelectedItem);
                    }
                }

                // The table's commands ask what they can do without a
                // column to ask about. Nothing copies, selects every row or
                // deletes one.
                Assert.False(ApplicationCommands.Copy.CanExecute(null, grid));
                Assert.False(ApplicationCommands.SelectAll.CanExecute(null, grid));
                Assert.False(ApplicationCommands.Delete.CanExecute(null, grid));
                Assert.False(DataGrid.BeginEditCommand.CanExecute(null, grid));
                _ = DataGrid.CommitEditCommand.CanExecute(null, grid);
                _ = DataGrid.CancelEditCommand.CanExecute(null, grid);
            });

        /// <summary>Why the page's row has a part of its own for its body:
        /// in a table with no columns, WPF's row details part throws on a
        /// left click inside it. When this stops failing the page could use
        /// the stock part again.</summary>
        [Fact]
        public void WpfsRowDetailsPartThrowsOnAClickWhenATableHasNoColumns()
            => WithAnOpenRow(StockPart, grouped: false, (grid, row, open) =>
            {
                var label = Under(row).OfType<TextBlock>().Single(t => t.Text == "options");
                var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => label.RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent }));
                Assert.Equal("displayIndex", thrown.ParamName);
            });

        /// <summary>The page's part is a plain presenter that takes the
        /// row's details template and shows it while the row is selected.</summary>
        [Fact]
        public void ThePagesRowBodyIsAPlainPresenter()
        {
            string part = PagesBodyPart();
            Assert.StartsWith("<ContentPresenter ", part);
            Assert.Contains("Content=\"{Binding}\"", part);
            Assert.Contains("ContentTemplate=\"{TemplateBinding DetailsTemplate}\"", part);
            Assert.Contains("Visibility=\"{TemplateBinding DetailsVisibility}\"", part);
        }

        /// <summary>No table in the app that has no columns holds a part
        /// WPF ties to columns: the row details part, a row header or the
        /// cells presenter. Each one reaches a column lookup from a click
        /// or a key.</summary>
        [Fact]
        public void NoTableWithoutColumnsHoldsAPartThatAsksForOne()
        {
            string views = Path.GetDirectoryName(AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "Views", "PadPage.xaml")));
            string app = Path.GetDirectoryName(views);
            string[] parts = { "DataGridDetailsPresenter", "DataGridRowHeader", "DataGridCellsPresenter" };
            var found = new List<string>();
            int tables = 0;
            foreach (string file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(app, file);
                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = File.ReadAllText(file);
                if (!text.Contains("<DataGrid", StringComparison.Ordinal)) continue;
                foreach (XElement table in XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "DataGrid"))
                {
                    tables++;
                    bool hasColumns = table.Elements().Any(e => e.Name.LocalName == "DataGrid.Columns")
                        || !string.Equals((string)table.Attribute("AutoGenerateColumns"), "False", StringComparison.Ordinal);
                    if (hasColumns) continue;
                    found.AddRange(table.Descendants()
                        .Where(inside => parts.Contains(inside.Name.LocalName))
                        .Select(inside => relative + ": " + inside.Name.LocalName));
                }
            }
            Assert.Empty(found);
            // The mapping table is one of them, so the walk found it.
            Assert.True(tables >= 1, "no table was found; re-anchor this test");
        }
    }
}
