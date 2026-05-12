using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Wpf.Ui.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// Modal dialog for authoring or editing a single
    /// <see cref="ShiftActivator"/>. Invoked from the "+ Shift Layer"
    /// button on the Mappings toolbar, and from the layer-tab right-click
    /// "Configure activator…" menu item. Returns a populated activator on
    /// Save; the caller is responsible for splicing it into the slot's
    /// <see cref="MappingSet.ShiftActivators"/> list and refreshing the UI.
    /// </summary>
    public partial class ShiftActivatorDialog : FluentWindow
    {
        /// <summary>Result on dialog OK. Null on cancel.</summary>
        public ShiftActivator Result { get; private set; }

        // Validation context: layer names must be unique on this slot
        // (excluding the activator being edited).
        private readonly HashSet<string> _existingLayerNames;
        private readonly HashSet<string> _existingLayerMasks;

        public ShiftActivatorDialog(
            IReadOnlyList<InputChoice> availableInputs,
            ShiftActivator existing,
            IEnumerable<ShiftActivator> otherActivators)
        {
            InitializeComponent();

            // Group inputs by device label for the picker (mirrors the
            // Mappings tab's source ComboBox grouping).
            var view = CollectionViewSource.GetDefaultView(availableInputs);
            if (view != null && view.GroupDescriptions != null)
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
            }
            InputCombo.ItemsSource = view;

            // Pre-populate the dialog with the existing activator's fields
            // when we're editing rather than creating.
            if (existing != null)
            {
                LayerNameBox.Text = string.IsNullOrEmpty(existing.LayerName)
                    ? (existing.LayerMask ?? "")
                    : existing.LayerName;
                ModeHold.IsChecked = string.Equals(existing.Mode ?? "Hold", "Hold", StringComparison.Ordinal);
                ModeToggle.IsChecked = string.Equals(existing.Mode, "Toggle", StringComparison.Ordinal);

                // Select the input that matches the existing activator.
                foreach (var choice in availableInputs)
                {
                    if (choice == null) continue;
                    if (string.Equals(choice.Descriptor ?? "", existing.Descriptor ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(choice.DeviceGuid ?? "", existing.DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        InputCombo.SelectedItem = choice;
                        break;
                    }
                }
            }
            else
            {
                LayerNameBox.Text = SuggestNextLayerName(otherActivators);
            }

            // Reserve names already used by other activators on this slot
            // so validation can reject duplicates.
            _existingLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _existingLayerMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (otherActivators != null)
            {
                foreach (var a in otherActivators)
                {
                    if (a == null) continue;
                    if (!string.IsNullOrEmpty(a.LayerName)) _existingLayerNames.Add(a.LayerName);
                    if (!string.IsNullOrEmpty(a.LayerMask)) _existingLayerMasks.Add(a.LayerMask);
                }
            }

            // Hold focus on the name field so the user can immediately type
            // a layer name without a click.
            Loaded += (_, __) =>
            {
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
            };
        }

        /// <summary>Picks a layer name that doesn't collide with existing
        /// activators. Starts at "Shift 1" and increments until free.</summary>
        private static string SuggestNextLayerName(IEnumerable<ShiftActivator> existing)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
            {
                foreach (var a in existing)
                {
                    if (a == null) continue;
                    if (!string.IsNullOrEmpty(a.LayerName)) taken.Add(a.LayerName);
                }
            }
            for (int i = 1; i < 1000; i++)
            {
                var candidate = $"Shift {i}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return "Shift";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string name = (LayerNameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                HintText.Text = Strings.Instance.Pad_Shift_HintNameRequired;
                LayerNameBox.Focus();
                return;
            }
            if (_existingLayerNames.Contains(name))
            {
                HintText.Text = Strings.Instance.Pad_Shift_HintNameDuplicate;
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
                return;
            }

            if (InputCombo.SelectedItem is not InputChoice input)
            {
                HintText.Text = Strings.Instance.Pad_Shift_HintInputRequired;
                InputCombo.Focus();
                return;
            }

            // LayerMask is the engine-side identifier; LayerName is the
            // user-visible label. Pick a unique LayerMask deterministically
            // from the name so saved files are stable. Reuse the name when
            // it isn't already taken as a mask; otherwise suffix a counter.
            string baseMask = string.IsNullOrWhiteSpace(name) ? "Shift" : name;
            string mask = baseMask;
            int suffix = 2;
            while (_existingLayerMasks.Contains(mask))
                mask = $"{baseMask}_{suffix++}";

            Result = new ShiftActivator
            {
                LayerName = name,
                LayerMask = mask,
                DeviceGuid = input.DeviceGuid ?? "",
                Descriptor = input.Descriptor ?? "",
                Mode = ModeToggle.IsChecked == true ? "Toggle" : "Hold",
                Kind = "Button",
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
