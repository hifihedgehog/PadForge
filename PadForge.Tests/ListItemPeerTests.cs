using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.ViewModels;
using PadForge.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>What a screen reader can reach inside the cards a list draws (#574). A stick's
    /// card and a trigger's card are items of a plain list, and UI Automation reaches a card's
    /// controls only through the item's peer.</summary>
    [Collection("WpfApplicationSingleton")]
    public class ListItemPeerTests
    {
        private const double Width = 1473, TallHeight = 3600;

        private static IEnumerable<DependencyObject> Under(DependencyObject root)
        {
            var open = new Stack<DependencyObject>();
            open.Push(root);
            while (open.Count > 0)
            {
                DependencyObject d = open.Pop();
                yield return d;
                int count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
                for (int i = count - 1; i >= 0; i--)
                    open.Push(VisualTreeHelper.GetChild(d, i));
            }
        }

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
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static ItemsControl CardList(ObservableCollection<string> cards)
            => new ItemsControl
            {
                ItemsSource = cards,
                ItemTemplate = (DataTemplate)XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                    "<StackPanel><TextBlock Text=\"{Binding}\"/><Button Content=\"Reset\"/></StackPanel></DataTemplate>"),
            };

        /// <summary>The peer of a plain list's item keeps the children it
        /// was first asked for, and nothing below a ContentPresenter marks
        /// it stale (ListItemPeers). A card asked before it was built read
        /// as empty for good: the controller page's second stick showed on
        /// screen with nothing in it a screen reader could reach. The item
        /// reads its children again as its container first takes a size,
        /// which the app does from that container's first SizeChanged.</summary>
        [Fact]
        public void AnItemAskedForItsChildrenBeforeItsCardWasBuilt_ReadsThemAgainOnceItIs() => RunSta(() =>
        {
            var cards = new ObservableCollection<string> { "Left" };
            ItemsControl list = CardList(cards);
            var window = new Window
            {
                Width = 400, Height = 600, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = list,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                AutomationPeer listPeer = UIElementAutomationPeer.CreatePeerForElement(list);
                Assert.Equal(2, Assert.Single(listPeer.GetChildren()).GetChildren().Count);

                // A second card joins the list. Its container is made at
                // once and built at the next layout, and something asks
                // the tree in between.
                cards.Add("Right");
                var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.NotNull(container);
                Assert.Equal(0, VisualTreeHelper.GetChildrenCount(container));
                listPeer.ResetChildrenCache();
                Assert.Equal(2, listPeer.GetChildren().Count);
                Assert.Null(listPeer.GetChildren()[1].GetChildren());

                // Built and on screen, the card still reads as empty.
                window.UpdateLayout();
                Assert.True(VisualTreeHelper.GetChildrenCount(container) > 0);
                Assert.Null(listPeer.GetChildren()[1].GetChildren());

                ListItemPeers.Refresh(container);
                AutomationPeer right = listPeer.GetChildren()[1];
                Assert.Equal("Right", right.GetName());
                Assert.Equal(new[] { "Right", "Reset" }, right.GetChildren().Select(p => p.GetName()).ToArray());
                // The first card keeps what it had.
                Assert.Equal(2, listPeer.GetChildren()[0].GetChildren().Count);

                // A list nothing has walked has no peer, and gets none. This
                // one stands in no window. Once a UI Automation client has
                // asked the process for events, WPF gives each new window a
                // root peer and walks the window's tree after a layout
                // (HwndSource.RootVisual, AutomationPeer.UpdateSubtree), so a
                // list in a window can have a peer no test made: a full run
                // of the suite ended that way once.
                ItemsControl unwalked = CardList(new ObservableCollection<string> { "Only" });
                // Outside a tree, a list takes its template as its
                // initialization ends (FrameworkElement.EndInit).
                unwalked.BeginInit();
                unwalked.EndInit();
                unwalked.Measure(new Size(400, 600));
                unwalked.Arrange(new Rect(0, 0, 400, 600));
                var only = (FrameworkElement)unwalked.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.NotNull(only);
                Assert.True(VisualTreeHelper.GetChildrenCount(only) > 0);
                ListItemPeers.Refresh(only);
                Assert.Null(UIElementAutomationPeer.FromElement(unwalked));
            }
            finally
            {
                window.Close();
            }
        });

        /// <summary>The repair runs from the handler that names an item as
        /// its container first takes a size, for a plain list's container
        /// alone: a ListBoxItem has a peer of its own, which passes a
        /// change below it on to its item's peer.</summary>
        [Fact]
        public void TheRepairRunsAsAPlainListsContainerFirstTakesASize()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PadForge.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir, "PadForge.App", "Common", "AccessibleNames.cs")).Replace("\r\n", "\n");
            Assert.Contains("EventManager.RegisterClassHandler(typeof(ContentPresenter), FrameworkElement.SizeChangedEvent,\n" +
                "                Sized(fe =>\n                {\n                    NameItem(fe);\n                    ListItemPeers.Refresh(fe);\n                }));", source);
            Assert.Contains("EventManager.RegisterClassHandler(typeof(ListBoxItem), FrameworkElement.SizeChangedEvent,\n" +
                "                Sized(fe => NameItem(fe)));", source);
        }

        [Fact]
        public void EveryStickAndTriggerCard_ExposesItsControls()
        {
            Exception failure = null;
            var report = new StringBuilder();
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try
                {
                    PageRenderDump.SetResourceAssembly(typeof(App).Assembly);
                    var app = PageRenderDump.MakeAppThatNeverStarts(dispatcher);
                    app.InitializeComponent();
                    PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
                    PadForge.Common.EmberTheme.ApplyAccent();

                    var vm = new PadViewModel(0);
                    var page = new PadPage { DataContext = vm };
                    page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                    var ground = new Border { Child = page };
                    void Lay()
                    {
                        ground.Measure(new Size(Width, TallHeight));
                        ground.Arrange(new Rect(0, 0, Width, TallHeight));
                        ground.UpdateLayout();
                    }
                    vm.SelectedConfigTab = 4;
                    Lay();
                    PageRenderDump.Settle(dispatcher);
                    foreach (string name in new[] { "TabSticks", "TabTriggers" })
                        ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
                    Lay();
                    PageRenderDump.Settle(dispatcher);
                    Lay();

                    foreach (string name in new[] { "TabSticks", "TabTriggers" })
                    {
                        var group = (FrameworkElement)page.FindName(name);
                        var list = Under(group).OfType<ItemsControl>().First(i => i.GetType() == typeof(ItemsControl));
                        AutomationPeer listPeer = UIElementAutomationPeer.CreatePeerForElement(list);
                        List<AutomationPeer> items = listPeer.GetChildren() ?? new List<AutomationPeer>();
                        report.AppendLine(name + ": " + list.Items.Count + " items, " + items.Count + " item peers");
                        for (int i = 0; i < list.Items.Count; i++)
                        {
                            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i);
                            int visuals = container == null ? -1 : Under(container).Count() - 1;
                            int withPeer = container == null ? -1 : Under(container).OfType<UIElement>().Count(e => e != container && UIElementAutomationPeer.CreatePeerForElement(e) != null);
                            string children;
                            try
                            {
                                List<AutomationPeer> c = i < items.Count ? items[i].GetChildren() : null;
                                children = c == null ? "null" : c.Count.ToString();
                            }
                            catch (Exception error) { children = "threw " + error.GetType().Name + ": " + error.Message; }
                            report.AppendLine("  item " + i + " '" + (i < items.Count ? items[i].GetName() : "?") + "': container " + (container == null ? "none" : container.GetType().Name)
                                + ", " + visuals + " visuals under it, " + withPeer + " with a peer, item peer children " + children);
                        }
                    }

                    foreach (string name in new[] { "TabSticks", "TabTriggers" })
                    {
                        var group = (FrameworkElement)page.FindName(name);
                        var list = Under(group).OfType<ItemsControl>().First(i => i.GetType() == typeof(ItemsControl));
                        List<AutomationPeer> items = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren();
                        Assert.True(list.Items.Count >= 2, name + " draws " + list.Items.Count + " cards.\n" + report);
                        Assert.Equal(list.Items.Count, items.Count);
                        foreach (AutomationPeer item in items)
                        {
                            List<AutomationPeer> children = item.GetChildren();
                            Assert.True(children != null && children.Count > 5, "The card '" + item.GetName() + "' exposes no controls.\n" + report);
                        }
                    }
                }
                catch (Exception error) { failure = new Exception(error.Message + "\n" + report, error); }
                finally
                {
                    try
                    {
                        PairDeviceDialogSerialTests.ReleaseApplication();
                        PageRenderDump.SetResourceAssembly(null);
                    }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "The card walk timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
