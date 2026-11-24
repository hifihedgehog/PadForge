using System.Collections.ObjectModel;
using System.Windows;
using PadForge.Common.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The names a Custom Expression variable's Script Value picker offers
    /// (#597): every name a program set this run, in the order they came. The
    /// picker takes a typed name too, so a macro can wait for a value its
    /// script has not set yet. Kept on the interface's thread.
    /// </summary>
    public static class ScriptValueNames
    {
        public static ObservableCollection<string> Items { get; } = Build();

        private static ObservableCollection<string> Build()
        {
            var items = new ObservableCollection<string>();
            foreach (string name in ScriptValues.Names()) items.Add(name);
            ScriptValues.NameAdded += name =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) Add(items, name);
                else dispatcher.BeginInvoke(new System.Action(() => Add(items, name)));
            };
            return items;
        }

        private static void Add(ObservableCollection<string> items, string name)
        {
            foreach (string known in items)
                if (string.Equals(known, name, System.StringComparison.OrdinalIgnoreCase)) return;
            items.Add(name);
        }
    }
}
