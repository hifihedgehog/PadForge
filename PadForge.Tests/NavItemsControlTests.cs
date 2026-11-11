using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.Controls;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The sidebar's entries to UI Automation (#574). An entry of a plain
    /// ItemsControl had no pattern, so nothing but a real click, or Enter
    /// with the keyboard's focus on it, opened a page: a screen reader's
    /// touch or scan mode could not, and neither could a UI script while
    /// another window was in front. An entry of a NavItemsControl offers
    /// Invoke and SelectionItem, and the window answers either the way it
    /// answers a click.
    /// </summary>
    public class NavItemsControlTests
    {
        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
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
            Assert.True(thread.Join(30000), "the sidebar list test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>Runs what the dispatcher has queued, so an event raised
        /// on its next pass has been raised.</summary>
        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        private static (NavItemsControl List, NavigationViewItem Home, NavigationViewItem Devices, TextBlock Label) Sidebar()
        {
            var list = new NavItemsControl();
            var home = new NavigationViewItem { Tag = "Home", Content = "Home" };
            var devices = new NavigationViewItem { Tag = "Devices", Content = "Devices" };
            var label = new TextBlock { Text = "Controllers" };
            list.Items.Add(home);
            list.Items.Add(label);
            list.Items.Add(devices);
            // A list has item peers once it has laid its items out.
            list.Measure(new Size(300, 600));
            list.Arrange(new Rect(0, 0, 300, 600));
            list.UpdateLayout();
            return (list, home, devices, label);
        }

        private static ItemAutomationPeer PeerOf(NavItemsControl list, object item)
        {
            var peer = Assert.IsAssignableFrom<ItemsControlAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(list));
            Assert.Equal(AutomationControlType.List, peer.GetAutomationControlType());
            return Assert.IsAssignableFrom<ItemAutomationPeer>(
                peer.GetChildren().OfType<ItemAutomationPeer>().Single(p => ReferenceEquals(p.Item, item)));
        }

        [Fact]
        public void AnEntry_OffersInvokeAndSelectionItem_AndALabelOffersNeither() => RunSta(() =>
        {
            var (list, home, _, label) = Sidebar();

            var entry = PeerOf(list, home);
            Assert.Equal(AutomationControlType.ListItem, entry.GetAutomationControlType());
            Assert.Equal("NavigationItem", entry.GetClassName());
            Assert.IsAssignableFrom<IInvokeProvider>(entry.GetPattern(PatternInterface.Invoke));
            Assert.IsAssignableFrom<ISelectionItemProvider>(entry.GetPattern(PatternInterface.SelectionItem));

            // The Controllers label is an item of the list and no entry.
            var text = PeerOf(list, label);
            Assert.Null(text.GetPattern(PatternInterface.Invoke));
            Assert.Null(text.GetPattern(PatternInterface.SelectionItem));

            // An entry with no tag opens nothing, so it offers nothing.
            home.Tag = null;
            Assert.Null(entry.GetPattern(PatternInterface.Invoke));
        });

        [Fact]
        public void InvokeAndSelect_RaiseTheEntrysEvent_OnTheNextDispatcherPass() => RunSta(() =>
        {
            var (list, home, devices, _) = Sidebar();
            var raised = new System.Collections.Generic.List<object>();
            list.AddHandler(NavItemsControl.ItemInvokedEvent, new RoutedEventHandler((_, e) => raised.Add(e.OriginalSource)));

            var invoke = (IInvokeProvider)PeerOf(list, home).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            // Invoke returns at once and has opened nothing yet.
            Assert.Empty(raised);
            Pump();
            Assert.Same(home, Assert.Single(raised));

            var select = (ISelectionItemProvider)PeerOf(list, devices).GetPattern(PatternInterface.SelectionItem);
            select.Select();
            Pump();
            Assert.Equal(new object[] { home, devices }, raised);

            // An entry that is turned off refuses.
            home.IsEnabled = false;
            Assert.Throws<ElementNotEnabledException>(() => invoke.Invoke());
        });

        [Fact]
        public void TheSelectedEntry_IsTheOneWhosePageShows() => RunSta(() =>
        {
            var (list, home, devices, _) = Sidebar();
            var homeItem = (ISelectionItemProvider)PeerOf(list, home).GetPattern(PatternInterface.SelectionItem);
            var devicesItem = (ISelectionItemProvider)PeerOf(list, devices).GetPattern(PatternInterface.SelectionItem);

            home.IsActive = true;
            Assert.True(homeItem.IsSelected);
            Assert.False(devicesItem.IsSelected);
            // A page is always showing: its entry cannot be unselected.
            Assert.Throws<InvalidOperationException>(() => homeItem.RemoveFromSelection());
            devicesItem.RemoveFromSelection();
        });

        /// <summary>The window answers the entry's event with the one method
        /// a click and a key press reach.</summary>
        [Fact]
        public void TheWindow_AnswersAnInvokedEntry_AsItAnswersAClick()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Matches(@"NavView\.AddHandler\(PadForge\.Controls\.NavItemsControl\.ItemInvokedEvent, new RoutedEventHandler\(\(s, e\) =>\s*\{\s*"
                + @"if \(e\.OriginalSource is NavigationViewItem nvi && nvi\.Tag != null\)\s*ActivateNavItem\(nvi\);", window);
            Assert.Equal(3, window.Split(new[] { "ActivateNavItem(nvi);" }, StringSplitOptions.None).Length - 1);
        }
    }
}
