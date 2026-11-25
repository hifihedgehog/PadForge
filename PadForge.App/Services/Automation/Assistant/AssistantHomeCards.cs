using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.Services.Catalog;
using PadForge.ViewModels;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Home's controller cards as live rows of the catalog (#600). Home
    /// builds its cards from templates in code (HomePage.Cards.cs), so its
    /// markup names no card, setting or button a program could reach. Each
    /// card is a place, <c>home/c&lt;number&gt;</c>, read as the card shows
    /// it, and its buttons are its menu: Enabled (the power pill), Configure
    /// (open the controller's page), Delete and Add Device, each pressed
    /// through the card's own button, found by its AutomationId and the
    /// card's controller, with the class its element sits in. They join the
    /// catalog through the one seam for live rows (LiveCatalog.AddProvider).
    /// Called on the interface's thread.
    /// </summary>
    internal static class AssistantHomeCards
    {
        /// <summary>The buttons of a card: their key in the ID, their
        /// AutomationId, and the string key of their words.</summary>
        internal static readonly IReadOnlyList<(string Key, string AutomationId, string WordsKey)> Buttons = new[]
        {
            ("power", "Dash_SlotPower", "Common_Enabled"),
            ("open", "Dash_SlotStage", "Dashboard_SlotCard_Configure"),
            ("remove", "Dash_DeleteSlot", "Main_DeleteVC"),
            ("add-device", "Dash_AddDevice", "Pad_AddDevice"),
        };

        internal static IReadOnlyList<LiveItem> Items(FrameworkElement home, DashboardViewModel dash, IReadOnlyList<PadViewModel> pads)
        {
            var list = new List<LiveItem>();
            if (home == null || dash == null) return list;
            var s = PadForge.Resources.Strings.Strings.Instance;
            foreach (var slot in dash.SlotSummaries)
            {
                int index = slot.PadIndex;
                if (index < 0 || index >= InputManager.MaxPads || !SettingsManager.SlotCreated[index]) continue;
                int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(index);
                if (number <= 0) number = index + 1;
                string id = "home/c" + number.ToString(CultureInfo.InvariantCulture);
                string name = SearchService.SlotName(pads != null && index < pads.Count ? pads[index] : null);
                FrameworkElement Card() => Element(home, "Dash_SlotStage", index) is { } stage ? CardOf(stage, index) : null;
                list.Add(new LiveItem(id, LiveKind.Setting, name, ReachClass.Free)
                {
                    Where = s.Home_Title,
                    Read = () => Words(Card()),
                    Element = Card,
                    Menu = Buttons.Select(b => Button(home, id + "/" + b.Key, PadForge.Resources.Strings.Strings.Get(b.WordsKey), b.AutomationId, index)).ToList(),
                });
            }
            return list;
        }

        private static LiveItem Button(FrameworkElement home, string id, string words, string automationId, int index)
        {
            var element = Element(home, automationId, index);
            var (cls, why) = element == null ? (ReachClass.Free, ReachReason.None) : Reach.Of(element);
            if (cls == ReachClass.Unset) cls = ReachClass.UsersOwn;
            return new LiveItem(id, LiveKind.Action, words, cls, why)
            {
                Where = PadForge.Resources.Strings.Strings.Instance.Home_Title,
                Element = () => Element(home, automationId, index),
                Press = () =>
                {
                    if (Element(home, automationId, index) is UIElement shown) AssistantCatalog.Invoke(shown);
                    else throw new AssistantRefusal("not-available", "Home does not show this button now.");
                },
            };
        }

        /// <summary>A button of a Home card by its AutomationId and the
        /// card's controller, or null when Home does not show it.</summary>
        internal static FrameworkElement Element(FrameworkElement home, string automationId, int index)
        {
            foreach (var element in SearchLocator.VisualDescendants(home).OfType<FrameworkElement>())
                if (element.Tag is int tag && tag == index && AutomationProperties.GetAutomationId(element) == automationId)
                    return element;
            return null;
        }

        private static FrameworkElement CardOf(FrameworkElement part, int index)
        {
            for (DependencyObject d = part; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is Border { Tag: int tag } border && tag == index) return border;
            return part;
        }

        private static string Words(FrameworkElement card)
            => card == null ? null : string.Join(" ", SearchLocator.VisualDescendants(card).OfType<TextBlock>()
                .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text));
    }
}
