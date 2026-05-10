using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
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
            string negSettingName = null, bool includeInMapAll = true)
        {
            TargetLabel = targetLabel ?? string.Empty;
            TargetSettingName = targetSettingName ?? string.Empty;
            Category = category;
            Strings.CultureChanged += OnCultureChanged;
            NegSettingName = negSettingName;
            IncludeInMapAll = includeInMapAll;

            // Re-fire computed-property notifications when ExtraSources
            // mutates so the +Add / Remove buttons + hints stay in sync.
            // Also keep per-source AvailableInputs lists in sync as
            // sources are added / removed and as their DeviceGuid
            // changes — this is what enables the cascading
            // device/input picker.
            ExtraSources.CollectionChanged += OnExtraSourcesCollectionChanged;
        }

        // ─────────────────────────────────────────────
        //  Phase 2C — cascading device/input picker
        //
        //  Slot-level state pushed in by InputService when the row is
        //  populated:
        //    - SlotMappedDevices: list of all devices assigned to the
        //      slot, used by the per-source Device ComboBox.
        //    - GetInputChoicesForDevice: lookup that returns the
        //      InputChoice list for a given device GUID.
        //
        //  When a source's DeviceGuid changes, we refresh that source's
        //  per-source AvailableInputs from the lookup. Empty GUID =
        //  "use the slot's primary device" and falls back to the
        //  parent MappingItem's AvailableInputs list.
        // ─────────────────────────────────────────────

        private object _slotMappedDevices;
        /// <summary>Reference (not owned) to the parent
        /// PadViewModel.MappedDevices collection. Bound by the per-source
        /// Device ComboBox via a RelativeSource walk to the DataGridRow's
        /// DataContext (this MappingItem). Typed as object so we don't
        /// take a hard dependency on PadViewModel.MappedDeviceInfo from
        /// MappingItem; the XAML only needs <c>Name</c> and
        /// <c>InstanceGuid</c> via reflection-style binding.</summary>
        public object SlotMappedDevices
        {
            get => _slotMappedDevices;
            set => SetProperty(ref _slotMappedDevices, value);
        }

        /// <summary>Set by InputService at row-population time. Returns
        /// the list of <see cref="InputChoice"/> for a given device
        /// GUID. Null means "fall back to the slot's primary device's
        /// inputs" (this row's <see cref="AvailableInputs"/>).</summary>
        public Func<string, IReadOnlyList<InputChoice>> GetInputChoicesForDevice { get; set; }

        private void OnExtraSourcesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsMultiSource));
            OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));

            if (e.NewItems != null)
            {
                foreach (var added in e.NewItems)
                {
                    if (added is MappingSourceItem msi)
                    {
                        msi.PropertyChanged += OnExtraSourcePropertyChanged;
                        RefreshExtraSourceInputs(msi);
                    }
                }
            }
            if (e.OldItems != null)
            {
                foreach (var removed in e.OldItems)
                {
                    if (removed is MappingSourceItem msi)
                        msi.PropertyChanged -= OnExtraSourcePropertyChanged;
                }
            }
        }

        private void OnExtraSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(MappingSourceItem.DeviceGuid), StringComparison.Ordinal)
                && sender is MappingSourceItem msi)
            {
                RefreshExtraSourceInputs(msi);
            }
        }

        /// <summary>Re-syncs a single extra source's
        /// <see cref="MappingSourceItem.SelectedInput"/> to match its
        /// stored DeviceGuid+Descriptor pair against this row's
        /// cross-device <see cref="AvailableInputs"/> list. Also pushes
        /// the parent's discrete-target flag down to the source so the
        /// per-source deadzone visibility tracks the row's target.</summary>
        public void RefreshExtraSourceInputs(MappingSourceItem msi)
        {
            if (msi == null) return;
            msi.ParentTargetIsDiscrete = IsTargetDiscrete;
            msi.SyncSelectedInputFromState(AvailableInputs);
        }

        /// <summary>True when the row's target is a discrete
        /// (button-class) output. Mirrors the second half of
        /// <see cref="IsDeadZoneApplicable"/>: an axis-source row
        /// targeting a button gets a per-mapping deadzone slider; an
        /// axis-source row targeting a stick axis does not. Pushed to
        /// each ExtraSource so the per-source deadzone visibility on
        /// extras matches.</summary>
        public bool IsTargetDiscrete
        {
            get
            {
                var t = TargetSettingName ?? "";
                if (t.Contains("ThumbAxis", StringComparison.Ordinal)
                    || t.StartsWith("ExtendedAxis", StringComparison.Ordinal)
                    || t.StartsWith("KbmMouse", StringComparison.Ordinal)
                    || t.StartsWith("KbmScroll", StringComparison.Ordinal)
                    || t.StartsWith("MidiCC", StringComparison.Ordinal))
                    return false;
                if (t == "LeftTrigger" || t == "RightTrigger") return false;
                return true;
            }
        }

        /// <summary>Bulk-refresh every extra source's selected-input
        /// state. Called by InputService after the slot's
        /// AvailableInputs list is rebuilt.</summary>
        public void RefreshAllExtraSourceInputs()
        {
            foreach (var msi in ExtraSources)
                RefreshExtraSourceInputs(msi);
        }

        /// <summary>
        /// Whether this row participates in the "Map All" walk-through.
        /// Optional rows (Xbox Series Share, etc.) are visible and
        /// individually mappable but skipped during the bulk sequence.
        /// </summary>
        public bool IncludeInMapAll { get; }

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
                    OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
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
                    OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
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
        /// Flat cross-device input choices for the source dropdown.
        /// Populated by InputService once per VC slot (not per Device
        /// dropdown change), spanning every device assigned to the slot.
        /// Each entry carries its own <see cref="InputChoice.DeviceGuid"/>
        /// + <see cref="InputChoice.DeviceLabel"/> so the picker can
        /// group by device via WPF's <c>GroupStyle</c>.
        /// </summary>
        public ObservableCollection<InputChoice> AvailableInputs { get; } = new();

        private ICollectionView _availableInputsView;
        /// <summary>The grouped view of <see cref="AvailableInputs"/> the
        /// XAML ComboBox binds to. <c>GroupDescription</c> lives on
        /// <see cref="InputChoice.DeviceLabel"/> so the picker renders a
        /// single dropdown with device-name headers between each device's
        /// inputs.</summary>
        public ICollectionView AvailableInputsView
        {
            get
            {
                if (_availableInputsView == null)
                {
                    _availableInputsView = CollectionViewSource.GetDefaultView(AvailableInputs);
                    if (_availableInputsView != null
                        && _availableInputsView.GroupDescriptions != null)
                    {
                        _availableInputsView.GroupDescriptions.Clear();
                        _availableInputsView.GroupDescriptions.Add(
                            new PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
                    }
                }
                return _availableInputsView;
            }
        }

        private InputChoice _selectedInput;
        private bool _suppressSelectionSync;

        /// <summary>
        /// The currently selected input from the dropdown.
        /// Setting this updates the SourceDescriptor — and the row's
        /// <see cref="PrimarySourceDeviceGuid"/> — accordingly.
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
                        // Tag the row's primary source with the picked
                        // device BEFORE LoadDescriptor so any downstream
                        // notify-listeners see the new device + descriptor
                        // together.
                        PrimarySourceDeviceGuid = value.DeviceGuid ?? "";
                        if (!string.IsNullOrEmpty(value.DeviceLabel))
                            PrimarySourceDeviceLabel = value.DeviceLabel;
                        LoadDescriptor(value.Descriptor);
                        InputSelectedFromDropdown?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// Synchronizes SelectedInput to match the current SourceDescriptor
        /// + <see cref="PrimarySourceDeviceGuid"/> without triggering a
        /// descriptor update. Match is on (DeviceGuid, Descriptor) so a
        /// "Button 0" on the DualSense and a "Button 0" on a keyboard
        /// (which auto-mapping might have stamped) don't get confused.
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

                string wantGuid = (_primarySourceDeviceGuid ?? "").ToLowerInvariant();
                InputChoice match = null;
                InputChoice descriptorOnlyMatch = null;
                foreach (var choice in AvailableInputs)
                {
                    if (!string.Equals(choice.Descriptor, clean, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (descriptorOnlyMatch == null) descriptorOnlyMatch = choice;
                    if (!string.IsNullOrEmpty(wantGuid)
                        && string.Equals(choice.DeviceGuid ?? "", wantGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = choice;
                        break;
                    }
                }
                _selectedInput = match ?? descriptorOnlyMatch;
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
                    OnPropertyChanged(nameof(RecordButtonIcon));
                }
            }
        }

        /// <summary>
        /// Text for the record button: "Record" or "Recording..." (with a visual cue).
        /// </summary>
        public string RecordButtonText => IsRecording ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;

        public string RecordButtonIcon => IsRecording ? "\uE71A" : "\uE7C8"; // Stop : Record

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
                {
                    RebuildDescriptor();
                    OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
                }
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
        /// Per-mapping deadzone percentage (0–100). When non-zero, overrides the
        /// global AxisToButtonThreshold for this specific axis-to-button mapping.
        /// Only meaningful when the source is an axis or slider.
        /// </summary>
        public int MappingDeadZone
        {
            get => _mappingDeadZone;
            set => SetProperty(ref _mappingDeadZone, Math.Clamp(value, 0, 100));
        }

        /// <summary>
        /// True when the deadzone column is applicable for this row:
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
                if (t.Contains("ThumbAxis") || t.StartsWith("ExtendedAxis")
                    || t.StartsWith("KbmMouse") || t.StartsWith("KbmScroll")
                    || t.StartsWith("MidiCC"))
                    return false;
                if (t == "LeftTrigger" || t == "RightTrigger")
                    return false;

                return true;
            }
        }

        /// <summary>
        /// Whether this mapping row supports recording (button press detection).
        /// Touchpad rows can't be isolated by touch (X and Y fire simultaneously).
        /// </summary>
        public bool IsRecordable => Category != MappingCategory.Touchpad;

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
                PrimarySourceDeviceGuid = "";
                PrimarySourceDeviceLabel = "";
                SyncSelectedInputFromDescriptor();
            });

        private RelayCommand _resetDeadZoneCommand;

        /// <summary>Command to reset the per-mapping deadzone to default (50%).</summary>
        public RelayCommand ResetDeadZoneCommand =>
            _resetDeadZoneCommand ??= new RelayCommand(() => MappingDeadZone = 50);

        /// <summary>Raised when the user clicks Record on this row.</summary>
        public event EventHandler StartRecordingRequested;

        /// <summary>Raised when recording should stop on this row.</summary>
        public event EventHandler StopRecordingRequested;

        // ─────────────────────────────────────────────
        //  Phase 2C — multi-source extras (Issue #61)
        //
        //  ExtraSources holds the rest of the row's sources beyond the
        //  primary, which stays bound to SourceDescriptor for legacy
        //  single-source UI compatibility. CombineMode applies to the row
        //  when ExtraSources.Count > 0; the engine's CombineHelper /
        //  MappingExpression consumes it in Step 3.
        // ─────────────────────────────────────────────

        public ObservableCollection<MappingSourceItem> ExtraSources { get; }
            = new ObservableCollection<MappingSourceItem>();

        // ExtraSources collection-changed wiring is set up in the
        // constructor below so IsMultiSource and ShouldShowEmptyDirectionHint
        // re-fire when the list mutates.

        /// <summary>True when this row's Target is a bipolar stick axis
        /// (LeftThumbAxisX/Y, RightThumbAxisX/Y). Drives the per-source
        /// direction-badge visibility — badges only make sense for the
        /// "+/−" interpretation of button sources on a bipolar axis.</summary>
        public bool IsBipolarAxisTarget =>
            string.Equals(TargetSettingName, "LeftThumbAxisX", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "LeftThumbAxisY", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "RightThumbAxisX", StringComparison.Ordinal)
         || string.Equals(TargetSettingName, "RightThumbAxisY", StringComparison.Ordinal);

        /// <summary>True when this is a bipolar axis row with exactly one
        /// button-class primary source set to Invert=false (i.e. only
        /// the positive direction is mapped). Surfaces a small inline
        /// hint nudging the user to map the opposite direction. Once
        /// they add a second source — or change Invert — the hint
        /// disappears.</summary>
        public bool ShouldShowEmptyDirectionHint
        {
            get
            {
                if (!IsBipolarAxisTarget) return false;
                if (ExtraSources != null && ExtraSources.Count > 0) return false;
                if (string.IsNullOrEmpty(_sourceDescriptor)) return false;
                if (!string.IsNullOrEmpty(_negSourceDescriptor)) return false;
                if (_isInverted) return false; // user explicitly inverted; assume intentional

                // Primary descriptor must be button-class (button / POV /
                // touchpad). An axis source is bidirectional on its own.
                var d = _sourceDescriptor.Trim();
                if (d.StartsWith("Button ", StringComparison.Ordinal)) return true;
                if (d.StartsWith("POV ", StringComparison.Ordinal)) return true;
                if (d.StartsWith("Touchpad ", StringComparison.Ordinal)) return true;
                return false;
            }
        }

        private string _primarySourceDeviceGuid = "";
        /// <summary>Phase 2C — DeviceGuid of the primary source
        /// (Sources[0]) on the per-VC MappingSet row. Surfaces in the
        /// Source column so users can tell which physical device the
        /// primary source is bound to without checking the Device
        /// dropdown. Empty string means "first available device on this
        /// VC."</summary>
        public string PrimarySourceDeviceGuid
        {
            get => _primarySourceDeviceGuid;
            set
            {
                if (SetProperty(ref _primarySourceDeviceGuid, value ?? ""))
                    OnPropertyChanged(nameof(PrimarySourceDeviceLabel));
            }
        }

        private string _primarySourceDeviceLabel = "";
        /// <summary>Human-friendly device name for the primary source.
        /// Resolved by the InputService load path against the user's
        /// known UserDevices.</summary>
        public string PrimarySourceDeviceLabel
        {
            get => _primarySourceDeviceLabel;
            set => SetProperty(ref _primarySourceDeviceLabel, value ?? "");
        }

        private string _combineMode = "";
        /// <summary>Per-row combine mode. Empty = the per-target-type
        /// default (MaxAbs for axes, OR for buttons). Other named modes:
        /// MaxAbs, Sum, Average, OR, AND, XOR, Custom.</summary>
        public string CombineMode
        {
            get => _combineMode;
            set
            {
                if (SetProperty(ref _combineMode, value ?? ""))
                {
                    OnPropertyChanged(nameof(IsCustomCombine));
                }
            }
        }

        private string _combineExpression = "";
        /// <summary>Custom combine expression, only meaningful when
        /// <see cref="CombineMode"/> == "Custom".</summary>
        public string CombineExpression
        {
            get => _combineExpression;
            set
            {
                if (SetProperty(ref _combineExpression, value ?? ""))
                {
                    OnPropertyChanged(nameof(CombineExpressionStatus));
                    OnPropertyChanged(nameof(IsCombineExpressionValid));
                    OnPropertyChanged(nameof(IsCombineExpressionInvalid));
                }
            }
        }

        public bool IsMultiSource => ExtraSources.Count > 0;
        public bool IsCustomCombine => string.Equals(_combineMode, "Custom", StringComparison.Ordinal);

        /// <summary>Live parse status of <see cref="CombineExpression"/>.
        /// "✓ valid" or a parse-error message; surfaced inline below
        /// the Custom expression TextBox so users get immediate
        /// feedback. Empty/whitespace expression compiles as 0 (always
        /// valid).</summary>
        public string CombineExpressionStatus
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_combineExpression))
                    return "✓ empty (evaluates to 0)";
                var c = Engine.Common.Mapping.MappingExpression.Compile(_combineExpression);
                if (c.IsValid)
                {
                    var refs = c.ReferencedSingleLetterVars ?? "";
                    var refsBit = string.IsNullOrEmpty(refs) ? "" : " · refs: " + string.Join(",", refs.ToCharArray());
                    if (c.MaxIndexedRef >= 0)
                        refsBit += (refsBit.Length == 0 ? " · refs: " : ", ") + "s[" + c.MaxIndexedRef + "]";
                    return "✓ valid" + refsBit;
                }
                return "✗ " + (c.Error ?? "parse error");
            }
        }

        public bool IsCombineExpressionValid
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_combineExpression)) return true;
                return Engine.Common.Mapping.MappingExpression.Compile(_combineExpression).IsValid;
            }
        }

        public bool IsCombineExpressionInvalid => !IsCombineExpressionValid;

        private RelayCommand _addExtraSourceCommand;
        /// <summary>Appends a blank <see cref="MappingSourceItem"/> to
        /// <see cref="ExtraSources"/>. The user fills it in via the
        /// per-source picker.</summary>
        public RelayCommand AddExtraSourceCommand =>
            _addExtraSourceCommand ??= new RelayCommand(() =>
            {
                EnsureCombineModeDefault();
                ExtraSources.Add(new MappingSourceItem());
                OnPropertyChanged(nameof(IsMultiSource));
            });

        /// <summary>If <see cref="CombineMode"/> is still the empty
        /// "implicit-default" sentinel when the user is transitioning a
        /// row to multi-source, auto-select the per-target-class
        /// default — MaxAbs for axes / triggers / sliders, OR for
        /// buttons and POV — so the combine pill never reads as blank
        /// for a multi-source row. The user can override afterwards.
        /// No-op when CombineMode is already set explicitly.</summary>
        private void EnsureCombineModeDefault()
        {
            if (!string.IsNullOrEmpty(_combineMode)) return;

            string t = TargetSettingName ?? "";
            bool isAxis =
                   t.Contains("ThumbAxis", StringComparison.Ordinal)
                || t == "LeftTrigger" || t == "RightTrigger"
                || t.StartsWith("ExtendedAxis", StringComparison.Ordinal)
                || t.StartsWith("KbmMouse", StringComparison.Ordinal)
                || t.StartsWith("KbmScroll", StringComparison.Ordinal)
                || t.StartsWith("MidiCC", StringComparison.Ordinal)
                || t.StartsWith("Touchpad", StringComparison.Ordinal);
            CombineMode = isAxis ? "MaxAbs" : "OR";
        }

        private RelayCommand<MappingSourceItem> _removeExtraSourceCommand;
        public RelayCommand<MappingSourceItem> RemoveExtraSourceCommand =>
            _removeExtraSourceCommand ??= new RelayCommand<MappingSourceItem>(item =>
            {
                if (item == null) return;
                ExtraSources.Remove(item);
                OnPropertyChanged(nameof(IsMultiSource));
            });

        private RelayCommand _addOppositeDirectionCommand;
        /// <summary>Companion to the empty-direction hint. Adds an
        /// extra source that mirrors the primary descriptor / device
        /// but with Invert=true so a single button-mapped bipolar axis
        /// row gets its negative direction with one click. Only
        /// meaningful when <see cref="ShouldShowEmptyDirectionHint"/>
        /// is true.</summary>
        public RelayCommand AddOppositeDirectionCommand =>
            _addOppositeDirectionCommand ??= new RelayCommand(() =>
            {
                // Strip any I/H prefix from the primary so the mirror
                // source descriptor matches the un-prefixed form the
                // ExtraSources picker expects.
                string clean = _sourceDescriptor ?? "";
                if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                    clean = clean.Substring(2);
                else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                    clean = clean.Substring(1);
                else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                    clean = clean.Substring(1);

                ExtraSources.Add(new MappingSourceItem
                {
                    Kind = "Direct",
                    DeviceGuid = _primarySourceDeviceGuid ?? "",
                    Descriptor = clean,
                    Invert = true,
                });
                OnPropertyChanged(nameof(IsMultiSource));
                OnPropertyChanged(nameof(ShouldShowEmptyDirectionHint));
            });

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
        RightStick,
        Touchpad
    }

    /// <summary>
    /// Represents an available input choice in the source dropdown.
    /// Each choice is tagged with the device it belongs to so a single
    /// flat-with-grouping list can span every device assigned to a slot
    /// — the picker uses WPF's <c>GroupStyle</c> + a
    /// <c>CollectionViewSource</c> grouping descriptor on
    /// <see cref="DeviceLabel"/> to render device-name headers between
    /// each device's input rows.
    /// </summary>
    public class InputChoice
    {
        /// <summary>Mapping descriptor (e.g., "Button 0", "Axis 1", "POV 0 Up").</summary>
        public string Descriptor { get; set; }

        /// <summary>Human-readable display name (e.g., "A", "Left Stick X", "Button 0").</summary>
        public string DisplayName { get; set; }

        /// <summary>Lowercase GUID of the device this choice belongs to.
        /// Empty string means "(any device)" / unbound.</summary>
        public string DeviceGuid { get; set; } = "";

        /// <summary>Friendly name of the device this choice belongs to.
        /// Used as the GroupStyle header in the picker.</summary>
        public string DeviceLabel { get; set; } = "";

        public override string ToString() => DisplayName;
    }
}
