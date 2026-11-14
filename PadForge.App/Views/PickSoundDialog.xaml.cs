using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>Pick a Sound (#574): the sounds of the added packages, each
    /// with a Play button, or the sounds inside one package that was just
    /// added, with an optional Browse Files escape to the file system
    /// (issue #83).</summary>
    public partial class PickSoundDialog : DialogWindow
    {
        /// <summary>One sound's row: its <see cref="Name"/>, the
        /// <see cref="Package"/> it comes from, and the <see cref="Value"/>
        /// handed back to the caller and played by the row's button (a
        /// <c>pfsound://</c> reference or a file path).</summary>
        public sealed class Item
        {
            public string Name { get; }
            public string Package { get; }
            public string Value { get; }

            /// <summary>The Play button's name and tooltip.</summary>
            public string PlayName => string.Format(Strings.Instance.PickSound_Play_Format, Name);

            public Item(string name, string package, string value)
            {
                Name = name;
                Package = package;
                Value = value;
            }

            public override string ToString() => Name;
        }

        private readonly int _previewSlot;
        private readonly int _previewVolume;

        /// <summary>The chosen sound's <see cref="Item.Value"/>, or null when
        /// canceled.</summary>
        public string SelectedSound => (SoundList.SelectedItem as Item)?.Value;

        /// <summary>True when the user clicked Browse Files instead of a row.</summary>
        public bool BrowseRequested { get; private set; }

        /// <summary>The sounds of the added packages, with an optional Browse
        /// Files escape and an optional value to pick on open. Play sounds a
        /// row on <paramref name="previewSlot"/>'s speakers at
        /// <paramref name="previewVolume"/>.</summary>
        public PickSoundDialog(IReadOnlyList<Item> items, bool allowBrowse, int previewSlot, int previewVolume,
            string preselectValue = null)
        {
            InitializeComponent();
            _previewSlot = previewSlot;
            _previewVolume = previewVolume;
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

        /// <summary>The sounds inside one package, such as one just added
        /// from a file: a row per entry, each carrying its reference into
        /// the package.</summary>
        public PickSoundDialog(string package, IReadOnlyList<string> entries, int previewSlot, int previewVolume)
            : this((entries ?? Array.Empty<string>())
                  .Select(entry => new Item(entry, package, PadForge.Common.SoundPackageManager.MakeRef(package, entry))).ToList(),
                  allowBrowse: false, previewSlot, previewVolume)
        {
        }

        /// <summary>Plays the row's sound once: a one-shot with no macro
        /// behind it, on the slot's speakers, or the PC's when the slot has
        /// none.</summary>
        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not Item item || string.IsNullOrEmpty(item.Value)) return;
            PadForge.Common.Input.SoundMacroService.Play(_previewSlot, null, item.Value, _previewVolume, loop: false);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }

        private void SoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // A double click on Play plays twice and picks nothing.
            if (e.OriginalSource is DependencyObject hit && IsInsideAButton(hit)) return;
            if (SoundList.SelectedItem != null)
                DialogResult = true;
        }

        private static bool IsInsideAButton(DependencyObject hit)
        {
            for (var at = hit; at != null; at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is System.Windows.Controls.Primitives.ButtonBase) return true;
            return false;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            BrowseRequested = true;
            DialogResult = true;
        }
    }
}
