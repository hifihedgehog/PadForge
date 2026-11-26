using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PadForge.Common
{
    /// <summary>
    /// An editable ComboBox that keeps the text it shows when the item that
    /// text picked leaves its list (#600, the Script Value picker). WPF
    /// raises SelectionChanged for the lost item and then sets the text to
    /// that of no item, the empty text (ComboBox.OnSelectionChanged,
    /// SelectedItemUpdated), and a Text binding that writes on LostFocus
    /// then writes it into its source. The picker's list holds the names
    /// the programs still connected set, so a macro's variable lost the
    /// name it waits for each time the program that set it exited.
    ///
    /// <para>The text is put back once WPF has cleared it, and only for a
    /// lost item that is no longer in the list: the user's own edit, a
    /// pick of another item and a reset that empties the source all leave
    /// the item in the list or the text empty before the event.</para>
    /// </summary>
    public static class KeptComboText
    {
        public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
            "IsOn", typeof(bool), typeof(KeptComboText), new PropertyMetadata(false, OnIsOnChanged));

        public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

        public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);

        private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ComboBox combo) return;
            combo.SelectionChanged -= OnSelectionChanged;
            if (e.NewValue is true) combo.SelectionChanged += OnSelectionChanged;
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The event is raised before the ComboBox clears its text, so the
            // text here is still the one the lost item showed.
            if (sender is not ComboBox { IsEditable: true } combo || !ReferenceEquals(e.OriginalSource, combo)
                || e.AddedItems.Count != 0 || e.RemovedItems.Count == 0)
                return;
            string kept = combo.Text;
            if (string.IsNullOrEmpty(kept) || combo.Items.Contains(e.RemovedItems[0])) return;
            combo.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (combo.SelectedItem == null && string.IsNullOrEmpty(combo.Text))
                    combo.SetCurrentValue(ComboBox.TextProperty, kept);
            }));
        }
    }
}
