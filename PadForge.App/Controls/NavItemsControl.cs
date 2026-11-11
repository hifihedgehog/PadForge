using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// A list of the sidebar's entries (#574). An ItemsControl gives each
    /// item a UI Automation peer with no pattern to activate it, so a
    /// screen reader's touch or scan mode, and a UI script, could open a
    /// page only with a real click. The items of this list offer Invoke
    /// and SelectionItem. Either raises <see cref="ItemInvokedEvent"/> on
    /// the entry, which the window answers the way it answers a click or
    /// Enter on it.
    /// </summary>
    public sealed class NavItemsControl : ItemsControl
    {
        public static readonly RoutedEvent ItemInvokedEvent = EventManager.RegisterRoutedEvent(
            "ItemInvoked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(NavItemsControl));

        protected override AutomationPeer OnCreateAutomationPeer() => new NavItemsControlAutomationPeer(this);
    }

    internal sealed class NavItemsControlAutomationPeer : ItemsControlAutomationPeer
    {
        public NavItemsControlAutomationPeer(NavItemsControl owner) : base(owner) { }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new NavItemAutomationPeer(item, this);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(NavItemsControl);
    }

    /// <summary>
    /// One entry of the sidebar, to UI Automation. An entry is its own
    /// item: the window adds NavigationViewItems to the list. An item that
    /// is no entry (the Controllers label) or an entry with no tag offers
    /// neither pattern.
    /// </summary>
    internal sealed class NavItemAutomationPeer : ItemAutomationPeer, IInvokeProvider, ISelectionItemProvider
    {
        public NavItemAutomationPeer(object item, ItemsControlAutomationPeer list) : base(item, list) { }

        private NavigationViewItem Entry => Item is NavigationViewItem entry && entry.Tag != null ? entry : null;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => "NavigationItem";

        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface is PatternInterface.Invoke or PatternInterface.SelectionItem && Entry != null)
                return this;
            return base.GetPattern(patternInterface);
        }

        /// <summary>Opens what the entry stands for. It returns at once, as
        /// UI Automation asks of Invoke, and the entry's event is raised on
        /// the next dispatcher pass.</summary>
        public void Invoke()
        {
            NavigationViewItem entry = Entry;
            if (entry == null || !entry.IsEnabled) throw new ElementNotEnabledException();
            entry.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                entry.RaiseEvent(new RoutedEventArgs(NavItemsControl.ItemInvokedEvent, entry))));
        }

        /// <summary>The entry whose page is showing.</summary>
        public bool IsSelected => Entry?.IsActive == true;

        public void Select() => Invoke();

        /// <summary>The list holds one selection, so adding to it selects.</summary>
        public void AddToSelection() => Invoke();

        /// <summary>A page is always showing: its entry cannot be
        /// unselected, only another selected.</summary>
        public void RemoveFromSelection()
        {
            if (IsSelected) throw new InvalidOperationException();
        }

        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(ItemsControlAutomationPeer);
    }
}
