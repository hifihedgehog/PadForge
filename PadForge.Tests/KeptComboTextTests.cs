using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Custom Expression variable's Script Value picker (#597) keeps the
    /// name it shows when the program that set that name exits and the name
    /// leaves the picker's list (ScriptValueNames). WPF clears an editable
    /// ComboBox's text when its selected item is removed
    /// (ComboBox.OnSelectionChanged, SelectedItemUpdated), and the text
    /// binding wrote the empty text into the variable at the next
    /// LostFocus.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class KeptComboTextTests
    {
        private sealed class Variable : INotifyPropertyChanged
        {
            private string _value = "x";
            public string ScriptValue
            {
                get => _value;
                set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScriptValue))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        /// <summary>The picker as PadPage.xaml binds it: editable, Text two
        /// way on LostFocus.</summary>
        private static (ComboBox Box, Variable Source, ObservableCollection<string> Items) Picker(bool keeps)
        {
            var items = new ObservableCollection<string> { "a", "x" };
            var source = new Variable();
            var box = new ComboBox { IsEditable = true, ItemsSource = items, DataContext = source };
            box.SetBinding(ComboBox.TextProperty, new Binding(nameof(Variable.ScriptValue)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            if (keeps) KeptComboText.SetIsOn(box, true);
            return (box, source, items);
        }

        [Fact]
        public void ANameThatLeavesTheList_StaysInThePicker_AndInTheVariable()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var (plain, plainSource, plainItems) = Picker(keeps: false);
                var (kept, keptSource, keptItems) = Picker(keeps: true);
                var panel = new StackPanel();
                panel.Children.Add(plain);
                panel.Children.Add(kept);
                var window = new Window { Width = 400, Height = 200, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Content = panel };
                try
                {
                    window.Show();
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal("x", plain.Text);
                    Assert.Equal("x", kept.Text);
                    Assert.Equal("x", kept.SelectedItem);

                    plainItems.Remove("x");
                    keptItems.Remove("x");
                    PageRenderDump.Settle(dispatcher);
                    // Control, in the same window: WPF's own picker loses the name.
                    Assert.Equal(string.Empty, plain.Text);
                    plain.GetBindingExpression(ComboBox.TextProperty).UpdateSource();
                    Assert.Equal(string.Empty, plainSource.ScriptValue);

                    Assert.Equal("x", kept.Text);
                    kept.GetBindingExpression(ComboBox.TextProperty).UpdateSource();
                    Assert.Equal("x", keptSource.ScriptValue);

                    // The name comes back to the list and is picked again.
                    keptItems.Add("x");
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal("x", kept.Text);

                    // The user's own picks and edits stand.
                    kept.SelectedItem = "a";
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal("a", kept.Text);
                    kept.Text = "typed";
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal("typed", kept.Text);
                    // A reset empties the variable, and the picker with it.
                    kept.GetBindingExpression(ComboBox.TextProperty).UpdateSource();
                    kept.SelectedItem = "a";
                    PageRenderDump.Settle(dispatcher);
                    keptSource.ScriptValue = string.Empty;
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal(string.Empty, kept.Text);
                }
                finally
                {
                    window.Close();
                }
            }, TimeSpan.FromMinutes(2), "The kept picker text test timed out.");
        }

        /// <summary>The Script Value picker of PadPage.xaml carries the
        /// behavior.</summary>
        [Fact]
        public void TheScriptValuePicker_KeepsItsText()
        {
            var doc = XDocument.Parse(File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", "PadPage.xaml")));
            var picker = doc.Descendants().Single(e => e.Name.LocalName == "ComboBox" && ReachMarkup.Attr(e, "ItemsSource") == "{x:Static vm:ScriptValueNames.Items}");
            Assert.Equal("True", ReachMarkup.Attr(picker, "KeptComboText.IsOn"));
        }
    }
}
