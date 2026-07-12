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
        private readonly PadForge.Services.RecorderService _recorder;
        private readonly int _padIndex;
        private bool _colorSet;
        private bool _suppressColorPickerWriteback;
        private bool _recordingPrimary;
        private bool _recordingChord;
        private bool _baseMode;   // #119: editing only the Base layer's flyout appearance
        private string _selectedIcon = "";

        // Tracked so the matching RemoveValueChanged fires on Closed.
        // DependencyPropertyDescriptor holds a static strong reference to
        // the value-changed handler; without the explicit removal the
        // dialog leaks for the lifetime of the process.
        private System.ComponentModel.DependencyPropertyDescriptor _redDpd;
        private System.ComponentModel.DependencyPropertyDescriptor _greenDpd;
        private System.ComponentModel.DependencyPropertyDescriptor _blueDpd;
        private EventHandler _onRgbHandler;

        /// <summary>Base-appearance mode (#119): edit only the Base layer's
        /// display name, icon, and color dot for the engaged-layer flyout and
        /// the Base tab. Base has no activator input/mode/cycle, so every
        /// activator-specific row is collapsed. Save returns a
        /// <see cref="ShiftActivator"/> carrying just LayerName/Color/Icon
        /// (LayerMask = "Base"); the caller stores those on the MappingSet.</summary>
        public ShiftActivatorDialog(string baseName, string baseColor, string baseIcon)
        {
            InitializeComponent();
            _baseMode = true;
            Title = Strings.Instance.Pad_Shift_BaseConfigTitle;
            // Only name + icon + color show in this mode; shrink from the full
            // 700px so the buttons aren't stranded above a dead gap.
            Height = 580; // 520 content + the item-11 dialog head

            LayerNameBox.Text = baseName ?? "";

            if (!string.IsNullOrEmpty(baseColor))
            {
                var c = ParseColor(baseColor);
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

            // Same RGB-watch wiring + leak teardown as the main ctor, scoped
            // to this mode (no recorder, no input lists).
            _redDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.RedProperty, typeof(ColorPickerControl));
            _greenDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.GreenProperty, typeof(ColorPickerControl));
            _blueDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.BlueProperty, typeof(ColorPickerControl));
            _onRgbHandler = (_, __) => OnColorPickerChanged();
            _redDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            _greenDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            _blueDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            UpdateHexBoxFromPicker();

            InitEmojiPicker(baseIcon ?? "");
            CollapseForBaseMode();

            Loaded += (_, __) => { LayerNameBox.Focus(); LayerNameBox.SelectAll(); };
            Closed += (_, __) =>
            {
                if (_onRgbHandler != null)
                {
                    _redDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _greenDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _blueDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _onRgbHandler = null;
                }
            };
        }

        /// <summary>Collapses every activator-specific row, leaving the name,
        /// icon, and color sections for Base-appearance mode.</summary>
        private void CollapseForBaseMode()
        {
            var hide = new System.Windows.UIElement[]
            {
                KindLabel, KindCombo, InputLabel, InputRow, ChordLabel, ChordSecondRow,
                AxisThresholdLabel, AxisThresholdRow, ModeLabel, ModeCombo,
                BehaviorChecksRow, CycleHeaderRow,
                CycleLayersList, DelayRow, HintText,
            };
            foreach (var el in hide)
                if (el != null) el.Visibility = Visibility.Collapsed;
        }

        public ShiftActivatorDialog(
            IReadOnlyList<InputChoice> availableInputs,
            ShiftActivator existing,
            IEnumerable<ShiftActivator> otherActivators,
            PadForge.Services.RecorderService recorder = null,
            int padIndex = -1)
        {
            InitializeComponent();
            _recorder = recorder;
            _padIndex = padIndex;

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

            // Populate the Cycle layer list with the OTHER layers on this slot.
            // Each item carries a LayerMask.
            var cycleItems = new List<LayerOption>();
            foreach (var a in _otherActivators)
            {
                var display = string.IsNullOrEmpty(a.LayerName) ? a.LayerMask : a.LayerName;
                cycleItems.Add(new LayerOption { LayerMask = a.LayerMask ?? "", DisplayName = display });
            }
            // #119: include the activator's OWN layer in the cycle queue. A
            // Next/Previous pair walking one queue must each list the SAME set
            // of layers to share a cursor, and a queue can include the walker's
            // own layer (Custom-jump excludes it, but Cycle is a queue, not a
            // jump-to-other). Without this the own layer is unreachable and two
            // buttons can never hold an identical list.
            if (existing != null && !string.IsNullOrEmpty(existing.LayerMask)
                && !cycleItems.Exists(o => string.Equals(o.LayerMask, existing.LayerMask, StringComparison.Ordinal)))
            {
                var ownDisplay = string.IsNullOrEmpty(existing.LayerName) ? existing.LayerMask : existing.LayerName;
                cycleItems.Add(new LayerOption { LayerMask = existing.LayerMask, DisplayName = ownDisplay });
            }
            // Canonical order (by mask) so two activators that check the same
            // set produce the same pipe-joined string, the shared-cursor key.
            cycleItems.Sort((x, y) => string.CompareOrdinal(x.LayerMask, y.LayerMask));
            CycleLayersList.ItemsSource = cycleItems;

            // Validation context.
            _existingLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _existingLayerMasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _otherActivators)
            {
                if (!string.IsNullOrEmpty(a.LayerName)) _existingLayerNames.Add(a.LayerName);
                if (!string.IsNullOrEmpty(a.LayerMask)) _existingLayerMasks.Add(a.LayerMask);
            }

            // Bind the data-shaped option lists for the two two-line
            // dropdowns. Set ItemsSource BEFORE SelectedValue so the
            // SelectedValuePath="Value" wiring can resolve the matching item.
            KindCombo.ItemsSource = BuildKindOptions();
            ModeCombo.ItemsSource = BuildModeOptions();

            // Pre-populate fields when editing.
            if (existing != null)
            {
                LayerNameBox.Text = string.IsNullOrEmpty(existing.LayerName)
                    ? (existing.LayerMask ?? "")
                    : existing.LayerName;
                KindCombo.SelectedValue = existing.Kind ?? "Button";
                ModeCombo.SelectedValue = existing.Mode ?? "Hold";
                AxisThresholdSlider.Value = existing.AxisThreshold;
                DelaySlider.Value = existing.DelayMs;
                AutoCancelSlider.Value = existing.AutoCancelMs;
                InheritUnmappedBox.IsChecked = existing.InheritUnmapped;
                PostponeMappingBox.IsChecked = existing.PostponeMapping;

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

                // Select Cycle layers from the pipe-separated string.
                SelectCycleLayers(existing.CycleLayers ?? "");
                CycleWrapBox.IsChecked = existing.CycleWrap;
                CycleIncludeBaseBox.IsChecked = existing.CycleIncludeBase;

                Loaded += (_, __) => SelectInputs(existing);
            }
            else
            {
                LayerNameBox.Text = SuggestNextLayerName(_otherActivators);
                KindCombo.SelectedValue = "Button";
                ModeCombo.SelectedValue = "Hold";
                AxisThresholdSlider.Value = 0.5;
                DelaySlider.Value = 0;
                CycleWrapBox.IsChecked = true;
                CycleIncludeBaseBox.IsChecked = false;
                // Honor the picker's initial color on first save. Without this
                // the user sees blue in the picker but the tab strip shows gray
                // because _colorSet stayed false until they dragged the picker.
                // Reset always takes it back to empty.
                _colorSet = true;
            }

            // Watch the picker's RGB DPs so any user drag flags color-set.
            // DependencyPropertyDescriptor pumps the change events without
            // requiring a custom event on ColorPickerControl. Stash the
            // DPDs + handler on instance fields so Closed can RemoveValueChanged.
            _redDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.RedProperty, typeof(ColorPickerControl));
            _greenDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.GreenProperty, typeof(ColorPickerControl));
            _blueDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ColorPickerControl.BlueProperty, typeof(ColorPickerControl));
            _onRgbHandler = (_, __) => OnColorPickerChanged();
            _redDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            _greenDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            _blueDpd?.AddValueChanged(ColorPicker, _onRgbHandler);
            UpdateHexBoxFromPicker();

            ApplyKindVisibility();
            ApplyModeVisibility();
            RefreshInputComboSources();
            InitEmojiPicker(existing?.Icon ?? "");

            Loaded += (_, __) =>
            {
                LayerNameBox.Focus();
                LayerNameBox.SelectAll();
            };

            // Cancel any freeform recording session if the dialog is closed
            // (Cancel button, Esc, or the X close). Without this the
            // recorder keeps polling against a closed dialog and the next
            // input would fire into a vanished callback.
            Closed += (_, __) =>
            {
                if (_recordingPrimary || _recordingChord)
                    _recorder?.CancelRecording();
                if (_onRgbHandler != null)
                {
                    _redDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _greenDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _blueDpd?.RemoveValueChanged(ColorPicker, _onRgbHandler);
                    _onRgbHandler = null;
                }
            };
        }

        private void OnColorPickerChanged()
        {
            if (_suppressColorPickerWriteback) return;
            _colorSet = true;
            UpdateHexBoxFromPicker();
        }

        private void UpdateHexBoxFromPicker()
        {
            if (ColorHexBox == null) return;
            ColorHexBox.Text = $"{ColorPicker.Red:X2}{ColorPicker.Green:X2}{ColorPicker.Blue:X2}";
        }

        private void ColorHexBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                ColorHexBox_Apply();
        }

        private void ColorHexBox_LostFocus(object sender, RoutedEventArgs e)
            => ColorHexBox_Apply();

        private void ColorHexBox_Apply()
        {
            if (ColorHexBox == null) return;
            string text = (ColorHexBox.Text ?? "").Trim();
            if (text.StartsWith("#")) text = text.Substring(1);

            if (text.Length == 6
                && byte.TryParse(text.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte b))
            {
                ColorPicker.Red = r;
                ColorPicker.Green = g;
                ColorPicker.Blue = b;
            }

            // Always reformat to canonical RRGGBB (echo back on success,
            // revert on failure).
            UpdateHexBoxFromPicker();
        }

        private void ClearColor_Click(object sender, RoutedEventArgs e)
        {
            _colorSet = false;
            _suppressColorPickerWriteback = true;
            ColorPicker.Red = 0;
            ColorPicker.Green = 0;
            ColorPicker.Blue = 0;
            _suppressColorPickerWriteback = false;
            UpdateHexBoxFromPicker();
        }

        private void ResetRed_Click(object sender, RoutedEventArgs e)
            => ColorPicker.Red = 0;

        private void ResetGreen_Click(object sender, RoutedEventArgs e)
            => ColorPicker.Green = 0;

        private void ResetBlue_Click(object sender, RoutedEventArgs e)
            => ColorPicker.Blue = 0;

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
            // The second picker holds the chord second half (Kind=Chord) or the
            // Cycle Previous button (Mode=Cycle, #119).
            bool isCycle = string.Equals(existing.Mode, "Cycle", StringComparison.Ordinal);
            string secondDesc = isCycle ? existing.CyclePrevDescriptor : existing.ChordSecondDescriptor;
            string secondGuid = isCycle ? existing.CyclePrevDeviceGuid : existing.ChordSecondDeviceGuid;
            if (ChordSecondCombo.ItemsSource != null && !string.IsNullOrEmpty(secondDesc))
            {
                foreach (var item in ChordSecondCombo.Items)
                {
                    if (item is InputChoice c
                        && string.Equals(c.Descriptor ?? "", secondDesc, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(c.DeviceGuid ?? "", secondGuid ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        ChordSecondCombo.SelectedItem = c;
                        break;
                    }
                }
            }
        }

        private void RefreshInputComboSources()
        {
            string kind = KindCombo.SelectedValue as string ?? "Button";
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

        /// <summary>Two-line dropdown item — bold name + small description.
        /// Mirrors the Mappings tab combine-mode picker shape so users get a
        /// one-line explanation of each option without hunting tooltips.</summary>
        private class ChoiceOption
        {
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
        }

        private static System.Collections.Generic.IReadOnlyList<ChoiceOption> BuildKindOptions()
        {
            var s = Strings.Instance;
            return new[]
            {
                new ChoiceOption { Value = "Button", Name = s.Pad_Shift_KindButton, Description = s.Pad_Shift_KindButton_Subtitle },
                new ChoiceOption { Value = "Chord",  Name = s.Pad_Shift_KindChord,  Description = s.Pad_Shift_KindChord_Subtitle  },
                new ChoiceOption { Value = "Axis",   Name = s.Pad_Shift_KindAxis,   Description = s.Pad_Shift_KindAxis_Subtitle   },
            };
        }

        private static System.Collections.Generic.IReadOnlyList<ChoiceOption> BuildModeOptions()
        {
            var s = Strings.Instance;
            return new[]
            {
                new ChoiceOption { Value = "Hold",   Name = s.Pad_Shift_ModeHold,   Description = s.Pad_Shift_ModeHold_Subtitle   },
                new ChoiceOption { Value = "Toggle", Name = s.Pad_Shift_ModeToggle, Description = s.Pad_Shift_ModeToggle_Subtitle },
                new ChoiceOption { Value = "Custom", Name = s.Pad_Shift_ModeCustom, Description = s.Pad_Shift_ModeCustom_Subtitle },
                new ChoiceOption { Value = "Cycle",  Name = s.Pad_Shift_ModeCycle,  Description = s.Pad_Shift_ModeCycle_Subtitle  },
                new ChoiceOption { Value = "Sticky", Name = s.Pad_Shift_ModeSticky, Description = s.Pad_Shift_ModeSticky_Subtitle },
                new ChoiceOption { Value = "Passive", Name = s.Pad_Shift_ModePassive, Description = s.Pad_Shift_ModePassive_Subtitle },
            };
        }

        private void ApplyKindVisibility()
        {
            string kind = KindCombo.SelectedValue as string ?? "Button";
            bool isChord = kind == "Chord";
            bool isAxis = kind == "Axis";
            ChordLabel.Visibility = isChord ? Visibility.Visible : Visibility.Collapsed;
            ChordSecondRow.Visibility = isChord ? Visibility.Visible : Visibility.Collapsed;
            AxisThresholdLabel.Visibility = isAxis ? Visibility.Visible : Visibility.Collapsed;
            AxisThresholdRow.Visibility = isAxis ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────
        //  Record / Clear handlers for the input pickers
        //
        //  Record routes through RecorderService.StartRecordingFreeform —
        //  the first button / POV / axis on any device assigned to this
        //  slot wins, and we update the ComboBox SelectedItem to the
        //  matching InputChoice. Clear empties the selection. Buttons fall
        //  back to no-op when no recorder was supplied (theoretical guard).
        // ─────────────────────────────────────────────

        private void InputRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingPrimary) { _recorder?.CancelRecording(); SetPrimaryRecording(false); return; }
            if (_recorder == null || _padIndex < 0) return;
            SetPrimaryRecording(true);
            _recorder.StartRecordingFreeform(_padIndex, (guid, descriptor) =>
            {
                SetPrimaryRecording(false);
                AssignToCombo(InputCombo, guid, descriptor);
            });
        }

        private void InputClear_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingPrimary) _recorder?.CancelRecording();
            SetPrimaryRecording(false);
            InputCombo.SelectedItem = null;
        }

        private void ChordSecondRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingChord) { _recorder?.CancelRecording(); SetChordRecording(false); return; }
            if (_recorder == null || _padIndex < 0) return;
            SetChordRecording(true);
            _recorder.StartRecordingFreeform(_padIndex, (guid, descriptor) =>
            {
                SetChordRecording(false);
                AssignToCombo(ChordSecondCombo, guid, descriptor);
            });
        }

        private void ChordSecondClear_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingChord) _recorder?.CancelRecording();
            SetChordRecording(false);
            ChordSecondCombo.SelectedItem = null;
        }

        private void SetPrimaryRecording(bool on)
        {
            _recordingPrimary = on;
            // Swap the icon: Stop (E71A) while recording, Record (E7C8) idle.
            InputRecordIcon.Text = on ? "" : "";
        }

        private void SetChordRecording(bool on)
        {
            _recordingChord = on;
            ChordSecondRecordIcon.Text = on ? "" : "";
        }

        /// <summary>Resolves the (deviceGuid, descriptor) tuple returned by
        /// the freeform recorder to the matching <see cref="InputChoice"/>
        /// in the supplied ComboBox's ItemsSource. Falls back to a
        /// synthetic InputChoice when no match is found so the user's
        /// recorded value still saves through, even if the dropdown
        /// doesn't show a label for it.</summary>
        private static void AssignToCombo(ComboBox combo, string guid, string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) { combo.SelectedItem = null; return; }
            if (combo.ItemsSource is not System.Collections.IEnumerable items) return;
            foreach (var item in items)
            {
                if (item is InputChoice c
                    && string.Equals(c.Descriptor ?? "", descriptor, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.DeviceGuid ?? "", guid ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = c;
                    return;
                }
            }
            // No direct match — pick by descriptor only as a fallback so the
            // user's input lands somewhere visible.
            foreach (var item in items)
            {
                if (item is InputChoice c
                    && string.Equals(c.Descriptor ?? "", descriptor, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = c;
                    return;
                }
            }
        }

        private void ApplyModeVisibility()
        {
            string mode = ModeCombo.SelectedValue as string ?? "Hold";
            bool isCycle = mode == "Cycle";
            bool isPassive = mode == "Passive";
            CycleHeaderRow.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;
            CycleLayersList.Visibility = isCycle ? Visibility.Visible : Visibility.Collapsed;

            // Passive (No Button, #119) layers have no activator at all. They are
            // reached only by a Cycle queue or a Custom jump, so hide every input
            // row. Cycle hides the Kind selector (button-driven) and Delay (press-
            // to-step). The Postpone toggle only makes sense with an own input.
            InputLabel.Visibility = isPassive ? Visibility.Collapsed : Visibility.Visible;
            InputRow.Visibility = isPassive ? Visibility.Collapsed : Visibility.Visible;
            KindLabel.Visibility = (isPassive || isCycle) ? Visibility.Collapsed : Visibility.Visible;
            KindCombo.Visibility = (isPassive || isCycle) ? Visibility.Collapsed : Visibility.Visible;
            DelayRow.Visibility = (isPassive || isCycle) ? Visibility.Collapsed : Visibility.Visible;
            // Auto-cancel (#206) is Toggle-only: auto-unlatching was
            // flagged as surprising by the requester, and Hold/Sticky
            // have their own release semantics.
            bool isToggle = mode == "Toggle";
            AutoCancelLabel.Visibility = isToggle ? Visibility.Visible : Visibility.Collapsed;
            AutoCancelRow.Visibility = isToggle ? Visibility.Visible : Visibility.Collapsed;
            PostponeMappingBox.Visibility = isPassive ? Visibility.Collapsed : Visibility.Visible;

            if (isPassive)
            {
                ChordLabel.Visibility = Visibility.Collapsed;
                ChordSecondRow.Visibility = Visibility.Collapsed;
                AxisThresholdLabel.Visibility = Visibility.Collapsed;
                AxisThresholdRow.Visibility = Visibility.Collapsed;
                return;
            }

            if (isCycle)
            {
                if (!string.Equals(KindCombo.SelectedValue as string, "Button", StringComparison.Ordinal))
                    KindCombo.SelectedValue = "Button";   // -> button-only combos
                InputLabel.Text = Strings.Instance.Pad_Shift_CycleNextButton;
                ChordLabel.Text = Strings.Instance.Pad_Shift_CyclePrevButton;
                ChordLabel.Visibility = Visibility.Visible;
                ChordSecondRow.Visibility = Visibility.Visible;
            }
            else
            {
                InputLabel.Text = Strings.Instance.Pad_Shift_ActivatorInput;
                ChordLabel.Text = Strings.Instance.Pad_Shift_ChordSecondInput;
                ApplyKindVisibility();   // chord / axis rows revert to kind-driven
            }
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
            if (_baseMode)
            {
                // Base-appearance mode: only name/color/icon matter. Name may
                // be empty (falls back to the localized Base label downstream).
                string baseColorHex = _colorSet
                    ? $"#{ColorPicker.Red:X2}{ColorPicker.Green:X2}{ColorPicker.Blue:X2}"
                    : "";
                Result = new ShiftActivator
                {
                    LayerMask = "Base",
                    LayerName = (LayerNameBox.Text ?? "").Trim(),
                    Color = baseColorHex,
                    Icon = _selectedIcon ?? "",
                };
                DialogResult = true;
                Close();
                return;
            }

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
            // Mode decides whether a button is required (#119). Passive (No
            // Button) has none by design. Cycle's Next/Previous are optional, so
            // it can be set up incrementally. Hold/Toggle/Custom/Sticky drive off
            // a button, so require one.
            string mode = ModeCombo.SelectedValue as string ?? "Hold";
            bool isPassive = mode == "Passive";
            bool isCycle = mode == "Cycle";
            InputChoice input = InputCombo.SelectedItem as InputChoice;
            if (!isPassive && !isCycle && input == null)
            {
                ShowHint(Strings.Instance.Pad_Shift_HintInputRequired);
                InputCombo.Focus();
                return;
            }
            string kind = KindCombo.SelectedValue as string ?? "Button";
            InputChoice chordSecond = ChordSecondCombo.SelectedItem as InputChoice;
            // A chord needs both halves, but only when a primary input is set.
            if (input != null && kind == "Chord" && chordSecond == null)
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

            // Latch (#119) has no jump target; it engages its own layer.
            string jumpToLayer = "";

            string cycleLayers = "";
            if (CycleLayersList.SelectedItems != null && CycleLayersList.SelectedItems.Count > 0)
            {
                // Walk Items (canonical display order), not SelectedItems
                // (selection order), so the saved string is deterministic and
                // two activators that check the same set match byte-for-byte
                // (#119, the shared-cursor key).
                var picked = new List<string>();
                foreach (var item in CycleLayersList.Items)
                    if (item is LayerOption opt && CycleLayersList.SelectedItems.Contains(item)
                        && !string.IsNullOrEmpty(opt.LayerMask))
                        picked.Add(opt.LayerMask);
                cycleLayers = string.Join("|", picked);
            }

            string colorHex = _colorSet
                ? $"#{ColorPicker.Red:X2}{ColorPicker.Green:X2}{ColorPicker.Blue:X2}"
                : "";

            // The second input picker doubles as the chord second half (Kind=
            // Chord) or the Cycle Previous button (Mode=Cycle, #119). mode/isCycle
            // were resolved during validation above.
            Result = new ShiftActivator
            {
                LayerName = name,
                LayerMask = mask,
                DeviceGuid = input?.DeviceGuid ?? "",
                Descriptor = input?.Descriptor ?? "",
                Mode = mode,
                Kind = kind,
                InheritUnmapped = InheritUnmappedBox.IsChecked == true,
                ChordSecondDeviceGuid = isCycle ? "" : (chordSecond?.DeviceGuid ?? ""),
                ChordSecondDescriptor = isCycle ? "" : (chordSecond?.Descriptor ?? ""),
                AxisThreshold = AxisThresholdSlider.Value,
                JumpToLayer = jumpToLayer,
                CycleLayers = cycleLayers,
                CyclePrevDeviceGuid = isCycle ? (chordSecond?.DeviceGuid ?? "") : "",
                CyclePrevDescriptor = isCycle ? (chordSecond?.Descriptor ?? "") : "",
                CycleWrap = CycleWrapBox.IsChecked == true,
                CycleIncludeBase = CycleIncludeBaseBox.IsChecked == true,
                DelayMs = (int)Math.Round(DelaySlider.Value),
                AutoCancelMs = mode == "Toggle" ? (int)Math.Round(AutoCancelSlider.Value) : 0,
                PostponeMapping = PostponeMappingBox.IsChecked == true,
                Color = colorHex,
                Icon = _selectedIcon ?? "",
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

        // ─────────────────────────────────────────────
        //  Emoji icon picker
        // ─────────────────────────────────────────────

        private class EmojiCategory
        {
            public string Name { get; set; } = "";
            public string Glyph { get; set; } = "";
            public string[] Emojis { get; set; } = Array.Empty<string>();
        }

        private void InitEmojiPicker(string preset)
        {
            EmojiCategoryBar.ItemsSource = EmojiCatalog;
            EmojiGrid.ItemsSource = EmojiCatalog[0].Emojis;

            if (!string.IsNullOrEmpty(preset))
            {
                _selectedIcon = preset;
                IconPickerGlyph.Text = preset;
            }
            else
            {
                _selectedIcon = "";
                IconPickerGlyph.Text = "⇧";
            }
        }

        private void IconPickerButton_Click(object sender, RoutedEventArgs e)
        {
            IconPickerPopup.IsOpen = !IconPickerPopup.IsOpen;
        }

        private void EmojiCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is EmojiCategory cat)
                EmojiGrid.ItemsSource = cat.Emojis;
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is string s && !string.IsNullOrEmpty(s))
            {
                _selectedIcon = s;
                IconPickerGlyph.Text = s;
            }
            IconPickerPopup.IsOpen = false;
        }

        private void EmojiReset_Click(object sender, RoutedEventArgs e)
        {
            _selectedIcon = "";
            IconPickerGlyph.Text = "⇧";
            IconPickerPopup.IsOpen = false;
        }

        private static readonly EmojiCategory[] EmojiCatalog = new[]
        {
            new EmojiCategory
            {
                Name = "Smileys",
                Glyph = "😀",
                Emojis = new[]
                {
                    "😀","😄","😆","😅","🤣","😂","🙂","🙃","😉","😊",
                    "😇","🥰","😍","🤩","😘","😋","😛","😜","🤪","😝",
                    "🤗","🤭","🤫","🤔","🤐","😐","😑","😶","😏","😒",
                    "🙄","😬","🤥","😌","😔","😴","😷","🤒","🤕","🤢",
                    "🤮","🤧","🥵","🥶","🥴","😵","🤯","🤠","🥳","🥸",
                    "😎","🤓","🧐","🥺","😢","😭","😱","😖","😞","😓",
                    "😩","😫","🥱","😡","😠","🤬","😈","👿","💀","☠️",
                    "👻","👽","🤖","💩","🎃","😸","😺","😻","😼","😽",
                }
            },
            new EmojiCategory
            {
                Name = "Hands",
                Glyph = "👍",
                Emojis = new[]
                {
                    "👋","🤚","✋","🖖","👌","🤏","✌️","🤞","🤟","🤘",
                    "🤙","👈","👉","👆","👇","☝️","👍","👎","✊","👊",
                    "🤛","🤜","👏","🙌","👐","🤲","🤝","🙏","💪","✍️",
                    "🦾","🦿","🦵","🦶","👀","👁️","👅","👄","👂","👃",
                }
            },
            new EmojiCategory
            {
                Name = "Animals",
                Glyph = "🐾",
                Emojis = new[]
                {
                    "🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐨","🐯",
                    "🦁","🐮","🐷","🐸","🐵","🐔","🐧","🐦","🦆","🦅",
                    "🦉","🦄","🐝","🦋","🐢","🦎","🐍","🐙","🦑","🦐",
                    "🦀","🐠","🐬","🐳","🦈","🐎","🐖","🐏","🐐","🦌",
                    "🦃","🦒","🦓","🦔","🦇","🐺","🐗","🐴","🐂","🐃",
                    "🦘","🦙","🦛","🦏","🦬","🦣","🐊","🦥","🦦","🦨",
                }
            },
            new EmojiCategory
            {
                Name = "Food",
                Glyph = "🍔",
                Emojis = new[]
                {
                    "🍎","🍐","🍊","🍋","🍌","🍉","🍇","🍓","🍒","🍑",
                    "🥭","🍍","🥥","🥝","🍅","🥑","🥦","🥬","🥒","🌶️",
                    "🌽","🥕","🥔","🍞","🥐","🧀","🍳","🥞","🥓","🥩",
                    "🍗","🍖","🌭","🍔","🍟","🍕","🌮","🌯","🥗","🍝",
                    "🍣","🍰","🍦","🍩","🍪","🍫","🍿","🍺","🍷","🍸",
                    "🧋","☕","🍵","🥛","🧃","🥤",
                }
            },
            new EmojiCategory
            {
                Name = "Activities",
                Glyph = "🎮",
                Emojis = new[]
                {
                    "⚽","🏀","🏈","⚾","🎾","🏐","🏉","🥏","🎱","🏓",
                    "🏸","🏒","🏑","🥍","🏏","⛳","🪁","🏹","🎣","🥊",
                    "🎯","🎮","🕹️","🎲","🧩","♟️","🎭","🎨","🎬","🎤",
                    "🎧","🎼","🎹","🥁","🎷","🎺","🎸","🎻","🎰","🎳",
                    "🏆","🏅","🥇","🥈","🥉","🎖️","🏁","🚩",
                }
            },
            new EmojiCategory
            {
                Name = "Travel",
                Glyph = "🚗",
                Emojis = new[]
                {
                    "🚗","🚕","🚙","🚌","🚎","🏎️","🚓","🚑","🚒","🚐",
                    "🚚","🚛","🚜","🛴","🚲","🛵","🏍️","🛺","✈️","🛩️",
                    "💺","🚀","🛸","🚁","🛶","⛵","🚤","⛴️","🚢","🚂",
                    "🚆","🚇","🚊","⛺","🏠","🏢","🏥","🏫","🏪","🌆",
                    "🌃","🌅","🌄","🗻","🗽","🗼","🏝️","🏞️","🏜️","🏟️",
                }
            },
            new EmojiCategory
            {
                Name = "Objects",
                Glyph = "💡",
                Emojis = new[]
                {
                    "⌚","📱","💻","⌨️","🖥️","🖨️","🖱️","💾","💿","📀",
                    "📼","📷","📹","🎥","📞","📺","📻","🔋","🔌","💡",
                    "🔦","🕯️","🧯","💰","💳","💎","⚖️","🔧","🔨","⚒️",
                    "🛠️","⛏️","🔩","⚙️","🔫","💣","⚔️","🗡️","🛡️","🔐",
                    "🔑","🗝️","🚪","🛏️","🛋️","🚽","🚿","🛁","📚","📖",
                    "📝","✏️","🖊️","🖌️","🎁","🎈","🎉","🧸","🪄","🔮",
                }
            },
            new EmojiCategory
            {
                Name = "Symbols",
                Glyph = "❤️",
                Emojis = new[]
                {
                    "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔",
                    "❣️","💕","💞","💓","💗","💖","💘","⭐","🌟","✨",
                    "⚡","☄️","💥","🔥","🌈","☀️","🌤️","⛅","☁️","⛈️",
                    "❄️","☃️","⛄","💧","💦","☂️","⌛","⏰","🌍","🌎",
                    "🌏","🌑","🌒","🌓","🌔","🌕","🌖","🌗","🌘","🌙",
                    "☘️","🍀","🌹","🌷","🌻","🌸","🌼","💐","🌺","🌵",
                    "🌴","🌳","⇧","⇩","⇦","⇨","✅","❌","❓","❗",
                }
            },
        };
    }
}
