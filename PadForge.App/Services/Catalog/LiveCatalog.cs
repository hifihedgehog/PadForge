using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.ViewModels;

namespace PadForge.Services.Catalog
{
    /// <summary>What kind of live object an entry is (#600). Append-only.</summary>
    internal enum LiveKind { Device, Controller, Profile, Macro, Plugin, PluginDeclaration, Setting, Action, MenuItem, Page }

    /// <summary>
    /// One live object of the catalog: a device, a virtual controller, a
    /// profile, a macro, a plugin or something a plugin declares. Its ID is
    /// stable and follows no interface language: <c>device/&lt;short ID&gt;</c>,
    /// <c>controller/&lt;number&gt;</c>, <c>profile/&lt;ID&gt;</c>,
    /// <c>macro/c&lt;number&gt;/&lt;position&gt;</c>,
    /// <c>plugin/&lt;short ID&gt;</c> and
    /// <c>plugin/&lt;short ID&gt;/&lt;key&gt;</c>. Its name is the one the
    /// interface shows, which a program reads through the scrubber.
    /// </summary>
    internal sealed record LiveEntry(string Id, LiveKind Kind, string Name, string Where, object Payload);

    /// <summary>
    /// One setting, action or menu item of a live object (#600) that a
    /// template draws or a program declares, and the markup cannot name: a
    /// mapping row's mouse settings, a user's database row, an assistant
    /// row's menu, a plugin's declared setting. <see cref="Id"/> is stable
    /// and follows no interface language (the provider's object ID, then
    /// its own key). It carries its class and reason, how its value is read
    /// and set, how it is pressed, its menu, and the dialog it opens, if any.
    /// </summary>
    internal sealed record LiveItem(string Id, LiveKind Kind, string Words, ReachClass Reach, ReachReason Why = ReachReason.None)
    {
        /// <summary>The value as the interface shows it, or null for an
        /// action.</summary>
        public Func<string> Read { get; init; }

        /// <summary>Sets the value from text, as a person's change does. Null
        /// for a value no program sets.</summary>
        public Action<string> Write { get; init; }

        /// <summary>Presses an action or a menu item.</summary>
        public Action Press { get; init; }

        /// <summary>The items of its menu, each a LiveItem of kind MenuItem.</summary>
        public IReadOnlyList<LiveItem> Menu { get; init; } = Array.Empty<LiveItem>();

        /// <summary>The dialog a press opens, by its name, or null.</summary>
        public string Opens { get; init; }

        /// <summary>Where it is, as the interface names it.</summary>
        public string Where { get; init; }

        /// <summary>The element to ring for it, when one is drawn now.</summary>
        public Func<System.Windows.FrameworkElement> Element { get; init; }
    }

    /// <summary>The live objects of the catalog (#600), read on the interface
    /// thread from the window's data.</summary>
    internal static class LiveCatalog
    {
        private static readonly object Sync = new();
        private static readonly List<Func<IReadOnlyList<LiveItem>>> Providers = new();

        /// <summary>
        /// Adds a source of live items (#600): the one way a template's rows,
        /// a plugin's declarations, the database's rows or an assistant row's
        /// menu join the catalog. Called on the interface thread when it is
        /// read. Every item it gives must carry a class: an item with
        /// none is dropped and reads as the user's own.
        /// </summary>
        internal static void AddProvider(Func<IReadOnlyList<LiveItem>> provider)
        {
            lock (Sync) if (!Providers.Contains(provider)) Providers.Add(provider);
        }

        internal static void RemoveProvider(Func<IReadOnlyList<LiveItem>> provider)
        {
            lock (Sync) Providers.Remove(provider);
        }

        /// <summary>Every item the providers give now, a menu's items after
        /// their owner, each with a class.</summary>
        internal static List<LiveItem> Items()
        {
            List<Func<IReadOnlyList<LiveItem>>> providers;
            lock (Sync) providers = Providers.ToList();
            var items = new List<LiveItem>();
            void AddOne(LiveItem item)
            {
                if (item == null || item.Reach == ReachClass.Unset) return;
                items.Add(item);
                foreach (var m in item.Menu ?? Array.Empty<LiveItem>()) AddOne(m);
            }
            foreach (var provider in providers)
                foreach (var item in provider() ?? Array.Empty<LiveItem>()) AddOne(item);
            return items;
        }

        /// <summary>A short, stable ID made from text that is not to be
        /// shown, such as a plugin's path: the first eight hex digits of
        /// its SHA-256, without regard to case.</summary>
        internal static string ShortId(string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((text ?? string.Empty).ToUpperInvariant())))
                .Substring(0, 8).ToLowerInvariant();

        /// <summary>Every live object now, in the order the interface lists
        /// them.</summary>
        internal static List<LiveEntry> Entries(MainViewModel vm)
        {
            var list = new List<LiveEntry>();
            var s = PadForge.Resources.Strings.Strings.Instance;
            if (vm != null) AddWindowsObjects(vm, list, s);
            foreach (var item in Items())
                list.Add(new LiveEntry(item.Id, item.Kind, item.Words, item.Where, item));
            return list;
        }

        private static void AddWindowsObjects(MainViewModel vm, List<LiveEntry> list, PadForge.Resources.Strings.Strings s)
        {
            // The sidebar's pages, which the window builds in code.
            foreach (var (tag, words) in new[]
                     {
                         ("Home", s.Home_Title), ("Devices", s.Devices_Title), ("Profiles", s.Profiles_Title),
                         ("Extras", s.Extras_Title), ("Settings", s.Settings_Title), ("About", s.About_Title),
                     })
                list.Add(new LiveEntry("page/" + tag, LiveKind.Page, words, string.Empty, tag));
            if (vm.Devices?.Devices != null)
                foreach (var row in vm.Devices.Devices)
                    list.Add(new LiveEntry("device/" + AutomationDevices.IdOf(row.InstanceGuid), LiveKind.Device, row.DeviceName, s.Devices_Title, row));
            for (int i = 0; i < InputManager.MaxPads && i < vm.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                var pad = vm.Pads[i];
                int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(i);
                if (number <= 0) number = i + 1;
                string controller = SearchService.SlotName(pad);
                list.Add(new LiveEntry("controller/" + number.ToString(CultureInfo.InvariantCulture), LiveKind.Controller, controller, s.Home_Controllers, i));
                for (int m = 0; m < pad.Macros.Count; m++)
                    list.Add(new LiveEntry("macro/c" + number.ToString(CultureInfo.InvariantCulture) + "/" + m.ToString(CultureInfo.InvariantCulture),
                        LiveKind.Macro, pad.Macros[m].Name, controller, (i, pad.Macros[m])));
            }
            if (vm.Settings?.ProfileItems != null)
                foreach (var profile in vm.Settings.ProfileItems)
                    list.Add(new LiveEntry("profile/" + profile.Id, LiveKind.Profile, profile.Name, s.Profiles_Title, profile));
            var plugins = vm.Dashboard?.PluginRows;
            if (plugins != null)
                foreach (var plugin in plugins)
                    list.Add(new LiveEntry("plugin/" + ShortId(plugin.Path), LiveKind.Plugin, plugin.Name, s.Extras_Plugins_Title, plugin));
        }
    }

    /// <summary>One item of the tray icon's menu (#600).</summary>
    internal sealed record TrayItem(string Key, string StringKey, ReachClass Reach, bool Bold, bool SeparatorBefore);

    /// <summary>
    /// The tray icon's menu, one list for the menu the window draws and the
    /// catalog's entries (#600), so a new item is in both.
    /// </summary>
    internal static class TrayMenu
    {
        internal static readonly IReadOnlyList<TrayItem> Items = new[]
        {
            new TrayItem("Show", "Main_ShowPadForge", ReachClass.Free, Bold: true, SeparatorBefore: false),
            new TrayItem("Exit", "Tray_Exit", ReachClass.Free, Bold: false, SeparatorBefore: true),
        };

        /// <summary>The items as catalog entries of the window, with stable
        /// IDs such as <c>window.menuitem/Tray/Tray_Exit</c>.</summary>
        internal static IReadOnlyList<SearchEntry> Entries()
        {
            var entries = new List<SearchEntry>();
            int order = 0;
            foreach (var item in Items)
                entries.Add(new SearchEntry(SearchKind.MenuItem, SearchPage.Window, new SearchLabel(item.StringKey, null), null, null,
                    -1, -1, null, false, false, "Tray", null, Array.Empty<SearchGate>(), null, null, SearchLabelSource.Button, order++)
                {
                    AutomationId = "Tray_" + item.Key,
                    Reach = item.Reach,
                });
            return entries;
        }
    }
}
