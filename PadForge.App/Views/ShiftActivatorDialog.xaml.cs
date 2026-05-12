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
    /// <see cref="ShiftActivator"/>. Supports the full v1/v2/v3 field set:
    /// activator kind (Button / Chord / Axis), input picker(s), axis
    /// threshold, mode (Hold / Toggle / Custom / Cycle / Sticky), jump
    /// target, cycle layer list, delay debounce, postpone-mapping
    /// behavior, and per-layer color.
    /// </summary>
    public partial class ShiftActivatorDialog : FluentWindow
    {
        public ShiftActivator Result { get; private set; }

        private readonly HashSet<string> _existingLayerNames;
        private readonly HashSet<string> _existingLayerMasks;
        private readonly IReadOnlyList<InputChoice> _buttonChoices;
        private readonly IReadOnlyList<InputChoice> _axisChoices;

        public ShiftActivatorDialog(
            IReadOnlyList<InputChoice> availableInputs,
            ShiftActivator existing,
            IEnumerable<ShiftActivator> otherActivators)
        {
            InitializeComponent();

            // Split the cross-device input list into button-class (Button,
            // POV-direction) and axis-class (Axis, Slider). Kind dropdown
            // determines which list the input picker shows.
            var buttons = new List<InputChoice>();
            var axes = new List<InputChoice>();
            foreach (var c in availableInputs ?? System.Array.Empty<InputChoice>())
            {
                if (c == null) continue;
                var d = c.Descriptor ?? "";
                if (d.StartsWith("Axis ", StringComparison.OrdinalIgnoreCase)
                    || d.StartsWith("Slider ", StringComparison.OrdinalIgnoreCase))
                    axes.Add(c);
                else
                    buttons.Add(c);
            }
            _buttonChoices = buttons;
            _axisChoices = axes;

            // Track other activators' names + masks so the dialog can
            // reject duplicates.
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

            // Pre-populate dialog fields when editing.
            if (existing != null)
            {
                LayerNameBox.Text = string.IsNullOrEmpty(existing.LayerName)
                    ? (existing.LayerMask ?? "")
                    : existing.LayerName;
                SelectComboItemByTag(KindCombo, existing.Kind ?? "Button");
                SelectComboItemByTag(ModeCombo, existing.Mode ?? "Hold");
                AxisThresholdSlider.Value = existing.AxisThreshold;
                JumpToLayerBox.Text = existing.JumpToLayer ?? "";
                CycleLayersBox.Text = existing.CycleLayers ?? "";
                DelaySlider.Value = existing.DelayMs;
                PostponeMappingBox.IsChecked = existing.PostponeMapping;
                ColorBox.Text = existing.Color ?? "";
                // Inputs populated after kind selection.
                Loaded += (_, __) => SelectInputs(existing);
            }
            else
            {
                LayerNameBox.Text = SuggestNextLayerName(otherActivators);
                SelectComboItemByTag(KindCombo, "Button");
                SelectComboItemByTag(ModeCombo, "Hold");
                AxisThresholdSlider.Value = 0.5;
                DelaySlider.Value = 0;
            }

            // Apply visibility based on the initial kind + mode.
            ApplyKindVisibility();
            ApplyModeVisibility();
            RefreshInputComboSources();

            Loaded += (_, __) =>
            {
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
            };
        }

        private static void SelectComboItemByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string ReadComboTag(ComboBox combo)
            => combo.SelectedItem is ComboBoxItem cbi ? (cbi.Tag as string ?? "") : "";

        private void SelectInputs(ShiftActivator existing)
        {
            if (InputCombo.ItemsSource is null) return;
            foreach (var item in InputCombo.Items)
            {
                if (item is InputChoice c
                    && string.Equals(c.Descriptor ?? "", existing.Descriptor ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.DeviceGuid ?? "", existing.DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    InputCombo.SelectedItem = c;
                    break;
                }
            }
            if (ChordSecondCombo.ItemsSource != null)
            {
                foreach (var item in ChordSecondCombo.Items)
                {
                    if (item is InputChoice c
                        && string.Equals(c.Descriptor ?? "", existing.ChordSecondDescriptor ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", existing.ChordSecondDeviceGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        ChordSecondCombo.SelectedItem = c;
                        break;
                    }
                }
            }
        }

        /// <summary>Sets InputCombo and ChordSecondCombo's ItemsSource based
        /// on the selected Kind (Button-class for Button/Chord, Axis-class
        /// for Axis). Re-runs whenever Kind changes.</summary>
        private void RefreshInputComboSources()
        {
            string kind = ReadComboTag(KindCombo);
            var primaryList = kind == "Axis" ? _axisChoices : _buttonChoices;

            var view = CollectionViewSource.GetDefaultView(primaryList);
            if (view?.GroupDescriptions != null)
            {
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
            }
            InputCombo.ItemsSource = view;

            // Chord second always uses button-class.
            var chordView = CollectionViewSource.GetDefaultView(_buttonChoices);
            if (chordView?.GroupDescriptions != null)
            {
                chordView.GroupDescriptions.Clear();
                chordView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
            }
            ChordSecondCombo.ItemsSource = chordView;
        }

        private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyKindVisibility();
            RefreshInputComboSources();
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyModeVisibility();
        }

        private void ApplyKindVisibility()
        {
            string kind = ReadComboTag(KindCombo);
            bool isChord = kind == "Chord";
            bool isAxis = kind == "Axis";
            ChordLabel.Visibility = isChord ? Visibility.Visible : Visibility.Collapsed;
            ChordSecondCombo.Visibility = isChord ? Visibility.Visible : Visibility.Collapsed;
            AxisThresholdLabel.Visibility = isAxis ? Visibility.Visible : Visibility.Collapsed;
            AxisThresholdRow.Visibility = isAxis ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyModeVisibility()
        {
            string mode = ReadComboTag(ModeCombo);
            bool isCustom = mode == "Custom";
            bool isCycle = mode == "Cycle";
            JumpLabel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            JumpToLayerBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            CycleLabel.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;
            CycleLayersBox.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string SuggestNextLayerName(IEnumerable<ShiftActivator> existing)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
                foreach (var a in existing)
                    if (a != null && !string.IsNullOrEmpty(a.LayerName)) taken.Add(a.LayerName);
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

            string kind = ReadComboTag(KindCombo);
            InputChoice chordSecond = ChordSecondCombo.SelectedItem as InputChoice;
            if (kind == "Chord" && chordSecond == null)
            {
                HintText.Text = Strings.Instance.Pad_Shift_HintInputRequired;
                ChordSecondCombo.Focus();
                return;
            }

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
                Mode = ReadComboTag(ModeCombo),
                Kind = kind,
                ChordSecondDeviceGuid = chordSecond?.DeviceGuid ?? "",
                ChordSecondDescriptor = chordSecond?.Descriptor ?? "",
                AxisThreshold = AxisThresholdSlider.Value,
                JumpToLayer = (JumpToLayerBox.Text ?? "").Trim(),
                CycleLayers = (CycleLayersBox.Text ?? "").Trim(),
                DelayMs = (int)System.Math.Round(DelaySlider.Value),
                PostponeMapping = PostponeMappingBox.IsChecked == true,
                Color = (ColorBox.Text ?? "").Trim(),
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
