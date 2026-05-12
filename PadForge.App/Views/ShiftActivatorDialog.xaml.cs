using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Wpf.Ui.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// Modal dialog for authoring or editing a single
    /// <see cref="ShiftActivator"/>. Drives the full v1/v2/v3 field set
    /// with proper controls (HSV color picker, layer-dropdown for Custom
    /// jump target, checkbox list for Cycle membership).
    /// </summary>
    public partial class ShiftActivatorDialog : FluentWindow
    {
        public ShiftActivator Result { get; private set; }

        private readonly HashSet<string> _existingLayerNames;
        private readonly HashSet<string> _existingLayerMasks;
        private readonly IReadOnlyList<InputChoice> _buttonChoices;
        private readonly IReadOnlyList<InputChoice> _axisChoices;
        private readonly IReadOnlyList<ShiftActivator> _otherActivators;
        private bool _colorSet;
        private bool _suppressColorPickerWriteback;

        private static readonly SolidColorBrush UnsetColorBrush =
            new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        public ShiftActivatorDialog(
            IReadOnlyList<InputChoice> availableInputs,
            ShiftActivator existing,
            IEnumerable<ShiftActivator> otherActivators)
        {
            InitializeComponent();

            // Split the cross-device input list into button-class (Button,
            // POV-direction) and axis-class (Axis, Slider).
            var buttons = new List<InputChoice>();
            var axes = new List<InputChoice>();
            foreach (var c in availableInputs ?? Array.Empty<InputChoice>())
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
            _otherActivators = otherActivators?.Where(a => a != null).ToList()
                ?? new List<ShiftActivator>();

            // Populate the Custom-jump and Cycle dropdowns with the
            // OTHER layers on this slot. Each item carries a LayerMask;
            // an extra "(Base)" entry maps to empty string.
            var jumpItems = new List<LayerOption>
            {
                new LayerOption { LayerMask = "", DisplayName = "(Base)" }
            };
            var cycleItems = new List<LayerOption>();
            foreach (var a in _otherActivators)
            {
                var display = string.IsNullOrEmpty(a.LayerName) ? a.LayerMask : a.LayerName;
                jumpItems.Add(new LayerOption { LayerMask = a.LayerMask ?? "", DisplayName = display });
                cycleItems.Add(new LayerOption { LayerMask = a.LayerMask ?? "", DisplayName = display });
            }
            JumpToLayerCombo.ItemsSource = jumpItems;
            CycleLayersList.ItemsSource = cycleItems;

            // Validation context.
            _existingLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _existingLayerMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _otherActivators)
            {
                if (!string.IsNullOrEmpty(a.LayerName)) _existingLayerNames.Add(a.LayerName);
                if (!string.IsNullOrEmpty(a.LayerMask)) _existingLayerMasks.Add(a.LayerMask);
            }

            // Pre-populate fields when editing.
            if (existing != null)
            {
                LayerNameBox.Text = string.IsNullOrEmpty(existing.LayerName)
                    ? (existing.LayerMask ?? "")
                    : existing.LayerName;
                SelectComboItemByTag(KindCombo, existing.Kind ?? "Button");
                SelectComboItemByTag(ModeCombo, existing.Mode ?? "Hold");
                AxisThresholdSlider.Value = existing.AxisThreshold;
                DelaySlider.Value = existing.DelayMs;
                InheritUnmappedBox.IsChecked = existing.InheritUnmapped;

                // Color: parse hex into picker RGB; set _colorSet flag.
                if (!string.IsNullOrEmpty(existing.Color))
                {
                    var c = ParseColor(existing.Color);
                    if (c.HasValue)
                    {
                        _suppressColorPickerWriteback = true;
                        ColorPicker.Red = c.Value.R;
                        ColorPicker.Green = c.Value.G;
                        ColorPicker.Blue = c.Value.B;
                        _suppressColorPickerWriteback = false;
                        _colorSet = true;
                    }
                }

                // Select JumpToLayer in the dropdown when in Custom mode.
                SelectJumpToLayer(existing.JumpToLayer ?? "");
                // Select Cycle layers from the pipe-separated string.
                SelectCycleLayers(existing.CycleLayers ?? "");

                Loaded += (_, __) => SelectInputs(existing);
            }
            else
            {
                LayerNameBox.Text = SuggestNextLayerName(_otherActivators);
                SelectComboItemByTag(KindCombo, "Button");
                SelectComboItemByTag(ModeCombo, "Hold");
                AxisThresholdSlider.Value = 0.5;
                DelaySlider.Value = 0;
                // Default jump target = Base for new Custom activators.
                if (jumpItems.Count > 0) JumpToLayerCombo.SelectedIndex = 0;
            }

            // Watch the picker's RGB DPs so any user drag flags color-set.
            // DependencyPropertyDescriptor pumps the change events without
            // requiring a custom event on ColorPickerControl.
            var redDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.RedProperty, typeof(ColorPickerControl));
            var greenDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.GreenProperty, typeof(ColorPickerControl));
            var blueDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.BlueProperty, typeof(ColorPickerControl));
            EventHandler onRgb = (_, __) => OnColorPickerChanged();
            redDpd?.AddValueChanged(ColorPicker, onRgb);
            greenDpd?.AddValueChanged(ColorPicker, onRgb);
            blueDpd?.AddValueChanged(ColorPicker, onRgb);
            UpdateColorPreview();

            ApplyKindVisibility();
            ApplyModeVisibility();
            RefreshInputComboSources();

            Loaded += (_, __) =>
            {
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
            };
        }

        private void OnColorPickerChanged()
        {
            if (_suppressColorPickerWriteback) return;
            _colorSet = true;
            UpdateColorPreview();
        }

        private void UpdateColorPreview()
        {
            if (!_colorSet)
            {
                ColorPreviewSwatch.Background = UnsetColorBrush;
                return;
            }
            ColorPreviewSwatch.Background = new SolidColorBrush(
                Color.FromRgb(ColorPicker.Red, ColorPicker.Green, ColorPicker.Blue));
        }

        private void ClearColor_Click(object sender, RoutedEventArgs e)
        {
            _colorSet = false;
            UpdateColorPreview();
        }

        private static Color? ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c) return c;
            }
            catch { }
            return null;
        }

        private void SelectJumpToLayer(string layerMask)
        {
            foreach (var item in JumpToLayerCombo.Items)
            {
                if (item is LayerOption opt
                    && string.Equals(opt.LayerMask, layerMask ?? "", StringComparison.Ordinal))
                {
                    JumpToLayerCombo.SelectedItem = item;
                    return;
                }
            }
            if (JumpToLayerCombo.Items.Count > 0) JumpToLayerCombo.SelectedIndex = 0;
        }

        private void SelectCycleLayers(string pipeSeparated)
        {
            if (string.IsNullOrEmpty(pipeSeparated)) return;
            var masks = new HashSet<string>(
                pipeSeparated.Split('|', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            foreach (var item in CycleLayersList.Items)
            {
                if (item is LayerOption opt && masks.Contains(opt.LayerMask))
                {
                    CycleLayersList.SelectedItems.Add(item);
                }
            }
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
            if (InputCombo.ItemsSource != null)
            {
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
            JumpToLayerCombo.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            CycleLabel.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;
            CycleLayersList.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;
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
                ShowHint(Strings.Instance.Pad_Shift_HintNameRequired);
                LayerNameBox.Focus();
                return;
            }
            if (_existingLayerNames.Contains(name))
            {
                ShowHint(Strings.Instance.Pad_Shift_HintNameDuplicate);
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
                return;
            }
            if (InputCombo.SelectedItem is not InputChoice input)
            {
                ShowHint(Strings.Instance.Pad_Shift_HintInputRequired);
                InputCombo.Focus();
                return;
            }
            string kind = ReadComboTag(KindCombo);
            InputChoice chordSecond = ChordSecondCombo.SelectedItem as InputChoice;
            if (kind == "Chord" && chordSecond == null)
            {
                ShowHint(Strings.Instance.Pad_Shift_HintInputRequired);
                ChordSecondCombo.Focus();
                return;
            }

            string baseMask = string.IsNullOrWhiteSpace(name) ? "Shift" : name;
            string mask = baseMask;
            int suffix = 2;
            while (_existingLayerMasks.Contains(mask))
                mask = $"{baseMask}_{suffix++}";

            string jumpToLayer = "";
            if (JumpToLayerCombo.SelectedItem is LayerOption jump)
                jumpToLayer = jump.LayerMask ?? "";

            string cycleLayers = "";
            if (CycleLayersList.SelectedItems != null && CycleLayersList.SelectedItems.Count > 0)
            {
                var picked = new List<string>();
                foreach (var item in CycleLayersList.SelectedItems)
                    if (item is LayerOption opt && !string.IsNullOrEmpty(opt.LayerMask))
                        picked.Add(opt.LayerMask);
                cycleLayers = string.Join("|", picked);
            }

            string colorHex = _colorSet
                ? $"#{ColorPicker.Red:X2}{ColorPicker.Green:X2}{ColorPicker.Blue:X2}"
                : "";

            Result = new ShiftActivator
            {
                LayerName = name,
                LayerMask = mask,
                DeviceGuid = input.DeviceGuid ?? "",
                Descriptor = input.Descriptor ?? "",
                Mode = ReadComboTag(ModeCombo),
                Kind = kind,
                InheritUnmapped = InheritUnmappedBox.IsChecked == true,
                ChordSecondDeviceGuid = chordSecond?.DeviceGuid ?? "",
                ChordSecondDescriptor = chordSecond?.Descriptor ?? "",
                AxisThreshold = AxisThresholdSlider.Value,
                JumpToLayer = jumpToLayer,
                CycleLayers = cycleLayers,
                DelayMs = (int)Math.Round(DelaySlider.Value),
                PostponeMapping = false,
                Color = colorHex,
            };

            DialogResult = true;
            Close();
        }

        private void ShowHint(string text)
        {
            HintText.Text = text;
            HintText.Visibility = Visibility.Visible;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>Adapter so the existing list-population code can bind
        /// against simple "(LayerMask, DisplayName)" rows. Mirrors how
        /// InputChoice wraps the (DeviceGuid, Descriptor, DisplayName)
        /// pair for the input picker.</summary>
        private class LayerOption
        {
            public string LayerMask { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }
    }
}
