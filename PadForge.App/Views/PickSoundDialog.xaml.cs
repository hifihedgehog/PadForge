using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace PadForge.Views
{
    /// <summary>List picker for a sound — either a flat list of entry names
    /// (single package) or a labelled list of sounds drawn from every added
    /// package, with an optional "Browse files…" escape to the filesystem
    /// (issue #83). Same FluentWindow chrome as the other dialogs.</summary>
    public partial class PickSoundDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>One pickable row: a friendly <see cref="Label"/> shown in the
        /// list and the underlying <see cref="Value"/> handed back to the caller
        /// (a <c>pfsound://</c> ref, an entry name, or a file path).</summary>
        public sealed class Item
        {
            public string Label { get; }
            public string Value { get; }
            public Item(string label, string value) { Label = label; Value = value; }
            public override string ToString() => Label;
        }

        /// <summary>The chosen value (the <see cref="Item.Value"/>, or the raw
        /// string in the flat-list case), or null when cancelled.</summary>
        public string SelectedSound => (SoundList.SelectedItem as Item)?.Value
                                       ?? SoundList.SelectedItem as string;

        /// <summary>True when the user clicked "Browse files…" instead of a row.</summary>
        public bool BrowseRequested { get; private set; }

        /// <summary>Flat picker over entry names (e.g. the sounds inside one
        /// freshly-imported package).</summary>
        public PickSoundDialog(string description, List<string> items)
        {
            InitializeComponent();
            DescriptionText.Text = description;
            SoundList.ItemsSource = items;
            if (items != null && items.Count > 0)
                SoundList.SelectedIndex = 0;
        }

        /// <summary>Labelled picker over sounds from the added packages, with an
        /// optional Browse-files escape and an optional pre-selected value.</summary>
        public PickSoundDialog(string description, IReadOnlyList<Item> items, bool allowBrowse, string preselectValue = null)
        {
            InitializeComponent();
            DescriptionText.Text = description;
            SoundList.ItemsSource = items;
            BrowseButton.Visibility = allowBrowse ? Visibility.Visible : Visibility.Collapsed;
            SoundList.SelectedIndex = items != null && items.Count > 0 ? 0 : -1;
            if (preselectValue != null && items != null)
                for (int i = 0; i < items.Count; i++)
                    if (string.Equals(items[i].Value, preselectValue, StringComparison.OrdinalIgnoreCase))
                    {
                        SoundList.SelectedIndex = i;
                        break;
                    }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }

        private void SoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseRequested = true;
            DialogResult = true;
        }

        // Head X (#175 item 11). No cancel handler existed to reuse: the
        // footer Cancel is IsCancel-only, so mirror its close-without-result.
        private void HeaderClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
