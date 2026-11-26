using System;
using System.Collections.ObjectModel;
using System.Windows;
using PadForge.Common.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The names a Custom Expression variable's Script Value picker offers
    /// (#600): every name a program that is still connected set. The picker
    /// takes a typed name too, so a macro can wait for a value its script has
    /// not set yet. Kept on the interface's thread.
    /// </summary>
    public static class ScriptValueNames
    {
        public static ObservableCollection<string> Items { get; } = Build(null, out _);

        /// <summary>The list, followed from now on. It follows the events
        /// before it reads the names held now, so a name a session sets while
        /// the list is filled arrives by one or the other, and
        /// <see cref="Add"/> keeps it once. <paramref name="between"/> runs
        /// between the two steps (a test seam), and
        /// <paramref name="unhook"/> stops the following.</summary>
        internal static ObservableCollection<string> Build(Action between, out Action unhook)
        {
            var items = new ObservableCollection<string>();
            // Every change is queued to the interface's thread, even one told
            // on it, so the changes are applied in the order ScriptValues
            // tells them: one applied at once could pass one queued before it.
            Action<string> added = name =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) Add(items, name);
                else dispatcher.BeginInvoke(new Action(() => Add(items, name)));
            };
            // A name whose last session ended is offered no more, so a
            // program that makes up names in a loop cannot grow the list.
            Action<string> removed = name =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) Remove(items, name);
                else dispatcher.BeginInvoke(new Action(() => Remove(items, name)));
            };
            Follow(added, removed);
            between?.Invoke();
            Fill(items);
            unhook = () =>
            {
                ScriptValues.NameAdded -= added;
                ScriptValues.NameRemoved -= removed;
            };
            return items;
        }

        private static void Follow(Action<string> added, Action<string> removed)
        {
            ScriptValues.NameAdded += added;
            ScriptValues.NameRemoved += removed;
        }

        private static void Fill(ObservableCollection<string> items)
        {
            foreach (string name in ScriptValues.Names()) Add(items, name);
        }

        private static void Remove(ObservableCollection<string> items, string name)
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (string.Equals(items[i], name, StringComparison.OrdinalIgnoreCase)) items.RemoveAt(i);
        }

        private static void Add(ObservableCollection<string> items, string name)
        {
            foreach (string known in items)
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return;
            items.Add(name);
        }
    }
}
