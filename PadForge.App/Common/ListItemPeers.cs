using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// Keeps what a plain list's item shows within reach of UI Automation and
    /// so of screen readers (#574).
    ///
    /// <para>A plain ItemsControl wraps each item in a ContentPresenter, and
    /// UI Automation reaches the item's content only through the item's peer,
    /// which reads its children from that ContentPresenter
    /// (ItemAutomationPeer.GetChildrenCore). A peer keeps the children it was
    /// first asked for until something marks it stale
    /// (AutomationPeer.EnsureChildren), and WPF marks a peer stale from the
    /// elements below it: each Measure and Arrange walks up from the element
    /// to the nearest one that has a peer and marks that peer and the peers
    /// above it (UIElementHelper.InvalidateAutomationAncestors,
    /// AutomationPeer.InvalidateAncestorsRecursive). A ListBoxItem has a peer
    /// of its own that passes the mark on to its item's peer through
    /// EventsSource. A ContentPresenter has none, so the walk goes by a plain
    /// list's item peer, and an item asked for its children before its
    /// content was built stayed empty for good.</para>
    ///
    /// <para>That happens while a list is first laid out. The panel makes
    /// every container, then measures them one at a time, and a control in
    /// the first card that raises an automation event as it is built sends
    /// WPF through the whole tree to find where that control's peer hangs
    /// (AutomationPeer.ValidateConnected), asking the later cards for
    /// children they do not have yet. On the controller page the second
    /// stick's card showed on screen and held nothing a screen reader could
    /// reach.</para>
    ///
    /// <para>So when an item's ContentPresenter first takes a size, which is
    /// when its content is built, the item's peer reads its children again.
    /// Nothing happens for a list no automation client has walked, since such
    /// a list has no peer.</para>
    /// </summary>
    internal static class ListItemPeers
    {
        /// <summary>Has the peer of a plain list's item read its children
        /// again. Called as the item's container first takes a size.</summary>
        internal static void Refresh(FrameworkElement container)
        {
            if (container is not ContentPresenter)
                return;
            ItemsControl owner = ItemsControl.ItemsControlFromItemContainer(container);
            if (owner == null || UIElementAutomationPeer.FromElement(owner) is not ItemsControlAutomationPeer list)
                return;
            if (!TryRefresh(owner, list, container))
            {
                // A layout that runs inside a call on the list's own peer
                // cannot ask that peer anything
                // (AutomationPeer.GetChildren throws on a call within a
                // call). The item is read again once that call is over.
                container.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => TryRefresh(owner, list, container)));
            }
        }

        private static bool TryRefresh(ItemsControl owner, ItemsControlAutomationPeer list, FrameworkElement container)
        {
            object item = owner.ItemContainerGenerator.ItemFromContainer(container);
            if (item == null || item == DependencyProperty.UnsetValue)
                return true;
            try
            {
                // The items the list already holds are searched first. Read
                // again for every container, a list of N items cost N
                // readings of N.
                if (RefreshIn(list.GetChildren(), item))
                    return true;
                // An item the list does not hold yet: the list reads its
                // items, and tells its listeners of any it gained or lost,
                // as it would after this layout.
                list.ResetChildrenCache();
                RefreshIn(list.GetChildren(), item);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            return true;
        }

        /// <summary>Has the peer of this item, if it is among these, read its
        /// children again. False when it is not among them.</summary>
        private static bool RefreshIn(List<AutomationPeer> items, object item)
        {
            if (items == null)
                return false;
            foreach (AutomationPeer peer in items)
            {
                if (peer is ItemAutomationPeer itemPeer && Equals(itemPeer.Item, item))
                {
                    itemPeer.ResetChildrenCache();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Numbers a list's entries for a screen reader: each one
        /// that <paramref name="counts"/> names gets its place among those
        /// and how many they are. WPF counts every item of a list, and a
        /// list that leads its entries with a label, or holds a button that
        /// is hidden, read "2 of 3" for the first of two. An item whose
        /// container is not made yet is passed over, and the list is
        /// numbered again when its containers are.</summary>
        internal static void Number(ItemsControl list, Func<object, bool> counts)
        {
            var entries = new List<DependencyObject>();
            foreach (object item in list.Items)
            {
                if (counts(item) && list.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container)
                    entries.Add(container);
            }
            for (int i = 0; i < entries.Count; i++)
            {
                AutomationProperties.SetPositionInSet(entries[i], i + 1);
                AutomationProperties.SetSizeOfSet(entries[i], entries.Count);
            }
        }
    }
}
