using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents a single mapping row linking a physical input source
    /// (e.g., "Button 0", "Axis 1") to an XInput output target
    /// (e.g., "ButtonA", "LeftThumbAxisX").
    /// 
    /// Displayed in the mapping grid on the Pad page. Supports input
    /// recording to auto-detect the source.
    /// </summary>
    public class MappingItem : ObservableObject
    {
        /// <summary>
        /// Creates a mapping item.
        /// </summary>
        /// <param name="targetLabel">Human-readable label for the XInput target (e.g., "A", "Left Stick X").</param>
        /// <param name="targetSettingName">PadSetting property name (e.g., "ButtonA", "LeftThumbAxisX").</param>
        /// <param name="category">Category for grouping in tabs.</param>
        /// <param name="negSettingName">PadSetting property for negative direction (null for non-axis targets).</param>
        public MappingItem(string targetLabel, string targetSettingName, MappingCategory category,
            string negSettingName = null)
        {
            TargetLabel = targetLabel ?? string.Empty;
            TargetSettingName = targetSettingName ?? string.Empty;
            Category = category;
            Strings.CultureChanged += OnCultureChanged;
            NegSettingName = negSettingName;
        }

        // ─────────────────────────────────────────────
        //  Target (XInput output)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Human-readable label for the XInput output this row maps to.
        /// Example: "A", "Left Stick X", "Right Trigger".
        /// </summary>
        public string TargetLabel { get; }

        /// <summary>
        /// The PadSetting property name this mapping corresponds to.
        /// Used to read/write the mapping descriptor string from PadSetting.
        /// Example: "ButtonA", "LeftThumbAxisX", "RightTrigger".
        /// </summary>
        public string TargetSettingName { get; }

        /// <summary>
        /// Category for grouping mapping rows in tabs.
        /// </summary>
        public MappingCategory Category { get; }

        /// <summary>
        /// PadSetting property name for the negative direction (e.g., "LeftThumbAxisXNeg").
        /// Null for non-axis targets that don't support bidirectional button mapping.
        /// </summary>
        public string NegSettingName { get; }

        /// <summary>Whether this mapping supports a negative direction (stick axes only).</summary>
        public bool HasNegDirection => NegSettingName != null;

        // ─────────────────────────────────────────────
        //  Source (physical input)
        // ─────────────────────────────────────────────

        private string _sourceDescriptor = string.Empty;

        /// <summary>
        /// The mapping descriptor string identifying the physical input source.
        /// Format: "{MapType} {Index}" or "IH{MapType} {Index}" or "POV {Index} {Direction}"
        /// Examples: "Button 0", "Axis 1", "IHAxis 2", "POV 0 Up", "Slider 0"
        /// Empty string means unmapped.
        /// </summary>
        public string SourceDescriptor
        {
            get => _sourceDescriptor;
            set
            {
                if (SetProperty(ref _sourceDescriptor, value ?? string.Empty))
                {
                    _resolvedSourceText = null; // Clear until re-resolved
                    OnPropertyChanged(nameof(SourceDisplayText));
                    OnPropertyChanged(nameof(IsMapped));
                    OnPropertyChanged(nameof(IsDeadZoneApplicable));
                }
            }
        }

        private string _resolvedSourceText;

        /// <summary>
        /// Cached base object name without any prefix (e.g., "X Axis", "Button A").
        /// Used by RebuildDescriptor to reconstruct resolved text after prefix changes.
        /// </summary>
        private string _resolvedBaseName;

        // ─────────────────────────────────────────────
        //  Negative direction source (for bidirectional stick axes)
        // ─────────────────────────────────────────────

        private string _negSourceDescriptor = string.Empty;

        /// <summary>
        /// Negative-direction descriptor for stick axes (e.g., the "left" button for an X axis).
        /// Only used when HasNegDirection is true.
        /// </summary>
        public string NegSourceDescriptor
        {
            get => _negSourceDescriptor;
            set
            {
                if (SetProperty(ref _negSourceDescriptor, value ?? string.Empty))
                {
                    _resolvedNegText = null;
                    OnPropertyChanged(nameof(SourceDisplayText));
                    OnPropertyChanged(nameof(IsMapped));
                }
            }
        }

        private string _resolvedNegText;

        /// <summary>
        /// Sets the human-readable resolved text for the negative direction.
        /// </summary>
        public void SetResolvedNegText(string text)
        {
            _resolvedNegText = text;
            OnPropertyChanged(nameof(SourceDisplayText));
        }

        /// <summary>
        /// Human-readable display text for the source.
        /// For bidirectional axes with both pos and neg set, shows "neg / pos" format.
        /// </summary>
        public string SourceDisplayText
        {
            get
            {
                bool hasPos = !string.IsNullOrEmpty(_sourceDescriptor);
                bool hasNeg = !string.IsNullOrEmpty(_negSourceDescriptor);

                if (!hasPos && !hasNeg) return Strings.Instance.Mapping_NotMapped;

                string posText = hasPos ? (_resolvedSourceText ?? _sourceDescriptor) : "";

                if (!HasNegDirection || (!hasNeg && hasPos))
                    return posText;

                string negText = hasNeg ? (_resolvedNegText ?? _negSourceDescriptor) : "";

                if (hasPos && hasNeg)
                    return $"{negText} / {posText}";
                if (hasNeg)
                    return $"{negText} / ...";
                return $"... / {posText}";
            }
        }

        /// <summary>
        /// Sets the human-readable resolved text for display (e.g., "A" instead of "Button 65").
        /// Called by InputService when loading mappings from a known device.
        /// </summary>
        public void SetResolvedSourceText(string text)
        {
            _resolvedSourceText = text;
            // Cache the base name (without prefix) for RebuildDescriptor.
            if (text != null)
            {
                string invHalfPrefix = Strings.Instance.Mapping_InvHalf + " ";
                string invPrefix = Strings.Instance.Mapping_Inv + " ";
                string halfPrefix = Strings.Instance.Mapping_Half + " ";
                if (text.StartsWith(invHalfPrefix, StringComparison.Ordinal))
                    _resolvedBaseName = text.Substring(invHalfPrefix.Length);
                else if (text.StartsWith(invPrefix, StringComparison.Ordinal))
                    _resolvedBaseName = text.Substring(invPrefix.Length);
                else if (text.StartsWith(halfPrefix, StringComparison.Ordinal))
                    _resolvedBaseName = text.Substring(halfPrefix.Length);
                else
                    _resolvedBaseName = text;
            }
            OnPropertyChanged(nameof(SourceDisplayText));
        }

        /// <summary>
        /// Whether this mapping row has a source assigned.
        /// </summary>
        public bool IsMapped => !string.IsNullOrEmpty(_sourceDescriptor) || !string.IsNullOrEmpty(_negSourceDescriptor);

        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(SourceDisplayText));
            OnPropertyChanged(nameof(RecordButtonText));
        }

        // ─────────────────────────────────────────────
        //  Available input choices (dropdown)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Available input choices for the source dropdown.
        /// Populated by InputService when the selected device changes.
        /// </summary>
        public ObservableCollection<InputChoice> AvailableInputs { get; } = new();

        private InputChoice _selectedInput;
        private bool _suppressSelectionSync;

        /// <summary>
        /// The currently selected input from the dropdown.
        /// Setting this updates the SourceDescriptor accordingly.
        /// </summary>
        public InputChoice SelectedInput
        {
            get => _selectedInput;
            set
            {
                if (_suppressSelectionSync) return;
                if (SetProperty(ref _selectedInput, value) && value != null)
                {
                    if (string.IsNullOrEmpty(value.Descriptor))
                    {
                        ClearCommand.Execute(null);
                    }
                    else
                    {
                        LoadDescriptor(value.Descriptor);
                        InputSelectedFromDropdown?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// Synchronizes SelectedInput to match the current SourceDescriptor
        /// without triggering a descriptor update.
        /// </summary>
        public void SyncSelectedInputFromDescriptor()
        {
            _suppressSelectionSync = true;
            try
            {
                if (string.IsNullOrEmpty(_sourceDescriptor))
                {
                    _selectedInput = null;
                    OnPropertyChanged(nameof(SelectedInput));
                    return;
                }

                // Strip I/H prefixes for matching.
                string clean = _sourceDescriptor;
                if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(2);
                else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                    clean = clean.Substring(1);
                else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                    clean = clean.Substring(1);

                InputChoice match = null;
                foreach (var choice in AvailableInputs)
                {
                    if (string.Equals(choice.Descriptor, clean, StringComparison.OrdinalIgnoreCase))
                    {
                        match = choice;
                        break;
                    }
                }
                _selectedInput = match;
                OnPropertyChanged(nameof(SelectedInput));
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }


        /// <summary>Raised when the user selects an input from the dropdown (for display text resolution).</summary>
        public event EventHandler InputSelectedFromDropdown;

        // ─────────────────────────────────────────────
        //  Recording state
        // ─────────────────────────────────────────────

        private bool _isRecording;

        /// <summary>
        /// Whether this mapping row is currently in recording mode,
        /// waiting for the user to press a button or move an axis.
        /// </summary>
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordButtonText));
                }
            }
        }

        /// <summary>
        /// Text for the record button: "Record" or "Recording..." (with a visual cue).
        /// </summary>
        public string RecordButtonText => IsRecording ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;

        // ─────────────────────────────────────────────
        //  Live value display
        // ─────────────────────────────────────────────

        private string _currentValueText = string.Empty;

        /// <summary>
        /// Shows the current raw value of the source input in real-time.
        /// Updated at 30Hz when the Pad page is visible.
        /// </summary>
        public string CurrentValueText
        {
            get => _currentValueText;
            set => SetProperty(ref _currentValueText, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Options
        // ─────────────────────────────────────────────

        private bool _isInverted;

        /// <summary>
        /// Sets the source descriptor and syncs the IsInverted/IsHalfAxis flags
        /// from the "I" and "H" prefixes in the descriptor string.
        /// </summary>
        public void LoadDescriptor(string descriptor)
        {
            string d = descriptor ?? string.Empty;
            bool inv = false;
            bool half = false;

            if (d.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            {
                inv = true;
                half = true;
            }
            else if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
            {
                inv = true;
            }
            else if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
            {
                half = true;
            }

            // Set flags first (without triggering RebuildDescriptor).
            _isInverted = inv;
            OnPropertyChanged(nameof(IsInverted));
            _isHalfAxis = half;
            OnPropertyChanged(nameof(IsHalfAxis));

            // Then set the descriptor string.
            SourceDescriptor = d;
        }

        /// <summary>
        /// Loads a negative-direction descriptor, parsing any I/H prefixes.
        /// </summary>
        public void LoadNegDescriptor(string descriptor)
        {
            NegSourceDescriptor = descriptor ?? string.Empty;
        }

        /// <summary>Whether the axis value should be inverted.</summary>
        public bool IsInverted
        {
            get => _isInverted;
            set
            {
                if (SetProperty(ref _isInverted, value))
                    RebuildDescriptor();
            }
        }

        private bool _isHalfAxis;

        /// <summary>Whether to use only the upper half of the axis range.</summary>
        public bool IsHalfAxis
        {
            get => _isHalfAxis;
            set
            {
                if (SetProperty(ref _isHalfAxis, value))
                    RebuildDescriptor();
            }
        }

        private int _mappingDeadZone = 50;

        /// <summary>
        /// Per-mapping dead zone percentage (0–100). When non-zero, overrides the
        /// global AxisToButtonThreshold for this specific axis-to-button mapping.
        /// Only meaningful when the source is an axis or slider.
        /// </summary>
        public int MappingDeadZone
        {
            get => _mappingDeadZone;
            set => SetProperty(ref _mappingDeadZone, Math.Clamp(value, 0, 100));
        }

        /// <summary>
        /// True when the dead zone column is applicable for this row:
        /// the source is an axis/slider AND the target is a discrete output
        /// (button, d-pad, POV, key, note) — NOT an axis-to-axis mapping.
        /// </summary>
        public bool IsDeadZoneApplicable
        {
            get
            {
                // Check source is axis/slider.
                var desc = _sourceDescriptor;
                if (string.IsNullOrEmpty(desc)) return false;
                int start = 0;
                if (start < desc.Length && desc[start] == 'I') start++;
                if (start < desc.Length && desc[start] == 'H') start++;
                var body = desc.AsSpan(start);
                if (!body.StartsWith("Axis") && !body.StartsWith("Slider"))
                    return false;

                // Check target is a discrete (button-type) output, not an axis.
                var t = TargetSettingName;
                if (t.Contains("ThumbAxis") || t.StartsWith("VJoyAxis")
                    || t.StartsWith("KbmMouse") || t.StartsWith("KbmScroll")
                    || t.StartsWith("MidiCC"))
                    return false;
                if (t == "LeftTrigger" || t == "RightTrigger")
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Rebuilds the source descriptor when inversion or half-axis options change.
        /// Adds/removes the "I" and "H" prefixes.
        /// </summary>
        private void RebuildDescriptor()
        {
            if (string.IsNullOrEmpty(_sourceDescriptor))
                return;

            // Strip existing prefixes.
            string clean = _sourceDescriptor;
            if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                clean = clean.Substring(1);
            else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                clean = clean.Substring(1);

            // Rebuild with new prefixes.
            string prefix = "";
            if (_isInverted) prefix += "I";
            if (_isHalfAxis) prefix += "H";

            SourceDescriptor = prefix + clean;

            // Rebuild resolved display text from cached base name so the UI
            // doesn't fall back to the raw descriptor (e.g., "IAxis 0").
            if (_resolvedBaseName != null)
            {
                string prefixLabel = prefix.ToUpperInvariant() switch
                {
                    "I" => Strings.Instance.Mapping_Inv,
                    "H" => Strings.Instance.Mapping_Half,
                    "IH" => Strings.Instance.Mapping_InvHalf,
                    _ => null
                };
                _resolvedSourceText = prefixLabel != null
                    ? $"{prefixLabel} {_resolvedBaseName}"
                    : _resolvedBaseName;
                OnPropertyChanged(nameof(SourceDisplayText));
            }
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _toggleRecordCommand;

        /// <summary>Command to toggle recording mode for this mapping row.</summary>
        public RelayCommand ToggleRecordCommand =>
            _toggleRecordCommand ??= new RelayCommand(() =>
            {
                if (IsRecording)
                    StopRecordingRequested?.Invoke(this, EventArgs.Empty);
                else
                    StartRecordingRequested?.Invoke(this, EventArgs.Empty);
            });

        private RelayCommand _clearCommand;

        /// <summary>Command to clear the source assignment.</summary>
        public RelayCommand ClearCommand =>
            _clearCommand ??= new RelayCommand(() =>
            {
                SourceDescriptor = string.Empty;
                NegSourceDescriptor = string.Empty;
                IsInverted = false;
                IsHalfAxis = false;
                MappingDeadZone = 50;
                SyncSelectedInputFromDescriptor();
            });

        private RelayCommand _resetDeadZoneCommand;

        /// <summary>Command to reset the per-mapping dead zone to 0 (use global default).</summary>
        public RelayCommand ResetDeadZoneCommand =>
            _resetDeadZoneCommand ??= new RelayCommand(() => MappingDeadZone = 50);

        /// <summary>Raised when the user clicks Record on this row.</summary>
        public event EventHandler StartRecordingRequested;

        /// <summary>Raised when recording should stop on this row.</summary>
        public event EventHandler StopRecordingRequested;

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        public override string ToString()
        {
            return $"{TargetLabel} ← {SourceDisplayText}";
        }
    }

    /// <summary>
    /// Categories for grouping mapping items in tabs.
    /// </summary>
    public enum MappingCategory
    {
        Buttons,
        DPad,
        Triggers,
        LeftStick,
        RightStick
    }

    /// <summary>
    /// Represents an available input choice in the source dropdown.
    /// </summary>
    public class InputChoice
    {
        /// <summary>Mapping descriptor (e.g., "Button 0", "Axis 1", "POV 0 Up").</summary>
        public string Descriptor { get; set; }

        /// <summary>Human-readable display name (e.g., "A", "Left Stick X", "Button 0").</summary>
        public string DisplayName { get; set; }

        public override string ToString() => DisplayName;
    }
}
